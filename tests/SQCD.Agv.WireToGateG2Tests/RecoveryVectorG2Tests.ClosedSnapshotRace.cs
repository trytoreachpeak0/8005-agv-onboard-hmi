using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The CLOSED snapshot's release racing the result it would otherwise clean up after
/// (onboard-hmi#123 coordinator review A).
/// </summary>
/// <remarks>
/// The server closes a resume's session as soon as it acknowledges the resume's result, so the CLOSED
/// snapshot can reach the vehicle while the result is still being recorded. The release reads the
/// journal, the recording writes <c>ResultRecorded</c>, and a release that then writes what it read
/// puts the settled attempt back as unsettled -- the vehicle would report a load it already finished
/// as still waiting for recovery. The same holds for the late <c>MarkResultRecordedAsync</c> of
/// onboard-hmi#124's acknowledged-completed path.
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// The resume's result is recorded between the release's read and its write. The journal keeps
    /// <c>ResultRecorded</c> with nothing unsettled.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task TheClosedSnapshotNeverRollsBackAResultRecordedWhileItReleases()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        InterleavingJournal? interleaving = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = AttemptId,
            wrapJournal: inner => interleaving = new InterleavingJournal(inner));
        WireToGateRecoveryState opened = await OpenResumeActionAsync(harness, token);
        Assert.Equal(AttemptId, opened.UnsettledSlotOperationAttemptId);

        interleaving!.RecordResultWhenTheReleaseReads();
        await SendClosedSnapshotAsync(harness, opened, "RESUME_AFTER_REPAIR", ResumeSlots);
        await harness.WaitForInboundAsync("SnapshotAppliedAck", token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => interleaving.ReleaseFinished,
            "the CLOSED snapshot's release to run across the recorded result",
            token);

        Assert.True(interleaving.ResultRecordedMidRelease, "the result was never recorded inside the release");
        WireToGateRecoveryState persisted = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, persisted.ProvenRecoveryCheckpoint);
        Assert.Null(persisted.UnsettledSlotOperationAttemptId);
        Assert.Null(persisted.OperationContext);
        Assert.Null(persisted.ExceptionRecoverySessionId);
    }

    /// <summary>
    /// Records the result -- the write <c>MarkResultRecordedAsync</c> makes -- inside the next release
    /// of a recovery session, once armed: after a separate read has answered, or before an atomic
    /// update reads.
    /// </summary>
    private sealed class InterleavingJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private int _armed;
        private int _fired;
        private int _finished;

        public bool ResultRecordedMidRelease => Volatile.Read(ref _fired) == 1;

        public bool ReleaseFinished => Volatile.Read(ref _finished) == 1;

        public void RecordResultWhenTheReleaseReads() => Volatile.Write(ref _armed, 1);

        public async Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default)
        {
            if (!TakeTheRelease())
            {
                return await inner.ReadRecoveryStateAsync(cancellationToken);
            }

            WireToGateRecoveryState read = await inner.ReadRecoveryStateAsync(cancellationToken);
            await RecordResultAsync(cancellationToken);
            return read;
        }

        public async Task WriteRecoveryStateAsync(
            WireToGateRecoveryState state,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteRecoveryStateAsync(state, cancellationToken);
            if (ResultRecordedMidRelease)
            {
                Volatile.Write(ref _finished, 1);
            }
        }

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            UpdateRecoveryStateAsync(change, static _ => { }, cancellationToken);

        public async Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default)
        {
            bool release = TakeTheRelease();
            if (release)
            {
                await RecordResultAsync(cancellationToken);
            }

            WireToGateRecoveryState? written = await inner.UpdateRecoveryStateAsync(
                change,
                settled,
                cancellationToken);
            if (release)
            {
                Volatile.Write(ref _finished, 1);
            }

            return written;
        }

        private bool TakeTheRelease() =>
            Volatile.Read(ref _armed) == 1
            && Environment.StackTrace.Contains("ForgetRecoverySessionAsync", StringComparison.Ordinal)
            && Interlocked.Exchange(ref _armed, 0) == 1;

        private async Task RecordResultAsync(CancellationToken cancellationToken)
        {
            WireToGateRecoveryState state = await inner.ReadRecoveryStateAsync(cancellationToken);
            await inner.WriteRecoveryStateAsync(
                state with
                {
                    UnsettledSlotOperationAttemptId = null,
                    ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ResultRecorded,
                    ActiveUnlockSlots = [],
                    PendingResults = [],
                    OperationContext = null,
                    CompletedSlots = [],
                    SlotResults = [],
                    ExceptionRecoverySessionId = null,
                    RecoveryActionId = null,
                    RecoverySessionRequestId = null,
                    RecoveryActionRequestId = null,
                    RecoveryReason = null,
                    RecoveryOperatorId = null,
                    RecoveryOperatorVerifiedAt = null,
                    RecoveryVector = null,
                    RecoveryResultObservedAt = null,
                    PendingLoadCancellation = null,
                    LastCompletedLoadOperationContext = state.OperationContext?.OperationType == OperationType.Load
                        ? state.OperationContext
                        : state.LastCompletedLoadOperationContext
                },
                cancellationToken);
            Volatile.Write(ref _fired, 1);
        }

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
