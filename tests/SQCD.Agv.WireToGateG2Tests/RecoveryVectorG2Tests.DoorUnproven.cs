using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The onboard half of <c>CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN</c> and <c>CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE</c>
/// (CP-0009, REQ-0357, REQ-0364, 8005-agv-onboard-hmi#219), driven through the real session and business services
/// against the fake control server.
/// </summary>
/// <remarks>
/// <para>
/// Slot 1's lock feedback cannot be read when the compensation starts, while its light curtain reads EMPTY: the
/// door is not proven, and opening it again would prove nothing. Slot 2 is empty, locked and reset. So the clear
/// settles at once, no door opened, and the server holds both target slots.
/// </para>
/// <para>
/// The release then runs on the same vehicle: a session with no demand over the held slots, the
/// <c>HARDWARE_REPAIR_RELEASE</c> action (no command follows it, no door is opened), a restart that must bring the
/// repair record form back, the record on the release action, the fresh readings, and one <c>SAFE</c>
/// <c>HOLD_RELEASE</c> answer.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    private static readonly int[] HeldSlotsOneAndTwo = [1, 2];

    /// <summary>
    /// <c>REPORT_LOCK_AND_OUTPUT_STATE_AS_READ</c>, <c>NEVER_OPEN_ANY_SLOT_AFTER_DOOR_UNPROVEN</c>,
    /// <c>JOURNAL_DOOR_UNPROVEN_RESULT_BEFORE_SENDING</c> and <c>DISPLAY_REPAIR_REQUIRED_NOTICE</c>: the result is
    /// the new value with each slot as read, it is on file before it goes out, no door opens, the business side is
    /// settled once it is acknowledged, and the hold the server publishes afterwards is acknowledged and shown.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    public async Task ACompensationOverAnEmptySlotWhoseLockCannotBeProvenSettlesAllEmptyDoorUnproven()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            loadAlreadySettled: true);

        JsonElement result = await CompensateOverAnUnreadableLockAsync(harness, token);

        Assert.Equal("ALL_EMPTY_DOOR_UNPROVEN", result.GetProperty("overallOutcome").GetString());
        JsonElement[] slots = [.. result.GetProperty("slotResults").EnumerateArray()];
        Assert.Equal(
            ("FAILED", "EMPTY", "UNKNOWN", "RESET"),
            SlotFields(Assert.Single(slots, slot => slot.GetProperty("slotNo").GetInt32() == 1)));
        Assert.Equal(
            ["SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY"],
            slots[0].GetProperty("reasonCodes").EnumerateArray().Select(code => code.GetString()));
        Assert.Equal(
            ("COMPLETED", "EMPTY", "LOCKED", "RESET"),
            SlotFields(Assert.Single(slots, slot => slot.GetProperty("slotNo").GetInt32() == 2)));
        Assert.Equal(0, harness.Io.UnlockCount);

        // Journaled before it went out: the outbox row the vehicle sent from is the result itself.
        string messageId = Assert.Single(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.MessageType == "LoadCompensationResult").MessageId;
        WireToGateDurableMessage? onFile = await harness.ReadOutgoingAsync(
            $"recovery-vector-result:{WireToGateRecoveryVectorTypes.LoadCompensation}:{ActionIdFor(CompensateLoadAction)}",
            token);
        Assert.Equal(messageId, onFile?.MessageId);

        // The hold arrives after the result's DurableAck, is acknowledged, and is what the vehicle shows.
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.DoorHeldSlots.SequenceEqual([1, 2]),
            "the server's hold on both target slots to reach the vehicle",
            token);
        string[] received = [.. harness.Server.Received.Select(item => item.MessageType)];
        int resultAt = Array.IndexOf(received, "LoadCompensationResult");
        Assert.True(Array.IndexOf(received, "ExceptionRecoverySessionRequested") < resultAt);
        Assert.True(Array.IndexOf(received, "RecoveryActionSubmitted") < resultAt);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(envelope =>
            {
                if (envelope.MessageType != "SnapshotAppliedAck")
                {
                    return false;
                }

                using JsonDocument ack = JsonDocument.Parse(envelope.WireLine);
                JsonElement applied = ack.RootElement.GetProperty("payload");
                return applied.GetProperty("snapshotKind").GetString() == "VEHICLE_BUSINESS_STATE"
                    && applied.GetProperty("appliedRevision").GetInt64() > 500;
            }),
            "the vehicle to acknowledge the business state carrying the hold",
            token);

        WireToGateRecoveryState settled = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(settled.RecoveryVector);
        Assert.Null(settled.UnsettledSlotOperationAttemptId);
        Assert.Null(settled.ExceptionRecoverySessionId);
        WireToGateOperatorEvent told = Assert.Single(
            harness.OperatorEvents,
            item => item.Kind == "RECOVERY_VECTOR_DOOR_UNPROVEN");
        Assert.Contains("1号仓：仓已确认无货，门锁未锁闭，本车需维修后才能继续。", told.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>REPLAY_SAME_DOOR_UNPROVEN_RESULT_AFTER_RESTART</c>: the result's DurableAck is lost and the vehicle restarts
    /// with the lock fixed in between. The result goes out again under the same messageId with the same payload --
    /// the new value, slot 1 still reported as it read when it settled -- and no door is opened.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    public async Task ADoorUnprovenCompensationResultIsSentAgainUnchangedAfterARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = NewRestartJournalPath();
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.LoadCompensationResultAcksToDrop = 1;
        string firstLine;
        string firstMessageId;
        await using (RecoveryVectorHarness first = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            loadAlreadySettled: true))
        {
            await CompensateOverAnUnreadableLockAsync(first, token);
            (_, _, firstMessageId, firstLine) = Assert.Single(
                server.ReceivedEnvelopes,
                envelope => envelope.MessageType == "LoadCompensationResult");
        }

        // The server after the restart is a new process too, with what it had made durable.
        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            restart: true,
            loadAlreadySettled: true);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => serverAfterRestart.ReceivedEnvelopes.Any(envelope => envelope.MessageType == "LoadCompensationResult"),
            "the unacknowledged result to be sent again after the restart",
            token);

        (_, _, string againMessageId, string againLine) = serverAfterRestart.ReceivedEnvelopes
            .First(envelope => envelope.MessageType == "LoadCompensationResult");
        Assert.Equal(firstMessageId, againMessageId);
        Assert.Equal(PayloadText(firstLine), PayloadText(againLine));
        Assert.Contains("ALL_EMPTY_DOOR_UNPROVEN", PayloadText(againLine), StringComparison.Ordinal);
        Assert.Equal(0, afterRestart.Io.UnlockCount);
    }

    /// <summary>
    /// The release, end to end on the vehicle (<c>CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE</c>):
    /// <c>OPEN_RELEASE_SESSION_OVER_HELD_SLOTS_WITHOUT_DEMAND</c>, <c>JOURNAL_RELEASE_ACTION_BEFORE_SUBMITTING</c>,
    /// <c>RESTORE_REPAIR_RECORD_FORM_AFTER_RESTART</c>, <c>SUBMIT_HARDWARE_RECORD_ON_RELEASE_ACTION</c>,
    /// <c>REPORT_FRESH_SLOT_READINGS_ON_REQUEST</c>, <c>ANSWER_HOLD_RELEASE_CHECK_WITHOUT_DEMAND_OR_LEG</c>,
    /// <c>DISPLAY_HOLD_FROM_BLOCKING_FACTS</c> and <c>NEVER_SLOT_IO_FOR_REPAIR_RELEASE</c>.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task AHoldForAnUnprovenDoorIsReleasedThroughTheRepairRecordAndASafeHoldReleaseCheck()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = NewRestartJournalPath();
        FakeControlServer server = RecoveryVectorHarness.NewServer();
        await using FakeControlServer firstServer = server;
        string releaseActionId;
        await using (RecoveryVectorHarness first = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            loadAlreadySettled: true))
        {
            await CompensateOverAnUnreadableLockAsync(first, token);
            await RecoveryVectorHarness.WaitUntilAsync(
                () => first.Business.CanRequestHardwareRepairRelease,
                "the repair release entry over the held slots",
                token);
            // The lock is repaired; nothing here is opened for it.
            first.Io.CloseDoor(0, cargo: false);

            Assert.True(await first.Business.RequestHardwareRepairReleaseAsync("1号仓锁体已更换。", token));

            using JsonDocument request = JsonDocument.Parse(
                first.ResultsOfType("ExceptionRecoverySessionRequested")[^1]);
            JsonElement requested = request.RootElement.GetProperty("payload");
            Assert.Equal(JsonValueKind.Null, requested.GetProperty("demandId").ValueKind);
            Assert.Equal([1, 2], requested.GetProperty("slots").EnumerateArray().Select(slot => slot.GetInt32()));
            using JsonDocument action = JsonDocument.Parse(first.ResultsOfType("RecoveryActionSubmitted")[^1]);
            JsonElement submitted = action.RootElement.GetProperty("payload");
            Assert.Equal("HARDWARE_REPAIR_RELEASE", submitted.GetProperty("action").GetString());
            Assert.Equal(JsonValueKind.Null, submitted.GetProperty("demandId").ValueKind);
            releaseActionId = submitted.GetProperty("recoveryActionId").GetString()!;

            WireToGateRecoveryState journaled = await first.ReadRecoveryStateAsync(token);
            Assert.Equal(releaseActionId, journaled.RepairRelease?.RecoveryActionId);
            Assert.True(journaled.RepairRelease!.Accepted);
            Assert.Equal(RecoverySessionId, journaled.RepairRelease.ExceptionRecoverySessionId);
            Assert.True(first.Business.CanSubmitHardwareRecoveryRecord);
            Assert.False(first.Business.CanRequestHardwareRepairRelease);
            Assert.Equal(0, first.Io.UnlockCount);
        }

        // The vehicle restarts before the record is made: the form is back, over the same release action.
        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        server = serverAfterRestart;
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            restart: true,
            loadAlreadySettled: true);
        // The server pushes its business state again on the new session, the hold still in it.
        await server.PublishDoorHoldAsync([1, 2]);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Business.CanSubmitHardwareRecoveryRecord,
            "the repair record form to be offered again after the restart",
            token);

        Assert.True(
            await afterRestart.Business.SubmitHardwareRecoveryRecordAsync("更换 1 号仓锁体，复测锁反馈正常。", token),
            string.Join(" / ", afterRestart.Logger.Entries
                .Where(entry => entry.Severity >= LogSeverity.Warning)
                .Select(entry => entry.Message)
                .TakeLast(3)));

        using (JsonDocument recordDocument = JsonDocument.Parse(
            Assert.Single(afterRestart.ResultsOfType("HardwareRecoveryRecordSubmitted"))))
        {
            JsonElement record = recordDocument.RootElement.GetProperty("payload");
            Assert.Equal(releaseActionId, record.GetProperty("recoveryActionId").GetString());
            Assert.Equal(RecoverySessionId, record.GetProperty("exceptionRecoverySessionId").GetString());
            Assert.Equal([1, 2], record.GetProperty("slots").EnumerateArray().Select(slot => slot.GetInt32()));
        }

        Assert.Null((await afterRestart.ReadRecoveryStateAsync(token)).RepairRelease);
        Assert.False(afterRestart.Business.CanSubmitHardwareRecoveryRecord);

        // The server closes the session and asks for fresh readings.
        await server.SendCommandAsync(
            "ExceptionRecoverySessionSnapshot",
            Guid.NewGuid().ToString("D"),
            new
            {
                exceptionRecoverySessionId = RecoverySessionId,
                recoverySessionRevision = 6,
                state = "CLOSED",
                administratorId = "maintenance-001",
                administratorRole = "MAINTENANCE_ADMINISTRATOR",
                eventId = Guid.NewGuid().ToString("D"),
                demandId = (string?)null,
                slotOperationAttemptId = (string?)null,
                slots = HeldSlotsOneAndTwo,
                selectedAction = "HARDWARE_REPAIR_RELEASE",
                allowedActions = Array.Empty<string>(),
                blockingFacts = Array.Empty<object>(),
                closedReason = (string?)null
            });
        int snapshotsBefore = afterRestart.Server.ReceivedEnvelopes.Count(item => item.MessageType == "SafetyStateSnapshot");
        await server.RequestSafetyStateSnapshotAsync();
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Server.ReceivedEnvelopes.Count(item => item.MessageType == "SafetyStateSnapshot")
                > snapshotsBefore,
            "the fresh readings the server asked for",
            token);
        using JsonDocument readings = JsonDocument.Parse(
            afterRestart.Server.ReceivedEnvelopes.Last(item => item.MessageType == "SafetyStateSnapshot").WireLine);
        long version = readings.RootElement.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64();

        string checkId = Guid.NewGuid().ToString("D");
        await server.SendCommandAsync(
            "PreDepartureSafetyCheck",
            Guid.NewGuid().ToString("D"),
            new
            {
                preDepartureSafetyCheckId = checkId,
                checkPurpose = "HOLD_RELEASE",
                demandId = (string?)null,
                movementLegId = (string?)null,
                expectedSafetyStateVersion = version,
                targetStationId = (string?)null
            });
        JsonElement answer = await afterRestart.WaitForResultAsync("PreDepartureSafetyCheckResult", token);
        Assert.Equal(checkId, answer.GetProperty("preDepartureSafetyCheckId").GetString());
        Assert.Equal("HOLD_RELEASE", answer.GetProperty("checkPurpose").GetString());
        Assert.Equal("SAFE", answer.GetProperty("outcome").GetString());

        await server.PublishDoorHoldAsync([]);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Business.DoorHeldSlots.Count == 0,
            "the lifted hold to reach the vehicle",
            token);
        Assert.False(afterRestart.Business.CanRequestHardwareRepairRelease);
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        Assert.DoesNotContain(server.Received, item => item.MessageType == "ProtocolProblem");
    }

    /// <summary>
    /// <c>release-session-stuck-after-onboard-restart</c>, the lost answer (PR #248 review, must-fix 1): the release's
    /// session opens on the server but its <c>ExceptionRecoverySessionOpened</c> never arrives, and the server then
    /// shows the session OPEN. The next press asks again under the same <c>requestId</c> with the same payload, so the
    /// server answers with the session it already holds, and the release goes through. Each send is a message of its own.
    /// </summary>
    /// <remarks>
    /// Red before the fix: the second press minted a new <c>requestId</c>, the server refused a second session while the
    /// first stood (<c>ACTION_NOT_ALLOWED_IN_STATE</c>), and the entry went grey on the OPEN snapshot -- the server's
    /// session has no timeout, so the vehicle stayed held for good.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task ARepairReleaseWhoseOpenedAnswerWasLostIsAskedAgainUnderTheSameRequest()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            loadAlreadySettled: true);
        await HoldBothSlotsAsync(harness, token);
        harness.Server.ModelOneOpenRecoverySession = true;
        harness.Server.RecoverySessionOpenedRepliesToLose = 1;

        Assert.False(await harness.Business.RequestHardwareRepairReleaseAsync("1号仓锁体已更换。", token));
        await ShowTheReleaseSessionOpenAsync(harness, harness.Server, harness.ResultsOfType("ExceptionRecoverySessionRequested")[0]);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanRequestHardwareRepairRelease,
            "the entry to stay open over the release's own OPEN session",
            token);

        Assert.True(await harness.Business.RequestHardwareRepairReleaseAsync("另一段理由，不应被发出。", token));

        AssertTheSameRequestWasRepeated(harness.Server, harness.Server);
        Assert.True((await harness.ReadRecoveryStateAsync(token)).RepairRelease?.Accepted);
        Assert.True(harness.Business.CanSubmitHardwareRecoveryRecord);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// The same, across a restart between the two journal writes of the request (PR #248 review, must-fix 1): the
    /// release is on file with no session id, the server holds the session, and the vehicle comes back up. The press
    /// after the restart repeats the request on file and the release goes through.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task ARepairReleaseInterruptedBetweenItsTwoJournalWritesGoesThroughAfterARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = NewRestartJournalPath();
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        await using (RecoveryVectorHarness first = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            loadAlreadySettled: true))
        {
            await HoldBothSlotsAsync(first, token);
            server.ModelOneOpenRecoverySession = true;
            server.RecoverySessionOpenedRepliesToLose = 1;
            Assert.False(await first.Business.RequestHardwareRepairReleaseAsync("1号仓锁体已更换。", token));
            WireToGateRecoveryState interrupted = await first.ReadRecoveryStateAsync(token);
            Assert.NotNull(interrupted.RepairRelease);
            Assert.Null(interrupted.RepairRelease!.ExceptionRecoverySessionId);
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        serverAfterRestart.ModelOneOpenRecoverySession = true;
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            restart: true,
            loadAlreadySettled: true);
        await serverAfterRestart.PublishDoorHoldAsync([1, 2]);
        await ShowTheReleaseSessionOpenAsync(
            afterRestart,
            serverAfterRestart,
            server.ReceivedEnvelopes.First(item => item.MessageType == "ExceptionRecoverySessionRequested").WireLine);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Business.CanRequestHardwareRepairRelease,
            "the entry over the release on file after the restart",
            token);

        Assert.True(await afterRestart.Business.RequestHardwareRepairReleaseAsync("另一段理由，不应被发出。", token));

        AssertTheSameRequestWasRepeated(server, serverAfterRestart);
        Assert.True((await afterRestart.ReadRecoveryStateAsync(token)).RepairRelease?.Accepted);
        Assert.Equal(0, afterRestart.Io.UnlockCount);
    }

    /// <summary>Compensates over an unreadable lock, waits for the hold, and repairs the lock.</summary>
    private static async Task HoldBothSlotsAsync(RecoveryVectorHarness harness, CancellationToken token)
    {
        await CompensateOverAnUnreadableLockAsync(harness, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanRequestHardwareRepairRelease,
            "the repair release entry over the held slots",
            token);
        harness.Io.CloseDoor(0, cargo: false);
    }

    /// <summary>
    /// What the server publishes once the release's session is open: OPEN, no demand, the held slots. Returns once the
    /// vehicle has stored it -- the OPEN revision is not acknowledged, so the operator event published after the store
    /// is the signal -- and an entry read afterwards is read over that snapshot.
    /// </summary>
    private static async Task ShowTheReleaseSessionOpenAsync(
        RecoveryVectorHarness harness,
        FakeControlServer server,
        string requestLine)
    {
        using JsonDocument request = JsonDocument.Parse(requestLine);
        JsonElement payload = request.RootElement.GetProperty("payload");
        int shownBefore = OpenSessionsShown(harness);
        await server.SendCommandAsync(
            "ExceptionRecoverySessionSnapshot",
            Guid.NewGuid().ToString("D"),
            new
            {
                exceptionRecoverySessionId = RecoverySessionId,
                recoverySessionRevision = 1,
                state = "OPEN",
                administratorId = "maintenance-001",
                administratorRole = "MAINTENANCE_ADMINISTRATOR",
                eventId = payload.GetProperty("eventId").GetString(),
                demandId = (string?)null,
                slotOperationAttemptId = (string?)null,
                slots = HeldSlotsOneAndTwo,
                selectedAction = (string?)null,
                allowedActions = ReleaseActions,
                blockingFacts = Array.Empty<object>(),
                closedReason = (string?)null
            });
        await RecoveryVectorHarness.WaitUntilAsync(
            () => OpenSessionsShown(harness) > shownBefore,
            "the vehicle to take the release's OPEN session snapshot",
            CancellationToken.None);
    }

    private static int OpenSessionsShown(RecoveryVectorHarness harness) =>
        harness.OperatorEvents.Count(item => item.Kind == "RECOVERY_SESSION_UPDATED"
            && item.Message.Contains("OPEN", StringComparison.Ordinal));

    private static readonly string[] ReleaseActions = ["FORCED_MECHANICAL_RECOVERY", "HARDWARE_REPAIR_RELEASE"];

    /// <summary>
    /// The two session requests carry one requestId and one payload byte for byte, under two messageIds, and the server
    /// refused none of them.
    /// </summary>
    private static void AssertTheSameRequestWasRepeated(FakeControlServer first, FakeControlServer second)
    {
        string[] requests =
        [
            .. first.ReceivedEnvelopes
                .Where(item => item.MessageType == "ExceptionRecoverySessionRequested")
                .Select(item => item.WireLine),
            .. ReferenceEquals(first, second)
                ? []
                : second.ReceivedEnvelopes
                    .Where(item => item.MessageType == "ExceptionRecoverySessionRequested")
                    .Select(item => item.WireLine)
        ];
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(MessageIdOf(requests[0]), MessageIdOf(requests[1]));
        Assert.Equal(PayloadText(requests[0]), PayloadText(requests[1]));
        Assert.Empty(first.RejectedRecoverySessionRequests);
        Assert.Empty(second.RejectedRecoverySessionRequests);
        Assert.Contains(
            second.ReceivedEnvelopes,
            item => item.MessageType == "RecoveryActionSubmitted"
                && item.WireLine.Contains("HARDWARE_REPAIR_RELEASE", StringComparison.Ordinal));
    }

    private static string? MessageIdOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("messageId").GetString();
    }

    /// <summary>
    /// Asks for the compensation of the seeded settled load with slot 1's lock feedback unreadable and its light
    /// curtain EMPTY, and returns the result's payload.
    /// </summary>
    private static async Task<JsonElement> CompensateOverAnUnreadableLockAsync(
        RecoveryVectorHarness harness,
        CancellationToken token)
    {
        harness.Io.SetUnreadable(0);
        Assert.True(harness.Business.CanRequestLoadCompensation);
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        return await harness.WaitForResultAsync("LoadCompensationResult", token);
    }

    private static (string?, string?, string?, string?) SlotFields(JsonElement slot) =>
        (slot.GetProperty("outcome").GetString(),
            slot.GetProperty("finalPhysicalState").GetString(),
            slot.GetProperty("lockState").GetString(),
            slot.GetProperty("unlockOutputState").GetString());

    private static string PayloadText(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("payload").GetRawText();
    }
}
