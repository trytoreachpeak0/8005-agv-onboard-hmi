using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// How a close-out refused while the emergency stop is on comes round again (8005-agv-onboard-hmi#409, PR #425 incremental
/// review, probes P2 and P3).
/// </summary>
/// <remarks>
/// <see cref="ACloseOutRefusedAtReconnectBecauseTheStopIsStillOnRunsOnceTheStopIsReleased"/> has the double announce
/// readiness by hand. The real server does not re-announce a readiness that has not changed; in the field the retry comes
/// from the vehicle's own side: the stop is released, the vehicle's safety signal says so, the vehicle sends
/// <c>SafetyStateChanged</c>, its acknowledgement moves the accepted safety version, and the session state published with
/// it runs the close-out again. These tests pin that path, and that retries follow publications and nothing else.
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    private const string CloseOutLine = "会话就绪：装货取消已获授权但未执行，现在收口";

    private static int CloseOuts(Harness harness) =>
        harness.Logger.Entries.Count(entry => entry.Message.StartsWith(CloseOutLine, StringComparison.Ordinal));

    /// <summary>
    /// The stop is still on at the reconnect, so the close-out there is refused. The stop is then released and seen only
    /// through the vehicle's safety signal -- the double announces nothing by itself -- and the cancellation is closed out
    /// once, without a press or a pulse.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ARefusedCloseOutRunsOnceTheReleasedStopIsSeenThroughTheSafetySignal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ObservableSwitchableVehicle vehicle = new();
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
        vehicle.Set(stopped: false);
        Assert.False(await harness.Business.RequestLoadCancellationAsync("现场不装了。", token));
        await harness.WaitForEventAsync("RECOVERY_VECTOR_AUTHORIZED", token);

        await harness.Client.DisconnectAsync();
        await harness.Client.ConnectAndRecoverAsync(token);
        await Harness.WaitUntilAsync(() => CloseOuts(harness) >= 1, "the refused close-out", token, harness.DescribeEvents);
        await Task.Delay(500, token);
        Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, envelope => envelope.MessageType == "LoadCancellationResult");

        vehicle.Set(stopped: true);

        await AssertClosedOutOnceAsync(harness, unlocksBefore, token);
    }

    /// <summary>
    /// While the stop stays on, the refused close-out does not run by itself: once at the reconnect, then at most once per
    /// readiness the server publishes. After the release it is closed out once, and later publications run nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ARefusedCloseOutIsRetriedOnlyOnPublicationsAndClosedOutOnce()
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

        await harness.Client.DisconnectAsync();
        await harness.Client.ConnectAndRecoverAsync(token);
        await Harness.WaitUntilAsync(() => CloseOuts(harness) >= 1, "the refused close-out", token, harness.DescribeEvents);
        await Task.Delay(3000, token);
        int idle = CloseOuts(harness);

        for (int i = 0; i < 5; i++)
        {
            await harness.Server.SendSessionReadinessAsync();
            await Task.Delay(300, token);
        }

        await Task.Delay(1000, token);
        int afterFive = CloseOuts(harness);
        Assert.InRange(idle, 1, 2);
        Assert.InRange(afterFive - idle, 1, 5);
        Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, envelope => envelope.MessageType == "LoadCancellationResult");

        vehicle.Stopped = true;
        await harness.Server.SendSessionReadinessAsync();
        await AssertClosedOutOnceAsync(harness, unlocksBefore, token);
        int settled = CloseOuts(harness);
        for (int i = 0; i < 3; i++)
        {
            await harness.Server.SendSessionReadinessAsync();
            await Task.Delay(300, token);
        }

        await Task.Delay(500, token);
        Assert.Equal(settled, CloseOuts(harness));
        Assert.Single(harness.Server.ReceivedEnvelopes, envelope => envelope.MessageType == "LoadCancellationResult");
    }

    /// <summary>Stopped or of unknown motion as the test says, and saying so when it changes, as the field provider does.</summary>
    private sealed class ObservableSwitchableVehicle : IObservableVehicleSafetySignalProvider
    {
        private volatile bool _stopped = true;

        public event EventHandler<ValueChangedEventArgs<VehicleSafetySignal>>? SignalChanged;

        public void Set(bool stopped)
        {
            _stopped = stopped;
            SignalChanged?.Invoke(this, new ValueChangedEventArgs<VehicleSafetySignal>(Read()));
        }

        public VehicleSafetySignal Read() => new(
            _stopped ? VehicleMotionState.Stopped : VehicleMotionState.Unknown,
            DateTimeOffset.UtcNow,
            "G2_TEST");
    }
}
