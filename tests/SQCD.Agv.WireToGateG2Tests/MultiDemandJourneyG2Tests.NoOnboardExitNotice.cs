using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ScreenState;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 一次装货以 UNKNOWN 收尾、结果已交服务端，而车上恢复开关关着（出厂就是这样）：车上一个恢复入口都没有，屏上要明确说
/// 「车上没有可按的，联系系统维护人员在服务端处理」，不能什么都不显示（trytoreachpeak0/8005-agv-onboard-hmi#370）。
/// </summary>
/// <remarks>
/// <para>
/// 现场形状（2026-10-19 agv03，旅程 <c>40c5532e</c>，5 号仓）：第三次开锁脉冲后锁只「未锁」了约 230 ms，开锁反馈 3 秒超时，
/// 车载端日志簿里那条 <c>OperationResult</c> 是 <c>overallOutcome=UNKNOWN</c>、5 号仓 <c>ACTION_NOT_ALLOWED_IN_STATE</c>
/// （执行器把 <c>TimeoutException</c> 映射成这个码）。服务端判 <c>RECOVERY_REQUIRED</c>，车上「取消装货」灭了，
/// 补偿等入口都在开关后面——屏上没有任何能按的，也没有任何一句说明。
/// </para>
/// <para>
/// 这一行只是说明，不是按钮，所以不必实测去按；断的是它出现时，屏上所有恢复按钮确实都不可见（说的是真话）。
/// 车被人工挪动后原因码会从 <c>SESSION_RECOVERY_REQUIRED</c> 变成 <c>DEPARTURE_UNSAFE</c>，待答的结果仍在：说明不能因为
/// 原因码换了就消失。开关开着、补偿入口亮着时说明不出现，由
/// <see cref="TheLoadCancellationEntryClosesOnceTheLoadsNonCompletedResultIsOnItsWay"/> 断。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    private const string NoOnboardExitAfterUnknownLoad =
        "这次装货的结果未知，已交服务端处理。车上没有可以处理它的按钮，请联系系统维护人员在服务端处理。";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnUnknownLoadWithTheSwitchOffSaysTheVehicleHasNoExitEvenAfterTheReasonChanges()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(io, token);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        // 装货在途、门开着：车上有出口（「取消装货」），说明不出现。
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CanRequestLoadCancellation),
            "the in-flight cancellation entry",
            token);
        Assert.False(harness.OnUi(() => harness.ViewModel.HasNoOnboardExitNotice));

        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 2, token);
        // 2 号仓开着时锁反馈读不到：装货以 UNKNOWN 结束，attempt 未结，服务端回 RECOVERY_REQUIRED。
        io.SetUnreadable(1);
        JsonElement loadResult = await WaitForOperationResultAsync(harness, AttemptA, token);
        Assert.Equal("UNKNOWN", loadResult.GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && !harness.OnUi(() => harness.ViewModel.CanRequestLoadCancellation),
            "the session to require recovery with the cancellation entry shut",
            token);

        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.NoOnboardExitNotice) == NoOnboardExitAfterUnknownLoad,
            "the screen to say the vehicle has no exit of its own",
            token);
        AssertNoRecoveryButtonShown(harness.ScreenState());

        // 车被人工挪走：原因码换成 DEPARTURE_UNSAFE，待答的 UNKNOWN 结果仍在，说明照旧。
        harness.Server.ReadinessReasonOverride = "DEPARTURE_UNSAFE";
        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && harness.Session.Current.ReasonCodes.Contains("DEPARTURE_UNSAFE"),
            "the new session to require recovery for departure safety",
            token);
        Assert.Equal(AttemptA, ReadJournal(harness, token).UnsettledSlotOperationAttemptId);
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.NoOnboardExitNotice) == NoOnboardExitAfterUnknownLoad,
            "the screen to keep saying the vehicle has no exit",
            token);
        AssertNoRecoveryButtonShown(harness.ScreenState());
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>The recovery entries of the window, every one hidden: what the notice says is true of the screen.</summary>
    private static void AssertNoRecoveryButtonShown(HmiScreenState screen)
    {
        string[] recoveryButtons =
        [
            "OnWireToGateRecoveryClick",
            "OnLoadCancellationClick",
            "OnLoadCompensationClick",
            "OnLoadCorrectionClick",
            "OnFaultCargoHandoffClick",
            "OnForcedMechanicalRecoveryClick",
            "OnConfirmForcedMechanicalRecoveryClick",
            "OnSubmitHardwareRecoveryRecordClick",
            "OnHardwareRepairReleaseClick",
            "OnCloseConflictedRecoveryClick",
        ];
        Assert.All(recoveryButtons, key => Assert.False(ButtonOf(screen, key).Visible, key));
    }
}
