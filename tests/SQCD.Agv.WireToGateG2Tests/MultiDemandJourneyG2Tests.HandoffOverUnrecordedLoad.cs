using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 故障交接接管了一次「结果已发、确认还没记录」的装货，交接以 UNKNOWN 结束，之后重连（8005-agv-onboard-hmi#278）。
/// </summary>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 装货 A 的 COMPLETED 结果发出、确认被扣住；会话转为需恢复，操作员按故障交接，交接向量接管 A 的尝试号、开了 1 号门；
    /// 此时 A 的确认才到（hmi#259 起只收结果自己那一份，向量留着）。1 号门的锁反馈随后读不到，交接以 UNKNOWN 结束，
    /// 向量被忘掉，留下未结 A、上下文 A 与 1 号门。重连后的还原不得把 A 当已完成的装货结清、把 1 号门从记录里抹掉。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AnUnknownHandoffOverALoadWhoseAckCameLateKeepsItsDoorAcrossAReconnect(bool ackAfterHandoffEnded)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Environment.SetEnvironmentVariable(HandoffOverUnrecordedLoadProofVariable, "hmi278-proof");
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        TaskCompletionSource resultAckHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
                server.OperationResultAckHold = resultAckHeld.Task;
                server.RespondToRecoveryRequests = true;
                server.SendRecoveryVectorCommandAfterRecoveryAction = true;
                server.RecoverySlotOperationAttemptId = AttemptA;
                server.RecoveryVectorSlotOperationAttemptId = AttemptA;
            },
            token,
            Harness.NewJournalPath(),
            io: io,
            recoveryOptions: HandoffOverUnrecordedLoadRecovery,
            messageTimeout: TimeSpan.FromSeconds(30));
        using ReleaseOnExit releaseAck = new(() => resultAckHeld.TrySetResult());
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null
                && harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the worklist on a ready session",
            token);

        // Load A runs to COMPLETED with both slots loaded; its result is taken and the ack held.
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 2, token);
        io.CloseDoor(1, cargo: true);
        await harness.WaitUntilAsync(
            () => harness.Server.OperationResultsHeld == 1,
            "the load's COMPLETED result to be held unacknowledged",
            token);
        Assert.Equal("COMPLETED", Assert.Single(ReceivedPayloads(harness, "OperationResult"))
            .GetProperty("overallOutcome").GetString());

        // The server's journey stops with cargo on board and hands the vehicle to an administrator (control-server#345).
        harness.Server.ReadinessReasonOverride = "SESSION_RECOVERY_REQUIRED";
        await harness.Server.SendSessionReadinessAsync();
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestFaultCargoHandoff,
            "the fault cargo handoff entry over load A",
            token);

        Task<bool> press = harness.Business.RequestFaultCargoHandoffAsync("现场确认故障仓货物需要交接处理。", token);
        await harness.WaitUntilAsync(
            () => io.UnlockCount == 3 || ReceivedPayloads(harness, "FaultCargoRecoveryResult").Length > 0,
            "the handoff to open slot 1",
            token);
        WireToGateRecoveryState taken = ReadJournal(harness, token);
        File.AppendAllText(ProbePath, $"taken: unlocks={io.UnlockCount} pressDone={press.IsCompleted} {Describe(taken)}{Environment.NewLine}");
        Assert.Equal(WireToGateRecoveryVectorTypes.FaultCargoHandoff, taken.RecoveryVector?.VectorType);
        Assert.Equal(AttemptA, taken.RecoveryVector?.SlotOperationAttemptId);
        Assert.Equal(AttemptA, taken.UnsettledSlotOperationAttemptId);
        Assert.Equal([1], taken.ActiveUnlockSlots);

        if (!ackAfterHandoffEnded)
        {
            // A's ack comes now, over the handoff that took its attempt over: the vector stays (onboard-hmi#259).
            await ReleaseLoadAckAsync(harness, resultAckHeld, token);
            Assert.Equal(WireToGateRecoveryVectorTypes.FaultCargoHandoff, ReadJournal(harness, token).RecoveryVector?.VectorType);
        }

        // Door 1's lock feedback goes unreadable while it stands open: the handoff ends UNKNOWN over it.
        io.SetUnreadable(0);
        await Record.ExceptionAsync(() => press);
        JsonElement handoffResult = await WaitForPayloadAsync(harness, "FaultCargoRecoveryResult", token);
        Assert.Equal("UNKNOWN", handoffResult.GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is null,
            "the UNKNOWN handoff to be forgotten once acknowledged",
            token);
        WireToGateRecoveryState forgotten = ReadJournal(harness, token);
        File.AppendAllText(ProbePath, $"[{ackAfterHandoffEnded}] after-forget: {Describe(forgotten)}{Environment.NewLine}");
        if (ackAfterHandoffEnded)
        {
            // A's ack comes only now, the handoff over and forgotten: nothing on file says a vector held the attempt.
            await ReleaseLoadAckAsync(harness, resultAckHeld, token);
            File.AppendAllText(ProbePath, $"[{ackAfterHandoffEnded}] after-late-ack: {Describe(ReadJournal(harness, token))}{Environment.NewLine}");
        }

        int restoresBefore = RestoredEntryLines(harness);
        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await harness.WaitUntilAsync(
            () => RestoredEntryLines(harness) > restoresBefore
                || ReadJournal(harness, token).UnsettledSlotOperationAttemptId is null,
            "the restore to owe the recovery entry, or to settle the attempt",
            token);

        WireToGateRecoveryState after = ReadJournal(harness, token);

        File.AppendAllText(ProbePath, $"[{ackAfterHandoffEnded}] after-reconnect: {Describe(after)}{Environment.NewLine}");
        Assert.Equal(AttemptA, after.UnsettledSlotOperationAttemptId);
        Assert.Equal([1], after.ActiveUnlockSlots);
        Assert.NotEqual(WireToGateRecoveryCheckpoint.ResultRecorded, after.ProvenRecoveryCheckpoint);
        Assert.Single(ReceivedPayloads(harness, "OperationResult"));
        Assert.Empty(harness.UiErrors);
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

    private static string ProbePath =>
        Path.Combine(Path.GetTempPath(), "hmi278-probe.txt");

    private static string Describe(WireToGateRecoveryState state) =>
        $"unsettled={state.UnsettledSlotOperationAttemptId ?? "null"} cp={state.ProvenRecoveryCheckpoint} "
        + $"active=[{string.Join(",", state.ActiveUnlockSlots)}] ctx={state.OperationContext?.SlotOperationAttemptId ?? "null"} "
        + $"vector={state.RecoveryVector?.VectorType ?? "null"} lastLoad={state.LastCompletedLoadOperationContext?.SlotOperationAttemptId ?? "null"} "
        + $"pending={state.PendingResults.Count}";

    private const string HandoffOverUnrecordedLoadProofVariable = "W2G_G2_HMI278_RECOVERY_PROOF";

    private static readonly WireToGateRecoveryOptions HandoffOverUnrecordedLoadRecovery = new(
        ResumeAfterRepairEnabled: true,
        AuthenticationProofEnvironmentVariable: HandoffOverUnrecordedLoadProofVariable,
        AdministratorRole: "MAINTENANCE_ADMINISTRATOR",
        VerificationMethod: "SESSION");
}
