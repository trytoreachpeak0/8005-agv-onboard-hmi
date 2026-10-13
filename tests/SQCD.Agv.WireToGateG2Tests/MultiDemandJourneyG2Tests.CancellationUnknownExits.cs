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
    /// 维护人员的出路真走得通：屏上按「补偿」，为 B 开会话、申请授权，补偿命令接管 1 号仓（取消交接过来、留在疑点里的那扇门，
    /// 操作员已清空关好，所以等它而不再开一次），结果
    /// <c>ALL_EMPTY</c>，B 的尝试收尾、日志簿清干净。上一条应答叫人「联系维护人员走恢复流程」，这一条就是那条流程。
    /// 修之前补偿入口是灰的，按下去被 <c>RECOVERY_VECTOR_CONFLICT</c> 拒绝，什么都不发（第 1 步实测）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationTakesOverAnAcknowledgedUnknownCancellationAndSettlesIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationUnknownStopAsync(io, token);
        await CancelBEndingUnknownAsync(harness, io, token);

        await harness.WaitUntilAsync(
            () =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                return harness.ViewModel.CanRequestLoadCompensation;
            },
            "the compensation entry over B",
            token);
        Assert.True(await harness.ViewModel.RequestLoadCompensationAsync(token));

        JsonElement session = Assert.Single(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));
        Assert.Equal(DemandB, session.GetProperty("demandId").GetString());
        Assert.Equal([1], session.GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()));
        JsonElement compensation = await WaitForPayloadAsync(harness, "LoadCompensationResult", token);
        Assert.Equal("ALL_EMPTY", compensation.GetProperty("overallOutcome").GetString());
        Assert.Equal(DemandB, compensation.GetProperty("demandId").GetString());
        Assert.Equal(AttemptB, compensation.GetProperty("slotOperationAttemptId").GetString());
        // Not pulsed again: slot 1 is the door the cancellation took over open and left in doubt, which the compensation
        // waits on rather than pulses, and the operator has already emptied and shut it.
        Assert.Equal(2, io.UnlockCount);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { RecoveryVector: null, UnsettledSlotOperationAttemptId: null } state
                && state.ActiveUnlockSlots.Count == 0
                && state.TakenOverSlotOperationAttemptId is null,
            "the compensation to settle B's attempt and clear the journal",
            token);
        Assert.Equal(AttemptA, ReadJournal(harness, token).LastCompletedLoadOperationContext?.SlotOperationAttemptId);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 门怎么都证明不了时的出路也走得通：为 B 申请强制机械恢复，确认隔离与人工取出（带货物交接记录），服务端确认
    /// <c>MECHANICALLY_ISOLATED</c>。被取消留在疑点里的 1 号仓进了隔离记录、标为物理状态未知——「可能已开过门」这件事
    /// 从取消一路带到了硬件恢复记录之前。修之前这个入口同样被 <c>RECOVERY_VECTOR_CONFLICT</c> 挡住（第 1 步实测）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedMechanicalRecoveryTakesOverAnAcknowledgedUnknownCancellationAndKeepsItsDoor()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationUnknownStopAsync(
            io,
            token,
            server => server.RecoverySessionScopeByDemand = new Dictionary<string, int[]>(StringComparer.Ordinal)
            {
                [DemandA] = [5],
                [DemandB] = [1]
            });
        await CancelBEndingUnknownAsync(harness, io, token);

        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestForcedMechanicalRecovery,
            "the forced mechanical recovery entry",
            token);
        Assert.True(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "现场确认仓门无法电动解锁，申请强制机械恢复。",
            DemandB,
            token));
        JsonElement session = Assert.Single(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));
        Assert.Equal(DemandB, session.GetProperty("demandId").GetString());
        Assert.Equal([1], session.GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()));

        await harness.WaitUntilAsync(
            () => harness.Business.CanConfirmForcedMechanicalRecovery,
            "the authorized forced recovery to wait for the operator's confirmation",
            token);
        await ConfirmForcedRecoveryWithHandoffAsync(harness, "SUBLOT-B", token);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { RecoveryVector: null, ForcedIsolation: not null },
            "the acknowledged isolation to settle the forced recovery",
            token);

        JsonElement result = Assert.Single(ReceivedPayloads(harness, "ForcedMechanicalRecoveryResult"));
        Assert.Equal(DemandB, result.GetProperty("demandId").GetString());
        Assert.Equal("MECHANICALLY_ISOLATED", result.GetProperty("outcome").GetString());
        Assert.Contains(1, ReadJournal(harness, token).ForcedIsolation!.PhysicallyUnknownSlots);
        Assert.Equal(2, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 修复续行不把已取消的装货重跑：取消已有结论后「申请恢复」入口关闭，按键到了业务层也只得到一条应答，不开会话、不开门。
    /// 修之前它走得通，续作命令重开 1 号仓、提示「请在1号仓放入货物并关门」，把操作员取消掉的装货又装一遍，跑完向量与未结尝试
    /// 仍留在日志簿里（第 1 步实测 <c>01-probe-resume*.txt</c>）。被取消接管的装货，结论归那次取消（ADR-cross-0046），
    /// 补偿与强制机械恢复已经走得通，所以这里选「不重跑」而不是「重跑后收尾」。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeAfterRepairDoesNotRerunALoadWhoseCancellationIsConcluded()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationUnknownStopAsync(io, token);
        await CancelBEndingUnknownAsync(harness, io, token);

        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCompensation,
            "the recovery entries to be offered over B",
            token);
        await HoldsForHalfASecondAsync(
            () => Assert.False(harness.Business.CanRequestResumeAfterRepair),
            token);

        for (int press = 1; press <= 2; press++)
        {
            int eventsBefore = harness.Events.Count;
            Assert.False(await harness.Business.RequestResumeAfterRepairAsync("现场确认修复完成。", token));
            WireToGateOperatorEvent answer = Assert.Single(harness.Events.Skip(eventsBefore));
            Assert.Equal("RECOVERY_BLOCKED", answer.Kind);
            Assert.Contains("取消已有结论", answer.Message, StringComparison.Ordinal);
            Assert.Contains("补偿清空", answer.Message, StringComparison.Ordinal);
        }

        Assert.Empty(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));
        Assert.Empty(ReceivedPayloads(harness, "RecoveryActionSubmitted"));
        Assert.Equal(2, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 重启后与重启前一致：取消入口与修复续行入口仍关着，补偿入口照样给出，门的疑点仍在日志簿里。「这次取消已有结论」
    /// 在进程里只是个缓存，重启就没了；入口要从发件箱里那条取消结果读回来，不能因为缓存空了又亮起来。会话就绪到还原流程读完
    /// 发件箱之间有一个毫秒级的窗口（与 onboard-hmi#254 的修正入口相同），所以先等入口关上、再持续半秒；窗口里的按键由按键路径
    /// 读发件箱拦下（<see cref="AnInFlightCancellationPressedAfterItsUnknownIsAcknowledgedIsAnswered"/>）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AcrossARestartAnAcknowledgedUnknownCancellationStaysShutAndCompensable()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness first = await StartCancellationUnknownStopAsync(io, token, journalPath: journalPath);
        await CancelBEndingUnknownAsync(first, io, token);
        await first.WaitUntilAsync(
            () => ReadJournal(first, token).RecoveryVector is null,
            "the acknowledged cancellation's vector to leave the journal",
            token);
        await first.StopVehicleAsync();
        first.Server.SimulateOnboardProcessRestart();

        await using Harness afterRestart = await Harness.StartAgainstAsync(
            first.Server,
            token,
            journalPath,
            io,
            recoveryOptions: TakeOverRecovery,
            messageTimeout: TimeSpan.FromSeconds(30));
        await afterRestart.WaitUntilAsync(
            () => afterRestart.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && afterRestart.Business.CanRequestLoadCompensation
                && !afterRestart.Business.CanRequestLoadCancellation,
            "the restarted vehicle held for B's recovery, compensation offered and the cancellation entry shut",
            token);
        await HoldsForHalfASecondAsync(
            () =>
            {
                Assert.False(afterRestart.Business.CanRequestLoadCancellation);
                Assert.False(afterRestart.Business.CanRequestResumeAfterRepair);
                Assert.True(afterRestart.Business.CanRequestLoadCompensation);
            },
            token);

        WireToGateRecoveryState onFile = ReadJournal(afterRestart, token);
        Assert.Null(onFile.RecoveryVector);
        Assert.Equal(AttemptB, onFile.UnsettledSlotOperationAttemptId);
        Assert.Equal(AttemptB, onFile.TakenOverSlotOperationAttemptId);
        Assert.Equal([1], onFile.ActiveUnlockSlots);
        Assert.Single(first.Server.ReceivedEnvelopes, envelope => envelope.MessageType == "LoadCancellationStartRequested");
        Assert.Equal(2, io.UnlockCount);
        Assert.Empty(afterRestart.UiErrors);
    }

    /// <summary>
    /// 确认不在首次发送时到达、靠重连补发的那条路，与关入口和按下的竞态：结果待确认时入口还亮着，操作员按下（确认框弹出，
    /// 按键绑在这条取消上）；重连后握手补发结果、服务端确认，车载端按发件箱收尾、忘掉向量、关入口；操作员这时才确认。
    /// 这一按得到一条应答，说清取消已有结论、要处理货物请联系维护人员，不发申请、不开门；之后补偿照样给出。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AConfirmationLeftOpenAcrossTheReplayedAcknowledgementIsAnswered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationUnknownStopAsync(
            io,
            token,
            server => server.LoadCancellationResultAcksToDrop = 1,
            messageTimeout: TimeSpan.FromSeconds(2));
        await CancelBEndingUnknownAsync(harness, io, token, resultAcknowledged: false);

        // The result is owed: the vector stays, and the entry with it.
        Assert.NotNull(ReadJournal(harness, token).RecoveryVector);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the entry still up while B's result is owed",
            token);
        SQCD.Agv.Wpf.WireToGateLoadCancellationPress press = harness.Business.BindLoadCancellationPress(
            SQCD.Agv.Wpf.WireToGateBusinessService.LoadCancellationDefaultReason,
            DemandA);
        Assert.Equal(DemandB, press.SubjectDemandId);

        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is null && !harness.Business.CanRequestLoadCancellation,
            "the replayed result's acknowledgement to settle the vector and shut the entry",
            token);

        int eventsBefore = harness.Events.Count;
        await press.SendAsync(token);
        WireToGateOperatorEvent answer = Assert.Single(harness.Events.Skip(eventsBefore));
        Assert.Equal("RECOVERY_BLOCKED", answer.Kind);
        Assert.Contains("取消已有结论", answer.Message, StringComparison.Ordinal);
        Assert.Contains("联系维护人员", answer.Message, StringComparison.Ordinal);

        Assert.Single(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.All(
            ReceivedPayloads(harness, "LoadCancellationResult"),
            result => Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString()));
        Assert.Equal(2, io.UnlockCount);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCompensation,
            "the compensation entry over B after the replay",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 走到「B 的取消报 UNKNOWN、服务端已确认（<paramref name="resultAcknowledged"/> 为 false 时服务端收下不回确认）、会话 RecoveryRequired、1 号仓已清空关好」：A 在 5 号仓装好结算，B 在 1 号仓开门后
    /// 操作员不动，按「取消装货」，替身在授权前把 1 号仓锁反馈设成读不到。
    /// </summary>
    private static async Task CancelBEndingUnknownAsync(
        Harness harness,
        FakeIoModuleClient io,
        CancellationToken token,
        bool resultAcknowledged = true)
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
        if (resultAcknowledged)
        {
            await harness.WaitUntilAsync(
                () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
                "the server to hold the vehicle for B's recovery",
                token);
        }

        // The operator empties B's slot and shuts it: from here on a door check is not what stands in any exit's way.
        io.CloseDoor(0, cargo: false);
        Assert.Equal(2, io.UnlockCount);
    }

    private static Task<Harness> StartCancellationUnknownStopAsync(
        FakeIoModuleClient io,
        CancellationToken token,
        Action<FakeControlServer>? alsoConfigure = null,
        string? journalPath = null,
        TimeSpan? messageTimeout = null) =>
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
            messageTimeout,
            journalPath);
}
