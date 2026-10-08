using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 车上多条需求的交接对象：恢复进行中时锁定、未证明离车的需求留在列表、补偿自有目标、选中行消失不换行（8005-agv-onboard-hmi#209 审查）。
/// </summary>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 会话已经为 A 开着（审查 S1）：动作被服务端拒绝、向量放掉之后，屏上锁定在 A——列表收起、目标行写 A、选不了 B；业务层收到
    /// 选 B 的按下也拒绝，不悄悄改发 A。再按一次沿用这个会话，动作带 A/[1]，确认框复述 A 的子批与仓号。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AnOpenSessionLocksTheEntriesOnItsDemand()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                server.RecoverySessionSnapshotStatesAfterOpened = ["OPEN"];
                server.OpenSnapshotAllowedActions = ["FAULT_CARGO_HANDOFF", "FORCED_MECHANICAL_RECOVERY"];
                server.RecoveryActionRejectionReasonCode = "ACTION_NOT_ALLOWED_IN_STATE";
                server.RecoverySessionScopeByDemand = TwoLoadScopes();
            },
            token);
        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await LoadAndRecordAsync(harness, io, DemandB, AttemptB, slot: 5, token);
        await HoldTheSessionForRecoveryAsync(harness, token);
        await WaitForChoicesAsync(harness, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓"], token);

        harness.ViewModel.SelectedRecoveryDemandChoice =
            harness.ViewModel.RecoveryDemandChoices.Single(row => row.DemandId == DemandA);
        Assert.Equal("处理对象：子批 SUBLOT-A / 1号仓。\n\n", harness.ViewModel.RecoveryTargetConfirmationText);
        Assert.False(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));
        Assert.Single(ReceivedPayloads(harness, "RecoveryActionSubmitted"));
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is null,
            "the refused handoff's vector to be released",
            token);

        // The session the press opened is A's: the screen is locked on A and offers nothing to choose.
        await WaitForChoicesAsync(harness, [], token);
        Assert.False(harness.ViewModel.ShowsRecoveryDemandChoices);
        Assert.Null(harness.ViewModel.SelectedRecoveryDemandChoice);
        Assert.Equal("目标：子批 SUBLOT-A", harness.ViewModel.RecoveryFallbackTargetText);
        Assert.True(harness.ViewModel.CanPressFaultCargoHandoff);
        Assert.Equal("处理对象：子批 SUBLOT-A / 1号仓。\n\n", harness.ViewModel.RecoveryTargetConfirmationText);

        // A press that names B anyway is refused, not turned into A's.
        harness.Server.RecoveryActionRejectionReasonCode = null;
        Assert.False(await harness.Business.RequestFaultCargoHandoffAsync("现场确认故障仓货物需要交接处理。", DemandB, token));
        Assert.Single(ReceivedPayloads(harness, "RecoveryActionSubmitted"));

        Assert.True(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));
        Assert.Single(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));
        JsonElement[] actions = ReceivedPayloads(harness, "RecoveryActionSubmitted");
        Assert.Equal(2, actions.Length);
        Assert.Equal(DemandA, actions[1].GetProperty("demandId").GetString());
        Assert.Equal([1], actions[1].GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 已备向量随日志簿跨过重启（审查 S1）：交接 A 的动作被接受、命令还没到，车载端重启。重启后屏上同样锁定在 A，不列可选的需求。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task APreparedVectorLocksTheEntriesOnItsDemandAcrossARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness first = await StartTakeOverStopAsync(
            io,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySessionScopeByDemand = TwoLoadScopes();
            },
            token);
        await LoadAndRecordAsync(first, io, DemandA, AttemptA, slot: 1, token);
        await LoadAndRecordAsync(first, io, DemandB, AttemptB, slot: 5, token);
        await HoldTheSessionForRecoveryAsync(first, token);
        await WaitForChoicesAsync(first, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓"], token);
        first.Server.RecoverySlotOperationAttemptId = AttemptA;
        first.Server.RecoveryVectorSlotOperationAttemptId = AttemptA;
        first.ViewModel.SelectedRecoveryDemandChoice =
            first.ViewModel.RecoveryDemandChoices.Single(row => row.DemandId == DemandA);
        Assert.True(await first.ViewModel.RequestFaultCargoHandoffAsync(token));
        await first.WaitUntilAsync(
            () => ReadJournal(first, token).RecoveryVector?.DemandId == DemandA,
            "A's handoff to be prepared on the journal",
            token);

        string journalPath = first.JournalPath;
        await first.StopVehicleAsync();
        first.Server.SimulateOnboardProcessRestart();
        FakeIoModuleClient ioAfterRestart = new() { OperatorNeverActs = true };
        ioAfterRestart.SetCargoPresent(0, true);
        ioAfterRestart.SetCargoPresent(4, true);
        await using Harness afterRestart = await Harness.StartAgainstAsync(
            first.Server,
            token,
            journalPath,
            ioAfterRestart,
            recoveryOptions: TakeOverRecovery);
        await afterRestart.WaitUntilAsync(
            () => afterRestart.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && afterRestart.Business.CachedRecoveryStateForTest.RecoveryVector is not null,
            "the prepared vector back in the entry gates' cache after the restart",
            token);
        await WaitForChoicesAsync(afterRestart, [], token);
        Assert.Equal(DemandA, ReadJournal(afterRestart, token).RecoveryVector?.DemandId);
        Assert.Equal([DemandA, DemandB], ReadJournal(afterRestart, token).LoadsOnBoard?.Select(load => load.DemandId));
        Assert.Equal(DemandA, afterRestart.Business.RecoveryFallbackDemandId);
        Assert.Equal(DemandA, afterRestart.Business.RecoveryTargetFor(DemandB)?.DemandId);
        Assert.Empty(first.UiErrors);
        Assert.Empty(afterRestart.UiErrors);
    }

    /// <summary>
    /// 交接 A 以 UNKNOWN 结束、被忘掉（审查 S2）：A 的货没有证明离车，A 留在车上已装列表里，屏上照样两行。门反馈在活动开锁集写下之后
    /// 才读不到，所以结果必是 UNKNOWN，不是开锁前就失败的 FAILED。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AnUnknownHandoffOverASettledLoadKeepsItsDemandOnBoard()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptA;
                server.RecoveryVectorSlotOperationAttemptId = AttemptA;
                server.RecoverySessionScopeByDemand = TwoLoadScopes();
            },
            token);
        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await LoadAndRecordAsync(harness, io, DemandB, AttemptB, slot: 5, token);
        await HoldTheSessionForRecoveryAsync(harness, token);
        await WaitForChoicesAsync(harness, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓"], token);

        harness.ViewModel.SelectedRecoveryDemandChoice =
            harness.ViewModel.RecoveryDemandChoices.Single(row => row.DemandId == DemandA);
        Assert.True(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { RecoveryVector: not null, ActiveUnlockSlots: [1] },
            "the handoff to fence slot 1 in the active unlock set",
            token);
        io.SetUnreadable(0);
        Assert.Equal(
            "UNKNOWN",
            (await WaitForPayloadAsync(harness, "FaultCargoRecoveryResult", token)).GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is null,
            "the UNKNOWN handoff to be forgotten once acknowledged",
            token);

        Assert.Equal(
            [DemandA, DemandB],
            ReadJournal(harness, token).LoadsOnBoard?.Select(load => load.DemandId));
        await WaitForChoicesAsync(harness, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓"], token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 补偿有自己的目标（审查 S3）：两条已装需求时，交接与强制恢复由列表选，补偿仍针对最近一次完成的装货 B，屏上单独写「补偿目标：子批
    /// SUBLOT-B」，「目标：」那一行不出现。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task CompensationNamesItsOwnTargetAmongSeveralLoads()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(io, _ => { }, token);
        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await LoadAndRecordAsync(harness, io, DemandB, AttemptB, slot: 5, token);
        await HoldTheSessionForRecoveryAsync(harness, token);
        await WaitForChoicesAsync(harness, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓"], token);

        Assert.True(harness.ViewModel.CanRequestLoadCompensation);
        Assert.True(harness.ViewModel.HasCompensationTarget);
        Assert.Equal("补偿目标：子批 SUBLOT-B", harness.ViewModel.CompensationTargetText);
        Assert.False(harness.ViewModel.HasRecoveryFallbackTarget);
        Assert.Equal(DemandB, harness.Business.CompensationFallbackDemandId);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 后装的 B 已经卸掉、先装的 A 还在车上（审查 S3）：交接只剩 A，屏上「目标：子批 SUBLOT-A」；补偿仍针对最近一次完成的装货 B，单独写
    /// 「补偿目标：子批 SUBLOT-B」，不写成 A。卸货把 B 移出列表由单测钉住，这里按卸完之后的日志簿预置。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AfterTheLaterLoadIsUnloadedCompensationStillNamesIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WireToGateRecoveryOperationContext loadA = SettledLoadOf(DemandA) with { Slots = [1], ExpectedBasketCount = 1 };
        WireToGateRecoveryOperationContext loadB = SettledLoadOf(DemandB) with { Slots = [5], ExpectedBasketCount = 1 };
        string journalPath = await NewJournalSeededAsync(
            WireToGateRecoveryState.Empty.WithLoadOnBoard(loadA) with
            {
                ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ResultRecorded,
                LastCompletedLoadOperationContext = loadB
            },
            token);
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        io.SetCargoPresent(0, true);
        await using Harness harness = await StartTakeOverStopAsync(io, _ => { }, token, journalPath: journalPath);
        await HoldTheSessionForRecoveryAsync(harness, token);
        await harness.WaitUntilAsync(
            () =>
            {
                harness.ViewModel.RefreshWireToGateInputState();
                return harness.ViewModel.CanRequestFaultCargoHandoff && harness.ViewModel.CanRequestLoadCompensation;
            },
            "the handoff and compensation entries",
            token);

        Assert.False(harness.ViewModel.ShowsRecoveryDemandChoices);
        Assert.Equal("目标：子批 SUBLOT-A", harness.ViewModel.RecoveryFallbackTargetText);
        Assert.True(harness.ViewModel.HasRecoveryFallbackTarget);
        Assert.Equal("补偿目标：子批 SUBLOT-B", harness.ViewModel.CompensationTargetText);
        Assert.True(harness.ViewModel.HasCompensationTarget);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 单需求旅程的三段出口（复审 S-b，审查 S4 的裁定）：
    /// 1. 交接 A 得到 HANDED_OFF，服务端没有对上（NOT_RECONCILED，需求不终结）：A 标「已交接，待系统确认」，入口仍在，目标行带这个
    ///    后缀，再交接一次照常进行——这正是修前就有、本票不能拿走的出口；
    /// 2. 这次服务端对上了，A 终结：再按，会话请求照常发出，服务端以 RECOVERY_DEMAND_NOT_BLOCKED 拒绝（cs#505 A3），屏上显示拒绝、
    ///    不发动作，入口仍在；
    /// 3. 旅程收尾：列表清空，入口关闭。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task ASingleDemandHandoffTheServerDidNotReconcileCanBeHandedOffAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                server.EndingResultsReconcile = false;
                server.RecoverySessionScopeByDemand = new Dictionary<string, int[]>(StringComparer.Ordinal)
                {
                    [DemandA] = [1]
                };
            },
            token);
        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await HoldTheSessionForRecoveryAsync(harness, token);
        await harness.WaitUntilAsync(
            () =>
            {
                harness.ViewModel.RefreshWireToGateInputState();
                return harness.ViewModel.CanPressFaultCargoHandoff;
            },
            "the handoff entry over A",
            token);
        Assert.False(harness.ViewModel.ShowsRecoveryDemandChoices);
        Assert.Equal("目标：子批 SUBLOT-A", harness.ViewModel.RecoveryFallbackTargetText);

        // 1. Handed off, not reconciled: A stays, marked, and is handed off again.
        await HandOffAsync(harness, io, DemandA, AttemptA, slot: 1, sessions: 1, token);
        Assert.Equal(
            [(DemandA, true)],
            ReadJournal(harness, token).LoadsOnBoard?.Select(load => (load.DemandId, load.HandedOffAwaitingServer)));
        io.CloseDoor(0, cargo: true);
        await harness.WaitUntilAsync(
            () =>
            {
                harness.ViewModel.RefreshWireToGateInputState();
                return harness.ViewModel.CanPressFaultCargoHandoff
                    && harness.ViewModel.RecoveryFallbackTargetText == "目标：子批 SUBLOT-A（已交接，待系统确认）";
            },
            "the handoff entry over A again, the target line saying it awaits the server",
            token);
        Assert.Equal(
            "处理对象：子批 SUBLOT-A / 1号仓（已交接，待系统确认）。\n\n",
            harness.ViewModel.RecoveryTargetConfirmationText);
        harness.Server.EndingResultsReconcile = true;
        await HandOffAsync(harness, io, DemandA, AttemptA, slot: 1, sessions: 2, token);

        // 2. Reconciled this time: a press is answered by A3, nothing is commanded, the entry stays.
        int actionsBefore = ReceivedPayloads(harness, "RecoveryActionSubmitted").Length;
        Assert.True(harness.ViewModel.CanPressFaultCargoHandoff);
        Assert.False(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));
        Assert.Equal(3, ReceivedPayloads(harness, "ExceptionRecoverySessionRequested").Length);
        Assert.Equal(actionsBefore, ReceivedPayloads(harness, "RecoveryActionSubmitted").Length);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.Logs.Any(line => line.Message.Contains(NotBlockedRefusalText, StringComparison.Ordinal)),
            "the server's refusal to reach the operator",
            token);
        harness.ViewModel.RefreshWireToGateInputState();
        Assert.True(harness.ViewModel.CanPressFaultCargoHandoff);

        // 3. The journey closes: the list empties and the entry goes.
        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", ClosureBusinessState(revision: 9));
        await harness.WaitUntilAsync(
            () =>
            {
                harness.ViewModel.RefreshWireToGateInputState();
                return ReadJournal(harness, token).LoadsOnBoard is { Count: 0 }
                    && !harness.ViewModel.CanRequestFaultCargoHandoff;
            },
            "the journey's closure to empty the loads and take the entry away",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 强制机械恢复被确认 MECHANICALLY_ISOLATED 之后，A 标成「已交接，待系统确认」；再按故障交接，会话请求照常发出，服务端以
    /// RECOVERY_DEMAND_NOT_BLOCKED 拒绝（真服务端 A3 看的是需求已 Succeeded／Cancelled，强制取出同样终结需求），屏上显示拒绝、
    /// 不发动作（复审 S-c）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AfterAForcedRecoveryAHandoffPressIsAnsweredByTheServer()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server => server.RecoverySessionScopeByDemand = new Dictionary<string, int[]>(StringComparer.Ordinal)
            {
                [DemandA] = [1]
            },
            token);
        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await HoldTheSessionForRecoveryAsync(harness, token);
        await harness.WaitUntilAsync(
            () =>
            {
                harness.ViewModel.RefreshWireToGateInputState();
                return harness.ViewModel.CanPressForcedMechanicalRecovery;
            },
            "the forced mechanical recovery entry over A",
            token);
        Assert.True(await harness.ViewModel.RequestForcedMechanicalRecoveryAsync(token));
        await harness.WaitUntilAsync(
            () => harness.Business.CanConfirmForcedMechanicalRecovery,
            "the authorized forced recovery to wait for the operator's confirmation",
            token);
        await harness.Business.ConfirmForcedMechanicalRecoveryAsync(token);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { RecoveryVector: null, ForcedIsolation: not null },
            "the acknowledged isolation to settle the forced recovery",
            token);
        Assert.Equal(
            [(DemandA, true)],
            ReadJournal(harness, token).LoadsOnBoard?.Select(load => (load.DemandId, load.HandedOffAwaitingServer)));

        await harness.WaitUntilAsync(
            () =>
            {
                harness.ViewModel.RefreshWireToGateInputState();
                return harness.ViewModel.CanPressFaultCargoHandoff;
            },
            "the handoff entry over A, marked",
            token);
        int actionsBefore = ReceivedPayloads(harness, "RecoveryActionSubmitted").Length;
        Assert.False(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));
        JsonElement[] sessions = ReceivedPayloads(harness, "ExceptionRecoverySessionRequested");
        Assert.Equal(2, sessions.Length);
        Assert.Equal(DemandA, sessions[1].GetProperty("demandId").GetString());
        Assert.Equal(actionsBefore, ReceivedPayloads(harness, "RecoveryActionSubmitted").Length);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.Logs.Any(line => line.Message.Contains(NotBlockedRefusalText, StringComparison.Ordinal)),
            "the server's refusal to reach the operator",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 漏收了收尾快照，下一趟的计划先到（审查 S4 甲 a）：只带一个需求号的计划把锚记到两条装货上；锚换成另一条需求的新计划到达时，
    /// 两条都被剪掉，屏上提示说的是「上一趟行程留下的条目」，不说「本趟行程已结束」（复审小问题 1）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task APlanOfTheNextJourneyDropsWhatTheLastOneLeft()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string NextAnchor = "cccccccc-0000-4000-8000-00000000000c";
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(io, _ => { }, token);
        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await LoadAndRecordAsync(harness, io, DemandB, AttemptB, slot: 5, token);

        // The real server's plan: every leg carries the journey's anchor (JourneyPlanBuilder.Plan).
        await harness.Server.SendJourneySnapshotAsync(
            "UpcomingStopPlanSnapshot",
            Payloads.Plan(
                2,
                [
                    Payloads.Leg(1, "TO_PICKUP", "BUSINESS", DemandA, "ST-01", "ARRIVED"),
                    Payloads.Leg(2, "TO_DROPOFF", "BUSINESS", DemandA, "ST-GATE", "PLANNED")
                ]));
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LoadsOnBoard is { Count: 2 } loads
                && loads.All(load => load.JourneyAnchorDemandId == DemandA),
            "both loads stamped with the journey's anchor",
            token);

        await harness.Server.SendJourneySnapshotAsync(
            "UpcomingStopPlanSnapshot",
            Payloads.Plan(3, [Payloads.Leg(1, "TO_PICKUP", "BUSINESS", NextAnchor, "ST-02", "ACTIVE")]));
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LoadsOnBoard is { Count: 0 },
            "the next journey's plan to drop the loads the last one left",
            token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.Logs.Any(line => line.Message.Contains(
                "「车上待交接的需求」里有上一趟行程留下的条目，已按服务端当前的行程移除。",
                StringComparison.Ordinal)),
            "the operator told that the last journey's loads were dropped",
            token);
        Assert.DoesNotContain(
            harness.ViewModel.Logs,
            line => line.Message.Contains("本趟行程已结束", StringComparison.Ordinal));
        Assert.Empty(harness.UiErrors);
    }

    private static Dictionary<string, int[]> TwoLoadScopes() =>
        new(StringComparer.Ordinal)
        {
            [DemandA] = [1],
            [DemandB] = [5]
        };
}
