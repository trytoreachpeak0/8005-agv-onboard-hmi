using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 迟到的确认到了，但 journal 里未结算的已经是下一单（<c>trytoreachpeak0/8005-agv-onboard-hmi#127</c>，PR #126 审查转来的
/// 第 3 条）。补记照 onboard-hmi#127 的做法在日志锁内被拒（<c>SLOT_OPERATION_CONFLICT</c>），这时日志要说清发生了什么，
/// 界面也不能把上一单的「操作完成」盖到下一单上。在此之前它只落一条「恢复未完成仓位操作的界面投影失败」。
/// </summary>
public sealed partial class StationDeadlineExpiredG2Tests
{
    private const string NextAttemptId = "55555555-5555-4555-8555-555555555555";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ALateAckForAnAttemptTheJournalHasMovedPastIsLoggedForWhatItIs()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        NextOperationJournal? journal = null;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { SimulateOperatorLoad = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.OperationResultAcksToDrop = 1;
            },
            wrapJournal: inner => journal = new NextOperationJournal(inner));
        await harness.WaitForEventAsync("RESULT_ACK_PENDING", token);

        await Harness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(
                entry => entry.Severity == LogSeverity.Warning && entry.Message.Contains(AttemptId, StringComparison.Ordinal)
                    && entry.Message.Contains(NextAttemptId, StringComparison.Ordinal)),
            "a warning naming both the late attempt and the one the journal moved on to",
            token,
            () => string.Join(Environment.NewLine, harness.Logger.Entries.Select(entry => entry.Message)));

        Assert.True(journal!.NextOperationWritten);
        Assert.DoesNotContain(
            harness.Logger.Entries,
            entry => entry.Message.Contains("界面投影失败", StringComparison.Ordinal));
        Assert.False(harness.HasEvent("OPERATION_COMPLETED"), harness.DescribeEvents());
        Assert.Equal(NextAttemptId, harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId);
    }

    /// <summary>
    /// Journals the next operation's Prepared right before the first read-modify-write under the journal lock -- the
    /// recording of the late result -- as the executor would when the next command starts at that moment.
    /// </summary>
    private sealed class NextOperationJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private int _written;

        public bool NextOperationWritten => Volatile.Read(ref _written) == 1;

        public async Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _written, 1) == 0)
            {
                await inner.WriteRecoveryStateAsync(
                    WireToGateRecoveryState.Empty with
                    {
                        UnsettledSlotOperationAttemptId = NextAttemptId,
                        ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.Prepared
                    },
                    cancellationToken);
            }

            return await inner.UpdateRecoveryStateAsync(change, cancellationToken);
        }

        // Business-side reads and writes that cache what they settle on. Only the executor's own
        // result record goes through the overload above, and that is the write this double races.
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

        public Task WriteRecoveryStateAsync(
            WireToGateRecoveryState state,
            CancellationToken cancellationToken = default) =>
            inner.WriteRecoveryStateAsync(state, cancellationToken);

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
