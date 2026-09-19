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
}
