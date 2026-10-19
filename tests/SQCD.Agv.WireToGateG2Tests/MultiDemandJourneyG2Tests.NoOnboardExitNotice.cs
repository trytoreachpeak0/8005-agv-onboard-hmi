using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ScreenState;
using SQCD.Agv.Wpf.ViewModels;
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
        // 这里原来断「装货在途时说明不出现」（PR #371 审查 S3）：那一刻会话是 Ready、日志里也还没有待答的结果，说明行的
        // 两个前提都不成立，那条断言永远过。真正能失败的判据在会话不再要求恢复的那几刻：
        // TheNoticeGoesOnceTheSessionNoLongerRequiresRecoveryWhileTheResultIsStillPending。
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
        // 屏幕快照里有这一行（审查 R1）：真装置场景按 ItemStatus 判读是哪种操作、结果确不确定。
        AssertNoOnboardExitStatus(harness.ScreenState(), "LOAD:UNKNOWN");

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

    /// <summary>
    /// 说明行的 <c>Readiness == RecoveryRequired</c> 判据（PR #371 审查 S3）：待答的 UNKNOWN 结果还在日志里，但会话断了、或者
    /// 服务端答了 READY（control-server#771 撤下旅程之后就是这样），说明都要消失——「车上没有出口」只在服务端要求恢复时才是
    /// 对操作员说的。去掉这个判据，两处都红。
    /// </summary>
    /// <remarks>
    /// 每一刻都同时断「日志里仍有这次装货的待答结果」与「屏上没有任何恢复按钮」：说明行的另外两个前提照旧成立，消失只能是
    /// 因为会话。READY 那一刻用替身的 <c>AnswerReadyOverPendingFactsForTest</c>：真服务端对带着待答事实的车答
    /// RECOVERY_REQUIRED，这里要的正是「服务端不再要求恢复、车上日志还没清」这一刻，与 cs#771 落地后的形状一样。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task TheNoticeGoesOnceTheSessionNoLongerRequiresRecoveryWhileTheResultIsStillPending()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(io, token);
        await EndTheLoadUnknownAsync(harness, io, token);
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.NoOnboardExitNotice) == NoOnboardExitAfterUnknownLoad,
            "the screen to say the vehicle has no exit of its own",
            token);

        // 断线：会话 Disconnected，结果仍待答。
        await harness.Session.Client.DisconnectAsync();
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Disconnected
                && harness.OnUi(() => harness.ViewModel.WireToGateText) == "离线",
            "the screen to show the session gone",
            token);
        await harness.WaitUntilAsync(
            () => !harness.OnUi(() => harness.ViewModel.HasNoOnboardExitNotice),
            "the notice to go with the session",
            token);
        Assert.Null(harness.Business.NoOnboardExitNotice);
        AssertResultStillPending(harness, token);
        AssertNoRecoveryButtonShown(harness.ScreenState());

        // 重连，服务端答 READY：结果仍待答，说明不回来。
        harness.Server.AnswerReadyOverPendingFactsForTest = true;
        harness.Server.AnswerReadyOverOperationsNeedingRecoveryForTest = true;
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready
                && harness.OnUi(() => harness.ViewModel.WireToGateText) == "就绪",
            "the new session to be ready on screen",
            token);
        Assert.False(harness.OnUi(() => harness.ViewModel.HasNoOnboardExitNotice));
        Assert.Null(harness.Business.NoOnboardExitNotice);
        AssertResultStillPending(harness, token);
        AssertNoRecoveryButtonShown(harness.ScreenState());
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 锁存严重故障时一次装货以 FAILED 收尾（照 <c>FatalFaultLatch</c> 里两仓装货的步骤），此后不再有任何会话、旅程、IO 或
    /// 操作员事件：说明行仍要出现在屏上（PR #371 审查 S1）。
    /// </summary>
    /// <remarks>
    /// 修之前这一行只在别的刷新路径上顺带重算；恢复状态缓存最后一次变化（待答结果写进日志）之后没有别的事件，屏上就一直是
    /// 空串，业务服务里却早有这句话。屏幕快照里同时带上 <c>LOAD:NOT_COMPLETED</c>（审查 R1）。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ALatchedLoadThatEndedFailedShowsTheNoticeWithNoFurtherEvent()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(io, token);
        await EndTheLoadFailedUnderALatchAsync(harness, io, token);

        await harness.WaitUntilAsync(
            () => harness.Business.NoOnboardExitNotice is not null,
            "the business service to have the notice",
            token);
        string expected = harness.Business.NoOnboardExitNotice!;
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.NoOnboardExitNotice) == expected,
            "the screen to show the notice with no further event",
            token);
        AssertNoRecoveryButtonShown(harness.ScreenState());
        AssertNoOnboardExitStatus(harness.ScreenState(), "LOAD:NOT_COMPLETED");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// FAILED 不说「未知」（PR #371 审查 S2）：锁存拒绝开门给出的 FAILED，门关好锁住、每一仓的状态都读得出来，结果是确定的。
    /// UNKNOWN 照旧说「未知」，由 <see cref="AnUnknownLoadWithTheSwitchOffSaysTheVehicleHasNoExitEvenAfterTheReasonChanges"/> 断。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AFailedLoadIsNotCalledUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(io, token);
        await EndTheLoadFailedUnderALatchAsync(harness, io, token);

        await harness.WaitUntilAsync(
            () => harness.Business.NoOnboardExitNotice is not null,
            "the business service to have the notice",
            token);
        Assert.Equal(NoOnboardExitAfterFailedLoad, harness.Business.NoOnboardExitNotice);
        Assert.DoesNotContain("未知", harness.Business.NoOnboardExitNotice, StringComparison.Ordinal);
    }

    /// <summary>
    /// 在途时按下「取消装货」，确认之前装货在锁存下以 FAILED 收尾、结果已交服务端：提醒同样不说「未知」（PR #371 审查 S2）。
    /// UNKNOWN 的那一格是 <see cref="APressConfirmedAfterTheLoadEndedUnknownSendsNothing"/>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task APressConfirmedAfterTheLoadEndedFailedIsNotToldTheResultIsUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(
            io,
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationDecision = "REJECTED";
            });

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry",
            token);
        LoadCancellationConfirmation press = harness.ViewModel.BeginLoadCancellation();
        await FinishTheLoadFailedUnderALatchAsync(harness, io, token);
        await harness.WaitUntilAsync(
            () => harness.Business.NoOnboardExitNotice is not null,
            "the load's result to be on file waiting for the server",
            token);

        bool answered = await press.SendAsync(token);

        Assert.True(answered);
        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        WireToGateOperatorEvent notice = Assert.Single(harness.Events, item => item.Kind == NothingToCancelKind);
        Assert.Equal(
            "这次装货没有完成，已交服务端处理，车上已不能再取消。这次取消没有执行，也没有开任何仓门。"
            + "请联系系统维护人员在服务端处理。",
            notice.Message);
        Assert.Equal(1, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    private const string NoOnboardExitAfterFailedLoad =
        "这次装货没有完成，已交服务端处理。车上没有可以处理它的按钮，请联系系统维护人员在服务端处理。";

    /// <summary>两仓装货，2 号仓开着时锁反馈读不到：以 UNKNOWN 收尾，服务端回 RECOVERY_REQUIRED。</summary>
    private static async Task EndTheLoadUnknownAsync(Harness harness, FakeIoModuleClient io, CancellationToken token)
    {
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 2, token);
        io.SetUnreadable(1);
        JsonElement loadResult = await WaitForOperationResultAsync(harness, AttemptA, token);
        Assert.Equal("UNKNOWN", loadResult.GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the session to require recovery",
            token);
    }

    /// <summary>
    /// <c>FatalFaultLatch</c> 里两仓装货那一格的形状：1 号仓开着时锁存，操作员装好关门，轮到 2 号仓开门被拒，FAILED。
    /// </summary>
    private static async Task EndTheLoadFailedUnderALatchAsync(Harness harness, FakeIoModuleClient io, CancellationToken token)
    {
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await FinishTheLoadFailedUnderALatchAsync(harness, io, token);
    }

    private static async Task FinishTheLoadFailedUnderALatchAsync(Harness harness, FakeIoModuleClient io, CancellationToken token)
    {
        LatchNow(harness);
        io.CloseDoor(0, cargo: true);
        JsonElement report = Assert.NotNull(await WaitForResultOrPulseAsync(harness, io, AttemptA, pulsesBefore: 1, token));
        Assert.Equal("FAILED", report.GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the session to require recovery",
            token);
    }

    /// <summary>说明行的其余两个前提照旧：这次装货仍未结、结果仍在日志里等服务端。</summary>
    private static void AssertResultStillPending(Harness harness, CancellationToken token)
    {
        WireToGateRecoveryState journal = ReadJournal(harness, token);
        Assert.Equal(AttemptA, journal.UnsettledSlotOperationAttemptId);
        Assert.Null(journal.RecoveryVector);
        Assert.Contains(
            journal.PendingResults,
            pending => pending.MessageType == "OperationResult" && pending.BusinessId == AttemptA);
    }

    /// <summary>说明行在屏幕快照里的那一项：AutomationId <c>NoOnboardExitNotice</c>，ItemStatus 是操作与结果。</summary>
    private static void AssertNoOnboardExitStatus(HmiScreenState screen, string expected)
    {
        HmiRawCodeState status = Assert.Single(screen.RawCodes, code => code.AutomationId == "NoOnboardExitNotice");
        Assert.Equal((expected, true), (status.Value, status.Visible));
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
