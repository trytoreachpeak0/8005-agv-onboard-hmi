using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// What the interrupted settlement calls "opened" (8005-agv-onboard-hmi#186). The process exits after the
/// executor journaled its conclusion and before the business layer put the result in the outbox; the next
/// start settles the attempt from the journal and the live IO alone, and must tell a slot this attempt opened
/// from one it never touched the same way a resume does.
/// </summary>
/// <remarks>
/// The slot that failed mid-execution and still reached a safe finish was opened, so it is never reported
/// NOT_STARTED. It is not counted COMPLETED either, whatever it reads after the restart: it failed on a
/// hardware condition (ADR-cross-0058 decision 2), and ADR-cross-0017 keeps such an operation blocked until a
/// person confirms it. The ticket first asked for COMPLETED on a final reading; the coordinator changed that
/// on the ADR's wording.
/// </remarks>
public sealed partial class WireToGateSlotOperationExecutorTests
{
    /// <summary>
    /// The slot whose lock never confirmed the unlock failed at a safe finish: journaled UNKNOWN, outside the
    /// active set, outside the completed ones. A settlement that looks only at the two sets reported it
    /// NOT_STARTED with no reason, which ADR-cross-0058 decision 6 reserves for a slot never opened. It is
    /// UNKNOWN under the reason it failed with.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ASettlementDoesNotCallTheSlotThatFailedAtASafeFinishNotStarted()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        fixture.Io.JamLock(0);
        WireToGateOperationExecutionResult first = await fixture.Executor.ExecuteAsync(command, null, token);
        IReadOnlyList<string> failedWith = AssertFailedAtASafeFinishOutsideBothSets(
            first,
            await fixture.Journal.ReadRecoveryStateAsync(token));
        int unlocksBeforeSettlement = fixture.Io.UnlockCount(0);

        // No result is recorded or sent: the process is gone at this point.
        WireToGateOperationExecutionResult settled = await fixture.Executor.SettleInterruptedAsync(token);

        WireToGateSlotExecutionResult slot = settled.SlotResults.Single();
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal(failedWith, slot.ReasonCodes);
        Assert.Equal("UNKNOWN", settled.OverallOutcome);
        Assert.Equal(unlocksBeforeSettlement, fixture.Io.UnlockCount(0));
    }

    /// <summary>
    /// The operator finished that load before the restart, and the slot now reads final: locked, output
    /// reset, basket in. It still settles UNKNOWN under the reason it failed with, and the conclusion stays
    /// UNKNOWN: the lock feedback that never confirmed the unlock is a hardware fault nobody has confirmed
    /// (ADR-cross-0017). The server puts it into recovery; a resume there counts the slot without another
    /// pulse (<see cref="AResumeStillCountsTheSlotThatFailedAtASafeFinishOnceItReadsFinal"/>).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ASettlementDoesNotCountTheSlotThatFailedAtASafeFinishEvenWhenItReadsFinal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        fixture.Io.JamLock(0);
        WireToGateOperationExecutionResult first = await fixture.Executor.ExecuteAsync(command, null, token);
        IReadOnlyList<string> failedWith = AssertFailedAtASafeFinishOutsideBothSets(
            first,
            await fixture.Journal.ReadRecoveryStateAsync(token));
        fixture.Io.UnjamLock(0);
        fixture.Io.CloseDoor(0, cargo: true);
        int unlocksBeforeSettlement = fixture.Io.UnlockCount(0);

        WireToGateOperationExecutionResult settled = await fixture.Executor.SettleInterruptedAsync(token);

        WireToGateSlotExecutionResult slot = settled.SlotResults.Single();
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal(failedWith, slot.ReasonCodes);
        // The physical fields are the live reading all the same (decision 6): it is the conclusion that waits.
        Assert.Equal("OCCUPIED", slot.FinalPhysicalState);
        Assert.Equal("LOCKED", slot.LockState);
        Assert.Equal("RESET", slot.UnlockOutputState);
        Assert.Equal("UNKNOWN", settled.OverallOutcome);
        Assert.Equal(unlocksBeforeSettlement, fixture.Io.UnlockCount(0));
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Empty(state.CompletedSlots);
        Assert.Equal(command.SlotOperationAttemptId, state.UnsettledSlotOperationAttemptId);
    }

    /// <summary>
    /// With more than one slot, the slot after the failed one was never reached: it stays NOT_STARTED with an
    /// empty reason even when it happens to read final, the failed one stays UNKNOWN, and the conclusion is
    /// UNKNOWN.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ASettlementLeavesTheSlotsAfterTheFailedOneNotStarted()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);
        fixture.Io.JamLock(0);
        WireToGateOperationExecutionResult first = await fixture.Executor.ExecuteAsync(command, null, token);
        IReadOnlyList<string> failedWith = AssertFailedAtASafeFinishOutsideBothSets(
            first,
            await fixture.Journal.ReadRecoveryStateAsync(token));
        Assert.Equal("NOT_STARTED", first.SlotResults.Single(slot => slot.SlotNo == 2).Outcome);
        fixture.Io.UnjamLock(0);
        fixture.Io.CloseDoor(0, cargo: true);
        fixture.Io.CloseDoor(1, cargo: true);

        WireToGateOperationExecutionResult settled = await fixture.Executor.SettleInterruptedAsync(token);

        WireToGateSlotExecutionResult failed = settled.SlotResults.Single(slot => slot.SlotNo == 1);
        Assert.Equal("UNKNOWN", failed.Outcome);
        Assert.Equal(failedWith, failed.ReasonCodes);
        WireToGateSlotExecutionResult neverReached = settled.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal("NOT_STARTED", neverReached.Outcome);
        Assert.Empty(neverReached.ReasonCodes);
        Assert.Equal("UNKNOWN", settled.OverallOutcome);
        Assert.Equal(0, fixture.Io.UnlockCount(1));
    }

    /// <summary>
    /// The #172 residual. An occupancy conflict is journaled at the safe finish, and the process exits before
    /// the refusal is recorded pending or sent. The settlement had nothing to say why: every slot NOT_STARTED,
    /// every reason empty, SLOT_OPERATION_CONFLICT lost. A slot never opened keeps the reason the journal holds
    /// for it -- the conflict slot is the one that stopped this attempt, so the reason is that slot's -- while
    /// its physical fields are read afresh. The conflict slot reads final and was never opened, so it must not
    /// be counted as this attempt's work either.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ASettledOccupancyConflictKeepsItsReason()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        fixture.Io.CloseDoor(1, cargo: true);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);
        WireToGateOperationExecutionResult refused = await fixture.Executor.ExecuteAsync(command, null, token);
        Assert.Equal("SAFE_FINISH_REACHED", refused.JournalCheckpoint);
        Assert.Equal(["SLOT_OPERATION_CONFLICT"], refused.SlotResults.Single(slot => slot.SlotNo == 2).ReasonCodes);

        WireToGateOperationExecutionResult settled = await fixture.Executor.SettleInterruptedAsync(token);

        WireToGateSlotExecutionResult untouched = settled.SlotResults.Single(slot => slot.SlotNo == 1);
        Assert.Equal("NOT_STARTED", untouched.Outcome);
        Assert.Empty(untouched.ReasonCodes);
        WireToGateSlotExecutionResult conflict = settled.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal("NOT_STARTED", conflict.Outcome);
        Assert.Equal(["SLOT_OPERATION_CONFLICT"], conflict.ReasonCodes);
        Assert.Equal("OCCUPIED", conflict.FinalPhysicalState);
        Assert.Equal("UNKNOWN", settled.OverallOutcome);
        Assert.Equal(0, fixture.Io.UnlockCount(0));
        Assert.Equal(0, fixture.Io.UnlockCount(1));
    }

    /// <summary>
    /// The shape the two-set test misses, checked here so a change in how the executor journals this failure
    /// shows up as a broken premise rather than as a settlement that suddenly passes. Returns the reason the
    /// slot failed with, as journaled.
    /// </summary>
    private static IReadOnlyList<string> AssertFailedAtASafeFinishOutsideBothSets(
        WireToGateOperationExecutionResult first,
        WireToGateRecoveryState journaled)
    {
        Assert.Equal("UNKNOWN", first.OverallOutcome);
        Assert.Equal("SAFE_FINISH_REACHED", first.JournalCheckpoint);
        Assert.Empty(journaled.ActiveUnlockSlots);
        Assert.Empty(journaled.CompletedSlots);
        WireToGateSlotExecutionResult failed = journaled.SlotResults.Single(slot => slot.SlotNo == 1);
        Assert.Equal("UNKNOWN", failed.Outcome);
        // A lock that never confirms the unlock times out waiting for it, and MapFailureReason turns that
        // timeout into this code today. Pinned so the reason the settlement carries is known to be a real one;
        // the settlement assertions compare with whatever the journal holds.
        Assert.Equal(["ACTION_NOT_ALLOWED_IN_STATE"], failed.ReasonCodes);
        return failed.ReasonCodes;
    }
}
