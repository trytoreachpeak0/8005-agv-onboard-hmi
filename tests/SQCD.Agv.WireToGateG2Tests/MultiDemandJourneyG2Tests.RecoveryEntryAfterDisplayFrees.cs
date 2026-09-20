using SQCD.Agv.Application;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// What happens to the recovery entry that onboard-hmi#152 withheld: the attempt whose result was
/// never acknowledged still has to reach the operator once the command that had the doors is done
/// with them -- and only then, only once, and only if it is still the recovery the operator has to
/// make (batch 7-156, <c>trytoreachpeak0/8005-agv-onboard-hmi#156</c>).
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
/// of the process: not null, and not A. So withholding the claim until a snapshot is carried would
/// leave the entry just as invisible; the moment the doors actually come free is a separate fact,
/// and these tests are about the entry arriving at it, not arriving twice, and not arriving at all
/// when the doors come free on a recovery of somebody else's.
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

    /// <summary>The line B publishes when its own result is the one that needs recovery.</summary>
    private const string BResultNeedsRecoveryLine =
        "5号仓操作失败或状态未知，服务端已收到结果，等待管理员恢复。";

    /// <summary>A third command at the same stop, for the run where the doors come free twice.</summary>
    private const string AttemptC = "44444444-0000-4444-8444-00000000000c";

    /// <summary>The line that third command publishes once its own result has been acknowledged.</summary>
    private const string CResultAcceptedLine = "3号仓操作结果已被服务端确认。";

    /// <summary>
    /// A's result acknowledgement is lost while B has the doors, so A's restore publishes its line
    /// without a snapshot (onboard-hmi#152). B then finishes and gives the display up, and A's
    /// recovery entry reaches the screen -- worded as this run's own, and carrying A's slots.
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
            // B's, sent later, is acknowledged, so B settles and gives the display up for good.
            server => server.OperationResultAcksToDrop = 2,
            wrapJournal: inner => window = new RestoreWindowJournal(inner, AttemptA, AttemptB));
        using ReleaseOnExit releaseHeldWrite = new(() => window!.Release());

        await DriveAOwedWhileBHoldsTheDoorsAsync(harness, io, window!, token);
        await FinishBSuccessfullyAsync(harness, io, token);
        await WaitForAsRecoveryEntryAsync(harness, token);

        WireToGateHmiOperationSnapshot carried = harness.Business.CurrentOperationSnapshot!;
        Assert.Equal([1], carried.Slots);
        Assert.Equal(OperationType.Load, carried.OperationType);

        // Asserted through the view model, and not only on the business service, because that is
        // where the entry is offered: MainViewModel gates CanRequestWireToGateRecovery on the current
        // operation's stage, and it learns what the current operation is from the published event and
        // from nothing else. Measured 2026-09-20: a version of this fix that republished under the
        // restore's own deduplication key had the event swallowed as a duplicate,
        // Business.CurrentOperationSnapshot read as the recovery entry, and the screen went on showing
        // the finished command -- green here without this assertion, and no entry for the operator.
        await harness.WaitUntilAsync(
            () => harness.ViewModel.StateText == "需要恢复"
                && harness.ViewModel.Guidance == RecoveryRestoredLine,
            "the screen to read A's recovery guidance",
            token);

        // Twice, and both are wanted: once when the fact was learned, once when it became something
        // the operator can act on. Only one of the two carries the entry.
        await AssertWhileAsync(
            DisplaySettleWindow,
            () => AssertRecoveryEntryAnnouncedExactlyTwice(harness),
            token);

        AssertNotRefused(harness);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The same run, carried on past the entry: a third command takes the doors and gives them back,
    /// and A's recovery entry is not published a second time.
    /// </summary>
    /// <remarks>
    /// <b>This is the risk the ticket names in so many words.</b> Anything that republishes the entry
    /// when the display frees turns "the operator never sees it again" into "the operator sees it
    /// over and over", and there is a release at the end of every command. What stops it is not a
    /// check at the publishing end but the debt itself: the restore records it on the one round it
    /// runs for an attempt, and taking it clears it under the same lock, so there is nothing left for
    /// the next release to find.
    /// </remarks>
    [Fact]
    public async Task AShownRecoveryEntryIsNotShownAgainWhenTheDoorsComeFreeASecondTime()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true, KeepSnapshotFresh = true };
        RestoreWindowJournal? window = null;
        await using Harness harness = await StartTwoDemandStopAsync(
            io,
            token,
            server => server.OperationResultAcksToDrop = 2,
            wrapJournal: inner => window = new RestoreWindowJournal(inner, AttemptA, AttemptB));
        using ReleaseOnExit releaseHeldWrite = new(() => window!.Release());

        await DriveAOwedWhileBHoldsTheDoorsAsync(harness, io, window!, token);
        await FinishBSuccessfullyAsync(harness, io, token);
        await WaitForAsRecoveryEntryAsync(harness, token);

        // A third command at the same stop: it takes the doors, runs, is acknowledged and releases
        // them, which is the second time in this process that nothing is executing.
        await SendSlotCommandAsync(harness, DemandA, AttemptC, [3]);
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptC
                && io.UnlockCount == 3,
            "the third command to take the doors and open its own",
            token);
        io.CloseDoor(2, cargo: true);
        await harness.WaitUntilAsync(
            () => OperatorLog(harness).Contains(CResultAcceptedLine),
            "the third command's result to be acknowledged",
            token);

        // Still twice. Held rather than read once: the release and the publication it might have made
        // are on the third command's own thread, not this one.
        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                AssertRecoveryEntryAnnouncedExactlyTwice(harness);
                Assert.Equal(
                    AttemptC,
                    harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId);
            },
            token);

        AssertNotRefused(harness);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// B does not finish cleanly: its own result is the one that needs recovery. Its recovery is what
    /// the operator has to act on, and A's withheld entry must not take the screen off it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fault this guards is onboard-hmi#152's, displaced by a few seconds.</b> B announces its
    /// own recovery and then, in the same <c>finally</c>, gives its claim up -- and a debt paid
    /// unconditionally there puts A's slots on screen over B's. <c>MainViewModel</c> takes any
    /// non-null operation on an event, so the screen then reads "1号仓需要恢复" while the attempt the
    /// server is waiting on is B.
    /// </para>
    /// <para>
    /// <b>And it does not come back.</b> B's own restore round returns at the announcement claim,
    /// which B has already taken; A's debt is not cleared either, because by then the journal's
    /// unsettled attempt is B and the round returns before it gets that far. The operator is left
    /// pointed at the wrong slot for the rest of the process.
    /// </para>
    /// <para>
    /// <b>Why onboard-hmi#152's own tests cannot see this.</b> Their B never finishes -- the operator
    /// never acts on its door -- so nothing there ever reaches a release with a debt outstanding.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AWithheldRecoveryEntryDoesNotTakeTheScreenOffTheCommandThatNeedsRecoveryItself()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true, KeepSnapshotFresh = true };
        RestoreWindowJournal? window = null;
        await using Harness harness = await StartTwoDemandStopAsync(
            io,
            token,
            // Two dropped acks are A's own and its resend's; B's result is acknowledged, so B
            // concludes and announces its own recovery rather than waiting on an acknowledgement.
            server => server.OperationResultAcksToDrop = 2,
            wrapJournal: inner => window = new RestoreWindowJournal(inner, AttemptA, AttemptB));
        using ReleaseOnExit releaseHeldWrite = new(() => window!.Release());

        await DriveAOwedWhileBHoldsTheDoorsAsync(harness, io, window!, token);

        // B's own lock feedback goes unreadable, the same way A's did: B ends UNKNOWN and needs an
        // administrator itself.
        io.SetUnreadable(4);
        await harness.WaitUntilAsync(
            () => OperatorLog(harness).Contains(BResultNeedsRecoveryLine),
            "B's own result to need recovery",
            token);

        // B is the recovery the operator has to make, and the screen has to go on saying so.
        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                WireToGateHmiOperationSnapshot? current = harness.Business.CurrentOperationSnapshot;
                Assert.NotNull(current);
                Assert.Equal(AttemptB, current.SlotOperationAttemptId);
                Assert.Equal([5], current.Slots);
                Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, current.Stage);
            },
            token);

        // A's line went out once, when it was withheld, and A's entry never followed: B's recovery
        // supersedes it.
        Assert.Single(harness.Events, item => item.Message == RecoveryRestoredLine);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A ends <c>UNKNOWN</c> with its acknowledgement lost while B is at the doors: A's recovery line
    /// goes to the operator without a snapshot (onboard-hmi#152), which is the debt the rest of these
    /// tests are about.
    /// </summary>
    private static async Task DriveAOwedWhileBHoldsTheDoorsAsync(
        Harness harness,
        FakeIoModuleClient io,
        RestoreWindowJournal window,
        CancellationToken token)
    {
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
            () => window.PreparedWriteHeld,
            "B to reach its own Prepared write",
            token);
        // Two waits, not one: A's DurableAck timeout alone is two seconds of the harness's five-second
        // budget, and the restore only starts after it.
        await harness.WaitUntilAsync(
            () => OperatorLog(harness).Contains(ResultAckPendingLine),
            "A's acknowledgement to be given up on",
            token);
        await harness.WaitUntilAsync(
            () => window.RestoreHasReadTheLeftover,
            "A's restore to read the journal while A is still the unsettled attempt",
            token);

        // The operator shuts A's door; only then can B open one beside it (REQ-0357).
        io.CloseDoor(0, cargo: true);
        window.Release();
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
    }

    /// <summary>The operator loads B's basket and shuts its door; B reports and is acknowledged.</summary>
    private static async Task FinishBSuccessfullyAsync(
        Harness harness,
        FakeIoModuleClient io,
        CancellationToken token)
    {
        io.CloseDoor(4, cargo: true);
        await harness.WaitUntilAsync(
            () => OperatorLog(harness).Contains(BResultAcceptedLine),
            "B's result to be acknowledged",
            token);
    }

    /// <summary>
    /// A is still the attempt the server is waiting on, and nothing is at the doors any more, so the
    /// entry the operator acts from has to be on screen.
    /// </summary>
    private static Task WaitForAsRecoveryEntryAsync(Harness harness, CancellationToken token) =>
        harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptA
                && harness.Business.CurrentOperationSnapshot?.Stage
                    == WireToGateHmiOperationStage.RecoveryRequired,
            "A's recovery entry to reach the screen once B has finished with the display",
            token);

    /// <summary>
    /// A's recovery line has gone to the operator exactly twice: once withheld, once carrying the
    /// entry. Asserted on the events rather than on the log, because only the events say what each
    /// one carried, and without pinning which of the two is which: the claim being taken once is what
    /// bounds the count, not any order between them.
    /// </summary>
    private static void AssertRecoveryEntryAnnouncedExactlyTwice(Harness harness)
    {
        WireToGateOperatorEvent[] announced =
            [.. harness.Events.Where(item => item.Message == RecoveryRestoredLine)];
        Assert.Equal(2, announced.Length);
        WireToGateOperatorEvent carrying = Assert.Single(
            announced,
            item => item.Operation is not null);
        Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, carrying.Operation!.Stage);
        Assert.Equal(AttemptA, carrying.Operation.SlotOperationAttemptId);
    }
}
