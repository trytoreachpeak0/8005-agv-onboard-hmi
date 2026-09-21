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

        (bool pressed, NotSupportedException? ioAttempted) = await PressRecordingIoAsync(
            harness, "设备故障，本站不装了。", token);

        // 这一行就是锁存期间放开它的理由，所以先断它：一旦开了门，红在这里，而不是红在后面别的地方。
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Null(ioAttempted);
        Assert.True(pressed);
        JsonElement authorization = Assert.Single(harness.PayloadsSent("LoadCancellationAuthorization"));
        Assert.Equal("AUTHORIZED", authorization.GetProperty("decision").GetString());
        (_, JsonElement result) = Assert.Single(harness.ResultsReceived());
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(0, result.GetProperty("slotResults").GetArrayLength());
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

    /// <summary>
    /// 请求路径的第二道防线：日志里已经有一个授权过、还没执行完的在途取消向量，锁存期间再按一次也不执行它
    /// （PR #189 审查 M1）。
    /// </summary>
    /// <remarks>
    /// 这一支按下去不再问服务端，直接走 <c>ExecuteRecoveryVectorAndReportAsync</c> → <c>ExecuteClearAsync</c>，逐仓开门清空。
    /// 所以它和「新发起的在途取消」各要一道锁存判断，也各要一条用例：审查删掉这一支上的那次调用时，别的用例全绿。
    /// 目标仓里有货（夹具放的），清空会去开锁，所以 <c>UnlockCount == 0</c> 才说明真的没开。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ALatchRefusesToExecuteAnInFlightCancellationVectorAlreadyOnFile()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        bool latched = false;
        WireToGateRecoveryOperationContext load = new(
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
            new string('0', 64));
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            // 授权过、已写成向量、还没执行：装货已被中止，向量在 Prepared，等下一次按下去执行清空。
            seed: new WireToGateRecoveryState(
                FlippedAttemptId,
                WireToGateRecoveryCheckpoint.Prepared,
                [],
                0,
                [])
            {
                OperationContext = load,
                RecoveryVector = new WireToGateRecoveryVectorContext(
                    WireToGateRecoveryVectorTypes.LoadCancellation,
                    "66666666-6666-4666-8666-666666666666",
                    null,
                    DemandId,
                    FlippedAttemptId,
                    null,
                    [1, 2],
                    null,
                    "operator-001",
                    "SESSION",
                    DateTimeOffset.UtcNow)
            },
            // 日志里有未结算的装货，会话是 RECOVERY_REQUIRED，替身不发录入请求，所以不等它。
            awaitEntryRequest: false,
            fatalFaultLatched: () => Volatile.Read(ref latched));
        await BeforeSublotHarness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the cancellation vector on file to be offered",
            token);
        Assert.Equal(0, harness.Io.UnlockCount);

        Volatile.Write(ref latched, true);
        (bool pressed, NotSupportedException? ioAttempted) = await PressRecordingIoAsync(
            harness, "设备故障，本站不装了。", token);

        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Null(ioAttempted);
        Assert.False(pressed);
        await harness.WaitForRecoveryBlockedAsync("FATAL_FAULT_LATCHED", token);
        Assert.Empty(harness.ResultsReceived());
        Assert.NotNull((await harness.ReadRecoveryStateAsync(token)).RecoveryVector);
    }

    /// <summary>
    /// 按下取消，把「真的去开了门」变成一个可断言的值，而不是一次从按键里冒出来的异常。
    /// </summary>
    /// <remarks>
    /// 这个夹具的假 IO 不模拟操作员（<c>FakeIoModuleClient</c> 既不是 <c>OperatorNeverActs</c> 也不是
    /// <c>SimulateOperatorLoad</c>）：开锁脉冲照常计数，紧接着的 <c>WaitForLockerAsync</c> 抛
    /// <c>NotSupportedException</c>，而恢复请求的包装不接这个类型，它会直接从按键里抛出来。不接住它，注入「真去开门」时
    /// 用例会红在这个异常上，而不是红在 <c>UnlockCount == 0</c> 上——照样能发现开门，但红的理由与判据写的不是同一件事
    /// （PR #189 审查低项 3）。
    /// </remarks>
    private static async Task<(bool Pressed, NotSupportedException? IoAttempted)> PressRecordingIoAsync(
        BeforeSublotHarness harness,
        string reason,
        CancellationToken token)
    {
        try
        {
            return (await harness.Business.RequestLoadCancellationAsync(reason, token), null);
        }
        catch (NotSupportedException exception)
        {
            return (false, exception);
        }
    }
}
