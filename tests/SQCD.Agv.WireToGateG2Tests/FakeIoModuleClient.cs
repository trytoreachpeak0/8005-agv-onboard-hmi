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
    /// The locker never reaches the state the executor waits for -- not even the lock releasing after
    /// the pulse, which is feedback the vehicle cannot trust: the operation ends UNKNOWN instead of
    /// throwing past the executor.
    /// </summary>
    public bool LockerWaitTimesOut { get; init; }

    /// <summary>
    /// The operator never acts. The pulse opens the slot and lets its own output fall back, and then
    /// the wait for a shut door never finishes -- it ends only when the process that started it goes
    /// away. That is the shape of 8005-agv-program#40: an unlocked slot, no result, and a journal
    /// entry nothing else will ever settle.
    /// </summary>
    public bool OperatorNeverActs { get; init; }

    /// <summary>
    /// With <see cref="SimulateOperatorLoad"/>, how many times the operator shuts the door over an
    /// empty slot before putting the basket in. Each one is a reopen (ADR-cross-0058 decision 1).
    /// </summary>
    public int EmptyClosesBeforeLoad { get; init; }

    private int _emptyClosesDone;

    public bool IsConnected => true;

    /// <summary>
    /// Whether a read is stamped as observed now. The real module polls every few milliseconds, so its
    /// snapshot is never more than that old; this fake stamps one only when a test changes something,
    /// and the executor refuses to operate on a snapshot older than <c>IoSnapshotMaxAge</c>. A test
    /// that spends seconds between actions -- waiting out a <c>DurableAck</c>, say -- would otherwise
    /// fail on staleness that no vehicle would ever see. Off by default: a test that means to prove
    /// what happens when readings stop must keep its snapshot where it left it.
    /// </summary>
    public bool KeepSnapshotFresh { get; init; }

    private IoSnapshot _snapshot = null!;

    public IoSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                if (!KeepSnapshotFresh)
                {
                    return _snapshot;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                return _snapshot with
                {
                    ObservedAt = now,
                    Lockers = _snapshot.Lockers.Select(locker => locker with { ObservedAt = now }).ToArray()
                };
            }
        }

        private set
        {
            lock (_sync)
            {
                _snapshot = value;
            }
        }
    }

    public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

    public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged;

    /// <summary>
    /// Announces the readings as they stand, the way the real Modbus client announces every poll. The
    /// setters above change the readings without raising anything, so the vehicle finds a change only
    /// when something else makes it read; a test that needs the change itself to drive the vehicle --
    /// the safety report that follows a reading, say -- says so here.
    /// </summary>
    public void PublishSnapshot() =>
        SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(CurrentSnapshot));

    public void SetCargoPresent(int slotIndex, bool present)
    {
        lock (_sync)
        {
            LockerSnapshot locker = _lockers[slotIndex];
            _lockers[slotIndex] = locker with { LightCurtainRaw = !present };
            CurrentSnapshot = CurrentSnapshot with { Lockers = _lockers.ToArray() };
        }
    }

    /// <summary>
    /// The slot's lock feedback can no longer be read -- a wire cut, a failed input -- so the slot
    /// reads as unknown until <see cref="CloseDoor"/> gives it readings again.
    /// </summary>
    public void SetUnreadable(int slotIndex)
    {
        lock (_sync)
        {
            Update(slotIndex, locker => locker with
            {
                LockFeedbackRaw = null,
                ObservedAt = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>
    /// What the vehicle finds when it comes back: the operator shut the door at some point while
    /// nothing was running, and whether a basket went in is visible only from the light curtain.
    /// </summary>
    public void CloseDoor(int slotIndex, bool cargo)
    {
        lock (_sync)
        {
            Update(slotIndex, locker => locker with
            {
                LockFeedbackRaw = true,
                LightCurtainRaw = !cargo,
                UnlockOutputRaw = false,
                ObservedAt = DateTimeOffset.UtcNow
            });
        }
    }

    public Task StartAsync(CancellationToken applicationStopping) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            UnlockCount++;
            if (OperatorNeverActs)
            {
                // The pulse is over: the lock has released and the output has already fallen back.
                Update(slotIndex, locker => locker with
                {
                    LockFeedbackRaw = false,
                    UnlockOutputRaw = false,
                    ObservedAt = DateTimeOffset.UtcNow
                });
            }
            else if (SimulateOperatorLoad)
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

        if (OperatorNeverActs)
        {
            // The executor's own timeout still applies, so a wait that is meant to end this way ends
            // by cancellation when the process goes away -- not by running out. Timing out anyway
            // keeps a stuck test reading like the other branches here instead of hanging.
            DateTimeOffset neverActsDeadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < neverActsDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    LockerSnapshot locker = CurrentSnapshot.GetLocker(slotIndex);
                    if (predicate(locker))
                    {
                        return locker;
                    }
                }

                await Task.Delay(1, cancellationToken);
            }

            throw new TimeoutException(
                $"G2 fake: slot {slotIndex + 1} never reached the expected state within {timeout}, "
                + "and OperatorNeverActs means nothing was going to move it.");
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
                    bool loaded = _emptyClosesDone >= EmptyClosesBeforeLoad;
                    _emptyClosesDone += loaded ? 0 : 1;
                    Update(slotIndex, current => current with
                    {
                        LockFeedbackRaw = true,
                        LightCurtainRaw = !loaded,
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
