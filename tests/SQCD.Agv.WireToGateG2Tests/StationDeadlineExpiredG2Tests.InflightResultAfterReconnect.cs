using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 装货进行中断线重连，车载端必须把这次装货的结果交出去（<c>trytoreachpeak0/8005-agv-onboard-hmi#127</c>，
/// control-server#189 第二步）。服务端收到新代次的恢复状态报告，里面报着这次还没结的 attempt，没对上账就只给
/// <c>RECOVERY_REQUIRED</c>；它等的正是这次装货的 <c>OperationResult</c>（ADR-cross-0028 的「结果补报」、
/// ADR-cross-0029 第 4 步）。车载端曾经因为会话不是 <c>Ready</c> 不发这条结果，两端互等，车停在站上不接活。
/// </summary>
public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>
    /// 断线重连后会话停在 <c>RecoveryRequired</c>，操作员随后放货关门：结果照样发出，用的是中断结算路径同一个
    /// <c>messageId</c>（即 attempt 本身），服务端收下后给 <c>READY</c>，装货结算，只开过一次锁，也不走中断结算。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-CONNECTION-LOSS-SAFE-FINISH")]
    public async Task ALoadThatEndsWhileTheSessionAwaitsItsResultSendsTheResultAndTheSessionComesBackReady()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.HoldReadinessForUnreconciledAttempt = true;
            });
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);

        int reconnect = await harness.ReconnectAwaitingTheLoadsResultAsync(token);
        harness.Io.CloseDoor(0, cargo: true);

        await Harness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(
                item => item.Connection == reconnect && item.MessageType == "OperationResult"),
            "the load's OperationResult on the reconnected session",
            token,
            harness.DescribeEvents);
        await Harness.WaitUntilAsync(
            () => harness.Client.Current.Readiness == WireToGateSessionReadiness.Ready
                && harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId is null,
            "the session back to Ready and the load settled",
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

    /// <summary>
    /// 已送达、已对账的结果不再发、也不再结算：上一条的场景走完之后再断一次、再连一次，新连接上没有
    /// <c>OperationResult</c>，「操作完成」只出一次，journal 仍是已结算，锁只开过一次。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AReconciledResultIsNeitherSentNorSettledAgainOnTheNextReconnect()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.HoldReadinessForUnreconciledAttempt = true;
            });
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        await harness.ReconnectAwaitingTheLoadsResultAsync(token);
        harness.Io.CloseDoor(0, cargo: true);
        await Harness.WaitUntilAsync(
            () => harness.Client.Current.Readiness == WireToGateSessionReadiness.Ready
                && harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId is null,
            "the session back to Ready and the load settled",
            token,
            harness.DescribeEvents);

        await harness.Client.DisconnectAsync();
        await harness.Client.ConnectAndRecoverAsync(token);
        int third = harness.Server.Received.Max(item => item.Connection);
        Assert.Equal(WireToGateSessionReadiness.Ready, harness.Client.Current.Readiness);
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);

        Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            item => item.Connection == third && item.MessageType == "OperationResult");
        Assert.Single(harness.Server.ReceivedEnvelopes, item => item.MessageType == "OperationResult");
        Assert.Equal(1, harness.CountEvents("OPERATION_COMPLETED"));
        WireToGateRecoveryState state = harness.ReadRecoveryState(token);
        Assert.Null(state.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, state.ProvenRecoveryCheckpoint);
        Assert.Equal(1, harness.Io.UnlockCount);
    }

    private sealed partial class Harness
    {
        public int CountEvents(string kind)
        {
            lock (_events)
            {
                return _events.Count(item => item.Kind == kind);
            }
        }

        /// <summary>
        /// Drops the connection while the load waits for its operator and reconnects to a server that holds the
        /// session at RECOVERY_REQUIRED for this very attempt. Returns the new connection's index.
        /// </summary>
        public async Task<int> ReconnectAwaitingTheLoadsResultAsync(CancellationToken cancellationToken)
        {
            Server.SendSlotOperationCommandAfterRecovery = false;
            Server.SendJourneySnapshotsAfterRecovery = false;
            await Client.DisconnectAsync();
            await Client.ConnectAndRecoverAsync(cancellationToken);
            Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, Client.Current.Readiness);
            return Server.Received.Max(item => item.Connection);
        }
    }
}
