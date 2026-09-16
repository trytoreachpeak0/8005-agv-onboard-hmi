using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

public sealed class WireToGateSlotOperationExecutorTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-CONNECTION-LOSS-SAFE-FINISH")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task LoadUsesOnlyServerFrozenSlotsAndJournalsBeforePulses()
    {
        string directory = Path.Combine(Path.GetTempPath(), "w2g-executor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string journalPath = Path.Combine(directory, "journal.db");
        SimulationIo io = new();
        await using SqliteWireToGateJournal journal = new(journalPath);
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        WireToGateSlotOperationExecutor executor = new(
            io,
            journal,
            new SystemClock(),
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(1)),
            () => true);
        await using (executor)
        {
            WireToGateSlotOperationCommand command = new(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                1,
                DateTimeOffset.UtcNow,
                "11111111-1111-1111-1111-111111111111",
                "22222222-2222-2222-2222-222222222222",
                "33333333-3333-3333-3333-333333333333",
                OperationType.Load,
                [1, 2],
                2,
                true,
                new string('0', 64));

            WireToGateOperationExecutionResult result = await executor.ExecuteAsync(
                command,
                null,
                TestContext.Current.CancellationToken);

            Assert.Equal("COMPLETED", result.OverallOutcome);
            Assert.Equal([1, 2], result.SlotResults.Select(slot => slot.SlotNo));
            Assert.All(result.SlotResults, slot => Assert.Equal("COMPLETED", slot.Outcome));
            Assert.Equal(2, io.UnlockCount);
            WireToGateRecoveryState checkpoint = await journal.ReadRecoveryStateAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, checkpoint.ProvenRecoveryCheckpoint);
            Assert.Equal(command.SlotOperationAttemptId, checkpoint.UnsettledSlotOperationAttemptId);

            await executor.MarkResultRecordedAsync(
                command.SlotOperationAttemptId,
                TestContext.Current.CancellationToken);
            checkpoint = await journal.ReadRecoveryStateAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, checkpoint.ProvenRecoveryCheckpoint);
            Assert.Null(checkpoint.UnsettledSlotOperationAttemptId);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("ProtocolVector", "CV-DESTINATION-UNLOAD-ALL-EMPTY")]
    public async Task UnloadAllTargetSlotsRequiresEverySlotToReachEmpty()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            initialCargo: true,
            finalCargo: false,
            cancellationToken: TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Unload,
            [1, 2, 3, 4, 5, 6, 7, 8],
            expectedOccupied: false);

        WireToGateOperationExecutionResult result =
            await fixture.Executor.ExecuteAsync(
                command,
                null,
                TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(8, fixture.Io.UnlockCount);
        Assert.All(result.SlotResults, slot =>
        {
            Assert.Equal("EMPTY", slot.FinalPhysicalState);
            Assert.Equal("LOCKED", slot.LockState);
            Assert.Equal("RESET", slot.UnlockOutputState);
        });
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task UnknownSnapshotFailsClosedWithoutUnlock()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        fixture.Io.SetUnknown();
        WireToGateOperationExecutionResult result =
            await fixture.Executor.ExecuteAsync(
                CreateCommand(OperationType.Load, [1], expectedOccupied: true),
                null,
                TestContext.Current.CancellationToken);

        Assert.Equal("FAILED", result.OverallOutcome);
        Assert.Equal(0, fixture.Io.UnlockCount);
        Assert.Contains("SLOT_STATE_UNKNOWN", result.SlotResults.Single().ReasonCodes);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ResumeSkipsSlotsAlreadyAtDesiredFinalState()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            initialCargo: false,
            finalCargo: true,
            cancellationToken: TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Load,
            [1, 2],
            expectedOccupied: true);
        WireToGateOperationExecutionResult first = await fixture.Executor.ExecuteAsync(
            command,
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal("COMPLETED", first.OverallOutcome);
        int unlocksBeforeResume = fixture.Io.UnlockCount;

        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        await fixture.Journal.WriteRecoveryStateAsync(state with
        {
            ExceptionRecoverySessionId = "44444444-4444-4444-8444-444444444444",
            RecoveryActionId = "55555555-5555-4555-8555-555555555555"
        }, TestContext.Current.CancellationToken);
        WireToGateSlotOperationResumeCommand resume = new(
            "66666666-6666-4666-8666-666666666666",
            2,
            DateTimeOffset.UtcNow,
            "44444444-4444-4444-8444-444444444444",
            "55555555-5555-4555-8555-555555555555",
            command.DemandId,
            command.SlotOperationAttemptId,
            WireToGateRecoveryCheckpoint.SafeFinishReached,
            command.Slots,
            command.CommandContentSha256);

        WireToGateOperationExecutionResult resumed = await fixture.Executor.ResumeAsync(
            resume,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", resumed.OverallOutcome);
        Assert.Equal(unlocksBeforeResume, fixture.Io.UnlockCount);
        Assert.All(resumed.SlotResults, slot => Assert.Equal("COMPLETED", slot.Outcome));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ResumeWithoutOriginalContextFailsClosed()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        WireToGateSlotOperationResumeCommand resume = new(
            "66666666-6666-4666-8666-666666666666",
            2,
            DateTimeOffset.UtcNow,
            "44444444-4444-4444-8444-444444444444",
            "55555555-5555-4555-8555-555555555555",
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            WireToGateRecoveryCheckpoint.Prepared,
            [1],
            new string('0', 64));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Executor.ResumeAsync(
                resume,
                null,
                TestContext.Current.CancellationToken));

        Assert.Equal("RECOVERY_OPERATION_CONTEXT_MISSING", error.Message);
        Assert.Equal(0, fixture.Io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ResumeRejectsDifferentCommandHashWithoutUnlock()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            initialCargo: false,
            finalCargo: true,
            cancellationToken: TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Load,
            [1],
            expectedOccupied: true);
        WireToGateOperationExecutionResult first = await fixture.Executor.ExecuteAsync(
            command,
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal("COMPLETED", first.OverallOutcome);

        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        await fixture.Journal.WriteRecoveryStateAsync(state with
        {
            ExceptionRecoverySessionId = "44444444-4444-4444-8444-444444444444",
            RecoveryActionId = "55555555-5555-4555-8555-555555555555"
        }, TestContext.Current.CancellationToken);
        WireToGateSlotOperationResumeCommand resume = new(
            "66666666-6666-4666-8666-666666666666",
            2,
            DateTimeOffset.UtcNow,
            "44444444-4444-4444-8444-444444444444",
            "55555555-5555-4555-8555-555555555555",
            command.DemandId,
            command.SlotOperationAttemptId,
            WireToGateRecoveryCheckpoint.SafeFinishReached,
            command.Slots,
            new string('1', 64));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Executor.ResumeAsync(
                resume,
                null,
                TestContext.Current.CancellationToken));

        Assert.Equal("RECOVERY_STATE_MISMATCH", error.Message);
        Assert.Equal(1, fixture.Io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnOperationInterruptedWhileWaitingSettlesAsUnknownWithRealReadingsAndNoPulse()
    {
        // The field window in 8005-agv-program#40: the unlock pulse went out, the process died while
        // waiting for the operator, and the operator then shut the door without loading anything.
        // A result has to leave the vehicle or both ends wait for each other, but no second unlock
        // may go out (ADR-cross-0017) and readable physical fields are never reported as unknown
        // (ADR-cross-0058 decision 6).
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Load,
            [1, 2],
            expectedOccupied: true);
        await InterruptWhileWaitingAsync(fixture, command);
        fixture.Io.CloseDoor(0, cargo: false);

        WireToGateOperationExecutionResult result = await fixture.Executor.SettleInterruptedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal(command.SlotOperationAttemptId, result.SlotOperationAttemptId);
        WireToGateSlotExecutionResult interrupted = result.SlotResults.Single(slot => slot.SlotNo == 1);
        Assert.Equal("UNKNOWN", interrupted.Outcome);
        Assert.Equal(["RECOVERY_CHECKPOINT_NOT_UNIQUE"], interrupted.ReasonCodes);
        Assert.Equal("EMPTY", interrupted.FinalPhysicalState);
        Assert.Equal("LOCKED", interrupted.LockState);
        Assert.Equal("RESET", interrupted.UnlockOutputState);
        WireToGateSlotExecutionResult neverStarted = result.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal("NOT_STARTED", neverStarted.Outcome);
        Assert.Empty(neverStarted.ReasonCodes);
        Assert.Equal("EMPTY", neverStarted.FinalPhysicalState);
        Assert.Equal("SAFE_FINISH_REACHED", result.JournalCheckpoint);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Equal(0, fixture.Io.UnlockCount(1));

        // Same journal shape as an UNKNOWN decided mid-execution: the compensation and recovery
        // vectors recognise this very attempt and its context.
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(command.SlotOperationAttemptId, state.UnsettledSlotOperationAttemptId);
        Assert.NotNull(state.OperationContext);
        Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, state.ProvenRecoveryCheckpoint);
        Assert.Empty(state.ActiveUnlockSlots);
    }

    /// <summary>
    /// Leaves an operation parked at "unlocked, waiting for the operator" and then kills it the way a
    /// dying process does: no result is written, and the journal keeps only the unsettled attempt and
    /// its active unlock set.
    /// </summary>
    private static async Task InterruptWhileWaitingAsync(
        ScriptedFixture fixture,
        WireToGateSlotOperationCommand command)
    {
        using CancellationTokenSource interrupt = new();
        Task<WireToGateOperationExecutionResult> operation = fixture.Executor.ExecuteAsync(
            command,
            (progress, token) =>
            {
                if (progress.Phase == "WAITING_OPERATOR")
                {
                    interrupt.Cancel();
                }

                return Task.CompletedTask;
            },
            interrupt.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    private sealed class ScriptedFixture : IAsyncDisposable
    {
        private ScriptedFixture(
            ScriptedIo io,
            SqliteWireToGateJournal journal,
            WireToGateSlotOperationExecutor executor,
            SafetyGate gate)
        {
            Io = io;
            Journal = journal;
            Executor = executor;
            Gate = gate;
        }

        public ScriptedIo Io { get; }

        /// <summary>The vehicle safety fact the executor asks before a reopen pulse.</summary>
        public SafetyGate Gate { get; }

        public SqliteWireToGateJournal Journal { get; }

        public WireToGateSlotOperationExecutor Executor { get; }

        public static async Task<ScriptedFixture> CreateAsync(
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null,
            TimeSpan? unlockOutputResetTimeout = null)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "w2g-executor",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
            await journal.InitializeAsync(cancellationToken);
            ScriptedIo io = new();
            SafetyGate gate = new();
            WireToGateSlotOperationExecutor executor = new(
                io,
                journal,
                new SystemClock(),
                new WireToGateSlotOperationExecutorOptions(
                    TimeSpan.FromSeconds(1),
                    unlockOutputResetTimeout ?? TimeSpan.FromSeconds(1),
                    operationTimeout ?? TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromSeconds(30)),
                () => gate.Permitted);
            return new ScriptedFixture(io, journal, executor, gate);
        }

        public async ValueTask DisposeAsync()
        {
            await Executor.DisposeAsync();
            await Journal.DisposeAsync();
        }
    }

    private sealed class SafetyGate
    {
        private volatile bool _permitted = true;

        public bool Permitted
        {
            get => _permitted;
            set => _permitted = value;
        }
    }

    /// <summary>
    /// An IO module nothing advances on its own.  The unlock pulse opens its slot and resets its own
    /// output, exactly as the hardware does; everything after that -- the operator shutting a door,
    /// an output that stays energised, the bus going away -- is driven by the test, because these
    /// cases are about what the vehicle reads at a moment it did not choose.
    /// </summary>
    private sealed class ScriptedIo : IIoModuleClient
    {
        private readonly LockerSnapshot[] _lockers;
        private readonly int[] _unlockCounts = new int[8];
        private readonly HashSet<int> _jammed = [];
        private readonly HashSet<int> _stuckOutputs = [];
        private readonly object _sync = new();
        private bool _connected = true;

        public ScriptedIo()
        {
            _lockers = Enumerable.Range(0, 8)
                .Select(index => new LockerSnapshot(
                    index,
                    index + 1,
                    false,
                    true,
                    true,
                    DateTimeOffset.UtcNow))
                .ToArray();
            CurrentSnapshot = new IoSnapshot(true, _lockers.ToArray(), DateTimeOffset.UtcNow);
            _ = ConnectionChanged;
        }

        public bool IsConnected => _connected;

        public IoSnapshot CurrentSnapshot { get; private set; }

        public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

        public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged;

        public Task StartAsync(CancellationToken applicationStopping) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public int UnlockCount(int slotIndex)
        {
            lock (_sync)
            {
                return _unlockCounts[slotIndex];
            }
        }

        public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _unlockCounts[slotIndex]++;
                // The pulse is over by the time it returns: the lock has released and the output has
                // already fallen back, which is what the executor waits for next -- unless the test
                // jammed the lock or welded the output.
                bool jammed = _jammed.Contains(slotIndex);
                bool stuck = _stuckOutputs.Contains(slotIndex);
                Update(slotIndex, locker => locker with
                {
                    LockFeedbackRaw = jammed,
                    UnlockOutputRaw = stuck,
                    ObservedAt = DateTimeOffset.UtcNow
                });
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
                }

                await Task.Delay(1, cancellationToken);
            }

            throw new TimeoutException();
        }

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

        /// <summary>
        /// The door is shut and the lock closed, but the unlock coil never fell back -- a welded
        /// contact or a stuck output. Every other reading is clean, which is what makes this the
        /// case that tells the two completion predicates apart.
        /// </summary>
        public void CloseDoorWithUnlockOutputStuckActive(int slotIndex, bool cargo)
        {
            lock (_sync)
            {
                Update(slotIndex, locker => locker with
                {
                    LockFeedbackRaw = true,
                    LightCurtainRaw = !cargo,
                    UnlockOutputRaw = true,
                    ObservedAt = DateTimeOffset.UtcNow
                });
            }
        }

        /// <summary>The lock does not release on the next pulses: its feedback never confirms one.</summary>
        public void JamLock(int slotIndex)
        {
            lock (_sync)
            {
                _jammed.Add(slotIndex);
            }
        }

        public void UnjamLock(int slotIndex)
        {
            lock (_sync)
            {
                _jammed.Remove(slotIndex);
            }
        }

        /// <summary>The unlock output stays energised after the next pulses.</summary>
        public void StickUnlockOutput(int slotIndex)
        {
            lock (_sync)
            {
                _stuckOutputs.Add(slotIndex);
            }
        }

        /// <summary>The unlock output falls back, late.</summary>
        public void ReleaseUnlockOutput(int slotIndex)
        {
            lock (_sync)
            {
                Update(slotIndex, locker => locker with
                {
                    UnlockOutputRaw = false,
                    ObservedAt = DateTimeOffset.UtcNow
                });
            }
        }

        /// <summary>The light curtain input stops reading while the bus stays up.</summary>
        public void LoseOccupancyReading(int slotIndex)
        {
            lock (_sync)
            {
                Update(slotIndex, locker => locker with
                {
                    LightCurtainRaw = null,
                    ObservedAt = DateTimeOffset.UtcNow
                });
            }
        }

        /// <summary>The lock feedback input stops reading while the bus stays up.</summary>
        public void LoseLockFeedback(int slotIndex)
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

        public void Disconnect()
        {
            lock (_sync)
            {
                _connected = false;
                CurrentSnapshot = new IoSnapshot(
                    false,
                    Enumerable.Range(0, 8)
                        .Select(index => LockerSnapshot.Unknown(index, DateTimeOffset.UtcNow))
                        .ToArray(),
                    DateTimeOffset.UtcNow);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Update(int slotIndex, Func<LockerSnapshot, LockerSnapshot> update)
        {
            _lockers[slotIndex] = update(_lockers[slotIndex]);
            CurrentSnapshot = new IoSnapshot(_connected, _lockers.ToArray(), DateTimeOffset.UtcNow);
            SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(CurrentSnapshot));
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnOperationTheOperatorFinishedAfterTheProcessDiedSettlesAsCompleted()
    {
        // The operator put the cargo in and shut the door after the process died. The slot's final
        // state reads clean and the journal says it is the one that was opened, which is the only
        // explanation available -- reporting UNKNOWN would turn a settled result into an unsettled
        // one and a perfectly good load would be compensated away.
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Load,
            [1],
            expectedOccupied: true);
        await InterruptWhileWaitingAsync(fixture, command);
        fixture.Io.CloseDoor(0, cargo: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.SettleInterruptedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = result.SlotResults.Single();
        Assert.Equal("COMPLETED", slot.Outcome);
        Assert.Empty(slot.ReasonCodes);
        Assert.Equal("OCCUPIED", slot.FinalPhysicalState);
        Assert.Equal("LOCKED", slot.LockState);
        Assert.Equal("RESET", slot.UnlockOutputState);
        Assert.Equal("SAFE_FINISH_REACHED", result.JournalCheckpoint);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnInterruptedSlotWhoseUnlockOutputIsStuckActiveIsUnknownNotCompleted()
    {
        // The door is shut, the lock is closed and the cargo is in, but the unlock coil still reads
        // energised. "Unlock output cannot be confirmed reset" is precisely the condition that sends
        // an operation into recovery (ADR-cross-0058 decision 2), and the settled-state predicate is
        // the same one the executor's other paths use (onboard-hmi#49) -- so this is UNKNOWN, and a
        // COMPLETED here would let the server judge RecoveryRequired against a vehicle that just
        // cleared its own context.
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Load,
            [1],
            expectedOccupied: true);
        await InterruptWhileWaitingAsync(fixture, command);
        fixture.Io.CloseDoorWithUnlockOutputStuckActive(0, cargo: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.SettleInterruptedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = result.SlotResults.Single();
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal(["RECOVERY_CHECKPOINT_NOT_UNIQUE"], slot.ReasonCodes);
        Assert.Equal("OCCUPIED", slot.FinalPhysicalState);
        Assert.Equal("LOCKED", slot.LockState);
        Assert.Equal("ACTIVE", slot.UnlockOutputState);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task EverySlotOfACompletedInterruptedSettlementMeetsTheServerCompletionCondition()
    {
        // The invariant onboard-hmi#49 pins down: an overall COMPLETED means every slot in the
        // command is COMPLETED, at its expected occupancy, LOCKED and RESET. That is what the server
        // checks before it accepts the result, so any path that can say COMPLETED has to meet it.
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Unload,
            [1, 2],
            expectedOccupied: false);
        fixture.Io.CloseDoor(0, cargo: true);
        fixture.Io.CloseDoor(1, cargo: true);
        await InterruptAfterFirstSlotAsync(fixture, command);
        fixture.Io.CloseDoor(0, cargo: false);
        fixture.Io.CloseDoor(1, cargo: false);

        WireToGateOperationExecutionResult result = await fixture.Executor.SettleInterruptedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(command.Slots, result.SlotResults.Select(slot => slot.SlotNo).ToArray());
        Assert.All(result.SlotResults, slot =>
        {
            Assert.Equal("COMPLETED", slot.Outcome);
            Assert.Equal("EMPTY", slot.FinalPhysicalState);
            Assert.Equal("LOCKED", slot.LockState);
            Assert.Equal("RESET", slot.UnlockOutputState);
            Assert.Empty(slot.ReasonCodes);
        });
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnInterruptedOperationWithOnlyOneOfItsSlotsDoneIsNotCompleted()
    {
        // The first slot is loaded, the second was never opened. Opening it needs the server's
        // authorisation (ADR-cross-0017), so this is not a completion however known every reading is.
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Load,
            [1, 2],
            expectedOccupied: true);
        await InterruptWhileWaitingAsync(fixture, command);
        fixture.Io.CloseDoor(0, cargo: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.SettleInterruptedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal("COMPLETED", result.SlotResults.Single(slot => slot.SlotNo == 1).Outcome);
        Assert.Equal("NOT_STARTED", result.SlotResults.Single(slot => slot.SlotNo == 2).Outcome);
        Assert.Equal(0, fixture.Io.UnlockCount(1));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnInterruptedSlotWhoseDoorIsStillOpenStaysInTheActiveSet()
    {
        // The door is still open: this is not a safe finish, so the checkpoint stays at
        // ACTIVE_UNLOCK_SET and the slot stays in the unlock set. A compensation vector does not
        // drive an open door; maintenance has to shut it first.
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Load,
            [1],
            expectedOccupied: true);
        await InterruptWhileWaitingAsync(fixture, command);

        WireToGateOperationExecutionResult result = await fixture.Executor.SettleInterruptedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = result.SlotResults.Single();
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal("UNLOCKED", slot.LockState);
        Assert.Equal("ACTIVE_UNLOCK_SET", result.JournalCheckpoint);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal([1], state.ActiveUnlockSlots);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnInterruptedOperationSettledWithoutIoSaysItCannotReadTheSlots()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Load,
            [1],
            expectedOccupied: true);
        await InterruptWhileWaitingAsync(fixture, command);
        fixture.Io.Disconnect();

        WireToGateOperationExecutionResult result = await fixture.Executor.SettleInterruptedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = result.SlotResults.Single();
        Assert.Equal(["SLOT_STATE_UNKNOWN"], slot.ReasonCodes);
        Assert.Equal("UNKNOWN", slot.FinalPhysicalState);
        Assert.Equal("ACTIVE_UNLOCK_SET", result.JournalCheckpoint);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task SettlingWithNothingUnsettledFailsClosed()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Executor.SettleInterruptedAsync(TestContext.Current.CancellationToken));

        Assert.Equal("RECOVERY_OPERATION_CONTEXT_MISSING", error.Message);
        Assert.Equal(0, fixture.Io.UnlockCount(0));
    }

    /// <summary>
    /// Finishes the first slot of a multi-slot command and then kills the operation while the second
    /// one is unlocked and waiting, so the journal carries both a completed slot and an active one.
    /// </summary>
    private static async Task InterruptAfterFirstSlotAsync(
        ScriptedFixture fixture,
        WireToGateSlotOperationCommand command)
    {
        using CancellationTokenSource interrupt = new();
        Task<WireToGateOperationExecutionResult> operation = fixture.Executor.ExecuteAsync(
            command,
            (progress, token) =>
            {
                if (progress.Phase == "WAITING_OPERATOR")
                {
                    if (progress.Completed.Count == 0)
                    {
                        fixture.Io.CloseDoor(progress.Active.Single() - 1, command.ExpectedOccupied);
                    }
                    else
                    {
                        interrupt.Cancel();
                    }
                }

                return Task.CompletedTask;
            },
            interrupt.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ALoadDoorShutEmptyIsReopenedEveryRoundUntilTheBasketIsIn()
    {
        // ADR-cross-0058 decision 1: a door shut over an empty slot is not a failure and not
        // recovery. Three empty rounds before the basket goes in -- more than two, because having no
        // limit is the point (ADR-cross-0040).
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        List<(string Phase, int PromptRound)> phases = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (progress, token) =>
            {
                phases.Add((progress.Phase, progress.PromptRound));
                if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(progress.Active.Single() - 1, cargo: progress.PromptRound >= 3);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults);
        Assert.Equal("COMPLETED", slot.Outcome);
        Assert.Empty(slot.ReasonCodes);
        Assert.Equal(4, fixture.Io.UnlockCount(0));
        Assert.Equal(
            [
                ("UNLOCKING", 0), ("WAITING_OPERATOR", 0),
                ("UNLOCKING", 1), ("WAITING_OPERATOR", 1),
                ("UNLOCKING", 2), ("WAITING_OPERATOR", 2),
                ("UNLOCKING", 3), ("WAITING_OPERATOR", 3)
            ],
            phases.Where(item => item.Phase is "UNLOCKING" or "WAITING_OPERATOR").ToArray());
        Assert.DoesNotContain(phases, item => item.Phase == "PAUSED");
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, state.ProvenRecoveryCheckpoint);
        Assert.All(state.SlotResults, item => Assert.Equal("COMPLETED", item.Outcome));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("ProtocolVector", "CV-DESTINATION-UNLOAD-ALL-EMPTY")]
    public async Task AnUnloadDoorShutOverTheBasketIsReopenedUntilTheSlotIsEmpty()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        fixture.Io.CloseDoor(0, cargo: true);
        List<string> phases = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Unload, [1], expectedOccupied: false),
            (progress, token) =>
            {
                phases.Add(progress.Phase);
                if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(progress.Active.Single() - 1, cargo: progress.PromptRound < 2);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults);
        Assert.Equal("COMPLETED", slot.Outcome);
        Assert.Equal("EMPTY", slot.FinalPhysicalState);
        Assert.Equal(3, fixture.Io.UnlockCount(0));
        Assert.DoesNotContain("PAUSED", phases);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ASlotAlreadyLoadedIsNotReopenedWhileAnotherKeepsBeingReopened()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Load,
            [1, 2, 3],
            expectedOccupied: true);
        List<int> reopenedSlots = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, token) =>
            {
                if (progress.Phase == "UNLOCKING" && progress.PromptRound > 0)
                {
                    reopenedSlots.Add(progress.Active.Single());
                }

                if (progress.Phase == "WAITING_OPERATOR")
                {
                    // 1 and 3 are loaded the first time; 2 is shut empty three times.
                    int physicalSlot = progress.Active.Single();
                    fixture.Io.CloseDoor(physicalSlot - 1, cargo: physicalSlot != 2 || progress.PromptRound >= 3);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        AssertCompletedMeetsServerCompletionCondition(command, result);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Equal(4, fixture.Io.UnlockCount(1));
        Assert.Equal(1, fixture.Io.UnlockCount(2));
        Assert.Equal([2, 2, 2], reopenedSlots);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task OperationTimeoutOnlyPromptsAgainWithoutAPulseOrAnEnd()
    {
        // Decision 3: the door stays open past OperationTimeout. That is one more prompt -- no second
        // pulse to a lock that is already open, no result, no recovery -- and the command goes on
        // waiting until the door is shut.
        TimeSpan cadence = TimeSpan.FromMilliseconds(300);
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken,
            operationTimeout: cadence);
        List<(string Phase, int PromptRound)> phases = [];
        DateTimeOffset firstPrompt = default;
        DateTimeOffset secondPrompt = default;

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (progress, token) =>
            {
                phases.Add((progress.Phase, progress.PromptRound));
                if (progress.Phase == "WAITING_OPERATOR" && progress.PromptRound == 0)
                {
                    firstPrompt = DateTimeOffset.UtcNow;
                }
                else if (progress.Phase == "WAITING_OPERATOR" && progress.PromptRound == 1)
                {
                    secondPrompt = DateTimeOffset.UtcNow;
                    fixture.Io.CloseDoor(progress.Active.Single() - 1, cargo: true);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Equal(
            [("UNLOCKING", 0), ("WAITING_OPERATOR", 0), ("WAITING_OPERATOR", 1)],
            phases.Where(item => item.Phase is "UNLOCKING" or "WAITING_OPERATOR").ToArray());
        Assert.True(
            secondPrompt - firstPrompt >= cadence - TimeSpan.FromMilliseconds(50),
            $"the second prompt came {(secondPrompt - firstPrompt).TotalMilliseconds} ms after the first");
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnOccupancyReadingLostWhileWaitingIsUnknown()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (progress, token) =>
            {
                if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.LoseOccupancyReading(progress.Active.Single() - 1);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults);
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal(["SLOT_STATE_UNKNOWN"], slot.ReasonCodes);
        Assert.Equal("UNKNOWN", slot.FinalPhysicalState);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ALockFeedbackLostWhileWaitingIsUnknown()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (progress, token) =>
            {
                if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.LoseLockFeedback(progress.Active.Single() - 1);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults);
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal(["SLOT_STATE_UNKNOWN"], slot.ReasonCodes);
        Assert.Equal("UNKNOWN", slot.LockState);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnUnlockOutputThatDoesNotResetAfterThePulseIsUnknownAndNotReopened()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken,
            unlockOutputResetTimeout: TimeSpan.FromMilliseconds(200));
        fixture.Io.StickUnlockOutput(0);
        List<string> phases = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (progress, token) =>
            {
                phases.Add(progress.Phase);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults);
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal("ACTIVE", slot.UnlockOutputState);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.DoesNotContain("WAITING_OPERATOR", phases);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ADoorShutOverTheBasketWithTheUnlockOutputStuckActiveIsUnknownNotCompleted()
    {
        // onboard-hmi#49: the target state includes the unlock output having fallen back. Locked and
        // loaded with the output still energised gets UnlockOutputResetTimeout; an output that does
        // not fall back is decision 2's "cannot be confirmed reset", not a completion.
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken,
            unlockOutputResetTimeout: TimeSpan.FromMilliseconds(200));

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (progress, token) =>
            {
                if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoorWithUnlockOutputStuckActive(progress.Active.Single() - 1, cargo: true);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults);
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal("OCCUPIED", slot.FinalPhysicalState);
        Assert.Equal("LOCKED", slot.LockState);
        Assert.Equal("ACTIVE", slot.UnlockOutputState);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        Assert.Empty(state.CompletedSlots);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task SlotsNeverStartedKeepTheirRealReadingsAndTheFailedSlotKeepsItsUnknown()
    {
        // ADR-cross-0058 Verification, decision 6: NOT_STARTED with the real IO readings and empty
        // reasonCodes, and the failed slot is not overwritten as NOT_STARTED afterwards.
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        fixture.Io.JamLock(1);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1, 2, 4], expectedOccupied: true),
            (progress, token) =>
            {
                if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(progress.Active.Single() - 1, cargo: true);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal([1, 2, 4], result.SlotResults.Select(slot => slot.SlotNo));
        Assert.Equal("COMPLETED", result.SlotResults[0].Outcome);

        WireToGateSlotExecutionResult failed = result.SlotResults[1];
        Assert.Equal("UNKNOWN", failed.Outcome);
        Assert.Equal(["ACTION_NOT_ALLOWED_IN_STATE"], failed.ReasonCodes);
        Assert.Equal("EMPTY", failed.FinalPhysicalState);
        Assert.Equal("LOCKED", failed.LockState);
        Assert.Equal("RESET", failed.UnlockOutputState);

        WireToGateSlotExecutionResult neverStarted = result.SlotResults[2];
        Assert.Equal("NOT_STARTED", neverStarted.Outcome);
        Assert.Empty(neverStarted.ReasonCodes);
        Assert.Equal("EMPTY", neverStarted.FinalPhysicalState);
        Assert.Equal("LOCKED", neverStarted.LockState);
        Assert.Equal("RESET", neverStarted.UnlockOutputState);
        Assert.Equal(0, fixture.Io.UnlockCount(3));

        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(
            ["COMPLETED", "UNKNOWN", "NOT_STARTED"],
            state.SlotResults.Select(slot => slot.Outcome));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ARefusedCommandReportsRealReadingsAndPutsTheReasonOnlyOnTheOffendingSlot()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        fixture.Io.CloseDoor(1, cargo: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("FAILED", result.OverallOutcome);
        Assert.All(result.SlotResults, slot =>
        {
            Assert.Equal("NOT_STARTED", slot.Outcome);
            Assert.Equal("LOCKED", slot.LockState);
            Assert.Equal("RESET", slot.UnlockOutputState);
        });
        Assert.Empty(result.SlotResults[0].ReasonCodes);
        Assert.Equal("EMPTY", result.SlotResults[0].FinalPhysicalState);
        Assert.Equal(["SLOT_OPERATION_CONFLICT"], result.SlotResults[1].ReasonCodes);
        Assert.Equal("OCCUPIED", result.SlotResults[1].FinalPhysicalState);
        Assert.Equal(0, fixture.Io.UnlockCount(0));
        Assert.Equal(0, fixture.Io.UnlockCount(1));
    }

    [Theory]
    [InlineData(OperationType.Load)]
    [InlineData(OperationType.Unload)]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    [Trait("ProtocolVector", "CV-DESTINATION-UNLOAD-ALL-EMPTY")]
    public async Task EveryCompletedMainPathResultMeetsTheServerCompletionCondition(OperationType operationType)
    {
        // The invariant of onboard-hmi#49 on the main path. Each door is shut at the expected
        // occupancy while its unlock output is still energised, and the output falls back a moment
        // later: a completion taken from the shut door alone would report ACTIVE.
        bool expectedOccupied = operationType == OperationType.Load;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            operationType,
            [1, 3, 5],
            expectedOccupied);
        foreach (int physicalSlot in command.Slots)
        {
            fixture.Io.CloseDoor(physicalSlot - 1, cargo: !expectedOccupied);
        }

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, token) =>
            {
                if (progress.Phase == "WAITING_OPERATOR")
                {
                    ShutWithTheOutputFallingBackLate(fixture.Io, progress.Active.Single() - 1, expectedOccupied);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        AssertCompletedMeetsServerCompletionCondition(command, result);
        Assert.All(command.Slots, slot => Assert.Equal(1, fixture.Io.UnlockCount(slot - 1)));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task EveryCompletedResumeResultMeetsTheServerCompletionCondition()
    {
        // The same invariant on the resume path: the first slot completed before the failure and is
        // reused, the second is driven again and its output falls back late.
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Load,
            [1, 2],
            expectedOccupied: true);
        fixture.Io.JamLock(1);
        WireToGateOperationExecutionResult first = await fixture.Executor.ExecuteAsync(
            command,
            (progress, token) =>
            {
                if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(progress.Active.Single() - 1, cargo: true);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
        Assert.Equal("UNKNOWN", first.OverallOutcome);
        Assert.Equal("SAFE_FINISH_REACHED", first.JournalCheckpoint);
        fixture.Io.UnjamLock(1);

        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        await fixture.Journal.WriteRecoveryStateAsync(state with
        {
            ExceptionRecoverySessionId = "44444444-4444-4444-8444-444444444444",
            RecoveryActionId = "55555555-5555-4555-8555-555555555555"
        }, TestContext.Current.CancellationToken);
        WireToGateSlotOperationResumeCommand resume = new(
            "66666666-6666-4666-8666-666666666666",
            2,
            DateTimeOffset.UtcNow,
            "44444444-4444-4444-8444-444444444444",
            "55555555-5555-4555-8555-555555555555",
            command.DemandId,
            command.SlotOperationAttemptId,
            WireToGateRecoveryCheckpoint.SafeFinishReached,
            command.Slots,
            command.CommandContentSha256);

        WireToGateOperationExecutionResult resumed = await fixture.Executor.ResumeAsync(
            resume,
            (progress, token) =>
            {
                if (progress.Phase == "WAITING_OPERATOR")
                {
                    ShutWithTheOutputFallingBackLate(fixture.Io, progress.Active.Single() - 1, cargo: true);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        AssertCompletedMeetsServerCompletionCondition(command, resumed);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Equal(2, fixture.Io.UnlockCount(1));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AReopenIsHeldWithoutAPulseWhileTheVehicleSafetyFactSaysNo()
    {
        // The door is shut over an empty slot while the vehicle is in an emergency stop (the safety
        // projection is not STOPPED). No reopen pulse goes out; the operator is told why; once the
        // fact allows it the slot is reopened and the load completes.
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        List<(string Phase, WireToGatePromptCause Cause)> phases = [];
        int unlocksWhileHeld = -1;

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (progress, token) =>
            {
                phases.Add((progress.Phase, progress.Cause));
                if (progress.Phase == "WAITING_OPERATOR" && progress.Cause == WireToGatePromptCause.FirstOpen)
                {
                    fixture.Gate.Permitted = false;
                    fixture.Io.CloseDoor(0, cargo: false);
                }
                else if (progress.Cause == WireToGatePromptCause.ReopenHeldBySafety)
                {
                    _ = Task.Run(
                        async () =>
                        {
                            await Task.Delay(400, CancellationToken.None);
                            unlocksWhileHeld = fixture.Io.UnlockCount(0);
                            fixture.Gate.Permitted = true;
                        },
                        CancellationToken.None);
                }
                else if (progress.Phase == "WAITING_OPERATOR" && progress.Cause == WireToGatePromptCause.OppositeReopen)
                {
                    fixture.Io.CloseDoor(0, cargo: true);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(1, unlocksWhileHeld);
        Assert.Equal(2, fixture.Io.UnlockCount(0));
        Assert.Equal(
            [
                ("UNLOCKING", WireToGatePromptCause.FirstOpen),
                ("WAITING_OPERATOR", WireToGatePromptCause.FirstOpen),
                ("WAITING_OPERATOR", WireToGatePromptCause.ReopenHeldBySafety),
                ("UNLOCKING", WireToGatePromptCause.OppositeReopen),
                ("WAITING_OPERATOR", WireToGatePromptCause.OppositeReopen)
            ],
            phases.Where(item => item.Phase is "UNLOCKING" or "WAITING_OPERATOR").ToArray());
        Assert.DoesNotContain(phases, item => item.Phase == "PAUSED");
    }

    [Theory]
    [InlineData("IO")]
    [InlineData("TIMEOUT")]
    [InlineData("INVALID_DATA")]
    [InlineData("INVALID_OPERATION")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AProgressReportThatCannotBeSentDoesNotMakeTheSlotUnknown(string failure)
    {
        // Every progress report fails the way a dropped connection makes it fail. That says nothing
        // about the slot: UNKNOWN comes from IO readings only, and the operation completes.
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true),
            (progress, token) =>
            {
                if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(progress.Active.Single() - 1, cargo: true);
                }

                throw failure switch
                {
                    "IO" => new IOException("connection dropped"),
                    "TIMEOUT" => new TimeoutException("send timed out"),
                    "INVALID_DATA" => new InvalidDataException("LOCK_NOT_CLOSED"),
                    _ => new InvalidOperationException("session not ready")
                };
            },
            TestContext.Current.CancellationToken);

        AssertCompletedMeetsServerCompletionCondition(
            CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true),
            result);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Equal(1, fixture.Io.UnlockCount(1));
    }

    [Fact]
    public async Task AFeedbackStableWindowOfZeroIsRefusedByBothExecutors()
    {
        // With no stable window the Modbus client returns on the first matching read, and a single
        // glitching light-curtain reading would reopen a slot.
        string directory = Path.Combine(Path.GetTempPath(), "w2g-executor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
        WireToGateSlotOperationExecutorOptions zeroWindow = new(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WireToGateSlotOperationExecutor(new ScriptedIo(), journal, new SystemClock(), zeroWindow, () => true));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WireToGateRecoveryVectorExecutor(new ScriptedIo(), journal, new SystemClock(), zeroWindow));
        await using WireToGateSlotOperationExecutor accepted = new(
            new ScriptedIo(),
            journal,
            new SystemClock(),
            zeroWindow with { FeedbackStableWindow = TimeSpan.FromMilliseconds(1) },
            () => true);
    }

    /// <summary>
    /// What the server checks before it accepts a completed operation (program#54, onboard-hmi#49):
    /// an overall COMPLETED covers every slot of the command, and each one is COMPLETED at the
    /// expected occupancy, LOCKED and RESET.
    /// </summary>
    private static void AssertCompletedMeetsServerCompletionCondition(
        WireToGateSlotOperationCommand command,
        WireToGateOperationExecutionResult result)
    {
        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(command.Slots, result.SlotResults.Select(slot => slot.SlotNo).ToArray());
        string expectedState = command.ExpectedOccupied ? "OCCUPIED" : "EMPTY";
        Assert.All(result.SlotResults, slot =>
        {
            Assert.Equal("COMPLETED", slot.Outcome);
            Assert.Equal(expectedState, slot.FinalPhysicalState);
            Assert.Equal("LOCKED", slot.LockState);
            Assert.Equal("RESET", slot.UnlockOutputState);
            Assert.Empty(slot.ReasonCodes);
        });
    }

    /// <summary>
    /// The door is shut at the given occupancy with the unlock output still energised, and the output
    /// falls back 100 ms later -- well inside the fixture's UnlockOutputResetTimeout.
    /// </summary>
    private static void ShutWithTheOutputFallingBackLate(ScriptedIo io, int slotIndex, bool cargo)
    {
        io.CloseDoorWithUnlockOutputStuckActive(slotIndex, cargo);
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            io.ReleaseUnlockOutput(slotIndex);
        });
    }

    private static WireToGateSlotOperationCommand CreateCommand(
        OperationType operationType,
        IReadOnlyList<int> slots,
        bool expectedOccupied) =>
        new(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            1,
            DateTimeOffset.UtcNow,
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            Guid.NewGuid().ToString("D"),
            operationType,
            slots,
            slots.Count,
            expectedOccupied,
            new string('0', 64));

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            SimulationIo io,
            SqliteWireToGateJournal journal,
            WireToGateSlotOperationExecutor executor)
        {
            Io = io;
            Journal = journal;
            Executor = executor;
        }

        public SimulationIo Io { get; }

        public SqliteWireToGateJournal Journal { get; }

        public WireToGateSlotOperationExecutor Executor { get; }

        public static async Task<TestFixture> CreateAsync(
            bool initialCargo = false,
            bool finalCargo = true,
            CancellationToken cancellationToken = default)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "w2g-executor",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
            await journal.InitializeAsync(cancellationToken);
            SimulationIo io = new(initialCargo, finalCargo);
            WireToGateSlotOperationExecutor executor = new(
                io,
                journal,
                new SystemClock(),
                new WireToGateSlotOperationExecutorOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromSeconds(1)),
                () => true);
            return new TestFixture(io, journal, executor);
        }

        public async ValueTask DisposeAsync()
        {
            await Executor.DisposeAsync();
            await Journal.DisposeAsync();
        }
    }

    private sealed class SimulationIo : IIoModuleClient
    {
        private readonly LockerSnapshot[] _lockers;
        private readonly bool _finalCargo;
        private readonly object _sync = new();

        public bool IsConnected => true;

        public int UnlockCount { get; private set; }

        public IoSnapshot CurrentSnapshot { get; private set; } = null!;

        public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

        public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged;

        public SimulationIo(bool initialCargo = false, bool finalCargo = true)
        {
            _finalCargo = finalCargo;
            _lockers = Enumerable.Range(0, 8)
                .Select(index => new LockerSnapshot(
                    index,
                    index + 1,
                    false,
                    true,
                    initialCargo ? false : true,
                    DateTimeOffset.UtcNow))
                .ToArray();
            CurrentSnapshot = new IoSnapshot(true, _lockers.ToArray(), DateTimeOffset.UtcNow);
            _ = ConnectionChanged;
        }

        public Task StartAsync(CancellationToken applicationStopping) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                UnlockCount++;
                UpdateLocker(slotIndex, locker => locker with
                {
                    LockFeedbackRaw = false,
                    UnlockOutputRaw = true,
                    ObservedAt = DateTimeOffset.UtcNow
                });
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
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LockerSnapshot locker;
                lock (_sync)
                {
                    locker = CurrentSnapshot.GetLocker(slotIndex);
                    if (predicate(locker))
                    {
                        return locker;
                    }

                    if (locker.LockFeedbackRaw is false && locker.UnlockOutputRaw is true)
                    {
                        UpdateLocker(slotIndex, current => current with
                        {
                            UnlockOutputRaw = false,
                            ObservedAt = DateTimeOffset.UtcNow
                        });
                    }
                    else if (locker.LockFeedbackRaw is false && locker.UnlockOutputRaw is false)
                    {
                        UpdateLocker(slotIndex, current => current with
                        {
                            LockFeedbackRaw = true,
                            LightCurtainRaw = _finalCargo ? false : true,
                            ObservedAt = DateTimeOffset.UtcNow
                        });
                    }
                }

                await Task.Delay(1, cancellationToken);
            }

            throw new TimeoutException();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void SetUnknown()
        {
            lock (_sync)
            {
                CurrentSnapshot = new IoSnapshot(
                    false,
                    Enumerable.Range(0, 8)
                        .Select(index => LockerSnapshot.Unknown(index, DateTimeOffset.UtcNow))
                        .ToArray(),
                    DateTimeOffset.UtcNow);
            }
        }

        private void UpdateLocker(int slotIndex, Func<LockerSnapshot, LockerSnapshot> update)
        {
            _lockers[slotIndex] = update(_lockers[slotIndex]);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CurrentSnapshot = new IoSnapshot(true, _lockers.ToArray(), now);
            SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(CurrentSnapshot));
        }
    }
}
