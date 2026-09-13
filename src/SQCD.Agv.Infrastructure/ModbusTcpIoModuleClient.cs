using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

public sealed class ModbusTcpIoModuleClient : IIoModuleClient
{
    private const byte ReadCoilsFunction = 0x01;
    private const byte ReadDiscreteInputsFunction = 0x02;
    private const byte WriteSingleCoilFunction = 0x05;

    private readonly IoModuleSettings _settings;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _transportLock = new(1, 1);
    private readonly object _lifecycleGate = new();
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _pollTask;
    private IoSnapshot _currentSnapshot = IoSnapshot.Unknown(DateTimeOffset.Now);
    private int _transactionId;
    private bool _disposed;

    public ModbusTcpIoModuleClient(IoModuleSettings settings, IAppLogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConnected => CurrentSnapshot.IsConnected;

    public IoSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);

    public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

    public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged;

    public Task StartAsync(CancellationToken applicationStopping)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lifecycleGate)
        {
            if (_pollTask is not null)
            {
                return Task.CompletedTask;
            }

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
            _pollTask = Task.Run(() => PollLoopAsync(_lifetimeCts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        lock (_lifecycleGate)
        {
            _lifetimeCts?.Cancel();
            task = _pollTask;
            _pollTask = null;
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

        await CloseTransportAsync().ConfigureAwait(false);
        PublishDisconnected();
    }

    public async Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken)
    {
        SlotIoMapping mapping = GetMapping(slotIndex);
        ushort address = checked((ushort)(_settings.DoStartAddress + mapping.DoChannel));

        byte[] pdu = new byte[5];
        pdu[0] = WriteSingleCoilFunction;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), 0xFF00);

        byte[] response = await ExecuteRequestAsync(pdu, WriteSingleCoilFunction, cancellationToken).ConfigureAwait(false);
        if (!response.AsSpan().SequenceEqual(pdu))
        {
            throw new IOException("FC05响应与请求不一致，开锁结果未知。");
        }

        _logger.Write(LogSeverity.Information, nameof(ModbusTcpIoModuleClient),
            $"已向物理{slotIndex + 1}号仓发送开锁脉冲触发，DO地址={address}。硬件负责自动复位。 ");
    }

    public async Task<LockerSnapshot> WaitForLockerAsync(
        int slotIndex,
        Func<LockerSnapshot, bool> predicate,
        TimeSpan timeout,
        TimeSpan stableWindow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _ = GetMapping(slotIndex);

        long started = Stopwatch.GetTimestamp();
        DateTimeOffset? stableStartedAt = null;
        DateTimeOffset? lastEvaluatedAt = null;

        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IoSnapshot snapshot = CurrentSnapshot;
            if (!snapshot.IsConnected)
            {
                throw new IOException("等待仓位反馈时IO连接已断开。");
            }

            LockerSnapshot locker = snapshot.GetLocker(slotIndex);
            if (lastEvaluatedAt == locker.ObservedAt)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                continue;
            }

            lastEvaluatedAt = locker.ObservedAt;
            if (predicate(locker))
            {
                stableStartedAt ??= locker.ObservedAt;
                if (stableWindow <= TimeSpan.Zero || locker.ObservedAt - stableStartedAt.Value >= stableWindow)
                {
                    return locker;
                }
            }
            else
            {
                stableStartedAt = null;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"等待物理{slotIndex + 1}号仓反馈超时。");
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        bool failureLogged = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                bool[] outputs = await ReadBitsAsync(
                    ReadCoilsFunction,
                    _settings.DoStartAddress,
                    _settings.ChannelCount,
                    cancellationToken).ConfigureAwait(false);
                bool[] inputs = await ReadBitsAsync(
                    ReadDiscreteInputsFunction,
                    _settings.DiStartAddress,
                    _settings.ChannelCount,
                    cancellationToken).ConfigureAwait(false);

                PublishConnectedSnapshot(outputs, inputs);
                if (failureLogged)
                {
                    _logger.Write(LogSeverity.Information, nameof(ModbusTcpIoModuleClient), "IO通信已恢复。");
                    failureLogged = false;
                }

                await Task.Delay(_settings.PollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or SocketException or TimeoutException)
            {
                PublishDisconnected();
                if (!failureLogged)
                {
                    _logger.Write(LogSeverity.Warning, nameof(ModbusTcpIoModuleClient), "IO通信中断，将自动重连。", exception);
                    failureLogged = true;
                }

                await DelayAfterFailureAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool[]> ReadBitsAsync(
        byte function,
        ushort startAddress,
        ushort count,
        CancellationToken cancellationToken)
    {
        byte[] pdu = new byte[5];
        pdu[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), count);

        byte[] response = await ExecuteRequestAsync(pdu, function, cancellationToken).ConfigureAwait(false);
        if (response.Length < 2 || response[1] != (count + 7) / 8 || response.Length != response[1] + 2)
        {
            throw new IOException($"FC{function:X2}响应字节数无效。");
        }

        bool[] result = new bool[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = (response[2 + (index / 8)] & (1 << (index % 8))) != 0;
        }

        return result;
    }

    /// <summary>
    /// 事务号在 1～255 之间循环。现场三台车上的康耐德 C2000 仓位模块只回写 MBAP 事务号的低 8 位，
    /// 16 位递增时第 256 条请求的应答是 0，严格比对就断开，而且重连后每一条都对不上（onboard-hmi#52）。
    /// 1～255 内完整回写 16 位的模块（slots-simulator）与只回写低 8 位的模块答出的是同一个值。
    /// 只在持有 <see cref="_transportLock"/> 时调用，所以不需要原子操作。
    /// </summary>
    private ushort NextTransactionId()
    {
        _transactionId = (_transactionId % byte.MaxValue) + 1;
        return (ushort)_transactionId;
    }

    private async Task<byte[]> ExecuteRequestAsync(byte[] pdu, byte expectedFunction, CancellationToken cancellationToken)
    {
        await _transportLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            NetworkStream stream = _stream ?? throw new IOException("Modbus TCP连接不可用。");
            ushort transactionId = NextTransactionId();

            byte[] request = new byte[7 + pdu.Length];
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), checked((ushort)(pdu.Length + 1)));
            request[6] = _settings.UnitId;
            pdu.CopyTo(request, 7);

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_settings.RequestTimeoutMs);
            await stream.WriteAsync(request, timeoutCts.Token).ConfigureAwait(false);

            byte[] header = new byte[7];
            await stream.ReadExactlyAsync(header, timeoutCts.Token).ConfigureAwait(false);
            ushort responseTransactionId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
            if (responseTransactionId != transactionId || protocolId != 0 || header[6] != _settings.UnitId || length is < 2 or > 254)
            {
                throw new IOException("Modbus TCP响应头无效。");
            }

            byte[] responsePdu = new byte[length - 1];
            await stream.ReadExactlyAsync(responsePdu, timeoutCts.Token).ConfigureAwait(false);
            if (responsePdu[0] == (expectedFunction | 0x80))
            {
                byte errorCode = responsePdu.Length > 1 ? responsePdu[1] : (byte)0;
                throw new IOException($"Modbus设备返回异常，功能码=0x{expectedFunction:X2}，异常码=0x{errorCode:X2}。");
            }

            if (responsePdu[0] != expectedFunction)
            {
                throw new IOException($"Modbus响应功能码不匹配，期望0x{expectedFunction:X2}，实际0x{responsePdu[0]:X2}。");
            }

            return responsePdu;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await CloseTransportCoreAsync().ConfigureAwait(false);
            throw new TimeoutException("Modbus TCP请求超时。", exception);
        }
        catch
        {
            await CloseTransportCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _transportLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null && _tcpClient is not null)
        {
            return;
        }

        TcpClient client = new();
        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_settings.RequestTimeoutMs);
            await client.ConnectAsync(_settings.Host, _settings.Port, timeoutCts.Token).ConfigureAwait(false);
            client.NoDelay = true;
            _tcpClient = client;
            _stream = client.GetStream();
            _logger.Write(LogSeverity.Information, nameof(ModbusTcpIoModuleClient),
                $"已连接IO模块 {_settings.Host}:{_settings.Port}，UnitId={_settings.UnitId}。");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void PublishConnectedSnapshot(bool[] outputs, bool[] inputs)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        LockerSnapshot[] lockers = _settings.Slots
            .OrderBy(mapping => mapping.SlotIndex)
            .Select(mapping => new LockerSnapshot(
                mapping.SlotIndex,
                mapping.SlotIndex + 1,
                outputs[mapping.DoChannel],
                inputs[mapping.LockFeedbackDiChannel],
                inputs[mapping.LightCurtainDiChannel],
                now))
            .ToArray();
        IoSnapshot next = new(true, lockers, now);
        LogIoChanges(CurrentSnapshot, next);
        PublishSnapshot(next);
    }

    private void PublishDisconnected()
    {
        PublishSnapshot(IoSnapshot.Unknown(DateTimeOffset.Now));
    }

    private void PublishSnapshot(IoSnapshot snapshot)
    {
        IoSnapshot previous = Interlocked.Exchange(ref _currentSnapshot, snapshot);
        if (previous.IsConnected != snapshot.IsConnected)
        {
            ConnectionChanged?.Invoke(this, new ValueChangedEventArgs<bool>(snapshot.IsConnected));
        }

        SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(snapshot));
    }

    private void LogIoChanges(IoSnapshot previous, IoSnapshot next)
    {
        if (!previous.IsConnected || previous.Lockers.Count != next.Lockers.Count)
        {
            return;
        }

        foreach (LockerSnapshot current in next.Lockers)
        {
            LockerSnapshot old = previous.GetLocker(current.SlotIndex);
            if (old.UnlockOutputRaw != current.UnlockOutputRaw
                || old.LockFeedbackRaw != current.LockFeedbackRaw
                || old.LightCurtainRaw != current.LightCurtainRaw)
            {
                _logger.Write(LogSeverity.Information, nameof(ModbusTcpIoModuleClient),
                    $"{current.PhysicalNumber}号仓IO变化：DO {ToRaw(old.UnlockOutputRaw)}->{ToRaw(current.UnlockOutputRaw)}，" +
                    $"锁DI {ToRaw(old.LockFeedbackRaw)}->{ToRaw(current.LockFeedbackRaw)}，" +
                    $"光幕DI {ToRaw(old.LightCurtainRaw)}->{ToRaw(current.LightCurtainRaw)}。");
            }
        }
    }

    private static string ToRaw(bool? value) => value.HasValue ? (value.Value ? "1" : "0") : "?";

    private SlotIoMapping GetMapping(int slotIndex)
    {
        return _settings.Slots.FirstOrDefault(mapping => mapping.SlotIndex == slotIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(slotIndex), "未配置对应仓位的IO映射。");
    }

    private async Task DelayAfterFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_settings.ReconnectDelayMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task CloseTransportAsync()
    {
        await _transportLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseTransportCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _transportLock.Release();
        }
    }

    private async Task CloseTransportCoreAsync()
    {
        NetworkStream? stream = _stream;
        _stream = null;
        TcpClient? client = _tcpClient;
        _tcpClient = null;

        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        client?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _lifetimeCts?.Dispose();
        _transportLock.Dispose();
    }
}
