using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A recovery command the vehicle refuses before any unlock still gets its result
/// (onboard-hmi#123).
/// </summary>
/// <remarks>
/// <para>
/// Before #123 a command stopped by <c>EnsureVehicleStoppedAndFresh</c> was answered with a log line
/// and a local <c>RECOVERY_BLOCKED</c> event and nothing on the wire. Once the control server began
/// refusing a second submission of the same action (control-server#187), that silence left the
/// server's workflow in <c>AwaitingResult</c> and its session in <c>EXECUTING</c> with no way out
/// short of a reconnect. The server already closes the session on a <c>FAILED</c> recovery result
/// (control-server#169), so all the vehicle owes it is that result.
/// </para>
/// <para>
/// <c>FAILED</c> with every slot <c>NOT_STARTED</c> is what the executor's own IO precheck already
/// reports when it refuses before the first pulse (ADR-cross-0058, producer audit item 3); a refusal
/// on vehicle motion is the same fact reached one step earlier.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationRefusedBeforeAnyUnlockIsReportedFailedWithTheSlotsAsRead()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        Assert.True(harness.Business.CanRequestLoadCompensation);

        // Stopped for the request, not for the command: the request path does not read the vehicle
        // safety fact, the command path does before anything else.
        harness.VehicleMotionUnknown();
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));

        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);

        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(ActionIdFor(CompensateLoadAction), result.GetProperty("recoveryActionId").GetString());
        Assert.Equal(AttemptId, result.GetProperty("slotOperationAttemptId").GetString());
        AssertNotStartedAsRead(result.GetProperty("slotResults"));
        Assert.Equal(0, harness.Io.UnlockCount);

        // The local account of the refusal is unchanged.
        await harness.WaitForRecoveryBlockedAsync("VEHICLE_NOT_READY", token);
    }

    /// <summary>
    /// The vehicle passes the command's first motion check and is moving by the second, the one
    /// the execution makes before it starts (onboard-hmi#129 C-2). Nothing has been unlocked, so it
    /// is the same refusal before any unlock and gets the same FAILED, and the vehicle can then open
    /// the next session.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationThatStartsMovingBetweenItsTwoMotionChecksIsStillReportedFailed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);

        harness.VehicleStartsMovingAfterTheFirstCommandCheck();
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));

        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(ActionIdFor(CompensateLoadAction), result.GetProperty("recoveryActionId").GetString());
        AssertNotStartedAsRead(result.GetProperty("slotResults"));
        Assert.Equal(0, harness.Io.UnlockCount);
        await harness.WaitForRecoveryBlockedAsync("VEHICLE_NOT_READY", token);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector is null,
            "the refusal on the second check to release its vector and session",
            token);
        WireToGateRecoveryState released = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(released.ExceptionRecoverySessionId);
        Assert.Equal(AttemptId, released.UnsettledSlotOperationAttemptId);

        harness.VehicleStopped();
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        Assert.Equal(2, harness.ResultsOfType("ExceptionRecoverySessionRequested").Count);
    }

    /// <summary>The same second-check refusal for a fault cargo handoff (onboard-hmi#129 C-2).</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AFaultCargoHandoffThatStartsMovingBetweenItsTwoMotionChecksIsStillReportedFailed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);

        harness.VehicleStartsMovingAfterTheFirstCommandCheck();
        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync(
            "现场确认故障仓货物需要交接处理。", token));

        JsonElement result = await harness.WaitForResultAsync("FaultCargoRecoveryResult", token);
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        AssertNotStartedAsRead(result.GetProperty("slotResults"));
        Assert.Equal(0, harness.Io.UnlockCount);
        await harness.WaitForRecoveryBlockedAsync("VEHICLE_NOT_READY", token);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AFaultCargoHandoffRefusedBeforeAnyUnlockIsReportedFailedWithTheSlotsAsRead()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);

        harness.VehicleMotionUnknown();
        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync(
            "现场确认故障仓货物需要交接处理。", token));

        JsonElement result = await harness.WaitForResultAsync("FaultCargoRecoveryResult", token);

        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(ActionIdFor(FaultCargoHandoffAction), result.GetProperty("recoveryActionId").GetString());
        Assert.Equal(ExpectedHandoffId, result.GetProperty("handoffId").GetString());
        AssertNotStartedAsRead(result.GetProperty("slotResults"));
        Assert.Equal(0, harness.Io.UnlockCount);
        await harness.WaitForRecoveryBlockedAsync("VEHICLE_NOT_READY", token);
    }

    /// <summary>
    /// The same command issued again after the refusal is answered by the one result already on
    /// file: one durable message, sent once, nothing re-derived from what the vehicle reads now.
    /// </summary>
    /// <remarks>
    /// The result is keyed on the recovery action, not on the command's envelope, so the second copy
    /// finds it before the vehicle safety fact is read again. A refusal that left nothing durable
    /// behind would be decided afresh on every copy -- and once the vehicle had stopped, a later
    /// copy would pulse both slots and report a second, different outcome for the same action.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationIssuedAgainAfterTheRefusalGetsTheSameSingleResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoveryVectorCommandCopies = 2,
            cargoInTargetSlots: true);
        int replays = 0;
        harness.Business.OperatorEventPublished += (_, args) =>
        {
            if (args.Value.Kind == "OPERATION_REPLAY")
            {
                Interlocked.Increment(ref replays);
            }
        };
        harness.VehicleMotionUnknown();

        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => Volatile.Read(ref replays) == 1,
            "the second copy of the command to be answered as a replay",
            token);

        string sent = Assert.Single(harness.ResultsOfType("LoadCompensationResult"));
        WireToGateDurableMessage durable = (await harness.ReadOutgoingAsync(
            $"recovery-vector-result:{WireToGateRecoveryVectorTypes.LoadCompensation}:{ActionIdFor(CompensateLoadAction)}",
            token))!;
        Assert.Equal(durable.WireLine.TrimEnd(), sent.TrimEnd());
        using JsonDocument document = JsonDocument.Parse(sent);
        Assert.Equal(
            "FAILED",
            document.RootElement.GetProperty("payload").GetProperty("overallOutcome").GetString());
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// Once the server has the refusal, the vehicle can ask for recovery again: the next press opens
    /// a new session over the same unsettled load instead of refusing locally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server closes the session on the <c>FAILED</c> result (control-server#169), and a closed
    /// session reaches this end as no session at all -- which is what this harness's double, sending
    /// no session snapshots, already shows it. Before the release the prepared vector and the old
    /// session's identity stayed in the journal, so the entry stayed lit and every press was refused
    /// with <c>RECOVERY_SESSION_STATE_PENDING</c>: the server was no longer stuck, the vehicle was.
    /// onboard-hmi#119 met the same wall after a refused resume on the real rig (P-07).
    /// </para>
    /// <para>
    /// The load itself is kept: the attempt is still unsettled, and the next session is what recovers
    /// it.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AfterARefusedCompensationIsAcknowledgedTheVehicleCanOpenAnotherSession()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        harness.VehicleMotionUnknown();
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await harness.WaitForResultAsync("LoadCompensationResult", token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector is null,
            "the acknowledged refusal to release its vector and session",
            token);

        WireToGateRecoveryState released = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(released.ExceptionRecoverySessionId);
        Assert.Null(released.RecoveryActionId);
        Assert.Equal(AttemptId, released.UnsettledSlotOperationAttemptId);
        Assert.Equal(AttemptId, released.OperationContext!.SlotOperationAttemptId);

        harness.VehicleStopped();
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        Assert.Equal(2, harness.ResultsOfType("ExceptionRecoverySessionRequested").Count);
    }

    /// <summary>
    /// The release does not wait for the server's acknowledgement: once the result is in the outbox
    /// the vector and the session are forgotten, acknowledged or not.
    /// </summary>
    /// <remarks>
    /// The outbox replays an unacknowledged result on the next session, the server closes the
    /// session on it, and nothing sends the command again -- so a release that waited for the
    /// acknowledgement would never happen, and every press would be refused with
    /// <c>RECOVERY_SESSION_STATE_PENDING</c> (coordinator review, onboard-hmi#119 and #123).
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnUnacknowledgedRefusalStillLetsTheVehicleOpenAnotherSession()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.LoadCompensationResultAcksToDrop = 1,
            cargoInTargetSlots: true);
        harness.VehicleMotionUnknown();
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await harness.WaitForResultAsync("LoadCompensationResult", token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector is null,
            "the unacknowledged refusal to release its vector and session",
            token);

        WireToGateRecoveryState released = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(released.ExceptionRecoverySessionId);
        Assert.Equal(AttemptId, released.UnsettledSlotOperationAttemptId);
        Assert.NotNull(await harness.ReadOutgoingAsync(
            $"recovery-vector-result:{WireToGateRecoveryVectorTypes.LoadCompensation}:{ActionIdFor(CompensateLoadAction)}",
            token));

        harness.VehicleStopped();
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        Assert.Equal(2, harness.ResultsOfType("ExceptionRecoverySessionRequested").Count);
    }

    /// <summary>
    /// The third command of #123 has no refusal before an unlock to answer: a forced mechanical
    /// recovery never unlocks, so its command path does not read vehicle motion at all.
    /// </summary>
    /// <remarks>
    /// This pins that premise rather than a fix -- it holds on the code before #123 too. The people
    /// at the vehicle cut its power for a forced recovery, so a safety fact that is unknown or stale
    /// is the expected condition, not a reason to refuse; the command binds and waits for the
    /// operator, and the operator's confirmation is what reports. Were a motion check ever added in
    /// front of it, this test would be the one to say the command now sits unanswered.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedRecoveryIsNotRefusedOnVehicleMotionAndStillReportsOnConfirmation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.ForcedRecoveryGeneration = 4,
            cargoInTargetSlots: true);

        harness.VehicleMotionUnknown();
        Assert.True(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "现场确认仓门无法电动解锁，申请强制机械恢复。", token));
        await harness.ConfirmForcedMechanicalRecoveryAsync(token);

        JsonElement result = await harness.WaitForResultAsync("ForcedMechanicalRecoveryResult", token);

        Assert.Equal("MECHANICALLY_ISOLATED", result.GetProperty("outcome").GetString());
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Equal(0, harness.RecoveryBlockedCount);
    }

    /// <summary>
    /// Slots 1 and 2 hold cargo behind shut, locked doors with the output reset, and nothing was
    /// done to them: each is <c>NOT_STARTED</c> with exactly that reading and the reason the vehicle
    /// refused.
    /// </summary>
    private static void AssertNotStartedAsRead(JsonElement slotResults)
    {
        Assert.Equal(
            [1, 2],
            slotResults.EnumerateArray().Select(slot => slot.GetProperty("slotNo").GetInt32()).ToArray());
        Assert.All(slotResults.EnumerateArray(), slot =>
        {
            Assert.Equal("NOT_STARTED", slot.GetProperty("outcome").GetString());
            Assert.Equal("OCCUPIED", slot.GetProperty("finalPhysicalState").GetString());
            Assert.Equal("LOCKED", slot.GetProperty("lockState").GetString());
            Assert.Equal("RESET", slot.GetProperty("unlockOutputState").GetString());
            Assert.Equal(
                ["VEHICLE_NOT_READY"],
                slot.GetProperty("reasonCodes").EnumerateArray().Select(code => code.GetString()!).ToArray());
        });
    }
}
