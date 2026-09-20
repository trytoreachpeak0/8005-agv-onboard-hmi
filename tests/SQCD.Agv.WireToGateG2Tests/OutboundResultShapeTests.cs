using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The lower bound on <c>slotResults</c> is per message, the way the 2.0.0 candidate's schemas
/// state it: <c>LoadCancellationResult</c> allows zero, every other result message still requires
/// at least one.
/// </summary>
/// <remarks>
/// <para>
/// A cancellation that arrives before any slot was opened has nothing to report per slot, and
/// answering it with a fabricated entry would be the vehicle claiming an observation it never made.
/// That is why <c>minItems</c> went to 0 there and nowhere else -- an empty result on a compensation
/// or a correction is a message that lost its content, not one whose content is empty.
/// </para>
/// <para>
/// The check runs before the message is made durable, so these assertions need a client but not a
/// connection: an accepted payload gets as far as the readiness check and fails there instead.
/// </para>
/// </remarks>
public sealed class OutboundResultShapeTests
{
    private const string CancellationId = "66666666-6666-4666-8666-000000000001";
    private const string DemandId = "66666666-6666-4666-8666-000000000002";
    private const string AttemptId = "66666666-6666-4666-8666-000000000003";
    private const string ActionId = "66666666-6666-4666-8666-000000000004";
    private const string CorrectionId = "66666666-6666-4666-8666-000000000005";
    private const string SessionId = "66666666-6666-4666-8666-000000000006";
    private const string HandoffId = "66666666-6666-4666-8666-000000000007";

    /// <summary>
    /// An empty <c>slotResults</c> passes the shape check on <c>LoadCancellationResult</c> -- the
    /// send then fails on readiness, which is a different failure and the point of the assertion.
    /// </summary>
    [Fact]
    public async Task ACancellationResultMayCarryNoSlotResults()
    {
        await using WireToGateSessionClient client = Disconnected();

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.SendLoadCancellationResultAsync(
                    $"load-cancellation-result:{CancellationId}",
                    Guid.NewGuid().ToString("D"),
                    new LoadCancellationResultPayload(
                        CancellationId,
                        DemandId,
                        AttemptId,
                        "ALL_EMPTY",
                        [],
                        DateTimeOffset.UtcNow),
                    TestContext.Current.CancellationToken));

        Assert.Equal("WIRE_TO_GATE_NOT_READY", failure.Message);
    }

    /// <summary>
    /// The same empty list is refused on every other result message that carries one.
    /// </summary>
    [Fact]
    public async Task EveryOtherResultMessageStillRefusesAnEmptySlotResultList()
    {
        await using WireToGateSessionClient client = Disconnected();
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        WireToGateOperatorContextPayload operatorContext =
            new("operator-001", "BADGE", observedAt);

        Assert.Equal(
            "PROTOCOL_SCHEMA_INVALID",
            (await Assert.ThrowsAsync<InvalidDataException>(
                () => client.SendLoadCompensationResultAsync(
                    $"load-compensation-result:{ActionId}",
                    Guid.NewGuid().ToString("D"),
                    new LoadCompensationResultPayload(
                        ActionId, DemandId, AttemptId, "ALL_EMPTY", [], observedAt),
                    TestContext.Current.CancellationToken))).Message);

        Assert.Equal(
            "PROTOCOL_SCHEMA_INVALID",
            (await Assert.ThrowsAsync<InvalidDataException>(
                () => client.SendLoadCorrectionResultAsync(
                    $"load-correction-result:{CorrectionId}",
                    Guid.NewGuid().ToString("D"),
                    new LoadCorrectionResultPayload(
                        CorrectionId, DemandId, AttemptId, "COMPLETED", [], observedAt),
                    TestContext.Current.CancellationToken))).Message);

        Assert.Equal(
            "PROTOCOL_SCHEMA_INVALID",
            (await Assert.ThrowsAsync<InvalidDataException>(
                () => client.SendFaultCargoRecoveryResultAsync(
                    $"fault-cargo-recovery-result:{ActionId}",
                    Guid.NewGuid().ToString("D"),
                    new FaultCargoRecoveryResultPayload(
                        SessionId, ActionId, DemandId, HandoffId, "HANDED_OFF", [],
                        operatorContext, observedAt),
                    TestContext.Current.CancellationToken))).Message);
    }

    /// <summary>
    /// A client wired up exactly as the other G2 fixtures wire one, and deliberately never
    /// connected.
    /// </summary>
    private static WireToGateSessionClient Disconnected()
    {
        Environment.SetEnvironmentVariable(
            "W2G_RESULT_SHAPE_CREDENTIAL", new string('c', 40));

        return new WireToGateSessionClient(
            new WireToGateSessionOptions(
                "127.0.0.1",
                1,
                "AGV-8005-01",
                Guid.NewGuid().ToString("D"),
                new string('a', 40),
                "W2G_RESULT_SHAPE_CREDENTIAL",
                G2SessionTimeouts.Connect,
                G2SessionTimeouts.Message,
                1,
                1,
                "eight-slot-v1",
                "eight-slot-modbus-v1",
                false),
            new FakeIoModuleClient(),
            new SqliteWireToGateJournal(Path.Combine(
                Path.GetTempPath(), $"w2g-result-shape-{Guid.NewGuid():N}.db")),
            new SystemClock(),
            new StoppedVehicle(),
            new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));
    }

    /// <summary>A vehicle that is always stopped, so nothing here turns on motion state.</summary>
    private sealed class StoppedVehicle : IVehicleSafetySignalProvider
    {
        public VehicleSafetySignal Read() =>
            new(VehicleMotionState.Stopped, DateTimeOffset.UtcNow, "RESULT_SHAPE_TEST");
    }
}
