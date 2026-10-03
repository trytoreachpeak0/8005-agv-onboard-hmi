using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The executor's half of an administrator's slot fault declaration (REQ-0359, CP-0005 item 4.2,
/// 8005-agv-onboard-hmi#215): apply it only to the slot the run is waiting on the operator for, end the run on an
/// UNKNOWN result it returns rather than in silence, open no further door, and keep the declared slot UNKNOWN
/// across a restart whatever it then reads.
/// </summary>
/// <remarks>
/// Every case drives a real run over <see cref="ScriptedIo"/>, which advances only when the test says so: the
/// questions here are about what the run does at a moment it did not choose. The declaration's
/// <c>announceApplied</c> callback runs at the one moment between the journal write and the stop, which is where
/// the races this ticket names are put.
/// </remarks>
public sealed partial class WireToGateSlotOperationExecutorTests
{
    private const string DeclarationId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";

    /// <summary>
    /// LOAD of three slots, the second one waiting for the operator: slot 1 COMPLETED from its reading, slot 2
    /// UNKNOWN with <c>SLOT_FAULT_DECLARED</c>, slot 3 NOT_STARTED, and no pulse after the declaration. The run
    /// returns the result -- the load cancellation's abort ends in silence, a declaration must not.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task ADeclarationOnTheAwaitedSlotEndsTheRunOnAnUnknownResultWithNoFurtherPulse()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2, 3], expectedOccupied: true);
        AwaitedSlots awaited = new();
        Task<WireToGateOperationExecutionResult> run = fixture.Executor.ExecuteAsync(command, awaited.Report, token);
        await awaited.WaitForAsync(1, token);
        fixture.Io.CloseDoor(0, cargo: true);
        await awaited.WaitForAsync(2, token);

        int pulsesAtDeclaration = -1;
        WireToGateRecoveryState? journaledAtAnnouncement = null;
        bool runEndedBeforeAnnouncement = false;
        WireToGateSlotFaultDeclarationOutcome outcome = await fixture.Executor.DeclareSlotFaultAsync(
            Declaration(command, 2),
            async () =>
            {
                pulsesAtDeclaration = TotalPulses(fixture.Io);
                runEndedBeforeAnnouncement = run.IsCompleted;
                journaledAtAnnouncement = await fixture.Journal.ReadRecoveryStateAsync(token);
            },
            token);

        WireToGateOperationExecutionResult result = await run.WaitAsync(TimeSpan.FromSeconds(10), token);

        Assert.Equal(WireToGateSlotFaultDeclarationOutcome.Applied, outcome);
        Assert.False(runEndedBeforeAnnouncement);
        // Journaled before the APPLIED is said.
        Assert.Equal(DeclarationId, journaledAtAnnouncement?.SlotFaultDeclaration?.DeclarationId);
        Assert.Equal("UNKNOWN", result.OverallOutcome);
        AssertSlot(result, 1, "COMPLETED", []);
        AssertSlot(result, 2, "UNKNOWN", [WireToGateSlotOperationExecutor.SlotFaultDeclaredReason]);
        AssertSlot(result, 3, "NOT_STARTED", []);
        Assert.Equal(pulsesAtDeclaration, TotalPulses(fixture.Io));
        Assert.Equal(0, fixture.Io.UnlockCount(2));

        // The journal keeps the attempt unsettled with the declaration and the door that may be open, as every
        // UNKNOWN decided mid-execution does.
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(command.SlotOperationAttemptId, state.UnsettledSlotOperationAttemptId);
        Assert.Equal(DeclarationId, state.SlotFaultDeclaration?.DeclarationId);
        Assert.Equal([2], state.ActiveUnlockSlots);
        Assert.Equal(WireToGateRecoveryCheckpoint.ActiveUnlockSet, state.ProvenRecoveryCheckpoint);
    }

    /// <summary>
    /// The declared slot's reading turns final -- the sensor reads the target state while the slot is in fact
    /// faulty -- after the declaration is applied and before the run is stopped. It is still UNKNOWN: the slot is
    /// the declaration's, and judging it from the reading is exactly what the interrupted settlement does and what
    /// a declaration exists to prevent. This is also the interleaving the ticket names: the slot closes its loop
    /// between the check and the stop, and the result is one, never COMPLETED turned UNKNOWN.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task TheDeclaredSlotStaysUnknownWhenItReadsFinalBetweenTheCheckAndTheStop()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2, 3], expectedOccupied: true);
        AwaitedSlots awaited = new();
        Task<WireToGateOperationExecutionResult> run = fixture.Executor.ExecuteAsync(command, awaited.Report, token);
        await awaited.WaitForAsync(1, token);
        fixture.Io.CloseDoor(0, cargo: true);
        await awaited.WaitForAsync(2, token);

        WireToGateSlotFaultDeclarationOutcome outcome = await fixture.Executor.DeclareSlotFaultAsync(
            Declaration(command, 2),
            async () =>
            {
                fixture.Io.CloseDoor(1, cargo: true);
                // Long enough for the run to see the closed door and reach its closing step, which the
                // declaration holds.
                await Task.Delay(200, token);
            },
            token);
        WireToGateOperationExecutionResult result = await run.WaitAsync(TimeSpan.FromSeconds(10), token);

        Assert.Equal(WireToGateSlotFaultDeclarationOutcome.Applied, outcome);
        Assert.Equal("UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult declared = AssertSlot(
            result,
            2,
            "UNKNOWN",
            [WireToGateSlotOperationExecutor.SlotFaultDeclaredReason]);
        // The physical fields are the live readings (ADR-cross-0058 decision 6); only the conclusion is the
        // declaration's.
        Assert.Equal("OCCUPIED", declared.FinalPhysicalState);
        Assert.Equal("LOCKED", declared.LockState);
        AssertSlot(result, 3, "NOT_STARTED", []);
        Assert.Equal(0, fixture.Io.UnlockCount(2));
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.DoesNotContain(2, state.CompletedSlots);
        Assert.Equal(
            "UNKNOWN",
            Assert.Single(state.SlotResults, item => item.SlotNo == 2).Outcome);
    }

    /// <summary>
    /// The same interleaving with nothing to fall back on: the journal does not observe cancellation (the interface
    /// does not promise it does), so the stop cannot catch the run at its next journal write. The slot closes while
    /// the declaration holds the gate; the run's closing step must itself find the declaration. Without that check
    /// it wrote slot 2 COMPLETED and then ended in silence at its next cancellation check -- an APPLIED with no
    /// OperationResult behind it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task TheClosingStepFindsTheDeclarationWhenNoJournalWriteObservesTheStop()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), "w2g-executor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
        await journal.InitializeAsync(token);
        ScriptedIo io = new();
        await using WireToGateSlotOperationExecutor executor = new(
            io,
            new CancellationDeafJournal(journal),
            new SystemClock(),
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(30)),
            () => true);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2, 3], expectedOccupied: true);
        AwaitedSlots awaited = new();
        Task<WireToGateOperationExecutionResult> run = executor.ExecuteAsync(command, awaited.Report, token);
        await awaited.WaitForAsync(1, token);
        io.CloseDoor(0, cargo: true);
        await awaited.WaitForAsync(2, token);

        await executor.DeclareSlotFaultAsync(
            Declaration(command, 2),
            async () =>
            {
                io.CloseDoor(1, cargo: true);
                await Task.Delay(200, token);
            },
            token);
        WireToGateOperationExecutionResult result = await run.WaitAsync(TimeSpan.FromSeconds(10), token);

        AssertSlot(result, 2, "UNKNOWN", [WireToGateSlotOperationExecutor.SlotFaultDeclaredReason]);
        Assert.DoesNotContain(2, (await journal.ReadRecoveryStateAsync(token)).CompletedSlots);
        Assert.Equal(0, io.UnlockCount(2));
    }

    /// <summary>
    /// While the declaration is being applied the operator shuts slot 2 empty -- the opposite occupancy for a load --
    /// and the run turns to reopen it. No pulse goes out: a reopen is decided under the same gate and finds the
    /// declaration (NEVER_UNLOCK_AFTER_DECLARATION_APPLIED). The IO module here ignores cancellation on a pulse, as a
    /// write already on its way to the bus would, so only that check stands between the declaration and the door.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task ADoorShutOverTheOppositeOccupancyDuringTheDeclarationIsNotReopened()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2, 3], expectedOccupied: true);
        AwaitedSlots awaited = new();
        Task<WireToGateOperationExecutionResult> run = fixture.Executor.ExecuteAsync(command, awaited.Report, token);
        await awaited.WaitForAsync(1, token);
        fixture.Io.CloseDoor(0, cargo: true);
        await awaited.WaitForAsync(2, token);
        int slotTwoPulses = fixture.Io.UnlockCount(1);

        await fixture.Executor.DeclareSlotFaultAsync(
            Declaration(command, 2),
            async () =>
            {
                fixture.Io.CloseDoor(1, cargo: false);
                await Task.Delay(200, token);
            },
            token);
        WireToGateOperationExecutionResult result = await run.WaitAsync(TimeSpan.FromSeconds(10), token);

        AssertSlot(result, 2, "UNKNOWN", [WireToGateSlotOperationExecutor.SlotFaultDeclaredReason]);
        Assert.Equal(slotTwoPulses, fixture.Io.UnlockCount(1));
        Assert.Equal(0, fixture.Io.UnlockCount(2));
    }

    /// <summary>
    /// A completed slot is reported as the live IO reads it when the declaration settles the run, not as it read when
    /// it closed: slot 1 was loaded, then its basket was taken out again. It is UNKNOWN, as the interrupted settlement
    /// would have it, not COMPLETED (review S2 of PR #247).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task ACompletedSlotThatNoLongerReadsFinalIsReportedUnknownWhenTheDeclarationSettles()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2, 3], expectedOccupied: true);
        AwaitedSlots awaited = new();
        Task<WireToGateOperationExecutionResult> run = fixture.Executor.ExecuteAsync(command, awaited.Report, token);
        await awaited.WaitForAsync(1, token);
        fixture.Io.CloseDoor(0, cargo: true);
        await awaited.WaitForAsync(2, token);
        fixture.Io.CloseDoor(0, cargo: false);

        await fixture.Executor.DeclareSlotFaultAsync(Declaration(command, 2), () => Task.CompletedTask, token);
        WireToGateOperationExecutionResult result = await run.WaitAsync(TimeSpan.FromSeconds(10), token);

        WireToGateSlotExecutionResult first = AssertSlot(result, 1, "UNKNOWN", ["RECOVERY_CHECKPOINT_NOT_UNIQUE"]);
        Assert.Equal("EMPTY", first.FinalPhysicalState);
        AssertSlot(result, 2, "UNKNOWN", [WireToGateSlotOperationExecutor.SlotFaultDeclaredReason]);
        Assert.DoesNotContain(1, (await fixture.Journal.ReadRecoveryStateAsync(token)).CompletedSlots);
    }

    /// <summary>
    /// The executor's half of the guard against a load cancellation (review S1 of PR #247): the operator's
    /// cancellation of this attempt is waiting for its answer when the declaration comes. It is refused in the journal
    /// step that would have written it, nothing is journaled, nothing is announced, and the run keeps going.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE")]
    public async Task ADeclarationOnAnAttemptWithACancellationPendingIsRefusedInTheJournalStep()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);
        AwaitedSlots awaited = new();
        Task<WireToGateOperationExecutionResult> run = fixture.Executor.ExecuteAsync(command, awaited.Report, token);
        await awaited.WaitForAsync(1, token);
        await fixture.Journal.UpdateRecoveryStateAsync(
            state => state with { PendingLoadCancellation = PendingCancellation(command) },
            token);

        WireToGateSlotFaultDeclarationOutcome outcome = await fixture.Executor.DeclareSlotFaultAsync(
            Declaration(command, 1),
            NotAnnounced,
            token);

        Assert.Equal(WireToGateSlotFaultDeclarationOutcome.TakenOverByLoadCancellation, outcome);
        Assert.Null((await fixture.Journal.ReadRecoveryStateAsync(token)).SlotFaultDeclaration);
        Assert.False(run.IsCompleted);
        Assert.True(await fixture.Executor.AbortOperationAsync(command.SlotOperationAttemptId, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    /// <summary>
    /// The moment between one slot closing its loop and the next one being waited on (review N1 of PR #247). The run is
    /// held at the journal write that records slot 2 COMPLETED; a declaration naming slot 2, just closed, or slot 3,
    /// not yet opened, is refused, and once the write goes through the run opens slot 3 as usual.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE")]
    public async Task BetweenOneSlotClosingAndTheNextBeingAwaitedNoSlotCanBeDeclared()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), "w2g-executor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
        await journal.InitializeAsync(token);
        HoldingJournal holding = new(
            journal,
            state => state.CompletedSlots.Contains(2) && state.ActiveUnlockSlots.Count == 0);
        ScriptedIo io = new();
        await using WireToGateSlotOperationExecutor executor = new(
            io,
            holding,
            new SystemClock(),
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(30)),
            () => true);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2, 3], expectedOccupied: true);
        AwaitedSlots awaited = new();
        Task<WireToGateOperationExecutionResult> run = executor.ExecuteAsync(command, awaited.Report, token);
        await awaited.WaitForAsync(1, token);
        io.CloseDoor(0, cargo: true);
        await awaited.WaitForAsync(2, token);
        io.CloseDoor(1, cargo: true);
        await holding.Held.WaitAsync(TimeSpan.FromSeconds(10), token);

        Assert.Equal(
            WireToGateSlotFaultDeclarationOutcome.SlotNotAwaiting,
            await executor.DeclareSlotFaultAsync(Declaration(command, 2), NotAnnounced, token));
        Assert.Equal(
            WireToGateSlotFaultDeclarationOutcome.SlotNotAwaiting,
            await executor.DeclareSlotFaultAsync(Declaration(command, 3), NotAnnounced, token));
        Assert.Equal(0, io.UnlockCount(2));

        holding.Release();
        await awaited.WaitForAsync(3, token);
        io.CloseDoor(2, cargo: true);
        WireToGateOperationExecutionResult result = await run.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Null((await journal.ReadRecoveryStateAsync(token)).SlotFaultDeclaration);
    }

    /// <summary>
    /// The other order: the operator shut slot 2 over its basket and the run moved on before the declaration
    /// came. It is refused as no longer waiting -- not applied to slot 3, which is the slot waiting now -- and the
    /// run goes on to finish the load.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE")]
    public async Task ADeclarationArrivingAfterTheSlotClosedItsLoopIsRefusedAndNotMovedToTheNextSlot()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2, 3], expectedOccupied: true);
        AwaitedSlots awaited = new();
        Task<WireToGateOperationExecutionResult> run = fixture.Executor.ExecuteAsync(command, awaited.Report, token);
        await awaited.WaitForAsync(1, token);
        fixture.Io.CloseDoor(0, cargo: true);
        await awaited.WaitForAsync(2, token);
        fixture.Io.CloseDoor(1, cargo: true);
        await awaited.WaitForAsync(3, token);
        bool announced = false;

        WireToGateSlotFaultDeclarationOutcome outcome = await fixture.Executor.DeclareSlotFaultAsync(
            Declaration(command, 2),
            () =>
            {
                announced = true;
                return Task.CompletedTask;
            },
            token);

        Assert.Equal(WireToGateSlotFaultDeclarationOutcome.SlotNotAwaiting, outcome);
        Assert.False(announced);
        Assert.Null((await fixture.Journal.ReadRecoveryStateAsync(token)).SlotFaultDeclaration);
        fixture.Io.CloseDoor(2, cargo: true);
        WireToGateOperationExecutionResult result = await run.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.All(result.SlotResults, slot => Assert.Equal("COMPLETED", slot.Outcome));
    }

    /// <summary>
    /// Refusals that change nothing: a slot the run is not at, an attempt it is not running, and no run at all.
    /// The run in progress keeps going and still opens its next door.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE")]
    public async Task ADeclarationNamingAnotherSlotOrAttemptIsRefusedAndTheRunGoesOn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2, 3], expectedOccupied: true);

        Assert.Equal(
            WireToGateSlotFaultDeclarationOutcome.AttemptNotExecuting,
            await fixture.Executor.DeclareSlotFaultAsync(Declaration(command, 1), NotAnnounced, token));

        AwaitedSlots awaited = new();
        Task<WireToGateOperationExecutionResult> run = fixture.Executor.ExecuteAsync(command, awaited.Report, token);
        await awaited.WaitForAsync(1, token);
        WireToGateRecoveryState before = await fixture.Journal.ReadRecoveryStateAsync(token);

        Assert.Equal(
            WireToGateSlotFaultDeclarationOutcome.SlotNotAwaiting,
            await fixture.Executor.DeclareSlotFaultAsync(Declaration(command, 3), NotAnnounced, token));
        Assert.Equal(
            WireToGateSlotFaultDeclarationOutcome.AttemptNotExecuting,
            await fixture.Executor.DeclareSlotFaultAsync(
                Declaration(command, 1) with { SlotOperationAttemptId = Guid.NewGuid().ToString("D") },
                NotAnnounced,
                token));

        WireToGateRecoveryState after = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Null(after.SlotFaultDeclaration);
        Assert.Equal(before.ActiveUnlockSlots, after.ActiveUnlockSlots);
        Assert.Equal(before.SlotResults, after.SlotResults);
        fixture.Io.CloseDoor(0, cargo: true);
        await awaited.WaitForAsync(2, token);
        fixture.Io.CloseDoor(1, cargo: true);
        await awaited.WaitForAsync(3, token);
        fixture.Io.CloseDoor(2, cargo: true);
        WireToGateOperationExecutionResult result = await run.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(1, fixture.Io.UnlockCount(2));
    }

    /// <summary>
    /// The process goes away after the declaration is journaled and before the run settled: the next start's
    /// interrupted settlement reports the declared slot UNKNOWN with <c>SLOT_FAULT_DECLARED</c> although it now
    /// reads final, rather than settling it a second time from the live IO -- which would have called it COMPLETED.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task AfterARestartTheInterruptedSettlementKeepsTheDeclaredSlotUnknownThoughItReadsFinal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WireToGateRecoveryState journaledAtDeclaration;
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2, 3], expectedOccupied: true);
        await using (ScriptedFixture first = await ScriptedFixture.CreateAsync(token))
        {
            AwaitedSlots awaited = new();
            Task<WireToGateOperationExecutionResult> run = first.Executor.ExecuteAsync(command, awaited.Report, token);
            await awaited.WaitForAsync(1, token);
            first.Io.CloseDoor(0, cargo: true);
            await awaited.WaitForAsync(2, token);
            WireToGateRecoveryState? captured = null;
            await first.Executor.DeclareSlotFaultAsync(
                Declaration(command, 2),
                async () => captured = await first.Journal.ReadRecoveryStateAsync(token),
                token);
            await run.WaitAsync(TimeSpan.FromSeconds(10), token);
            // What the journal held when the process "went away": the declaration, and nothing of the settlement.
            journaledAtDeclaration = Assert.IsType<WireToGateRecoveryState>(captured);
        }

        Assert.Equal([2], journaledAtDeclaration.ActiveUnlockSlots);
        Assert.DoesNotContain(journaledAtDeclaration.SlotResults, item => item.SlotNo == 2);

        await using ScriptedFixture restarted = await ScriptedFixture.CreateAsync(token);
        await restarted.Journal.UpdateRecoveryStateAsync(_ => journaledAtDeclaration, token);
        restarted.Io.CloseDoor(0, cargo: true);
        restarted.Io.CloseDoor(1, cargo: true);

        WireToGateOperationExecutionResult settled = await restarted.Executor.SettleInterruptedAsync(token);

        Assert.Equal("UNKNOWN", settled.OverallOutcome);
        AssertSlot(settled, 1, "COMPLETED", []);
        AssertSlot(settled, 2, "UNKNOWN", [WireToGateSlotOperationExecutor.SlotFaultDeclaredReason]);
        AssertSlot(settled, 3, "NOT_STARTED", []);
        Assert.Equal(0, TotalPulses(restarted.Io));

        // Settled again -- a second restart before the result reached the outbox: the same conclusion.
        WireToGateOperationExecutionResult again = await restarted.Executor.SettleInterruptedAsync(token);
        AssertSlot(again, 2, "UNKNOWN", [WireToGateSlotOperationExecutor.SlotFaultDeclaredReason]);
    }

    /// <summary>
    /// The load cancellation's abort is unchanged: its run still ends in <see cref="OperationCanceledException"/>
    /// with no result, because the cancellation reports the attempt. Only a declaration ends a run on a result.
    /// </summary>
    [Fact]
    public async Task TheLoadCancellationAbortStillEndsTheRunWithoutAResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);
        AwaitedSlots awaited = new();
        Task<WireToGateOperationExecutionResult> run = fixture.Executor.ExecuteAsync(command, awaited.Report, token);
        await awaited.WaitForAsync(1, token);

        Assert.True(await fixture.Executor.AbortOperationAsync(command.SlotOperationAttemptId, token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Null((await fixture.Journal.ReadRecoveryStateAsync(token)).SlotFaultDeclaration);
        Assert.Equal(
            WireToGateSlotFaultDeclarationOutcome.AttemptNotExecuting,
            await fixture.Executor.DeclareSlotFaultAsync(Declaration(command, 1), NotAnnounced, token));
    }

    private static Task NotAnnounced() =>
        throw new InvalidOperationException("A refused declaration must not be announced as applied.");

    private static WireToGateSlotFaultDeclaration Declaration(WireToGateSlotOperationCommand command, int slotNo) =>
        new(DeclarationId, command.SlotOperationAttemptId, slotNo, "LOCK");

    private static int TotalPulses(ScriptedIo io) => Enumerable.Range(0, 8).Sum(io.UnlockCount);

    private static WireToGateSlotExecutionResult AssertSlot(
        WireToGateOperationExecutionResult result,
        int slotNo,
        string outcome,
        IReadOnlyList<string> reasonCodes)
    {
        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults, item => item.SlotNo == slotNo);
        Assert.Equal(outcome, slot.Outcome);
        Assert.Equal(reasonCodes, slot.ReasonCodes);
        return slot;
    }

    /// <summary>
    /// A journal that holds the first recovery-state write whose result matches <c>hold</c> until the test releases it.
    /// </summary>
    private sealed class HoldingJournal(IWireToGateJournal inner, Func<WireToGateRecoveryState, bool> hold)
        : IWireToGateJournal
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed = 1;

        public Task Held => _held.Task;

        public void Release() => _released.TrySetResult();

        public async Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1
                && change(await inner.ReadRecoveryStateAsync(CancellationToken.None)) is { } preview
                && hold(preview)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _held.TrySetResult();
                await _released.Task;
            }

            return await inner.UpdateRecoveryStateAsync(change, cancellationToken);
        }

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

        public Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default) =>
            inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);

        public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
            WireToGateDurableMessage expected,
            WireToGateDurableMessage replacement,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceOutgoingForReplayAsync(expected, replacement, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByMessageIdAsync(messageId, cancellationToken);

        public Task MarkOutgoingAcknowledgedAsync(
            string messageId,
            string acceptedContentSha256,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAcknowledgedAsync(messageId, acceptedContentSha256, cancellationToken);

        public Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadUnacknowledgedOutgoingAsync(cancellationToken);

        public Task<IReadOnlyList<WireToGateAppliedJourneySnapshot>> ReadAppliedJourneySnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadAppliedJourneySnapshotsAsync(cancellationToken);

        public Task<WireToGateAppliedJourneySnapshot> SaveAppliedJourneySnapshotAsync(
            WireToGateAppliedJourneySnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            inner.SaveAppliedJourneySnapshotAsync(snapshot, cancellationToken);

        public Task<string> ComputeContentSha256Async(CancellationToken cancellationToken = default) =>
            inner.ComputeContentSha256Async(cancellationToken);

        // The test owns the inner journal.
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A journal whose recovery-state writes never observe a cancellation token.</summary>
    private sealed class CancellationDeafJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, CancellationToken.None);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, settled, CancellationToken.None);

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(CancellationToken.None);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

        public Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default) =>
            inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);

        public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
            WireToGateDurableMessage expected,
            WireToGateDurableMessage replacement,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceOutgoingForReplayAsync(expected, replacement, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByMessageIdAsync(messageId, cancellationToken);

        public Task MarkOutgoingAcknowledgedAsync(
            string messageId,
            string acceptedContentSha256,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAcknowledgedAsync(messageId, acceptedContentSha256, cancellationToken);

        public Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadUnacknowledgedOutgoingAsync(cancellationToken);

        public Task<IReadOnlyList<WireToGateAppliedJourneySnapshot>> ReadAppliedJourneySnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadAppliedJourneySnapshotsAsync(cancellationToken);

        public Task<WireToGateAppliedJourneySnapshot> SaveAppliedJourneySnapshotAsync(
            WireToGateAppliedJourneySnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            inner.SaveAppliedJourneySnapshotAsync(snapshot, cancellationToken);

        public Task<string> ComputeContentSha256Async(CancellationToken cancellationToken = default) =>
            inner.ComputeContentSha256Async(cancellationToken);

        // The fixture owns the inner journal.
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// The slots the run has reported waiting on the operator for, from its own progress reports: the moment a
    /// declaration can name a slot, taken from the run rather than from a delay.
    /// </summary>
    private sealed class AwaitedSlots
    {
        private readonly object _sync = new();
        private readonly Dictionary<int, TaskCompletionSource> _waits = [];

        public Task Report(WireToGateOperationProgress progress, CancellationToken cancellationToken)
        {
            if (progress is { Phase: "WAITING_OPERATOR", Active: [int slot] })
            {
                Wait(slot).TrySetResult();
            }

            return Task.CompletedTask;
        }

        public Task WaitForAsync(int slot, CancellationToken cancellationToken) =>
            Wait(slot).Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        private TaskCompletionSource Wait(int slot)
        {
            lock (_sync)
            {
                if (!_waits.TryGetValue(slot, out TaskCompletionSource? wait))
                {
                    wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waits[slot] = wait;
                }

                return wait;
            }
        }
    }
}
