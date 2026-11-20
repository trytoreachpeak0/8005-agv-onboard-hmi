using System.Text.Json;
using Xunit;
using Harness = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Harness;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 在途装货的取消每次按键一个 <c>cancellationId</c>（8005-agv-onboard-hmi#421）：被拒后再按是新请求、新 id。规则与替身见
/// <c>LoadCancellationBeforeSublotG2Tests.PerPressId.cs</c> 的类注释。
/// </summary>
public sealed partial class LoadCancellationEmptyScopeG2Tests
{
    /// <summary>
    /// 一篮未装、门关好时按取消，服务端以门没锁好拒（<c>LOCK_NOT_CLOSED</c>），装货照旧；入口仍按得动，再按是新的请求、新的 id，服务端重新判、授权，
    /// 车不开门、报 <c>ALL_EMPTY</c>，结果按第二个 id 上报。修前第二次仍用由需求与 attempt 推出的同一个 id、新的 <c>verifiedAt</c>，替身判内容冲突，
    /// 这条变红。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BASKET-LOADED")]
    public async Task APressAfterARefusalIsANewRequestUnderANewIdAndIsJudgedAfresh()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(Slot, OpensThenShut(cargo: false));
        await using Harness harness = await StartAsync(
            io,
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationDecision = "REJECTED";
                server.LoadCancellationRejectionReasonCode = "LOCK_NOT_CLOSED";
            });

        await WaitForReopenOfferAsync(harness, token);
        Assert.True(harness.OnUi(() => harness.ViewModel.CanPressLoadCancellation));
        _ = await harness.OnUi(() => harness.ViewModel.RequestLoadCancellationAsync(token));
        await WaitUntilAsync(
            harness,
            () => harness.Events.Any(item => item.Message.Contains("（LOCK_NOT_CLOSED）", StringComparison.Ordinal)),
            "the refusal shown",
            token);
        Assert.Null((await harness.Session.Journal.ReadRecoveryStateAsync(token)).PendingLoadCancellation);

        // The entry the refusal leaves is pressable, and pressing it -- on the screen, as the operator does -- is a new request.
        harness.Server.LoadCancellationDecision = "AUTHORIZED";
        Assert.True(harness.OnUi(() => harness.ViewModel.CanPressLoadCancellation));
        await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        Assert.True(
            await harness.OnUi(() => harness.ViewModel.RequestLoadCancellationAsync(token)),
            "The second press was not carried out: " + Said(harness));

        JsonElement result = await WaitForCancellationResultAsync(harness, token);
        JsonElement[] requests = [.. harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "LoadCancellationStartRequested")
            .Select(envelope =>
            {
                using JsonDocument document = JsonDocument.Parse(envelope.WireLine);
                return document.RootElement.GetProperty("payload").Clone();
            })];
        Assert.Equal(2, requests.Length);
        string secondId = requests[1].GetProperty("cancellationId").GetString()!;
        Assert.NotEqual(requests[0].GetProperty("cancellationId").GetString(), secondId);
        Assert.Equal(
            requests[0].GetProperty("slotOperationAttemptId").GetString(),
            requests[1].GetProperty("slotOperationAttemptId").GetString());
        Assert.Empty(harness.Server.RecoveryRequestConflicts);
        Assert.Equal(secondId, result.GetProperty("cancellationId").GetString());
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(1, io.UnlockCount);
        AssertNoOperationResult(harness);
    }
}
