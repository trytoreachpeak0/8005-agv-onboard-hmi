using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// A target slot already at the occupancy the command is meant to produce (8005-agv-onboard-hmi#172).
/// Nothing is opened, but the demand cannot go on, so the refusal has to leave the attempt where the
/// recovery entries find it -- and a later resume must not take that slot's reading as its own work.
/// </summary>
public sealed partial class WireToGateSlotOperationExecutorTests
{
    private const string ConflictRecoverySessionId = "44444444-4444-4444-8444-444444444444";
    private const string ConflictRecoveryActionId = "55555555-5555-4555-8555-555555555555";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AnOccupancyConflictOverSafeSlotsIsJournaledAsTheAttemptsSafeFinish()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        fixture.Io.CloseDoor(1, cargo: true);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(command, null, token);

        // The report is the refusal's, unchanged: every slot NOT_STARTED with its real readings, the
        // reason only on the slot that has it.
        Assert.Equal("FAILED", result.OverallOutcome);
        Assert.All(result.SlotResults, slot => Assert.Equal("NOT_STARTED", slot.Outcome));
        Assert.Empty(result.SlotResults[0].ReasonCodes);
        Assert.Equal(["SLOT_OPERATION_CONFLICT"], result.SlotResults[1].ReasonCodes);
        Assert.Equal(0, fixture.Io.UnlockCount(0));
        Assert.Equal(0, fixture.Io.UnlockCount(1));

        // What changed: the checkpoint is not NONE, so the caller records the result as pending, and
        // the journal holds this attempt as the unsettled one -- the subject every recovery entry
        // looks for.
        Assert.Equal("SAFE_FINISH_REACHED", result.JournalCheckpoint);
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(command.SlotOperationAttemptId, state.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, state.ProvenRecoveryCheckpoint);
        Assert.Equal(command.SlotOperationAttemptId, state.OperationContext?.SlotOperationAttemptId);
        Assert.Equal(command.DemandId, state.OperationContext?.DemandId);
        Assert.Empty(state.ActiveUnlockSlots);
        Assert.Empty(state.CompletedSlots);
        Assert.Equal(
            result.SlotResults.Select(slot => $"{slot.SlotNo}:{slot.Outcome}:{string.Join(",", slot.ReasonCodes)}"),
            state.SlotResults.Select(slot => $"{slot.SlotNo}:{slot.Outcome}:{string.Join(",", slot.ReasonCodes)}"));

        // The caller's next step (WireToGateBusinessService) has to succeed against this journal.
        await fixture.Executor.RecordPendingResultAsync(
            command.SlotOperationAttemptId,
            new WireToGatePendingResult(
                "OperationResult",
                command.SlotOperationAttemptId,
                command.SlotOperationAttemptId,
                new string('a', 64)),
            token);
    }

    /// <summary>
    /// The direction the safe-finish claim must not reach. A conflict is journaled as a safe finish
    /// only because the precheck looks at every slot's safety before any slot's occupancy. Slot 1 is the
    /// conflict and comes first; slot 2 fails one safety condition. A precheck that stops at the first
    /// problem reports the conflict on slot 1 and never reads slot 2 -- and the executor would then
    /// journal a safe finish over a live unlock output, an open door or a slot it cannot read.
    /// </summary>
    /// <remarks>
    /// One case per condition in the first pass, because each can be moved behind the occupancy check on
    /// its own: moving only <c>LOCK_NOT_CLOSED</c> left a single-condition version of this test green
    /// (review of PR #184). "Unknown" has three ways in -- a null unlock output, a null lock feedback, a
    /// null light curtain. The first two are also refused by the output and lock checks, so only the
    /// third needs <c>IsKnown</c> itself, and that is the case given here.
    /// </remarks>
    [Theory]
    [InlineData("UNLOCK_OUTPUT_NOT_RESET")]
    [InlineData("LOCK_NOT_CLOSED")]
    [InlineData("SLOT_STATE_UNKNOWN")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AConflictBesideAnUnsafeSlotClaimsNoSafeFinishAndJournalsNothing(string unsafeReason)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        fixture.Io.CloseDoor(0, cargo: true);
        switch (unsafeReason)
        {
            case "UNLOCK_OUTPUT_NOT_RESET":
                fixture.Io.CloseDoorWithUnlockOutputStuckActive(1, cargo: false);
                break;
            case "LOCK_NOT_CLOSED":
                fixture.Io.OpenDoor(1);
                break;
            case "SLOT_STATE_UNKNOWN":
                fixture.Io.LoseOccupancyReading(1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(unsafeReason), unsafeReason, null);
        }

        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(command, null, token);

        Assert.Equal("FAILED", result.OverallOutcome);
        Assert.Equal("NONE", result.JournalCheckpoint);
        Assert.Equal(["SLOT_OPERATION_CONFLICT"], result.SlotResults[0].ReasonCodes);
        Assert.Equal([unsafeReason], result.SlotResults[1].ReasonCodes);
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Null(state.UnsettledSlotOperationAttemptId);
        Assert.Null(state.OperationContext);
        Assert.Equal(0, fixture.Io.UnlockCount(0));
        Assert.Equal(0, fixture.Io.UnlockCount(1));
    }

    [Theory]
    [InlineData(OperationType.Load)]
    [InlineData(OperationType.Unload)]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeDoesNotCountASlotThisAttemptNeverOpenedAsCompleted(OperationType operationType)
    {
        // The slot read final before the command came -- loaded by nobody under this attempt, or
        // emptied by nobody -- and still does. Before #172 the resume saw a final reading, recorded
        // COMPLETED and reported a load (or unload) that never happened.
        CancellationToken token = TestContext.Current.CancellationToken;
        bool expectedOccupied = operationType == OperationType.Load;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        fixture.Io.CloseDoor(0, cargo: expectedOccupied);
        WireToGateSlotOperationCommand command = CreateCommand(operationType, [1], expectedOccupied);
        WireToGateOperationExecutionResult first = await fixture.Executor.ExecuteAsync(command, null, token);
        Assert.Equal("SAFE_FINISH_REACHED", first.JournalCheckpoint);
        WireToGateRecoveryState journaled = await ArmResumeAsync(fixture);

        WireToGateResumeNotStartedException error = await Assert.ThrowsAsync<WireToGateResumeNotStartedException>(
            () => fixture.Executor.ResumeAsync(ResumeOf(command), null, token));

        Assert.Equal("SLOT_OPERATION_CONFLICT", error.Message);
        Assert.Equal(0, fixture.Io.UnlockCount(0));
        // Refused before its first journal write: the attempt is still the refusal's, not a completion.
        WireToGateRecoveryState after = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(journaled.ProvenRecoveryCheckpoint, after.ProvenRecoveryCheckpoint);
        Assert.Empty(after.CompletedSlots);
        Assert.All(after.SlotResults, slot => Assert.Equal("NOT_STARTED", slot.Outcome));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeStillCountsTheSlotThatFailedAtASafeFinishOnceItReadsFinal()
    {
        // The other side of the narrowing. The slot whose lock never confirmed the unlock failed at a
        // safe finish: journaled UNKNOWN, outside the active set and outside the completed ones. It was
        // opened by this attempt, so when it reads final at the resume it is this attempt's work and is
        // reused without another pulse -- a narrowing on the active and completed sets alone refuses it
        // as a conflict and leaves the resume with no way to finish.
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        fixture.Io.JamLock(0);
        WireToGateOperationExecutionResult first = await fixture.Executor.ExecuteAsync(command, null, token);
        Assert.Equal("UNKNOWN", first.OverallOutcome);
        Assert.Equal("SAFE_FINISH_REACHED", first.JournalCheckpoint);
        WireToGateRecoveryState journaled = await ArmResumeAsync(fixture);
        Assert.Empty(journaled.ActiveUnlockSlots);
        Assert.Empty(journaled.CompletedSlots);
        Assert.Equal("UNKNOWN", journaled.SlotResults.Single().Outcome);
        fixture.Io.UnjamLock(0);
        fixture.Io.CloseDoor(0, cargo: true);
        int unlocksBeforeResume = fixture.Io.UnlockCount(0);

        WireToGateOperationExecutionResult resumed = await fixture.Executor.ResumeAsync(ResumeOf(command), null, token);

        Assert.Equal("COMPLETED", resumed.OverallOutcome);
        Assert.Equal("COMPLETED", resumed.SlotResults.Single().Outcome);
        Assert.Equal(unlocksBeforeResume, fixture.Io.UnlockCount(0));
    }

    private static async Task<WireToGateRecoveryState> ArmResumeAsync(ScriptedFixture fixture)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
        await fixture.Journal.UpdateRecoveryStateAsync(
            _ => state with
            {
                ExceptionRecoverySessionId = ConflictRecoverySessionId,
                RecoveryActionId = ConflictRecoveryActionId
            },
            token);
        return await fixture.Journal.ReadRecoveryStateAsync(token);
    }

    private static WireToGateSlotOperationResumeCommand ResumeOf(WireToGateSlotOperationCommand command) =>
        new(
            "66666666-6666-4666-8666-666666666666",
            2,
            DateTimeOffset.UtcNow,
            ConflictRecoverySessionId,
            ConflictRecoveryActionId,
            command.DemandId,
            command.SlotOperationAttemptId,
            WireToGateRecoveryCheckpoint.SafeFinishReached,
            command.Slots,
            command.CommandContentSha256);
}
