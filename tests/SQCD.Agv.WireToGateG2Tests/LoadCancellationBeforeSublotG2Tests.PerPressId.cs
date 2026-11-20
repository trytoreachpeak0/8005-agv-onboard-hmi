using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 装货取消每次按键一个 <c>cancellationId</c>（8005-agv-onboard-hmi#421，协议 98f17264，照减篮 <c>skipRequestId</c> 的写法）：记盘后再发；
/// 同一次按键的重连／重启重提是新 messageId、同 id、同载荷；服务端同 id 同内容回放首次判定、不同内容回 <c>BUSINESS_ID_CONTENT_CONFLICT</c>；
/// 被拒后再按是新请求、新 id。替身服务端照这条规则答（<c>FakeControlServer._loadCancellationFirstRefusals</c>）。
/// </summary>
/// <remarks>
/// 修前 id 由需求与本站作业会话推出：被拒后再按仍是同一个 id、带新的 <c>verifiedAt</c>，到了按协议回放首次判定的服务端就是业务号内容冲突，这条需求
/// 在这一站再也取消不了。同一次按键的重提（应答丢失、重启）见 <see cref="ALostAuthorizationIsAskedForAgainWithTheFirstPressContent"/> 与
/// <see cref="ALostAuthorizationIsAskedForAgainWithTheFirstPressContentAcrossARestart"/>，同 id、同载荷，本票不变。
/// </remarks>
public sealed partial class LoadCancellationBeforeSublotG2Tests
{
    /// <summary>
    /// 第一次按被拒，屏上说原因、那一行仍按得动；照着再按一次是新的请求，带新的 <c>cancellationId</c>，服务端重新判、授权，结果按第二个 id 上报，
    /// 全程没有业务号冲突。修前第二次仍用推出的同一个 id，替身判内容冲突，这条变红。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ARowPressAfterARefusalIsANewRequestUnderANewIdAndIsJudgedAfresh()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server => server.LoadCancellationDecision = "REJECTED");

        Assert.False(await harness.Business.RequestWorklistRowCancellationAsync(DemandId,
            "到站后现场确认本站没有要装的货。", token));
        await harness.WaitForRecoveryBlockedAsync("ACTION_NOT_ALLOWED_IN_STATE", token);
        Assert.Null((await harness.ReadRecoveryStateAsync(token)).PendingLoadCancellation);
        // The way out the refusal leaves: the same row, pressable.
        Assert.True(harness.Business.CanRequestWorklistRowCancellation(DemandId));

        harness.Server.LoadCancellationDecision = "AUTHORIZED";
        await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        Assert.True(await harness.Business.RequestWorklistRowCancellationAsync(DemandId,
            "到站后现场确认本站没有要装的货。", token));

        IReadOnlyList<JsonElement> requests = harness.PayloadsReceived("LoadCancellationStartRequested");
        Assert.Equal(2, requests.Count);
        string refusedId = requests[0].GetProperty("cancellationId").GetString()!;
        string secondId = requests[1].GetProperty("cancellationId").GetString()!;
        Assert.NotEqual(refusedId, secondId);
        Assert.Equal(DemandId, requests[1].GetProperty("demandId").GetString());
        Assert.Empty(harness.Server.RecoveryRequestConflicts);
        (string _, JsonElement result) = Assert.Single(harness.ResultsReceived());
        Assert.Equal(secondId, result.GetProperty("cancellationId").GetString());
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// 记盘后再发：日志簿里待答的那一条带着这次按键的 id 与它所属对象的键（需求＋作业会话）。id 就是发出去的那个，不再等于由对象推出的键；
    /// 再按（答复丢了）靠对象键找回这一条，按它的 id 与内容逐字重提，不冲突。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task TheUnansweredPressIsJournaledWithItsOwnIdAndItsSubjectKeyBeforeItIsSent()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server => server.LoadCancellationAuthorizationsToDrop = 1);

        Assert.False(await harness.Business.RequestWorklistRowCancellationAsync(DemandId,
            "到站后现场确认本站没有要装的货。", token));

        WireToGatePendingLoadCancellation pending = (await harness.ReadRecoveryStateAsync(token)).PendingLoadCancellation!;
        JsonElement sent = Assert.Single(harness.PayloadsReceived("LoadCancellationStartRequested"));
        Assert.Equal(sent.GetProperty("cancellationId").GetString(), pending.CancellationId);
        Assert.NotNull(pending.SubjectKey);
        Assert.NotEqual(pending.SubjectKey, pending.CancellationId);

        await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        Assert.True(await harness.Business.RequestWorklistRowCancellationAsync(DemandId, "又按了一次。", token));
        IReadOnlyList<JsonElement> requests = harness.PayloadsReceived("LoadCancellationStartRequested");
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].GetRawText(), requests[1].GetRawText());
        Assert.Empty(harness.Server.RecoveryRequestConflicts);
    }
}
