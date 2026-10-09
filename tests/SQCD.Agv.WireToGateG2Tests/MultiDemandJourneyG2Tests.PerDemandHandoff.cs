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
        Assert.Equal([DemandA, DemandB], harness.OnUi(() => harness.ViewModel.RecoveryDemandChoices.Select(row => row.DemandId).ToArray()));
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

        // B was handed off, and only the server knows whether that reconciled: B stays, marked, and A is still to hand off
        // (review S4).
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && harness.Business.CanRequestFaultCargoHandoff,
            "the handoff entry after B's handoff",
            token);
        await WaitForChoicesAsync(harness, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓（已交接，待系统确认）"], token);
        Assert.Equal(
            [(DemandA, false), (DemandB, true)],
            ReadJournal(harness, token).LoadsOnBoard?.Select(load => (load.DemandId, load.HandedOffAwaitingServer)));

        // Pressed for B again, the session request goes out and the server answers it: it ended B, so it refuses the session
        // (control-server#505, A3). The refusal is shown, nothing is commanded, and B is not taken off on that answer alone.
        harness.OnUi(() => harness.ViewModel.SelectedRecoveryDemandChoice =
            harness.ViewModel.RecoveryDemandChoices.Single(row => row.DemandId == DemandB));
        Assert.True(harness.ViewModel.CanPressFaultCargoHandoff);
        Assert.Equal(
            "处理对象：子批 SUBLOT-B / 5号仓（已交接，待系统确认）。\n\n",
            harness.ViewModel.RecoveryTargetConfirmationText);
        int actionsBefore = ReceivedPayloads(harness, "RecoveryActionSubmitted").Length;
        Assert.False(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));
        JsonElement[] sessions = ReceivedPayloads(harness, "ExceptionRecoverySessionRequested");
        Assert.Equal(2, sessions.Length);
        Assert.Equal(DemandB, sessions[1].GetProperty("demandId").GetString());
        Assert.Equal(actionsBefore, ReceivedPayloads(harness, "RecoveryActionSubmitted").Length);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.Logs.Any(line => line.Message.Contains(
                NotBlockedRefusalText,
                StringComparison.Ordinal)),
            "the server's refusal to reach the operator",
            token);
        Assert.Equal([DemandA, DemandB], ReadJournal(harness, token).LoadsOnBoard?.Select(load => load.DemandId));

        await HandOffAsync(harness, io, DemandA, AttemptA, slot: 1, sessions: 3, token);
        Assert.Equal(
            [(DemandA, true), (DemandB, true)],
            ReadJournal(harness, token).LoadsOnBoard?.Select(load => (load.DemandId, load.HandedOffAwaitingServer)));

        // The journey closes: the server's closure business state has no purpose, and the list empties with it.
        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", ClosureBusinessState(revision: 9));
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LoadsOnBoard is { Count: 0 },
            "the journey's closure to empty the loads on board",
            token);
        await WaitForChoicesAsync(harness, [], token);
        Assert.False(harness.Business.CanRequestFaultCargoHandoff);
        Assert.Contains(
            harness.ViewModel.Logs,
            line => line.Message.Contains("本趟行程已结束，「车上待交接的需求」已清空。", StringComparison.Ordinal));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>What the operator reads when the server refuses a session over a demand it does not hold for recovery.</summary>
    private const string NotBlockedRefusalText =
        "服务端当前没有因这条需求停住行程（行程不在待恢复状态，或这条需求已经结束），不能为它开处置会话。（RECOVERY_DEMAND_NOT_BLOCKED）";

    /// <summary>
    /// The business state <c>JourneyClosure</c> stages when a journey ends: no purpose, no loading phase (control-server
    /// <c>JourneyClosure.StageAsync</c>).
    /// </summary>
    private static object ClosureBusinessState(long revision) =>
        new
        {
            vehicleBusinessStateRevision = revision,
            readiness = "READY",
            activePurpose = (string?)null,
            manualChargingHold = false,
            batteryState = "SUFFICIENT",
            chargingCycleState = "NOT_CHARGING",
            loadingPhase = (object?)null,
            blockingFacts = Array.Empty<object>(),
            observedAt = DateTimeOffset.UtcNow
        };

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
        Assert.Equal([DemandA, DemandB], afterRestart.OnUi(() => afterRestart.ViewModel.RecoveryDemandChoices.Select(row => row.DemandId).ToArray()));

        await HandOffAsync(afterRestart, ioAfterRestart, DemandA, AttemptA, slot: 1, sessions: 1, token);
        Assert.Equal(
            [(DemandA, true), (DemandB, false)],
            ReadJournal(afterRestart, token).LoadsOnBoard?.Select(load => (load.DemandId, load.HandedOffAwaitingServer)));
        Assert.Empty(first.UiErrors);
        Assert.Empty(afterRestart.UiErrors);
    }

    /// <summary>
    /// 与 hmi#278 的接管标记交错：B 的装货确认被扣住，交接接管 B、以 UNKNOWN 结束、被忘掉，标记记着 B；B 的确认随后到达，
    /// 只收结果自己那一份，B 进入已装列表、标记留着。再交接 B 一次，HANDED_OFF 结清 B：标记清掉，B 标成「已交接，待系统确认」
    /// 留在列表里，A 还在、还能交接。
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
            () => ReadJournal(harness, token).LoadsOnBoard?.Count == 2,
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
        Assert.Equal(
            [(DemandA, false), (DemandB, true)],
            settled.LoadsOnBoard?.Select(load => (load.DemandId, load.HandedOffAwaitingServer)));
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestFaultCargoHandoff
                && harness.Business.RecoveryDemandChoices.Select(load => load.DemandId).SequenceEqual([DemandA, DemandB]),
            "the handoff entry over A, with B marked",
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

        harness.OnUi(() => harness.ViewModel.SelectedRecoveryDemandChoice =
            harness.ViewModel.RecoveryDemandChoices.Single(row => row.DemandId == DemandA));
        Assert.True(harness.ViewModel.CanPressForcedMechanicalRecovery);
        Assert.True(await harness.ViewModel.RequestForcedMechanicalRecoveryAsync(token));
        JsonElement session = Assert.Single(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));
        Assert.Equal(DemandA, session.GetProperty("demandId").GetString());
        Assert.Equal([1], session.GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()));

        // The person confirms the isolation and the manual extraction; the server acknowledges MECHANICALLY_ISOLATED, which
        // asks to end A's demand as a handoff does: A is marked handed off, awaiting the server, and B stays as it was
        // (review RC).
        await harness.WaitUntilAsync(
            () => harness.Business.CanConfirmForcedMechanicalRecovery,
            "the authorized forced recovery to wait for the operator's confirmation",
            token);
        await ConfirmForcedRecoveryWithHandoffAsync(harness, "SUBLOT-A", token);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { RecoveryVector: null, ForcedIsolation: not null },
            "the acknowledged isolation to settle the forced recovery",
            token);
        Assert.Equal(
            [(DemandA, true), (DemandB, false)],
            ReadJournal(harness, token).LoadsOnBoard?.Select(load => (load.DemandId, load.HandedOffAwaitingServer)));

        // What went out on the wire, not only what the journal says (8005-agv-onboard-hmi#282 review S2): the helper's first
        // press may return false for a reason it does not tell apart, so the result itself is checked -- A's demand, the
        // isolation, the handoff record pressed and A's slot.
        JsonElement result = Assert.Single(ReceivedPayloads(harness, "ForcedMechanicalRecoveryResult"));
        Assert.Equal(DemandA, result.GetProperty("demandId").GetString());
        Assert.Equal("MECHANICALLY_ISOLATED", result.GetProperty("outcome").GetString());
        JsonElement handoff = result.GetProperty("cargoHandoff");
        Assert.Equal("SUBLOT-A", handoff.GetProperty("sublot").GetString());
        Assert.Equal(ForcedHandoffReceiver, handoff.GetProperty("receiverName").GetString());
        Assert.Equal([1], result.GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()));
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
        // Counted, not taken from the session count: a session the server refused leaves no result.
        int resultsBefore = ReceivedPayloads(harness, "FaultCargoRecoveryResult").Length;
        harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
        if (harness.ViewModel.ShowsRecoveryDemandChoices)
        {
            harness.OnUi(() => harness.ViewModel.SelectedRecoveryDemandChoice =
                harness.ViewModel.RecoveryDemandChoices.Single(row => row.DemandId == demandId));
        }

        Assert.True(harness.ViewModel.CanPressFaultCargoHandoff);
        // Emptied and shut by the fake when the executor waits for it (see LoadAndRecordAsync).
        io.CloseDoorAfterNextUnlock(slot - 1, cargo: false);
        Assert.True(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));
        JsonElement[] requested = ReceivedPayloads(harness, "ExceptionRecoverySessionRequested");
        Assert.Equal(sessions, requested.Length);
        Assert.Equal(demandId, requested[^1].GetProperty("demandId").GetString());
        Assert.Equal([slot], requested[^1].GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()));
        await harness.WaitUntilAsync(
            () => ReceivedPayloads(harness, "FaultCargoRecoveryResult").Length == resultsBefore + 1
                && ReadJournal(harness, token).RecoveryVector is null,
            $"the handoff over {demandId} to be settled",
            token);
        Assert.Equal(
            "HANDED_OFF",
            ReceivedPayloads(harness, "FaultCargoRecoveryResult")[^1].GetProperty("overallOutcome").GetString());
        Assert.Equal(unlocksBefore + 1, io.UnlockCount);
        Assert.False(io.HasScheduledClose);
    }

    /// <summary>
    /// Confirms the authorized forced recovery the way a press on this line must: with the cargo handoff record a forced
    /// recovery on a demand carries since protocol 3.0.0 (b8-14, 8005-agv-onboard-hmi#216). Without it the press is refused
    /// with <c>FORCED_RECOVERY_HANDOFF_RECORD_REQUIRED</c> before any result is written (v3 sync, 8005-agv-onboard-hmi#282).
    /// When the current stop's worklist does not name the demand, the first press only warns that the SUBLOT cannot be
    /// checked, and the operator's second press sends it.
    /// </summary>
    private static async Task ConfirmForcedRecoveryWithHandoffAsync(
        Harness harness,
        string sublot,
        CancellationToken token)
    {
        if (!await harness.Business.ConfirmForcedMechanicalRecoveryAsync(sublot, ForcedHandoffReceiver, token))
        {
            Assert.True(await harness.Business.ConfirmForcedMechanicalRecoveryAsync(sublot, ForcedHandoffReceiver, token));
        }
    }

    /// <summary>The receiver <see cref="ConfirmForcedRecoveryWithHandoffAsync"/> names in the cargo handoff record.</summary>
    private const string ForcedHandoffReceiver = "现场接收人";

    /// <summary>The rows of the loads-on-board choice as the screen shows them, refreshed the way the dispatcher would.</summary>
    private static Task WaitForChoicesAsync(Harness harness, string[] texts, CancellationToken token) =>
        harness.WaitUntilAsync(
            () => harness.OnUi(() =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                // A list on screen is a list for the handoff entry: waited on together, since the list is read from the
                // journal and the entry from the session, and either may be the later one (re-review S-a).
                return harness.ViewModel.RecoveryDemandChoices.Select(row => row.Text).SequenceEqual(texts)
                    && (texts.Length == 0 || harness.ViewModel.CanRequestFaultCargoHandoff);
            }),
            $"the loads on board listed as [{string.Join("; ", texts)}]"
            + (texts.Length == 0 ? string.Empty : ", with the handoff entry on screen"),
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
        // Closed by the fake when the executor waits for it, not by this thread after it sees the pulse: a late wake-up
        // here would run the 2-second operation timeout out.
        io.CloseDoorAfterNextUnlock(slot - 1, cargo: true);
        await SendSlotCommandAsync(harness, demandId, attemptId, [slot]);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { UnsettledSlotOperationAttemptId: null } state
                && state.LastCompletedLoadOperationContext?.SlotOperationAttemptId == attemptId,
            $"the load of {demandId} to be recorded",
            token);
        Assert.Equal(pulses, io.UnlockCount);
        Assert.False(io.HasScheduledClose);
    }
}
