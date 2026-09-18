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
    private const byte WriteMultipleCoilsFunction = 0x0F;

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

    /// <summary>
    /// Unlocks a set of slots on this module with FC0F multiple-coil writes of ones (ADR-cross-0035
    /// BatchUnlock). One write covers each contiguous run of target DO addresses; with the standard
    /// mapping (DO channel = slot index) a contiguous slot set is a single write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A channel between two targets is never written, not even with a zero. Whether writing 0 to an
    /// idle unlock output is inert depends on the module: the slots simulator's <c>FollowOutput</c>
    /// lock-feedback model treats a written 0 as the output ending and reports the lock closing, and
    /// nothing documents the real module either way. So a gap splits the set into another write.
    /// </para>
    /// <para>
    /// Only ones are written, exactly as <see cref="PulseUnlockAsync"/> writes 0xFF00 and nothing else:
    /// the reset is the hardware pulse timer's (<c>PulseResetMilliseconds</c>, part of the slot
    /// configuration fingerprint), and the executors prove it slot by slot from the read-back output.
    /// Do not add a software write of 0 after the pulse width. It would make that read-back prove our
    /// own write instead of the timer, hiding an output stuck on; it would race the timer and could
    /// shorten a pulse; and it would give the batch path a reset the per-slot FC05 path does not have.
    /// </para>
    /// <para>
    /// This module is the only one on the vehicle (<see cref="IoModuleSettings"/> binds all eight slots
    /// to one host), so there is no cross-module grouping to do here; a second module would get its own
    /// client and its own group, sent in parallel without cross-device atomicity.
    /// </para>
    /// </remarks>
    public async Task PulseUnlockBatchAsync(IReadOnlyCollection<int> slotIndexes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slotIndexes);
        if (slotIndexes.Count == 0 || slotIndexes.Distinct().Count() != slotIndexes.Count)
        {
            throw new ArgumentException("批量开锁的仓位集合不能为空或重复。", nameof(slotIndexes));
        }

        ushort[] addresses = slotIndexes
            .Select(slotIndex => checked((ushort)(_settings.DoStartAddress + GetMapping(slotIndex).DoChannel)))
            .Order()
            .ToArray();
        foreach ((ushort start, int count) in ContiguousRuns(addresses))
        {
            byte byteCount = checked((byte)((count + 7) / 8));
            byte[] pdu = new byte[6 + byteCount];
            pdu[0] = WriteMultipleCoilsFunction;
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), start);
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), checked((ushort)count));
            pdu[5] = byteCount;
            for (int index = 0; index < count; index++)
            {
                pdu[6 + (index / 8)] |= (byte)(1 << (index % 8));
            }

            byte[] response = await ExecuteRequestAsync(pdu, WriteMultipleCoilsFunction, cancellationToken)
                .ConfigureAwait(false);
            if (!response.AsSpan().SequenceEqual(pdu.AsSpan(0, 5)))
            {
                throw new IOException("FC0F响应与请求不一致，开锁结果未知。");
            }

            _logger.Write(LogSeverity.Information, nameof(ModbusTcpIoModuleClient),
                $"已批量发送开锁脉冲触发，DO地址={start}..{start + count - 1}。硬件负责自动复位。");
        }
    }

    private static IEnumerable<(ushort Start, int Count)> ContiguousRuns(IReadOnlyList<ushort> sortedAddresses)
    {
        int runStart = 0;
        for (int index = 1; index <= sortedAddresses.Count; index++)
        {
            if (index == sortedAddresses.Count || sortedAddresses[index] != sortedAddresses[index - 1] + 1)
            {
                yield return (sortedAddresses[runStart], index - runStart);
                runStart = index;
            }
        }
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

    private async Task<byte[]> ExecuteRequestAsync(byte[] pdu, byte expectedFunction, CancellationToken cancellationToken)
    {
        await _transportLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            NetworkStream stream = _stream ?? throw new IOException("Modbus TCP连接不可用。");
            ushort transactionId = unchecked((ushort)Interlocked.Increment(ref _transactionId));

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
