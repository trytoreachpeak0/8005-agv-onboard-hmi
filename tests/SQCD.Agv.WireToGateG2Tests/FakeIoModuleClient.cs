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

    /// <summary>
    /// The IO plays back a script of the field's readings instead of an operator model (8005-agv-onboard-hmi#385). A pulse
    /// runs the steps <see cref="ScriptPulse"/> gave its number -- or <see cref="DefaultPulseScript"/> -- on a timer of
    /// its own, each step a change to the readings at a delay after the pulse, the way the module's own lines move: output
    /// on, lock released, output fallen back, door shut. A wait polls the readings and holds its predicate for its stable
    /// window, as <c>ModbusTcpIoModuleClient.WaitForLockerAsync</c> does, and times out as it does. Readings are always
    /// fresh, the module being polled every few milliseconds on a vehicle.
    /// </summary>
    public bool ScriptedHardware { get; init; }

    /// <summary>What a pulse with no script of its own does: the lock releases and the output falls back, nothing more.</summary>
    public static IReadOnlyList<ScriptStep> DefaultPulseScript { get; } =
    [
        new(TimeSpan.FromMilliseconds(40), (io, slot) => io.SetLockFeedback(slot, locked: false)),
        new(TimeSpan.FromMilliseconds(600), (io, slot) => io.SetUnlockOutput(slot, energised: false)),
    ];

    /// <summary>One change to the readings, <paramref name="After"/> the pulse that started its script.</summary>
    public sealed record ScriptStep(TimeSpan After, Action<FakeIoModuleClient, int> Apply);

    private readonly Dictionary<int, IReadOnlyList<ScriptStep>> _pulseScripts = [];
    private readonly List<Task> _runningScripts = [];

    /// <summary>
    /// With <see cref="ScriptedHardware"/>, what the slot's readings do after the <paramref name="pulseNumber"/>th pulse of
    /// this module (counted as <see cref="UnlockCount"/>, from 1). The output turns on with the pulse itself; the steps
    /// are what follows.
    /// </summary>
    public void ScriptPulse(int pulseNumber, params ScriptStep[] steps)
    {
        lock (_sync)
        {
            _pulseScripts[pulseNumber] = steps;
        }
    }

    /// <summary>The lock feedback alone changes: released (<c>false</c>) or caught (<c>true</c>).</summary>
    public void SetLockFeedback(int slotIndex, bool locked)
    {
        lock (_sync)
        {
            Update(slotIndex, locker => locker with { LockFeedbackRaw = locked, ObservedAt = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>The unlock output alone changes.</summary>
    public void SetUnlockOutput(int slotIndex, bool energised)
    {
        lock (_sync)
        {
            Update(slotIndex, locker => locker with { UnlockOutputRaw = energised, ObservedAt = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>The door is shut on a released lock: the lock catches, and the light curtain reads what is inside.</summary>
    public void ShutDoor(int slotIndex, bool cargo)
    {
        lock (_sync)
        {
            Update(slotIndex, locker => locker with
            {
                LockFeedbackRaw = true,
                LightCurtainRaw = !cargo,
                ObservedAt = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>Every scripted step has run: the readings are where the scripts leave them.</summary>
    public Task ScriptsFinishedAsync()
    {
        lock (_sync)
        {
            return Task.WhenAll(_runningScripts);
        }
    }

    public bool IsConnected => CurrentSnapshot.IsConnected;

    /// <summary>
    /// Whether a read is stamped as observed now. The real module polls every few milliseconds, so its
    /// snapshot is never more than that old; this fake stamps one only when a test changes something,
    /// and the executor refuses to operate on a snapshot older than <c>IoSnapshotMaxAge</c>. A test
    /// that spends seconds between actions -- waiting out a <c>DurableAck</c>, say -- would otherwise
    /// fail on staleness that no vehicle would ever see. Off by default: a test that means to prove
    /// what happens when readings stop must keep its snapshot where it left it. Turning it off mid-test
    /// is the module going quiet: the readings keep the stamp <see cref="MakeStale"/> gives them.
    /// </summary>
    public bool KeepSnapshotFresh
    {
        get => Volatile.Read(ref _keepSnapshotFresh);
        set => Volatile.Write(ref _keepSnapshotFresh, value);
    }

    private bool _keepSnapshotFresh;

    private IoSnapshot _snapshot = null!;

    public IoSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                if (!KeepSnapshotFresh && !ScriptedHardware)
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
    /// The slot's door stands open with its output reset and its light curtain as it was: unlocked by hand, not by a
    /// pulse this vehicle sent (8005-agv-onboard-hmi#219 review: a door outside the held set left unlocked).
    /// </summary>
    public void LeaveDoorUnlocked(int slotIndex)
    {
        lock (_sync)
        {
            Update(slotIndex, locker => locker with
            {
                LockFeedbackRaw = false,
                UnlockOutputRaw = false,
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
            _heldUntilShut.Remove(slotIndex);
            Update(slotIndex, locker => locker with
            {
                LockFeedbackRaw = true,
                LightCurtainRaw = !cargo,
                UnlockOutputRaw = false,
                ObservedAt = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>
    /// With <see cref="OperatorNeverActs"/>, the operator's next act is told in advance: once the slot has been pulsed
    /// after this call and reads released, the first wait on it that is not yet satisfied finds the door shut as
    /// <see cref="CloseDoor"/> leaves it. The executor's own wait drives the close, so a test thread that wakes late
    /// cannot run the executor past its operation timeout (8005-agv-onboard-hmi#209: an UNKNOWN about 1 run in 20 when
    /// the test closed the door itself after seeing the pulse).
    /// </summary>
    public void CloseDoorAfterNextUnlock(int slotIndex, bool cargo)
    {
        lock (_sync)
        {
            _scheduledCloses.Add((slotIndex, cargo, UnlockCount + 1));
        }
    }

    /// <summary>
    /// As <see cref="CloseDoorAfterNextUnlock"/>, but keyed on the pulse count rather than on the next pulse: the door is
    /// shut by the first wait on the slot once <see cref="UnlockCount"/> has reached <paramref name="unlockCount"/> and the
    /// slot reads released -- also when that pulse has already happened by the time this is called. Several slots can be
    /// told in advance this way, for an operator who empties one slot after another as a vector opens each
    /// (8005-agv-onboard-hmi#323: closing each door after seeing its pulse ran the vector past its operation timeout
    /// whenever the test thread woke late).
    /// </summary>
    public void CloseDoorOnceUnlockedAt(int slotIndex, bool cargo, int unlockCount)
    {
        lock (_sync)
        {
            _scheduledCloses.Add((slotIndex, cargo, unlockCount));
        }
    }

    /// <summary>Whether a scheduled close is still waiting for its pulse.</summary>
    public bool HasScheduledClose
    {
        get
        {
            lock (_sync)
            {
                return _scheduledCloses.Count > 0;
            }
        }
    }

    private readonly List<(int SlotIndex, bool Cargo, int AtUnlockCount)> _scheduledCloses = [];

    /// <summary>
    /// With <see cref="OperatorNeverActs"/>, the operator will shut this slot's door at a moment the test chooses: until
    /// <see cref="CloseDoor"/> is called for the slot, a wait on it does not run out at its timeout but lasts until the
    /// slot reaches the state it waits for, or the wait is cancelled. For a door a recovery vector takes over standing open
    /// -- no pulse of its own to key a <see cref="CloseDoorOnceUnlockedAt"/> on, and the aborted load's own waits on the
    /// same slot still running when the close would have to be told -- and for a test that must act while the vector waits
    /// on it (8005-agv-onboard-hmi#331: shut by the test thread after it saw the vector start, a late wake-up ran the
    /// vector's wait past its two-second operation timeout and the slot did not come out COMPLETED -- the cancellation
    /// ended ALL_EMPTY_DOOR_UNPROVEN over an emptied handed-over door, UNKNOWN where a slot was left unfinished). Told just
    /// before the press, the hold also covers the load's own wait on the slot until the cancellation aborts it: the load
    /// does not prompt again in that interval.
    /// </summary>
    /// <remarks>
    /// Only <see cref="OperatorNeverActs"/> reads the hold; on any other fake it is silently without effect. It is lifted
    /// only by <see cref="CloseDoor"/> -- also the one a scheduled close (<see cref="CloseDoorOnceUnlockedAt"/>) makes --
    /// never by <see cref="SetUnreadable"/> or <see cref="OpenDoor"/>: a test whose vector must run its wait out to its
    /// timeout must not hold that slot. Nor may a test hold a slot it then stops the vehicle over with the door still open:
    /// stopping the business service waits for the vector, and the vector's wait on a held slot is not ended by the stop
    /// (8005-agv-onboard-hmi#331 review H1: the stop never returned).
    /// </remarks>
    public void HoldWaitsUntilShut(int slotIndex)
    {
        lock (_sync)
        {
            _heldUntilShut.Add(slotIndex);
        }
    }

    private readonly HashSet<int> _heldUntilShut = [];

    /// <summary>
    /// The door reads open -- lock feedback released, unlock output reset -- as a door left standing open
    /// after a vector that could not confirm it reads (8005-agv-onboard-hmi#255).
    /// </summary>
    public void OpenDoor(int slotIndex)
    {
        lock (_sync)
        {
            Update(slotIndex, locker => locker with
            {
                LockFeedbackRaw = false,
                UnlockOutputRaw = false,
                ObservedAt = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>
    /// The door reads shut and locked, but the unlock output still reads energised: a lock that is not proven locked.
    /// </summary>
    public void SetUnlockOutputActive(int slotIndex)
    {
        lock (_sync)
        {
            Update(slotIndex, locker => locker with
            {
                LockFeedbackRaw = true,
                UnlockOutputRaw = true,
                ObservedAt = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>
    /// The readings stay as they are and stop being refreshed: the snapshot is stamped <paramref name="age"/> in the past,
    /// as when the module has not been polled for that long. Any later change stamps it fresh again.
    /// </summary>
    public void MakeStale(TimeSpan age)
    {
        lock (_sync)
        {
            DateTimeOffset then = DateTimeOffset.UtcNow - age;
            CurrentSnapshot = new IoSnapshot(
                true,
                _lockers.Select(locker => locker with { ObservedAt = then }).ToArray(),
                then);
        }
    }

    /// <summary>
    /// The module stops answering the way the real client reports it: a fresh snapshot that is not connected, every slot
    /// unknown (<c>IoSnapshot.Unknown</c>, as <c>ModbusTcpIoModuleClient</c> publishes after a failed read). Nothing here
    /// brings it back.
    /// </summary>
    public void FailReads()
    {
        lock (_sync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (int index = 0; index < _lockers.Length; index++)
            {
                _lockers[index] = LockerSnapshot.Unknown(index, now);
            }

            CurrentSnapshot = IoSnapshot.Unknown(now);
        }
    }

    public Task StartAsync(CancellationToken applicationStopping) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            UnlockCount++;
            if (ScriptedHardware)
            {
                Update(slotIndex, locker => locker with { UnlockOutputRaw = true, ObservedAt = DateTimeOffset.UtcNow });
                IReadOnlyList<ScriptStep> steps = _pulseScripts.TryGetValue(UnlockCount, out IReadOnlyList<ScriptStep>? script)
                    ? script
                    : DefaultPulseScript;
                _runningScripts.Add(RunScriptAsync(slotIndex, steps));
            }
            else if (OperatorNeverActs)
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

        if (ScriptedHardware)
        {
            return await WaitLikeTheModuleAsync(slotIndex, predicate, timeout, stableWindow, cancellationToken);
        }

        if (OperatorNeverActs)
        {
            // The executor's own timeout still applies, so a wait that is meant to end this way ends
            // by cancellation when the process goes away -- not by running out. Timing out anyway
            // keeps a stuck test reading like the other branches here instead of hanging.
            // The deadline is checked after the reading, under the same lock: a door shut by the test, which lifts a
            // HoldWaitsUntilShut, is always seen before the wait gives up on it.
            DateTimeOffset neverActsDeadline = DateTimeOffset.UtcNow + timeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    LockerSnapshot locker = CurrentSnapshot.GetLocker(slotIndex);
                    if (predicate(locker))
                    {
                        return locker;
                    }

                    int due = _scheduledCloses.FindIndex(close =>
                        close.SlotIndex == slotIndex && UnlockCount >= close.AtUnlockCount);
                    if (due >= 0 && _lockers[slotIndex].LockFeedbackRaw == false)
                    {
                        bool cargo = _scheduledCloses[due].Cargo;
                        _scheduledCloses.RemoveAt(due);
                        CloseDoor(slotIndex, cargo);
                        continue;
                    }

                    if (DateTimeOffset.UtcNow >= neverActsDeadline && !_heldUntilShut.Contains(slotIndex))
                    {
                        break;
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

    private async Task RunScriptAsync(int slotIndex, IReadOnlyList<ScriptStep> steps)
    {
        // Timed from the pulse, not from the step before: a late wake-up delays one step, not every one after it.
        System.Diagnostics.Stopwatch sincePulse = System.Diagnostics.Stopwatch.StartNew();
        foreach (ScriptStep step in steps)
        {
            TimeSpan remaining = step.After - sincePulse.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining).ConfigureAwait(false);
            }

            step.Apply(this, slotIndex);
        }
    }

    /// <summary>The algorithm of <c>ModbusTcpIoModuleClient.WaitForLockerAsync</c>, on the readings as they stand.</summary>
    private async Task<LockerSnapshot> WaitLikeTheModuleAsync(
        int slotIndex,
        Func<LockerSnapshot, bool> predicate,
        TimeSpan timeout,
        TimeSpan stableWindow,
        CancellationToken cancellationToken)
    {
        System.Diagnostics.Stopwatch started = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? stableSince = null;
        while (started.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IoSnapshot snapshot = CurrentSnapshot;
            if (!snapshot.IsConnected)
            {
                throw new IOException("G2 fake: the IO connection dropped during a wait.");
            }

            LockerSnapshot locker = snapshot.GetLocker(slotIndex) with { ObservedAt = DateTimeOffset.UtcNow };
            if (predicate(locker))
            {
                stableSince ??= started.Elapsed;
                if (stableWindow <= TimeSpan.Zero || started.Elapsed - stableSince.Value >= stableWindow)
                {
                    return locker;
                }
            }
            else
            {
                stableSince = null;
            }

            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"G2 fake: slot {slotIndex + 1} did not hold the expected state within {timeout}.");
    }

    private void Update(int slotIndex, Func<LockerSnapshot, LockerSnapshot> change)
    {
        _lockers[slotIndex] = change(_lockers[slotIndex]);
        CurrentSnapshot = new IoSnapshot(true, _lockers.ToArray(), DateTimeOffset.UtcNow);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
