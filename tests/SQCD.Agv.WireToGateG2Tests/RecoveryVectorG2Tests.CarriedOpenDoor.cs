using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// A vector ended <c>UNKNOWN</c> with slot 1 in the active unlock set; its result was acknowledged and the vector
    /// forgotten, and slot 1's door reads open. The operator presses compensation again. Whatever that press does, the
    /// door that reads open stays in the active unlock set -- the set the next handshake reports as the doors that may
    /// be open (8005-agv-onboard-hmi#255).
    /// </summary>
    /// <remarks>
    /// <c>ForgetSettledVector</c> keeps the active unlock set on purpose: the next handshake reports it. The next
    /// prepared vector used to write it as <c>[]</c> (<c>WriteRecoveryVectorPreparedAsync</c>), so from that press on
    /// neither the journal nor the server heard of the door, with nothing at the vehicle proving it shut.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ADoorLeftInDoubtByAnAcknowledgedUnknownVectorIsNotDroppedByTheNextPress()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
            },
            cargoInTargetSlots: true,
            lockerWaitTimesOut: true);

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
        WireToGateRecoveryState forgotten = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal([1], forgotten.ActiveUnlockSlots);

        // The door the vector could not confirm now reads open.
        harness.Io.OpenDoor(0);
        harness.VehicleStopped();
        bool requested = await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token);

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.True(
            after.ActiveUnlockSlots.Contains(1),
            $"requested={requested} vector={after.RecoveryVector?.VectorType}/{after.RecoveryVector?.PrimaryId} "
            + $"checkpoint={after.ProvenRecoveryCheckpoint} active=[{string.Join(",", after.ActiveUnlockSlots)}] "
            + $"sessionRequests={harness.ResultsOfType("ExceptionRecoverySessionRequested").Count}");
    }
}
