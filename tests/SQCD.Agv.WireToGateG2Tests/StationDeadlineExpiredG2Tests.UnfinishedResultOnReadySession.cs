using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 会话 <c>READY</c> 时一次装货以未完成结果收尾，服务端在那条 <c>DurableAck</c> 后追加
/// <c>SessionReadiness: RECOVERY_REQUIRED</c>（<c>OnboardMessageProcessor.cs</c> 的 <c>OperationResult</c> 分支，
/// 2026-09-04 起如此）之后的恢复投影（<c>trytoreachpeak0/8005-agv-onboard-hmi#139</c>）：本进程刚给出结论、已经
/// 告诉过操作员的这次操作，不是「上个进程留下的操作」，不再投影第二遍，也不称它为「上次」。
/// </summary>
/// <remarks>
/// 这条路在真服务端上不需要任何违约：只要会话 <c>READY</c> 时有一次装卸以 <c>UNKNOWN</c> 收尾。
/// <c>trytoreachpeak0/8005-agv-onboard-hmi#128</c> 把替身对齐到这个行为之后才暴露出来——对齐之前，替身在这种情况下
/// 一直答 <c>READY</c>，追加的那条 <c>RECOVERY_REQUIRED</c> 根本不存在。
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>
    /// 验收第 1 条：装货以 <c>UNKNOWN</c> 收尾、服务端追加 <c>RECOVERY_REQUIRED</c> 之后，
    /// <c>OPERATION_RECOVERY_REQUIRED</c> 只出现一次，措辞是本次操作的「操作失败或状态未知，服务端已收到结果」，
    /// 不含「上次」。
    /// </summary>
    /// <remarks>
    /// 不按固定延时断言：先等那条 <c>RECOVERY_REQUIRED</c> 被车载端应用，再等
    /// <see cref="SettlementProbeJournal"/> 看到「由它触发的恢复判断已经读到这次 attempt 的发件箱行」——那是投影前的
    /// 最后一次 I/O，于是「本该出第二条的时点」是结构上确定的，不是等出来的；随后再走一个完整的安全态往返，让第二轮
    /// 恢复判断也跑完。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnUnfinishedResultOnAReadySessionIsAnnouncedOnceAndNotAsLastTimesOperation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        SettlementProbeJournal? probe = null;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { LockerWaitTimesOut = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                // 真服务端对每条安全态变化回一条 SessionReadiness（control-server#142）。这里用它把「又跑了一轮
                // 恢复判断」变成可等待的事实，不是违约。
                server.SendReadinessAfterSafetyStateChangedAck = true;
            },
            wrapJournal: inner => probe = new SettlementProbeJournal(inner, $"operation-result:{AttemptId}"));

        await harness.WaitForInboundAsync("OperationResult", token);
        Assert.Equal("UNKNOWN", harness.FirstResult("OperationResult").GetProperty("overallOutcome").GetString());
        await harness.WaitForEventAsync("OPERATION_RECOVERY_REQUIRED", token);

        await Harness.WaitUntilAsync(
            () => harness.Client.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the RECOVERY_REQUIRED the server appended after the result's ack",
            token,
            harness.DescribeEvents);
        await harness.LetTwoMoreRecoveryDecisionsRunAsync(() => probe!.SettlementReads, token);

        string events = harness.DescribeEvents();
        Assert.Equal(1, CountRecoveryRequired(events));
        Assert.Contains("1号仓操作失败或状态未知，服务端已收到结果，等待管理员恢复。", events, StringComparison.Ordinal);
        Assert.DoesNotContain("上次", events, StringComparison.Ordinal);
        await harness.AssertRecoveryRequiredStaysAtAsync(1, token);
        Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, harness.Business.CurrentOperationSnapshot?.Stage);
        Assert.Equal(AttemptId, harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId);
    }

    /// <summary>
    /// 验收第 2 条的后一半：断线重连（进程不重启）之后，同一次操作不再被投影第二次。重连后的握手照旧重放那条
    /// 未结算的结果、服务端照旧答 <c>RECOVERY_REQUIRED</c>，去重的依据是「本进程已经为这次操作发布过投影」，
    /// 与连接和会话世代无关——操作员事件的去重键按世代重置，靠它挡不住。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AReconnectAfterAnUnfinishedResultDoesNotAnnounceItAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        SettlementProbeJournal? probe = null;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { LockerWaitTimesOut = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.SendReadinessAfterSafetyStateChangedAck = true;
            },
            wrapJournal: inner => probe = new SettlementProbeJournal(inner, $"operation-result:{AttemptId}"));

        await harness.WaitForInboundAsync("OperationResult", token);
        await harness.WaitForEventAsync("OPERATION_RECOVERY_REQUIRED", token);
        await Harness.WaitUntilAsync(
            () => harness.Client.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the RECOVERY_REQUIRED the server appended after the result's ack",
            token,
            harness.DescribeEvents);

        await harness.Client.DisconnectAsync();
        await harness.Client.ConnectAndRecoverAsync(token);
        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, harness.Client.Current.Readiness);
        await harness.LetTwoMoreRecoveryDecisionsRunAsync(() => probe!.SettlementReads, token);

        string events = harness.DescribeEvents();
        Assert.Equal(1, CountRecoveryRequired(events));
        Assert.DoesNotContain("上次", events, StringComparison.Ordinal);
        await harness.AssertRecoveryRequiredStaysAtAsync(1, token);
    }

    /// <summary>
    /// 验收第 2 条的前一半：结果为 <c>FAILED</c> 时同样只有一条提示。<c>FAILED</c> 是开锁前的预检拒绝
    /// （<c>WireToGateSlotOperationExecutor.CreateRejectedResult</c>），它在任何日志写入之前就返回，于是日志里没有
    /// 未结算的 attempt，恢复判断读不到上下文、本就不投影——服务端照旧把这次操作记进恢复、追加
    /// <c>RECOVERY_REQUIRED</c>，车上仍只有执行路径发的那一条。
    /// </summary>
    /// <remarks>
    /// 半条守护用例，说清楚哪一格在基线上就绿：「只出现一次」那一格是——<c>FAILED</c> 根本不进日志，恢复判断
    /// 读不到上下文；而 <c>DoesNotContain("上次")</c> 那一格在 <c>ae45627</c> 基线上必红，那里这条投影一律叫
    /// 「上次…」。它钉住的是「去重不能靠删掉恢复判断来做」和「FAILED 这条路不许长出第二条提示」。反向验证见
    /// PR 正文：要让「只出现一次」那一格红，得同时注入「预检拒绝也写日志」与「去重判断恒 false」两处。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AFailedResultOnAReadySessionIsAnnouncedOnceToo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        // 1 号仓的锁反馈读不出来：预检判 SLOT_STATE_UNKNOWN，整条命令在开锁前被拒，结果 FAILED。
        FakeIoModuleClient io = new();
        io.SetUnreadable(0);
        SettlementProbeJournal? probe = null;
        await using Harness harness = await Harness.StartAsync(
            io,
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.SendReadinessAfterSafetyStateChangedAck = true;
            },
            wrapJournal: inner => probe = new SettlementProbeJournal(inner, $"operation-result:{AttemptId}"));

        await harness.WaitForInboundAsync("OperationResult", token);
        Assert.Equal("FAILED", harness.FirstResult("OperationResult").GetProperty("overallOutcome").GetString());
        await harness.WaitForEventAsync("OPERATION_RECOVERY_REQUIRED", token);
        await Harness.WaitUntilAsync(
            () => harness.Client.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the RECOVERY_REQUIRED the server appended after the result's ack",
            token,
            harness.DescribeEvents);
        await harness.LetTwoMoreRecoveryDecisionsRunAsync(() => probe!.RecoveryStateSteps, token);

        string events = harness.DescribeEvents();
        Assert.Equal(1, CountRecoveryRequired(events));
        Assert.DoesNotContain("上次", events, StringComparison.Ordinal);
        Assert.Equal(0, harness.Io.UnlockCount);
        await harness.AssertRecoveryRequiredStaysAtAsync(1, token);
    }

    /// <summary>
    /// 结果的 <c>DurableAck</c> 没回来时，执行路径发的是 <c>RESULT_ACK_PENDING</c>、没有宣告过恢复，于是恢复判断
    /// 里那条投影是操作员通往恢复入口的唯一一条（onboard-hmi#131、hmi#109），照旧发——但只发一次：重发被确认后
    /// 服务端追加 <c>RECOVERY_REQUIRED</c>，那一轮以及之后的每一轮都不再重复。措辞也不叫「上次」，这次装卸是本
    /// 进程执行、本进程给的结论。
    /// </summary>
    /// <remarks>
    /// 守护用例。「只出现一次」这一格在这条路径上本就由操作员事件的世代内去重键兜住（两轮恢复判断用同一个
    /// <c>recovery-operation-restored</c> 键），它真正钉住的是另外两格：去重不许把这条唯一的投影一并挡掉，
    /// 措辞不许叫「上次」。反向验证：把 ack 丢失分支的 <c>MarkConcludedHere</c> 改成 <c>announced: true</c>
    /// （本票第一版的写法），它与 <c>AResendRefusedWithAProtocolProblemStillRestoresTheRecoveryEntry</c> 一起红，
    /// 都停在等不到投影。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnUnfinishedResultWhoseAckWasLostIsStillAnnouncedOnceByTheRestore()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        SettlementProbeJournal? probe = null;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { LockerWaitTimesOut = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.OperationResultAcksToDrop = 1;
                server.SendReadinessAfterSafetyStateChangedAck = true;
            },
            wrapJournal: inner => probe = new SettlementProbeJournal(inner, $"operation-result:{AttemptId}"));

        await harness.WaitForEventAsync("RESULT_ACK_PENDING", token);
        await harness.WaitForEventAsync("OPERATION_RECOVERY_REQUIRED", token);
        await Harness.WaitUntilAsync(
            () => harness.Client.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the RECOVERY_REQUIRED the server appended once the resent result was acknowledged",
            token,
            harness.DescribeEvents);
        await harness.LetTwoMoreRecoveryDecisionsRunAsync(() => probe!.SettlementReads, token);

        string events = harness.DescribeEvents();
        Assert.Equal(1, CountRecoveryRequired(events));
        Assert.Contains("装货操作未完成：1号仓，需要管理员恢复。", events, StringComparison.Ordinal);
        Assert.DoesNotContain("上次", events, StringComparison.Ordinal);
        Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, harness.Business.CurrentOperationSnapshot?.Stage);
        await harness.AssertRecoveryRequiredStaysAtAsync(1, token);
    }

    /// <summary>
    /// 上个进程留下的 attempt 在「中断结算的结果发不出去」这一支上，措辞照旧是「上次…」。中断结算只处理遗留，
    /// 本进程从来没有执行过它——只是替它收了尾——所以无论结算的结果有没有送到，它都不是本进程的操作
    /// （onboard-hmi#139 独立审查 F-1）。
    /// </summary>
    /// <remarks>
    /// 组合是三样凑一起：重启后有遗留 → 中断结算报 <c>UNKNOWN</c> 而结果的 <c>DurableAck</c> 没回来（只发
    /// <c>RESULT_ACK_PENDING</c>）→ 下一轮恢复判断重发成功、走到投影。`AckPendingNotUnfinished.cs` 那几条都
    /// 不是这个组合：它们的 attempt 是本进程执行的。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ALeftoverWhoseSettlementResultCouldNotBeSentIsStillRestoredAsLastTimes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        FakeControlServer first;
        await using (Harness beforeRestart = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server => server.StationDepartureDeadlineAt = null,
            journalPath: journalPath))
        {
            first = beforeRestart.Server;
            await beforeRestart.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        }

        SettlementProbeJournal? probe = null;
        await using Harness afterRestart = await Harness.StartAsync(
            new FakeIoModuleClient(),
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.AdoptDurableRecoveryMemoryFrom(first);
                // The interrupted settlement's result gets no ack, so it ends in RESULT_ACK_PENDING with nothing
                // announced; the restore resends it and publishes the recovery entry.
                server.OperationResultAcksToDrop = 1;
                server.SendReadinessAfterSafetyStateChangedAck = true;
            },
            journalPath: journalPath,
            baselineRevision: 2,
            wrapJournal: inner => probe = new SettlementProbeJournal(inner, $"operation-result:{AttemptId}"));

        // The settlement runs inside the handshake readiness's own recovery decision and returns TakenOver, so it
        // publishes nothing; the entry comes from the next decision, which resends the result and gets its ack.
        await afterRestart.WaitForEventAsync("RESULT_ACK_PENDING", token);
        await afterRestart.LetRecoveryDecisionsRunUntilProjectionAsync(() => probe!.SettlementReads, token);
        await afterRestart.WaitForEventAsync("OPERATION_RECOVERY_REQUIRED", token);

        string events = afterRestart.DescribeEvents();
        Assert.Contains("上次装货操作未完成：1号仓，需要管理员恢复。", events, StringComparison.Ordinal);
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        Assert.Equal(
            WireToGateHmiOperationStage.RecoveryRequired,
            afterRestart.Business.CurrentOperationSnapshot?.Stage);
    }

    private static int CountRecoveryRequired(string events) =>
        events.Split(Environment.NewLine).Count(
            line => line.StartsWith("OPERATION_RECOVERY_REQUIRED:", StringComparison.Ordinal));

    private sealed partial class Harness
    {
        /// <summary>
        /// Runs two more recovery decisions to completion, each brought on by a <c>SessionReadiness</c> of its own:
        /// the real server answers every safety state change with one (control-server#142), and the vehicle's receive
        /// loop is serial, so the decision the server's appended <c>RECOVERY_REQUIRED</c> started is over by the time
        /// the second one reads this attempt's outbox row. A projection, when it comes, comes right after that read:
        /// measured at 23 ms on this harness, with nothing else between the two.
        /// </summary>
        public async Task LetTwoMoreRecoveryDecisionsRunAsync(
            Func<int> progressed,
            CancellationToken cancellationToken)
        {
            for (int round = 0; round < 2; round++)
            {
                int before = progressed();
                await Server.RequestSafetyStateSnapshotAsync();
                await WaitUntilAsync(
                    () => progressed() > before,
                    $"recovery decision {round + 1} of 2 to get past the step a projection follows",
                    cancellationToken,
                    DescribeEvents);
            }
        }

        /// <summary>
        /// Like <see cref="LetTwoMoreRecoveryDecisionsRunAsync"/>, but for the one caller that goes on to wait for
        /// the projection itself rather than to assert it did not repeat.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The difference matters because the second read is not a fact that has to happen. A decision reads this
        /// attempt's outbox row only while the attempt is still unsettled: once the resent result is acknowledged,
        /// <c>UnsettledSlotOperationAttemptId</c> no longer matches and the decision returns early
        /// (<c>WireToGateBusinessService.cs</c>, before <c>TrySettleInterruptedOperationAsync</c>), so that read
        /// never comes. Whether it comes at all turns on when the acknowledgement lands relative to the cached
        /// recovery state -- a race, not a duration, which is why waiting longer does not help.
        /// </para>
        /// <para>
        /// So this waits for either fact: the read, or the projection that read would have produced. Both say the
        /// decision got to where it was going. Observed in CI run 35486462929, where the wait timed out after its
        /// full 10 seconds with no stall compensation at all, and the events it printed already contained
        /// <c>OPERATION_RECOVERY_REQUIRED</c> -- the projection was there, only the second read never was.
        /// </para>
        /// <para>
        /// <b>The other four call sites must keep the old helper.</b> They assert afterwards that the projection did
        /// NOT come a second time, so their second round has to actually run: accepting an already-published
        /// projection there would let the round end early and leave "it did not repeat" untested.
        /// </para>
        /// </remarks>
        public async Task LetRecoveryDecisionsRunUntilProjectionAsync(
            Func<int> progressed,
            CancellationToken cancellationToken)
        {
            for (int round = 0; round < 2; round++)
            {
                int before = progressed();
                await Server.RequestSafetyStateSnapshotAsync();
                await WaitUntilAsync(
                    () => progressed() > before || HasEvent("OPERATION_RECOVERY_REQUIRED"),
                    $"recovery decision {round + 1} of 2 to reach the step a projection follows, "
                        + "or the projection itself",
                    cancellationToken,
                    DescribeEvents);
            }
        }

        /// <summary>
        /// Holds the count for half a second, asserting throughout rather than reading once at the end: the last
        /// decision's own projection would land within tens of milliseconds of the read that anchored it.
        /// </summary>
        public async Task AssertRecoveryRequiredStaysAtAsync(int expected, CancellationToken cancellationToken)
        {
            DateTimeOffset until = DateTimeOffset.UtcNow.AddMilliseconds(500);
            while (DateTimeOffset.UtcNow < until)
            {
                string events = DescribeEvents();
                Assert.Equal(expected, CountRecoveryRequired(events));
                await Task.Delay(10, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 透传日志，只数「这次 attempt 的 <c>OperationResult</c> 发件箱行被按去重键读了几次」。那一步只出现在
    /// <c>TrySettleInterruptedOperationAsync</c> 里，是恢复投影发布之前的最后一次 I/O，所以它的次数就是
    /// 「恢复判断跑到了要不要投影这一步」的次数。
    /// </summary>
    private sealed class SettlementProbeJournal(IWireToGateJournal inner, string resultKey) : IWireToGateJournal
    {
        private int _settlementReads;
        private int _recoveryStateSteps;

        public int SettlementReads => Volatile.Read(ref _settlementReads);

        /// <summary>
        /// 恢复状态的「读缓存」步跑了几次。<c>ReadRecoveryStateCachedAsync</c> 走的就是这个重载
        /// （onboard-hmi#129），而它是恢复判断的第一步——结果为 <c>FAILED</c> 时日志里没有未结算 attempt，
        /// 判断读完状态就返回，走不到发件箱那一步，于是只有这个计数能看见它跑过。
        /// </summary>
        public int RecoveryStateSteps => Volatile.Read(ref _recoveryStateSteps);

        public Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(deduplicationKey, resultKey, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _settlementReads);
            }

            return inner.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);
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

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _recoveryStateSteps);
            return inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);
        }

        public Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default) =>
            inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);

        public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
            WireToGateDurableMessage expected,
            WireToGateDurableMessage replacement,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceOutgoingForReplayAsync(expected, replacement, cancellationToken);

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
