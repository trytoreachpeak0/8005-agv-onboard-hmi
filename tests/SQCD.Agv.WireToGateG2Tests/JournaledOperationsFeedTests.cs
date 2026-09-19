using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 清单项标侧读日志的那条路（批次7-13，<c>8005-agv-onboard-hmi#134</c>，PR #143 独立审查）：刷新要合并。
/// </summary>
/// <remarks>
/// 装货过程中每个操作员事件、每份行程快照都会请求刷新一次，而 <c>SqliteWireToGateJournal</c> 全局只有一把
/// 信号量：把每次请求都排成一次读，等于在执行器写检查点的同时反复抢同一把锁。显示要的只是「最新那一份」，
/// 所以正在读的时候再来的请求合并成一次——最多排一个，且那一个一定发生在最后一次请求之后，读到的不会是旧的。
/// </remarks>
public sealed class JournaledOperationsFeedTests
{
    [Fact]
    public async Task RefreshesAskedForWhileAReadIsRunningAreMergedIntoOne()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        GatedJournal journal = new();
        List<WireToGateRecoveryState> published = [];
        JournaledOperationsFeed feed = new(journal, state => published.Add(state), new RecordingLogger());

        Task first = feed.RefreshAsync();
        await journal.WaitForReadStartedAsync(token);

        // 读还没回来，这几次都合并成排在后面的那一次。
        Task[] queued = [feed.RefreshAsync(), feed.RefreshAsync(), feed.RefreshAsync(), feed.RefreshAsync()];

        journal.ReleaseFirstRead();
        await first;
        await Task.WhenAll(queued);

        Assert.Equal(2, journal.Reads);
        Assert.Equal(2, published.Count);
    }

    /// <summary>
    /// 合并后的那一次仍然读得到最新：它在最后一次请求之后才开始读。
    /// </summary>
    [Fact]
    public async Task TheMergedRefreshStartsAfterTheLastRequestSoItReadsTheLatest()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        GatedJournal journal = new();
        List<WireToGateRecoveryState> published = [];
        JournaledOperationsFeed feed = new(journal, state => published.Add(state), new RecordingLogger());

        Task first = feed.RefreshAsync();
        await journal.WaitForReadStartedAsync(token);
        Task merged = feed.RefreshAsync();
        journal.NextReadReturns("22222222-2222-4222-8222-222222222222");
        journal.ReleaseFirstRead();

        await first;
        await merged;

        Assert.Equal(
            "22222222-2222-4222-8222-222222222222",
            published[^1].UnsettledSlotOperationAttemptId);

        // 全部静下来之后再请求一次，照常读一次，不被之前的合并吞掉。
        await feed.RefreshAsync();

        Assert.Equal(3, journal.Reads);
    }

    /// <summary>读失败不抛给调用方，也不挡住后面的刷新：侧是显示，不是能让车停下的事实。</summary>
    [Fact]
    public async Task AFailedReadIsSwallowedAndTheNextRefreshStillRuns()
    {
        GatedJournal journal = new() { GateFirstRead = false, FailNextRead = true };
        List<WireToGateRecoveryState> published = [];
        JournaledOperationsFeed feed = new(journal, state => published.Add(state), new RecordingLogger());

        await feed.RefreshAsync();
        Assert.Empty(published);

        await feed.RefreshAsync();

        Assert.Equal(2, journal.Reads);
        Assert.Single(published);
    }

    /// <summary>一把只会读恢复状态的日志：第一次读挂在闸门上，其余立刻返回。</summary>
    private sealed class GatedJournal : IWireToGateJournal
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();
        private string? _nextAttemptId;
        private int _reads;

        /// <summary>第一次读是否挂在闸门上，等 <see cref="ReleaseFirstRead"/>。</summary>
        public bool GateFirstRead { get; init; } = true;

        public bool FailNextRead { get; set; }

        public int Reads => Volatile.Read(ref _reads);

        public void NextReadReturns(string attemptId)
        {
            lock (_sync)
            {
                _nextAttemptId = attemptId;
            }
        }

        public Task WaitForReadStartedAsync(CancellationToken cancellationToken) =>
            _readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        public void ReleaseFirstRead() => _firstRead.TrySetResult();

        public async Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1 && GateFirstRead)
            {
                _readStarted.TrySetResult();
                await _firstRead.Task.ConfigureAwait(false);
            }

            if (FailNextRead)
            {
                FailNextRead = false;
                throw new IOException("G2 fake: 日志读失败。");
            }

            lock (_sync)
            {
                return WireToGateRecoveryState.Empty with { UnsettledSlotOperationAttemptId = _nextAttemptId };
            }
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteRecoveryStateAsync(WireToGateRecoveryState state, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
            WireToGateDurableMessage expected,
            WireToGateDurableMessage replacement,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WireToGateDurableMessage?> ReadOutgoingByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task MarkOutgoingAcknowledgedAsync(
            string messageId,
            string acceptedContentSha256,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WireToGateAppliedJourneySnapshot>> ReadAppliedJourneySnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WireToGateAppliedJourneySnapshot> SaveAppliedJourneySnapshotAsync(
            WireToGateAppliedJourneySnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> ComputeContentSha256Async(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
