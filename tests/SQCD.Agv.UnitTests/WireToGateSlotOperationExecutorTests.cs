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
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1)));
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
            (phase, active, completed, token) =>
            {
                if (phase == "WAITING_OPERATOR")
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
            WireToGateSlotOperationExecutor executor)
        {
            Io = io;
            Journal = journal;
            Executor = executor;
        }

        public ScriptedIo Io { get; }

        public SqliteWireToGateJournal Journal { get; }

        public WireToGateSlotOperationExecutor Executor { get; }

        public static async Task<ScriptedFixture> CreateAsync(CancellationToken cancellationToken)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "w2g-executor",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
            await journal.InitializeAsync(cancellationToken);
            ScriptedIo io = new();
            WireToGateSlotOperationExecutor executor = new(
                io,
                journal,
                new SystemClock(),
                new WireToGateSlotOperationExecutorOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(30)));
            return new ScriptedFixture(io, journal, executor);
        }

        public async ValueTask DisposeAsync()
        {
            await Executor.DisposeAsync();
            await Journal.DisposeAsync();
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
                // already fallen back, which is what the executor waits for next.
                Update(slotIndex, locker => locker with
                {
                    LockFeedbackRaw = false,
                    UnlockOutputRaw = false,
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
            (phase, active, completed, token) =>
            {
                if (phase == "WAITING_OPERATOR")
                {
                    if (completed.Count == 0)
                    {
                        fixture.Io.CloseDoor(active.Single() - 1, command.ExpectedOccupied);
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
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1)));
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
