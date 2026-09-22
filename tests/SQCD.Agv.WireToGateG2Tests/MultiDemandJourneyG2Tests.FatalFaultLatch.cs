using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 严重安全故障锁存期间，服务端下发的仓位操作一扇新门都不开（8005-agv-onboard-hmi#191，一并解决 #84）。
/// </summary>
/// <remarks>
/// <para>
/// <b>判据（#191 票面，调度 2026-09-22 确认）：</b>锁存期间任何一次新的开锁脉冲都拒绝；已经开着的门照常等操作员关上。
/// 被拒的那一次照实上报：<c>FAILED</c>，从没开过的仓 <c>NOT_STARTED</c>，开过又空关、这次不再重开的仓 <c>FAILED</c>，原因码
/// <c>VEHICLE_NOT_READY</c>（协议 v2.0.0 没有更准的码，专用码记在 program#115）。服务端对非完成结果一律判
/// <c>RecoveryRequired</c> 并 Block（<c>WireToGateStore</c> 结果落库处、<c>DeterminateLoadFailure.Judge</c> 只放过
/// <c>OPERATOR_TIMEOUT</c>），所以它得到一个答复，不会停在等结果上。
/// </para>
/// <para>
/// <b>断在 IO 层。</b><c>IIoModuleClient</c> 唯一会写 DO 的方法是 <c>PulseUnlockAsync</c>，<c>UnlockCount</c> 就是它被调的次数。
/// 每条拒绝用例都先等「结果到了，或者多开了一次门」二者之一，再先断 <c>UnlockCount</c>：修复不在时红在那一行，而不是红在
/// 一个等结果的超时上。
/// </para>
/// <para>
/// <b>锁存用的是真控制器。</b>夹具把 <c>OnboardController.IsFatalFaultLatched</c> 接进业务服务，与 App 同一根线，所以这里
/// <c>EnterFatalFault</c> 之后，业务服务与两个执行器读到的就是产品里那个锁存。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 锁存之后才到的装货命令（票面第 2 条的「锁存前已提交的子批」）：一扇门都不开，结果照实报出去，并且日志留在
    /// <c>SAFE_FINISH_REACHED</c>、这次 attempt 仍是未结算的那一个——复位之后 <c>RESUME_AFTER_REPAIR</c> 要靠它续上
    /// （服务端要求报告的已证实检查点是 PREPARED、ACTIVE_UNLOCK_SET 或 SAFE_FINISH_REACHED 之一）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ALatchedVehicleOpensNoDoorForASlotCommandAndReportsItFailedNotStarted()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(io, token);

        LatchNow(harness);
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        JsonElement? result = await WaitForResultOrPulseAsync(harness, io, AttemptA, pulsesBefore: 0, token);

        Assert.Equal(0, io.UnlockCount);
        JsonElement report = Assert.NotNull(result);
        Assert.Equal("FAILED", report.GetProperty("overallOutcome").GetString());
        Assert.Equal("SAFE_FINISH_REACHED", report.GetProperty("journalCheckpoint").GetString());
        JsonElement[] slots = [.. report.GetProperty("slotResults").EnumerateArray()];
        Assert.Equal([1, 2], slots.Select(slot => slot.GetProperty("slotNo").GetInt32()));
        Assert.All(slots, slot => Assert.Equal("NOT_STARTED", slot.GetProperty("outcome").GetString()));
        // 停下它的是 1 号仓那一次开锁，原因记在那一仓；2 号仓只是没轮到（ADR-cross-0058 第 6 条）。
        Assert.Equal(["VEHICLE_NOT_READY"], ReasonCodes(slots[0]));
        Assert.Empty(ReasonCodes(slots[1]));

        WireToGateRecoveryState journal = ReadJournal(harness, token);
        Assert.Equal(AttemptA, journal.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, journal.ProvenRecoveryCheckpoint);
        Assert.Empty(journal.ActiveUnlockSlots);
        // 操作员看到的是「为什么没开门」，不是一句光秃秃的「操作未完成」。这一句在结果发出之前发布。
        AssertLatchGuidance(harness, "本机已锁存严重安全故障，1、2号仓停止开门；复核并复位后可申请恢复。");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 两仓装货，1 号仓在锁存前已经开着：操作员装好关门，1 号仓照常完成；轮到 2 号仓第一次开锁时被拒。提示只点名 2 号仓——
    /// 说「1、2号仓停止开门」会把一个确实装好了的仓说成没装（PR #198 审查低项 3）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ALatchMidwayThroughATwoSlotLoadKeepsTheFinishedSlotAndNamesOnlyTheOtherOne()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(io, token);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        LatchNow(harness);
        io.CloseDoor(0, cargo: true);
        JsonElement? result = await WaitForResultOrPulseAsync(harness, io, AttemptA, pulsesBefore: 1, token);

        Assert.Equal(1, io.UnlockCount);
        JsonElement report = Assert.NotNull(result);
        Assert.Equal("FAILED", report.GetProperty("overallOutcome").GetString());
        JsonElement[] slots = [.. report.GetProperty("slotResults").EnumerateArray()];
        Assert.Equal(
            [(1, "COMPLETED"), (2, "NOT_STARTED")],
            slots.Select(slot => (slot.GetProperty("slotNo").GetInt32(), slot.GetProperty("outcome").GetString())));
        Assert.Equal(["VEHICLE_NOT_READY"], ReasonCodes(slots[1]));
        AssertLatchGuidance(harness, "本机已锁存严重安全故障，2号仓停止开门；复核并复位后可申请恢复。");
    }

    /// <summary>
    /// 被允许的一格：锁存时门已经开着、操作员正在放货。锁存不去碰那扇门，操作员放好关门，装货照常完成——这一步不发脉冲，
    /// 「锁存期间不开门」管的是新开的门。钉住它，是为了不让修复把「停止开门」做成「停止等待」：那样门开着、没人等，
    /// 装货既不完成也不上报。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ALatchWhileTheDoorIsAlreadyOpenLetsTheOperatorFinishTheLoadWithoutAnotherPulse()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(io, token);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        LatchNow(harness);
        io.CloseDoor(0, cargo: true);

        JsonElement result = await WaitForOperationResultAsync(harness, AttemptA, token);
        Assert.Equal("COMPLETED", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(1, io.UnlockCount);
    }

    /// <summary>
    /// 锁存时门开着，操作员没放货就把门关了。平时这一下会重新弹开（ADR-cross-0058 第 1 条），锁存期间不再弹：这一仓报
    /// <c>FAILED</c>——门锁着、开锁输出复位、有没有货都读得出来，不是 <c>UNKNOWN</c>——日志停在 <c>SAFE_FINISH_REACHED</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ALatchRefusesTheReopenAfterADoorShutEmptyAndReportsTheSlotFailed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(io, token);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        LatchNow(harness);
        io.CloseDoor(0, cargo: false);
        JsonElement? result = await WaitForResultOrPulseAsync(harness, io, AttemptA, pulsesBefore: 1, token);

        Assert.Equal(1, io.UnlockCount);
        JsonElement report = Assert.NotNull(result);
        Assert.Equal("FAILED", report.GetProperty("overallOutcome").GetString());
        Assert.Equal("SAFE_FINISH_REACHED", report.GetProperty("journalCheckpoint").GetString());
        JsonElement slot = Assert.Single(report.GetProperty("slotResults").EnumerateArray());
        Assert.Equal("FAILED", slot.GetProperty("outcome").GetString());
        Assert.Equal("EMPTY", slot.GetProperty("finalPhysicalState").GetString());
        Assert.Equal("LOCKED", slot.GetProperty("lockState").GetString());
        Assert.Equal("RESET", slot.GetProperty("unlockOutputState").GetString());
        Assert.Equal(["VEHICLE_NOT_READY"], ReasonCodes(slot));
        Assert.Empty(ReadJournal(harness, token).ActiveUnlockSlots);
    }

    /// <summary>
    /// 票面第 1 条：操作员按下在途取消时还没锁存，锁存发生在「车已经问了、服务端授权还没回来」之间。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>窗口是确定构造的。</b>替身在收到 <c>LoadCancellationStartRequested</c>、写出授权之前调
    /// <see cref="FakeControlServer.BeforeLoadCancellationAuthorization"/>，锁存就在那里发生——按下时的那道请求侧检查
    /// （<c>RefuseDoorOpeningCancellationWhileLatched</c>）已经过去，授权还在路上。
    /// </para>
    /// <para>
    /// <b>装货是真在途的：</b>1 号仓已经装好关门，2 号仓开着等操作员。授权回来后装货照常中止（不中止，服务端的取消工作流
    /// 会停在 <c>AwaitingResult</c>，它没有超时）。开着的 2 号仓是交接过来的门，不用再开，等操作员清空关上；1 号仓有货、
    /// 锁着，清空它要新开一次门——锁存期间这一次被拒。所以结果不是 <c>ALL_EMPTY</c>，服务端据此进 <c>RecoveryRequired</c>。
    /// 修复之前那一次照开，<c>UnlockCount</c> 变成 3。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ALatchArrivingWhileTheInFlightCancellationIsAskedOpensNoFurtherDoor()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        Harness? latchTarget = null;
        await using Harness harness = await StartLatchStopAsync(
            io,
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1, 2];
                server.BeforeLoadCancellationAuthorization = () => LatchNow(latchTarget!);
            });
        latchTarget = harness;

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 2, token);
        Assert.False(harness.Controller.IsFatalFaultLatched);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry",
            token);

        Task<bool> press = harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is not null,
            "the authorized cancellation to take the slots over",
            token);
        Assert.True(harness.Controller.IsFatalFaultLatched);
        // 交接过来的 2 号仓：操作员把刚放进去的货拿出来，空着关上。
        io.CloseDoor(1, cargo: false);
        await harness.WaitUntilAsync(
            () => io.UnlockCount > 2
                || ReceivedPayloads(harness, "LoadCancellationResult").Length > 0,
            "the cancellation result, or a third pulse",
            token);

        Assert.Equal(2, io.UnlockCount);
        JsonElement result = Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        // FAILED，不只是「不是 ALL_EMPTY」：锁存拒绝的物理状态是读得出来的，错报成 UNKNOWN 也不该过（PR #198 审查）。
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        JsonElement[] slots = [.. result.GetProperty("slotResults").EnumerateArray()];
        JsonElement refused = Assert.Single(slots, slot => slot.GetProperty("slotNo").GetInt32() == 1);
        Assert.Equal("NOT_STARTED", refused.GetProperty("outcome").GetString());
        Assert.Equal("OCCUPIED", refused.GetProperty("finalPhysicalState").GetString());
        Assert.Equal(["VEHICLE_NOT_READY"], ReasonCodes(refused));
        JsonElement handedOver = Assert.Single(slots, slot => slot.GetProperty("slotNo").GetInt32() == 2);
        Assert.Equal("COMPLETED", handedOver.GetProperty("outcome").GetString());
        await press;
        // 恢复向量这一路的提示也说明是锁存，并且只点名没清空的 1 号仓（PR #198 审查低项 2、3）。
        AssertLatchGuidance(harness, "本机已锁存严重安全故障，1号仓停止开门；复核并复位后可再次申请恢复。");
    }

    private static async Task<Harness> StartLatchStopAsync(
        FakeIoModuleClient io,
        CancellationToken token,
        Action<FakeControlServer>? alsoConfigure = null)
    {
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
                alsoConfigure?.Invoke(server);
            },
            token,
            io: io);
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null
                && harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the worklist on a ready session",
            token);
        return harness;
    }

    /// <summary>与 App 同一个入口：界面命令失败锁存的就是这个码。</summary>
    private static void LatchNow(Harness harness) =>
        harness.Controller.EnterFatalFault("UI_COMMAND_FAILED", OnboardFatalFaultBanner.UiCommandFailed);

    private static Task WaitForDoorOpenAsync(
        Harness harness,
        FakeIoModuleClient io,
        string attemptId,
        int pulses,
        CancellationToken token) =>
        harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator
                && harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == attemptId
                && io.UnlockCount == pulses,
            $"pulse {pulses} of {attemptId} to be waiting on the operator",
            token);

    /// <summary>
    /// 等到这次 attempt 的结果到了，或者多开了一次门。两者都算「出结论了」，由调用方先断开门次数。
    /// </summary>
    private static async Task<JsonElement?> WaitForResultOrPulseAsync(
        Harness harness,
        FakeIoModuleClient io,
        string attemptId,
        int pulsesBefore,
        CancellationToken token)
    {
        JsonElement? found = null;
        await harness.WaitUntilAsync(
            () =>
            {
                found = ReceivedPayloads(harness, "OperationResult").FirstOrDefault(
                    payload => payload.GetProperty("slotOperationAttemptId").GetString() == attemptId);
                return found is { ValueKind: JsonValueKind.Object } || io.UnlockCount > pulsesBefore;
            },
            $"the OperationResult of {attemptId}, or a pulse",
            token);
        return found is { ValueKind: JsonValueKind.Object } ? found : null;
    }

    /// <summary>操作面板上那一句，逐字断：它点名哪些仓就是这条判据的一部分。</summary>
    private static void AssertLatchGuidance(Harness harness, string expected) =>
        Assert.Contains(
            harness.Events,
            item => item.Kind == "OPERATION_PROGRESS" && item.Message == expected);

    private static string[] ReasonCodes(JsonElement slot) =>
        [.. slot.GetProperty("reasonCodes").EnumerateArray().Select(code => code.GetString()!)];
}
