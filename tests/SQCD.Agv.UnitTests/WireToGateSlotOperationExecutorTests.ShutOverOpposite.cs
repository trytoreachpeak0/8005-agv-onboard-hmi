using SQCD.Agv.Application;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The executor's half of 「重新开门」 (8005-agv-onboard-hmi#385, CP-0019): a door shut over the opposite occupancy is held
/// shut and nothing is pulsed until the operator presses; a pressed reopen whose lock does not release, or releases and
/// falls back, over a slot read shut, locked and reset is a door held or shut again -- back to waiting for the press,
/// not decision 2. The G2 replays of the two field occasions are <c>DoorShutOverOppositeOccupancyG2Tests</c>; these
/// cover the branches of the wait itself.
/// </summary>
public sealed partial class WireToGateSlotOperationExecutorTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task NothingIsPulsedWhileTheSlotIsHeldShutForThePressAndThePromptRepeats()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            token,
            operationTimeout: TimeSpan.FromMilliseconds(300));
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        List<WireToGateOperationProgress> held = [];
        WireToGateOperatorRequestWait? seen = null;

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    held.Add(progress);
                    seen = fixture.Executor.OperatorRequestWait;
                    if (held.Count == 3)
                    {
                        Assert.Equal(1, fixture.Io.UnlockCount(0));
                        PressReopenWhenAsked(fixture.Executor, command, progress);
                    }
                }
                else if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(0, cargo: fixture.Io.UnlockCount(0) >= 2);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(2, fixture.Io.UnlockCount(0));
        Assert.Equal([1, 2, 3], held.Select(progress => progress.PromptRound));
        Assert.All(held, progress => Assert.Equal([1], progress.Active));
        Assert.Equal(new WireToGateOperatorRequestWait(command.SlotOperationAttemptId, 1, WireToGatePromptCause.ShutOverOpposite), seen);
        Assert.Null(fixture.Executor.OperatorRequestWait);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task OnlyTheFirstPressOfAWaitIsTakenAndOnlyForTheSlotAndAttemptHeld()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        Assert.False(fixture.Executor.SubmitOperatorRequest(command.SlotOperationAttemptId, 1, WireToGateOperatorSlotRequest.Reopen));
        List<bool> answers = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    answers.Add(fixture.Executor.SubmitOperatorRequest(Guid.NewGuid().ToString("D"), 1, WireToGateOperatorSlotRequest.Reopen));
                    answers.Add(fixture.Executor.SubmitOperatorRequest(command.SlotOperationAttemptId, 2, WireToGateOperatorSlotRequest.Reopen));
                    answers.Add(fixture.Executor.SubmitOperatorRequest(command.SlotOperationAttemptId, 1, WireToGateOperatorSlotRequest.Reopen));
                    answers.Add(fixture.Executor.SubmitOperatorRequest(command.SlotOperationAttemptId, 1, WireToGateOperatorSlotRequest.Reopen));
                }
                else if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(0, cargo: fixture.Io.UnlockCount(0) >= 2);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal([false, false, true, false], answers);
        Assert.Equal(2, fixture.Io.UnlockCount(0));
    }

    /// <summary>
    /// The agv02 shape (10-21): the pressed reopen's lock does not release over a slot read shut, locked and reset. Back to
    /// waiting for the press under <see cref="WireToGatePromptCause.ReopenNotReleased"/>; the next press opens it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task APressedReopenWhoseLockDoesNotReleaseWaitsForTheNextPress()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        List<WireToGatePromptCause> causes = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                causes.Add(progress.Cause);
                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    fixture.Io.JamLock(0);
                    PressReopenWhenAsked(fixture.Executor, command, progress);
                }
                else if (progress.Cause == WireToGatePromptCause.ReopenNotReleased)
                {
                    fixture.Io.UnjamLock(0);
                    PressReopenWhenAsked(fixture.Executor, command, progress);
                }
                else if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(0, cargo: fixture.Io.UnlockCount(0) >= 3);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Empty(Assert.Single(result.SlotResults).ReasonCodes);
        Assert.Equal(3, fixture.Io.UnlockCount(0));
        Assert.Contains(WireToGatePromptCause.ReopenNotReleased, causes);
    }

    /// <summary>
    /// Decision 2 is not relaxed: after a pressed reopen whose lock did not release, an output that does not reset is
    /// still UNKNOWN, and says <c>UNLOCK_OUTPUT_NOT_RESET</c>.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task APressedReopenWhoseLockDoesNotReleaseAndWhoseOutputStaysOnIsUnknown()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            token,
            unlockOutputResetTimeout: TimeSpan.FromMilliseconds(200));
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    fixture.Io.JamLock(0);
                    fixture.Io.StickUnlockOutput(0);
                    PressReopenWhenAsked(fixture.Executor, command, progress);
                }
                else if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(0, cargo: false);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults);
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal(["UNLOCK_OUTPUT_NOT_RESET"], slot.ReasonCodes);
        Assert.Equal("ACTIVE", slot.UnlockOutputState);
        Assert.Equal(2, fixture.Io.UnlockCount(0));
    }

    /// <summary>
    /// A lock that reads released without a pulse while the slot is held for the press: the door was not latched, and it
    /// is open. Nothing is pulsed; the run waits for it to be shut again, as after any pulse.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ALockReadReleasedWhileHeldIsAnOpenDoorAndIsNotPulsed()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        List<WireToGatePromptCause> causes = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                causes.Add(progress.Cause);
                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    fixture.Io.OpenDoor(0);
                }
                else if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(0, cargo: causes.Contains(WireToGatePromptCause.ShutOverOpposite));
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Equal(
            WireToGatePromptCause.PromptCadence,
            causes[causes.IndexOf(WireToGatePromptCause.ShutOverOpposite) + 1]);
    }

    /// <summary>
    /// A fatal fault latched while the slot is held for the press ends the run at once as the latch's refusal of the
    /// reopen -- FAILED with the latch's reason, no pulse -- rather than waiting on a press nobody may act on: the latch
    /// cannot be lifted while an operation is in flight (8005-agv-onboard-hmi#191).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AFatalFaultLatchedWhileHeldEndsTheRunAsTheLatchRefusal()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        bool latched = false;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            token,
            fatalFaultLatched: () => Volatile.Read(ref latched));
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    Volatile.Write(ref latched, true);
                }
                else if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(0, cargo: false);
                }

                return Task.CompletedTask;
            },
            token);

        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults);
        Assert.Equal("FAILED", slot.Outcome);
        Assert.Equal([WireToGateSlotOperationExecutor.FatalFaultLatchedReason], slot.ReasonCodes);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Null(fixture.Executor.OperatorRequestWait);
    }

    /// <summary>
    /// A load cancellation taking the attempt over while the slot is held for the press stops the run without a pulse,
    /// and no press is taken after it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnAbortWhileHeldForThePressStopsWithoutAPulse()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<WireToGateOperationExecutionResult> run = fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    held.TrySetResult();
                }
                else if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(0, cargo: false);
                }

                return Task.CompletedTask;
            },
            token);
        await held.Task.WaitAsync(token);

        Assert.True(await fixture.Executor.AbortOperationAsync(command.SlotOperationAttemptId, token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(fixture.Executor.SubmitOperatorRequest(command.SlotOperationAttemptId, 1, WireToGateOperatorSlotRequest.Reopen));
        Assert.Null(fixture.Executor.OperatorRequestWait);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
    }

    /// <summary>
    /// A slot that turns unreadable while it is held for the press -- here the light curtain stops reading -- is decision 2
    /// at once: UNKNOWN under <c>SLOT_STATE_UNKNOWN</c>, not a prompt about an empty slot until somebody presses or cancels
    /// (review M1 of PR #390, the reviewer's probe).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ASlotThatTurnsUnreadableWhileHeldIsUnknown()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    fixture.Io.LoseOccupancyReading(0);
                }
                else if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(0, cargo: false);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal(["SLOT_STATE_UNKNOWN"], Assert.Single(result.SlotResults).ReasonCodes);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Null(fixture.Executor.OperatorRequestWait);
    }

    /// <summary>
    /// A slot fault declaration on the slot held for the press is applied: the run settles it UNKNOWN under the
    /// declaration's reason, with no pulse after the first (the reviewer's probe of PR #390).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task ADeclarationOnTheSlotHeldForThePressIsApplied()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<WireToGateOperationExecutionResult> run = fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    held.TrySetResult();
                }
                else if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(0, cargo: false);
                }

                return Task.CompletedTask;
            },
            token);
        await held.Task.WaitAsync(token);

        WireToGateSlotFaultDeclarationOutcome outcome = await fixture.Executor.DeclareSlotFaultAsync(
            Declaration(command, 1),
            () => Task.CompletedTask,
            token);
        WireToGateOperationExecutionResult result = await run.WaitAsync(token);

        Assert.Equal(WireToGateSlotFaultDeclarationOutcome.Applied, outcome);
        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal(
            [WireToGateSlotOperationExecutor.SlotFaultDeclaredReason],
            Assert.Single(result.SlotResults).ReasonCodes);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Null(fixture.Executor.OperatorRequestWait);
    }

    /// <summary>
    /// 「重新开门」 pressed while the vehicle is not proven stopped: the press is spent on the hold, nothing is pulsed, and when
    /// the safety fact allows a pulse again the slot is held for a press again -- the button comes back, and the door does
    /// not spring open by itself, maybe long after the person who pressed has gone. Only the next press opens it (review M3
    /// of PR #390, the coordinator's ruling).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task APressSpentOnTheSafetyHoldOpensNothingWhenTheHoldEndsAndTheButtonComesBack()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            token,
            operationTimeout: TimeSpan.FromMilliseconds(300));
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        TaskCompletionSource heldBySafety = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource askedAgain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int shutPrompts = 0;
        WireToGateOperatorRequestWait? waitDuringHold = new("x", 0, WireToGatePromptCause.FirstOpen);
        WireToGateOperatorRequestWait? waitAfterHold = null;
        Task<WireToGateOperationExecutionResult> run = fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Cause == WireToGatePromptCause.ShutOverOpposite && ++shutPrompts == 1)
                {
                    fixture.Gate.Permitted = false;
                    PressReopenWhenAsked(fixture.Executor, command, progress);
                }
                else if (progress.Cause == WireToGatePromptCause.ShutOverOpposite)
                {
                    waitAfterHold = fixture.Executor.OperatorRequestWait;
                    askedAgain.TrySetResult();
                }
                else if (progress.Cause == WireToGatePromptCause.ReopenHeldBySafety)
                {
                    waitDuringHold = fixture.Executor.OperatorRequestWait;
                    heldBySafety.TrySetResult();
                }
                else if (progress.Phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(0, cargo: fixture.Io.UnlockCount(0) >= 2);
                }

                return Task.CompletedTask;
            },
            token);
        await heldBySafety.Task.WaitAsync(token);
        Assert.Null(waitDuringHold);

        fixture.Gate.Permitted = true;
        await askedAgain.Task.WaitAsync(token);
        // A full prompt cadence after the hold ended, still one pulse: nothing opened by itself.
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Equal(
            new WireToGateOperatorRequestWait(command.SlotOperationAttemptId, 1, WireToGatePromptCause.ShutOverOpposite),
            waitAfterHold);
        Assert.False(run.IsCompleted);

        Assert.True(fixture.Executor.SubmitOperatorRequest(
            command.SlotOperationAttemptId,
            1,
            WireToGateOperatorSlotRequest.Reopen));
        WireToGateOperationExecutionResult result = await run.WaitAsync(token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(2, fixture.Io.UnlockCount(0));
    }
}
