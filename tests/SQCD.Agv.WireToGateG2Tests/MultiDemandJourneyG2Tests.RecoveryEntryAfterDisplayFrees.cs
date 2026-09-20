using SQCD.Agv.Application;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// What happens to the recovery entry that onboard-hmi#152 withheld: the attempt whose result was
/// never acknowledged still has to reach the operator once the command that had the doors is done
/// with them (batch 7-156, <c>trytoreachpeak0/8005-agv-onboard-hmi#156</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this guards.</b> <c>RestorePendingRecoveryOperationProjectionAsync</c> claims "this
/// recovery has been announced once in this process" before it works out whether the display is
/// free. On the branch onboard-hmi#152 added -- another attempt holds the screen, so the line goes
/// out without its snapshot -- the claim is spent all the same and the snapshot is dropped. Every
/// later readiness stops at the claim, so A's recovery entry never appears again in this process.
/// onboard-hmi#152 traded a wrong display for no display.
/// </para>
/// <para>
/// <b>Why "the display frees up" cannot be read off the owner field.</b>
/// <c>_operationDisplayOwnerAttemptId</c> is written when a command takes the doors and never
/// cleared -- <c>ReleaseOperationDisplay</c> deliberately leaves it where it is (onboard-hmi#146).
/// Once B has taken the display, <c>NoOtherAttemptOwnsOperationDisplay(A)</c> is false for the rest
/// of the process: not null, and not A. So simply withholding the claim until a snapshot is carried
/// would leave the entry just as invisible; the moment the doors actually come free is a separate
/// fact, and this test is about the entry arriving at it.
/// </para>
/// <para>
/// <b>The interleaving is made, not raced for</b>, the same way and for the same reason as in
/// <c>MultiDemandJourneyG2Tests.RecoveryProjectionDisplay</c>: <c>RestoreWindowJournal</c> holds B's
/// <c>Prepared</c> write until A's restore has read the journal, so the restore is looking at A
/// while B is at the doors.
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>The line B publishes once its own result has been acknowledged.</summary>
    private const string BResultAcceptedLine = "5号仓操作结果已被服务端确认。";

    /// <summary>
    /// A's result acknowledgement is lost while B has the doors, so A's restore publishes its line
    /// without a snapshot (onboard-hmi#152). B then finishes and gives the display up, and A's
    /// recovery entry reaches the screen -- once, and worded as this run's own.
    /// </summary>
    [Fact]
    public async Task AWithheldRecoveryEntryReachesTheOperatorOnceTheRunningCommandHasFinished()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true, KeepSnapshotFresh = true };
        RestoreWindowJournal? window = null;
        await using Harness harness = await StartTwoDemandStopAsync(
            io,
            token,
            // A's OperationResult and the restore's resend of it are both taken and never answered;
            // B's, sent later, is acknowledged, so B settles and releases the display for good.
            server => server.OperationResultAcksToDrop = 2,
            wrapJournal: inner => window = new RestoreWindowJournal(inner, AttemptA, AttemptB));
        using ReleaseOnExit releaseHeldWrite = new(() => window!.Release());

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator
                && harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptA
                && io.UnlockCount == 1,
            "A's slot to be unlocked and waiting on the operator",
            token);

        await SendSlotCommandAsync(harness, DemandB, AttemptB, [5]);
        await WaitForBothCommandsToReachTheVehicleAsync(harness, token);

        io.SetUnreadable(0);
        await harness.WaitUntilAsync(
            () => window!.PreparedWriteHeld,
            "B to reach its own Prepared write",
            token);
        // Two waits, not one: A's DurableAck timeout alone is two seconds of the harness's five-second
        // budget, and the restore only starts after it.
        await harness.WaitUntilAsync(
            () => OperatorLog(harness).Contains(ResultAckPendingLine),
            "A's acknowledgement to be given up on",
            token);
        await harness.WaitUntilAsync(
            () => window!.RestoreHasReadTheLeftover,
            "A's restore to read the journal while A is still the unsettled attempt",
            token);

        io.CloseDoor(0, cargo: true);
        window!.Release();
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentExpectedActionWait?.SlotOperationAttemptId == AttemptB
                && io.UnlockCount == 2,
            "B to take the doors and open its own",
            token);

        // onboard-hmi#152 still holds here: the line is out, the snapshot is not.
        await harness.WaitUntilAsync(
            () => OperatorLog(harness).Contains(RecoveryRestoredLine),
            "A's restored recovery line to reach the operator",
            token);
        Assert.Null(harness.Events.Single(item => item.Message == RecoveryRestoredLine).Operation);

        // The operator loads B's basket and shuts its door: B reports, is acknowledged, and is done
        // with the display.
        io.CloseDoor(4, cargo: true);
        await harness.WaitUntilAsync(
            () => OperatorLog(harness).Contains(BResultAcceptedLine),
            "B's result to be acknowledged",
            token);

        // The thing this ticket is about. A is still the unsettled attempt and still needs an
        // administrator, and nothing is at the doors any more, so the entry the operator acts from
        // has to be on screen.
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptA
                && harness.Business.CurrentOperationSnapshot?.Stage
                    == WireToGateHmiOperationStage.RecoveryRequired,
            "A's recovery entry to reach the screen once B has finished with the display",
            token);

        WireToGateHmiOperationSnapshot carried = harness.Business.CurrentOperationSnapshot!;
        Assert.Equal([1], carried.Slots);
        Assert.Equal(OperationType.Load, carried.OperationType);

        // Asserted through the view model, because that is where the entry is offered: MainViewModel
        // gates CanRequestWireToGateRecovery on the current operation's stage, and the banner it
        // builds from the same snapshot is what the operator reads.
        await harness.WaitUntilAsync(
            () => harness.ViewModel.StateText == "需要恢复"
                && harness.ViewModel.Guidance == RecoveryRestoredLine,
            "the screen to read A's recovery guidance",
            token);

        // Said once, not once per time the doors come free.
        Assert.Single(OperatorLog(harness), line => line == RecoveryRestoredLine);
        AssertNotRefused(harness);
        Assert.Empty(harness.UiErrors);
    }
}
