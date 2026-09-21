using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 严重安全故障锁存期间，取消装货只剩「扫码之前」那一半，而且按下去一扇门都不开
/// （<c>trytoreachpeak0/8005-agv-onboard-hmi#174</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 锁存期间其余八个恢复入口全关，唯独这一个开着，理由只有一条：它不碰 IO。所以这里断的是那条理由本身——
/// <c>IIoModuleClient</c> 唯一会写 DO 的方法是 <c>PulseUnlockAsync</c>，夹具目标仓里放了货（清空时会去开锁），
/// 一次都没调才说明真的没开。
/// </para>
/// <para>
/// <b>请求路径那道防线也在这里断。</b>入口判定与按下之间日志可能变：一条仓位命令恰好到了，「扫码之前」就变成
/// 「在途装货」，而在途那一半经恢复向量执行器开门，锁存在执行层拦不住它。所以按下时再判一次锁存。
/// </para>
/// </remarks>
public sealed partial class LoadCancellationBeforeSublotG2Tests
{
    private const string FlippedAttemptId = "44444444-4444-4444-8444-444444444444";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ALatchedVehicleCanStillCancelBeforeAnySublotAndOpensNoDoor()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            fatalFaultLatched: () => true);
        Assert.True(harness.Business.CanRequestLoadCancellationBeforeAnySublot);

        Assert.True(await harness.Business.RequestLoadCancellationAsync(
            "设备故障，本站不装了。", token));

        JsonElement authorization = Assert.Single(harness.PayloadsSent("LoadCancellationAuthorization"));
        Assert.Equal("AUTHORIZED", authorization.GetProperty("decision").GetString());
        (_, JsonElement result) = Assert.Single(harness.ResultsReceived());
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(0, result.GetProperty("slotResults").GetArrayLength());
        // 这一行就是锁存期间放开它的理由。
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ALatchRefusesACancellationThatTurnedIntoALoadInFlightBeforeThePress()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            fatalFaultLatched: () => true);
        // 入口判定时是扫码之前。
        Assert.True(harness.Business.CanRequestLoadCancellationBeforeAnySublot);

        // 按下之前，一条装货命令到了：执行器把它写成未结算的在途装货（1、2 号仓），1 号仓在活动集里。
        await harness.UpdateRecoveryStateAsync(
            _ => new WireToGateRecoveryState(
                FlippedAttemptId,
                WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                [1],
                0,
                [])
            {
                OperationContext = new WireToGateRecoveryOperationContext(
                    Guid.NewGuid().ToString("D"),
                    null,
                    1,
                    DateTimeOffset.UtcNow,
                    DemandId,
                    OperationSessionId,
                    FlippedAttemptId,
                    OperationType.Load,
                    [1, 2],
                    2,
                    true,
                    new string('0', 64))
            },
            token);

        // 恢复请求的包装把拒绝落成 false 加一条 RECOVERY_BLOCKED，码是 FATAL_FAULT_LATCHED。
        Assert.False(await harness.Business.RequestLoadCancellationAsync("设备故障，本站不装了。", token));
        await harness.WaitForRecoveryBlockedAsync("FATAL_FAULT_LATCHED", token);
        Assert.Empty(harness.PayloadsReceived("LoadCancellationStartRequested"));
        Assert.Equal(0, harness.Io.UnlockCount);
    }
}
