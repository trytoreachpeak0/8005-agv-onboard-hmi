using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A recovery vector that ends on anything but <c>COMPLETED</c> is forgotten once the server has
/// acknowledged its result, exactly as a completed one is (onboard-hmi#145 (a)).
/// </summary>
/// <remarks>
/// <para>
/// Until #145 only the <c>COMPLETED</c> branch cleared anything. A <c>FAILED</c> or <c>UNKNOWN</c>
/// result went out, was acknowledged, and left the prepared vector and the recovery session's
/// identity on file for good: every later press of a recovery entry was refused locally with
/// <c>RECOVERY_SESSION_STATE_PENDING</c>, and the only way on was to clear the journal by hand. The
/// server was not stuck -- it closes the session on that result (control-server#169) -- the vehicle
/// was.
/// </para>
/// <para>
/// The <c>CLOSED</c> fallback does not cover this. It forgets only a vector the journal shows did
/// nothing (<c>ForgetRefusedVector</c>), and a vector that reported <c>UNKNOWN</c> after pulsing a
/// door is by definition one that may have acted.
/// </para>
/// <para>
/// Acknowledged first, forgotten second. The clearing hangs off the same return from the durable
/// send the <c>COMPLETED</c> branch uses, so a result whose <c>DurableAck</c> never came leaves the
/// record where it is and is replayed from the outbox on the next session, as it always was.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    private static string CompensationResultKey(string recoveryActionId) =>
        $"recovery-vector-result:{WireToGateRecoveryVectorTypes.LoadCompensation}:{recoveryActionId}";

    /// <summary>
    /// The door was pulsed and its lock never answered: <c>UNKNOWN</c>, with the slot still in the
    /// active unlock set. The vehicle forgets the vector and the session and can ask again.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnUnknownCompensationResultIsForgottenOnceTheServerAcknowledgesIt()
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

        // The door was pulsed, so the CLOSED fallback would refuse to forget this one: nothing but
        // the result path can clear it.
        Assert.Equal(1, harness.Io.UnlockCount);
        await AssertTheVectorAndSessionAreForgottenAsync(harness, token);
    }

    /// <summary>
    /// The IO precheck could not read a target slot: <c>FAILED</c> before any pulse. Same clearing,
    /// same second session -- the outcome decides nothing here, the acknowledgement does.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AFailedCompensationResultIsForgottenOnceTheServerAcknowledgesIt()
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

        // Made unreadable after the vector is prepared, so the request path still opens and only the
        // executor's precheck fails.
        harness.Io.SetUnreadable(1);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));

        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        await WaitForCompensationResultAcknowledgedAsync(harness, prepared, token);

        Assert.Equal(0, harness.Io.UnlockCount);
        await AssertTheVectorAndSessionAreForgottenAsync(harness, token);
    }

    /// <summary>
    /// The result reached the outbox and the server never acknowledged it. Nothing is forgotten: the
    /// record is what the next session's replay is judged against, and clearing it here would leave
    /// a replayed result naming a vector this end no longer has.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnUnacknowledgedCompensationResultLeavesTheVectorAndSessionOnFile()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.LoadCompensationResultAcksToDrop = 1;
            },
            cargoInTargetSlots: true,
            lockerWaitTimesOut: true);

        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));

        // RESULT_ACK_PENDING is published by the very branch that would otherwise have cleared the
        // record, so waiting on it is waiting for that decision to have been made.
        await harness.WaitForResultAsync("LoadCompensationResult", token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Message.Contains("恢复向量结果暂未收到DurableAck", StringComparison.Ordinal)),
            "the vehicle to record that the result has no acknowledgement yet",
            token);

        WireToGateDurableMessage? onFile = await harness.ReadOutgoingAsync(
            CompensationResultKey(prepared.RecoveryVector!.PrimaryId), token);
        Assert.NotNull(onFile);
        Assert.False(onFile.Acknowledged);

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.NotNull(after.RecoveryVector);
        Assert.Equal(prepared.ExceptionRecoverySessionId, after.ExceptionRecoverySessionId);
        Assert.Equal(prepared.RecoveryActionId, after.RecoveryActionId);
    }

    /// <summary>Waits for the vehicle to have recorded the server's DurableAck for the result.</summary>
    private static async Task WaitForCompensationResultAcknowledgedAsync(
        RecoveryVectorHarness harness,
        WireToGateRecoveryState prepared,
        CancellationToken token)
    {
        string key = CompensationResultKey(prepared.RecoveryVector!.PrimaryId);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadOutgoingAsync(key, token).GetAwaiter().GetResult()?.Acknowledged == true,
            "the server's DurableAck for the compensation result to be recorded",
            token);
    }

    /// <summary>
    /// The vector and the session are gone, the unsettled load is not, and the next press opens a
    /// second session for it.
    /// </summary>
    private static async Task AssertTheVectorAndSessionAreForgottenAsync(
        RecoveryVectorHarness harness,
        CancellationToken token)
    {
        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(after.RecoveryVector);
        Assert.Null(after.ExceptionRecoverySessionId);
        Assert.Null(after.RecoveryActionId);
        Assert.Null(after.RecoverySessionRequestId);
        Assert.Equal(AttemptId, after.UnsettledSlotOperationAttemptId);
        Assert.NotNull(after.OperationContext);

        harness.VehicleStopped();
        bool requested = await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token);
        Assert.True(requested, string.Join(" / ", harness.Logger.Entries
            .Where(entry => entry.Severity >= LogSeverity.Warning)
            .Select(entry => entry.Message)
            .TakeLast(3)));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ResultsOfType("ExceptionRecoverySessionRequested").Count == 2,
            "a second recovery session request for the same vehicle",
            token);
    }
}
