using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SQCD.Agv.Contracts;

namespace SQCD.Agv.RuleMock;

public sealed class RuleMockServer : IAsyncDisposable
{
    // Mock 配置、扫码规则、故障注入配置
    private readonly RuleMockSettings _settings;
    // TCP 服务端监听器
    private readonly TcpListener _listener;
    // 记录“某次扫码请求对应的回复”
    private readonly ConcurrentDictionary<string, RuleEnvelope> _scanResponses = new(StringComparer.Ordinal);
    // 记录“某次结果上报对应的 ACK”
    private readonly ConcurrentDictionary<string, RuleEnvelope> _resultAcknowledgements = new(StringComparer.Ordinal);
    // 当前连接的车载端任务列表
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private readonly ConcurrentDictionary<int, ClientSession> _sessions = new();
    private readonly object _travelGate = new();
    private int _clientSequence;
    private long _visitMessageSequence;
    private bool _isArrived;
    private DateTimeOffset _visitExpiresAt;
    private bool _disposed;

    public RuleMockServer(RuleMockSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _listener = new TcpListener(IPAddress.Parse(settings.ListenIp), settings.ListenPort);
        _isArrived = settings.AutoStartVisit;
        _visitExpiresAt = settings.AutoStartVisit
            ? DateTimeOffset.Now.AddMinutes(settings.VisitValidMinutes)
            : DateTimeOffset.MinValue;
    }

    /// <summary>
    /// 监听并接收多个客户端
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 端口开始监听。
        _listener.Start();
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 监听 {_settings.ListenIp}:{_settings.ListenPort}，AGV={_settings.AgvId}");
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 规则：{string.Join(", ", _settings.Rules.Select(rule => $"{rule.Sublot}->{rule.SlotIndex + 1}号仓/{rule.OperationType}"))}");
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 车辆状态：{(IsArrived ? "已到站" : "在途（等待 arrive 命令）")}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 阻塞等待一个 TCP 客户端连接。主循环不会等待这个客户端处理完成，因此还可以继续接收第二、第三个客户端。
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                int clientId = Interlocked.Increment(ref _clientSequence);
                // 每连接一个车载端，就创建一个 HandleClientAsync() 去单独处理它。
                Task task = HandleClientAsync(clientId, client, cancellationToken);
                _clients[clientId] = task;
                // 客户端断开或处理结束后，从 _clients 中移除，避免列表越来越大。
                _ = task.ContinueWith(
                    completedTask =>
                    {
                        _ = completedTask.Exception;
                        _clients.TryRemove(clientId, out _);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Stop();
            await Task.WhenAll(_clients.Values).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 处理一个已经连接进来的车载端 TCP 客户端。不断接收 JSON 消息、解析、分发、回复；直到客户端断开或服务停止。
    /// </summary>
    /// <param name="clientId">Mock 给本次连接分配的编号，只用于日志和客户端管理。</param>
    /// <param name="client">刚刚通过 AcceptTcpClientAsync() 接收到的 TCP 连接。</param>
    /// <param name="serverCancellation">整个 Mock 服务停止时使用的取消信号</param>
    /// <returns></returns>
    private async Task HandleClientAsync(int clientId, TcpClient client, CancellationToken serverCancellation)
    {
        // 把传进来的 client 赋给本地变量 ownedClient。
        using TcpClient ownedClient = client;
        // 创建“当前客户端专用”的取消令牌,之后所有读写操作使用 clientCts.Token，服务停掉时就不会一直卡在网络读取上。
        using CancellationTokenSource clientCts = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
        ownedClient.NoDelay = true;
        // 获取网络流
        NetworkStream stream = ownedClient.GetStream();
        using StreamReader reader = new(stream, new UTF8Encoding(false, true), false, 4_096, true);
        SemaphoreSlim sendLock = new(1, 1);
        ClientSession session = new(stream, sendLock);
        _sessions[clientId] = session;

        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 客户端#{clientId}已连接：{ownedClient.Client.RemoteEndPoint}");
        try
        {
            // 只要当前客户端没有被取消，就不断循环读取消息
            while (!clientCts.IsCancellationRequested)
            {
                // 异步读取一整行文字
                string? line = await reader.ReadLineAsync(clientCts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                RuleEnvelope request;
                try
                {
                    // 把这一行 JSON 转换为 RuleEnvelope,包含了version、type、messageId、correlationId、agvId、visitId、timestamp、payload
                    request = RuleProtocolSerializer.DeserializeLine(line);
                }
                // 如果 JSON 格式不合法、缺少 messageId、缺少 type 等必填字段，会抛异常。
                catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
                {
                    // 给车载端回复一条 protocol.error 消息
                    await SendProtocolErrorAsync(stream, sendLock, "INVALID_JSON", exception.Message, clientCts.Token)
                        .ConfigureAwait(false);
                    continue;
                }

                // JSON 已经解析成功，交给 HandleMessageAsync() 按 request.Type 分发。
                await HandleMessageAsync(clientId, request, stream, sendLock, clientCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            if (!serverCancellation.IsCancellationRequested)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 客户端#{clientId}通信结束：{exception.Message}");
            }
        }
        finally
        {
            _sessions.TryRemove(clientId, out _);
            // 释放这个客户端专属的发送锁，不再占用系统资源。
            sendLock.Dispose();
            // 关闭该客户端对应的 TCP 网络流。网络流关闭后，这条连接就不能再收发 JSON 了。
            await stream.DisposeAsync().ConfigureAwait(false);
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 客户端#{clientId}已断开");
        }
    }

    /// <summary>
    /// 对已经成功解析的 JSON 消息做基础校验，然后按照 type 分发给对应的业务处理方法。
    /// </summary>
    /// <param name="request"></param>
    /// <param name="stream"></param>
    /// <param name="sendLock"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task HandleMessageAsync(
        int clientId,
        RuleEnvelope request,
        NetworkStream stream,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        // 判断对方的协议版本是否与当前程序支持的版本一致。
        if (request.Version != RuleProtocolSerializer.CurrentVersion)
        {
            await SendProtocolErrorAsync(
                stream,
                sendLock,
                "PROTOCOL_VERSION_UNSUPPORTED",
                request.Version,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // 判断消息中的车号是否等于 Mock 配置中的车号。
        if (request.AgvId != _settings.AgvId)
        {
            await SendProtocolErrorAsync(stream, sendLock, "AGV_ID_MISMATCH", request.AgvId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // 前面两个校验都通过后，进入真正的消息路由,会根据不同类型，走不同的逻辑。
        switch (request.Type)
        {
            // 握手消息
            case RuleMessageTypes.Hello:
                await HandleHelloAsync(clientId, request, stream, sendLock, cancellationToken).ConfigureAwait(false);
                break;

            // 心跳消息
            case RuleMessageTypes.Heartbeat:
                await SendAsync(
                    stream,
                    sendLock,
                    RuleProtocolSerializer.Create(
                        RuleMessageTypes.HeartbeatAck,
                        _settings.AgvId,
                        IsArrived ? _settings.VisitId : null,
                        new { accepted = true },
                        correlationId: request.MessageId),
                    cancellationToken).ConfigureAwait(false);
                break;

            // 扫码校验请求
            case RuleMessageTypes.ScanVerifyRequest:
                await HandleScanAsync(request, stream, sendLock, cancellationToken).ConfigureAwait(false);
                break;

            // 车载端操作结果上报。
            case RuleMessageTypes.OperationResult:
                await HandleResultAsync(request, stream, sendLock, cancellationToken).ConfigureAwait(false);
                break;

            // Mock 收到 heartbeat.ack 或 protocol.error。
            case RuleMessageTypes.HeartbeatAck:
            case RuleMessageTypes.ProtocolError:
                break;

            default:
                await SendProtocolErrorAsync(stream, sendLock, "UNKNOWN_MESSAGE_TYPE", request.Type, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleHelloAsync(
        int clientId,
        RuleEnvelope request,
        NetworkStream stream,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        _ = RuleProtocolSerializer.DeserializePayload<HelloPayload>(request);
        RuleEnvelope acknowledgement = RuleProtocolSerializer.Create(
            RuleMessageTypes.HelloAck,
            _settings.AgvId,
            null,
            new HelloAckPayload(true, null, null),
            correlationId: request.MessageId);
        await SendAsync(stream, sendLock, acknowledgement, cancellationToken).ConfigureAwait(false);
        if (_sessions.TryGetValue(clientId, out ClientSession? session))
        {
            session.MarkHandshaken();
        }

        if (IsArrived)
        {
            await SendAsync(stream, sendLock, CreateVisitStartedEnvelope(), cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 客户端#{clientId}握手完成，当前已到站：{_settings.VisitId}");
        }
        else
        {
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 客户端#{clientId}握手完成，车辆仍在途；输入 arrive 模拟到站。");
        }
    }

    private async Task HandleScanAsync(
        RuleEnvelope request,
        NetworkStream stream,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        if (_settings.Faults.IgnoreScanRequests)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 故障注入：忽略扫码请求 {request.MessageId}");
            return;
        }

        if (!IsVisitActive(request.VisitId))
        {
            RuleEnvelope notArrived = CreateRejectedScanResponse(
                request,
                "VISIT_NOT_ACTIVE",
                "车辆尚未到站或当前到站已结束");
            await SendAsync(stream, sendLock, notArrived, cancellationToken).ConfigureAwait(false);
            return;
        }

        RuleEnvelope response = _scanResponses.GetOrAdd(request.MessageId, _ => CreateScanResponse(request));
        if (_settings.ResponseDelayMs > 0)
        {
            await Task.Delay(_settings.ResponseDelayMs, cancellationToken).ConfigureAwait(false);
        }

        await SendAsync(stream, sendLock, response, cancellationToken).ConfigureAwait(false);
        if (_settings.Faults.SendDuplicateScanResponse)
        {
            await SendAsync(stream, sendLock, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private RuleEnvelope CreateScanResponse(RuleEnvelope request)
    {
        ScanVerifyRequestPayload payload = RuleProtocolSerializer.DeserializePayload<ScanVerifyRequestPayload>(request);
        MockSublotRule? rule = _settings.Rules.FirstOrDefault(
            item => string.Equals(item.Sublot, payload.Sublot, StringComparison.OrdinalIgnoreCase));

        ScanVerifyResponsePayload responsePayload;
        if (request.VisitId != _settings.VisitId)
        {
            responsePayload = new(false, null, null, null, null, null, null, "VISIT_NOT_ACTIVE", "当前到站ID无效");
        }
        else if (rule is null)
        {
            responsePayload = new(false, null, null, null, null, null, null, "SUBLOT_NOT_FOUND", "未找到对应任务");
        }
        else
        {
            int slotIndex = _settings.Faults.ReturnInvalidSlotIndex ? 8 : rule.SlotIndex;
            responsePayload = new(
                true,
                $"OP-{Guid.NewGuid():N}",
                rule.TaskId,
                payload.Sublot,
                slotIndex,
                rule.OperationType,
                rule.OperationType == "Load",
                null,
                null);
        }

        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 扫码 {payload.Sublot} -> accepted={responsePayload.Accepted}, slot={responsePayload.SlotIndex}");
        return RuleProtocolSerializer.Create(
            RuleMessageTypes.ScanVerifyResponse,
            _settings.AgvId,
            request.VisitId,
            responsePayload,
            correlationId: request.MessageId);
    }

    private RuleEnvelope CreateRejectedScanResponse(RuleEnvelope request, string code, string message)
    {
        ScanVerifyRequestPayload payload = RuleProtocolSerializer.DeserializePayload<ScanVerifyRequestPayload>(request);
        ScanVerifyResponsePayload responsePayload = new(
            false,
            null,
            null,
            payload.Sublot,
            null,
            null,
            null,
            code,
            message);
        return RuleProtocolSerializer.Create(
            RuleMessageTypes.ScanVerifyResponse,
            _settings.AgvId,
            request.VisitId,
            responsePayload,
            correlationId: request.MessageId);
    }

    private async Task HandleResultAsync(
        RuleEnvelope request,
        NetworkStream stream,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        OperationResultPayload payload = RuleProtocolSerializer.DeserializePayload<OperationResultPayload>(request);
        Console.WriteLine(
            $"[{DateTimeOffset.Now:HH:mm:ss}] 结果 operationId={payload.OperationId}, slot={payload.SlotIndex + 1}, success={payload.Success}, failure={payload.FailureCode ?? "-"}");

        if (_settings.Faults.DropResultAcknowledgements)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 故障注入：丢弃结果ACK {request.MessageId}");
            return;
        }

        RuleEnvelope acknowledgement = _resultAcknowledgements.GetOrAdd(
            request.MessageId,
            _ => RuleProtocolSerializer.Create(
                RuleMessageTypes.OperationResultAck,
                _settings.AgvId,
                request.VisitId,
                new OperationResultAckPayload(payload.OperationId, true, null, null),
                correlationId: request.MessageId));
        await SendAsync(stream, sendLock, acknowledgement, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendProtocolErrorAsync(
        NetworkStream stream,
        SemaphoreSlim sendLock,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        RuleEnvelope error = RuleProtocolSerializer.Create(
            RuleMessageTypes.ProtocolError,
            _settings.AgvId,
            IsArrived ? _settings.VisitId : null,
            new ProtocolErrorPayload(code, message));
        await SendAsync(stream, sendLock, error, cancellationToken).ConfigureAwait(false);
    }

    public bool IsArrived
    {
        get
        {
            lock (_travelGate)
            {
                return _isArrived && _visitExpiresAt > DateTimeOffset.Now;
            }
        }
    }

    public string GetStatusText()
    {
        lock (_travelGate)
        {
            string travel = _isArrived && _visitExpiresAt > DateTimeOffset.Now
                ? $"已到站，visitId={_settings.VisitId}，有效至{_visitExpiresAt:HH:mm:ss}"
                : "在途/未到站";
            int handshaken = _sessions.Values.Count(session => session.IsHandshaken);
            return $"[{DateTimeOffset.Now:HH:mm:ss}] 状态：{travel}，TCP客户端={_sessions.Count}，已握手={handshaken}。";
        }
    }

    public async Task ArriveAsync(CancellationToken cancellationToken = default)
    {
        lock (_travelGate)
        {
            if (_isArrived && _visitExpiresAt > DateTimeOffset.Now)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 车辆已经到站，无需重复发送。 ");
                return;
            }

            _isArrived = true;
            _visitExpiresAt = DateTimeOffset.Now.AddMinutes(_settings.VisitValidMinutes);
        }

        RuleEnvelope visit = CreateVisitStartedEnvelope();
        await BroadcastAsync(visit, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 模拟到站：{_settings.VisitId}，已通知所有已握手客户端。");
    }

    public async Task DepartAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        lock (_travelGate)
        {
            if (!_isArrived)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 车辆当前已经在途。 ");
                return;
            }

            _isArrived = false;
            _visitExpiresAt = DateTimeOffset.MinValue;
        }

        RuleEnvelope ended = RuleProtocolSerializer.Create(
            RuleMessageTypes.VisitEnded,
            _settings.AgvId,
            _settings.VisitId,
            new VisitEndedPayload(reason ?? "MOCK模拟离站"),
            messageId: NextVisitMessageId("END"));
        await BroadcastAsync(ended, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 模拟离站，车辆进入在途状态：{_settings.VisitId}。");
    }

    private bool IsVisitActive(string? visitId)
    {
        lock (_travelGate)
        {
            return _isArrived
                && _visitExpiresAt > DateTimeOffset.Now
                && visitId == _settings.VisitId;
        }
    }

    private RuleEnvelope CreateVisitStartedEnvelope()
    {
        DateTimeOffset expiresAt;
        lock (_travelGate)
        {
            expiresAt = _visitExpiresAt;
        }

        return RuleProtocolSerializer.Create(
            RuleMessageTypes.VisitStarted,
            _settings.AgvId,
            _settings.VisitId,
            new VisitStartedPayload(
                _settings.StationId,
                _settings.StationName,
                _settings.AllowOperation,
                expiresAt),
            messageId: NextVisitMessageId("START"));
    }

    private string NextVisitMessageId(string suffix) =>
        $"MSG-{_settings.VisitId}-{suffix}-{Interlocked.Increment(ref _visitMessageSequence)}";

    private async Task BroadcastAsync(RuleEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (ClientSession session in _sessions.Values.Where(session => session.IsHandshaken))
        {
            try
            {
                await SendAsync(session.Stream, session.SendLock, envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 广播{envelope.Type}失败：{exception.Message}");
            }
        }
    }

    private static async Task SendAsync(
        NetworkStream stream,
        SemaphoreSlim sendLock,
        RuleEnvelope envelope,
        CancellationToken cancellationToken)
    {
        byte[] data = Encoding.UTF8.GetBytes(RuleProtocolSerializer.SerializeLine(envelope) + "\n");
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listener.Stop();
        await Task.WhenAll(_clients.Values).ConfigureAwait(false);
    }

    private sealed class ClientSession(NetworkStream stream, SemaphoreSlim sendLock)
    {
        private int _handshaken;

        public NetworkStream Stream { get; } = stream;

        public SemaphoreSlim SendLock { get; } = sendLock;

        public bool IsHandshaken => Volatile.Read(ref _handshaken) == 1;

        public void MarkHandshaken() => Interlocked.Exchange(ref _handshaken, 1);
    }
}
