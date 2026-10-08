using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 一趟先后装了两条需求、途中停住要交接货物时，每条已装需求都要有交接对象（8005-agv-onboard-hmi#209）。
/// </summary>
/// <remarks>
/// 假服务端用 <c>ReadinessReasonOverride</c> 让会话停在 RECOVERY_REQUIRED，模拟真服务端 cs#345 的「等待货物交接」；会话范围按
/// <c>RecoverySessionScopeByDemand</c> 照真服务端 <c>ValidateSessionScopeAsync</c> 校验：仓位要等于这条需求最近一次操作的目标仓位。
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// A 装进 1 号仓、B 装进 5 号仓，两次装货都已确认在案；服务端判车上有货要交接。两条需求都要能交接，服务端都要能开出会话。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task EveryLoadedDemandOfAJourneyCanBeHandedOff()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptB;
                server.RecoveryVectorSlotOperationAttemptId = AttemptB;
                server.RecoverySessionScopeByDemand = new Dictionary<string, int[]>(StringComparer.Ordinal)
                {
                    [DemandA] = [1],
                    [DemandB] = [5]
                };
            },
            token);

        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await LoadAndRecordAsync(harness, io, DemandB, AttemptB, slot: 5, token);

        // The server's journey stops with both loads on board and hands the vehicle to an administrator (control-server#345).
        await HoldTheSessionForRecoveryAsync(harness, token);
        await WaitForChoicesAsync(harness, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓"], token);
        Assert.Equal([DemandA, DemandB], harness.ViewModel.RecoveryDemandChoices.Select(row => row.DemandId));
        Assert.True(harness.ViewModel.CanRequestFaultCargoHandoff);
        Assert.True(harness.ViewModel.ShowsRecoveryDemandChoices);
        Assert.Equal(string.Empty, harness.ViewModel.RecoveryFallbackTargetText);

        // Nothing chosen: the button stays but is greyed out, the hint says to choose, and a press asks nothing.
        Assert.False(harness.ViewModel.CanPressFaultCargoHandoff);
        Assert.True(harness.ViewModel.HasRecoveryDemandSelectionHint);
        Assert.Equal("请先在「车上待交接的需求」中选择要处理的一条。", harness.ViewModel.RecoveryDemandSelectionHintText);
        Assert.False(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));
        Assert.Empty(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));

        // B's handoff, as the operator chose it on screen; the server's scope check accepts it.
        await HandOffAsync(harness, io, DemandB, AttemptB, slot: 5, sessions: 1, token);

        // B is off the vehicle and A is still on it: A is the only subject now, and the screen asks for no choice.
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && harness.Business.CanRequestFaultCargoHandoff,
            "the handoff entry over A's cargo",
            token);
        await WaitForChoicesAsync(harness, [], token);
        Assert.False(harness.ViewModel.ShowsRecoveryDemandChoices);
        Assert.True(harness.ViewModel.CanPressFaultCargoHandoff);
        Assert.Equal("目标：子批 SUBLOT-A", harness.ViewModel.RecoveryFallbackTargetText);
        Assert.Equal([DemandA], ReadJournal(harness, token).LoadedDemandOperationContexts?.Select(load => load.DemandId));

        // B handed off cannot be asked about again: before this change a press after B's handoff asked for B over slot 5.
        Assert.False(await harness.Business.RequestFaultCargoHandoffAsync(
            "现场确认故障仓货物需要交接处理。", DemandB, token));
        Assert.Single(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));

        await HandOffAsync(harness, io, DemandA, AttemptA, slot: 1, sessions: 2, token);
        Assert.Empty(ReadJournal(harness, token).LoadedDemandOperationContexts ?? [null!]);
        await harness.WaitUntilAsync(
            () => !harness.Business.CanRequestFaultCargoHandoff,
            "the handoff entry to close with nothing left on board",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 会话已经为 A 开着：动作被服务端拒绝、向量放掉之后，入口仍对 A 开着（会话说的是哪条需求，主体就是哪条），再按一次沿用
    /// 这个会话，不再申请新会话。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AnOpenSessionKeepsTheEntryOnItsDemandAmongSeveralLoads()
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
                server.RecoverySessionScopeByDemand = new Dictionary<string, int[]>(StringComparer.Ordinal)
                {
                    [DemandA] = [1],
                    [DemandB] = [5]
                };
            },
            token);
        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await LoadAndRecordAsync(harness, io, DemandB, AttemptB, slot: 5, token);
        await HoldTheSessionForRecoveryAsync(harness, token);
        await WaitForChoicesAsync(harness, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓"], token);

        harness.ViewModel.SelectedRecoveryDemandChoice =
            harness.ViewModel.RecoveryDemandChoices.Single(row => row.DemandId == DemandA);
        Assert.False(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));
        Assert.Single(ReceivedPayloads(harness, "RecoveryActionSubmitted"));
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is null,
            "the refused handoff's vector to be released",
            token);

        // The session the press opened is A's, and the entry stays on A.
        await AssertWhileAsync(
            DisplaySettleWindow,
            () => Assert.True(harness.Business.CanRequestFaultCargoHandoff),
            token);
        harness.Server.RecoveryActionRejectionReasonCode = null;
        Assert.True(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));
        Assert.Single(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));
        JsonElement[] actions = ReceivedPayloads(harness, "RecoveryActionSubmitted");
        Assert.Equal(2, actions.Length);
        Assert.Equal(DemandA, actions[1].GetProperty("demandId").GetString());
        Assert.Equal([1], actions[1].GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 两条已装需求的上下文都在日志簿里：断线重连之后、车载端重启之后都还能选，重启后先交接较早装的 A，B 仍在。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task EveryLoadedDemandCanStillBeHandedOffAfterAReconnectAndARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness first = await StartTakeOverStopAsync(
            io,
            server => server.RecoverySessionScopeByDemand = new Dictionary<string, int[]>(StringComparer.Ordinal)
            {
                [DemandA] = [1],
                [DemandB] = [5]
            },
            token);
        await LoadAndRecordAsync(first, io, DemandA, AttemptA, slot: 1, token);
        await LoadAndRecordAsync(first, io, DemandB, AttemptB, slot: 5, token);
        await HoldTheSessionForRecoveryAsync(first, token);

        await first.Session.Client.DisconnectAsync();
        await first.Session.Client.ConnectAndRecoverAsync(token);
        await first.WaitUntilAsync(
            () => first.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && first.Business.CanRequestFaultCargoHandoff,
            "the handoff entry after the reconnect",
            token);
        await WaitForChoicesAsync(first, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓"], token);

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
                && afterRestart.Business.CanRequestFaultCargoHandoff
                && afterRestart.Business.RecoveryDemandChoices.Count == 2,
            "both loads offered after the restart",
            token);
        // Restored from the journal: the sublots come back with the worklist the server sends again on the handshake.
        await WaitForChoicesAsync(afterRestart, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓"], token);
        Assert.Equal([DemandA, DemandB], afterRestart.ViewModel.RecoveryDemandChoices.Select(row => row.DemandId));

        await HandOffAsync(afterRestart, ioAfterRestart, DemandA, AttemptA, slot: 1, sessions: 1, token);
        Assert.Equal([DemandB], ReadJournal(afterRestart, token).LoadedDemandOperationContexts?.Select(load => load.DemandId));
        Assert.Empty(first.UiErrors);
        Assert.Empty(afterRestart.UiErrors);
    }

    /// <summary>
    /// 与 hmi#278 的接管标记交错：B 的装货确认被扣住，交接接管 B、以 UNKNOWN 结束、被忘掉，标记记着 B；B 的确认随后到达，
    /// 只收结果自己那一份，B 进入已装列表、标记留着。再交接 B 一次，HANDED_OFF 结清 B：标记清掉、B 移出列表，A 还在、还能交接。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AForgottenHandoffMarkerAndTheLoadedListDoNotStepOnEachOther()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        TaskCompletionSource resultAckHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptB;
                server.RecoveryVectorSlotOperationAttemptId = AttemptB;
                server.RecoverySessionScopeByDemand = new Dictionary<string, int[]>(StringComparer.Ordinal)
                {
                    [DemandA] = [1],
                    [DemandB] = [5]
                };
            },
            token);
        using ReleaseOnExit releaseAck = new(() => resultAckHeld.TrySetResult());

        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        // B's COMPLETED result goes out and its acknowledgement is held.
        harness.Server.OperationResultAckHold = resultAckHeld.Task;
        int pulses = io.UnlockCount + 1;
        await SendSlotCommandAsync(harness, DemandB, AttemptB, [5]);
        await WaitForDoorOpenAsync(harness, io, AttemptB, pulses, token);
        io.CloseDoor(4, cargo: true);
        await harness.WaitUntilAsync(
            () => harness.Server.OperationResultsHeld == 1,
            "B's COMPLETED result to be held unacknowledged",
            token);
        await HoldTheSessionForRecoveryAsync(harness, token);

        // B's attempt is unsettled, so B is the subject on its own: nothing to choose.
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestFaultCargoHandoff,
            "the handoff entry over B",
            token);
        Assert.Empty(harness.Business.RecoveryDemandChoices);
        int unlocksBefore = io.UnlockCount;
        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync("现场确认故障仓货物需要交接处理。", token));
        await harness.WaitUntilAsync(
            () => io.UnlockCount > unlocksBefore || ReceivedPayloads(harness, "FaultCargoRecoveryResult").Length > 0,
            "the handoff to open slot 5",
            token);
        io.SetUnreadable(4);
        Assert.Equal(
            "UNKNOWN",
            (await WaitForPayloadAsync(harness, "FaultCargoRecoveryResult", token)).GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is null,
            "the UNKNOWN handoff to be forgotten once acknowledged",
            token);
        Assert.Equal(AttemptB, ReadJournal(harness, token).TakenOverSlotOperationAttemptId);

        // B's acknowledgement arrives: only the result's share is recorded, and B is on board.
        resultAckHeld.SetResult();
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LoadedDemandOperationContexts?.Count == 2,
            "B's late acknowledgement to put B on the loaded list",
            token);
        WireToGateRecoveryState acknowledged = ReadJournal(harness, token);
        Assert.Equal(AttemptB, acknowledged.TakenOverSlotOperationAttemptId);
        Assert.Equal(AttemptB, acknowledged.UnsettledSlotOperationAttemptId);
        Assert.Equal([5], acknowledged.ActiveUnlockSlots);

        // Door 5 shut empty, readable again; the handoff over B again settles B, the marker and B's place on the list.
        io.CloseDoor(4, cargo: false);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestFaultCargoHandoff,
            "the handoff entry over B again",
            token);
        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync("现场确认故障仓货物需要交接处理。", token));
        await harness.WaitUntilAsync(
            () => ReceivedPayloads(harness, "FaultCargoRecoveryResult").Length == 2
                && ReadJournal(harness, token) is { RecoveryVector: null, UnsettledSlotOperationAttemptId: null },
            "the second handoff over B to settle it",
            token);
        Assert.Equal(
            "HANDED_OFF",
            ReceivedPayloads(harness, "FaultCargoRecoveryResult")[1].GetProperty("overallOutcome").GetString());
        WireToGateRecoveryState settled = ReadJournal(harness, token);
        Assert.Null(settled.TakenOverSlotOperationAttemptId);
        Assert.Equal([DemandA], settled.LoadedDemandOperationContexts?.Select(load => load.DemandId));
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestFaultCargoHandoff && harness.Business.RecoveryFallbackDemandId == DemandA,
            "the handoff entry over A",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 强制机械恢复与交接用同一个选择：两条已装需求时不选不发，选了 A 就为 A 的 1 号仓开会话。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedMechanicalRecoveryIsAskedForTheChosenLoad()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySessionScopeByDemand = new Dictionary<string, int[]>(StringComparer.Ordinal)
                {
                    [DemandA] = [1],
                    [DemandB] = [5]
                };
            },
            token);
        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await LoadAndRecordAsync(harness, io, DemandB, AttemptB, slot: 5, token);
        await HoldTheSessionForRecoveryAsync(harness, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestForcedMechanicalRecovery,
            "the forced mechanical recovery entry",
            token);

        await WaitForChoicesAsync(harness, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓"], token);
        Assert.True(harness.ViewModel.CanRequestForcedMechanicalRecovery);
        Assert.False(harness.ViewModel.CanPressForcedMechanicalRecovery);
        Assert.False(await harness.ViewModel.RequestForcedMechanicalRecoveryAsync(token));
        Assert.Empty(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));

        harness.ViewModel.SelectedRecoveryDemandChoice =
            harness.ViewModel.RecoveryDemandChoices.Single(row => row.DemandId == DemandA);
        Assert.True(harness.ViewModel.CanPressForcedMechanicalRecovery);
        Assert.True(await harness.ViewModel.RequestForcedMechanicalRecoveryAsync(token));
        JsonElement session = Assert.Single(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));
        Assert.Equal(DemandA, session.GetProperty("demandId").GetString());
        Assert.Equal([1], session.GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// One handoff over <paramref name="demandId"/>'s load in <paramref name="slot"/>, pressed on screen -- the row chosen
    /// first when the screen lists several loads, nothing chosen when it lists none: the session
    /// request names that demand and slot, the door opens, the operator empties and shuts it, and the server reconciles it
    /// HANDED_OFF.
    /// </summary>
    private static async Task HandOffAsync(
        Harness harness,
        FakeIoModuleClient io,
        string demandId,
        string attemptId,
        int slot,
        int sessions,
        CancellationToken token)
    {
        harness.Server.RecoverySlotOperationAttemptId = attemptId;
        harness.Server.RecoveryVectorSlotOperationAttemptId = attemptId;
        int unlocksBefore = io.UnlockCount;
        harness.ViewModel.RefreshWireToGateInputState();
        if (harness.ViewModel.ShowsRecoveryDemandChoices)
        {
            harness.ViewModel.SelectedRecoveryDemandChoice =
                harness.ViewModel.RecoveryDemandChoices.Single(row => row.DemandId == demandId);
        }

        Assert.True(harness.ViewModel.CanPressFaultCargoHandoff);
        Assert.True(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));
        JsonElement[] requested = ReceivedPayloads(harness, "ExceptionRecoverySessionRequested");
        Assert.Equal(sessions, requested.Length);
        Assert.Equal(demandId, requested[^1].GetProperty("demandId").GetString());
        Assert.Equal([slot], requested[^1].GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()));
        await harness.WaitUntilAsync(
            () => io.UnlockCount > unlocksBefore || ReceivedPayloads(harness, "FaultCargoRecoveryResult").Length >= sessions,
            $"the handoff to open slot {slot}",
            token);
        io.CloseDoor(slot - 1, cargo: false);
        await harness.WaitUntilAsync(
            () => ReceivedPayloads(harness, "FaultCargoRecoveryResult").Length == sessions
                && ReadJournal(harness, token).RecoveryVector is null,
            $"the handoff over {demandId} to be settled",
            token);
        Assert.Equal(
            "HANDED_OFF",
            ReceivedPayloads(harness, "FaultCargoRecoveryResult")[^1].GetProperty("overallOutcome").GetString());
    }

    /// <summary>The rows of the loads-on-board choice as the screen shows them, refreshed the way the dispatcher would.</summary>
    private static Task WaitForChoicesAsync(Harness harness, string[] texts, CancellationToken token) =>
        harness.WaitUntilAsync(
            () =>
            {
                harness.ViewModel.RefreshWireToGateInputState();
                return harness.ViewModel.RecoveryDemandChoices.Select(row => row.Text).SequenceEqual(texts);
            },
            $"the loads on board listed as [{string.Join("; ", texts)}]",
            token);

    private static async Task LoadAndRecordAsync(
        Harness harness,
        FakeIoModuleClient io,
        string demandId,
        string attemptId,
        int slot,
        CancellationToken token)
    {
        int pulses = io.UnlockCount + 1;
        await SendSlotCommandAsync(harness, demandId, attemptId, [slot]);
        await WaitForDoorOpenAsync(harness, io, attemptId, pulses, token);
        io.CloseDoor(slot - 1, cargo: true);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { UnsettledSlotOperationAttemptId: null } state
                && state.LastCompletedLoadOperationContext?.SlotOperationAttemptId == attemptId,
            $"the load of {demandId} to be recorded",
            token);
    }
}
