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
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestFaultCargoHandoff,
            "the fault cargo handoff entry",
            token);
        harness.ViewModel.RefreshWireToGateInputState();
        Assert.True(harness.ViewModel.CanRequestFaultCargoHandoff);
        Assert.Equal("目标：子批 SUBLOT-B", harness.ViewModel.RecoveryFallbackTargetText);

        // The handoff the entry offers is B's, and the server opens it.
        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync("现场确认故障仓货物需要交接处理。", token));
        JsonElement sessionB = Assert.Single(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));
        Assert.Equal(DemandB, sessionB.GetProperty("demandId").GetString());
        Assert.Equal([5], sessionB.GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()));
        await harness.WaitUntilAsync(
            () => io.UnlockCount == 3 || ReceivedPayloads(harness, "FaultCargoRecoveryResult").Length > 0,
            "B's handoff to open slot 5",
            token);
        io.CloseDoor(4, cargo: false);
        JsonElement handoffB = await WaitForPayloadAsync(harness, "FaultCargoRecoveryResult", token);
        Assert.Equal("HANDED_OFF", handoffB.GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is null,
            "B's handoff to be settled",
            token);

        // A's cargo is still in slot 1 and the server still holds the vehicle for it: A must be the next subject.
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the session still held for A's cargo",
            token);
        harness.ViewModel.RefreshWireToGateInputState();
        Assert.Equal(DemandA, harness.Business.RecoveryFallbackDemandId);
        Assert.True(harness.ViewModel.CanRequestFaultCargoHandoff);
        Assert.Equal("目标：子批 SUBLOT-A", harness.ViewModel.RecoveryFallbackTargetText);

        // The second press asks about A over slot 1, which the server's scope check accepts.
        harness.Server.RecoverySlotOperationAttemptId = AttemptA;
        harness.Server.RecoveryVectorSlotOperationAttemptId = AttemptA;
        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync("现场确认故障仓货物需要交接处理。", token));
        JsonElement sessionA = ReceivedPayloads(harness, "ExceptionRecoverySessionRequested")[1];
        Assert.Equal(DemandA, sessionA.GetProperty("demandId").GetString());
        Assert.Equal([1], sessionA.GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Empty(harness.UiErrors);
    }

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
