using SQCD.Agv.Core;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed class FakeIoModuleClient : IIoModuleClient
{
    private readonly LockerSnapshot[] _lockers;
    private readonly object _sync = new();

    public FakeIoModuleClient()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _lockers = Enumerable.Range(0, 8)
            .Select(index => new LockerSnapshot(index, index + 1, false, true, true, now))
            .ToArray();
        CurrentSnapshot = new IoSnapshot(true, _lockers.ToArray(), now);
    }

    public int UnlockCount { get; private set; }

    /// <summary>
    /// Off by default, and then a slot operation is refused outright: the session tests perform none,
    /// and a wait that quietly succeeded would hide one that ran by mistake. On, it plays a
    /// cooperative operator the way the unit tests' simulation does -- the pulse unlocks the slot,
    /// the output resets, and the door closes again over a basket.
    /// </summary>
    public bool SimulateOperatorLoad { get; init; }

    /// <summary>
    /// The locker never reaches the state the executor waits for, the way a door closed without a
    /// basket times out on the real rig: the operation ends UNKNOWN instead of throwing past the
    /// executor.
    /// </summary>
    public bool LockerWaitTimesOut { get; init; }

    public bool IsConnected => true;

    public IoSnapshot CurrentSnapshot { get; private set; }

    public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

    public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged;

    public void SetCargoPresent(int slotIndex, bool present)
    {
        lock (_sync)
        {
            LockerSnapshot locker = _lockers[slotIndex];
            _lockers[slotIndex] = locker with { LightCurtainRaw = !present };
            CurrentSnapshot = CurrentSnapshot with { Lockers = _lockers.ToArray() };
        }
    }

    public Task StartAsync(CancellationToken applicationStopping) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            UnlockCount++;
            if (SimulateOperatorLoad)
            {
                Update(slotIndex, locker => locker with
                {
                    LockFeedbackRaw = false,
                    UnlockOutputRaw = true,
                    ObservedAt = DateTimeOffset.UtcNow
                });
            }
        }

        return Task.CompletedTask;
    }

    public async Task<LockerSnapshot> WaitForLockerAsync(
        int slotIndex,
        Func<LockerSnapshot, bool> predicate,
        TimeSpan timeout,
        TimeSpan stableWindow,
        CancellationToken cancellationToken)
    {
        if (LockerWaitTimesOut)
        {
            throw new TimeoutException("G2 fake: the locker never reached the expected state.");
        }

        if (!SimulateOperatorLoad)
        {
            throw new NotSupportedException("G2会话测试不执行仓位操作。");
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                LockerSnapshot locker = CurrentSnapshot.GetLocker(slotIndex);
                if (predicate(locker))
                {
                    return locker;
                }

                if (locker.LockFeedbackRaw is false && locker.UnlockOutputRaw is true)
                {
                    Update(slotIndex, current => current with
                    {
                        UnlockOutputRaw = false,
                        ObservedAt = DateTimeOffset.UtcNow
                    });
                }
                else if (locker.LockFeedbackRaw is false)
                {
                    Update(slotIndex, current => current with
                    {
                        LockFeedbackRaw = true,
                        LightCurtainRaw = false,
                        ObservedAt = DateTimeOffset.UtcNow
                    });
                }
            }

            await Task.Delay(1, cancellationToken);
        }

        throw new TimeoutException();
    }

    private void Update(int slotIndex, Func<LockerSnapshot, LockerSnapshot> change)
    {
        _lockers[slotIndex] = change(_lockers[slotIndex]);
        CurrentSnapshot = new IoSnapshot(true, _lockers.ToArray(), DateTimeOffset.UtcNow);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
