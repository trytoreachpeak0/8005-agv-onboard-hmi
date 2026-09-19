using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The CLOSED snapshot's fallback gets another chance when it fails: the vehicle acknowledges the
/// CLOSED only once the fallback has done its part, so the server replays it (onboard-hmi#129 B).
/// </summary>
/// <remarks>
/// <para>
/// A refused recovery command or resume forgets its session right away, and the CLOSED snapshot the
/// server sends on the refusal is the fallback for a forget that failed. Until #129 the vehicle
/// acknowledged that CLOSED as it read it and handled it afterwards. The server replays only what is
/// unacknowledged, so a fallback write that failed too -- or a stop between the acknowledgement and
/// the write -- left the journal naming a session nothing would ever clear, and every recovery entry
/// refused locally with <c>RECOVERY_SESSION_STATE_PENDING</c> for good.
/// </para>
/// <para>
/// The failing writes are real <see cref="IOException"/>s from the journal, injected on the release
/// path only, rather than a journal rewritten afterwards to look as if a write had been lost.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// The refused compensation's release fails, and so does the first CLOSED fallback. That CLOSED
    /// is not acknowledged; its replay forgets the vector and the session, and is acknowledged.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AClosedSnapshotWhoseFallbackFailedIsReplayedUntilItForgetsTheRefusedVector()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FailingReleaseJournal? failing = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.ReplayUnacknowledgedClosedRecoverySnapshots = true;
            },
            cargoInTargetSlots: true,
            wrapJournal: inner => failing = new FailingReleaseJournal(inner));
        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);

        failing!.FailTheNextReleases(2);
        await RefuseCompensationAsync(harness, prepared, token);
        await WaitForFailedReleasesAsync(failing, 1, token);
        Assert.NotNull((await harness.ReadRecoveryStateAsync(token)).RecoveryVector);

        await SendClosedSnapshotAsync(harness, prepared, "COMPENSATE_LOAD_ALL_EMPTY", CompensationSlots);
        await AssertTheClosedSnapshotIsHeldBackAsync(harness, failing, token);

        await harness.Server.ReplayUnacknowledgedRecoverySnapshotsAsync();
        await AssertTheReplayedClosedSnapshotForgetsTheSessionAsync(harness, token);
        await AssertANewSessionCanBeOpenedAsync(harness, token);
    }

    /// <summary>
    /// The same for the refused resume of onboard-hmi#119: its release fails, the first CLOSED
    /// fallback fails, the replayed CLOSED forgets the session.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AClosedSnapshotWhoseFallbackFailedIsReplayedUntilItForgetsTheRefusedResume()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FailingReleaseJournal? failing = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.ReplayUnacknowledgedClosedRecoverySnapshots = true;
            },
            wrapJournal: inner => failing = new FailingReleaseJournal(inner));
        WireToGateRecoveryState opened = await OpenResumeActionAsync(harness, token);

        failing!.FailTheNextReleases(2);
        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(opened, checkpoint: AnotherCheckpointThan(opened.ProvenRecoveryCheckpoint)));
        await WaitForSingleRejectionAsync(harness, token);
        await WaitForFailedReleasesAsync(failing, 1, token);
        Assert.Equal(
            opened.ExceptionRecoverySessionId,
            (await harness.ReadRecoveryStateAsync(token)).ExceptionRecoverySessionId);

        await SendClosedSnapshotAsync(harness, opened, "RESUME_AFTER_REPAIR", ResumeSlots);
        await AssertTheClosedSnapshotIsHeldBackAsync(harness, failing, token);

        await harness.Server.ReplayUnacknowledgedRecoverySnapshotsAsync();
        await AssertTheReplayedClosedSnapshotForgetsTheSessionAsync(harness, token);
        await AssertANewSessionCanBeOpenedAsync(harness, token);
    }

    /// <summary>
    /// The vehicle stops after its CLOSED fallback failed -- the case that, acknowledged first, left
    /// nothing to come back. On the restart the server replays the unacknowledged CLOSED after the
    /// handshake, and it forgets the refused vector and its session.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AClosedSnapshotWhoseFallbackFailedBeforeARestartIsReplayedAfterTheReconnect()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Path.Combine(
            Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.SendRecoveryVectorCommandAfterRecoveryAction = false;
        server.RecoverySlotOperationAttemptId = AttemptId;
        server.ReplayUnacknowledgedClosedRecoverySnapshots = true;

        FailingReleaseJournal? failing = null;
        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            cargoInTargetSlots: true,
            wrapJournal: inner => failing = new FailingReleaseJournal(inner)))
        {
            WireToGateRecoveryState prepared = await PrepareCompensationAsync(beforeRestart, token);
            failing!.FailTheNextReleases(2);
            await RefuseCompensationAsync(beforeRestart, prepared, token);
            await WaitForFailedReleasesAsync(failing, 1, token);
            await SendClosedSnapshotAsync(
                beforeRestart, prepared, "COMPENSATE_LOAD_ALL_EMPTY", CompensationSlots);
            await AssertTheClosedSnapshotIsHeldBackAsync(beforeRestart, failing, token);
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.SendRecoveryVectorCommandAfterRecoveryAction = false;
        serverAfterRestart.RecoverySlotOperationAttemptId = AttemptId;
        serverAfterRestart.ReplayUnacknowledgedClosedRecoverySnapshots = true;
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        Assert.Contains(ClosedSnapshotMessageId, serverAfterRestart.UnacknowledgedClosedRecoverySnapshots);

        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            baselineRevision: 2,
            restart: true,
            cargoInTargetSlots: true);
        await AssertTheReplayedClosedSnapshotForgetsTheSessionAsync(afterRestart, token);

        WireToGateRecoveryState released = await afterRestart.ReadRecoveryStateAsync(token);
        Assert.Null(released.ExceptionRecoverySessionId);
        Assert.Null(released.RecoveryVector);
        Assert.Equal(AttemptId, released.UnsettledSlotOperationAttemptId);
        afterRestart.VehicleStopped();
        Assert.True(await afterRestart.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.ResultsOfType("ExceptionRecoverySessionRequested").Count == 1,
            "a new recovery session request after the restart",
            token);
    }

    private static async Task WaitForFailedReleasesAsync(
        FailingReleaseJournal failing,
        int count,
        CancellationToken token) =>
        await RecoveryVectorHarness.WaitUntilAsync(
            () => failing.FailedReleases >= count,
            $"release write {count} to fail",
            token);

    /// <summary>
    /// The CLOSED fallback ran and failed, the session is still on file, and the CLOSED was not
    /// acknowledged.
    /// </summary>
    private static async Task AssertTheClosedSnapshotIsHeldBackAsync(
        RecoveryVectorHarness harness,
        FailingReleaseJournal failing,
        CancellationToken token)
    {
        await WaitForFailedReleasesAsync(failing, 2, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry => entry.Message.StartsWith(
                "恢复会话已关闭，但清除本地恢复会话记录失败", StringComparison.Ordinal)),
            "the failed CLOSED fallback to be logged",
            token);
        // The read loop acknowledges straight after the handling returns, so a short settle is enough
        // for an acknowledgement that is coming to have arrived.
        await Task.Delay(200, token);

        Assert.NotNull((await harness.ReadRecoveryStateAsync(token)).ExceptionRecoverySessionId);
        Assert.DoesNotContain(ClosedSnapshotMessageId, AcknowledgedRecoverySnapshots(harness));
        Assert.Contains(ClosedSnapshotMessageId, harness.Server.UnacknowledgedClosedRecoverySnapshots);
    }

    private static async Task AssertTheReplayedClosedSnapshotForgetsTheSessionAsync(
        RecoveryVectorHarness harness,
        CancellationToken token)
    {
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult()
                .ExceptionRecoverySessionId is null,
            "the replayed CLOSED snapshot to forget the session",
            token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => AcknowledgedRecoverySnapshots(harness).Contains(ClosedSnapshotMessageId),
            "the replayed CLOSED snapshot to be acknowledged",
            token);
        Assert.Empty(harness.Server.UnacknowledgedClosedRecoverySnapshots);
    }

    /// <summary>
    /// Fails the atomic update of the next <c>n</c> session releases -- the refusal's own forget and
    /// the CLOSED fallback both go through <c>ForgetRecoverySessionAsync</c> -- with an
    /// <see cref="IOException"/>, the way a full or locked disk would, and lets every other call through.
    /// </summary>
    private sealed class FailingReleaseJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private int _toFail;
        private int _failed;

        public int FailedReleases => Volatile.Read(ref _failed);

        public void FailTheNextReleases(int count) => Volatile.Write(ref _toFail, count);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            UpdateRecoveryStateAsync(change, static _ => { }, cancellationToken);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default)
        {
            ThrowIfThisReleaseFails();
            return inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);
        }

        private void ThrowIfThisReleaseFails()
        {
            if (Volatile.Read(ref _toFail) > 0
                && Environment.StackTrace.Contains("ForgetRecoverySessionAsync", StringComparison.Ordinal)
                && Interlocked.Decrement(ref _toFail) >= 0)
            {
                Interlocked.Increment(ref _failed);
                throw new IOException("injected: the journal could not be written");
            }
        }

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

        public Task WriteRecoveryStateAsync(
            WireToGateRecoveryState state,
            CancellationToken cancellationToken = default) =>
            inner.WriteRecoveryStateAsync(state, cancellationToken);

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

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
