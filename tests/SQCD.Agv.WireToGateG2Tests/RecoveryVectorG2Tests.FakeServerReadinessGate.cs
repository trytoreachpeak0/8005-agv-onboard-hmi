using System.Text.Json;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 替身自证的恢复半边（<c>trytoreachpeak0/8005-agv-onboard-hmi#128</c>）：会话因未结算 attempt 停在
/// <c>RECOVERY_REQUIRED</c> 时，恢复类报文照常往来——真服务端的 <c>OnboardRecoveryCoordinator</c> 不经
/// <c>JourneyRuntimeEngine.AdvanceAsync</c> 的就绪门；补偿清空的结果把 attempt 结掉之后，替身在那条
/// <c>DurableAck</c> 后面追加 <c>READY</c>，挡在门后的行程这才补推。
/// </summary>
public sealed partial class RecoveryVectorG2Tests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task RecoveryFlowsWhileTheDoubleIsNotReadyAndACompensationThatSettlesTheAttemptMakesItReady()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.SendJourneySnapshotsAfterRecovery = true;
            });

        var handshake = harness.Server.SentEnvelopes.First(item => item.MessageType == "SessionReadiness");
        Assert.Equal("RECOVERY_REQUIRED", PayloadString(handshake.WireLine, "readiness"));

        Assert.True(harness.Business.CanRequestLoadCompensation);
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Server.SentEnvelopes.Any(item => item.MessageType == "UpcomingStopPlanSnapshot"),
            "the journey the double held back until READY",
            token);

        var sent = harness.Server.SentEnvelopes.ToList();
        int ready = sent.FindIndex(
            item => item.MessageType == "SessionReadiness" && PayloadString(item.WireLine, "readiness") == "READY");
        int resultAck = sent.FindIndex(
            item => item.MessageType == "DurableAck"
                && PayloadString(item.WireLine, "acceptedMessageType") == "LoadCompensationResult");
        Assert.True(resultAck >= 0 && ready == resultAck + 1, "READY did not follow the compensation result's ack");
        foreach (string recovery in new[]
                 {
                     "ExceptionRecoverySessionOpened", "RecoveryActionAccepted", "LoadCompensationCommand"
                 })
        {
            int index = sent.FindIndex(item => item.MessageType == recovery);
            Assert.True(index >= 0 && index < ready, $"{recovery} was not sent while the session was not ready");
        }

        Assert.True(
            sent.FindIndex(item => item.MessageType is "VehicleBusinessStateSnapshot" or "CurrentStopWorklistSnapshot"
                or "UpcomingStopPlanSnapshot") > ready,
            "a journey snapshot went out before READY");
    }

    private static string? PayloadString(string wireLine, string property)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("payload").GetProperty(property).GetString();
    }
}
