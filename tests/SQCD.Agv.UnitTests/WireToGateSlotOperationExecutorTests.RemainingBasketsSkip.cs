using SQCD.Agv.Application;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The executor's half of 「剩下的不装」 (8005-agv-onboard-hmi#388, CP-0021 REQ-0367, protocol 6.0.0 candidate): offered only
/// while the run holds a slot of a multi-basket load shut for 「重新开门」 with at least one slot loaded; a press holds the run
/// -- nothing opens -- until the server's decision; an authorization ends the load with the loaded slots
/// (<c>COMPLETED_REMAINING_SKIPPED</c>), a rejection goes back to waiting for the press. The G2 shapes are in
/// <c>RemainingBasketsSkipG2Tests</c>; these cover the branches of the hold and the settlement.
/// </summary>
public sealed partial class WireToGateSlotOperationExecutorTests
{
    private const string SkipRequestId = "77777777-7777-4777-8777-777777777777";

    /// <summary>
    /// Not while the first slot's door is open, and not over the first slot shut empty (k = 0); offered over the second slot
    /// shut empty once the first holds its basket, naming exactly the loaded slot -- the 10-21 agv03 @160 shape.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-NOTHING-LOADED")]
    public async Task TheSkipIsOfferedOnlyOverAHeldSlotOnceABasketIsLoaded()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);
        List<WireToGateRemainingBasketsSkipOffer?> seen = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                seen.Add(fixture.Executor.RemainingBasketsSkipOffer);
                if (progress.Phase != "WAITING_OPERATOR")
                {
                    return Task.CompletedTask;
                }

                if (progress.Cause is WireToGatePromptCause.ShutOverOpposite)
                {
                    PressReopenWhenAsked(fixture.Executor, command, progress);
                }
                else if (progress.Active.Single() == 1)
                {
                    // Slot 1: shut empty first, loaded after the press.
                    fixture.Io.CloseDoor(0, cargo: fixture.Io.UnlockCount(0) >= 2);
                }
                else
                {
                    // Slot 2: shut empty once, then loaded after the press.
                    fixture.Io.CloseDoor(1, cargo: fixture.Io.UnlockCount(1) >= 2);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        WireToGateRemainingBasketsSkipOffer offer = Assert.Single(seen.OfType<WireToGateRemainingBasketsSkipOffer>().Distinct());
        Assert.Equal(command.SlotOperationAttemptId, offer.SlotOperationAttemptId);
        Assert.Equal(command.DemandId, offer.DemandId);
        Assert.Equal(command.OperationSessionId, offer.OperationSessionId);
        Assert.Equal(2, offer.ExpectedBasketCount);
        Assert.Equal([1], offer.LoadedSlots);
        Assert.Equal(2, offer.HeldSlot);
        Assert.Null(fixture.Executor.RemainingBasketsSkipOffer);
    }

    /// <summary>A single-basket load shut empty is offered 「重新开门」 and never the skip.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-NOTHING-LOADED")]
    public async Task ASingleBasketLoadIsNeverOfferedTheSkip()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        List<WireToGateRemainingBasketsSkipOffer?> seen = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                seen.Add(fixture.Executor.RemainingBasketsSkipOffer);
                if (!PressReopenWhenAsked(fixture.Executor, command, progress) && progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(0, cargo: fixture.Io.UnlockCount(0) >= 2);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.All(seen, Assert.Null);
        Assert.False(fixture.Executor.HoldForRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]));
    }

    /// <summary>
    /// The authorized skip: the load ends at once, the loaded slot <c>COMPLETED</c>, the slot opened and shut empty and the slot
    /// never opened both <c>NOT_STARTED</c>, <c>EMPTY</c>, <c>LOCKED</c>, <c>RESET</c> with <c>REMAINING_BASKETS_SKIPPED</c>; no
    /// slot is opened after the press, and the journal ends at the safe finish with the loaded slot completed.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    public async Task AnAuthorizedSkipEndsTheLoadWithTheLoadedSlotsAndOpensNothingMore()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2, 3], expectedOccupied: true);
        List<WireToGatePromptCause> causes = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                causes.Add(progress.Cause);
                if (progress.Phase != "WAITING_OPERATOR")
                {
                    return Task.CompletedTask;
                }

                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    Assert.True(fixture.Executor.HoldForRemainingBasketsSkip(
                        command.SlotOperationAttemptId,
                        SkipRequestId,
                        [1]));
                }
                else if (progress.Cause == WireToGatePromptCause.RemainingBasketsSkipPending)
                {
                    Assert.True(fixture.Executor.DecideRemainingBasketsSkip(
                        command.SlotOperationAttemptId,
                        SkipRequestId,
                        [1]));
                }
                else
                {
                    int slotIndex = progress.Active.Single() - 1;
                    fixture.Io.CloseDoor(slotIndex, cargo: slotIndex == 0);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("COMPLETED_REMAINING_SKIPPED", result.OverallOutcome);
        Assert.Equal("SAFE_FINISH_REACHED", result.JournalCheckpoint);
        WireToGateSlotExecutionResult loaded = result.SlotResults.Single(slot => slot.SlotNo == 1);
        Assert.Equal("COMPLETED", loaded.Outcome);
        Assert.Equal("OCCUPIED", loaded.FinalPhysicalState);
        Assert.Empty(loaded.ReasonCodes);
        foreach (int skipped in new[] { 2, 3 })
        {
            WireToGateSlotExecutionResult slot = result.SlotResults.Single(item => item.SlotNo == skipped);
            Assert.Equal("NOT_STARTED", slot.Outcome);
            Assert.Equal("EMPTY", slot.FinalPhysicalState);
            Assert.Equal("LOCKED", slot.LockState);
            Assert.Equal("RESET", slot.UnlockOutputState);
            Assert.Equal(["REMAINING_BASKETS_SKIPPED"], slot.ReasonCodes);
        }

        Assert.Equal(1, fixture.Io.UnlockCount(1));
        Assert.Equal(0, fixture.Io.UnlockCount(2));
        Assert.Contains(WireToGatePromptCause.RemainingBasketsSkipPending, causes);
        Assert.Null(fixture.Executor.RemainingBasketsSkipOffer);

        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(command.SlotOperationAttemptId, state.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, state.ProvenRecoveryCheckpoint);
        Assert.Empty(state.ActiveUnlockSlots);
        Assert.Equal([1], state.CompletedSlots);
    }

    /// <summary>
    /// While the press waits for the server nothing opens: 「重新开门」 is refused, the pending prompt repeats on the cadence and
    /// the slot is not pulsed; a second hold of the same wait is refused.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    public async Task NothingOpensWhileTheSkipWaitsForTheServer()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            token,
            operationTimeout: TimeSpan.FromMilliseconds(200));
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);
        List<int> pendingRounds = [];
        List<bool> refused = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Phase != "WAITING_OPERATOR")
                {
                    return Task.CompletedTask;
                }

                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    Assert.True(fixture.Executor.HoldForRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]));
                }
                else if (progress.Cause == WireToGatePromptCause.RemainingBasketsSkipPending)
                {
                    pendingRounds.Add(progress.PromptRound);
                    refused.Add(fixture.Executor.SubmitOperatorRequest(
                        command.SlotOperationAttemptId,
                        2,
                        WireToGateOperatorSlotRequest.Reopen));
                    refused.Add(fixture.Executor.HoldForRemainingBasketsSkip(
                        command.SlotOperationAttemptId,
                        "88888888-8888-4888-8888-888888888888",
                        [1]));
                    Assert.Null(fixture.Executor.RemainingBasketsSkipOffer);
                    Assert.Equal(SkipRequestId, fixture.Executor.HeldRemainingBasketsSkipRequestId);
                    if (pendingRounds.Count == 3)
                    {
                        Assert.Equal(1, fixture.Io.UnlockCount(1));
                        Assert.True(fixture.Executor.DecideRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]));
                    }
                }
                else
                {
                    int slotIndex = progress.Active.Single() - 1;
                    fixture.Io.CloseDoor(slotIndex, cargo: slotIndex == 0);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("COMPLETED_REMAINING_SKIPPED", result.OverallOutcome);
        Assert.Equal(3, pendingRounds.Count);
        Assert.Equal(pendingRounds.Order(), pendingRounds);
        Assert.Equal(pendingRounds.Distinct().Count(), pendingRounds.Count);
        Assert.All(refused, Assert.False);
        Assert.Equal(1, fixture.Io.UnlockCount(1));
        Assert.Null(fixture.Executor.HeldRemainingBasketsSkipRequestId);
    }

    /// <summary>
    /// A rejected (or dropped) skip lets go of the hold: the run goes back to waiting for 「重新开门」, which opens the slot
    /// again, and the load completes normally; a later press of the skip would be a new request.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-REJECTED")]
    public async Task ARejectedSkipGoesBackToWaitingForThePress()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);
        int shutOverOppositePrompts = 0;
        bool held = false;

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Phase != "WAITING_OPERATOR")
                {
                    return Task.CompletedTask;
                }

                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    shutOverOppositePrompts++;
                    if (!held)
                    {
                        held = fixture.Executor.HoldForRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]);
                        Assert.True(held);
                    }
                    else
                    {
                        Assert.NotNull(fixture.Executor.RemainingBasketsSkipOffer);
                        PressReopenWhenAsked(fixture.Executor, command, progress);
                    }
                }
                else if (progress.Cause == WireToGatePromptCause.RemainingBasketsSkipPending)
                {
                    Assert.True(fixture.Executor.DecideRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, null));
                    Assert.False(fixture.Executor.DecideRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]));
                }
                else
                {
                    int slotIndex = progress.Active.Single() - 1;
                    fixture.Io.CloseDoor(slotIndex, cargo: slotIndex == 0 || fixture.Io.UnlockCount(1) >= 2);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.All(result.SlotResults, slot => Assert.Equal("COMPLETED", slot.Outcome));
        Assert.Equal(2, shutOverOppositePrompts);
        Assert.Equal(2, fixture.Io.UnlockCount(1));
    }

    /// <summary>
    /// A hold is taken only over the run's own attempt, while it waits for the press, for exactly its loaded slots; a decision
    /// only for the request held, and only for the slots that request named.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-UNAUTHORIZED-RESULT")]
    public async Task AHoldAndADecisionAreTakenOnlyForTheirOwnAttemptAndSlots()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);
        Assert.False(fixture.Executor.HoldForRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]));
        List<bool> answers = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Phase != "WAITING_OPERATOR")
                {
                    return Task.CompletedTask;
                }

                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    answers.Add(fixture.Executor.HoldForRemainingBasketsSkip(Guid.NewGuid().ToString("D"), SkipRequestId, [1]));
                    answers.Add(fixture.Executor.HoldForRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1, 2]));
                    answers.Add(fixture.Executor.HoldForRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, []));
                    answers.Add(fixture.Executor.HoldForRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]));
                }
                else if (progress.Cause == WireToGatePromptCause.RemainingBasketsSkipPending)
                {
                    answers.Add(fixture.Executor.DecideRemainingBasketsSkip(
                        command.SlotOperationAttemptId,
                        "88888888-8888-4888-8888-888888888888",
                        [1]));
                    answers.Add(fixture.Executor.DecideRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1, 2]));
                    answers.Add(fixture.Executor.DecideRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]));
                }
                else
                {
                    int slotIndex = progress.Active.Single() - 1;
                    fixture.Io.CloseDoor(slotIndex, cargo: slotIndex == 0);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("COMPLETED_REMAINING_SKIPPED", result.OverallOutcome);
        Assert.Equal([false, false, false, true, false, false, true], answers);
    }

    /// <summary>
    /// The slots no longer read as the authorization says when the load ends -- here a basket reads in the skipped slot -- so
    /// the vehicle reports what it would without the skip: <c>UNKNOWN</c>, never <c>REMAINING_BASKETS_SKIPPED</c>.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-UNAUTHORIZED-RESULT")]
    public async Task AnAuthorizationOverSlotsThatNoLongerReadSoIsNotReportedAsSkipped()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Phase != "WAITING_OPERATOR")
                {
                    return Task.CompletedTask;
                }

                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    Assert.True(fixture.Executor.HoldForRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]));
                }
                else if (progress.Cause == WireToGatePromptCause.RemainingBasketsSkipPending)
                {
                    fixture.Io.CloseDoor(1, cargo: true);
                    Assert.True(fixture.Executor.DecideRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]));
                }
                else
                {
                    int slotIndex = progress.Active.Single() - 1;
                    fixture.Io.CloseDoor(slotIndex, cargo: slotIndex == 0);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal("COMPLETED", result.SlotResults.Single(slot => slot.SlotNo == 1).Outcome);
        WireToGateSlotExecutionResult off = result.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal("UNKNOWN", off.Outcome);
        Assert.Equal("OCCUPIED", off.FinalPhysicalState);
        Assert.Equal(["RECOVERY_CHECKPOINT_NOT_UNIQUE"], off.ReasonCodes);
        Assert.DoesNotContain(result.SlotResults, slot => slot.ReasonCodes.Contains("REMAINING_BASKETS_SKIPPED"));
    }

    /// <summary>A slot turning unreadable while the skip is pending is decision 2, as in every other wait.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ASlotTurningUnreadableWhileTheSkipIsPendingIsUnknown()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Phase != "WAITING_OPERATOR")
                {
                    return Task.CompletedTask;
                }

                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    Assert.True(fixture.Executor.HoldForRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]));
                }
                else if (progress.Cause == WireToGatePromptCause.RemainingBasketsSkipPending)
                {
                    fixture.Io.LoseLockFeedback(1);
                }
                else
                {
                    int slotIndex = progress.Active.Single() - 1;
                    fixture.Io.CloseDoor(slotIndex, cargo: slotIndex == 0);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal(["SLOT_STATE_UNKNOWN"], result.SlotResults.Single(slot => slot.SlotNo == 2).ReasonCodes);
        Assert.Null(fixture.Executor.HeldRemainingBasketsSkipRequestId);
        Assert.False(fixture.Executor.DecideRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]));
    }

    /// <summary>
    /// A restart with the authorization journaled and no result written: the interrupted settlement reports the skip from the
    /// live IO, without a pulse. With the request still unanswered on file it settles as it always has, UNKNOWN.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-DUPLICATE-REQUEST")]
    public async Task AnInterruptedLoadSettlesAsSkippedOnlyWithTheAuthorizationOnFile(bool authorized)
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);
        await InterruptHeldForTheSkipAsync(fixture, command);
        await fixture.Journal.UpdateRecoveryStateAsync(
            state => state with { RemainingBasketsSkip = SkipOnFile(command, authorized) },
            token);

        WireToGateOperationExecutionResult result = await fixture.Executor.SettleInterruptedAsync(token);

        Assert.Equal(1, fixture.Io.UnlockCount(1));
        Assert.Equal(authorized ? "COMPLETED_REMAINING_SKIPPED" : "UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult second = result.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal(authorized ? "NOT_STARTED" : "UNKNOWN", second.Outcome);
        Assert.Equal(
            authorized ? ["REMAINING_BASKETS_SKIPPED"] : ["RECOVERY_CHECKPOINT_NOT_UNIQUE"],
            second.ReasonCodes);
    }

    /// <summary>
    /// The server's acknowledgement of a skipped result records the load of the k loaded slots as on board, not the command's N:
    /// its unload names only those, with <c>expectedBasketCount</c> k (wire notes, REQ-0352).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    public async Task RecordingASkippedResultPutsOnlyTheLoadedSlotsOnBoard()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);
        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Phase != "WAITING_OPERATOR")
                {
                    return Task.CompletedTask;
                }

                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    fixture.Executor.HoldForRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]);
                }
                else if (progress.Cause == WireToGatePromptCause.RemainingBasketsSkipPending)
                {
                    fixture.Executor.DecideRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]);
                }
                else
                {
                    int slotIndex = progress.Active.Single() - 1;
                    fixture.Io.CloseDoor(slotIndex, cargo: slotIndex == 0);
                }

                return Task.CompletedTask;
            },
            token);
        Assert.Equal("COMPLETED_REMAINING_SKIPPED", result.OverallOutcome);

        Assert.Equal(
            WireToGateResultRecording.Settled,
            await fixture.Executor.MarkResultRecordedAsync(command.SlotOperationAttemptId, token));

        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
        WireToGateLoadOnBoard onBoard = Assert.Single(state.LoadsOnBoard!);
        Assert.Equal([1], onBoard.Load.Slots);
        Assert.Equal(1, onBoard.Load.ExpectedBasketCount);
        Assert.Equal([1], state.LastCompletedLoadOperationContext!.Slots);
        Assert.Null(state.UnsettledSlotOperationAttemptId);
    }

    /// <summary>Loads slot 1, shuts slot 2 empty, holds it for the skip, and then kills the run as a dying process does.</summary>
    private static async Task InterruptHeldForTheSkipAsync(
        ScriptedFixture fixture,
        WireToGateSlotOperationCommand command)
    {
        // Bounded like every other run here: a build whose hold is refused would otherwise wait for the press for ever.
        using CancellationTokenSource interrupt = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        interrupt.CancelAfter(TimeSpan.FromSeconds(10));
        Task<WireToGateOperationExecutionResult> operation = fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Phase != "WAITING_OPERATOR")
                {
                    return Task.CompletedTask;
                }

                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    Assert.True(fixture.Executor.HoldForRemainingBasketsSkip(command.SlotOperationAttemptId, SkipRequestId, [1]));
                }
                else if (progress.Cause == WireToGatePromptCause.RemainingBasketsSkipPending)
                {
                    interrupt.Cancel();
                }
                else
                {
                    int slotIndex = progress.Active.Single() - 1;
                    fixture.Io.CloseDoor(slotIndex, cargo: slotIndex == 0);
                }

                return Task.CompletedTask;
            },
            interrupt.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    private static WireToGateRemainingBasketsSkip SkipOnFile(WireToGateSlotOperationCommand command, bool authorized) =>
        new(
            SkipRequestId,
            command.DemandId,
            command.OperationSessionId,
            command.SlotOperationAttemptId,
            [1],
            command.ExpectedBasketCount,
            null,
            DateTimeOffset.UtcNow)
        {
            Result = authorized
                ? new WireToGateRemainingBasketsSkipOutcome(
                    "99999999-9999-4999-8999-999999999999",
                    new string('a', 64),
                    WireToGateRemainingBasketsSkipOutcome.Authorized,
                    [1],
                    null,
                    DateTimeOffset.UtcNow)
                : null
        };
}
