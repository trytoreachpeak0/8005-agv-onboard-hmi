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
                server.OperationResultAcksToDrop = 1;
            });
        await using OnboardAlarmMonitor monitor = OverdueMonitor(harness);
        await harness.WaitForEventAsync("RESULT_ACK_PENDING", token);
        Assert.Equal("COMPLETED", harness.SingleResult("OperationResult").GetProperty("overallOutcome").GetString());

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
                server.OperationResultAcksToDrop = 1;
            });
        await harness.WaitForEventAsync("RESULT_ACK_PENDING", token);
        Assert.Equal(AttemptId, harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId);

        // The server holds the result; it has nothing to command again.
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
    /// 「只差确认」只看发件箱里有没有这次的结果行，不看发送时进没进 catch：装货在会话断开时结束，
    /// <c>SendDurableCoreAsync</c> 在写发件箱之前就以 <c>WIRE_TO_GATE_NOT_READY</c> 拒绝，结果从未落盘、没有东西可补发。
    /// 重连后这次 attempt 不能被当成「只差确认」压掉：它仍是遗留，照旧按实时 IO 中断结算，服务端收到它的结果。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ALoadThatEndedWhileNotReadyHasNoResultToWaitForAndIsStillSettled()
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
        Assert.Null(await harness.Journal.ReadOutgoingByDeduplicationKeyAsync($"operation-result:{AttemptId}", token));
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "OperationResult");

        harness.Server.SendSlotOperationCommandAfterRecovery = false;
        harness.Server.SendJourneySnapshotsAfterRecovery = false;
        await harness.Client.ConnectAndRecoverAsync(token);

        await harness.WaitForInboundAsync("OperationResult", token);
        await Harness.WaitUntilAsync(
            () => harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId is null,
            "the leftover attempt to be settled from the live IO",
            token,
            harness.DescribeEvents);
        Assert.Equal("COMPLETED", harness.SingleResult("OperationResult").GetProperty("overallOutcome").GetString());
        Assert.Contains("上次装货在执行中中断", harness.DescribeEvents(), StringComparison.Ordinal);
        Assert.Equal(1, harness.Io.UnlockCount);
    }
}
