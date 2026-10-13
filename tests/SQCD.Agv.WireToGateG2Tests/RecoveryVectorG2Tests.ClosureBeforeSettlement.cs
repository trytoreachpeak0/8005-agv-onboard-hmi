using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The journey's closure reaches the vehicle while a compensation's vector is still on file, and the vector settles after it
/// (8005-agv-onboard-hmi#330 review S1).
/// </summary>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// The compensation is over the settled load; its <c>ALL_EMPTY</c> result goes out and the ack is held past the send. The server's
    /// closure business state (no purpose) arrives meanwhile: the vector is still on file, so the last completed load is
    /// kept. Then the ack is released and the vector settles: that same write applies the closure the vehicle already holds
    /// and drops the record, so no correction or compensation is left over the finished journey's load. Before the review
    /// the record stayed until a later business state without a transport, a reconnect or a restart.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AClosureBeforeTheVectorSettlesDropsTheLastLoadWhenItSettles()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeControlServer.DurableAckHold? hold = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
                hold = server.HoldNextDurableAck("LoadCompensationResult");
            },
            loadAlreadySettled: true);

        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        Assert.NotNull(prepared.LastCompletedLoadOperationContext);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        await WaitForAckPendingAsync(harness, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => hold!.Held.IsCompleted,
            "the compensation result whose ack is held to reach the control server",
            token);

        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", ClosureBusinessState(revision: 5));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.VehicleBusinessState?.Revision == 5,
            "the closure to be applied",
            token);
        // Give the fire-and-forget journey write its turn: it must keep the record while the vector stands.
        await Task.Delay(300, token);
        WireToGateRecoveryState beforeSettling = await harness.ReadRecoveryStateAsync(token);
        Assert.NotNull(beforeSettling.RecoveryVector);
        Assert.NotNull(beforeSettling.LastCompletedLoadOperationContext);

        WireToGateLateDurableAck ack = await LateDurableAckObservation.SettleAsync(
            harness.Session,
            await hold!.Held,
            hold.ReleaseAsync,
            () => string.Join(
                Environment.NewLine,
                harness.Logger.Entries.Select(entry => $"{entry.Severity} {entry.Message}")),
            token);
        Assert.Equal(WireToGateLateDurableAckOutcome.Acknowledged, ack.Outcome);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult() is
            {
                RecoveryVector: null,
                LastCompletedLoadOperationContext: null
            },
            "the settled vector to take the closed journey's last load with it",
            token);
        Assert.False(harness.Business.CanRequestLoadCorrection);
        Assert.False(harness.Business.CanRequestLoadCompensation);
    }

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
}
