using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 替身自证：<see cref="FakeControlServer"/> 的握手就绪与真服务端一致（<c>trytoreachpeak0/8005-agv-onboard-hmi#128</c>）。
/// 真服务端收到恢复状态报告后，报告里有未结算的 attempt 或待补报的结果就答 <c>RECOVERY_REQUIRED</c>
/// （<c>OnboardMessageProcessor.cs</c> 的 <c>RecoveryStateReport</c> 分支、<c>WireToGateStore.DecideReadinessAsync</c> 的
/// <c>noPendingFacts</c>），会话不是 <c>Ready</c> 时不推行程快照与仓位命令（<c>JourneyRuntimeEngine.AdvanceAsync</c> 的就绪门），
/// 结果补报之后才就绪，门后的报文随后发出。断言用替身自己记录的线上顺序。
/// </summary>
public sealed partial class StationDeadlineExpiredG2Tests
{
    private static readonly string[] GatedMessageTypes =
    [
        "VehicleBusinessStateSnapshot",
        "CurrentStopWorklistSnapshot",
        "UpcomingStopPlanSnapshot",
        "SublotEntryRequested",
        "SlotOperationCommand",
        "PreDepartureSafetyCheck"
    ];

    /// <summary>
    /// 装货等操作员时断线重连：报告里带着这次没结算的 attempt，替身答 <c>RECOVERY_REQUIRED</c>（<c>SESSION_RECOVERY_REQUIRED</c>），
    /// 其后什么行程报文都不推；操作员放货关门、结果到达并被确认之后，替身在那条 <c>DurableAck</c> 后面追加 <c>READY</c>，
    /// 门后的快照这才发出，而且只发一次。这条装货命令已经被结果应答，门后不再重发（真服务端 <c>SettleAnsweredCommandAsync</c>）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-CONNECTION-LOSS-SAFE-FINISH")]
    public async Task TheDoubleHoldsJourneyAndCommandsBehindRecoveryRequiredUntilTheReportedAttemptsResultArrives()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                // The same snapshot content on every connection, so the vehicle takes the deferred push as a replay
                // rather than a revision conflict: what is under test is when the double sends, not the vehicle.
                server.ReplayJourneySnapshotsWithStableIdentity = true;
            });
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);

        await harness.Client.DisconnectAsync();
        await harness.Client.ConnectAndRecoverAsync(token);
        int reconnect = harness.Server.Received.Max(item => item.Connection);
        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, harness.Client.Current.Readiness);
        await Task.Delay(TimeSpan.FromMilliseconds(300), token);

        var held = harness.Server.SentEnvelopes.Where(item => item.Connection == reconnect).ToArray();
        var readiness = Assert.Single(held, item => item.MessageType == "SessionReadiness");
        Assert.Equal("RECOVERY_REQUIRED", ReadinessOf(readiness.WireLine));
        Assert.Equal(["SESSION_RECOVERY_REQUIRED"], ReasonCodesOf(readiness.WireLine));
        Assert.DoesNotContain(held, item => GatedMessageTypes.Contains(item.MessageType));

        harness.Io.CloseDoor(0, cargo: true);
        await Harness.WaitUntilAsync(
            () => harness.Server.SentEnvelopes.Any(
                item => item.Connection == reconnect && item.MessageType == "UpcomingStopPlanSnapshot"),
            "the deferred journey on the reconnected session",
            token,
            harness.DescribeEvents);
        await Task.Delay(TimeSpan.FromMilliseconds(300), token);

        var sent = harness.Server.SentEnvelopes.Where(item => item.Connection == reconnect).ToList();
        int resultAck = sent.FindIndex(
            item => item.MessageType == "DurableAck" && CorrelationOf(item.WireLine) == AttemptId);
        int ready = sent.FindIndex(
            item => item.MessageType == "SessionReadiness" && ReadinessOf(item.WireLine) == "READY");
        int firstGated = sent.FindIndex(item => GatedMessageTypes.Contains(item.MessageType));
        Assert.True(resultAck >= 0, "the result was never acknowledged");
        Assert.True(ready > resultAck, "READY did not follow the result's DurableAck");
        Assert.True(firstGated > ready, "a gated message went out before READY");
        Assert.Single(sent, item => item.MessageType == "UpcomingStopPlanSnapshot");
        Assert.DoesNotContain(sent, item => item.MessageType == "SlotOperationCommand");
    }

    /// <summary>
    /// 一条未完成的结果已经送达、被确认，操作仍未结算：重连的恢复报告里 <c>pendingResults</c> 非空。替身答
    /// <c>RECOVERY_REQUIRED</c>；车按同一 <c>messageId</c> 补报这条结果后，待补报项消了，但结果不是 <c>COMPLETED</c>，
    /// attempt 仍未结算（真服务端判 <c>RecoveryRequired</c>），所以不追加 <c>READY</c>，行程与命令继续挡在门后。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task APendingResultKeepsTheDoubleAtRecoveryRequiredAndItsReplayDoesNotSettleAnUnfinishedAttempt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { LockerWaitTimesOut = true },
            token,
            server => server.StationDepartureDeadlineAt = null);
        await Harness.WaitUntilAsync(
            () => harness.Journal.ReadOutgoingByDeduplicationKeyAsync($"operation-result:{AttemptId}", token)
                .GetAwaiter().GetResult() is { Acknowledged: true },
            "the unfinished result acknowledged on the first connection",
            token,
            harness.DescribeEvents);

        await harness.Client.DisconnectAsync();
        await harness.Client.ConnectAndRecoverAsync(token);
        int reconnect = harness.Server.Received.Max(item => item.Connection);
        await Harness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(
                item => item.Connection == reconnect && item.MessageType == "OperationResult"),
            "the pending result replayed on the reconnected session",
            token,
            harness.DescribeEvents);
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);

        var report = Assert.Single(
            harness.Server.ReceivedEnvelopes,
            item => item.Connection == reconnect && item.MessageType == "RecoveryStateReport");
        using (JsonDocument document = JsonDocument.Parse(report.WireLine))
        {
            Assert.Equal(
                AttemptId,
                Assert.Single(document.RootElement.GetProperty("payload").GetProperty("pendingResults").EnumerateArray())
                    .GetProperty("messageId").GetString());
        }

        var sent = harness.Server.SentEnvelopes.Where(item => item.Connection == reconnect).ToArray();
        var readiness = Assert.Single(sent, item => item.MessageType == "SessionReadiness");
        Assert.Equal("RECOVERY_REQUIRED", ReadinessOf(readiness.WireLine));
        Assert.Equal(["SESSION_RECOVERY_REQUIRED"], ReasonCodesOf(readiness.WireLine));
        Assert.Contains(sent, item => item.MessageType == "DurableAck" && CorrelationOf(item.WireLine) == AttemptId);
        Assert.DoesNotContain(sent, item => GatedMessageTypes.Contains(item.MessageType));
        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, harness.Client.Current.Readiness);
    }

    /// <summary>
    /// 服务端收下并结算了结果、<c>DurableAck</c> 在路上丢了（连接在确认前断开）：车不知道，重连时报告仍带着这个 attempt。
    /// 真服务端在报告到达时就把自己早已结算的 attempt 消掉（<c>TryTakeOffSettledReportedAttemptsAsync</c>），握手直接答
    /// <c>READY</c>，行程随即推送；车随后按同一 <c>messageId</c> 补发的结果只是重放，不改变就绪、不再追加 <c>SessionReadiness</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-CONNECTION-LOSS-SAFE-FINISH")]
    public async Task AnAttemptTheDoubleSettledBeforeItsAckWasLostIsAnsweredReadyOnTheNextHandshake()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.ReplayJourneySnapshotsWithStableIdentity = true;
            });
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);

        harness.Server.DropBeforeOperationResultAck = true;
        harness.Io.CloseDoor(0, cargo: true);
        await Harness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(item => item.MessageType == "OperationResult")
                && harness.Client.Current.Readiness == WireToGateSessionReadiness.Disconnected,
            "the result taken and the connection dropped before its ack",
            token,
            harness.DescribeEvents);
        Assert.Equal(AttemptId, harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId);

        harness.Server.DropBeforeOperationResultAck = false;
        await harness.Client.ConnectAndRecoverAsync(token);
        int reconnect = harness.Server.Received.Max(item => item.Connection);
        Assert.Equal(WireToGateSessionReadiness.Ready, harness.Client.Current.Readiness);
        await Harness.WaitUntilAsync(
            () => harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId is null,
            "the replayed result acknowledged and the load settled",
            token,
            harness.DescribeEvents);
        await Task.Delay(TimeSpan.FromMilliseconds(300), token);

        var sent = harness.Server.SentEnvelopes.Where(item => item.Connection == reconnect).ToList();
        var readiness = Assert.Single(sent, item => item.MessageType == "SessionReadiness");
        Assert.Equal("READY", ReadinessOf(readiness.WireLine));
        Assert.True(
            sent.FindIndex(item => item.MessageType == "UpcomingStopPlanSnapshot") > sent.IndexOf(readiness),
            "the journey did not follow the handshake's READY");
        Assert.DoesNotContain(sent, item => item.MessageType == "SlotOperationCommand");
    }

    /// <summary>
    /// 报告干净时（新 journal，没有未结算 attempt、没有待补报结果）行为与改动前一致：握手答 <c>READY</c>，行程快照与仓位命令
    /// 紧跟其后发出，各一次。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-CONNECTION-LOSS-SAFE-FINISH")]
    public async Task ACleanReportIsAnsweredReadyAndTheJourneyFollowsAtOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server => server.StationDepartureDeadlineAt = null);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);

        var sent = harness.Server.SentEnvelopes.Where(item => item.Connection == 1).ToList();
        int readiness = sent.FindIndex(item => item.MessageType == "SessionReadiness");
        Assert.Equal("READY", ReadinessOf(sent[readiness].WireLine));
        Assert.Empty(ReasonCodesOf(sent[readiness].WireLine));
        Assert.Equal(
            ["VehicleBusinessStateSnapshot", "CurrentStopWorklistSnapshot", "UpcomingStopPlanSnapshot", "SlotOperationCommand"],
            sent.Skip(readiness + 1).Where(item => GatedMessageTypes.Contains(item.MessageType))
                .Select(item => item.MessageType).ToArray());
        Assert.Single(sent, item => item.MessageType == "SessionReadiness");
    }

    private static string? ReadinessOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("payload").GetProperty("readiness").GetString();
    }

    private static string[] ReasonCodesOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return [.. document.RootElement.GetProperty("payload").GetProperty("reasonCodes")
            .EnumerateArray()
            .Select(item => item.GetString()!)];
    }

    private static string? CorrelationOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("correlationId").GetString();
    }
}
