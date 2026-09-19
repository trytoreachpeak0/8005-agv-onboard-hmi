using System.Text.Json;
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
    [Trait("IntegrationSlice", "FP-IS-05")]
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
    /// 进度消息仍只在 <c>Ready</c> 时发（守护）：重连后会话 <c>RecoveryRequired</c> 期间装货走完核对与收尾，这两条进度
    /// 照旧「未能发送，不影响仓位判定」，新连接上没有这两个阶段的 <c>OperationProgress</c>——ADR 只放行结果补报，进度是遥测。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-CONNECTION-LOSS-SAFE-FINISH")]
    public async Task ProgressIsStillNotSentWhileTheSessionIsRecoveryRequired()
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
            () => harness.HasEvent("OPERATION_COMPLETED") || harness.HasEvent("RESULT_ACK_PENDING"),
            "the load to finish on the vehicle",
            token,
            harness.DescribeEvents);

        // Phases reached only after the reconnect. The handshake may replay a progress the first connection left
        // unacknowledged; that is the outbox, not a send while RecoveryRequired.
        Assert.NotEmpty(harness.ProgressMessages(WireToGateHmiOperationStage.Verifying));
        Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            item => item.Connection == reconnect
                && item.MessageType == "OperationProgress"
                && ProgressPhase(item.WireLine) is "VERIFYING" or "SAFE_FINISH");
    }

    private static string? ProgressPhase(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("payload").GetProperty("phase").GetString();
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

    /// <summary>
    /// 就绪翻转的竞态里，原始 <c>SlotOperationCommand</c> 撞上 <c>RecoveryRequired</c> 的会话：车载端拒绝它，而且这条拒绝
    /// 发得出去（照 onboard-hmi#119 给续行拒绝开的口子），关联到那条命令的 <c>messageId</c>；仓门一次也没动。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SESSION-RECOVERY-HAPPY")]
    public async Task ASlotOperationCommandRefusedWhileRecoveryRequiredStillGetsItsRejectionOut()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.ForceRecoveryRequiredReadiness = true;
            });

        await harness.WaitForInboundAsync("SlotOperationCommandRejected", token);

        var command = Assert.Single(harness.Server.SentEnvelopes, item => item.MessageType == "SlotOperationCommand");
        var rejection = Assert.Single(
            harness.Server.ReceivedEnvelopes,
            item => item.MessageType == "SlotOperationCommandRejected");
        using JsonDocument document = JsonDocument.Parse(rejection.WireLine);
        Assert.Equal(command.MessageId, document.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(
            "ACTION_NOT_ALLOWED_IN_STATE",
            document.RootElement.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, harness.Client.Current.Readiness);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// 被拒过的 attempt 在会话就绪后再下发一次：装货照常执行，结果照常落盘并送达。拒绝与结果不能共用同一个
    /// <c>messageId</c>——发件箱按 <c>messageId</c> 唯一，拒绝先占了它，结果就永远存不进去（独立审查发现）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SESSION-RECOVERY-HAPPY")]
    public async Task AnAttemptRefusedWhileRecoveryRequiredStillGetsItsResultOutWhenReissued()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { SimulateOperatorLoad = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.ForceRecoveryRequiredReadiness = true;
            });
        await harness.WaitForInboundAsync("SlotOperationCommandRejected", token);
        Assert.Equal(0, harness.Io.UnlockCount);

        await harness.ReceiveMidSessionReadinessAsync(token);
        await harness.Server.ResendSlotOperationCommandAsync();

        await Harness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(item => item.MessageType == "OperationResult")
                && harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId is null,
            "the reissued load's result on the server and the load settled",
            token,
            harness.DescribeEvents);
        var rejection = Assert.Single(
            harness.Server.ReceivedEnvelopes,
            item => item.MessageType == "SlotOperationCommandRejected");
        var result = Assert.Single(harness.Server.ReceivedEnvelopes, item => item.MessageType == "OperationResult");
        Assert.NotEqual(rejection.MessageId, result.MessageId);
        Assert.Equal("COMPLETED", harness.SingleResult("OperationResult").GetProperty("overallOutcome").GetString());
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
