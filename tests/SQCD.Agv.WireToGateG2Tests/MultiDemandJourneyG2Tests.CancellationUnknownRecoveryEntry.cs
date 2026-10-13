using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 一站两条需求：A 已装完结算，取消 B 的在途装货，清空以 <c>UNKNOWN</c> 结束——屏上当前操作是 B（被取消的那一条），
/// 不是 A，也不是空；结果一经确认，在途取消入口关掉，再按也动不到 A（trytoreachpeak0/8005-agv-onboard-hmi#164、#343）。
/// </summary>
/// <remarks>
/// <para>
/// <b>构造。</b>A 在 5 号仓装好关门、结算；B 在 1 号仓开门，操作员一直不动（<c>OperatorNeverActs</c>），门交接给取消时仍开着。
/// 替身在写出取消授权之前把 1 号仓的锁反馈设成读不到：取消向量的前检拒绝这扇交接过来的门，结果报 <c>UNKNOWN</c>。这条路径
/// 单需求时由 <see cref="ACancellationRefusedWhileTheHandedOverDoorStandsOpenReportsThatDoorInTheHandshake"/> 钉住；这里多的是第二条需求。
/// </para>
/// <para>
/// <b>为什么断到视图层。</b>视图只从被抛出的事件学习当前操作，而业务层快照在事件被去重器吞掉时照写（onboard-hmi#161），
/// 只断业务层会假绿。所以这里断三处：视图模型的取消入口（选中行故意放在 A 上，排除「碰巧选中了 B」），
/// 最后一条带 operation 的已发布事件，以及真按下去之后车上执行和上报的对象。
/// </para>
/// <para>
/// <b>#343 改了什么。</b>hmi#164 时这里钉的是「入口一直亮着、确认文案复述 B」：那时向量故意留在日志簿里，再按就重跑它。
/// 可结果已确认后重跑只会以同一个 UNKNOWN 收尾，留着的向量还把补偿等出路全挡住，所以 #343 让确认后的向量离开日志簿，
/// 入口按「这次取消已有结果就关入口」（onboard-hmi#254）关掉。指向 B 而不是 A 的那半没变。
/// </para>
/// <para>
/// <b>它什么时候会红。</b>屏上当前操作不再是 B 的这次尝试，入口在结果确认后仍亮着，或再按时为 A 申请、开了 A 的仓。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnInFlightCancellationOfTheSecondDemandEndingUnknownKeepsTheScreenOnThatDemandAndShutsTheEntry()
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
                server.BeforeLoadCancellationAuthorization = () => io.SetUnreadable(0);
            });
        await harness.WaitUntilAsync(
            () => harness.ViewModel.WorklistItems.Count == 2,
            "both worklist rows",
            token);

        // A loads and settles first.
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.CloseDoor(4, cargo: true);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LastCompletedLoadOperationContext?.SlotOperationAttemptId == AttemptA
                && ReadJournal(harness, token).UnsettledSlotOperationAttemptId is null,
            "A to complete and be recorded",
            token);

        // B's load is in flight, its door standing open; the operator's selected row is A.
        await SendSlotCommandAsync(harness, DemandB, AttemptB, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptB, pulses: 2, token);
        SelectWorklistItem(harness, DemandA);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanRequestLoadCancellation,
            "the in-flight cancellation entry over B",
            token);

        // The operator cancels B; the clearing ends UNKNOWN over its unreadable, still-open door.
        await harness.ViewModel.RequestLoadCancellationAsync(token);
        JsonElement unknown = Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Equal("UNKNOWN", unknown.GetProperty("overallOutcome").GetString());
        Assert.Equal(DemandB, unknown.GetProperty("demandId").GetString());
        Assert.Equal(AttemptB, unknown.GetProperty("slotOperationAttemptId").GetString());

        // The screen is left on B, the cancelled demand, and the entry shuts once B's UNKNOWN is acknowledged.
        await harness.WaitUntilAsync(
            () => LastPublishedOperation(harness) is { Stage: WireToGateHmiOperationStage.RecoveryRequired } operation
                && operation.SlotOperationAttemptId == AttemptB
                && !harness.ViewModel.CanRequestLoadCancellation,
            "the screen to stay on B, the cancelled demand, with the entry shut",
            token);
        await HoldsForHalfASecondAsync(
            () =>
            {
                WireToGateHmiOperationSnapshot? operation = LastPublishedOperation(harness);
                Assert.NotNull(operation);
                Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, operation.Stage);
                Assert.Equal(AttemptB, operation.SlotOperationAttemptId);
                Assert.False(harness.ViewModel.CanRequestLoadCancellation);
                Assert.False(harness.ViewModel.CanPressLoadCancellation);
                Assert.False(ButtonOf(harness.ScreenState(), "OnLoadCancellationClick") is { Visible: true, Enabled: true });
                Assert.Equal(DemandA, harness.ViewModel.SelectedWorklistItem?.DemandId);
            },
            token);
        WireToGateRecoveryState onFile = ReadJournal(harness, token);
        Assert.Null(onFile.RecoveryVector);
        Assert.Equal(AttemptB, onFile.UnsettledSlotOperationAttemptId);

        // A press that reaches the business layer anyway, with A still the selected row, acts on nothing: not on A, which
        // a fall-through to a cancellation before any sublot for the selected row would ask for, and not on B again. What it
        // tells the operator is pinned by AnInFlightCancellationPressedAfterItsUnknownIsAcknowledgedIsAnswered.
        await harness.Business.RequestLoadCancellationAsync(
            SQCD.Agv.Wpf.WireToGateBusinessService.LoadCancellationDefaultReason,
            DemandA,
            token);

        JsonElement request = Assert.Single(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.Equal(DemandB, request.GetProperty("demandId").GetString());
        Assert.Equal(AttemptB, request.GetProperty("slotOperationAttemptId").GetString());
        Assert.All(ReceivedPayloads(harness, "LoadCancellationResult"), result =>
        {
            Assert.Equal(DemandB, result.GetProperty("demandId").GetString());
            Assert.Equal(AttemptB, result.GetProperty("slotOperationAttemptId").GetString());
        });
        WireToGateRecoveryState afterPress = ReadJournal(harness, token);
        Assert.Null(afterPress.RecoveryVector);
        Assert.Equal(AttemptA, afterPress.LastCompletedLoadOperationContext?.SlotOperationAttemptId);
        // No door was opened by either press: B's door was handed over open, and A's slot was never touched again.
        Assert.Equal(2, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The operation the screen last learnt of: the last published event that carries one, taken as it is. The view learns
    /// the current operation only from these events, so this is the screen's input, not a copy of its rules.
    /// </summary>
    private static WireToGateHmiOperationSnapshot? LastPublishedOperation(Harness harness) =>
        harness.Events.LastOrDefault(item => item.Operation is not null)?.Operation;

    private static async Task HoldsForHalfASecondAsync(Action assertion, CancellationToken token)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow.AddMilliseconds(500);
        do
        {
            assertion();
            await Task.Delay(20, token);
        }
        while (DateTimeOffset.UtcNow < until);
    }
}
