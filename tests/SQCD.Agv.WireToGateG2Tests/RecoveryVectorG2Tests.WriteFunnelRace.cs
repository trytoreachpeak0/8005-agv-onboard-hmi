using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 恢复状态的每一处写入都在日志锁内基于最新状态合并，而不是把锁外读到的整条记录写回去
/// （批次 7-19，<c>trytoreachpeak0/8005-agv-onboard-hmi#136</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 这些用例覆盖的是业务服务与向量执行器里的写入点。每一条都让一个竞争写入**确定地**落在目标路径的
/// 读与写之间——由包装日志按调用栈认出目标路径、在放行它的写入之前插进去，不靠计时、不靠 sleep。
/// </para>
/// <para>
/// <b>同一份用例在两个版本上都成立。</b>夹具认「写入」时同时接受旧名字（<c>WriteRecoveryStateAsync</c>、
/// <c>WriteRecoveryStateCachedAsync</c>）与新名字（<c>UpdateRecoveryStateCachedAsync</c>），所以收口
/// 之后不需要改动用例来适配实现。基线上目标路径把竞争写入盖掉、用例红；收口之后改动函数在锁内读到
/// 竞争写入、用例绿。
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// 第 7 处（<c>WireToGateBusinessService.HardwareRecovery.cs</c> 清 <c>ForcedIsolation</c>）：
    /// 硬件恢复记录被服务端 <c>RECORDED</c> 之后重读状态、清隔离，这中间执行器写下一个检查点。
    /// 基线上那个检查点被整条盖掉，车辆忘掉自己正在做的操作。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ClearingAnIsolationNeverOverwritesACheckpointWrittenWhileItClears()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string checkpointAttemptId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        WriteFunnelRaceJournal? race = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true,
            wrapJournal: inner => race = new WriteFunnelRaceJournal(inner));
        await harness.IsolateByForcedRecoveryAsync(token);

        // 隔离记录已经带上待发记录，说明这一次写入就是「记录被 RECORDED 之后清隔离」那一次，
        // 不是它前面那次落待发记录的写入。按状态认，不按第几次写入认。
        race!.BeforeTheNextWriteFrom(
            "SubmitHardwareRecoveryRecordCoreAsync",
            state => state.ForcedIsolation?.PendingRecord is not null,
            async (inner, cancellationToken) => await inner.UpdateRecoveryStateAsync(
                state => state with
                {
                    UnsettledSlotOperationAttemptId = checkpointAttemptId,
                    ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.SafeFinishReached
                },
                cancellationToken));

        Assert.True(await harness.Business.SubmitHardwareRecoveryRecordAsync(
            "更换 1、2 号仓锁体，复测锁反馈正常。", token));

        Assert.True(race.Fired, "竞争写入没有落在目标路径的读与写之间");
        WireToGateRecoveryState persisted = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(persisted.ForcedIsolation);
        Assert.Equal(checkpointAttemptId, persisted.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, persisted.ProvenRecoveryCheckpoint);
    }

    /// <summary>
    /// 第 5 处（<c>WireToGateBusinessService.cs</c> 的「修复后恢复原操作」申请，写恢复会话申请字段）：
    /// 读与写之间，同一 attempt 被记账。基线上 <c>OperationContext</c> 与
    /// <c>UnsettledSlotOperationAttemptId</c> 随整条记录复活。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task TheResumeRequestNeverRevivesAnAttemptRecordedWhileItJournalsItsRequest()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WriteFunnelRaceJournal? race = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = AttemptId,
            wrapJournal: inner => race = new WriteFunnelRaceJournal(inner));

        race!.BeforeTheNextWriteFrom(
            "RequestResumeAfterRepairAsync",
            static _ => true,
            async (inner, cancellationToken) => await inner.UpdateRecoveryStateAsync(
                ResultRecorded,
                cancellationToken));
        await harness.Business.RequestResumeAfterRepairAsync(null, token);

        Assert.True(race.Fired, "竞争写入没有落在目标路径的读与写之间");
        WireToGateRecoveryState persisted = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(persisted.UnsettledSlotOperationAttemptId);
        Assert.Null(persisted.OperationContext);
        Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, persisted.ProvenRecoveryCheckpoint);
    }

    /// <summary>
    /// 第 4 处（<c>WireToGateRecoveryVectorExecutor.WriteVectorStateAsync</c>，向量的检查点）：
    /// 两个检查点之间，一份迟到的待发结果被记进 journal。基线上下一个检查点按向量开始时的副本
    /// 把 <c>PendingResults</c> 写回空，那份结果从此不会再被任何会话补报。
    /// </summary>
    /// <remarks>
    /// 竞争者选「待发结果」而不是「会话被释放」，是因为释放会话会让向量执行本身失败（作用域校验），
    /// 于是结果发不出去、用例超时在等待上——那时候红的原因是注入太粗，不是缺陷。待发结果不在向量执行
    /// 读的任何判据里，注入之后目标路径照常走完，红的只剩「它被写回去了」这一件事。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AVectorCheckpointNeverDropsAPendingResultRecordedWhileItRuns()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WriteFunnelRaceJournal? race = null;
        // 目标仓位留空：故障货物交接因此走到安全收尾而不用真的开锁，向量的检查点与结果观测照样写。
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            wrapJournal: inner => race = new WriteFunnelRaceJournal(inner));

        // 向量已经落在 journal 上，说明这一次写入是向量执行中的检查点，而不是它前面
        // 「盖章向量上下文」那一次。
        race!.BeforeTheNextWriteFrom(
            "WriteVectorStateAsync",
            state => state.RecoveryVector is not null,
            RecordALateResultAsync);
        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync(
            "现场确认故障仓货物需要交接处理。", token));
        await harness.WaitForResultAsync("FaultCargoRecoveryResult", token);

        Assert.True(race.Fired, "竞争写入没有落在目标路径的读与写之间");
        WireToGateRecoveryState persisted = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(
            [LateResultMessageId],
            persisted.PendingResults.Select(result => result.MessageId).ToArray());
    }

    /// <summary>
    /// 第 3 处（<c>WireToGateRecoveryVectorExecutor.EnsureResultObservedAtAsync</c>，写结果观测时间）：
    /// 读与写之间一份迟到的待发结果被记进 journal，基线上随整条记录被写回空。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task WritingTheResultObservationNeverDropsAPendingResultRecordedBeforeIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WriteFunnelRaceJournal? race = null;
        // 目标仓位留空，同上：不用真的开锁，结果观测时间照样写。
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            wrapJournal: inner => race = new WriteFunnelRaceJournal(inner));

        // 向量已落盘、观测时间还没写：这正是 EnsureResultObservedAtAsync 会写的那一次。
        race!.BeforeTheNextWriteFrom(
            "EnsureResultObservedAtAsync",
            static state => state.RecoveryVector is not null && state.RecoveryResultObservedAt is null,
            RecordALateResultAsync);
        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync(
            "现场确认故障仓货物需要交接处理。", token));
        await harness.WaitForResultAsync("FaultCargoRecoveryResult", token);

        Assert.True(race.Fired, "竞争写入没有落在目标路径的读与写之间");
        WireToGateRecoveryState persisted = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(
            [LateResultMessageId],
            persisted.PendingResults.Select(result => result.MessageId).ToArray());
        Assert.NotNull(persisted.RecoveryResultObservedAt);
    }

    /// <summary>
    /// 第 7 处的第三条：向量准备落盘（<c>RecoveryVectors.cs</c> 的
    /// <c>WriteRecoveryVectorPreparedAsync</c>）与一份迟到的待发结果交错。基线上向量准备按调用方
    /// 读到的副本整条写回，那份结果被写成空。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task PreparingAVectorNeverDropsAPendingResultRecordedBeforeIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WriteFunnelRaceJournal? race = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            wrapJournal: inner => race = new WriteFunnelRaceJournal(inner));

        race!.BeforeTheNextWriteFrom(
            "WriteRecoveryVectorPreparedAsync",
            static _ => true,
            RecordALateResultAsync);
        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync(
            "现场确认故障仓货物需要交接处理。", token));
        await harness.WaitForResultAsync("FaultCargoRecoveryResult", token);

        Assert.True(race.Fired, "竞争写入没有落在目标路径的读与写之间");
        WireToGateRecoveryState persisted = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(
            [LateResultMessageId],
            persisted.PendingResults.Select(result => result.MessageId).ToArray());
    }

    /// <summary>这些用例共用的竞争写入：一份迟到的待发结果被记进 journal。</summary>
    /// <remarks>
    /// 它落在目标路径的读与写之间。基线上目标路径把锁外读到的整条记录写回去，<c>PendingResults</c>
    /// 随之回到空——那份结果从此不会被任何会话补报，而且什么都不会报错。
    /// </remarks>
    private static async Task RecordALateResultAsync(
        IWireToGateJournal inner,
        CancellationToken cancellationToken) =>
        await inner.UpdateRecoveryStateAsync(
            state => state with
            {
                PendingResults =
                [
                    new WireToGatePendingResult(
                        "OperationResult",
                        LateResultMessageId,
                        state.UnsettledSlotOperationAttemptId ?? AttemptId,
                        LateResultContentSha256)
                ]
            },
            cancellationToken);

    private const string LateResultMessageId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";

    private const string LateResultContentSha256 =
        "3333333333333333333333333333333333333333333333333333333333333333";

    /// <summary>
    /// 在目标路径的下一次恢复状态**写入**之前跑一次竞争写入。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「目标路径」按调用栈里的方法名认。「写入」同时接受旧名字与新名字，所以收口前后同一份用例都成立
    /// ——这条夹具不能靠「实现调的是哪个接口方法」来判断，否则收口本身就会把用例变成另一个用例。
    /// </para>
    /// <para>
    /// 缓存读（<c>ReadRecoveryStateCachedAsync</c>）走的也是 <c>UpdateRecoveryStateAsync</c>，
    /// 但它写不了东西；把它排掉，注入才落在读之后而不是读之前。
    /// </para>
    /// <para>
    /// 认「哪一次写入」用谓词看当时盘上的状态，不数第几次：一条路径上的写入次数会随代码演进变化，
    /// 而「隔离记录已带待发记录」「向量已落盘」这类状态判据是它本来的形状。
    /// </para>
    /// </remarks>
    private sealed class WriteFunnelRaceJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        /// <summary>写入恢复状态的入口名，收口前后两套都在。</summary>
        private static readonly string[] WriteEntryPoints =
        [
            "WriteRecoveryStateCachedAsync",
            "UpdateRecoveryStateCachedAsync"
        ];

        private string? _target;
        private Func<WireToGateRecoveryState, bool>? _when;
        private Func<IWireToGateJournal, CancellationToken, Task>? _race;
        private int _fired;
        private Exception? _raceFailure;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        /// <summary>注入自己抛出的异常，或 <c>null</c>。用例失败时先看它。</summary>
        public Exception? RaceFailure => Volatile.Read(ref _raceFailure);

        /// <param name="target">目标业务方法名，按调用栈认。</param>
        /// <param name="when">看当时盘上的状态，决定这一次写入是不是要竞争的那一次。</param>
        /// <param name="race">竞争写入，直接走内层日志，所以它自己不会再触发注入。</param>
        public void BeforeTheNextWriteFrom(
            string target,
            Func<WireToGateRecoveryState, bool> when,
            Func<IWireToGateJournal, CancellationToken, Task> race)
        {
            Volatile.Write(ref _when, when);
            Volatile.Write(ref _race, race);
            Volatile.Write(ref _target, target);
        }

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            UpdateRecoveryStateAsync(change, static _ => { }, cancellationToken);

        public async Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default)
        {
            await RaceIfThisIsTheWriteAsync(cancellationToken);
            return await inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);
        }

        private async Task RaceIfThisIsTheWriteAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _target) is not { } target)
            {
                return;
            }

            string stack = Environment.StackTrace;
            if (!stack.Contains(target, StringComparison.Ordinal)
                || stack.Contains("ReadRecoveryStateCachedAsync", StringComparison.Ordinal))
            {
                return;
            }

            // 直接调接口的写入点栈里没有中间层名字；经业务服务缓存写的那些有。两种都是写入。
            bool throughCache = WriteEntryPoints.Any(entry => stack.Contains(entry, StringComparison.Ordinal));
            bool direct = !stack.Contains("WriteRecoveryStateCachedAsync", StringComparison.Ordinal)
                && !stack.Contains("UpdateRecoveryStateCachedAsync", StringComparison.Ordinal);
            if (!throughCache && !direct)
            {
                return;
            }

            if (Volatile.Read(ref _when) is { } when
                && !when(await inner.ReadRecoveryStateAsync(cancellationToken)))
            {
                return;
            }

            if (Interlocked.Exchange(ref _race, null) is not { } race)
            {
                return;
            }

            Volatile.Write(ref _target, null);
            try
            {
                await race(inner, cancellationToken);
            }
            catch (Exception exception)
            {
                // 注入自己失败了是测试的问题，不是产品的。记下来，别让它伪装成「窗口没命中」，
                // 也别让业务层的 catch 把它吞成一次普通的失败。
                Volatile.Write(ref _raceFailure, exception);
                throw;
            }

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
