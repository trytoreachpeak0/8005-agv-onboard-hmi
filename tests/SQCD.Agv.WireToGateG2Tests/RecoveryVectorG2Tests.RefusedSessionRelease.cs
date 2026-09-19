using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A session the vehicle refused inside is forgotten by every path that can see the refusal: the
/// refusal itself, a replay of the refused command, and the server's CLOSED snapshot for that session
/// (onboard-hmi#119 and #123, coordinator review).
/// </summary>
/// <remarks>
/// <para>
/// Forgetting it is what lets the vehicle open the next session. The journal keeps the recovery
/// session's identity -- and for a recovery vector, the prepared vector -- until something clears it,
/// and while it names a session the server has closed, every recovery entry refuses locally with
/// <c>RECOVERY_SESSION_STATE_PENDING</c>.
/// </para>
/// <para>
/// The refusal alone is not enough. The write that forgets can itself fail, and the vehicle can stop
/// between putting the answer on file and forgetting; either way the answer still reaches the server,
/// the server closes the session, and nothing sends the command again. The tests below leave the
/// journal in exactly that state with <see cref="RecoveryVectorHarness.RewriteRecoveryStateAsync"/>
/// and then show which later event clears it.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    private const string ClosedSnapshotMessageId = "abcdabcd-0000-4000-8000-000000000123";
    private const string CompensationCommandMessageId = "abcdabcd-0000-4000-8000-000000001230";
    private static readonly int[] CompensationSlots = [1, 2];

    /// <summary>
    /// The same compensation command, arriving again after its refusal is on file but before the
    /// vehicle forgot the session, is answered as a replay and forgets the session then.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AReplayedRefusedCompensationForgetsTheSessionLeftBehind()
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
        await RefuseCompensationAsync(harness, prepared, token);

        // The journal as a stop between the answer and the release leaves it.
        await harness.RewriteRecoveryStateAsync(_ => prepared, token);
        int blockedBefore = harness.RecoveryBlockedCount;
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector is null,
            "the replayed refusal to forget the vector and session left behind",
            token);

        await AssertANewSessionCanBeOpenedAsync(harness, token);
        Assert.Single(harness.ResultsOfType("LoadCompensationResult"));
        Assert.Equal(blockedBefore, harness.RecoveryBlockedCount);
    }

    /// <summary>
    /// The release after the refused compensation did not reach the disk; the server closes the
    /// session on the result, and its CLOSED snapshot for that session is what clears the journal.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task TheClosedSnapshotForgetsARefusedCompensationWhoseReleaseWasLost()
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
        await RefuseCompensationAsync(harness, prepared, token);

        // What a failed release write leaves: the answer went out, the journal still names it all.
        await harness.RewriteRecoveryStateAsync(_ => prepared, token);
        await SendClosedSnapshotAsync(harness, prepared, "COMPENSATE_LOAD_ALL_EMPTY", CompensationSlots);
        await harness.WaitForInboundAsync("SnapshotAppliedAck", token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector is null,
            "the CLOSED snapshot to forget the refused vector and its session",
            token);

        await AssertANewSessionCanBeOpenedAsync(harness, token);
    }

    /// <summary>
    /// The fallback never forgets a vector that may have acted: a closed session does not make an
    /// unproven slot proven. The vector and its session stay on file for its result to settle.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task TheClosedSnapshotLeavesAVectorThatMayHaveActedOnFile()
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

        // Slot 1 is the active unlock set: the vector may have pulsed it.
        WireToGateRecoveryState acting = prepared with
        {
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ActiveUnlockSet,
            ActiveUnlockSlots = [1]
        };
        await harness.RewriteRecoveryStateAsync(_ => acting, token);
        await SendClosedSnapshotAsync(harness, prepared, "COMPENSATE_LOAD_ALL_EMPTY", CompensationSlots);
        await harness.WaitForInboundAsync("SnapshotAppliedAck", token);

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.NotNull(after.RecoveryVector);
        Assert.Equal(prepared.ExceptionRecoverySessionId, after.ExceptionRecoverySessionId);
        Assert.Equal(prepared.RecoveryActionId, after.RecoveryActionId);
        Assert.Equal([1], after.ActiveUnlockSlots);
    }

    /// <summary>
    /// The same fallback for the refused resume of onboard-hmi#119: its release write lost, the
    /// CLOSED snapshot for its session clears the journal.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task TheClosedSnapshotForgetsARefusedResumeWhoseReleaseWasLost()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = AttemptId);
        WireToGateRecoveryState opened = await OpenResumeActionAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(opened, checkpoint: AnotherCheckpointThan(opened.ProvenRecoveryCheckpoint)));
        await WaitForSingleRejectionAsync(harness, token);

        await harness.RewriteRecoveryStateAsync(
            persisted => persisted with
            {
                ExceptionRecoverySessionId = opened.ExceptionRecoverySessionId,
                RecoveryActionId = opened.RecoveryActionId,
                RecoverySessionRequestId = opened.RecoverySessionRequestId,
                RecoveryActionRequestId = opened.RecoveryActionRequestId,
                RecoveryReason = opened.RecoveryReason,
                RecoveryOperatorId = opened.RecoveryOperatorId,
                RecoveryOperatorVerifiedAt = opened.RecoveryOperatorVerifiedAt
            },
            token);
        await SendClosedSnapshotAsync(harness, opened, "RESUME_AFTER_REPAIR", ResumeSlots);
        await harness.WaitForInboundAsync("SnapshotAppliedAck", token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult()
                .ExceptionRecoverySessionId is null,
            "the CLOSED snapshot to forget the refused resume's session",
            token);

        await AssertANewSessionCanBeOpenedAsync(harness, token);
    }

    /// <summary>
    /// Requests a compensation and stops where the server has accepted the action: the vector is
    /// prepared and names its session, and no command has been sent.
    /// </summary>
    private static async Task<WireToGateRecoveryState> PrepareCompensationAsync(
        RecoveryVectorHarness harness,
        CancellationToken token)
    {
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult() is
            {
                RecoveryVector: not null,
                ExceptionRecoverySessionId: not null,
                RecoveryActionId: not null
            },
            "the compensation vector to be prepared under its session",
            token);
        return await harness.ReadRecoveryStateAsync(token);
    }

    /// <summary>Sends the compensation command to a vehicle that is not stopped, and waits for its FAILED.</summary>
    private static async Task RefuseCompensationAsync(
        RecoveryVectorHarness harness,
        WireToGateRecoveryState prepared,
        CancellationToken token)
    {
        harness.VehicleMotionUnknown();
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
        await harness.WaitForResultAsync("LoadCompensationResult", token);
        await harness.WaitForRecoveryBlockedAsync("VEHICLE_NOT_READY", token);
    }

    private static object CompensationCommand(WireToGateRecoveryState prepared)
    {
        WireToGateRecoveryVectorContext vector = prepared.RecoveryVector!;
        return new
        {
            recoveryActionId = vector.PrimaryId,
            exceptionRecoverySessionId = vector.ExceptionRecoverySessionId,
            demandId = DemandId,
            slotOperationAttemptId = AttemptId,
            slots = CompensationSlots,
            expectedFinalPhysicalState = "EMPTY",
            commandContentSha256 = FakeControlServerIdentifiers.LoadCompensationContentSha256(
                vector.PrimaryId, DemandId, AttemptId, CompensationSlots)
        };
    }

    private static Task SendClosedSnapshotAsync(
        RecoveryVectorHarness harness,
        WireToGateRecoveryState opened,
        string selectedAction,
        IReadOnlyList<int> slots) =>
        harness.Server.SendCommandAsync(
            "ExceptionRecoverySessionSnapshot",
            ClosedSnapshotMessageId,
            new
            {
                exceptionRecoverySessionId = opened.ExceptionRecoverySessionId,
                recoverySessionRevision = 3,
                state = "CLOSED",
                administratorId = "maintenance-001",
                administratorRole = "MAINTENANCE_ADMINISTRATOR",
                eventId = opened.RecoverySessionRequestId,
                demandId = DemandId,
                slotOperationAttemptId = AttemptId,
                slots,
                selectedAction,
                allowedActions = Array.Empty<string>(),
                blockingFacts = Array.Empty<object>()
            });

    /// <summary>The unsettled load is kept, and the next press opens a second session for it.</summary>
    private static async Task AssertANewSessionCanBeOpenedAsync(
        RecoveryVectorHarness harness,
        CancellationToken token)
    {
        WireToGateRecoveryState released = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(released.ExceptionRecoverySessionId);
        Assert.Null(released.RecoveryActionId);
        Assert.Equal(AttemptId, released.UnsettledSlotOperationAttemptId);
        Assert.NotNull(released.OperationContext);

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
        Assert.Equal(0, harness.Io.UnlockCount);
    }
}
