using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

public sealed class TcpJsonRuleGateway : IRuleGateway
{
    private readonly RuleGatewaySettings _settings;
    private readonly string _agvId;
    private readonly string _onboardInstanceId;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _lifecycleGate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RuleEnvelope>> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _receivedVisitMessages = new();
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _connectionCts;
    private Task? _connectionTask;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private TaskCompletionSource<HelloAckPayload>? _helloAcknowledgement;
    private VisitContext? _currentVisit;
    private int _connected;
    private long _lastHeartbeatAcknowledgement;
    private bool _disposed;

    public TcpJsonRuleGateway(
        RuleGatewaySettings settings,
        string agvId,
        string onboardInstanceId,
        IAppLogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _agvId = string.IsNullOrWhiteSpace(agvId) ? throw new ArgumentException("AGV编号不能为空。", nameof(agvId)) : agvId;
        _onboardInstanceId = string.IsNullOrWhiteSpace(onboardInstanceId)
            ? throw new ArgumentException("车载实例编号不能为空。", nameof(onboardInstanceId))
            : onboardInstanceId;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    public VisitContext? CurrentVisit => Volatile.Read(ref _currentVisit);

    public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

    public event EventHandler<ValueChangedEventArgs<VisitContext?>>? VisitChanged;

    public Func<RuleHeartbeatStatus>? HeartbeatStatusProvider { private get; set; }

    public Task StartAsync(CancellationToken applicationStopping)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lifecycleGate)
        {
            if (_connectionTask is not null)
            {
                return Task.CompletedTask;
            }

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
            _connectionTask = Task.Run(() => ConnectionLoopAsync(_lifetimeCts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        lock (_lifecycleGate)
        {
            _lifetimeCts?.Cancel();
            _connectionCts?.Cancel();
            CloseConnection();
            task = _connectionTask;
            _connectionTask = null;
        }

        if (task is not null)
        {
            try
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        SetConnected(false);
        FailPending(new IOException("规则模块连接已停止。"));
    }

    public async Task<ScanAuthorization> VerifyScanAsync(
        ScanVerificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        VisitContext visit = CurrentVisit ?? throw new InvalidOperationException("当前没有有效到站上下文。");
        if (!IsConnected || visit.VisitId != request.VisitId || !visit.IsActive(DateTimeOffset.Now))
        {
            throw new InvalidOperationException("规则模块未连接或当前到站已失效。");
        }

        RuleEnvelope envelope = RuleProtocolSerializer.Create(
            RuleMessageTypes.ScanVerifyRequest,
            _agvId,
            request.VisitId,
            new ScanVerifyRequestPayload(request.Sublot, request.InputMethod.ToString()));

        RuleEnvelope response = await SendRequestAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (response.Type != RuleMessageTypes.ScanVerifyResponse
            || response.CorrelationId != envelope.MessageId
            || response.AgvId != _agvId
            || response.VisitId != request.VisitId)
        {
            throw new InvalidDataException("扫码核验响应的关联信息不匹配。");
        }

        ScanVerifyResponsePayload payload = RuleProtocolSerializer.DeserializePayload<ScanVerifyResponsePayload>(response);
        if (!payload.Accepted)
        {
            return ScanAuthorization.Rejected(
                request.Sublot,
                payload.ErrorCode ?? "SCAN_REJECTED",
                payload.ErrorMessage ?? "规则模块拒绝了本次扫码。");
        }

        if (!string.Equals(payload.Sublot, request.Sublot, StringComparison.Ordinal))
        {
            throw new InvalidDataException("扫码核验响应的SUBLOT与请求不一致。");
        }

        OperationType? operationType = payload.OperationType switch
        {
            "Load" => OperationType.Load,
            "Unload" => OperationType.Unload,
            _ => null
        };

        return new ScanAuthorization(
            true,
            payload.OperationId,
            payload.TaskId,
            payload.Sublot ?? request.Sublot,
            payload.SlotIndex,
            operationType,
            payload.ExpectedCargoAfter,
            null,
            null);
    }

    public async Task<bool> ReportOperationAsync(OperationResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        OperationResultPayload payload = new(
            result.OperationId,
            result.TaskId,
            result.Sublot,
            result.SlotIndex,
            result.OperationType.ToString(),
            result.Success,
            result.FailureCode,
            result.Success ? null : result.FailureStage.ToString(),
            ToRaw(result.FinalLocker.UnlockOutputRaw),
            ToRaw(result.FinalLocker.LockFeedbackRaw),
            ToRaw(result.FinalLocker.LightCurtainRaw),
            result.DeparturePermitted,
            result.StartedAt,
            result.CompletedAt);

        RuleEnvelope envelope = RuleProtocolSerializer.Create(
            RuleMessageTypes.OperationResult,
            _agvId,
            result.VisitId,
            payload,
            messageId: result.MessageId,
            timestamp: result.CompletedAt);

        for (int attempt = 1; attempt <= _settings.ResultRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RuleEnvelope response = await SendRequestAsync(envelope, cancellationToken).ConfigureAwait(false);
                if (response.Type != RuleMessageTypes.OperationResultAck
                    || response.CorrelationId != result.MessageId
                    || response.VisitId != result.VisitId)
                {
                    _logger.Write(LogSeverity.Warning, nameof(TcpJsonRuleGateway),
                        $"忽略不匹配的结果ACK，operationId={result.OperationId}。");
                    continue;
                }

                OperationResultAckPayload acknowledgement =
                    RuleProtocolSerializer.DeserializePayload<OperationResultAckPayload>(response);
                return acknowledgement.Accepted && acknowledgement.OperationId == result.OperationId;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
            {
                _logger.Write(LogSeverity.Warning, nameof(TcpJsonRuleGateway),
                    $"结果上报第{attempt}次未确认，将使用相同messageId重试。", exception);
                if (attempt < _settings.ResultRetryCount)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return false;
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        int reconnectIndex = 0;
        bool failureLogged = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using CancellationTokenSource connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _connectionCts = connectionCts;
                await ConnectAsync(connectionCts.Token).ConfigureAwait(false);

                _helloAcknowledgement = new TaskCompletionSource<HelloAckPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task readTask = ReadLoopAsync(connectionCts.Token);
                RuleEnvelope hello = RuleProtocolSerializer.Create(
                    RuleMessageTypes.Hello,
                    _agvId,
                    null,
                    new HelloPayload(_onboardInstanceId, "0.1.0", RuleProtocolSerializer.CurrentVersion));
                await SendEnvelopeAsync(hello, connectionCts.Token).ConfigureAwait(false);

                HelloAckPayload helloAck = await _helloAcknowledgement.Task
                    .WaitAsync(TimeSpan.FromMilliseconds(_settings.RequestTimeoutMs), connectionCts.Token)
                    .ConfigureAwait(false);
                if (!helloAck.Accepted)
                {
                    throw new InvalidDataException(helloAck.ErrorMessage ?? "规则模块拒绝握手。");
                }

                SetConnected(true);
                Interlocked.Exchange(ref _lastHeartbeatAcknowledgement, Stopwatch.GetTimestamp());
                reconnectIndex = 0;
                failureLogged = false;
                _logger.Write(LogSeverity.Information, nameof(TcpJsonRuleGateway),
                    $"已连接规则模块 {_settings.Host}:{_settings.Port} 并完成握手。");

                Task heartbeatTask = HeartbeatLoopAsync(connectionCts.Token);
                await Task.WhenAny(readTask, heartbeatTask).ConfigureAwait(false);
                connectionCts.Cancel();
                await ObserveConnectionTaskAsync(readTask).ConfigureAwait(false);
                await ObserveConnectionTaskAsync(heartbeatTask).ConfigureAwait(false);
                throw new IOException("规则模块连接已结束。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or InvalidDataException)
            {
                if (!failureLogged)
                {
                    _logger.Write(LogSeverity.Warning, nameof(TcpJsonRuleGateway), "规则模块连接中断，将自动重连。", exception);
                    failureLogged = true;
                }
            }
            finally
            {
                _connectionCts = null;
                _helloAcknowledgement = null;
                CloseConnection();
                SetConnected(false);
                ClearCurrentVisit();
                FailPending(new IOException("规则模块连接已断开。"));
            }

            int configuredDelay = _settings.ReconnectDelaysMs[Math.Min(reconnectIndex, _settings.ReconnectDelaysMs.Length - 1)];
            reconnectIndex++;
            try
            {
                await Task.Delay(configuredDelay + Random.Shared.Next(0, 251), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        TcpClient client = new();
        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_settings.ConnectTimeoutMs);
            await client.ConnectAsync(_settings.Host, _settings.Port, timeoutCts.Token).ConfigureAwait(false);
            client.NoDelay = true;
            _client = client;
            _stream = client.GetStream();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException("连接规则模块超时。", exception);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        NetworkStream stream = _stream ?? throw new IOException("规则模块网络流不可用。");
        using StreamReader reader = new(stream, new UTF8Encoding(false, true), false, 4_096, true);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new IOException("规则模块关闭了TCP连接。");
            }

            RuleEnvelope envelope;
            try
            {
                envelope = RuleProtocolSerializer.DeserializeLine(line);
            }
            catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
            {
                _logger.Write(LogSeverity.Warning, nameof(TcpJsonRuleGateway), "收到非法JSON协议消息，已忽略。", exception);
                await TrySendProtocolErrorAsync("INVALID_JSON", exception.Message, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await HandleEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleEnvelopeAsync(RuleEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Version != RuleProtocolSerializer.CurrentVersion)
        {
            await TrySendProtocolErrorAsync("PROTOCOL_VERSION_UNSUPPORTED", envelope.Version, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (envelope.AgvId != _agvId)
        {
            _logger.Write(LogSeverity.Warning, nameof(TcpJsonRuleGateway), $"忽略其他AGV消息：{envelope.AgvId}。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(envelope.CorrelationId)
            && _pending.TryGetValue(envelope.CorrelationId, out TaskCompletionSource<RuleEnvelope>? pending))
        {
            pending.TrySetResult(envelope);
            return;
        }

        switch (envelope.Type)
        {
            case RuleMessageTypes.HelloAck:
                _helloAcknowledgement?.TrySetResult(RuleProtocolSerializer.DeserializePayload<HelloAckPayload>(envelope));
                break;

            case RuleMessageTypes.Heartbeat:
                RuleEnvelope heartbeatAck = RuleProtocolSerializer.Create(
                    RuleMessageTypes.HeartbeatAck,
                    _agvId,
                    CurrentVisit?.VisitId,
                    new { accepted = true },
                    correlationId: envelope.MessageId);
                await SendEnvelopeAsync(heartbeatAck, cancellationToken).ConfigureAwait(false);
                break;

            case RuleMessageTypes.HeartbeatAck:
                Interlocked.Exchange(ref _lastHeartbeatAcknowledgement, Stopwatch.GetTimestamp());
                break;

            case RuleMessageTypes.VisitStarted:
                HandleVisitStarted(envelope);
                break;

            case RuleMessageTypes.VisitEnded:
                HandleVisitEnded(envelope);
                break;

            case RuleMessageTypes.ProtocolError:
                ProtocolErrorPayload error = RuleProtocolSerializer.DeserializePayload<ProtocolErrorPayload>(envelope);
                _logger.Write(LogSeverity.Warning, nameof(TcpJsonRuleGateway),
                    $"规则模块返回协议错误：{error.ErrorCode} - {error.ErrorMessage}");
                break;

            default:
                _logger.Write(LogSeverity.Warning, nameof(TcpJsonRuleGateway), $"忽略未知消息类型：{envelope.Type}。");
                await TrySendProtocolErrorAsync("UNKNOWN_MESSAGE_TYPE", envelope.Type, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private void HandleVisitStarted(RuleEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.VisitId) || !_receivedVisitMessages.TryAdd(envelope.MessageId, 0))
        {
            return;
        }

        VisitStartedPayload payload = RuleProtocolSerializer.DeserializePayload<VisitStartedPayload>(envelope);
        VisitContext? current = CurrentVisit;
        if (current is not null && current.VisitId != envelope.VisitId)
        {
            _logger.Write(LogSeverity.Warning, nameof(TcpJsonRuleGateway),
                $"当前到站{current.VisitId}尚未结束，忽略新到站{envelope.VisitId}。");
            return;
        }

        VisitContext visit = new(envelope.VisitId, payload.StationId, payload.StationName, payload.AllowOperation, payload.ExpiresAt);
        Interlocked.Exchange(ref _currentVisit, visit);
        VisitChanged?.Invoke(this, new ValueChangedEventArgs<VisitContext?>(visit));
        _logger.Write(LogSeverity.Information, nameof(TcpJsonRuleGateway),
            $"收到到站通知：{visit.StationName}({visit.VisitId})，允许操作={visit.AllowOperation}。");
    }

    private void HandleVisitEnded(RuleEnvelope envelope)
    {
        VisitContext? current = CurrentVisit;
        if (current is null || current.VisitId != envelope.VisitId)
        {
            return;
        }

        Interlocked.Exchange(ref _currentVisit, null);
        VisitChanged?.Invoke(this, new ValueChangedEventArgs<VisitContext?>(null));
        _logger.Write(LogSeverity.Information, nameof(TcpJsonRuleGateway), $"到站上下文已结束：{current.VisitId}。");
    }

    private void ClearCurrentVisit()
    {
        VisitContext? current = Interlocked.Exchange(ref _currentVisit, null);
        if (current is null)
        {
            return;
        }

        VisitChanged?.Invoke(this, new ValueChangedEventArgs<VisitContext?>(null));
        _logger.Write(LogSeverity.Information, nameof(TcpJsonRuleGateway),
            $"规则连接已断开，清除到站上下文：{current.VisitId}。");
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_settings.HeartbeatIntervalMs));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            long lastAcknowledgement = Volatile.Read(ref _lastHeartbeatAcknowledgement);
            if (lastAcknowledgement != 0
                && Stopwatch.GetElapsedTime(lastAcknowledgement)
                    > TimeSpan.FromMilliseconds(_settings.HeartbeatIntervalMs * 3L))
            {
                throw new TimeoutException("连续三个心跳周期未收到规则模块响应。");
            }

            RuleHeartbeatStatus status = HeartbeatStatusProvider?.Invoke() ?? new RuleHeartbeatStatus(false, null, false);
            RuleEnvelope heartbeat = RuleProtocolSerializer.Create(
                RuleMessageTypes.Heartbeat,
                _agvId,
                CurrentVisit?.VisitId,
                new HeartbeatPayload(status.IoOnline, status.ActiveOperationId, status.DeparturePermitted));
            await SendEnvelopeAsync(heartbeat, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RuleEnvelope> SendRequestAsync(RuleEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("规则模块未连接。");
        }

        TaskCompletionSource<RuleEnvelope> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(envelope.MessageId, completion))
        {
            throw new InvalidOperationException($"消息{envelope.MessageId}已存在未决请求。");
        }

        try
        {
            await SendEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
            return await completion.Task
                .WaitAsync(TimeSpan.FromMilliseconds(_settings.RequestTimeoutMs), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(envelope.MessageId, out _);
        }
    }

    private async Task SendEnvelopeAsync(RuleEnvelope envelope, CancellationToken cancellationToken)
    {
        string line = RuleProtocolSerializer.SerializeLine(envelope) + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(line);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NetworkStream stream = _stream ?? throw new IOException("规则模块TCP连接不可用。");
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task TrySendProtocolErrorAsync(string code, string message, CancellationToken cancellationToken)
    {
        try
        {
            RuleEnvelope error = RuleProtocolSerializer.Create(
                RuleMessageTypes.ProtocolError,
                _agvId,
                CurrentVisit?.VisitId,
                new ProtocolErrorPayload(code, message));
            await SendEnvelopeAsync(error, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            _logger.Write(LogSeverity.Debug, nameof(TcpJsonRuleGateway), "协议错误响应发送失败。", exception);
        }
    }

    private void SetConnected(bool value)
    {
        int next = value ? 1 : 0;
        int previous = Interlocked.Exchange(ref _connected, next);
        if (previous != next)
        {
            ConnectionChanged?.Invoke(this, new ValueChangedEventArgs<bool>(value));
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (TaskCompletionSource<RuleEnvelope> pending in _pending.Values)
        {
            pending.TrySetException(exception);
        }
    }

    private void CloseConnection()
    {
        NetworkStream? stream = Interlocked.Exchange(ref _stream, null);
        TcpClient? client = Interlocked.Exchange(ref _client, null);
        stream?.Dispose();
        client?.Dispose();
    }

    private static int? ToRaw(bool? value) => value.HasValue ? (value.Value ? 1 : 0) : null;

    private static async Task ObserveConnectionTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _connectionCts?.Dispose();
        _lifetimeCts?.Dispose();
        _sendLock.Dispose();
    }
}

public sealed record RuleHeartbeatStatus(bool IoOnline, string? ActiveOperationId, bool DeparturePermitted);
