using SQCD.Agv.Application;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The restored recovery projection of an attempt whose result was never acknowledged, against the
/// command that has taken the doors since (batch 7-152,
/// <c>trytoreachpeak0/8005-agv-onboard-hmi#152</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this guards.</b> <c>RestorePendingRecoveryOperationProjectionAsync</c> published its
/// <c>RecoveryRequired</c> snapshot unconditionally. When A's result acknowledgement is lost and B
/// has since taken the executor, that snapshot puts A's slots back on screen over B's open door --
/// the same fault face as onboard-hmi#146, reached from the recovery side instead of from a queued
/// command.
/// </para>
/// <para>
/// <b>And it is not only the display.</b> Every snapshot handed to <c>PublishOperatorEvent</c> also
/// goes to <c>SlotExpectedActionWaitTracker.Observe</c> with no active slots, which stops the clock
/// unconditionally and without comparing attempts. B's expected-action clock is cleared and rebuilt
/// from the time of its next <c>WAITING_OPERATOR</c>, so <c>REQ-0358</c>'s
/// <c>SLOT_EXPECTED_ACTION_OVERDUE</c> comes late or not at all.
/// </para>
/// <para>
/// <b>Why batch 7-13's clock test cannot see this.</b>
/// <see cref="ASecondSlotCommandQueuesBehindTheFirstAndItsOverdueClockStartsAtItsOwnUnlock"/>
/// asserts <c>bWait.FirstUnlockAt >= aSettledAt</c>. Pushing that instant <i>later</i> -- exactly
/// what clearing and rebuilding the clock does -- still satisfies that inequality, so that assertion
/// is structurally blind to this defect. The tests here pin the instant itself: B's
/// <c>FirstUnlockAt</c>, read before the recovery projection, is the same value afterwards.
/// </para>
/// <para>
/// <b>The window is made, not raced for.</b> The restore reads the recovery state once, at its
/// start, and returns early if what it finds there is the command now running. The defect needs the
/// other order: the restore reads while A is still the unsettled attempt, and publishes once B is at
/// the doors. In the field that window is the stretch between B taking the display and B journaling
/// its own <c>Prepared</c> -- narrow, and a test that merely raced for it would be a coin toss.
/// <see cref="RestoreWindowJournal"/> holds B's <c>Prepared</c> write where it is until the restore
/// has done its read, and the test then lets it go: the same interleaving, reached by construction.
/// </para>
/// <para>
/// <b>Why the operator shuts A's door here.</b> <c>REQ-0357</c> lets one door be open at a time
/// across the whole vehicle, so while A's slot is not proven shut B is refused before its first
/// pulse. The test shuts it at the point the operator would: after the screen has said A's operation
/// ended and needs recovery.
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>The line A's settlement publishes once its <c>DurableAck</c> wait has run out.</summary>
    private const string ResultAckPendingLine =
        "操作已安全结束，但结果确认暂未收到；系统将保持同一结果重放，不会重复执行IO。";

    /// <summary>The line the restored recovery projection publishes for A, in A's own process.</summary>
    private const string RecoveryRestoredLine = "装货操作未完成：1号仓，需要管理员恢复。";

    /// <summary>
    /// The same line worded as a previous process's, which is what the restore says about an attempt
    /// this process never ran.
    /// </summary>
    private const string PreviousRunRecoveryLine = "上次装货操作未完成：1号仓，需要管理员恢复。";

    /// <summary>
    /// A ends <c>UNKNOWN</c> and its result acknowledgement never comes. Its restore reads the
    /// journal while A is still the unsettled attempt, and publishes only once B has the doors. The
    /// recovery line reaches the operator, and nothing else of A's does: B stays the current
    /// operation, the highlight stays on slot 5, and B's expected-action clock keeps the
    /// <c>FirstUnlockAt</c> it already had.
    /// </summary>
    [Fact]
    public async Task ARestoredRecoveryProjectionLeavesTheRunningCommandsDisplayAndClockAlone()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true, KeepSnapshotFresh = true };
        RestoreWindowJournal? window = null;
        await using Harness harness = await StartTwoDemandStopAsync(
            io,
            token,
            // A's OperationResult and the restore's resend of it are both taken and never answered:
            // each wait for the DurableAck times out with the session up, so A stays unsettled in the
            // journal and nothing the server does can advance it behind the test's back.
            server => server.OperationResultAcksToDrop = 2,
            wrapJournal: inner => window = new RestoreWindowJournal(inner, AttemptA, AttemptB));
        // Declared after the harness, so it runs before the harness is torn down. A failed assertion
        // below would otherwise leave B's journal write held forever and the teardown waiting on it:
        // the run would hang rather than report which assertion failed.
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

        // A's lock feedback goes unreadable while its door stands open: the operation settles UNKNOWN
        // and gives the display up before its result is even sent, so B starts while A is still
        // waiting on an acknowledgement that never comes.
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

        // The operator shuts A's door; only then can B open one beside it (REQ-0357).
        io.CloseDoor(0, cargo: true);
        window!.Release();
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentExpectedActionWait?.SlotOperationAttemptId == AttemptB
                && io.UnlockCount == 2,
            "B to take the doors and open its own",
            token);
        SlotExpectedActionWait before = harness.Business.CurrentExpectedActionWait!;

        await harness.WaitUntilAsync(
            () => OperatorLog(harness).Contains(RecoveryRestoredLine),
            "A's restored recovery line to reach the operator",
            token);

        // Asserted on the event itself, not only on its downstream effects. The assertions below say
        // "B's clock and display were not disturbed", which is also true of a run where the
        // projection happened to be published before B unlocked -- there would have been nothing to
        // disturb yet, and the run would go green over an unfixed product. This one says the
        // projection carried no snapshot, which is the thing the fix actually does.
        Assert.Null(harness.Events.Single(item => item.Message == RecoveryRestoredLine).Operation);

        // Held rather than read once: the clock and the snapshot are written before the event is
        // deduplicated, so a single read can land in front of the overwrite it is meant to catch.
        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                SlotExpectedActionWait? wait = harness.Business.CurrentExpectedActionWait;
                Assert.NotNull(wait);
                Assert.Equal(AttemptB, wait.SlotOperationAttemptId);
                Assert.Equal(5, wait.PhysicalSlotNumber);
                Assert.Equal(before.FirstUnlockAt, wait.FirstUnlockAt);
                Assert.Equal(AttemptB, harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId);
                AssertHighlighted(harness, 5);
            },
            token);

        AssertNotRefused(harness);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The same interleaving, carried on to what the clock is for: B's door stays open past the
    /// overdue threshold and <c>SLOT_EXPECTED_ACTION_OVERDUE</c> is raised for slot 5 on B's own
    /// first unlock, not on a clock the recovery projection restarted (<c>REQ-0358</c>).
    /// </summary>
    [Fact]
    public async Task AnOverdueIsStillRaisedOnTheRunningCommandsOwnUnlockAfterARestoredRecoveryProjection()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TimeSpan threshold = TimeSpan.FromMilliseconds(600);
        FakeIoModuleClient io = new() { OperatorNeverActs = true, KeepSnapshotFresh = true };
        RestoreWindowJournal? window = null;
        await using Harness harness = await StartTwoDemandStopAsync(
            io,
            token,
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

        DateTimeOffset bUnlockedAt = harness.Business.CurrentExpectedActionWait!.FirstUnlockAt;
        await harness.WaitUntilAsync(
            () => OperatorLog(harness).Contains(RecoveryRestoredLine),
            "A's restored recovery line to reach the operator",
            token);

        // Counted from B's own unlock, so the alarm is due whatever the wall clock has done since.
        await harness.WaitUntilAsync(
            () => DateTimeOffset.UtcNow >= bUnlockedAt + threshold,
            "B's door to stand open past the overdue threshold",
            token);

        // Same reason as in the test above: without this, an interleaving where the projection landed
        // before B's unlock would go green over an unfixed product.
        Assert.Null(harness.Events.Single(item => item.Message == RecoveryRestoredLine).Operation);
        Assert.NotNull(OverdueFor(harness, 5, threshold));
        Assert.Equal(bUnlockedAt, harness.Business.CurrentExpectedActionWait?.FirstUnlockAt);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// One command, its acknowledgement lost, nothing queued behind it: the attempt has given the
    /// display up but nobody has taken it, and its restore carries its snapshot as it always did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the commonest path of the three, and the one no test was watching.</b> The display
    /// is given up before the result is even sent, so by the time the restore runs the attempt no
    /// longer holds it -- but <c>ReleaseOperationDisplay</c> deliberately leaves the owner field
    /// where it is, so the owner is still this very attempt and the <i>equality</i> branch is what
    /// carries the snapshot. Drop that branch and keep only <c>owner is null</c> and this path stops
    /// putting the recovery entry on screen.
    /// </para>
    /// <para>
    /// <b>Why it needed its own test.</b> Measured 2026-09-20: with the equality branch dropped,
    /// every test in <c>MultiDemandJourneyG2Tests</c> and <c>StationDeadlineExpiredG2Tests</c> stayed
    /// green. The two that come closest --
    /// <c>StationDeadlineExpiredG2Tests.AnUnfinishedResultOnAReadySessionIsRestoredOnce</c> and
    /// <c>...AckPendingNotUnfinished</c> -- assert <c>Business.CurrentOperationSnapshot</c>, and on
    /// this path the <c>RESULT_ACK_PENDING</c> event carries a <c>RecoveryRequired</c> snapshot of
    /// its own moments earlier, so the screen reads identically either way. The assertion here is on
    /// the restore's own event, the same correction this file's restart test needed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ALoneAttemptStillCarriesItsSnapshotAfterGivingTheDisplayUp()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true, KeepSnapshotFresh = true };
        await using Harness harness = await StartTwoDemandStopAsync(
            io,
            token,
            server => server.OperationResultAcksToDrop = 2);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator
                && harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptA
                && io.UnlockCount == 1,
            "A's slot to be unlocked and waiting on the operator",
            token);

        // Nothing is sent for B: this is the single-command shape, where the display is released and
        // never taken again.
        io.SetUnreadable(0);
        await harness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(item => item.MessageType == "OperationResult"),
            "A's UNKNOWN result to reach the server",
            token);
        await harness.WaitUntilAsync(
            () => harness.Events.Any(item => item.Message == RecoveryRestoredLine),
            "A's restored recovery line to be published",
            token);

        WireToGateHmiOperationSnapshot? carried = harness.Events
            .Single(item => item.Message == RecoveryRestoredLine)
            .Operation;
        Assert.NotNull(carried);
        Assert.Equal(AttemptA, carried.SlotOperationAttemptId);
        Assert.Equal([1], carried.Slots);
        Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, carried.Stage);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// Nothing owns the current-operation display -- the process has just started and has handled no
    /// slot command -- and the restore publishes its snapshot as it always did, so the recovery entry
    /// appears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the test that stops the fix from going too far.</b> The natural reading of
    /// onboard-hmi#146 is "carry the snapshot only while this attempt still owns the display", and
    /// both places it changed sit inside command handling, where
    /// <c>_operationDisplayOwnerAttemptId</c> has necessarily been written. This path runs after a
    /// restart, where that field is <c>null</c> and no other path puts the recovery entry on screen
    /// (onboard-hmi#131). An owner-equality test would therefore take the entry away from the
    /// operator entirely -- the shape of onboard-hmi#109, which every unit test and the whole of CI
    /// stayed green through.
    /// </para>
    /// <para>
    /// <b>What it asserts is the snapshot on the restore's own event, and nothing weaker.</b>
    /// <c>MainViewModel</c> offers the recovery entry only while the current operation's stage is
    /// <c>RecoveryRequired</c>, so a restore that publishes its line without a snapshot is an entry
    /// that never appears. But the current operation is not enough to assert on here: the
    /// interrupted settlement this restart also runs publishes a <c>RecoveryRequired</c> snapshot of
    /// its own just before, so the screen would read the same either way and the test would pass over
    /// a restore that had stopped carrying anything. Measured, not assumed -- an owner-equality
    /// version of the fix left that weaker assertion green.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheRestoredRecoveryProjectionStillCarriesItsSnapshotWhenNothingOwnsTheDisplay()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        FakeIoModuleClient firstIo = new() { OperatorNeverActs = true, KeepSnapshotFresh = true };
        FakeControlServer first;
        await using (Harness before = await StartTwoDemandStopAsync(
            firstIo,
            token,
            // A's result goes out and is never answered. Without that the restart's own settlement
            // concludes the attempt and announces the recovery itself, and the restore -- which says
            // a recovery once per process, whoever said it -- returns before publishing anything.
            server => server.OperationResultAcksToDrop = 1,
            journalPath: journalPath))
        {
            first = before.Server;
            await SendSlotCommandAsync(before, DemandA, AttemptA, [1]);
            // Waited on the stage, not just on the clock: the clock is already keyed while the door
            // is still being unlocked, and lock feedback taken away at that point is retried rather
            // than given up on.
            await before.WaitUntilAsync(
                () => before.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator
                    && before.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptA
                    && firstIo.UnlockCount == 1,
                "A's slot to be unlocked and waiting on the operator",
                token);

            // Two waits, because the harness gives each one five seconds and the DurableAck timeout
            // alone is two.
            firstIo.SetUnreadable(0);
            await before.WaitUntilAsync(
                () => before.Server.ReceivedEnvelopes.Any(item => item.MessageType == "OperationResult"),
                "A's UNKNOWN result to reach the server",
                token);
            await before.WaitUntilAsync(
                () => OperatorLog(before).Contains(ResultAckPendingLine),
                "A's acknowledgement to be given up on",
                token);
        }

        // A new vehicle process over the same journal: A is the unsettled attempt on file, and nothing
        // in this process has taken the display.
        await using Harness after = await Harness.StartAsync(
            server =>
            {
                // The stop's journey is not what this is about, and it is not what puts the entry on
                // screen either.
                server.SendJourneySnapshotsAfterRecovery = false;
                server.AdoptDurableRecoveryMemoryFrom(first);
            },
            token,
            journalPath: journalPath,
            io: new FakeIoModuleClient { KeepSnapshotFresh = true });

        await after.WaitUntilAsync(
            () => after.Events.Any(item => item.Message == PreviousRunRecoveryLine),
            "the restored recovery projection to be published",
            token);

        WireToGateHmiOperationSnapshot? carried = after.Events
            .Single(item => item.Message == PreviousRunRecoveryLine)
            .Operation;
        Assert.NotNull(carried);
        Assert.Equal(AttemptA, carried.SlotOperationAttemptId);
        Assert.Equal([1], carried.Slots);
        Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, carried.Stage);
        Assert.Equal(AttemptA, after.Business.CurrentOperationSnapshot?.SlotOperationAttemptId);
        Assert.Empty(after.UiErrors);
    }

    /// <summary>Runs an action when the scope ends, however it ends.</summary>
    private sealed class ReleaseOnExit(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    /// <summary>
    /// A journal that holds the next command's <c>Prepared</c> write until the leftover attempt's
    /// restore has read the recovery state, so the restore is looking at the leftover rather than at
    /// the command that has since started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the write and not the read.</b> Holding the restore's read instead would mean handing it
    /// a state captured earlier, which says nothing about whether the product could ever read that
    /// state at that point. Holding B's write leaves every read as the product makes it: the restore
    /// reads the journal exactly as it stands, and what the test controls is only how long B's own
    /// entry takes to land -- a stretch that on a real disk is not the test's to choose.
    /// </para>
    /// <para>
    /// <b>The read is recognised by content and order</b>, not by thread: A's settlement crosses
    /// several awaits between giving the display up and starting the restore, so the restore does not
    /// run on the thread anything observable was raised from. Only a recovery-state read taken after
    /// B's write is already held, and still naming the leftover as unsettled, can be the restore's.
    /// </para>
    /// </remarks>
    private sealed class RestoreWindowJournal(
        IWireToGateJournal inner,
        string leftoverAttemptId,
        string nextAttemptId) : IWireToGateJournal
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        /// <summary>Whether the next command's own journal entry is being held.</summary>
        public bool PreparedWriteHeld => Volatile.Read(ref _held) != 0;

        /// <summary>Whether a recovery-state read since then still found the leftover unsettled.</summary>
        public bool RestoreHasReadTheLeftover { get; private set; }

        public void Release() => _release.TrySetResult();

        public Task WriteRecoveryStateAsync(
            WireToGateRecoveryState state,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(state.UnsettledSlotOperationAttemptId, nextAttemptId, StringComparison.Ordinal)
                || Interlocked.Exchange(ref _held, 1) != 0)
            {
                return inner.WriteRecoveryStateAsync(state, cancellationToken);
            }

            return HeldAsync();

            async Task HeldAsync()
            {
                await _release.Task;
                await inner.WriteRecoveryStateAsync(state, cancellationToken);
            }
        }

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(
                change,
                state =>
                {
                    Observe(state);
                    settled(state);
                },
                cancellationToken);

        private void Observe(WireToGateRecoveryState state)
        {
            if (PreparedWriteHeld
                && string.Equals(
                    state.UnsettledSlotOperationAttemptId,
                    leftoverAttemptId,
                    StringComparison.Ordinal))
            {
                RestoreHasReadTheLeftover = true;
            }
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, cancellationToken);

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

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
