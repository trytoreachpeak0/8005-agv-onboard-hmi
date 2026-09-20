using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 把待发结果记进 journal 与同一 attempt 的迟到确认之间的竞态（批次 7-19，
/// <c>trytoreachpeak0/8005-agv-onboard-hmi#136</c> 第 1 处写入点）。
/// </summary>
/// <remarks>
/// <para>
/// <c>RecordPendingResultAsync</c> 先读状态、在锁外校验「这个 attempt 就是 journal 的未结算 attempt」、
/// 再把整条记录写回去。服务端的迟到确认走 <c>MarkResultRecordedAsync</c> 把这个 attempt 记成已结算
/// （清空 <c>UnsettledSlotOperationAttemptId</c> 与 <c>PendingResults</c>，落 <c>ResultRecorded</c>），
/// 它若落在读与写之间，写回就把已经结清的 attempt 连同待发结果一起复活——不抛异常、不写日志，
/// 重启之后车辆把一次已经结清的操作当成未结算，恢复入口从此要人清 journal 才好。
/// </para>
/// <para>
/// 判定不靠计时：竞争写入由包装日志在「目标路径第一次碰 journal」时确定地插进去。
/// </para>
/// </remarks>
public sealed class WireToGateSlotOperationExecutorPendingResultRaceTests
{
    private const string AttemptId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    /// <summary>
    /// 结果内容摘要。这一层的 <see cref="WireToGatePendingResult"/> 不带 outcome，FAILED 与 UNKNOWN
    /// 的差别只在报文身份上——所以这两个用例在**本层是同构的**，它们钉住的是「无论记下的是哪一份
    /// 待发结果都不复活」。让 outcome 参与判定的是业务服务的 <c>completedSuccessfully</c> 分支，
    /// 那条判据在 G2。此处不假装区分。
    /// </summary>
    private const string FailedResultSha = "1111111111111111111111111111111111111111111111111111111111111111";

    private const string UnknownResultSha = "2222222222222222222222222222222222222222222222222222222222222222";

    /// <summary>
    /// 迟到的确认在读与写之间把这个 attempt 记成已结算。待发结果不写，journal 保持已结算，
    /// 收场与今天前提检查失败时相同：<c>SLOT_OPERATION_CONFLICT</c>。
    /// </summary>
    [Theory]
    [InlineData(FailedResultSha)]
    [InlineData(UnknownResultSha)]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task APendingResultNeverRevivesAnAttemptRecordedBetweenItsReadAndItsWrite(
        string resultContentSha256)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RaceFixture fixture = await RaceFixture.CreateAsync(token);
        await fixture.SeedUnsettledAsync(token);
        fixture.Journal.OnNextRecoveryStateAccess = () => fixture.RecordTheAttemptAsync(token);

        Exception? refused = await Record.ExceptionAsync(() => fixture.Executor.RecordPendingResultAsync(
            AttemptId,
            new WireToGatePendingResult("OperationResult", AttemptId, AttemptId, resultContentSha256),
            token));

        WireToGateRecoveryState persisted = await fixture.Sqlite.ReadRecoveryStateAsync(token);
        Assert.Null(persisted.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, persisted.ProvenRecoveryCheckpoint);
        Assert.Empty(persisted.PendingResults);
        Assert.Equal("SLOT_OPERATION_CONFLICT", Assert.IsType<InvalidDataException>(refused).Message);
    }

    /// <summary>
    /// 竞争之后重启：同一个 journal 重开读到的状态与上面断言的一致。持久化才是这条判据的事实，
    /// 内存里的状态不算。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task WhatTheRaceLeavesOnFileIsWhatTheNextProcessReads()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath;
        await using (RaceFixture fixture = await RaceFixture.CreateAsync(token))
        {
            journalPath = fixture.JournalPath;
            await fixture.SeedUnsettledAsync(token);
            fixture.Journal.OnNextRecoveryStateAccess = () => fixture.RecordTheAttemptAsync(token);
            await Record.ExceptionAsync(() => fixture.Executor.RecordPendingResultAsync(
                AttemptId,
                new WireToGatePendingResult("OperationResult", AttemptId, AttemptId, FailedResultSha),
                token));
        }

        await using SqliteWireToGateJournal reopened = new(journalPath);
        await reopened.InitializeAsync(token);
        WireToGateRecoveryState restored = await reopened.ReadRecoveryStateAsync(token);
        Assert.Null(restored.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, restored.ProvenRecoveryCheckpoint);
        Assert.Empty(restored.PendingResults);
    }

    /// <summary>
    /// 无竞争时逐字不变：待发结果写进 journal，未结算的 attempt 还在，检查点不动。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task WithoutARaceThePendingResultIsJournaledExactlyAsBefore()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RaceFixture fixture = await RaceFixture.CreateAsync(token);
        await fixture.SeedUnsettledAsync(token);

        await fixture.Executor.RecordPendingResultAsync(
            AttemptId,
            new WireToGatePendingResult("OperationResult", AttemptId, AttemptId, FailedResultSha),
            token);

        WireToGateRecoveryState persisted = await fixture.Sqlite.ReadRecoveryStateAsync(token);
        Assert.Equal(AttemptId, persisted.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, persisted.ProvenRecoveryCheckpoint);
        WireToGatePendingResult kept = Assert.Single(persisted.PendingResults);
        Assert.Equal(FailedResultSha, kept.ContentSha256);
        Assert.Equal(AttemptId, kept.BusinessId);
    }

    /// <summary>
    /// 内层 journal 写入抛 <see cref="IOException"/> 时，异常照今天传出，盘上状态一点没变。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AJournalWriteFailureLeavesTheStateUntouchedAndThrowsAsBefore()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RaceFixture fixture = await RaceFixture.CreateAsync(token);
        await fixture.SeedUnsettledAsync(token);
        fixture.Journal.FailNextWrite = new IOException("journal is gone");

        IOException thrown = await Assert.ThrowsAsync<IOException>(
            () => fixture.Executor.RecordPendingResultAsync(
                AttemptId,
                new WireToGatePendingResult("OperationResult", AttemptId, AttemptId, FailedResultSha),
                token));

        Assert.Equal("journal is gone", thrown.Message);
        WireToGateRecoveryState persisted = await fixture.Sqlite.ReadRecoveryStateAsync(token);
        Assert.Equal(AttemptId, persisted.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, persisted.ProvenRecoveryCheckpoint);
        Assert.Empty(persisted.PendingResults);
    }

    /// <summary>
    /// 一个 sqlite journal、一层可注入竞争写入的包装、一个不碰 IO 的执行器。
    /// </summary>
    private sealed class RaceFixture : IAsyncDisposable
    {
        private RaceFixture(
            string journalPath,
            SqliteWireToGateJournal sqlite,
            InterleavingJournal journal,
            WireToGateSlotOperationExecutor executor)
        {
            JournalPath = journalPath;
            Sqlite = sqlite;
            Journal = journal;
            Executor = executor;
        }

        public string JournalPath { get; }

        public SqliteWireToGateJournal Sqlite { get; }

        public InterleavingJournal Journal { get; }

        public WireToGateSlotOperationExecutor Executor { get; }

        public static async Task<RaceFixture> CreateAsync(CancellationToken cancellationToken)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "w2g-pending-result-race",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string journalPath = Path.Combine(directory, "journal.db");
            SqliteWireToGateJournal sqlite = new(journalPath);
            await sqlite.InitializeAsync(cancellationToken);
            InterleavingJournal journal = new(sqlite);
            WireToGateSlotOperationExecutor executor = new(
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
            return new RaceFixture(journalPath, sqlite, journal, executor);
        }

        /// <summary>这个 attempt 是 journal 的未结算 attempt，已到安全收尾。</summary>
        public async Task SeedUnsettledAsync(CancellationToken cancellationToken) =>
            await Sqlite.UpdateRecoveryStateAsync(
                _ => WireToGateRecoveryState.Empty with
                {
                    UnsettledSlotOperationAttemptId = AttemptId,
                    ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.SafeFinishReached
                },
                cancellationToken);

        /// <summary>
        /// 迟到的确认把这个 attempt 记成已结算，写的就是 <c>MarkResultRecordedAsync</c> 写的那些字段。
        /// 走内层 journal，不经包装，所以它自己不会再触发注入。
        /// </summary>
        public async Task RecordTheAttemptAsync(CancellationToken cancellationToken) =>
            await Sqlite.UpdateRecoveryStateAsync(
                state => state with
                {
                    UnsettledSlotOperationAttemptId = null,
                    ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ResultRecorded,
                    ActiveUnlockSlots = [],
                    PendingResults = [],
                    OperationContext = null,
                    CompletedSlots = [],
                    SlotResults = []
                },
                cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Executor.DisposeAsync();
            await Sqlite.DisposeAsync();
        }
    }

    /// <summary>记待发结果不碰 IO；这里用到它就是测试写错了。</summary>
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
    /// 在「下一次碰恢复状态」时跑一次额外写入：分离读写的话落在读返回之后，锁内读改写的话落在它读之前
    /// ——相对于目标写入，另一个写入者只能落在这两处。也可以让下一次写入抛出。
    /// </summary>
    private sealed class InterleavingJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private Func<Task>? _onNext;
        private Exception? _failNextWrite;

        public Func<Task>? OnNextRecoveryStateAccess
        {
            set => Volatile.Write(ref _onNext, value);
        }

        public Exception? FailNextWrite
        {
            set => Volatile.Write(ref _failNextWrite, value);
        }

        public async Task<WireToGateRecoveryState> ReadRecoveryStateAsync(
            CancellationToken cancellationToken = default)
        {
            WireToGateRecoveryState state = await inner.ReadRecoveryStateAsync(cancellationToken);
            await InterleaveAsync();
            return state;
        }

        public Task WriteRecoveryStateAsync(
            WireToGateRecoveryState state,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed();
            return inner.WriteRecoveryStateAsync(state, cancellationToken);
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
            await InterleaveAsync();
            ThrowIfArmed();
            return await inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);
        }

        private async Task InterleaveAsync()
        {
            if (Interlocked.Exchange(ref _onNext, null) is { } interleave)
            {
                await interleave();
            }
        }

        private void ThrowIfArmed()
        {
            if (Interlocked.Exchange(ref _failNextWrite, null) is { } failure)
            {
                throw failure;
            }
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
