using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
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
/// 断的是三件事：没有 <c>RECOVERY_BLOCKED</c>（屏上的「故障」就是它映射出来的），服务端没收到任何取消申请，门只开过装货那一次。
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

        bool answered = await harness.Business.RequestLoadCancellationAsync(
            WireToGateBusinessService.LoadCancellationDefaultReason,
            token);

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
}
