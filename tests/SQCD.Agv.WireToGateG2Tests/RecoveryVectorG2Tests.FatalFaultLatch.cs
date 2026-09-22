using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 操作员在锁存之前申请的恢复，命令到车时已经锁存：一扇门都不开，结果照样报给服务端（8005-agv-onboard-hmi#191）。
/// </summary>
/// <remarks>
/// <para>
/// 锁存期间恢复入口全关（#171/#174），所以能走到这里的只有「申请在锁存前、命令在锁存后」这一个窗口。窗口是确定构造的：
/// 替身在受理恢复动作之后、下发它授权的命令之前调 <see cref="FakeControlServer.BeforeRecoveryVectorCommand"/>，锁存在那里发生。
/// </para>
/// <para>
/// 目标仓里有货、锁着（夹具的 <c>cargoInTargetSlots</c>），清空会去开锁，所以 <c>UnlockCount == 0</c> 才说明真的没开。每条都先等
/// 「结果到了，或者开了一次门」二者之一，再先断开门次数。结果必须到：服务端的恢复工作流在 <c>AwaitingResult</c> 里没有超时，
/// 车不答，它就一直等（onboard-hmi#123 那一次就是这样卡住的）。
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ALatchBetweenTheCompensationRequestAndItsCommandOpensNoDoorAndStillAnswers()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        bool latched = false;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.BeforeRecoveryVectorCommand = () => Volatile.Write(ref latched, true),
            cargoInTargetSlots: true,
            fatalFaultLatched: () => Volatile.Read(ref latched));
        Assert.True(harness.Business.CanRequestLoadCompensation);

        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await WaitForResultOrPulseAsync(harness, "LoadCompensationResult", token);

        Assert.Equal(0, harness.Io.UnlockCount);
        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        AssertRefusedByLatchAsRead(result.GetProperty("slotResults"));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task ALatchBetweenTheFaultCargoHandoffRequestAndItsCommandOpensNoDoorAndStillAnswers()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        bool latched = false;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.BeforeRecoveryVectorCommand = () => Volatile.Write(ref latched, true),
            cargoInTargetSlots: true,
            fatalFaultLatched: () => Volatile.Read(ref latched));

        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync(
            "现场确认故障仓货物需要交接处理。", token));
        await WaitForResultOrPulseAsync(harness, "FaultCargoRecoveryResult", token);

        Assert.Equal(0, harness.Io.UnlockCount);
        JsonElement result = await harness.WaitForResultAsync("FaultCargoRecoveryResult", token);
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        AssertRefusedByLatchAsRead(result.GetProperty("slotResults"));
    }

    /// <summary>
    /// 续跑是一次新的开门决定，锁存期间在执行器入口整条拒掉，什么都不写：车回 <c>SlotOperationCommandRejected</c>（原因码
    /// <c>VEHICLE_NOT_READY</c>），服务端据此关闭这次续跑、进 RecoveryRequired（control-server#187），复位之后操作员再申请。
    /// </summary>
    /// <remarks>
    /// 用 <c>lockerWaitTimesOut</c> 的夹具，与 <c>AResumeThatFailsAfterItsFirstPulseIsSettledNotRejected</c> 同一个起点：那一条里
    /// 同样的续跑命令会开一次门，所以这里的 0 是锁存挡下的，而不是这条命令本来就开不了门。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ALatchedVehicleRejectsAResumeBeforeAnyDoorIo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        bool latched = false;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            lockerWaitTimesOut: true,
            fatalFaultLatched: () => Volatile.Read(ref latched));
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
        int resultsBefore = harness.ResultsOfType("OperationResult").Count;

        Volatile.Write(ref latched, true);
        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(state));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Io.UnlockCount > 0
                || harness.ResultsOfType("OperationResult").Count > resultsBefore
                || Rejections(harness).Count > 0,
            "the resume to be rejected, reported or to pulse",
            token);

        Assert.Equal(0, harness.Io.UnlockCount);
        JsonElement rejection = await WaitForSingleRejectionAsync(harness, token);
        Assert.Equal(ResumeMessageId, rejection.GetProperty("correlationId").GetString());
        Assert.Equal(
            "VEHICLE_NOT_READY",
            rejection.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(resultsBefore, harness.ResultsOfType("OperationResult").Count);
        await harness.WaitForRecoveryBlockedAsync("FATAL_FAULT_LATCHED", token);
    }

    private static Task WaitForResultOrPulseAsync(
        RecoveryVectorHarness harness,
        string resultType,
        CancellationToken token) =>
        RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Io.UnlockCount > 0 || harness.ResultsOfType(resultType).Count > 0,
            $"the {resultType}, or a pulse",
            token);

    /// <summary>
    /// 1、2 号仓都有货、锁着、输出复位，都没被这个向量开过：<c>NOT_STARTED</c>，读数照实。1 号仓是锁存拒掉的那一次开锁，带
    /// <c>VEHICLE_NOT_READY</c>；2 号仓没轮到，不带原因（ADR-cross-0058 第 6 条）。
    /// </summary>
    private static void AssertRefusedByLatchAsRead(JsonElement slotResults)
    {
        JsonElement[] slots = [.. slotResults.EnumerateArray()];
        Assert.Equal([1, 2], slots.Select(slot => slot.GetProperty("slotNo").GetInt32()));
        Assert.All(slots, slot =>
        {
            Assert.Equal("NOT_STARTED", slot.GetProperty("outcome").GetString());
            Assert.Equal("OCCUPIED", slot.GetProperty("finalPhysicalState").GetString());
            Assert.Equal("LOCKED", slot.GetProperty("lockState").GetString());
            Assert.Equal("RESET", slot.GetProperty("unlockOutputState").GetString());
        });
        Assert.Equal(
            ["VEHICLE_NOT_READY"],
            slots[0].GetProperty("reasonCodes").EnumerateArray().Select(code => code.GetString()!));
        Assert.Empty(slots[1].GetProperty("reasonCodes").EnumerateArray());
    }
}
