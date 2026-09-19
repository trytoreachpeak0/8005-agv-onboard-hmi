using System.Text.Json;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
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

    private static readonly int[] ResumeSlots = [1, 2];

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
    /// A resend of the same resume is answered with the rejection already on file, byte for byte,
    /// even though the vehicle would refuse it for another reason by now. Its ack was lost, so the
    /// vehicle does send it again -- which is what makes the answer visible here.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResentResumeIsAnsweredWithTheSameRejection()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.SlotOperationCommandRejectedAcksToDrop = 1,
            cargoInTargetSlots: true);
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
        object resume = ResumePayload(
            state,
            checkpoint: AnotherCheckpointThan(state.ProvenRecoveryCheckpoint));

        await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, resume);
        await WaitForSingleRejectionAsync(harness, token);
        // The server resends later, not while the first copy is still waiting for its ack: that wait
        // ends when the onboard gives up on the ack and logs it.
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Message.StartsWith("续行命令的拒绝暂未", StringComparison.Ordinal)),
            "the onboard to stop waiting for the dropped ack",
            token);
        // Refused again now, the reason would be RECOVERY_AUTHENTICATION_FAILED.
        await harness.RewriteRecoveryStateAsync(
            persisted => persisted with { OperationContext = null },
            token);
        await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, resume);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => Rejections(harness).Count == 2,
            "the unacknowledged rejection to be sent again for the resent resume",
            token);
        IReadOnlyList<JsonElement> rejections = Rejections(harness);
        Assert.Single(rejections.Select(item => item.GetProperty("messageId").GetString()).Distinct());
        Assert.All(rejections, item =>
        {
            Assert.Equal(ResumeMessageId, item.GetProperty("correlationId").GetString());
            Assert.Equal(
                "RECOVERY_SESSION_NOT_OPEN",
                item.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        });
        Assert.Equal(
            rejections[0].GetProperty("payload").GetRawText(),
            rejections[1].GetProperty("payload").GetRawText());
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// A resume refused once stays refused. The server may have closed its resume workflow on that
    /// rejection, so a resend that the safety gate would now let through must not open a door.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeRefusedOnceIsNotRunOnAResendTheGateWouldNowAllow()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(token);
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
        object resume = ResumePayload(state);

        harness.VehicleMotionUnknown();
        await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, resume);
        await harness.WaitForRecoveryBlockedAsync("ACTION_NOT_ALLOWED_IN_STATE", token);
        await WaitForSingleRejectionAsync(harness, token);

        harness.VehicleStopped();
        await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, resume);

        // Were the resend run, the slots are empty and locked, so the load would pulse slot 1 at once.
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Io.UnlockCount > 0
                || harness.Logger.Entries.Any(entry => entry.Message.StartsWith(
                    "收到已拒绝过的SlotOperationResumeCommand", StringComparison.Ordinal)),
            "the resent resume to be either run or refused on the rejection on file",
            token);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Single(Rejections(harness)
            .Select(item => item.GetProperty("messageId").GetString())
            .Distinct());
    }

    /// <summary>
    /// The original <c>SlotOperationCommand</c> and a later resume of the same attempt are refused
    /// with the same reason. The original's rejection is keyed by attempt and reason alone, so a
    /// resume rejection keyed the same way would find it, and never be sent.
    /// </summary>
    /// <remarks>
    /// The original rejection is seeded, unacknowledged, into the journal the vehicle starts from:
    /// exactly the row <c>SendOperationRejectedAsync</c> writes. Letting the vehicle write it itself
    /// is not possible here -- that path only runs while the session is not READY, and its send
    /// refuses anything but READY, so it saves nothing.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task TheOriginalCommandsRejectionAndTheResumesRejectionDoNotShareAKey()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string reasonCode = "ACTION_NOT_ALLOWED_IN_STATE";
        string journalPath = Path.Combine(
            Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        await SeedOriginalCommandRejectionAsync(journalPath, reasonCode, token);

        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            journalPath: journalPath);
        // The handshake replays the seeded rejection.
        await WaitForSingleRejectionAsync(harness, token);
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);

        harness.VehicleMotionUnknown();
        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(state));
        await harness.WaitForRecoveryBlockedAsync(reasonCode, token);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => Rejections(harness).Count == 2,
            "the resume's rejection to be sent beside the original command's",
            token);
        IReadOnlyList<JsonElement> rejections = Rejections(harness);
        Assert.Equal(
            new[] { CommandMessageId, ResumeMessageId },
            rejections.Select(item => item.GetProperty("correlationId").GetString()!).ToArray());
        Assert.Equal(2, rejections.Select(item => item.GetProperty("messageId").GetString()).Distinct().Count());
        Assert.All(rejections, item =>
        {
            JsonElement payload = item.GetProperty("payload");
            Assert.Equal(AttemptId, payload.GetProperty("slotOperationAttemptId").GetString());
            Assert.Equal(reasonCode, payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        });
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// The rejection ends the resume on the vehicle's side, and the server closes the session on it
    /// (control-server#187). The vehicle must then be able to open another session for the same
    /// unsettled load: found on the real rig, where the closed session's id stayed in the journal
    /// and every recovery entry refused locally with RECOVERY_SESSION_STATE_PENDING.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AfterARejectedResumeClosesItsSessionTheVehicleCanOpenAnother()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        // The real server names the session's attempt in every recovery response and snapshot alike.
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = AttemptId);
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);

        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(state, checkpoint: AnotherCheckpointThan(state.ProvenRecoveryCheckpoint)));
        await WaitForSingleRejectionAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "ExceptionRecoverySessionSnapshot",
            "abcdabcd-0000-4000-8000-000000000120",
            new
            {
                exceptionRecoverySessionId = state.ExceptionRecoverySessionId,
                recoverySessionRevision = 3,
                state = "CLOSED",
                administratorId = "maintenance-001",
                administratorRole = "MAINTENANCE_ADMINISTRATOR",
                eventId = state.RecoverySessionRequestId,
                demandId = DemandId,
                slotOperationAttemptId = AttemptId,
                slots = ResumeSlots,
                selectedAction = "RESUME_AFTER_REPAIR",
                allowedActions = Array.Empty<string>(),
                blockingFacts = Array.Empty<object>()
            });
        await harness.WaitForInboundAsync("SnapshotAppliedAck", token);
        WireToGateRecoveryState afterClose = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(AttemptId, afterClose.UnsettledSlotOperationAttemptId);
        Assert.NotNull(afterClose.OperationContext);

        bool requested = await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token);

        Assert.True(requested, string.Join(" / ", harness.Logger.Entries
            .Where(entry => entry.Severity >= Core.LogSeverity.Warning)
            .Select(entry => entry.Message)
            .TakeLast(3)));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ResultsOfType("ExceptionRecoverySessionRequested").Count == 2,
            "a second recovery session request for the same vehicle",
            token);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// A restart sends no second rejection: not on its own, and not when the server issues the same
    /// resume again to the restarted vehicle, whose capability revision has moved on in between.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ARestartSendsNoFurtherRejectionForAResumeAlreadyRejected()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Path.Combine(
            Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        object resume;
        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            cargoInTargetSlots: true))
        {
            WireToGateRecoveryState state = await OpenResumeActionAsync(beforeRestart, token);
            resume = ResumePayload(
                state,
                checkpoint: AnotherCheckpointThan(state.ProvenRecoveryCheckpoint));
            await server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, resume);
            await WaitForSingleRejectionAsync(beforeRestart, token);
            await RecoveryVectorHarness.WaitUntilAsync(
                () => server.SentEnvelopes.Any(item => item.MessageType == "DurableAck"
                    && item.WireLine.Contains("SlotOperationCommandRejected", StringComparison.Ordinal)),
                "the rejection to be acknowledged before the restart",
                token);
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            baselineRevision: 2,
            restart: true,
            cargoInTargetSlots: true);
        int blockedBefore = afterRestart.RecoveryBlockedCount;
        await serverAfterRestart.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, resume);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.RecoveryBlockedCount > blockedBefore,
            "the restarted vehicle to refuse the resent resume",
            token);

        // One rejection identity across both sessions. The same messageId may arrive again after the
        // restart: when the restart beat the vehicle's recording of the ack, the handshake replays the
        // stored line, which is the protocol's own replay duty and not a further rejection.
        string?[] identities =
        [
            .. server.ReceivedEnvelopes
                .Where(item => item.MessageType == "SlotOperationCommandRejected")
                .Select(item => item.MessageId),
            .. Rejections(afterRestart).Select(item => item.GetProperty("messageId").GetString())
        ];
        Assert.NotEmpty(identities);
        Assert.Single(identities.Distinct());
        Assert.All(Rejections(afterRestart), item => Assert.Equal(
            "RECOVERY_SESSION_NOT_OPEN",
            item.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString()));
        Assert.Equal(0, afterRestart.Io.UnlockCount);
    }

    /// <summary>
    /// A resume that fails after its first pulse is not refused: the door may be open, and the
    /// server learns what happened from the result the existing settlement reports, not from a
    /// rejection that would claim nothing was done.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeThatFailsAfterItsFirstPulseIsSettledNotRejected()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            lockerWaitTimesOut: true);
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
        int resultsBefore = harness.ResultsOfType("OperationResult").Count;

        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(state));

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ResultsOfType("OperationResult").Count > resultsBefore,
            "the interrupted resume to be settled and reported",
            token);
        using JsonDocument result = JsonDocument.Parse(harness.ResultsOfType("OperationResult")[^1]);
        Assert.Equal(
            "UNKNOWN",
            result.RootElement.GetProperty("payload").GetProperty("overallOutcome").GetString());
        Assert.Equal(1, harness.Io.UnlockCount);
        Assert.Empty(Rejections(harness));
    }

    /// <summary>
    /// Writes the row <c>SendOperationRejectedAsync</c> saves before sending: key
    /// <c>slot-operation-rejected:{attempt}:{reason}</c>, messageId the attempt id, correlated to the
    /// original command.
    /// </summary>
    private static async Task SeedOriginalCommandRejectionAsync(
        string journalPath,
        string reasonCode,
        CancellationToken token)
    {
        SqliteWireToGateJournal journal = new(journalPath);
        await journal.InitializeAsync(token);
        WireToGateEnvelope envelope = WireToGateProtocolSerializer.Create(
            "SlotOperationCommandRejected",
            AttemptId,
            CommandMessageId,
            "AGV-8005-01",
            1,
            DateTimeOffset.UtcNow,
            new SlotOperationCommandRejectedPayload(
                AttemptId,
                new WireToGateProblemPayload(reasonCode, null, null),
                1,
                null));
        await journal.SaveOutgoingBeforeSendAsync(
            new WireToGateDurableMessage(
                $"slot-operation-rejected:{AttemptId}:{reasonCode}",
                "SlotOperationCommandRejected",
                AttemptId,
                WireToGateProtocolSerializer.ComputeContentSha256(envelope),
                WireToGateProtocolSerializer.SerializeLine(envelope),
                DateTimeOffset.UtcNow,
                false),
            token);
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
