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
        Func<CancellationToken, Task<bool>> press = harness.ViewModel.BeginLoadCancellation();

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
        bool answered = await press(token);

        Assert.DoesNotContain(harness.Events, item => item.Kind == "RECOVERY_BLOCKED");
        Assert.True(answered);
        WireToGateOperatorEvent notice = Assert.Single(harness.Events, item => item.Kind == NothingToCancelKind);
        Assert.Equal(
            "按下确认时，屏上那次可以取消的装货已经不在了（通常是装货刚好结束），取消没有执行，也没有开任何仓门。"
            + "请以仓位状态为准；如需退回货物，请联系系统维护人员。",
            notice.Message);
        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.Equal(1, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
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
        Func<CancellationToken, Task<bool>> first = harness.ViewModel.BeginLoadCancellation();
        Func<CancellationToken, Task<bool>> second = harness.ViewModel.BeginLoadCancellation();
        io.CloseDoor(4, cargo: true);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LastCompletedLoadOperationContext?.SlotOperationAttemptId == AttemptA
                && ReadJournal(harness, token).UnsettledSlotOperationAttemptId is null,
            "A to complete and be recorded",
            token);

        Assert.True(await first(token));
        Assert.True(await second(token));

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
        Func<CancellationToken, Task<bool>> press = harness.ViewModel.BeginLoadCancellation();
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

        bool answered = await press(token);

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
        Func<CancellationToken, Task<bool>> press = harness.ViewModel.BeginLoadCancellation();

        // 2 号仓开着时锁反馈读不到：装货以 UNKNOWN 结束，attempt 未结。
        io.SetUnreadable(1);
        JsonElement loadResult = await WaitForOperationResultAsync(harness, AttemptA, token);
        Assert.Equal("UNKNOWN", loadResult.GetProperty("overallOutcome").GetString());
        Assert.Equal(AttemptA, ReadJournal(harness, token).UnsettledSlotOperationAttemptId);

        bool answered = await press(token);

        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.True(answered);
        Assert.Single(harness.Events, item => item.Kind == NothingToCancelKind);
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
        Func<CancellationToken, Task<bool>> press = harness.ViewModel.BeginLoadCancellation();

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

        bool answered = await press(token);

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
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellationBeforeAnySublot,
            "the cancellation-before-sublot entry to be offered",
            token);
        Func<CancellationToken, Task<bool>> press = harness.ViewModel.BeginLoadCancellation();

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);

        bool answered = await press(token);

        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.True(answered);
        Assert.Single(harness.Events, item => item.Kind == NothingToCancelKind);
        Assert.Equal(AttemptA, ReadJournal(harness, token).UnsettledSlotOperationAttemptId);
        Assert.Equal(1, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }
}
