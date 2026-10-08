using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 恢复向量接管了一次尚未结清的仓位操作，以非完成结果结束、被忘掉之后，这次操作迟到的 COMPLETED 确认不再把它结清、把向量
/// 留下的存疑仓门从活动开锁集里抹掉（8005-agv-onboard-hmi#278）。
/// </summary>
/// <remarks>
/// <para>
/// 向量被忘掉时（<c>ForgetSettledVector</c>），尝试、上下文与活动开锁集都留着，可日志簿里原先没有任何东西记得「有向量接管过
/// 这次尝试」：向量结果的键是它自己的 actionId。之后这次装货的确认不管从哪条路到——向量还在时（hmi#259 那一支）、向量刚被
/// 忘掉之后、还是重连握手补发——都按装货结清，活动开锁集写成空。现在忘掉向量的同一次写入记下
/// <c>TakenOverSlotOperationAttemptId</c>，记录结果与中断结算都认它。
/// </para>
/// <para>
/// 假服务端用 <c>ReadinessReasonOverride</c> 让会话停在 RECOVERY_REQUIRED，模拟真服务端 cs#345 的「等待货物交接」
/// （<c>WireToGateStore.DecideReadinessAsync</c> 的 <c>cargoHandoffAwaited</c>）：交接失败时那条重建记录留着，会话照旧需恢复，
/// 再交接一次就是出口。旅程因别的原因被阻断时，A 被服务端收下之后会话会判 READY，车上开不了恢复会话——那一种修前修后都
/// 没有车上的出口，记在 control-server#485。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 装货 A 的 COMPLETED 结果发出、确认被扣住；操作员按故障交接，交接向量接管 A、开了 1 号门；1 号门的锁反馈随后读不到，
    /// 交接以 UNKNOWN 结束、被忘掉。A 的确认在交接进行中到，或在交接结束之后才到，两种都不得把 A 结清、抹掉 1 号门，重连
    /// 之后也一样。出口实测：关好 1 号门再交接一次，HANDED_OFF 结清 A、标记清掉，下一单照常执行。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AnUnknownHandoffOverALoadWhoseAckCameLateKeepsItsDoorAcrossAReconnect(bool ackAfterHandoffEnded)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        TaskCompletionSource resultAckHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server => server.OperationResultAckHold = resultAckHeld.Task,
            token);
        using ReleaseOnExit releaseAck = new(() => resultAckHeld.TrySetResult());

        // Load A runs to COMPLETED with both slots loaded; its result is taken and the ack held.
        await LoadBothSlotsOfAAsync(harness, io, token);
        await harness.WaitUntilAsync(
            () => harness.Server.OperationResultsHeld == 1,
            "the load's COMPLETED result to be held unacknowledged",
            token);
        Assert.Equal("COMPLETED", Assert.Single(ReceivedPayloads(harness, "OperationResult"))
            .GetProperty("overallOutcome").GetString());

        // The server's journey stops with cargo on board and hands the vehicle to an administrator (control-server#345).
        await HoldTheSessionForRecoveryAsync(harness, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestFaultCargoHandoff,
            "the fault cargo handoff entry over load A",
            token);

        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync("现场确认故障仓货物需要交接处理。", token));
        await harness.WaitUntilAsync(
            () => io.UnlockCount == 3 || ReceivedPayloads(harness, "FaultCargoRecoveryResult").Length > 0,
            "the handoff to open slot 1",
            token);
        WireToGateRecoveryState taken = ReadJournal(harness, token);
        Assert.Equal(WireToGateRecoveryVectorTypes.FaultCargoHandoff, taken.RecoveryVector?.VectorType);
        Assert.Equal(AttemptA, taken.RecoveryVector?.SlotOperationAttemptId);
        Assert.Equal(AttemptA, taken.UnsettledSlotOperationAttemptId);
        Assert.Equal([1], taken.ActiveUnlockSlots);

        if (!ackAfterHandoffEnded)
        {
            // A's ack comes now, over the handoff that took its attempt over: the vector stays (onboard-hmi#259).
            await ReleaseLoadAckAsync(harness, resultAckHeld, token);
            Assert.Equal(
                WireToGateRecoveryVectorTypes.FaultCargoHandoff,
                ReadJournal(harness, token).RecoveryVector?.VectorType);
        }

        // Door 1's lock feedback goes unreadable while it stands open: the handoff ends UNKNOWN over it.
        io.SetUnreadable(0);
        JsonElement handoffResult = await WaitForPayloadAsync(harness, "FaultCargoRecoveryResult", token);
        Assert.Equal("UNKNOWN", handoffResult.GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is null,
            "the UNKNOWN handoff to be forgotten once acknowledged",
            token);
        AssertAttemptAndDoorKept(ReadJournal(harness, token));

        if (ackAfterHandoffEnded)
        {
            // A's ack comes only now, the handoff over and forgotten: only the marker says a vector held the attempt.
            await ReleaseLoadAckAsync(harness, resultAckHeld, token);
            AssertAttemptAndDoorKept(ReadJournal(harness, token));
        }

        int restoresBefore = RestoredEntryLines(harness);
        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await harness.WaitUntilAsync(
            () => RestoredEntryLines(harness) > restoresBefore
                || ReadJournal(harness, token).UnsettledSlotOperationAttemptId is null,
            "the restore to owe the recovery entry, or to settle the attempt",
            token);
        AssertAttemptAndDoorKept(ReadJournal(harness, token));
        Assert.Single(ReceivedPayloads(harness, "OperationResult"));

        await HandOffAgainAndRunTheNextDemandAsync(harness, io, token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 补偿也写标记：装货 A 自己以 UNKNOWN 结束（2 号门锁反馈读不到），尝试未结；关好 2 号门后补偿接管 A，开 1 号门、
    /// 读不到，以 UNKNOWN 结束、被忘掉。标记记着 A，尝试与 1 号门留着（8005-agv-onboard-hmi#278）。不依赖确认迟到那个
    /// 窄窗口：补偿持有日志簿里的未结尝试，这本身就是写标记的条件。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AForgottenCompensationOverAnUnsettledLoadLeavesItsAttemptMarked()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server => server.SendLoadCompensationCommandOnRequest = true,
            token);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 2, token);
        // Slot 2's lock feedback goes unreadable while it stands open: the load ends UNKNOWN, its attempt unsettled.
        io.SetUnreadable(1);
        JsonElement loadResult = await WaitForOperationResultAsync(harness, AttemptA, token);
        Assert.Equal("UNKNOWN", loadResult.GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the server to hold the vehicle for the load's recovery",
            token);
        // The operator shuts slot 2 with its basket in, so the compensation's door check passes.
        io.CloseDoor(1, cargo: true);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCompensation,
            "the compensation entry over load A",
            token);

        int unlocksBefore = io.UnlockCount;
        Assert.True(await harness.Business.RequestLoadCompensationAsync("现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await harness.WaitUntilAsync(
            () => io.UnlockCount > unlocksBefore || ReceivedPayloads(harness, "LoadCompensationResult").Length > 0,
            "the compensation to open slot 1",
            token);
        WireToGateRecoveryState taken = ReadJournal(harness, token);
        Assert.Equal(WireToGateRecoveryVectorTypes.LoadCompensation, taken.RecoveryVector?.VectorType);
        Assert.Equal(AttemptA, taken.RecoveryVector?.SlotOperationAttemptId);
        Assert.Equal(AttemptA, taken.UnsettledSlotOperationAttemptId);

        io.SetUnreadable(0);
        JsonElement compensation = await WaitForPayloadAsync(harness, "LoadCompensationResult", token);
        Assert.Equal("UNKNOWN", compensation.GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is null,
            "the UNKNOWN compensation to be forgotten once acknowledged",
            token);

        WireToGateRecoveryState forgotten = ReadJournal(harness, token);
        Assert.Equal(AttemptA, forgotten.TakenOverSlotOperationAttemptId);
        Assert.Equal(AttemptA, forgotten.UnsettledSlotOperationAttemptId);
        Assert.Equal([1], forgotten.ActiveUnlockSlots);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 护栏，修前修后都绿：补偿接管的是一次已经记录完成的装货（服务端收下 COMPLETED 却判需恢复），以 UNKNOWN 结束、被忘掉。
    /// 记录早已清掉操作上下文，重连后的还原在 <c>context is null</c> 处返回，尝试与 1 号门都留着。票面说补偿那一格走不到，
    /// 靠的就是这一条（8005-agv-onboard-hmi#278）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnUnknownCompensationOverARecordedLoadKeepsItsAttemptAndDoorAcrossAReconnect()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                server.SendLoadCompensationCommandOnRequest = true;
                server.SendRecoveryRequiredReadinessAfterOperationResultAck = true;
            },
            token);

        await LoadBothSlotsOfAAsync(harness, io, token);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { UnsettledSlotOperationAttemptId: null } state
                && state.LastCompletedLoadOperationContext?.SlotOperationAttemptId == AttemptA
                && harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the load's COMPLETED result recorded, and the server holding it for recovery",
            token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCompensation,
            "the compensation entry over the recorded load",
            token);

        int unlocksBefore = io.UnlockCount;
        Assert.True(await harness.Business.RequestLoadCompensationAsync("现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await harness.WaitUntilAsync(
            () => io.UnlockCount > unlocksBefore || ReceivedPayloads(harness, "LoadCompensationResult").Length > 0,
            "the compensation to open slot 1",
            token);
        io.SetUnreadable(0);
        JsonElement compensation = await WaitForPayloadAsync(harness, "LoadCompensationResult", token);
        Assert.Equal("UNKNOWN", compensation.GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is null,
            "the UNKNOWN compensation to be forgotten once acknowledged",
            token);
        WireToGateRecoveryState forgotten = ReadJournal(harness, token);
        Assert.Null(forgotten.OperationContext);
        Assert.Equal(AttemptA, forgotten.UnsettledSlotOperationAttemptId);
        Assert.Equal([1], forgotten.ActiveUnlockSlots);

        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        // The restore runs on the readiness, and the entry it leaves offered is read from the state it refreshed.
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && harness.Business.CanRequestLoadCompensation,
            "the compensation entry offered again after the reconnect",
            token);
        WireToGateRecoveryState after = ReadJournal(harness, token);
        Assert.Equal(AttemptA, after.UnsettledSlotOperationAttemptId);
        Assert.Equal([1], after.ActiveUnlockSlots);
        Assert.Single(ReceivedPayloads(harness, "OperationResult"));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>The attempt the forgotten handoff held, its context and door 1 all still on file, and the marker on A.</summary>
    private static void AssertAttemptAndDoorKept(WireToGateRecoveryState journal)
    {
        Assert.Null(journal.RecoveryVector);
        Assert.Equal(AttemptA, journal.UnsettledSlotOperationAttemptId);
        Assert.Equal(AttemptA, journal.OperationContext?.SlotOperationAttemptId);
        Assert.Equal([1], journal.ActiveUnlockSlots);
        Assert.NotEqual(WireToGateRecoveryCheckpoint.ResultRecorded, journal.ProvenRecoveryCheckpoint);
        Assert.Equal(AttemptA, journal.TakenOverSlotOperationAttemptId);
    }

    /// <summary>
    /// The way out of the owed entry: the operator empties and shuts door 1, hands the cargo off again, and the server
    /// reconciles it HANDED_OFF. A settles with the vector, the marker goes, and once the server lets the vehicle go the
    /// next demand's command runs.
    /// </summary>
    private static async Task HandOffAgainAndRunTheNextDemandAsync(
        Harness harness,
        FakeIoModuleClient io,
        CancellationToken token)
    {
        io.CloseDoor(0, cargo: false);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestFaultCargoHandoff,
            "the fault cargo handoff entry over A again",
            token);
        int unlocksBefore = io.UnlockCount;
        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync("现场确认故障仓货物需要交接处理。", token));
        await harness.WaitUntilAsync(
            () => io.UnlockCount > unlocksBefore || ReceivedPayloads(harness, "FaultCargoRecoveryResult").Length > 1,
            "the second handoff to open slot 2",
            token);
        io.CloseDoor(1, cargo: false);
        await harness.WaitUntilAsync(
            () => ReceivedPayloads(harness, "FaultCargoRecoveryResult").Length == 2,
            "the second handoff's result",
            token);
        Assert.Equal(
            "HANDED_OFF",
            ReceivedPayloads(harness, "FaultCargoRecoveryResult")[1].GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { RecoveryVector: null, UnsettledSlotOperationAttemptId: null },
            "A to settle with the second handoff",
            token);
        WireToGateRecoveryState settled = ReadJournal(harness, token);
        Assert.Null(settled.TakenOverSlotOperationAttemptId);
        Assert.Null(settled.OperationContext);
        Assert.Empty(settled.ActiveUnlockSlots);
        Assert.Single(ReceivedPayloads(harness, "OperationResult"));

        // The handoff record ends and the server lets the vehicle go.
        harness.Server.ReadinessReasonOverride = null;
        await harness.Server.SendSessionReadinessAsync();
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the session to be ready again",
            token);
        int unlocksBeforeB = io.UnlockCount;
        await SendSlotCommandAsync(harness, DemandB, AttemptB, [5]);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).UnsettledSlotOperationAttemptId == AttemptB
                && io.UnlockCount == unlocksBeforeB + 1,
            "the next demand's command to run",
            token);
        Assert.Equal(0, RefusalsOfB(harness));
    }

    private static async Task LoadBothSlotsOfAAsync(Harness harness, FakeIoModuleClient io, CancellationToken token)
    {
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 2, token);
        io.CloseDoor(1, cargo: true);
    }

    /// <summary>
    /// RECOVERY_REQUIRED from now on, whatever the modelled inputs say: the real server's verdict for a Blocked journey whose
    /// own-order rebuild waits for a cargo handoff (control-server#345). The code is the one existing tests use for a verdict
    /// this double does not model; the real one is not in protocol 2.0.0's enumeration.
    /// </summary>
    private static async Task HoldTheSessionForRecoveryAsync(Harness harness, CancellationToken token)
    {
        harness.Server.ReadinessReasonOverride = "SESSION_RECOVERY_REQUIRED";
        await harness.Server.SendSessionReadinessAsync();
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the session held for recovery",
            token);
    }

    private static async Task ReleaseLoadAckAsync(
        Harness harness,
        TaskCompletionSource resultAckHeld,
        CancellationToken token)
    {
        resultAckHeld.SetResult();
        await harness.WaitUntilAsync(
            () => harness.ViewModel.Logs.Any(line =>
                line.Message.Contains("1、2号仓操作结果已被服务端确认", StringComparison.Ordinal)),
            "the load's handler to be done with the acknowledgement",
            token);
    }

    private const string TakeOverProofVariable = "W2G_G2_HMI278_RECOVERY_PROOF";

    private static readonly WireToGateRecoveryOptions TakeOverRecovery = new(
        ResumeAfterRepairEnabled: true,
        AuthenticationProofEnvironmentVariable: TakeOverProofVariable,
        AdministratorRole: "MAINTENANCE_ADMINISTRATOR",
        VerificationMethod: "SESSION");

    private static async Task<Harness> StartTakeOverStopAsync(
        FakeIoModuleClient io,
        Action<FakeControlServer> configure,
        CancellationToken token)
    {
        Environment.SetEnvironmentVariable(TakeOverProofVariable, "hmi278-proof");
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
                server.RespondToRecoveryRequests = true;
                server.RecoverySessionIdPerRequest = true;
                server.SendRecoveryVectorCommandAfterRecoveryAction = true;
                server.RecoverySlotOperationAttemptId = AttemptA;
                server.RecoveryVectorSlotOperationAttemptId = AttemptA;
                configure(server);
            },
            token,
            Harness.NewJournalPath(),
            io: io,
            recoveryOptions: TakeOverRecovery,
            messageTimeout: TimeSpan.FromSeconds(30));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null
                && harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the worklist on a ready session",
            token);
        return harness;
    }
}
