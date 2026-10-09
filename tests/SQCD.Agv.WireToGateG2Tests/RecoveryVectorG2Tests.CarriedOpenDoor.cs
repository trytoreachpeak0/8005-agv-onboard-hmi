using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A door a vector left in doubt is not dropped by preparing the next vector (8005-agv-onboard-hmi#255).
/// </summary>
/// <remarks>
/// <para>
/// A vector that ends <c>UNKNOWN</c> leaves its door in the active unlock set, and <c>ForgetSettledVector</c> keeps it there
/// once the server has the result, because the next handshake reports it as the door that may be open. The next prepared
/// vector used to write the set as <c>[]</c> (<c>WriteRecoveryVectorPreparedAsync</c>): from that press on neither the
/// journal nor the server heard of the door, with nothing at the vehicle proving it shut.
/// </para>
/// <para>
/// Now a press that would prepare a door-opening vector over such a door asks a fresh reading first. Proven shut, the
/// door leaves the set and the press goes on; not proven shut, the press is refused before anything is sent and the
/// operator is told which door to shut. A forced mechanical recovery -- the way out when the door will not shut -- is not
/// refused, and carries the door.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// Slot 1's door reads open after the acknowledged <c>UNKNOWN</c>: the next compensation press is refused with the
    /// door named, nothing is sent, and the door stays in the active unlock set. Once the door is shut the same press goes
    /// through, and the door, proven shut, leaves the set.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ADoorLeftInDoubtByAnAcknowledgedUnknownVectorIsNotDroppedByTheNextPress()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartWithDoorLeftInDoubtAsync(token);

        // The door the vector could not confirm now reads open.
        harness.Io.OpenDoor(0);
        bool requested = await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token);

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.True(
            after.ActiveUnlockSlots.Contains(1),
            $"requested={requested} vector={after.RecoveryVector?.VectorType}/{after.RecoveryVector?.PrimaryId} "
            + $"checkpoint={after.ProvenRecoveryCheckpoint} active=[{string.Join(",", after.ActiveUnlockSlots)}] "
            + $"sessionRequests={harness.ResultsOfType("ExceptionRecoverySessionRequested").Count}");
        Assert.False(requested);
        Assert.Null(after.RecoveryVector);
        Assert.Single(harness.ResultsOfType("ExceptionRecoverySessionRequested"));
        WireToGateOperatorEvent refused = await harness.WaitForRecoveryBlockedAsync("请先关好1号仓的门", token);
        Assert.Contains("改用「强制机械恢复」", refused.Message, StringComparison.Ordinal);

        // The way out: the operator shuts the door and presses again.
        harness.Io.CloseDoor(0, cargo: false);
        Assert.True(harness.Business.CanRequestLoadCompensation);
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ResultsOfType("ExceptionRecoverySessionRequested").Count == 2,
            "a second recovery session request once the door is shut",
            token);
        WireToGateRecoveryState prepared = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(WireToGateRecoveryVectorTypes.LoadCompensation, prepared.RecoveryVector?.VectorType);
        Assert.Empty(prepared.ActiveUnlockSlots);
    }

    /// <summary>
    /// Slot 1's lock is broken and its door will not shut: the forced mechanical recovery entry is lit and its press goes
    /// through, and the prepared forced vector keeps the door in the active unlock set rather than dropping it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedMechanicalRecoveryIsStillOfferedOverADoorLeftInDoubtAndCarriesIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartWithDoorLeftInDoubtAsync(token);
        harness.Io.OpenDoor(0);
        Assert.False(await harness.Business.RequestFaultCargoHandoffAsync("现场确认故障仓货物需要交接处理。", token));
        WireToGateOperatorEvent refused = await harness.WaitForRecoveryBlockedAsync("请先关好1号仓的门", token);
        // The way out named for this press is the entry pressed next, not maintenance (review S1).
        Assert.Contains("改用「强制机械恢复」", refused.Message, StringComparison.Ordinal);

        Assert.True(harness.Business.CanRequestForcedMechanicalRecovery);
        Assert.True(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "现场确认仓门无法电动解锁，申请强制机械恢复。", token));

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector is
            {
                VectorType: WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery
            },
            "the forced mechanical recovery vector to be prepared",
            token);
        WireToGateRecoveryState prepared = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal([1], prepared.ActiveUnlockSlots);
        Assert.Equal(2, harness.ResultsOfType("ExceptionRecoverySessionRequested").Count);
    }

    /// <summary>
    /// Slot 1's door reads shut, but the reading is older than the vehicle trusts: that proves nothing, so the press is
    /// refused and the door stays in the set. A fresh reading of the same shut door lets the next press through.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AStaleReadingOfAShutDoorLeftInDoubtDoesNotProveItShut()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartWithDoorLeftInDoubtAsync(token);
        harness.Io.MakeStale(TimeSpan.FromMinutes(5));

        Assert.False(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        // The door reads shut: the reading is what is wrong, and the operator is told so (review S4).
        WireToGateOperatorEvent refused = await harness.WaitForRecoveryBlockedAsync("IO 读数过期或已断开", token);
        Assert.Contains("无法确认1号仓的门已关好", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("请先关好", refused.Message, StringComparison.Ordinal);
        Assert.Single(harness.ResultsOfType("ExceptionRecoverySessionRequested"));
        Assert.Equal([1], (await harness.ReadRecoveryStateAsync(token)).ActiveUnlockSlots);

        harness.Io.CloseDoor(0, cargo: false);
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        Assert.Empty((await harness.ReadRecoveryStateAsync(token)).ActiveUnlockSlots);
    }

    /// <summary>
    /// Slot 1's door reads shut and locked, but its unlock output is still energised: not proven locked, so the press is
    /// refused, and the operator is told it is the output, not the door (review S2 B5, S4).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ALockedDoorWithItsUnlockOutputStillEnergisedIsNotProvenShut()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartWithDoorLeftInDoubtAsync(token);
        harness.Io.SetUnlockOutputActive(0);

        Assert.False(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));

        WireToGateOperatorEvent refused = await harness.WaitForRecoveryBlockedAsync("开锁输出没有复位", token);
        Assert.Contains("1号仓", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("请先关好", refused.Message, StringComparison.Ordinal);
        Assert.Single(harness.ResultsOfType("ExceptionRecoverySessionRequested"));
        Assert.Equal([1], (await harness.ReadRecoveryStateAsync(token)).ActiveUnlockSlots);
    }

    /// <summary>
    /// The correction press is held to the same rule: slot 1 left in the active unlock set and reading open refuses it,
    /// with the way out named; shut, the press goes through and the door leaves the set (review S2 B1).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task ALoadCorrectionPressIsRefusedOverADoorLeftInDoubtUntilItIsShut()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            loadAlreadySettled: true);
        await harness.RewriteRecoveryStateAsync(state => state with { ActiveUnlockSlots = [1] }, token);
        harness.Io.OpenDoor(0);

        Assert.False(await harness.Business.RequestLoadCorrectionAsync("现场确认需要修正已完成的装货结果。", token));

        WireToGateOperatorEvent refused = await harness.WaitForRecoveryBlockedAsync("请先关好1号仓的门", token);
        Assert.Contains("联系维护人员", refused.Message, StringComparison.Ordinal);
        Assert.Empty(harness.ResultsOfType("LoadCorrectionRequested"));
        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(after.RecoveryVector);
        Assert.Equal([1], after.ActiveUnlockSlots);

        harness.Io.CloseDoor(0, cargo: true);
        Assert.True(await harness.Business.RequestLoadCorrectionAsync("现场确认需要修正已完成的装货结果。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ResultsOfType("LoadCorrectionRequested").Count == 1,
            "the correction request once the door is shut",
            token);
        Assert.Empty((await harness.ReadRecoveryStateAsync(token)).ActiveUnlockSlots);
    }

    /// <summary>
    /// A compensation rejected after it was prepared keeps whatever the active unlock set holds: nothing of the rejected
    /// vector was opened (review S2 B3).
    /// </summary>
    /// <remarks>
    /// The set is put there by hand. With the press's own check it is empty when a compensation is prepared, and nothing
    /// writes it between the preparation and a rejection -- a rejected vector never gets a command -- so the rule only
    /// shows on a journal some other path leaves. This test pins the rule for those, the way hmi#254's manual end of a
    /// recovery may leave one.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ARejectedCompensationKeepsTheDoorInTheActiveSet()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
            },
            cargoInTargetSlots: true);
        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(item => item.MessageType == "LoadCompensationRequested"),
            "the vehicle's request for the compensation's authorization",
            token);
        string requestMessageId = harness.Server.ReceivedEnvelopes
            .First(item => item.MessageType == "LoadCompensationRequested").MessageId;
        await harness.RewriteRecoveryStateAsync(state => state with { ActiveUnlockSlots = [3] }, token);

        // Shaped as the double's own refusal of a compensation request: an answer to the vehicle's request, so the
        // correlationId the schema requires names it.
        await harness.Server.SendCommandAsync(
            "LoadCompensationRejected",
            "5e5e5e5e-5e5e-4e5e-8e5e-5e5e5e5e5e5e",
            new
            {
                recoveryActionId = prepared.RecoveryVector!.PrimaryId,
                problem = new
                {
                    reasonCode = "ACTION_NOT_ALLOWED_IN_STATE",
                    fieldPath = "payload",
                    displayMessage = "Load compensation is not authorized."
                }
            },
            requestMessageId);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector is null,
            "the rejection to clear the prepared compensation",
            token);

        Assert.Equal([3], (await harness.ReadRecoveryStateAsync(token)).ActiveUnlockSlots);
    }

    /// <summary>
    /// An acknowledged forced mechanical recovery turns the slots it covers into physically unknown slots; a door in
    /// doubt it does not cover stays in the active unlock set (review S2 B4).
    /// </summary>
    /// <remarks>
    /// Slot 3 is outside the operation's slots 1 and 2, put in the set by hand: the door a forced recovery is asked for is
    /// normally one of its own slots, and then the set ends empty either way.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedIsolationKeepsADoorInDoubtItDoesNotCover()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(token);
        await harness.RewriteRecoveryStateAsync(state => state with { ActiveUnlockSlots = [3] }, token);

        await harness.IsolateByForcedRecoveryAsync(token);

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal([1, 2], after.ForcedIsolation?.PhysicallyUnknownSlots);
        Assert.Equal([3], after.ActiveUnlockSlots);
    }

    /// <summary>
    /// The server refuses the forced mechanical recovery the operator asked for over the door left in doubt. The refused
    /// vector is cleared, and the door it carried stays in the active unlock set: nothing of the vector was opened, so the
    /// door is as much in doubt as before it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ARefusedForcedMechanicalRecoveryLeavesTheDoorItCarriedInTheActiveSet()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartWithDoorLeftInDoubtAsync(token);
        harness.Io.OpenDoor(0);
        harness.Server.RecoveryActionRejectionReasonCode = "ACTION_NOT_ALLOWED_IN_STATE";

        Assert.False(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "现场确认仓门无法电动解锁，申请强制机械恢复。", token));

        await harness.WaitForRecoveryBlockedAsync("ACTION_NOT_ALLOWED_IN_STATE", token);
        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(after.RecoveryVector);
        Assert.Equal([1], after.ActiveUnlockSlots);
    }

    /// <summary>
    /// The press finds slot 1's door shut and goes on; the door opens again while the session request is on its way. The
    /// write that prepares the vector reads the door again and refuses instead of dropping it: no vector, the door still in
    /// the active unlock set, and the operator told to shut it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ADoorLeftInDoubtThatOpensAgainWhileTheSessionIsRequestedRefusesThePreparation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartWithDoorLeftInDoubtAsync(token);
        // The real server's one open recovery session per vehicle: the session the refused press opens stands, and the
        // next press is answered on it rather than opening another.
        harness.Server.ModelOneOpenRecoverySession = true;
        harness.Server.RecoverySessionSnapshotStatesAfterOpened = ["OPEN"];
        // What the real server offers for a load in RecoveryRequired (OnboardRecoveryCoordinator.AllowedActions).
        harness.Server.OpenSnapshotAllowedActions =
            ["RESUME_AFTER_REPAIR", "COMPENSATE_LOAD_ALL_EMPTY", "FAULT_CARGO_HANDOFF", "FORCED_MECHANICAL_RECOVERY"];
        harness.Server.BeforeRecoverySessionAnswer = () => harness.Io.OpenDoor(0);

        Assert.False(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));

        Assert.Equal(2, harness.ResultsOfType("ExceptionRecoverySessionRequested").Count);
        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(after.RecoveryVector);
        Assert.Equal([1], after.ActiveUnlockSlots);
        WireToGateOperatorEvent refused = await harness.WaitForRecoveryBlockedAsync("请先关好1号仓的门", token);
        Assert.Contains("改用「强制机械恢复」", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("不要仅凭本提示重试", refused.Message, StringComparison.Ordinal);

        // The way out: the session the refused press opened stays open, and the server already queued its OPEN snapshot
        // when it opened it (control-server ca327799, OnboardRecoveryCoordinator.cs:455). With the door shut, the next
        // press goes through on that session -- no third session request (review S3).
        harness.Server.BeforeRecoverySessionAnswer = null;
        harness.Io.CloseDoor(0, cargo: false);
        await RecoveryVectorHarness.WaitUntilAsync(
            // An OPEN snapshot is not acknowledged (only CLOSED is); the vehicle says it applied one with this event.
            () => harness.OperatorEvents.Any(item =>
                item.Kind == "RECOVERY_SESSION_UPDATED" && item.Message.Contains("OPEN", StringComparison.Ordinal)),
            "the vehicle to apply the OPEN snapshot of the session the refused press opened",
            token);
        Assert.True(harness.Business.CanRequestLoadCompensation);
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        Assert.Equal(2, harness.ResultsOfType("ExceptionRecoverySessionRequested").Count);
        WireToGateRecoveryState prepared = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(WireToGateRecoveryVectorTypes.LoadCompensation, prepared.RecoveryVector?.VectorType);
        Assert.Equal(RecoverySessionId, prepared.RecoveryVector?.ExceptionRecoverySessionId);
        Assert.Empty(prepared.ActiveUnlockSlots);
    }

    /// <summary>
    /// A compensation pulsed slot 1 and could not confirm its lock: <c>UNKNOWN</c>, acknowledged, the vector and its
    /// session forgotten, slot 1 left in the active unlock set.
    /// </summary>
    private static async Task<RecoveryVectorHarness> StartWithDoorLeftInDoubtAsync(CancellationToken token)
    {
        RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
            },
            cargoInTargetSlots: true,
            lockerWaitTimesOut: true);
        try
        {
            WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
            await harness.Server.SendCommandAsync(
                "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
            JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
            Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString());
            await WaitForCompensationResultAcknowledgedAsync(harness, prepared, token);
            await RecoveryVectorHarness.WaitUntilAsync(
                () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector is null,
                "the acknowledged result to clear the vector and its recovery session",
                token);
            Assert.Equal([1], (await harness.ReadRecoveryStateAsync(token)).ActiveUnlockSlots);
            harness.VehicleStopped();
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }
}
