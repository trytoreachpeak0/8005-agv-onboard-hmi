using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 结果补记与下一单的 journal 写入之间的竞态（<c>trytoreachpeak0/8005-agv-onboard-hmi#127</c>，PR #126 审查转来的第 2 条）。
/// </summary>
/// <remarks>
/// 迟到的确认让上一单 A 补记为已结算时，下一单 B 可能已经在执行器里写下自己的 <c>Prepared</c>。补记若是「先读、校验、
/// 再整份写回」，B 的写入落在读与写之间，写回就按 A 的旧状态把 B 的 journal 清空：B 正在开锁，车却不再记得它。
/// 补记与 onboard-hmi#123 的 CLOSED 兜底用同一个做法：在日志锁内读改写（<c>UpdateRecoveryStateAsync</c>）。
/// </remarks>
public sealed class WireToGateSlotOperationExecutorResultRecordRaceTests
{
    private const string AttemptA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string AttemptB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task RecordingALateResultNeverClearsTheNextOperationThatStartedInBetween()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), "w2g-executor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using SqliteWireToGateJournal sqlite = new(Path.Combine(directory, "journal.db"));
        await sqlite.InitializeAsync(token);
        await sqlite.WriteRecoveryStateAsync(
            WireToGateRecoveryState.Empty with
            {
                UnsettledSlotOperationAttemptId = AttemptA,
                ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.SafeFinishReached
            },
            token);
        InterleavingJournal journal = new(sqlite);
        await using WireToGateSlotOperationExecutor executor = new(
            new NoIo(),
            journal,
            new SystemClock(),
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(1)),
            () => true);

        // The next operation B journals its Prepared at the first moment the recording touches the journal.
        journal.OnNextRecoveryStateAccess = () => sqlite.WriteRecoveryStateAsync(
            WireToGateRecoveryState.Empty with
            {
                UnsettledSlotOperationAttemptId = AttemptB,
                ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.Prepared
            },
            token);

        Exception? refused = await Record.ExceptionAsync(() => executor.MarkResultRecordedAsync(AttemptA, token));

        WireToGateRecoveryState state = await sqlite.ReadRecoveryStateAsync(token);
        Assert.Equal(AttemptB, state.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.Prepared, state.ProvenRecoveryCheckpoint);
        Assert.Equal("SLOT_OPERATION_CONFLICT", Assert.IsType<InvalidDataException>(refused).Message);
    }

    /// <summary>Recording a result touches no IO; any use of it here is a test bug.</summary>
    private sealed class NoIo : IIoModuleClient
    {
        public bool IsConnected => true;

        public IoSnapshot CurrentSnapshot => throw new NotSupportedException();

        public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken applicationStopping) => throw new NotSupportedException();

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LockerSnapshot> WaitForLockerAsync(
            int slotIndex,
            Func<LockerSnapshot, bool> predicate,
            TimeSpan timeout,
            TimeSpan stableWindow,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Runs one extra write right after the next recovery state read returns -- between a separate read and write
    /// -- or, for a read-modify-write under the journal lock, right before it: the only two places another writer
    /// can land relative to it.
    /// </summary>
    private sealed class InterleavingJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private Func<Task>? _onNext;

        public Func<Task>? OnNextRecoveryStateAccess
        {
            set => Volatile.Write(ref _onNext, value);
        }

        public async Task<WireToGateRecoveryState> ReadRecoveryStateAsync(
            CancellationToken cancellationToken = default)
        {
            WireToGateRecoveryState state = await inner.ReadRecoveryStateAsync(cancellationToken);
            if (Interlocked.Exchange(ref _onNext, null) is { } interleave)
            {
                await interleave();
            }

            return state;
        }

        public async Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _onNext, null) is { } interleave)
            {
                await interleave();
            }

            return await inner.UpdateRecoveryStateAsync(change, cancellationToken);
        }

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
