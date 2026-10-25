using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ScreenState;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 续行（修复后续行原仓位操作，<c>RESUME_AFTER_REPAIR</c>）装货时仓里没有货：屏上要有按得动的「不装了」，按下去续行收尾、
/// 不再开门，再按「补偿清空」把这次装货结掉（trytoreachpeak0/8005-agv-onboard-hmi#410 验收 4，agv01 2026-10-22）。
/// </summary>
/// <remarks>
/// <para>
/// 现场形状：结果交服务端之后，维护人员开了恢复窗口，操作员按了「申请恢复」（那是续行），5 号仓空着关门又被打开，一次又一次；
/// 续行里取消入口不出现（这次装货的结果已交服务端，服务端也拒绝），屏上没有别的出路，最后靠停程序、关会话脱困。如今空关不再
/// 自动重开（onboard-hmi#385），但续行停在「等按重新开门」，照样没有退出。
/// </para>
/// <para>
/// 「不装了」只在续行等「重新开门」、目标仓全都现读为空、锁闭、输出复位时给：续行以 FAILED 收尾——等着的那一仓 FAILED，
/// 没轮到的 NOT_STARTED，每一仓 EMPTY／LOCKED／RESET——服务端照规则判为仍需恢复并关会话（control-server
/// <c>OnboardRecoveryCoordinator.AdvanceSessionAfterResultAsync</c>），授权还在，于是「补偿清空」接着可按。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumedLoadOverAnEmptySlotIsGivenUpByPressAndThenCompensated()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server => server.SendLoadCompensationCommandOnRequest = true,
            token);
        WireToGateRecoveryState opened = await EndTheLoadUnknownAndOpenAResumeAsync(harness, io, token);

        // 续行开门，操作员把空仓门关上：续行停在「等按重新开门」。
        io.CloseDoorAfterNextUnlock(0, cargo: false);
        int unlocksBefore = io.UnlockCount;
        await harness.Server.SendCommandAsync("SlotOperationResumeCommand", Guid.NewGuid().ToString("D"), ResumeOf(opened));
        await harness.WaitUntilAsync(
            () =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                return io.UnlockCount == unlocksBefore + 1
                    && harness.Business.CanReopenWaitingSlot
                    && harness.ViewModel.CanAbandonResume;
            },
            "the resume to hold slot 1 shut empty and offer 「不装了」",
            token);
        HmiButtonState abandon = ButtonOf(harness.ScreenState(), "OnAbandonResumeClick");
        Assert.Equal((true, true), (abandon.Visible, abandon.Enabled));
        Assert.False(harness.ViewModel.CanRequestLoadCancellation);
        // 提示与可按的按钮一致：续行里没有「取消装货」，放弃的那一个按钮叫「不装了」（10-22 屏上写着「不装了请按取消」）。
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.Guidance
                == "1号仓关门时没有放入篮子。请按「重新开门」继续装；不装了请按「不装了」。",
            "the resume's prompt to name the button that gives up",
            token);

        Assert.True(await harness.ViewModel.AbandonResumeAsync(token));
        string resumeResultId = FakeControlServerIdentifiers.StableUuid(
            $"recovery-operation-result:{AttemptA}:{opened.RecoveryActionId}");
        await harness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                envelope.MessageType == "OperationResult" && envelope.MessageId == resumeResultId),
            "the resume's replacement result",
            token);
        JsonElement replacement = ReceivedPayloads(harness, "OperationResult")
            .Last(payload => payload.GetProperty("slotOperationAttemptId").GetString() == AttemptA);
        Assert.Equal("FAILED", replacement.GetProperty("overallOutcome").GetString());
        JsonElement slot = Assert.Single(replacement.GetProperty("slotResults").EnumerateArray());
        Assert.Equal(
            ("FAILED", "EMPTY", "LOCKED", "RESET"),
            (slot.GetProperty("outcome").GetString(),
                slot.GetProperty("finalPhysicalState").GetString(),
                slot.GetProperty("lockState").GetString(),
                slot.GetProperty("unlockOutputState").GetString()));
        Assert.Equal(unlocksBefore + 1, io.UnlockCount);
        Assert.False(harness.ViewModel.CanAbandonResume);
        Assert.False(await harness.ViewModel.AbandonResumeAsync(token));

        // 服务端判仍需恢复，关会话；授权还在：照屏上说的按「补偿清空」，一直走到会话就绪。
        await harness.Server.SendCommandAsync(
            "ExceptionRecoverySessionSnapshot",
            Guid.NewGuid().ToString("D"),
            ClosedAfterResume(opened));
        await harness.WaitUntilAsync(
            () =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                return harness.ViewModel.CanRequestLoadCompensation
                    && harness.OnUi(() => harness.ViewModel.NoOnboardExitNotice) == LoadResultHandedOverExitAfterFailedResume;
            },
            "the compensation entry and the line naming it, once the resume's session closed",
            token);
        Assert.True(await harness.ViewModel.RequestLoadCompensationAsync(token));
        JsonElement compensation = await WaitForPayloadAsync(harness, "LoadCompensationResult", token);
        Assert.Equal("ALL_EMPTY", compensation.GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { RecoveryVector: null, UnsettledSlotOperationAttemptId: null }
                && harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the compensation to settle the load and the session to be ready again",
            token);
        Assert.Equal(unlocksBefore + 1, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 不是空仓不给（反向对照）：续行等「重新开门」时仓里读到有货，「不装了」不出现，按下去什么都不发——那时放弃会把货留在
    /// 一个不再被这次装货认领的仓里。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task GivingUpAResumeIsNotOfferedOverASlotThatReadsLoaded()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(io, _ => { }, token);
        WireToGateRecoveryState opened = await EndTheLoadUnknownAndOpenAResumeAsync(harness, io, token);

        io.CloseDoorAfterNextUnlock(0, cargo: false);
        int unlocksBefore = io.UnlockCount;
        await harness.Server.SendCommandAsync("SlotOperationResumeCommand", Guid.NewGuid().ToString("D"), ResumeOf(opened));
        await harness.WaitUntilAsync(
            () =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                return io.UnlockCount == unlocksBefore + 1 && harness.ViewModel.CanAbandonResume;
            },
            "the control: 「不装了」 offered over the empty slot",
            token);

        // 光幕读到有货（例如篮子是在门关上之后才被读到的）：不再给。
        io.SetCargoPresent(0, true);
        io.PublishSnapshot();
        await harness.WaitUntilAsync(
            () =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                return !harness.ViewModel.CanAbandonResume && !harness.Business.CanAbandonResume;
            },
            "「不装了」 to go once the slot reads loaded",
            token);
        Assert.False(await harness.Business.AbandonResumeAsync(token));
        string resumeResultId = FakeControlServerIdentifiers.StableUuid(
            $"recovery-operation-result:{AttemptA}:{opened.RecoveryActionId}");
        Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.MessageType == "OperationResult" && envelope.MessageId == resumeResultId);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>不是续行不给：头一次装货等「重新开门」时，出路是「取消装货」，「不装了」不出现。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task GivingUpIsNotOfferedOverTheFirstRunOfALoad()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(io, _ => { }, token);

        io.CloseDoorAfterNextUnlock(0, cargo: false);
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await harness.WaitUntilAsync(
            () =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                return harness.Business.CanReopenWaitingSlot && harness.ViewModel.CanRequestLoadCancellation;
            },
            "the first run to hold slot 1 shut empty, with the cancellation offered",
            token);
        Assert.False(harness.ViewModel.CanAbandonResume);
        Assert.False(harness.Business.CanAbandonResume);
        Assert.False(await harness.Business.AbandonResumeAsync(token));
        Assert.Empty(harness.UiErrors);
    }

    private const string LoadResultHandedOverExitAfterFailedResume =
        "这次装货没有完成，已交服务端处理。仓里没有放货的话：请联系系统维护人员开放这辆车的恢复，开放后按「补偿清空」。";

    /// <summary>
    /// A 的装货以 UNKNOWN 收尾、结果交服务端；操作员关好空仓门，按「申请恢复」，服务端开会话、受理续行动作。返回受理后的日志。
    /// </summary>
    private static async Task<WireToGateRecoveryState> EndTheLoadUnknownAndOpenAResumeAsync(
        Harness harness,
        FakeIoModuleClient io,
        CancellationToken token)
    {
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.SetUnreadable(0);
        JsonElement loadResult = await WaitForOperationResultAsync(harness, AttemptA, token);
        Assert.Equal("UNKNOWN", loadResult.GetProperty("overallOutcome").GetString());
        io.CloseDoor(0, cargo: false);
        io.PublishSnapshot();
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && harness.Business.CanRequestResumeAfterRepair,
            "the resume entry over the load handed to the server",
            token);

        Assert.True(await harness.Business.RequestResumeAfterRepairAsync("现场维修完成，申请恢复原仓位操作。", token));
        WireToGateRecoveryState? accepted = null;
        await harness.WaitUntilAsync(
            () => (accepted = ReadJournal(harness, token)).RecoveryActionId is not null,
            "the accepted RESUME_AFTER_REPAIR action to be persisted",
            token);
        return accepted!;
    }

    /// <summary>The resume command the real server issues for the accepted action: every field what the vehicle has on file.</summary>
    private static object ResumeOf(WireToGateRecoveryState state) =>
        new
        {
            exceptionRecoverySessionId = state.ExceptionRecoverySessionId,
            recoveryActionId = state.RecoveryActionId,
            demandId = DemandA,
            slotOperationAttemptId = AttemptA,
            provenRecoveryCheckpoint = state.ProvenRecoveryCheckpoint switch
            {
                WireToGateRecoveryCheckpoint.Prepared => "PREPARED",
                WireToGateRecoveryCheckpoint.ActiveUnlockSet => "ACTIVE_UNLOCK_SET",
                _ => "SAFE_FINISH_REACHED"
            },
            slots = new[] { 1 },
            commandContentSha256 = state.OperationContext!.CommandContentSha256
        };

    /// <summary>
    /// The server's close once the resume's replacement result is judged not reconciled (control-server
    /// <c>AdvanceSessionAfterResultAsync</c>: <c>RECOVERY_ACTION_RESULT_NOT_RECONCILED</c>).
    /// </summary>
    private static object ClosedAfterResume(WireToGateRecoveryState opened) =>
        new
        {
            exceptionRecoverySessionId = opened.ExceptionRecoverySessionId,
            recoverySessionRevision = 9,
            state = "CLOSED",
            administratorId = "maintenance-001",
            administratorRole = "MAINTENANCE_ADMINISTRATOR",
            eventId = opened.RecoverySessionRequestId,
            demandId = DemandA,
            slotOperationAttemptId = AttemptA,
            slots = new[] { 1 },
            selectedAction = "RESUME_AFTER_REPAIR",
            allowedActions = Array.Empty<string>(),
            closedReason = "RECOVERY_ACTION_RESULT_NOT_RECONCILED",
            blockingFacts = Array.Empty<object>()
        };
}
