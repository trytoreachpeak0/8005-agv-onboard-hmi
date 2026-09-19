using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A <c>SlotOperationResumeCommand</c> the vehicle refuses before any door IO is answered with
/// <c>SlotOperationCommandRejected</c> correlated to that command (8005-agv-onboard-hmi#119).
/// </summary>
/// <remarks>
/// Until then the refusal only reached the log and the operator, and the control server's resume
/// workflow waited for an answer that never came: its session stayed <c>EXECUTING</c> and the vehicle
/// could not open another one. The server half, closing the session on this rejection, is
/// 8005-agv-control-server#187.
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    private const string ResumeMessageId = "abcdabcd-0000-4000-8000-000000000119";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeWhosePersistedStateDoesNotMatchIsRejectedBeforeAnyDoorIo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
        int resultsBefore = harness.ResultsOfType("OperationResult").Count;

        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(state, checkpoint: AnotherCheckpointThan(state.ProvenRecoveryCheckpoint)));

        // The local refusal first: it proves the guard ran, so a missing rejection below is the
        // vehicle staying silent, not the command never arriving.
        await harness.WaitForRecoveryBlockedAsync("RECOVERY_SESSION_NOT_OPEN", token);
        JsonElement rejection = await WaitForSingleRejectionAsync(harness, token);
        Assert.Equal(ResumeMessageId, rejection.GetProperty("correlationId").GetString());
        JsonElement payload = rejection.GetProperty("payload");
        Assert.Equal(AttemptId, payload.GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal(
            "RECOVERY_SESSION_NOT_OPEN",
            payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Equal(resultsBefore, harness.ResultsOfType("OperationResult").Count);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeWhoseOperationContextIsGoneIsRejectedBeforeAnyDoorIo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
        await harness.RewriteRecoveryStateAsync(
            persisted => persisted with { OperationContext = null },
            token);

        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(state));

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_AUTHENTICATION_FAILED", token);
        JsonElement rejection = await WaitForSingleRejectionAsync(harness, token);
        Assert.Equal(ResumeMessageId, rejection.GetProperty("correlationId").GetString());
        JsonElement payload = rejection.GetProperty("payload");
        Assert.Equal(AttemptId, payload.GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal(
            "RECOVERY_AUTHENTICATION_FAILED",
            payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// The safety gate lets the resume through and the executor's own check stops it, still before
    /// the first journal write and the first pulse. The executor's local code is not in the
    /// protocol's registry; the rejection carries the registered one for a resume scope that does
    /// not match what the vehicle has on file.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeTheExecutorRefusesBeforeItsFirstPulseIsRejected()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);

        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(state, commandContentSha256: new string('f', 64)));

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_STATE_MISMATCH", token);
        JsonElement rejection = await WaitForSingleRejectionAsync(harness, token);
        Assert.Equal(ResumeMessageId, rejection.GetProperty("correlationId").GetString());
        JsonElement payload = rejection.GetProperty("payload");
        Assert.Equal(AttemptId, payload.GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal(
            "RECOVERY_SCOPE_MISMATCH",
            payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// Presses the resume entry and waits for the accepted action to be on disk, which is what the
    /// vehicle checks a <c>SlotOperationResumeCommand</c> against.
    /// </summary>
    private static async Task<WireToGateRecoveryState> OpenResumeActionAsync(
        RecoveryVectorHarness harness,
        CancellationToken token)
    {
        Assert.True(await harness.Business.RequestResumeAfterRepairAsync(
            "现场维修完成，申请恢复原仓位操作。", token));
        WireToGateRecoveryState? accepted = null;
        await RecoveryVectorHarness.WaitUntilAsync(
            () =>
            {
                accepted = harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult();
                return accepted.RecoveryActionId is not null;
            },
            "the accepted RESUME_AFTER_REPAIR action to be persisted",
            token);
        return accepted!;
    }

    /// <summary>
    /// The resume command the real server issues for the persisted action: every field names what
    /// the vehicle has on file, unless the test says otherwise.
    /// </summary>
    private static object ResumePayload(
        WireToGateRecoveryState state,
        WireToGateRecoveryCheckpoint? checkpoint = null,
        string? commandContentSha256 = null) =>
        new
        {
            exceptionRecoverySessionId = state.ExceptionRecoverySessionId,
            recoveryActionId = state.RecoveryActionId,
            demandId = DemandId,
            slotOperationAttemptId = AttemptId,
            provenRecoveryCheckpoint = WireCheckpoint(checkpoint ?? state.ProvenRecoveryCheckpoint),
            slots = new[] { 1, 2 },
            commandContentSha256 = commandContentSha256 ?? new string('0', 64)
        };

    private static string WireCheckpoint(WireToGateRecoveryCheckpoint checkpoint) => checkpoint switch
    {
        WireToGateRecoveryCheckpoint.Prepared => "PREPARED",
        WireToGateRecoveryCheckpoint.ActiveUnlockSet => "ACTIVE_UNLOCK_SET",
        WireToGateRecoveryCheckpoint.SafeFinishReached => "SAFE_FINISH_REACHED",
        _ => throw new ArgumentOutOfRangeException(nameof(checkpoint), checkpoint, null)
    };

    private static WireToGateRecoveryCheckpoint AnotherCheckpointThan(WireToGateRecoveryCheckpoint persisted) =>
        persisted == WireToGateRecoveryCheckpoint.SafeFinishReached
            ? WireToGateRecoveryCheckpoint.Prepared
            : WireToGateRecoveryCheckpoint.SafeFinishReached;

    private static IReadOnlyList<JsonElement> Rejections(RecoveryVectorHarness harness) =>
    [
        .. harness.ResultsOfType("SlotOperationCommandRejected")
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
    ];

    private static async Task<JsonElement> WaitForSingleRejectionAsync(
        RecoveryVectorHarness harness,
        CancellationToken token)
    {
        await harness.WaitForInboundAsync("SlotOperationCommandRejected", token);
        return Assert.Single(Rejections(harness));
    }
}
