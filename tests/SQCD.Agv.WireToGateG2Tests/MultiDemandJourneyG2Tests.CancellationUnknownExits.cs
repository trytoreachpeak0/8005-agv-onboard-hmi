using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 操作员取消 B 的在途装货、清空以非 COMPLETED 结束、结果已被服务端确认之后，车不能卡在这条取消上
/// （trytoreachpeak0/8005-agv-onboard-hmi#343）。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前。</b>取消向量故意留在日志簿里，好让再按入口时重跑它；可结果一经确认，重跑只会按发件箱里已确认的
/// <c>UNKNOWN</c> 收尾，什么都不发、什么都不开，提示还被去重器吞掉。更要紧的是，补偿、故障交接、强制机械恢复
/// 三个入口都因为「日志簿里有别的类型的向量」被 <c>RECOVERY_VECTOR_CONFLICT</c> 挡住（第 1 步实测，
/// <c>evidence/hmi-343/01-*</c>），服务端那边旅程卡在 <c>LoadCancellationResult_NOT_RECONCILED</c>，没有出路。
/// </para>
/// <para>
/// <b>B 有没有自己的结果，决定服务端放哪几条出路，所以两种情形分开、各自造确定。</b>
/// <see cref="LoadResultOfB.None"/>：装货被取消静默中止，车上不发它的 <c>OperationResult</c>（这是现场按「取消装货」的常态）。
/// 真服务端的取消结果只把需求置为 <c>RecoveryRequired</c>，不碰仓位操作，所以它拒绝补偿（control-server#697 跟进），但交接与
/// 强制机械恢复照放；就绪靠握手报上来的未结尝试进入 <c>RECOVERY_REQUIRED</c>，所以这一侧的用例先重连一次再按恢复入口。
/// <see cref="LoadResultOfB.UnknownFirst"/>：装货自己先以 <c>UNKNOWN</c> 结束、结果先于取消结果到服务端，仓位操作因此是
/// <c>RecoveryRequired</c>，补偿合法。审查时同一夹具 5 次里 4 次是后者、1 次是前者（PR #346 审查 H1），这里各自钉死。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 取消的结果一经确认为非 COMPLETED，在途取消入口就关掉，与「这次取消已有结果就关入口」（onboard-hmi#254）同一条规则；
    /// 向量离开日志簿，但它交接过来时还开着的 1 号仓与这次被接管的尝试都留着——「可能已开过门」这件事不丢。B 没有自己的结果，
    /// 所以入口关掉只能是因为「这次取消已有结论」，不是因为待发的装货结果。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnAcknowledgedUnknownCancellationShutsTheEntryAndKeepsTheDoorInDoubt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationUnknownStopAsync(io, LoadResultOfB.None, token);
        await CancelBEndingUnknownAsync(harness, io, LoadResultOfB.None, token, holdForRecovery: false);

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

        AssertDoorInDoubtKept(ReadJournal(harness, token));
        Assert.Equal(AttemptA, ReadJournal(harness, token).LastCompletedLoadOperationContext?.SlotOperationAttemptId);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 入口关掉之后仍到达业务层的按键（界面在关之前读到入口亮着），每按一次都得到一条应答：这次装货已取消、取消已有结论，
    /// 要处理货物请联系维护人员，按三个恢复入口走。修之前它重跑向量、按发件箱里已确认的 UNKNOWN 收尾，提示的去重键与第一次
    /// 相同，<c>Events</c> 一条不增，屏上什么都没有（hmi#164 的诊断）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnInFlightCancellationPressedAfterItsUnknownIsAcknowledgedIsAnswered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationUnknownStopAsync(io, LoadResultOfB.None, token);
        await CancelBEndingUnknownAsync(harness, io, LoadResultOfB.None, token, holdForRecovery: false);
        // No wait on the entry: the press is the one that got past it. The result's DurableAck is in: the helper waited
        // for the press, which returns once the send has its acknowledgement.

        for (int press = 1; press <= 2; press++)
        {
            int eventsBefore = harness.Events.Count;
            await harness.Business.RequestLoadCancellationAsync(
                WireToGateBusinessService.LoadCancellationDefaultReason,
                null,
                token);
            AssertConcludedCancellationAnswer(Assert.Single(RecoveryBlockedSince(harness, eventsBefore)));
        }

        Assert.Single(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Equal(2, io.UnlockCount);
        Assert.Null(ReadJournal(harness, token).RecoveryVector);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// B 的装货先报了 <c>UNKNOWN</c>、仓位操作因此在服务端是需要恢复的：屏上按「补偿清空」，为 B 开会话、申请授权，补偿命令接管
    /// 1 号仓（取消交接过来、留在疑点里的那扇门，操作员已清空关好，所以等它而不再开一次），结果 <c>ALL_EMPTY</c>，B 的尝试收尾、
    /// 日志簿清干净。修之前补偿入口是灰的，按下去被 <c>RECOVERY_VECTOR_CONFLICT</c> 拒绝，什么都不发（第 1 步实测）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationTakesOverAnAcknowledgedUnknownCancellationAndSettlesIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationUnknownStopAsync(io, LoadResultOfB.UnknownFirst, token);
        await CancelBEndingUnknownAsync(harness, io, LoadResultOfB.UnknownFirst, token);

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
    /// B 没有自己的结果时，服务端会拒绝补偿（control-server#697），能走的是故障交接：为 B 开会话，交接接管 1 号仓（取消留在疑点
    /// 里、操作员已清空关好的门，等它而不再开），结果 <c>HANDED_OFF</c>，B 的尝试收尾、日志簿清干净。修之前这个入口同样被
    /// <c>RECOVERY_VECTOR_CONFLICT</c> 挡住（第 1 步实测）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AFaultCargoHandoffTakesOverAnAcknowledgedUnknownCancellationWithNoLoadResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationUnknownStopAsync(io, LoadResultOfB.None, token);
        await CancelBEndingUnknownAsync(harness, io, LoadResultOfB.None, token);

        await HandOffBAsync(harness, io, DemandB, token);
        Assert.Equal(2, io.UnlockCount);
        Assert.Equal(AttemptA, ReadJournal(harness, token).LastCompletedLoadOperationContext?.SlotOperationAttemptId);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 门怎么都证明不了时的出路也走得通：为 B 申请强制机械恢复，确认隔离与人工取出（带货物交接记录），服务端确认
    /// <c>MECHANICALLY_ISOLATED</c>。被取消留在疑点里的 1 号仓进了隔离记录、标为物理状态未知。B 没有自己的结果，真服务端也放这条。
    /// 修之前这个入口同样被 <c>RECOVERY_VECTOR_CONFLICT</c> 挡住（第 1 步实测）。
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
            LoadResultOfB.None,
            token,
            server => server.RecoverySessionScopeByDemand = new Dictionary<string, int[]>(StringComparer.Ordinal)
            {
                [DemandA] = [5],
                [DemandB] = [1]
            });
        await CancelBEndingUnknownAsync(harness, io, LoadResultOfB.None, token);

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
    /// 仍留在日志簿里（第 1 步实测 <c>01-probe-resume*.txt</c>）。B 没有自己的结果时真服务端照放修复续行，所以这一侧的闸门只能在车上。
    /// 被取消接管的装货，结论归那次取消（ADR-cross-0046），交接与强制机械恢复已经走得通，所以这里选「不重跑」而不是「重跑后收尾」。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeAfterRepairDoesNotRerunALoadWhoseCancellationIsConcluded()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationUnknownStopAsync(io, LoadResultOfB.None, token);
        await CancelBEndingUnknownAsync(harness, io, LoadResultOfB.None, token);

        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestFaultCargoHandoff,
            "the recovery entries to be offered over B",
            token);
        await HoldsForHalfASecondAsync(
            () => Assert.False(harness.Business.CanRequestResumeAfterRepair),
            token);

        for (int press = 1; press <= 2; press++)
        {
            int eventsBefore = harness.Events.Count;
            Assert.False(await harness.Business.RequestResumeAfterRepairAsync("现场确认修复完成。", token));
            WireToGateOperatorEvent answer = Assert.Single(RecoveryBlockedSince(harness, eventsBefore));
            Assert.Contains("取消已有结论", answer.Message, StringComparison.Ordinal);
            AssertNamesTheThreeExits(answer.Message);
        }

        Assert.Empty(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));
        Assert.Empty(ReceivedPayloads(harness, "RecoveryActionSubmitted"));
        Assert.Equal(2, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 重启后与重启前一致：取消入口与修复续行入口仍关着，交接入口照样给出，门的疑点仍在日志簿里。「这次取消已有结论」在进程里
    /// 只是个标记，重启就没了，由还原流程从发件箱里那条取消结果读回。等的是读回之后那条技术日志，不是「入口关」本身：重启刚开始
    /// 时缓存还没读入，入口也读成关，随后又会亮一下（PR #346 审查 H3）。会话就绪到读回之间那个毫秒级窗口里的按键由按键路径读发件箱
    /// 拦下（<see cref="AnInFlightCancellationPressedAfterItsUnknownIsAcknowledgedIsAnswered"/>）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AcrossARestartAnAcknowledgedUnknownCancellationStaysShutAndRecoverable()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness first = await StartCancellationUnknownStopAsync(
            io,
            LoadResultOfB.None,
            token,
            journalPath: journalPath);
        await CancelBEndingUnknownAsync(first, io, LoadResultOfB.None, token, holdForRecovery: false);
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
            () => afterRestart.Logger.Entries.Any(entry => entry.Message.StartsWith(
                    "这次装货已被操作员取消且取消已有结果，取消与修复续行入口保持关闭",
                    StringComparison.Ordinal))
                && afterRestart.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && afterRestart.Business.CanRequestFaultCargoHandoff,
            "the restarted vehicle to read B's concluded cancellation back, held for B's recovery",
            token);
        await HoldsForHalfASecondAsync(
            () =>
            {
                Assert.False(afterRestart.Business.CanRequestLoadCancellation);
                Assert.False(afterRestart.Business.CanRequestResumeAfterRepair);
                Assert.True(afterRestart.Business.CanRequestFaultCargoHandoff);
            },
            token);

        AssertDoorInDoubtKept(ReadJournal(afterRestart, token));
        Assert.Single(first.Server.ReceivedEnvelopes, envelope => envelope.MessageType == "LoadCancellationStartRequested");
        Assert.Equal(2, io.UnlockCount);
        Assert.Empty(afterRestart.UiErrors);
    }

    /// <summary>
    /// 确认不在首次发送时到达、靠重连补发的那条路，与关入口和按下的竞态：结果待确认时入口还亮着，操作员按下（确认框弹出，
    /// 按键绑在这条取消上）；重连后握手补发结果、服务端确认，车载端按发件箱收尾、忘掉向量、关入口；操作员这时才确认。
    /// 这一按得到一条应答，说清取消已有结论、要处理货物请联系维护人员，不发申请、不开门；之后交接照样给出。
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
            LoadResultOfB.None,
            token,
            server => server.LoadCancellationResultAcksToDrop = 1,
            messageTimeout: TimeSpan.FromSeconds(2));
        await CancelBEndingUnknownAsync(harness, io, LoadResultOfB.None, token, holdForRecovery: false);

        // The result is owed: the vector stays, and the entry with it.
        Assert.NotNull(ReadJournal(harness, token).RecoveryVector);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the entry still up while B's result is owed",
            token);
        WireToGateLoadCancellationPress press = harness.Business.BindLoadCancellationPress(
            WireToGateBusinessService.LoadCancellationDefaultReason,
            DemandA);
        Assert.Equal(DemandB, press.SubjectDemandId);

        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is null && !harness.Business.CanRequestLoadCancellation,
            "the replayed result's acknowledgement to settle the vector and shut the entry",
            token);

        // Only the answer is counted: the restore the reconnect runs publishes its own notice about B into the same window
        // (PR #346 review H2).
        int eventsBefore = harness.Events.Count;
        await press.SendAsync(token);
        AssertConcludedCancellationAnswer(Assert.Single(RecoveryBlockedSince(harness, eventsBefore)));

        Assert.Single(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.All(
            ReceivedPayloads(harness, "LoadCancellationResult"),
            result => Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString()));
        Assert.Equal(2, io.UnlockCount);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestFaultCargoHandoff,
            "the handoff entry over B after the replay",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 重跑那一支也会收尾（PR #346 审查 M3）：结果的确认第一次没到，向量留着、入口亮着；操作员再按，车上重跑这条向量、补发同一条
    /// 结果，这次确认在发送里就到了——向量当场离开日志簿、入口关，不用等下一次会话状态变化。门这时仍读不到，重跑的结论仍是
    /// <c>UNKNOWN</c>，与已发出的那条一致。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ARepressWhoseResendIsAcknowledgedForgetsTheVectorThere()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationUnknownStopAsync(
            io,
            LoadResultOfB.None,
            token,
            server => server.LoadCancellationResultAcksToDrop = 1,
            messageTimeout: TimeSpan.FromSeconds(2));
        await CancelBEndingUnknownAsync(
            harness,
            io,
            LoadResultOfB.None,
            token,
            resultAcknowledged: false,
            holdForRecovery: false,
            emptyAndShutAfter: false);
        Assert.NotNull(ReadJournal(harness, token).RecoveryVector);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the entry still up while B's result is owed",
            token);

        await harness.Business.RequestLoadCancellationAsync(
            WireToGateBusinessService.LoadCancellationDefaultReason,
            null,
            token);

        WireToGateRecoveryState afterPress = ReadJournal(harness, token);
        Assert.Null(afterPress.RecoveryVector);
        Assert.Equal(AttemptB, afterPress.UnsettledSlotOperationAttemptId);
        Assert.Equal(AttemptB, afterPress.TakenOverSlotOperationAttemptId);
        Assert.False(harness.Business.CanRequestLoadCancellation);
        Assert.All(
            ReceivedPayloads(harness, "LoadCancellationResult"),
            result => Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString()));
        Assert.Single(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.Equal(2, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 结果是 <c>FAILED</c> 也一样（票面「或其他非 COMPLETED 的已确认结果」）。B 装 1、2 号仓：1 号仓装好关门，2 号仓开着时操作员
    /// 按取消；授权之后车上锁存严重安全故障，1 号仓要新开一次门、被拒（<c>NOT_STARTED</c>），交接过来的 2 号仓空着关上，结果
    /// <c>FAILED</c>（造法同 <see cref="ALatchArrivingWhileTheInFlightCancellationIsAskedOpensNoFurtherDoor"/>）。装货被取消中止，
    /// B 没有自己的结果。维护人员复核仓门、复位锁存、重连之后：取消与「申请恢复」入口都关，向量离开日志簿，被接管的尝试记着（2 号仓
    /// 被取消清空过），没有门在疑点里；故障交接把 B 的货交出、收尾。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnAcknowledgedFailedCancellationShutsTheEntriesAndIsHandedOff()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        Harness? latchTarget = null;
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                ConfigureCancellationStop(server);
                server.LoadCancellationAuthorizedSlots = [1, 2];
                server.BeforeLoadCancellationAuthorization = () => LatchNow(latchTarget!);
            },
            token);
        latchTarget = harness;

        await SendSlotCommandAsync(harness, DemandB, AttemptB, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptB, pulses: 1, token);
        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptB, pulses: 2, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry over B",
            token);
        // Held until this thread shuts it, as in the latch test: a late wake-up must not run the handed-over wait out.
        io.HoldWaitsUntilShut(1);
        Task<bool> press = harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is not null,
            "the authorized cancellation to take the slots over",
            token);
        Assert.True(harness.Controller.IsFatalFaultLatched);
        io.CloseDoor(1, cargo: false);
        JsonElement failed = await WaitForPayloadAsync(harness, "LoadCancellationResult", token);
        Assert.Equal("FAILED", failed.GetProperty("overallOutcome").GetString());
        Assert.Equal(DemandB, failed.GetProperty("demandId").GetString());
        await press;
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is null,
            "the acknowledged FAILED to leave the journal",
            token);
        Assert.DoesNotContain(
            ReceivedPayloads(harness, "OperationResult"),
            result => result.GetProperty("slotOperationAttemptId").GetString() == AttemptB);
        Assert.Equal(2, io.UnlockCount);
        WireToGateRecoveryState onFile = ReadJournal(harness, token);
        Assert.Equal(AttemptB, onFile.UnsettledSlotOperationAttemptId);
        Assert.Equal(AttemptB, onFile.TakenOverSlotOperationAttemptId);
        Assert.Empty(onFile.ActiveUnlockSlots);

        // Maintenance reviews the doors and lifts the latch; the handshake reports B's attempt still unsettled.
        Assert.True(await harness.Controller.ClearFatalFaultAsync("operator-134", token));
        await ReconnectHeldForRecoveryAsync(harness, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestFaultCargoHandoff,
            "the handoff entry over B once the latch is lifted",
            token);
        await HoldsForHalfASecondAsync(
            () =>
            {
                Assert.False(harness.Business.CanRequestLoadCancellation);
                Assert.False(harness.Business.CanRequestResumeAfterRepair);
            },
            token);

        // The handoff opens slot 1 to hand the basket out, and the operator shuts it empty; slot 2, which the cancellation
        // already emptied, is not opened again.
        io.CloseDoorOnceUnlockedAt(0, cargo: false, unlockCount: 3);
        await HandOffBAsync(harness, io, demandChoice: null, token);
        Assert.Equal(3, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 单需求也一样（PR #346 审查 M2）：站上只有 B 一条需求、车上没有已结算的装货，取消 B 报 <c>UNKNOWN</c>、确认之后入口关、
    /// 门的疑点留着，故障交接把 B 收尾。前面几条都是「A 已结算、取消 B」，这一条没有 A。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ASingleDemandsAcknowledgedUnknownCancellationShutsTheEntryAndIsHandedOff()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationUnknownStopAsync(
            io,
            LoadResultOfB.None,
            token,
            server => server.JourneySnapshotPayloads = new Dictionary<string, object>
            {
                ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemB),
                ["UpcomingStopPlanSnapshot"] = Payloads.Plan(
                    1,
                    [
                        Payloads.Leg(1, "TO_PICKUP", "BUSINESS", DemandB, "ST-01", "ARRIVED"),
                        Payloads.Leg(2, "TO_DROPOFF", "BUSINESS", DemandB, "ST-OPT", "PLANNED")
                    ])
            });
        await harness.WaitUntilAsync(
            () => harness.ViewModel.WorklistItems.Count == 1,
            "the one worklist row",
            token);

        await SendSlotCommandAsync(harness, DemandB, AttemptB, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptB, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanRequestLoadCancellation,
            "the in-flight cancellation entry over B",
            token);
        await harness.ViewModel.RequestLoadCancellationAsync(token);
        JsonElement unknown = Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Equal("UNKNOWN", unknown.GetProperty("overallOutcome").GetString());
        Assert.Empty(ReceivedPayloads(harness, "OperationResult"));
        await harness.WaitUntilAsync(
            () => !harness.Business.CanRequestLoadCancellation && ReadJournal(harness, token).RecoveryVector is null,
            "the entry to shut and the vector to leave once B's UNKNOWN is acknowledged",
            token);
        AssertDoorInDoubtKept(ReadJournal(harness, token));
        Assert.Null(ReadJournal(harness, token).LastCompletedLoadOperationContext);

        io.CloseDoor(0, cargo: false);
        await ReconnectHeldForRecoveryAsync(harness, token);
        await HandOffBAsync(harness, io, demandChoice: null, token);
        Assert.Equal(1, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>B 的装货在取消前有没有自己的结果（见类注释）。</summary>
    private enum LoadResultOfB
    {
        /// <summary>装货被取消静默中止，没有 <c>OperationResult</c>。</summary>
        None,

        /// <summary>装货先以 <c>UNKNOWN</c> 结束，结果先于取消结果发出。</summary>
        UnknownFirst
    }

    /// <summary>
    /// 走到「B 的取消报 UNKNOWN、服务端已确认（<paramref name="resultAcknowledged"/> 为 false 时服务端收下不回确认）」：A 在 5 号仓
    /// 装好结算，B 在 1 号仓开门后操作员不动，按「取消装货」。<paramref name="holdForRecovery"/> 时再等车进入
    /// <c>RECOVERY_REQUIRED</c>（B 没有结果时靠重连握手，见类注释）；<paramref name="emptyAndShutAfter"/> 时操作员随后把 1 号仓清空
    /// 关好，这样挡路的只可能是向量，不是门检。
    /// </summary>
    private static async Task CancelBEndingUnknownAsync(
        Harness harness,
        FakeIoModuleClient io,
        LoadResultOfB loadResult,
        CancellationToken token,
        bool resultAcknowledged = true,
        bool holdForRecovery = true,
        bool emptyAndShutAfter = true)
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
        JsonElement[] resultsOfB =
        [
            .. ReceivedPayloads(harness, "OperationResult")
                .Where(result => result.GetProperty("slotOperationAttemptId").GetString() == AttemptB)
        ];
        if (loadResult == LoadResultOfB.UnknownFirst)
        {
            Assert.Equal("UNKNOWN", Assert.Single(resultsOfB).GetProperty("overallOutcome").GetString());
        }
        else
        {
            Assert.Empty(resultsOfB);
        }

        if (emptyAndShutAfter)
        {
            io.CloseDoor(0, cargo: false);
        }

        if (resultAcknowledged && holdForRecovery)
        {
            if (loadResult == LoadResultOfB.None)
            {
                await ReconnectHeldForRecoveryAsync(harness, token);
            }
            else
            {
                await harness.WaitUntilAsync(
                    () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
                    "the server to hold the vehicle for B's recovery",
                    token);
            }
        }

        Assert.Equal(2, io.UnlockCount);
    }

    /// <summary>
    /// 重连一次，等车进入 <c>RECOVERY_REQUIRED</c>：握手的 <c>RecoveryStateReport</c> 报出未结的尝试，服务端判
    /// <c>PENDING_FACT_RECONCILIATION_REQUIRED</c>。B 没有自己的结果时，这是车进入恢复的路；会话中途它进不去，是 control-server#697。
    /// </summary>
    private static async Task ReconnectHeldForRecoveryAsync(Harness harness, CancellationToken token)
    {
        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the reconnected vehicle held for B's unsettled attempt",
            token);
    }

    /// <summary>
    /// 为 B 按「故障交接」，等结果 <c>HANDED_OFF</c>、B 的尝试收尾、日志簿清干净。<paramref name="demandChoice"/> 是车上有几条已装需求时
    /// 屏上要选的那一条；只有 B 时为 null。
    /// </summary>
    private static async Task HandOffBAsync(
        Harness harness,
        FakeIoModuleClient io,
        string? demandChoice,
        CancellationToken token)
    {
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestFaultCargoHandoff,
            "the fault cargo handoff entry over B",
            token);
        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync(
            "现场确认故障仓货物需要交接处理。",
            demandChoice,
            token));
        JsonElement session = Assert.Single(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));
        Assert.Equal(DemandB, session.GetProperty("demandId").GetString());
        JsonElement handoff = await WaitForPayloadAsync(harness, "FaultCargoRecoveryResult", token);
        Assert.Equal("HANDED_OFF", handoff.GetProperty("overallOutcome").GetString());
        Assert.Equal(DemandB, handoff.GetProperty("demandId").GetString());
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { RecoveryVector: null, UnsettledSlotOperationAttemptId: null } state
                && state.ActiveUnlockSlots.Count == 0
                && state.TakenOverSlotOperationAttemptId is null,
            "the handoff to settle B's attempt and clear the journal",
            token);
        Assert.False(io.HasScheduledClose);
    }

    /// <summary>
    /// 向量已离开日志簿，取消交接过来的 1 号仓仍在活动开锁集里，被接管的尝试仍记着 B，B 的尝试仍未结——「可能已开过门」不丢。
    /// </summary>
    private static void AssertDoorInDoubtKept(WireToGateRecoveryState onFile)
    {
        Assert.Null(onFile.RecoveryVector);
        Assert.Equal(AttemptB, onFile.UnsettledSlotOperationAttemptId);
        Assert.Equal(AttemptB, onFile.TakenOverSlotOperationAttemptId);
        Assert.Equal([1], onFile.ActiveUnlockSlots);
    }

    /// <summary>入口关后到达的取消按键得到的那一条应答。</summary>
    private static void AssertConcludedCancellationAnswer(WireToGateOperatorEvent answer)
    {
        Assert.Contains("取消已有结论", answer.Message, StringComparison.Ordinal);
        Assert.Contains("联系维护人员", answer.Message, StringComparison.Ordinal);
        AssertNamesTheThreeExits(answer.Message);
    }

    /// <summary>
    /// 应答叫人走的出路：三个恢复入口都点名，不只是「补偿清空」——B 没有自己的结果时服务端拒绝补偿（control-server#697）。
    /// </summary>
    private static void AssertNamesTheThreeExits(string message)
    {
        Assert.Contains("补偿清空", message, StringComparison.Ordinal);
        Assert.Contains("故障交接", message, StringComparison.Ordinal);
        Assert.Contains("强制机械恢复", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="since"/> 之后发布的 <c>RECOVERY_BLOCKED</c> 事件。按键的应答就是这一种；同一窗口里还原流程可能并发发布别的
    /// 提示（PR #346 审查 H2），不算进来。
    /// </summary>
    private static WireToGateOperatorEvent[] RecoveryBlockedSince(Harness harness, int since) =>
        [.. harness.Events.Skip(since).Where(item => item.Kind == "RECOVERY_BLOCKED")];

    private static async Task<Harness> StartCancellationUnknownStopAsync(
        FakeIoModuleClient io,
        LoadResultOfB loadResult,
        CancellationToken token,
        Action<FakeControlServer>? alsoConfigure = null,
        string? journalPath = null,
        TimeSpan? messageTimeout = null)
    {
        Harness? started = null;
        Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                ConfigureCancellationStop(server);
                if (loadResult == LoadResultOfB.UnknownFirst)
                {
                    // The load sees its slot go unreadable and ends UNKNOWN on its own; the authorization waits until that
                    // result is on the vehicle's file, so it reaches the server first. Waited on the vehicle's journal, not
                    // on the server: this runs on the fake's receive loop, which would not read the result while it waits.
                    server.BeforeLoadCancellationAuthorization = () =>
                    {
                        io.SetUnreadable(0);
                        DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(10);
                        while (DateTimeOffset.UtcNow < until
                            && !(started is { } running
                                && ReadJournal(running, CancellationToken.None).PendingResults.Any(pending =>
                                    pending.MessageType == "OperationResult" && pending.BusinessId == AttemptB)))
                        {
                            Thread.Sleep(20);
                        }
                    };
                }

                alsoConfigure?.Invoke(server);
            },
            token,
            messageTimeout,
            journalPath,
            // With no load result: the slot goes unreadable only once the cancellation has put its vector on file, which
            // the business service does after aborting the load -- so the load cannot see it and end with a result.
            loadResult == LoadResultOfB.None ? inner => new UnreadableOnCancellationJournal(inner, io) : null);
        started = harness;
        return harness;
    }

    private static void ConfigureCancellationStop(FakeControlServer server)
    {
        server.RespondToLoadCancellationRequests = true;
        server.LoadCancellationAuthorizedSlots = [1];
        server.SendLoadCompensationCommandOnRequest = true;
        server.RecoverySlotOperationAttemptId = AttemptB;
        server.RecoveryVectorSlotOperationAttemptId = AttemptB;
    }

    /// <summary>
    /// Makes slot 1 unreadable once a load cancellation's vector is first put on file through the business service's
    /// overload: after the load was aborted, before the vector's precheck reads the slot.
    /// </summary>
    private sealed class UnreadableOnCancellationJournal(IWireToGateJournal inner, FakeIoModuleClient io) : IWireToGateJournal
    {
        private int _done;

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, cancellationToken);

        public async Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default)
        {
            WireToGateRecoveryState? written = await inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);
            if (written is { RecoveryVector.VectorType: WireToGateRecoveryVectorTypes.LoadCancellation }
                && Interlocked.Exchange(ref _done, 1) == 0)
            {
                io.SetUnreadable(0);
            }

            return written;
        }

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

        public Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default) =>
            inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);

        public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
            WireToGateDurableMessage expected,
            WireToGateDurableMessage replacement,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceOutgoingForReplayAsync(expected, replacement, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByMessageIdAsync(messageId, cancellationToken);

        public Task MarkOutgoingAcknowledgedAsync(
            string messageId,
            string acceptedContentSha256,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAcknowledgedAsync(messageId, acceptedContentSha256, cancellationToken);

        public Task<WireToGateDurableMessage> MarkOutgoingAbandonedAsync(
            string messageId,
            string contentSha256,
            string reasonCode,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAbandonedAsync(messageId, contentSha256, reasonCode, cancellationToken);

        public Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadUnacknowledgedOutgoingAsync(cancellationToken);

        public Task<IReadOnlyList<WireToGateAppliedJourneySnapshot>> ReadAppliedJourneySnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadAppliedJourneySnapshotsAsync(cancellationToken);

        public Task<WireToGateAppliedJourneySnapshot> SaveAppliedJourneySnapshotAsync(
            WireToGateAppliedJourneySnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            inner.SaveAppliedJourneySnapshotAsync(snapshot, cancellationToken);

        public Task<string> ComputeContentSha256Async(CancellationToken cancellationToken = default) =>
            inner.ComputeContentSha256Async(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
