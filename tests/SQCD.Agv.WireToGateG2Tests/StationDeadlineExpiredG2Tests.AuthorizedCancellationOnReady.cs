using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A load cancellation the server authorized and the vehicle did not carry out (8005-agv-onboard-hmi#409, split from
/// onboard-hmi#392 item 5; agv02 @217 on 10-21): the authorization arrived during an emergency stop, the vector went on
/// file, and the execution was refused <c>VEHICLE_NOT_READY</c> because the vehicle's motion could not be read as stopped.
/// Once the session was READY again nobody ran it or answered it, and the server's journey stayed in
/// <c>AwaitingLoadResult</c> while the resent <c>SlotOperationCommand</c> was ignored every seven seconds.
/// </summary>
/// <remarks>
/// Under protocol 6.0.0 the cancellation opens no door: its vector reads the slots, waits on any door the load left open,
/// and reports <c>ALL_EMPTY</c> or <c>ALL_EMPTY_DOOR_UNPROVEN</c>. So the vehicle closes it out by itself once the session
/// can carry the result, without anyone pressing again -- once, whatever number of READY sessions follow.
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>
    /// The @217 shape across a reconnect: authorized while the vehicle's motion is unknown, refused before anything is
    /// read, the link drops and comes back with the vehicle stopped. The new READY session closes the cancellation out
    /// with one <c>ALL_EMPTY</c>, pulses nothing, and a further reconnect does not run it again.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnAuthorizedCancellationRefusedAsNotReadyIsClosedOutOnceTheReconnectedSessionIsReady()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        SwitchableVehicle vehicle = new();
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.ReplayJourneySnapshotsWithStableIdentity = true;
            },
            vehicleSafety: vehicle);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        await ShutEmptyForCancellationAsync(harness, token);
        int unlocksBefore = harness.Io.UnlockCount;

        await AuthorizeWhileTheVehicleIsNotReadyAsync(harness, vehicle, token);

        vehicle.Stopped = true;
        await harness.Client.DisconnectAsync();
        await harness.Client.ConnectAndRecoverAsync(token);

        await AssertClosedOutOnceAsync(harness, unlocksBefore, token);

        // A second READY session finds nothing left to close out.
        await harness.Client.DisconnectAsync();
        await harness.Client.ConnectAndRecoverAsync(token);
        await harness.WaitForInboundAsync("SafetyStateChanged", token);
        await Task.Delay(500, token);
        Assert.Single(harness.Server.ReceivedEnvelopes, envelope => envelope.MessageType == "LoadCancellationResult");
        Assert.Equal(unlocksBefore, harness.Io.UnlockCount);
    }

    /// <summary>
    /// The @217 shape on one connection: the session goes RECOVERY_REQUIRED <c>DEPARTURE_UNSAFE</c> for the emergency stop,
    /// the cancellation is authorized and refused <c>VEHICLE_NOT_READY</c> in it, and the stop is released -- the same
    /// session announced READY again with the vehicle stopped. That READY closes the cancellation out once.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnAuthorizedCancellationRefusedDuringAnEmergencyStopIsClosedOutOnceTheSameSessionIsReadyAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        SwitchableVehicle vehicle = new();
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server => server.RespondToLoadCancellationRequests = true,
            vehicleSafety: vehicle);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        await ShutEmptyForCancellationAsync(harness, token);
        int unlocksBefore = harness.Io.UnlockCount;

        harness.Server.ReadinessReasonOverride = "DEPARTURE_UNSAFE";
        await harness.Server.SendSessionReadinessAsync();
        await Harness.WaitUntilAsync(
            () => harness.Client.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the session to be RECOVERY_REQUIRED for the emergency stop",
            token);
        await AuthorizeWhileTheVehicleIsNotReadyAsync(harness, vehicle, token);

        vehicle.Stopped = true;
        harness.Server.ReadinessReasonOverride = null;
        await harness.Server.SendSessionReadinessAsync();

        await AssertClosedOutOnceAsync(harness, unlocksBefore, token);
    }

    /// <summary>
    /// The same refusal, then the vehicle restarts before anyone presses again. The restarted vehicle finds the vector on
    /// file and closes it out with one <c>ALL_EMPTY</c> once its session is READY: no <c>OperationResult</c> for the
    /// load the cancellation took over, and no pulse.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnAuthorizedCancellationRefusedAsNotReadyIsClosedOutOnceAfterARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        FakeControlServer before;
        SwitchableVehicle vehicle = new();
        await using (Harness beforeRestart = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.ReplayJourneySnapshotsWithStableIdentity = true;
            },
            journalPath: journalPath,
            vehicleSafety: vehicle))
        {
            before = beforeRestart.Server;
            await beforeRestart.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
            await ShutEmptyForCancellationAsync(beforeRestart, token);
            await AuthorizeWhileTheVehicleIsNotReadyAsync(beforeRestart, vehicle, token);
        }

        // The door stays shut empty while the vehicle is down, and the vehicle comes back stopped.
        await using Harness afterRestart = await Harness.StartAsync(
            new FakeIoModuleClient(),
            token,
            server =>
            {
                server.ReplayJourneySnapshotsWithStableIdentity = true;
                server.RespondToLoadCancellationRequests = true;
                server.AdoptDurableRecoveryMemoryFrom(before);
            },
            journalPath: journalPath,
            baselineRevision: 2);

        await AssertClosedOutOnceAsync(afterRestart, unlocksBefore: 0, token);
        Assert.DoesNotContain(
            afterRestart.Server.ReceivedEnvelopes,
            envelope => envelope.MessageType == "LoadCancellationStartRequested");
    }

    /// <summary>
    /// Presses the cancellation with the vehicle's motion unknown, as during the @217 emergency stop: the server authorizes
    /// it, the vector goes on file, and the execution is refused <c>VEHICLE_NOT_READY</c> with nothing sent.
    /// </summary>
    private static async Task AuthorizeWhileTheVehicleIsNotReadyAsync(
        Harness harness,
        SwitchableVehicle vehicle,
        CancellationToken token)
    {
        vehicle.Stopped = false;
        Assert.False(await harness.Business.RequestLoadCancellationAsync("现场不装了。", token));
        await harness.WaitForEventAsync("RECOVERY_VECTOR_AUTHORIZED", token);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Message.Contains("未执行：reason=VEHICLE_NOT_READY", StringComparison.Ordinal));
        WireToGateRecoveryState refused = harness.ReadRecoveryState(token);
        Assert.Equal(WireToGateRecoveryVectorTypes.LoadCancellation, refused.RecoveryVector?.VectorType);
        Assert.Null(refused.PendingLoadCancellation);
        Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.MessageType == "LoadCancellationResult");
    }

    private static async Task AssertClosedOutOnceAsync(Harness harness, int unlocksBefore, CancellationToken token)
    {
        await harness.WaitForInboundAsync("LoadCancellationResult", token);
        await Harness.WaitUntilAsync(
            () => harness.ReadRecoveryState(token).RecoveryVector is null,
            "the acknowledged cancellation result to clear the vector",
            token,
            harness.DescribeEvents);
        await Task.Delay(300, token);

        JsonElement result = harness.SingleResult("LoadCancellationResult");
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        JsonElement slot = Assert.Single(result.GetProperty("slotResults").EnumerateArray());
        Assert.Equal(1, slot.GetProperty("slotNo").GetInt32());
        Assert.Equal("EMPTY", slot.GetProperty("finalPhysicalState").GetString());
        Assert.Equal("LOCKED", slot.GetProperty("lockState").GetString());
        Assert.Equal("RESET", slot.GetProperty("unlockOutputState").GetString());
        Assert.Equal(unlocksBefore, harness.Io.UnlockCount);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "OperationResult");
    }

    /// <summary>Stopped or of unknown motion as the test says, always freshly observed.</summary>
    private sealed class SwitchableVehicle : IVehicleSafetySignalProvider
    {
        private volatile bool _stopped = true;

        public bool Stopped
        {
            get => _stopped;
            set => _stopped = value;
        }

        public VehicleSafetySignal Read() => new(
            _stopped ? VehicleMotionState.Stopped : VehicleMotionState.Unknown,
            DateTimeOffset.UtcNow,
            "G2_TEST");
    }
}
