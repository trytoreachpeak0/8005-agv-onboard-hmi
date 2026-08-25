using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// Compatibility bridge that lets the existing fail-closed OnboardController consume the
/// formal business-state ISlotIoProvider without exposing simulator or raw DI semantics.
/// </summary>
public sealed class SlotIoModuleClientAdapter(
    ISlotIoProvider provider,
    IClock clock,
    TimeSpan pollInterval,
    IAppLogger logger) : IIoModuleClient
{
    private readonly object _sync = new();
    private CancellationTokenSource? _stopping;
    private Task? _polling;
    private IoSnapshot _current = IoSnapshot.Unknown(clock.Now);

    public bool IsConnected => CurrentSnapshot.IsConnected;
    public IoSnapshot CurrentSnapshot { get { lock (_sync) return _current; } }
    public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;
    public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged;

    public Task StartAsync(CancellationToken applicationStopping)
    {
        if (_polling is not null)
        {
            return Task.CompletedTask;
        }
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
        _polling = PollAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _stopping?.Cancel();
        if (_polling is not null)
        {
            try { await _polling.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        Publish(IoSnapshot.Unknown(clock.Now));
    }

    public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken)
    {
        if (slotIndex is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }
        return provider.PulseUnlockAsync([slotIndex + 1], cancellationToken);
    }

    public async Task<LockerSnapshot> WaitForLockerAsync(
        int slotIndex,
        Func<LockerSnapshot, bool> predicate,
        TimeSpan timeout,
        TimeSpan stableWindow,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = clock.Now + timeout;
        DateTimeOffset? satisfiedAt = null;
        while (clock.Now <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LockerSnapshot snapshot = CurrentSnapshot.GetLocker(slotIndex);
            if (predicate(snapshot))
            {
                satisfiedAt ??= clock.Now;
                if (clock.Now - satisfiedAt.Value >= stableWindow)
                {
                    return snapshot;
                }
            }
            else
            {
                satisfiedAt = null;
            }
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"仓位 {slotIndex + 1} 未在限定时间内达到目标状态。");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await provider.DisposeAsync().ConfigureAwait(false);
        _stopping?.Dispose();
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        bool logged = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<SlotIoState> states = await provider.ReadAllAsync(cancellationToken).ConfigureAwait(false);
                Publish(new IoSnapshot(
                    states.Count == 8 && states.All(item => item.Online),
                    states.OrderBy(item => item.PhysicalSlotNumber).Select(ToLegacySnapshot).ToArray(),
                    states.Select(item => item.ObservedAt).DefaultIfEmpty(clock.Now).Max()));
                logged = false;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or IOException)
            {
                Publish(IoSnapshot.Unknown(clock.Now));
                if (!logged)
                {
                    logger.Write(LogSeverity.Warning, nameof(SlotIoModuleClientAdapter),
                        "ISlotIoProvider 不可用，八仓全部投影为 UNKNOWN。", exception);
                    logged = true;
                }
            }
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Publish(IoSnapshot next)
    {
        bool previousConnection;
        lock (_sync)
        {
            previousConnection = _current.IsConnected;
            _current = next;
        }
        if (previousConnection != next.IsConnected)
        {
            ConnectionChanged?.Invoke(this, new ValueChangedEventArgs<bool>(next.IsConnected));
        }
        SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(next));
    }

    private static LockerSnapshot ToLegacySnapshot(SlotIoState state) => new(
        state.PhysicalSlotNumber - 1,
        state.PhysicalSlotNumber,
        state.UnlockOutput switch { UnlockOutputState.Reset => false, UnlockOutputState.Active => true, _ => null },
        state.DoorLock switch { SlotDoorLock.Locked => true, SlotDoorLock.NotLocked => false, _ => null },
        state.Occupancy switch { SlotOccupancy.Empty => true, SlotOccupancy.Occupied => false, _ => null },
        state.ObservedAt);
}
