using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The cached recovery state is never left older than the journal by a refresh that lands out of
/// order (onboard-hmi#129, review of onboard-hmi#123).
/// </summary>
/// <remarks>
/// Every recovery entry, and whether a forced recovery is waiting for the operator, reads a cached
/// copy of the journal's recovery state. Each business write used to put what it wrote into that copy
/// after the journal step had returned and released its lock. A result recorded and cached in that
/// gap -- the server acknowledges a resume's result and closes its session at once -- was then
/// overwritten with the older state, and the entries showed that older state until the next read of
/// the journal.
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// The CLOSED fallback's release is written, and before it caches what it wrote, the resume's
    /// result is recorded and the cache refreshed from it. The cache ends up equal to the journal.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task TheCachedStateIsNotRolledBackByAReleaseCachedAfterALaterResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        CacheInterleavingJournal? interleaving = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = AttemptId,
            wrapJournal: inner => interleaving = new CacheInterleavingJournal(inner));
        WireToGateRecoveryState opened = await OpenResumeActionAsync(harness, token);

        interleaving!.AfterTheNextWriteFrom(
            "ForgetRecoverySessionAsync",
            async inner =>
            {
                await inner.WriteRecoveryStateAsync(
                    ResultRecorded(await inner.ReadRecoveryStateAsync(token)),
                    token);
                await harness.Business.RefreshCachedRecoveryStateForTestAsync(token);
            });
        await SendClosedSnapshotAsync(harness, opened, "RESUME_AFTER_REPAIR", ResumeSlots);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => AcknowledgedRecoverySnapshots(harness).Contains(ClosedSnapshotMessageId),
            "the CLOSED snapshot to be acknowledged",
            token);

        Assert.True(interleaving.Fired, "the result was never recorded inside the release");
        WireToGateRecoveryState journal = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, journal.ProvenRecoveryCheckpoint);
        Assert.Equal(
            JsonSerializer.Serialize(journal),
            JsonSerializer.Serialize(harness.Business.CachedRecoveryStateForTest));
    }

    /// <summary>
    /// The same gap after <c>WriteRecoveryStateCachedAsync</c>: a later write and its refresh land
    /// between the journal write and the cache write. The cache ends up equal to the journal.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task TheCachedStateIsNotRolledBackByACachedWriteThatLandsBeforeALaterOne()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        CacheInterleavingJournal? interleaving = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            wrapJournal: inner => interleaving = new CacheInterleavingJournal(inner));
        WireToGateRecoveryState current = await harness.ReadRecoveryStateAsync(token);

        interleaving!.AfterTheNextWriteFrom(
            "WriteRecoveryStateCachedAsync",
            async inner =>
            {
                await inner.WriteRecoveryStateAsync(current with { RecoveryReason = "the later write" }, token);
                await harness.Business.RefreshCachedRecoveryStateForTestAsync(token);
            });
        await harness.Business.WriteCachedRecoveryStateForTestAsync(
            current with { RecoveryReason = "the earlier write" },
            token);

        Assert.True(interleaving.Fired, "the later write never landed inside the earlier one");
        WireToGateRecoveryState journal = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal("the later write", journal.RecoveryReason);
        Assert.Equal(
            JsonSerializer.Serialize(journal),
            JsonSerializer.Serialize(harness.Business.CachedRecoveryStateForTest));
    }

    /// <summary>What <c>MarkResultRecordedAsync</c> leaves: the attempt settled, the session forgotten.</summary>
    private static WireToGateRecoveryState ResultRecorded(WireToGateRecoveryState state) =>
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
        };

    /// <summary>
    /// Once armed, runs one action against the inner journal right after the next recovery-state write
    /// made from inside the named business method has returned -- after its journal step, before
    /// whatever the caller does next.
    /// </summary>
    private sealed class CacheInterleavingJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private string? _caller;
        private Func<IWireToGateJournal, Task>? _action;
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public void AfterTheNextWriteFrom(string caller, Func<IWireToGateJournal, Task> action)
        {
            _action = action;
            Volatile.Write(ref _caller, caller);
        }

        public async Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default)
        {
            Func<IWireToGateJournal, Task>? action = TakeTheAction();
            WireToGateRecoveryState? written = await inner.UpdateRecoveryStateAsync(change, cancellationToken);
            await RunAsync(action);
            return written;
        }

        public async Task WriteRecoveryStateAsync(
            WireToGateRecoveryState state,
            CancellationToken cancellationToken = default)
        {
            Func<IWireToGateJournal, Task>? action = TakeTheAction();
            await inner.WriteRecoveryStateAsync(state, cancellationToken);
            await RunAsync(action);
        }

        private Func<IWireToGateJournal, Task>? TakeTheAction()
        {
            string? caller = Volatile.Read(ref _caller);
            return caller is not null
                && Environment.StackTrace.Contains(caller, StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _caller, null, caller) == caller
                    ? _action
                    : null;
        }

        private async Task RunAsync(Func<IWireToGateJournal, Task>? action)
        {
            if (action is null)
            {
                return;
            }

            await action(inner);
            Volatile.Write(ref _fired, 1);
        }

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

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
