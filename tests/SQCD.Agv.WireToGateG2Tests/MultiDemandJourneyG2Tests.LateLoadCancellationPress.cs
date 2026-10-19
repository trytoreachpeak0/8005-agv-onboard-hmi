using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 「取消装货」的按键落到业务层时，屏上那次可以取消的对象已经不在了：车上不报故障、不发申请、不开门，只给一条提醒
/// （trytoreachpeak0/8005-agv-onboard-hmi#291）。
/// </summary>
/// <remarks>
/// <para>
/// 现场形状（2026-10-10 agv02，5 号仓）：服务端等装货结果时每 2 秒重发同一个 <c>SlotOperationCommand</c>，车载端忽略；
/// 门关上、装货结算；再来的重复命令按原结果重放；随后一次「取消装货」按键落到业务层。确认框是模态的，按钮点下时装货还在途，
/// 点「是」时已经结束。修之前这一下走到扫码前取消的兜底分支，抛 <c>RECOVERY_OPERATION_CONTEXT_MISSING</c>，屏上记成「故障」，
/// 而实际一切正常。
/// </para>
/// <para>
/// 按键走视图模型的真实路径：装货在途时 <see cref="MainViewModel.BeginLoadCancellation"/> 取下按键（按钮点下、确认框弹出），
/// 装货结束后才执行它（点「是」）。断的是三件事：没有 <c>RECOVERY_BLOCKED</c>（屏上的「故障」就是它映射出来的），
/// 服务端没收到任何取消申请，门只开过装货那一次。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    private const string NothingToCancelKind = "LOAD_CANCELLATION_NOTHING_TO_CANCEL";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task APressLandingAfterTheLoadSettledAmidResentCommandsIsANoticeNotAFault()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(
            io,
            token,
            server => server.RespondToLoadCancellationRequests = true);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry",
            token);
        // 按下「取消装货」，确认框开着。
        LoadCancellationConfirmation press = harness.ViewModel.BeginLoadCancellation();

        // 服务端等结果时的自动重发：车载端忽略，不再开门。
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);

        io.CloseDoor(4, cargo: true);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LastCompletedLoadOperationContext?.SlotOperationAttemptId == AttemptA
                && ReadJournal(harness, token).UnsettledSlotOperationAttemptId is null,
            "A to complete and be recorded",
            token);

        // 结算之后的重发：按原结果重放。
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await harness.WaitUntilAsync(
            () => harness.Events.Any(item => item.Kind == "OPERATION_REPLAY"),
            "the resent command to be answered from the result on file",
            token);

        // 这时才在确认框上点「是」。
        bool answered = await press.SendAsync(token);

        Assert.DoesNotContain(harness.Events, item => item.Kind == "RECOVERY_BLOCKED");
        Assert.True(answered);
        WireToGateOperatorEvent notice = Assert.Single(harness.Events, item => item.Kind == NothingToCancelKind);
        Assert.Equal(
            "按下确认时，要取消的对象已经变了，这次取消没有执行，也没有开任何仓门。"
            + "如仍需取消，请重新按「取消装货」，按屏上当前显示为准。",
            notice.Message);
        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.Equal(1, io.UnlockCount);
        Assert.Empty(harness.UiErrors);

        // 屏上的操作记录：这一行记作「提醒」，整段没有一行「故障」。
        await harness.WaitUntilAsync(
            () => harness.LogsSnapshot().Any(line => line.Message == notice.Message),
            "the notice to reach the operator record",
            token);
        Assert.Equal("提醒", harness.LogsSnapshot().Single(line => line.Message == notice.Message).CategoryText);
        Assert.DoesNotContain(harness.LogsSnapshot(), line => line.CategoryText == "故障");
    }

    /// <summary>
    /// 两次迟到的按键（都在 A 在途时按下、A 结束后才确认），两次都只是提醒；接着下一张装货在途时按取消，申请照常发到服务端。
    /// </summary>
    /// <remarks>
    /// 后半是护栏，不是复现：本票的失败定义之一是「一次真正的装货取消被这次修复挡掉」。它点名 B 的 attempt，
    /// 说明落到服务端的是在途那一张，不是上一张已经结算的 A。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ALatePressTwiceIsTwoNoticesAndTheNextLoadInFlightIsStillCancellable()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource authorizationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(
            io,
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
                server.LoadCancellationAuthorizationHold = authorizationHeld.Task;
            });
        using ReleaseOnExit releaseAuthorization = new(() => authorizationHeld.TrySetResult());

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry over A",
            token);
        LoadCancellationConfirmation first = harness.ViewModel.BeginLoadCancellation();
        LoadCancellationConfirmation second = harness.ViewModel.BeginLoadCancellation();
        io.CloseDoor(4, cargo: true);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LastCompletedLoadOperationContext?.SlotOperationAttemptId == AttemptA
                && ReadJournal(harness, token).UnsettledSlotOperationAttemptId is null,
            "A to complete and be recorded",
            token);

        Assert.True(await first.SendAsync(token));
        Assert.True(await second.SendAsync(token));

        Assert.Equal(2, harness.Events.Count(item => item.Kind == NothingToCancelKind));
        Assert.DoesNotContain(harness.Events, item => item.Kind == "RECOVERY_BLOCKED");
        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));

        await SendSlotCommandAsync(harness, DemandB, AttemptB, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptB, pulses: 2, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry over B",
            token);

        _ = harness.ViewModel.RequestLoadCancellationAsync(token);
        await harness.WaitUntilAsync(
            () => ReceivedPayloads(harness, "LoadCancellationStartRequested").Length == 1,
            "B's cancellation request to reach the server",
            token);

        JsonElement request = Assert.Single(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.Equal(AttemptB, request.GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal(2, harness.Events.Count(item => item.Kind == NothingToCancelKind));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 装货结算后断线重连，服务端在新会话里重放同一个 <c>SlotOperationCommand</c>，车载端按原结果重放；这时落到业务层的
    /// 取消按键同样只是提醒。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task APressAfterTheCommandIsReplayedIntoANewSessionIsANoticeNotAFault()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(
            io,
            token,
            server => server.RespondToLoadCancellationRequests = true);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry",
            token);
        LoadCancellationConfirmation press = harness.ViewModel.BeginLoadCancellation();
        io.CloseDoor(4, cargo: true);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LastCompletedLoadOperationContext?.SlotOperationAttemptId == AttemptA
                && ReadJournal(harness, token).UnsettledSlotOperationAttemptId is null,
            "A to complete and be recorded",
            token);

        // The harness connects the client itself, so the test reconnects it, as the other reconnect cases do.
        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the new session to be ready",
            token);
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await harness.WaitUntilAsync(
            () => harness.Events.Any(item => item.Kind == "OPERATION_REPLAY"),
            "the replayed command to be answered from the result on file",
            token);

        bool answered = await press.SendAsync(token);

        Assert.DoesNotContain(harness.Events, item => item.Kind == "RECOVERY_BLOCKED");
        Assert.True(answered);
        Assert.Single(harness.Events, item => item.Kind == NothingToCancelKind);
        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.Equal(1, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 失败／状态未知分支：装货在途时按下，确认之前装货以 UNKNOWN 结束、结果已经报出去。确认只给提醒，不向服务端申请取消。
    /// </summary>
    /// <remarks>
    /// 这一格入口早就灭了（结果在路上，服务端对 <c>RecoveryRequired</c> 的操作不授权取消，onboard-hmi#188），但修之前按键路径
    /// 不看结果，确认之后照样把申请发出去。服务端这里配成拒绝，免得修之前的那次授权真去开门。
    /// <para>
    /// 提醒的文字也断（onboard-hmi#370，2026-10-19 agv03 5 号仓）：修之前它说「如仍需取消，请重新按「取消装货」」，而那个入口
    /// 此时已经灰了、结果出来之后再也不会亮——屏上叫人去按一个按不动的按钮。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task APressConfirmedAfterTheLoadEndedUnknownSendsNothing()
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
        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 2, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry",
            token);
        LoadCancellationConfirmation press = harness.ViewModel.BeginLoadCancellation();

        // 2 号仓开着时锁反馈读不到：装货以 UNKNOWN 结束，attempt 未结。
        io.SetUnreadable(1);
        JsonElement loadResult = await WaitForOperationResultAsync(harness, AttemptA, token);
        Assert.Equal("UNKNOWN", loadResult.GetProperty("overallOutcome").GetString());
        Assert.Equal(AttemptA, ReadJournal(harness, token).UnsettledSlotOperationAttemptId);

        bool answered = await press.SendAsync(token);

        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.True(answered);
        WireToGateOperatorEvent notice = Assert.Single(harness.Events, item => item.Kind == NothingToCancelKind);
        // The notice does not send the operator back to an entry that is shut for good (onboard-hmi#370, agv03 2026-10-19):
        // the result is the server's now, and nothing on the vehicle can cancel it.
        Assert.Equal(
            "这次装货的结果未知，已交服务端处理，车上已不能再取消。这次取消没有执行，也没有开任何仓门。"
            + "请联系系统维护人员在服务端处理。",
            notice.Message);
        Assert.DoesNotContain("重新按", notice.Message, StringComparison.Ordinal);
        Assert.False(harness.Business.CanRequestLoadCancellation);
        Assert.DoesNotContain(harness.Events, item => item.Kind == "RECOVERY_BLOCKED");
        Assert.Equal(2, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 确认框开着时对象换了：按下时在途的是装货 A，确认时 A 已结束、装货 B 在途。确认只给提醒，不去取消 B。
    /// </summary>
    /// <remarks>
    /// 修之前确认落到「当前在途的那一张」，于是操作员看着 A 点的「是」，取消的是 B——二次确认的键没有含比对对象。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task APressTakenOverLoadAIsNotSpentOnLoadBConfirmedInItsPlace()
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

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry over A",
            token);
        LoadCancellationConfirmation press = harness.ViewModel.BeginLoadCancellation();

        io.CloseDoor(4, cargo: true);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LastCompletedLoadOperationContext?.SlotOperationAttemptId == AttemptA
                && ReadJournal(harness, token).UnsettledSlotOperationAttemptId is null,
            "A to complete and be recorded",
            token);
        await SendSlotCommandAsync(harness, DemandB, AttemptB, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptB, pulses: 2, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry over B",
            token);

        bool answered = await press.SendAsync(token);

        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.True(answered);
        Assert.Single(harness.Events, item => item.Kind == NothingToCancelKind);
        Assert.Equal(AttemptB, ReadJournal(harness, token).UnsettledSlotOperationAttemptId);
        Assert.Equal(2, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 确认框开着时对象换了：按下时是扫码前取消（不开门），确认时同一需求的装货已经在途。确认只给提醒，不变成一次在途取消。
    /// </summary>
    /// <remarks>
    /// 扫码前取消服务端授权时不带仓位、车上不开任何门；在途取消要接管仓位、开门清空。修之前确认落到在途那一路，
    /// 操作员确认的是「不开门」的那一种，执行的却是会开门的那一种。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task APressTakenBeforeAnySublotIsNotSpentOnTheLoadThatStartedMeanwhile()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await Harness.StartAsync(
            server => ConfigureStop(
                server,
                ["SUBLOT-A"],
                [Payloads.ItemA],
                server => server.LoadCancellationDecision = "REJECTED"),
            token,
            io: io);
        await harness.WaitUntilAsync(
            () => harness.Business.ExpectedSublots is not null && harness.ViewModel.WorklistItems.Count == 1,
            "the one-item worklist and the entry request to arrive",
            token);
        // At a machine pickup the cancellation before any sublot is the row's 「取消」 (8005-agv-onboard-hmi#363).
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestWorklistRowCancellation(DemandA),
            "the cancellation-before-sublot entry to be offered",
            token);
        WorklistRowCancellationConfirmation press = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandA));

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);

        bool answered = await press.SendAsync(token);

        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.True(answered);
        Assert.Single(harness.Events, item => item.Kind == NothingToCancelKind);
        Assert.Equal(AttemptA, ReadJournal(harness, token).UnsettledSlotOperationAttemptId);
        Assert.Equal(1, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 装货在途时清单里选中了另一行：确认框复述的是在途装货那一条，取消的也是它，选中行不进确认文案。
    /// </summary>
    /// <remarks>
    /// 在途取消的对象是在途装货本身，与选中哪一行无关；确认框原来照样复述选中行，于是操作员读到的是 B，取消的是 A。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task TheConfirmationNamesTheLoadInFlightNotTheSelectedRow()
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
        await harness.WaitUntilAsync(
            () => harness.ViewModel.WorklistItems.Count == 2,
            "both worklist rows",
            token);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry over A",
            token);
        SelectWorklistItem(harness, DemandB);

        // 文案与按键出自同一次按下（审查 S1）。
        LoadCancellationConfirmation press = harness.ViewModel.BeginLoadCancellation();
        Assert.StartsWith("将要取消的任务：子批 SUBLOT-A，", press.DetailText, StringComparison.Ordinal);
        Assert.DoesNotContain("SUBLOT-B", press.DetailText, StringComparison.Ordinal);

        await press.SendAsync(token);
        JsonElement request = Assert.Single(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.Equal(DemandA, request.GetProperty("demandId").GetString());
        Assert.Equal(AttemptA, request.GetProperty("slotOperationAttemptId").GetString());
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 一次扫码前取消已经发出、还没等到答复：只有日志簿里那条需求的那一行按得动，别的行与底部入口都不给，再按重发的也是它。
    /// </summary>
    /// <remarks>
    /// 原名 <c>WithACancellationSentAndUnansweredTheConfirmationNamesItsDemandNotTheSelectedRow</c>，钉的是「先选另一行、底部
    /// 入口的确认框仍复述 B」。CP-0016 起机台上扫码前取消在每一行上（8005-agv-onboard-hmi#363），同一件事换成「另一行按不动」。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task WithACancellationSentAndUnansweredOnlyItsRowCanBePressedAndItResendsIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartTwoItemStopAsync(
            configure: server => server.LoadCancellationAuthorizationsToDrop = 1,
            cancellationToken: token);
        Assert.False(await harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB)).SendAsync(token));
        Assert.True(harness.Business.IsLoadCancellationBeforeSublotOpen);

        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray()) is
                [{ DemandId: DemandA, CanPressCancellation: false }, { DemandId: DemandB, CanPressCancellation: true }],
            "only B's row, the demand the sent cancellation is about, to be pressable",
            token);
        Assert.False(harness.Business.CanRequestLoadCancellation);

        await harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB)).SendAsync(token);
        Assert.All(
            Received(harness, "LoadCancellationStartRequested"),
            request => Assert.Equal(DemandB, request.GetProperty("demandId").GetString()));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 扫码前取消已经授权、结果还没被确认（向量在日志里）：只有向量那条需求的那一行按得动，别的行与底部入口都不给，再按补报的
    /// 也是它。
    /// </summary>
    /// <remarks>
    /// 原名 <c>WithABeforeSublotCancellationOnFileTheConfirmationNamesItsDemandNotTheSelectedRow</c>，改法同上一条
    /// （8005-agv-onboard-hmi#363）。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task WithABeforeSublotCancellationOnFileOnlyItsRowCanBePressedAndItReportsIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartTwoItemStopAsync(
            configure: server => server.LoadCancellationResultAcksToDrop = 1,
            cancellationToken: token);
        Assert.False(await harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB)).SendAsync(token));
        Assert.NotNull(ReadJournal(harness, token).RecoveryVector);

        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray()) is
                [{ DemandId: DemandA, CanPressCancellation: false }, { DemandId: DemandB, CanPressCancellation: true }],
            "only B's row, the demand the cancellation on file is about, to be pressable",
            token);
        Assert.False(harness.Business.CanRequestLoadCancellation);

        Assert.True(await harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB)).SendAsync(token));
        Assert.All(
            Received(harness, "LoadCancellationResult"),
            result => Assert.Equal(DemandB, result.GetProperty("demandId").GetString()));
        Assert.Single(Received(harness, "LoadCancellationStartRequested"));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 在途取消已经授权、在等操作员清空（向量在日志里）：选中另一行时，确认框复述的仍是向量那条需求。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task WithAnInFlightCancellationOnFileTheConfirmationNamesItsDemandNotTheSelectedRow()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(
            io,
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
            });
        await harness.WaitUntilAsync(
            () => harness.ViewModel.WorklistItems.Count == 2,
            "both worklist rows",
            token);
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry over A",
            token);
        _ = harness.ViewModel.RequestLoadCancellationAsync(token);
        await harness.WaitUntilAsync(
            () => harness.Business.CachedRecoveryStateForTest.RecoveryVector is { } vector && vector.DemandId == DemandA,
            "A's cancellation to be on file, waiting for slot 1 to be emptied",
            token);

        SelectWorklistItem(harness, DemandB);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.LoadCancellationConfirmationDetailText.StartsWith(
                "将要取消的任务：子批 SUBLOT-A，",
                StringComparison.Ordinal),
            "the confirmation to name A, the demand the cancellation on file is about",
            token);
        Assert.DoesNotContain(
            "SUBLOT-B",
            harness.ViewModel.LoadCancellationConfirmationDetailText,
            StringComparison.Ordinal);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 在途装货的需求不在本站清单里：确认框只说「正在进行的这次装货」，不拿选中行顶替。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ALoadInFlightOffTheWorklistIsNamedAsSuchNotAsTheSelectedRow()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string offWorklistDemand = "cccccccc-0000-4000-8000-00000000000c";
        const string offWorklistAttempt = "44444444-0000-4444-8444-00000000000c";
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(
            io,
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationDecision = "REJECTED";
            });
        await harness.WaitUntilAsync(
            () => harness.ViewModel.WorklistItems.Count == 2,
            "both worklist rows",
            token);
        await SendSlotCommandAsync(harness, offWorklistDemand, offWorklistAttempt, [5]);
        await WaitForDoorOpenAsync(harness, io, offWorklistAttempt, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry",
            token);
        SelectWorklistItem(harness, DemandB);

        Assert.Equal(
            "将要取消的是正在进行的这次装货。",
            harness.ViewModel.LoadCancellationConfirmationDetailText);
        Assert.Empty(harness.UiErrors);
    }
}
