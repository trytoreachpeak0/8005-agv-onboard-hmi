using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 扫码前取消的就绪收口（8005-agv-onboard-hmi#409，PR #425 审查 L2）：授权已到、向量已记盘，上报之前进程没了。
/// 重启后会话一就绪，车辆自己把它按空范围报 <c>ALL_EMPTY</c>，不等人再按、不重新申请、不开门。
/// </summary>
public sealed partial class LoadCancellationBeforeSublotG2Tests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task AnAuthorizedCancellationBeforeAnySublotLeftUnreportedIsReportedOnceTheSessionIsUp()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string cancellationId = "77777777-7777-4777-8777-777777777777";
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            seed: WireToGateRecoveryState.Empty with
            {
                RecoveryVector = new WireToGateRecoveryVectorContext(
                    WireToGateRecoveryVectorTypes.LoadCancellation,
                    cancellationId,
                    null,
                    DemandId,
                    null,
                    null,
                    [],
                    null,
                    "operator-001",
                    "SESSION",
                    DateTimeOffset.UtcNow)
                {
                    LoadCancellationSubjectKey = "5a5a5a5a-5a5a-4a5a-8a5a-5a5a5a5a5a5a"
                }
            },
            awaitEntryRequest: false);

        await BeforeSublotHarness.WaitUntilAsync(
            () => harness.ResultsReceived().Count > 0,
            "the authorized cancellation before any sublot to be reported without a press",
            token);
        await BeforeSublotHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector is null,
            "the acknowledged result to clear the vector",
            token);
        await Task.Delay(300, token);

        (string _, JsonElement result) = Assert.Single(harness.ResultsReceived());
        Assert.Equal(cancellationId, result.GetProperty("cancellationId").GetString());
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Empty(result.GetProperty("slotResults").EnumerateArray());
        Assert.Empty(harness.PayloadsReceived("LoadCancellationStartRequested"));
        Assert.Equal(0, harness.Io.UnlockCount);
    }
}
