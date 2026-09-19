using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 已完成、只差确认的装货不是「上次没做完的操作」（<c>trytoreachpeak0/8005-agv-onboard-hmi#124</c> 第 1 条，
/// onboard-hmi#120 审查后续）：装货 <c>COMPLETED</c>，结果已写进持久发件箱，<c>DurableAck</c> 却没回来，于是
/// journal 里这次 attempt 没有被标成已结算。之后每条 <c>SessionReadiness</c> 都曾把它报成「上次装货操作未完成……
/// 需要管理员恢复」，并出 <c>ONBOARD_SLOT_OPERATION_UNFINISHED</c>。
/// </summary>
public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>
    /// 结果的确认超时之后连续收到几条 <c>SessionReadiness</c>：不发布 <c>RecoveryRequired</c> 投影，不出
    /// <c>ONBOARD_SLOT_OPERATION_UNFINISHED</c>，HMI 停在「结果等待确认」。
    /// </summary>
    /// <remarks>
    /// 服务端这里一条确认都不回：onboard-hmi#127 起车载端会用同一 <c>messageId</c> 重发只差确认的结果，只丢一次确认的话
    /// 重发就结算了，那是 <see cref="AResultWhoseAckIsLostMidSessionIsSentOnceMoreAndSettles"/> 的事。这条守的仍是
    /// 「确认一直不来也不报未完成」。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ACompletedLoadWhoseResultAckIsLateIsNotRestoredAsUnfinished()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { SimulateOperatorLoad = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.OperationResultAcksToDrop = int.MaxValue;
            });
        await using OnboardAlarmMonitor monitor = OverdueMonitor(harness);
        await harness.WaitForEventAsync("RESULT_ACK_PENDING", token);
        Assert.All(
            harness.Server.ReceivedEnvelopes.Where(item => item.MessageType == "OperationResult"),
            item => Assert.Equal(AttemptId, item.MessageId));
        Assert.Equal("COMPLETED", harness.FirstResult("OperationResult").GetProperty("overallOutcome").GetString());

        for (int i = 0; i < 3; i++)
        {
            await harness.ReceiveMidSessionReadinessAsync(token);
            await harness.AssertNoRecoveryProjectionForAWhileAsync(token);
        }

        await monitor.EvaluateOnceAsync(token);
        Assert.DoesNotContain(
            LatestAlarms(harness.Server),
            item => item.GetProperty("code").GetString() == OnboardAlarmCodes.SlotOperationUnfinished);
        Assert.Equal(WireToGateHmiOperationStage.Reporting, harness.Business.CurrentOperationSnapshot?.Stage);
        Assert.Equal(1, harness.Io.UnlockCount);
    }

    /// <summary>
    /// 会话中途结果的 <c>DurableAck</c> 丢了、链路没断，之后也没有任何会话事件：车载端用同一去重键与 <c>messageId</c>
    /// 自己重发一次，确认到了就照常结算（onboard-hmi#124 审查转来的第 1 条）。在此之前它停在「结果等待确认」，
    /// 直到下一次重连或下一单才可能解开。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AResultWhoseAckIsLostMidSessionIsSentOnceMoreAndSettles()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { SimulateOperatorLoad = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.OperationResultAcksToDrop = 1;
            });
        await harness.WaitForEventAsync("RESULT_ACK_PENDING", token);

        await Harness.WaitUntilAsync(
            () => harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId is null,
            "the result sent once more and the load settled",
            token,
            harness.DescribeEvents);

        (int Connection, string MessageType, string MessageId, string WireLine)[] results =
            [.. harness.Server.ReceivedEnvelopes.Where(item => item.MessageType == "OperationResult")];
        Assert.Equal(2, results.Length);
        Assert.All(results, item => Assert.Equal(AttemptId, item.MessageId));
        Assert.All(results, item => Assert.Equal(1, item.Connection));
        Assert.DoesNotContain(harness.Server.Received, item => item.Connection > 1);
        Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, harness.ReadRecoveryState(token).ProvenRecoveryCheckpoint);
        Assert.Equal(WireToGateHmiOperationStage.Completed, harness.Business.CurrentOperationSnapshot?.Stage);
        Assert.Equal(1, harness.Io.UnlockCount);
    }

    /// <summary>
    /// 确认到达之后照常结算：断线重连，握手按同一 <c>messageId</c> 补发结果、服务端这次回了确认；之后的就绪让 journal
    /// 转为已结算（<c>ResultRecorded</c>，这次装货成为可更正的最近一次装货），全程不报未完成、不再开锁。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ACompletedLoadIsSettledOnceItsLateResultIsAcknowledged()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { SimulateOperatorLoad = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                // No ack at all on this connection, so the vehicle's own resend (onboard-hmi#127) cannot settle it
                // first: what settles it here is the handshake's replay.
                server.OperationResultAcksToDrop = int.MaxValue;
            });
        await harness.WaitForEventAsync("RESULT_ACK_PENDING", token);
        Assert.Equal(AttemptId, harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId);

        // The server holds the result; it has nothing to command again.
        harness.Server.OperationResultAcksToDrop = 0;
        harness.Server.SendSlotOperationCommandAfterRecovery = false;
        await harness.Client.DisconnectAsync();
        await harness.Client.ConnectAndRecoverAsync(token);

        await Harness.WaitUntilAsync(
            () => harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId is null,
            "the journal to settle the acknowledged attempt",
            token,
            harness.DescribeEvents);
        WireToGateRecoveryState settled = harness.ReadRecoveryState(token);
        Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, settled.ProvenRecoveryCheckpoint);
        Assert.Equal(AttemptId, settled.LastCompletedLoadOperationContext?.SlotOperationAttemptId);

        string[] results = [.. harness.Server.ReceivedEnvelopes
            .Where(item => item.MessageType == "OperationResult")
            .Select(item => item.MessageId)
            .Distinct()];
        Assert.Single(results);
        Assert.False(harness.HasEvent("OPERATION_RECOVERY_REQUIRED"), harness.DescribeEvents());
        Assert.Equal(1, harness.Io.UnlockCount);
    }

    /// <summary>
    /// 装货在会话断开时结束：<c>OperationResult</c> 先落盘再看能不能发（manifest 的 <c>durableBeforeSend</c>），重连握手按
    /// 同一 <c>messageId</c> 补发（ADR-cross-0029 第 4 步），服务端确认后这次装货照常结算——发出去的是执行器自己那份结果，
    /// 不是事后按实时 IO 重算的一份，所以不走中断结算。
    /// </summary>
    /// <remarks>
    /// 这条是 onboard-hmi#124 守护测试 <c>ALoadThatEndedWhileNotReadyHasNoResultToWaitForAndIsStillSettled</c> 的改写，预期是
    /// 有意改变的（onboard-hmi#127）：那时发送口在写发件箱之前就以 <c>WIRE_TO_GATE_NOT_READY</c> 拒绝，结果从未落盘，重连后
    /// 只能按实时 IO 中断结算。它守的那条判断没变——「只差确认」只看发件箱里真有没有这次的结果行——只是现在那一行在了。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ALoadThatEndedWhileDisconnectedPutsItsResultOnFileAndTheHandshakeReplaysIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server => server.StationDepartureDeadlineAt = null);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);

        await harness.Client.DisconnectAsync();
        harness.Io.CloseDoor(0, cargo: true);
        await harness.WaitForEventAsync("RESULT_ACK_PENDING", token);
        WireToGateDurableMessage? onFile =
            await harness.Journal.ReadOutgoingByDeduplicationKeyAsync($"operation-result:{AttemptId}", token);
        Assert.NotNull(onFile);
        Assert.Equal(AttemptId, onFile.MessageId);
        Assert.False(onFile.Acknowledged);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "OperationResult");

        harness.Server.SendSlotOperationCommandAfterRecovery = false;
        harness.Server.SendJourneySnapshotsAfterRecovery = false;
        await harness.Client.ConnectAndRecoverAsync(token);

        await Harness.WaitUntilAsync(
            () => harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId is null,
            "the replayed result to settle the load",
            token,
            harness.DescribeEvents);
        Assert.Equal(
            AttemptId,
            Assert.Single(harness.Server.ReceivedEnvelopes, item => item.MessageType == "OperationResult").MessageId);
        Assert.Equal("COMPLETED", harness.SingleResult("OperationResult").GetProperty("overallOutcome").GetString());
        Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, harness.ReadRecoveryState(token).ProvenRecoveryCheckpoint);
        Assert.DoesNotContain("上次装货在执行中中断", harness.DescribeEvents(), StringComparison.Ordinal);
        Assert.Equal(1, harness.Io.UnlockCount);
    }
}
