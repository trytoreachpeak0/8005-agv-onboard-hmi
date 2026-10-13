using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 操作员取消 B 的在途装货、清空以 <c>UNKNOWN</c> 结束、结果已被服务端确认之后，车不能卡在这条取消上
/// （trytoreachpeak0/8005-agv-onboard-hmi#343）。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前。</b>取消向量故意留在日志簿里，好让再按入口时重跑它；可结果一经确认，重跑只会按发件箱里已确认的
/// <c>UNKNOWN</c> 收尾，什么都不发、什么都不开，提示还被去重器吞掉。更要紧的是，补偿、故障货物交接、强制机械恢复
/// 三个入口都因为「日志簿里有别的类型的向量」被 <c>RECOVERY_VECTOR_CONFLICT</c> 挡住（第 1 步实测，
/// <c>evidence/hmi-343/01-*</c>），服务端那边旅程卡在 <c>LoadCancellationResult_NOT_RECONCILED</c>，没有出路。
/// </para>
/// <para>
/// <b>构造。</b>与 <see cref="AnInFlightCancellationOfTheSecondDemandEndingUnknownKeepsTheScreenOnThatDemandAndShutsTheEntry"/>
/// 相同，只是夹具开了恢复开关与凭据（管理员恢复要用），假服务端应答恢复会话并下发补偿命令。清空报 <c>UNKNOWN</c> 后，
/// 操作员把 1 号仓清空关好，这样挡路的只可能是那条向量，不是门检。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 取消的结果一经确认为非 COMPLETED，在途取消入口就关掉，与「这次取消已有结果就关入口」（onboard-hmi#254）同一条规则；
    /// 向量离开日志簿，但它交接过来时还开着的 1 号仓与这次被接管的尝试都留着——「可能已开过门」这件事不丢。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnAcknowledgedUnknownCancellationShutsTheEntryAndKeepsTheDoorInDoubt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationUnknownStopAsync(io, token);

        await CancelBEndingUnknownAsync(harness, io, token);

        await harness.WaitUntilAsync(
            () => !harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry to shut once its UNKNOWN is acknowledged",
            token);
        await HoldsForHalfASecondAsync(
            () =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                Assert.False(harness.Business.CanRequestLoadCancellation);
                Assert.False(harness.ViewModel.CanRequestLoadCancellation);
                Assert.False(harness.ViewModel.CanPressLoadCancellation);
                Assert.False(ButtonOf(harness.ScreenState(), "OnLoadCancellationClick") is { Visible: true, Enabled: true });
            },
            token);

        WireToGateRecoveryState onFile = ReadJournal(harness, token);
        Assert.Null(onFile.RecoveryVector);
        Assert.Equal(AttemptB, onFile.UnsettledSlotOperationAttemptId);
        Assert.Equal(AttemptB, onFile.TakenOverSlotOperationAttemptId);
        Assert.Equal([1], onFile.ActiveUnlockSlots);
        Assert.Equal(AttemptA, onFile.LastCompletedLoadOperationContext?.SlotOperationAttemptId);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 入口关掉之后仍到达业务层的按键（界面在关之前读到入口亮着），每按一次都得到一条应答：这次装货已取消、取消已有结论，
    /// 要处理货物请联系维护人员。修之前它重跑向量、按发件箱里已确认的 UNKNOWN 收尾，提示的去重键与第一次相同，
    /// <c>Events</c> 一条不增，屏上什么都没有（hmi#164 的诊断）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnInFlightCancellationPressedAfterItsUnknownIsAcknowledgedIsAnswered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationUnknownStopAsync(io, token);
        // No wait on the entry: the press is the one that got past it. CancelBEndingUnknownAsync has already seen the
        // server's readiness change, which the fake writes after the result's DurableAck.
        await CancelBEndingUnknownAsync(harness, io, token);

        for (int press = 1; press <= 2; press++)
        {
            int eventsBefore = harness.Events.Count;
            await harness.Business.RequestLoadCancellationAsync(
                SQCD.Agv.Wpf.WireToGateBusinessService.LoadCancellationDefaultReason,
                null,
                token);
            WireToGateOperatorEvent answer = Assert.Single(harness.Events.Skip(eventsBefore));
            Assert.Equal("RECOVERY_BLOCKED", answer.Kind);
            Assert.Contains("取消已有结论", answer.Message, StringComparison.Ordinal);
            Assert.Contains("联系维护人员", answer.Message, StringComparison.Ordinal);
        }

        Assert.Single(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Equal(2, io.UnlockCount);
        Assert.Null(ReadJournal(harness, token).RecoveryVector);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 走到「B 的取消报 UNKNOWN、服务端已确认、会话 RecoveryRequired、1 号仓已清空关好」：A 在 5 号仓装好结算，B 在 1 号仓开门后
    /// 操作员不动，按「取消装货」，替身在授权前把 1 号仓锁反馈设成读不到。
    /// </summary>
    private static async Task CancelBEndingUnknownAsync(Harness harness, FakeIoModuleClient io, CancellationToken token)
    {
        await harness.WaitUntilAsync(
            () => harness.ViewModel.WorklistItems.Count == 2,
            "both worklist rows",
            token);
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.CloseDoor(4, cargo: true);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LastCompletedLoadOperationContext?.SlotOperationAttemptId == AttemptA
                && ReadJournal(harness, token).UnsettledSlotOperationAttemptId is null,
            "A to complete and be recorded",
            token);

        await SendSlotCommandAsync(harness, DemandB, AttemptB, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptB, pulses: 2, token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanRequestLoadCancellation,
            "the in-flight cancellation entry over B",
            token);
        await harness.ViewModel.RequestLoadCancellationAsync(token);

        JsonElement unknown = Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Equal("UNKNOWN", unknown.GetProperty("overallOutcome").GetString());
        Assert.Equal(DemandB, unknown.GetProperty("demandId").GetString());
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the server to hold the vehicle for B's recovery",
            token);
        // The operator empties B's slot and shuts it: from here on a door check is not what stands in any exit's way.
        io.CloseDoor(0, cargo: false);
        Assert.Equal(2, io.UnlockCount);
    }

    private static Task<Harness> StartCancellationUnknownStopAsync(
        FakeIoModuleClient io,
        CancellationToken token,
        Action<FakeControlServer>? alsoConfigure = null,
        string? journalPath = null) =>
        StartTakeOverStopAsync(
            io,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
                server.BeforeLoadCancellationAuthorization = () => io.SetUnreadable(0);
                server.SendLoadCompensationCommandOnRequest = true;
                server.RecoverySlotOperationAttemptId = AttemptB;
                server.RecoveryVectorSlotOperationAttemptId = AttemptB;
                server.HoldOperationOnUnreconciledLoadCancellationResult = true;
                alsoConfigure?.Invoke(server);
            },
            token,
            journalPath: journalPath);
}
