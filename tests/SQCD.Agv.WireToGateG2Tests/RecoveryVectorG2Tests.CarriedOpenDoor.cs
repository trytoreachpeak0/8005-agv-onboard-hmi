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
        Assert.Contains("强制机械恢复", refused.Message, StringComparison.Ordinal);

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
        await harness.WaitForRecoveryBlockedAsync("请先关好1号仓的门", token);

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
        harness.Server.BeforeRecoverySessionAnswer = () => harness.Io.OpenDoor(0);

        Assert.False(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));

        Assert.Equal(2, harness.ResultsOfType("ExceptionRecoverySessionRequested").Count);
        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(after.RecoveryVector);
        Assert.Equal([1], after.ActiveUnlockSlots);
        await harness.WaitForRecoveryBlockedAsync("请先关好仓门", token);
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
