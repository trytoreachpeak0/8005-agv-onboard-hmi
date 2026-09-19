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
    /// 门后的快照与命令这才发出，而且只发一次。
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
                item => item.Connection == reconnect && item.MessageType == "SlotOperationCommand"),
            "the deferred command on the reconnected session",
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
        Assert.Single(sent, item => item.MessageType == "SlotOperationCommand");
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
