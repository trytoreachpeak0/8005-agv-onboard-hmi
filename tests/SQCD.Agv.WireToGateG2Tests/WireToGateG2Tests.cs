using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed class WireToGateG2Tests
{
    private const string CredentialVariable = "W2G_G2_TEST_CREDENTIAL";

    static WireToGateG2Tests()
    {
        Environment.SetEnvironmentVariable(CredentialVariable, "g2-test-credential");
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public async Task HappyPathCompletesFullSequenceAndBecomesReady()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        string journalPath = NewJournalPath();
        FakeIoModuleClient io = new();
        await using SqliteWireToGateJournal journal = new(journalPath);
        await using WireToGateSessionClient client = CreateClient(server, io, journalPath);

        await journal.InitializeAsync(testToken);
        WireToGateSessionSnapshot snapshot =
            await client.ConnectAndRecoverAsync(testToken);

        Assert.Equal(WireToGateSessionReadiness.Ready, snapshot.Readiness);
        Assert.True(client.IsReady);
        Assert.All(server.IdentityValidationResults, result => Assert.Equal("PASS", result));
        Assert.Equal(
            ["SessionHello", "CapabilitySnapshot", "SafetyStateSnapshot", "RecoveryStateReport"],
            InboundMessageTypes(server));
        Assert.Equal(0, io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    public async Task ReconnectDuringRecoveryRebindsDurableReportWithoutUnlockSideEffects()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            DropBeforeRecoveryAck = true,
            InitialAcceptedCapabilityVersion = 1,
            InitialAcceptedSafetyStateVersion = 1
        };
        string journalPath = NewJournalPath();
        FakeIoModuleClient io = new();
        await using SqliteWireToGateJournal journal = new(journalPath);
        await using WireToGateSessionClient client = CreateClient(server, io, journalPath);

        await journal.InitializeAsync(testToken);
        string originalMessageId = Guid.NewGuid().ToString("D");
        WireToGateEnvelope originalReport = WireToGateProtocolSerializer.Create(
            "RecoveryStateReport",
            originalMessageId,
            null,
            "AGV-8005-01",
            1,
            DateTimeOffset.UtcNow,
            new
            {
                reportId = Guid.NewGuid().ToString("D"),
                observedAt = DateTimeOffset.UtcNow,
                unsettledSlotOperationAttemptId = (string?)null,
                provenRecoveryCheckpoint = "NONE",
                activeUnlockSlots = Array.Empty<int>(),
                forcedRecoveryGeneration = 0,
                pendingResults = Array.Empty<object>(),
                journalContentSha256 = new string('0', 64)
            });
        string originalWireLine = WireToGateProtocolSerializer.SerializeLine(originalReport);
        string originalContentSha256 = WireToGateProtocolSerializer.ComputeContentSha256(originalReport);
        await journal.SaveOutgoingBeforeSendAsync(new WireToGateDurableMessage(
            "recovery:0:interrupted",
            "RecoveryStateReport",
            originalMessageId,
            originalContentSha256,
            originalWireLine,
            DateTimeOffset.UtcNow,
            false), testToken);

        await Assert.ThrowsAnyAsync<IOException>(
            () => client.ConnectAndRecoverAsync(testToken));

        server.DropBeforeRecoveryAck = false;
        server.SendReadinessAfterRecoveryAck = true;
        WireToGateSessionSnapshot resumed = await client.ConnectAndRecoverAsync(testToken);

        Assert.Equal(WireToGateSessionReadiness.Ready, resumed.Readiness);

        string[] received = server.Received.Select(item => item.MessageType).ToArray();
        Assert.Equal(
            ["CONNECTED", "SessionHello", "RecoveryStateReport", "CONNECTED", "SessionHello", "RecoveryStateReport"],
            received);
        Assert.DoesNotContain("CapabilitySnapshot", received);
        Assert.Equal(0, io.UnlockCount);

        var replayed = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "RecoveryStateReport")
            .ToArray();
        Assert.Equal(2, replayed.Length);
        Assert.All(replayed, item => Assert.Equal(originalMessageId, item.MessageId));
        WireToGateEnvelope[] replayedEnvelopes = replayed
            .Select(item => WireToGateProtocolSerializer.DeserializeAndValidate(item.WireLine, "AGV-8005-01"))
            .ToArray();
        Assert.Equal([1L, 2L], replayedEnvelopes.Select(item => item.SessionGeneration).ToArray());
        Assert.All(replayedEnvelopes, item => Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(originalReport.Payload.GetRawText()),
            JsonNode.Parse(item.Payload.GetRawText()))));
        Assert.Equal(
            originalContentSha256,
            WireToGateProtocolSerializer.ComputeContentSha256(replayedEnvelopes[0]));
        string reboundContentSha256 = WireToGateProtocolSerializer.ComputeContentSha256(replayedEnvelopes[1]);
        Assert.NotEqual(originalContentSha256, reboundContentSha256);
        Assert.NotEqual(replayed[0].WireLine, replayed[1].WireLine);
        Assert.Empty(server.StaleGenerationRejections);

        WireToGateDurableMessage? stored = await journal.ReadOutgoingByDeduplicationKeyAsync(
            "recovery:0:interrupted",
            testToken);
        Assert.NotNull(stored);
        Assert.True(stored!.Acknowledged);
        Assert.Equal(reboundContentSha256, stored.ContentSha256);
        Assert.Equal(replayed[1].WireLine + "\n", stored.WireLine);
        Assert.Empty(await journal.ReadUnacknowledgedOutgoingAsync(testToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public async Task SnapshotReplaceAndAckAcceptsHigherRevisionOnNewConnection()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        string journalPath = NewJournalPath();
        FakeIoModuleClient io = new();

        await using SqliteWireToGateJournal firstJournal = new(journalPath);
        _ = firstJournal;
        await using WireToGateSessionClient firstClient = CreateClient(server, io, journalPath);
        await firstClient.ConnectAndRecoverAsync(testToken);
        await firstClient.DisposeAsync();

        await using WireToGateSessionClient secondClient =
            CreateClient(server, io, journalPath, capability: 2, safety: 2);
        WireToGateSessionSnapshot snapshot = await secondClient.ConnectAndRecoverAsync(testToken);

        Assert.Equal(WireToGateSessionReadiness.Ready, snapshot.Readiness);
        Assert.Equal(
            [("CapabilitySnapshot", 1L), ("SafetyStateSnapshot", 1L), ("CapabilitySnapshot", 2L), ("SafetyStateSnapshot", 2L)],
            server.AppliedSnapshots.ToArray());
        Assert.Equal(0, io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public async Task SameRevisionDifferentContentFailsClosedWithProtocolProblemReasonCode()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        string journalPath = NewJournalPath();
        FakeIoModuleClient io = new();

        await using WireToGateSessionClient firstClient = CreateClient(
            server,
            io,
            journalPath,
            onboardInstanceId: "0198f1a2-7c3d-4e5f-8a9b-c0de5a7e1001");
        await firstClient.ConnectAndRecoverAsync(testToken);
        await firstClient.DisposeAsync();

        io.SetCargoPresent(3, present: true);

        await using WireToGateSessionClient secondClient = CreateClient(
            server,
            io,
            journalPath,
            onboardInstanceId: "0198f1a2-7c3d-4e5f-8a9b-c0de5a7e1001");
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => secondClient.ConnectAndRecoverAsync(testToken));

        Assert.Equal("SNAPSHOT_REVISION_CONTENT_CONFLICT", failure.Message);
        Assert.False(secondClient.IsReady);
        Assert.Equal(WireToGateSessionReadiness.Disconnected, secondClient.Current.Readiness);
        Assert.Equal(0, io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task DurableOutboxIsAcknowledgedAfterServerDurableAck()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        string journalPath = NewJournalPath();
        FakeIoModuleClient io = new();
        await using SqliteWireToGateJournal journal = new(journalPath);
        await using WireToGateSessionClient client = CreateClient(server, io, journalPath);

        await client.ConnectAndRecoverAsync(testToken);

        Assert.Empty(await journal.ReadUnacknowledgedOutgoingAsync(testToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public async Task HeartbeatIsAcknowledgedByControlServer()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        string journalPath = NewJournalPath();
        FakeIoModuleClient io = new();
        await using SqliteWireToGateJournal journal = new(journalPath);
        await using WireToGateSessionClient client = CreateClient(server, io, journalPath);

        await client.ConnectAndRecoverAsync(testToken);
        await client.SendHeartbeatAsync(testToken);

        Assert.Equal("Heartbeat", InboundMessageTypes(server).Last());
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task RecoverySessionAndActionResponsesAreCorrelatedWithoutPhysicalIo()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            RespondToRecoveryRequests = true
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);

        string requestId = "77777777-7777-4777-8777-777777777770";
        WireToGateOperatorContextPayload operatorContext = new(
            "maintenance-001",
            "CONFIGURED_PROOF",
            DateTimeOffset.UtcNow);
        ExceptionRecoverySessionOpenedPayload opened = await client
            .RequestExceptionRecoverySessionAsync(
                requestId,
                new ExceptionRecoverySessionRequestedPayload(
                    requestId,
                    operatorContext,
                    "MAINTENANCE_ADMINISTRATOR",
                    "88888888-8888-4888-8888-888888888888",
                    "99999999-9999-4999-8999-999999999999",
                    [1, 2],
                    "repair complete",
                    "test-proof"),
                testToken);

        Assert.Equal(requestId, opened.RequestId);
        Assert.Equal("77777777-7777-4777-8777-777777777777", opened.ExceptionRecoverySessionId);

        string actionId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        RecoveryActionAcceptedPayload accepted = await client.SubmitRecoveryActionAsync(
            actionId,
            new RecoveryActionSubmittedPayload(
                actionId,
                opened.ExceptionRecoverySessionId,
                "RESUME_AFTER_REPAIR",
                opened.EventId,
                opened.DemandId,
                opened.Slots,
                operatorContext,
                "repair complete"),
            testToken);

        Assert.Equal(actionId, accepted.RecoveryActionId);
        Assert.Equal("RESUME_AFTER_REPAIR", accepted.AcceptedAction);
        Assert.Equal(0, io.UnlockCount);
        Assert.Contains(server.Received, item => item.MessageType == "ExceptionRecoverySessionRequested");
        Assert.Contains(server.Received, item => item.MessageType == "RecoveryActionSubmitted");
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task ManualChargingReturnToServiceAcceptedResultIsCorrelatedToRequestedMessage()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            RespondToManualChargingReturnToServiceRequests = true,
            ManualChargingReturnToServiceVehicleBusinessStateRevision = 4
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);

        const string messageId = "11111111-1111-4111-8111-111111111116";
        const string requestId = "22222222-2222-4222-8222-222222222226";
        ManualChargingReturnToServiceResultPayload result = await client
            .RequestManualChargingReturnToServiceAsync(
                messageId,
                new ManualChargingReturnToServiceRequestedPayload(
                    requestId,
                    new WireToGateOperatorContextPayload(
                        "maintenance-001",
                        "BADGE",
                        DateTimeOffset.UtcNow),
                    "MAINTENANCE_ADMINISTRATOR",
                    "manual charging completed",
                    86.5),
                testToken);

        Assert.Equal(requestId, result.RequestId);
        Assert.Equal("RETURNED_TO_ELIGIBILITY_EVALUATION", result.Outcome);
        Assert.Null(result.Problem);
        Assert.Equal(4, result.VehicleBusinessStateRevision);

        var requestEnvelope = server.ReceivedEnvelopes
            .Single(item => item.MessageType == "ManualChargingReturnToServiceRequested");
        using JsonDocument requestDocument = JsonDocument.Parse(requestEnvelope.WireLine);
        Assert.Equal(messageId, requestEnvelope.MessageId);
        Assert.Equal(
            JsonValueKind.Null,
            requestDocument.RootElement.GetProperty("correlationId").ValueKind);
        Assert.Equal(
            requestId,
            requestDocument.RootElement.GetProperty("payload").GetProperty("requestId").GetString());
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task ManualChargingReturnToServiceRejectedResultPreservesRegisteredProblem()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            RespondToManualChargingReturnToServiceRequests = true,
            ManualChargingReturnToServiceOutcome = "REJECTED",
            ManualChargingReturnToServiceProblem = new WireToGateProblemPayload(
                "MANUAL_CHARGING_HOLD_ACTIVE",
                null,
                null)
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);

        const string messageId = "33333333-3333-4333-8333-333333333336";
        const string requestId = "44444444-4444-4444-8444-444444444446";
        ManualChargingReturnToServiceResultPayload result = await client
            .RequestManualChargingReturnToServiceAsync(
                messageId,
                new ManualChargingReturnToServiceRequestedPayload(
                    requestId,
                    new WireToGateOperatorContextPayload(
                        "maintenance-002",
                        "SESSION",
                        DateTimeOffset.UtcNow),
                    "SYSTEM_ADMINISTRATOR",
                    "manual charging completed",
                    null),
                testToken);

        Assert.Equal(requestId, result.RequestId);
        Assert.Equal("REJECTED", result.Outcome);
        Assert.NotNull(result.Problem);
        Assert.Equal("MANUAL_CHARGING_HOLD_ACTIVE", result.Problem!.ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task DuplicateManualChargingReturnToServiceResultDoesNotRaiseSecondCommand()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            RespondToManualChargingReturnToServiceRequests = true,
            ManualChargingReturnToServiceResponseCopies = 2
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());
        int receivedResultCommands = 0;
        client.ServerCommandReceived += (_, args) =>
        {
            if (args.Value.MessageType == "ManualChargingReturnToServiceResult")
            {
                Interlocked.Increment(ref receivedResultCommands);
            }
        };
        await client.ConnectAndRecoverAsync(testToken);

        const string messageId = "55555555-5555-4555-8555-555555555556";
        const string requestId = "66666666-6666-4666-8666-666666666666";
        ManualChargingReturnToServiceResultPayload result = await client
            .RequestManualChargingReturnToServiceAsync(
                messageId,
                new ManualChargingReturnToServiceRequestedPayload(
                    requestId,
                    new WireToGateOperatorContextPayload(
                        "maintenance-003",
                        "BADGE",
                        DateTimeOffset.UtcNow),
                    "MAINTENANCE_ADMINISTRATOR",
                    "manual charging completed",
                    90),
                testToken);

        Assert.Equal(requestId, result.RequestId);
        await Task.Delay(100, testToken);
        Assert.Equal(0, receivedResultCommands);

        await client.SendHeartbeatAsync(testToken);
        Assert.True(client.IsConnected);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task MissingManualChargingReturnToServiceResultTimesOutExplicitly()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(
            server,
            io,
            NewJournalPath(),
            messageTimeout: TimeSpan.FromMilliseconds(100));
        await client.ConnectAndRecoverAsync(testToken);

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.RequestManualChargingReturnToServiceAsync(
                "77777777-7777-4777-8777-777777777776",
                new ManualChargingReturnToServiceRequestedPayload(
                    "88888888-8888-4888-8888-888888888886",
                    new WireToGateOperatorContextPayload(
                        "maintenance-004",
                        "BADGE",
                        DateTimeOffset.UtcNow),
                    "MAINTENANCE_ADMINISTRATOR",
                    "manual charging completed",
                    75),
                testToken));

        Assert.True(client.IsConnected);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public async Task BusinessBootstrapsRecoveryRequestBeforeServerSnapshot()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        const string operatorVariable = "W2G_G2_RECOVERY_OPERATOR";
        const string proofVariable = "W2G_G2_RECOVERY_PROOF";
        string? previousOperator = Environment.GetEnvironmentVariable(operatorVariable);
        string? previousProof = Environment.GetEnvironmentVariable(proofVariable);
        Environment.SetEnvironmentVariable(operatorVariable, "maintenance-001");
        Environment.SetEnvironmentVariable(proofVariable, "test-proof");

        try
        {
            await using FakeControlServer server = new(IPAddress.Loopback)
            {
                RequireSafeSafetyForReadiness = true,
                SendReadinessAfterRecoveryAck = true,
                RespondToRecoveryRequests = true
            };
            FakeIoModuleClient io = new();
            NullLogger logger = new();
            string journalPath = NewJournalPath();
            await using WireToGateSessionService session = new(
                CreateSessionOptions(server),
                io,
                new SqliteWireToGateJournal(journalPath),
                logger,
                new SystemClock(),
                new DelegateVehicleSafetySignalProvider(() => false),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(500));
            await using WireToGateBusinessService business = new(
                session,
                io,
                logger,
                new SystemClock(),
                () => false,
                new WireToGateSlotOperationExecutorOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(30)),
                operatorVariable,
                recoveryOptions: new WireToGateRecoveryOptions(
                    true,
                    proofVariable,
                    "MAINTENANCE_ADMINISTRATOR",
                    "CONFIGURED_PROOF"));

            DateTimeOffset sentAt = DateTimeOffset.UtcNow;
            const string demandId = "11111111-1111-4111-8111-111111111111";
            const string operationSessionId = "22222222-2222-4222-8222-222222222222";
            const string attemptId = "33333333-3333-4333-8333-333333333333";
            WireToGateRecoveryOperationContext context = new(
                "44444444-4444-4444-8444-444444444444",
                null,
                1,
                sentAt,
                demandId,
                operationSessionId,
                attemptId,
                OperationType.Load,
                [1, 2],
                2,
                true,
                new string('0', 64));
            await session.Journal.InitializeAsync(testToken);
            await session.Journal.WriteRecoveryStateAsync(
                new WireToGateRecoveryState(
                    attemptId,
                    WireToGateRecoveryCheckpoint.Prepared,
                    [],
                    0,
                    [])
                {
                    OperationContext = context
                },
                testToken);

            business.Start();
            WireToGateSessionSnapshot connected = await session.Client
                .ConnectAndRecoverAsync(testToken);
            Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, connected.Readiness);
            await WaitUntilAsync(
                () => business.CurrentOperationSnapshot?.Stage
                    == WireToGateHmiOperationStage.RecoveryRequired,
                testToken);
            Assert.Equal(attemptId, business.CurrentOperationSnapshot!.SlotOperationAttemptId);
            Assert.True(business.CanRequestResumeAfterRepair);

            bool[] requestOutcomes = await Task.WhenAll(
                business.RequestResumeAfterRepairAsync("repair complete", testToken),
                business.RequestResumeAfterRepairAsync("repair complete", testToken));

            Assert.Equal(1, requestOutcomes.Count(outcome => outcome));
            await WaitUntilAsync(
                () => server.Received.Count(item =>
                    item.MessageType is "ExceptionRecoverySessionRequested" or "RecoveryActionSubmitted") == 2,
                testToken);
            string[] recoveryMessages = server.Received
                .Select(item => item.MessageType)
                .Where(item => item is "ExceptionRecoverySessionRequested" or "RecoveryActionSubmitted")
                .ToArray();
            Assert.Equal(["ExceptionRecoverySessionRequested", "RecoveryActionSubmitted"], recoveryMessages);
            Assert.Equal(0, io.UnlockCount);

            var requestEnvelope = server.ReceivedEnvelopes
                .Single(item => item.MessageType == "ExceptionRecoverySessionRequested");
            var actionEnvelope = server.ReceivedEnvelopes
                .Single(item => item.MessageType == "RecoveryActionSubmitted");
            JsonDocument requestDocument = JsonDocument.Parse(requestEnvelope.WireLine);
            JsonDocument actionDocument = JsonDocument.Parse(actionEnvelope.WireLine);
            string requestId = requestDocument.RootElement
                .GetProperty("payload")
                .GetProperty("requestId")
                .GetString()!;
            string requestEventId = requestDocument.RootElement
                .GetProperty("payload")
                .GetProperty("eventId")
                .GetString()!;
            string actionEventId = actionDocument.RootElement
                .GetProperty("payload")
                .GetProperty("eventId")
                .GetString()!;
            Assert.Equal(requestEnvelope.MessageId, requestId);
            Assert.Equal(requestId, requestEventId);
            Assert.Equal(requestEventId, actionEventId);
            Assert.Equal(
                demandId,
                requestDocument.RootElement.GetProperty("payload").GetProperty("demandId").GetString());
            Assert.Equal(
                [1, 2],
                requestDocument.RootElement.GetProperty("payload").GetProperty("slots")
                    .EnumerateArray()
                    .Select(item => item.GetInt32())
                    .ToArray());

            WireToGateRecoveryState persisted = await session.Journal
                .ReadRecoveryStateAsync(testToken);
            Assert.Equal(requestId, persisted.RecoverySessionRequestId);
            Assert.Equal("77777777-7777-4777-8777-777777777777", persisted.ExceptionRecoverySessionId);
            Assert.NotNull(persisted.RecoveryActionId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(operatorVariable, previousOperator);
            Environment.SetEnvironmentVariable(proofVariable, previousProof);
        }
    }

    /// <summary>
    /// A pickup stop that turns out to have nothing to load. The entry is open, no slot operation
    /// was ever commanded, and until now that was the one state with no way out: the cancellation
    /// button only appeared once a load was already underway, so the journey held the vehicle and
    /// the station until somebody drove a recovery by hand.
    ///
    /// The path is deliberately short. No door was opened, so there is nothing to clear, no
    /// recovery vector to journal and no LoadCancellationResult to send -- that message could not
    /// carry this case anyway, its slotResults being minItems 1. The authorization is the whole
    /// handshake.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task CancellingAtAStopWithNothingToLoadNeedsNoSlotOperationAndTouchesNoIo()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        const string operatorVariable = "W2G_G2_BEFORE_LOAD_OPERATOR";
        string? previousOperator = Environment.GetEnvironmentVariable(operatorVariable);
        Environment.SetEnvironmentVariable(operatorVariable, "operator-001");

        try
        {
            await using FakeControlServer server = new(IPAddress.Loopback)
            {
                SendReadinessAfterRecoveryAck = true,
                SendJourneySnapshotsAfterRecovery = true,
                SendSublotEntryRequestAfterRecovery = true,
                RespondToLoadCancellationRequests = true
            };
            FakeIoModuleClient io = new();
            NullLogger logger = new();
            await using WireToGateSessionService session = new(
                CreateSessionOptions(server),
                io,
                new SqliteWireToGateJournal(NewJournalPath()),
                logger,
                new SystemClock(),
                new DelegateVehicleSafetySignalProvider(() => true),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(500));
            await using WireToGateBusinessService business = new(
                session,
                io,
                logger,
                new SystemClock(),
                () => true,
                new WireToGateSlotOperationExecutorOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(30)),
                operatorVariable);

            business.Start();
            await session.Client.ConnectAndRecoverAsync(testToken);
            await WaitUntilAsync(() => business.CanSubmitSublot, testToken);

            // The entry is open and nothing has been commanded to a slot. This is the assertion the
            // change exists for: before it, the button was bound to an unsettled load operation and
            // stayed hidden here.
            Assert.True(business.CanRequestLoadCancellation);
            Assert.Equal(["SUBLOT-001"], business.ExpectedSublots);
            Assert.Equal(0, io.UnlockCount);

            Assert.True(await business.RequestLoadCancellationAsync(
                "现场确认本站没有要装的货。",
                testToken));

            // Raised with no attempt id: that null is what tells the server this is the before-load
            // path, and it is what lets the authorization stand as the whole handshake.
            Assert.Equal("(null)", Assert.Single(server.ReceivedLoadCancellationAttemptIds));
            Assert.Contains(
                server.Received,
                item => item.MessageType == "LoadCancellationStartRequested");
            // Nothing to clear means no result and no IO. A result here would be a claim about
            // slots this cancellation never touched.
            Assert.DoesNotContain(
                server.Received,
                item => item.MessageType == "LoadCancellationResult");
            Assert.Equal(0, io.UnlockCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(operatorVariable, previousOperator);
        }
    }

    /// <summary>
    /// The same stop, but the server refuses. The peer must report the refusal and change nothing:
    /// a rejected cancellation leaves the entry open, because the demand is still the vehicle's.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task ARefusedBeforeLoadCancellationLeavesTheStopExactlyAsItWas()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        const string operatorVariable = "W2G_G2_BEFORE_LOAD_REFUSED_OPERATOR";
        string? previousOperator = Environment.GetEnvironmentVariable(operatorVariable);
        Environment.SetEnvironmentVariable(operatorVariable, "operator-001");

        try
        {
            await using FakeControlServer server = new(IPAddress.Loopback)
            {
                SendReadinessAfterRecoveryAck = true,
                SendJourneySnapshotsAfterRecovery = true,
                SendSublotEntryRequestAfterRecovery = true,
                RespondToLoadCancellationRequests = true,
                LoadCancellationDecision = "REJECTED"
            };
            FakeIoModuleClient io = new();
            NullLogger logger = new();
            await using WireToGateSessionService session = new(
                CreateSessionOptions(server),
                io,
                new SqliteWireToGateJournal(NewJournalPath()),
                logger,
                new SystemClock(),
                new DelegateVehicleSafetySignalProvider(() => true),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(500));
            await using WireToGateBusinessService business = new(
                session,
                io,
                logger,
                new SystemClock(),
                () => true,
                new WireToGateSlotOperationExecutorOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(30)),
                operatorVariable);

            business.Start();
            await session.Client.ConnectAndRecoverAsync(testToken);
            await WaitUntilAsync(() => business.CanSubmitSublot, testToken);

            Assert.False(await business.RequestLoadCancellationAsync(
                "现场确认本站没有要装的货。",
                testToken));

            Assert.True(business.CanSubmitSublot);
            Assert.Equal(["SUBLOT-001"], business.ExpectedSublots);
            Assert.Equal(0, io.UnlockCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(operatorVariable, previousOperator);
        }
    }

    /// <summary>
    /// The recovery entry has to open on a session that turned RECOVERY_REQUIRED *while it was
    /// running*, not only on one that handshook that way. On the real vehicle the trigger is a
    /// refused load result: the server applies it, decides the session needs recovery, and appends
    /// one SessionReadiness line to the result ack. By then the vehicle has already cleared its own
    /// recovery state -- recording the result is what clears it -- so the entry has to open with no
    /// checkpoint and no server recovery-session snapshot to lean on.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task RecoveryRequiredAnnouncedOnAResultAckOpensTheRecoveryEntry()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        const string operatorVariable = "W2G_G2_MIDSESSION_OPERATOR";
        const string proofVariable = "W2G_G2_MIDSESSION_PROOF";
        string? previousOperator = Environment.GetEnvironmentVariable(operatorVariable);
        string? previousProof = Environment.GetEnvironmentVariable(proofVariable);
        Environment.SetEnvironmentVariable(operatorVariable, "maintenance-002");
        Environment.SetEnvironmentVariable(proofVariable, "test-proof");

        try
        {
            await using FakeControlServer server = new(IPAddress.Loopback)
            {
                SendReadinessAfterRecoveryAck = true,
                SendRecoveryRequiredReadinessAfterOperationResultAck = true
            };
            FakeIoModuleClient io = new();
            NullLogger logger = new();
            await using WireToGateSessionService session = new(
                CreateSessionOptions(server),
                io,
                new SqliteWireToGateJournal(NewJournalPath()),
                logger,
                new SystemClock(),
                new DelegateVehicleSafetySignalProvider(() => false),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(500));
            await using WireToGateBusinessService business = new(
                session,
                io,
                logger,
                new SystemClock(),
                () => false,
                new WireToGateSlotOperationExecutorOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(30)),
                operatorVariable,
                recoveryOptions: new WireToGateRecoveryOptions(
                    true,
                    proofVariable,
                    "MAINTENANCE_ADMINISTRATOR",
                    "CONFIGURED_PROOF"));

            business.Start();
            WireToGateSessionSnapshot connected = await session.Client
                .ConnectAndRecoverAsync(testToken);

            // The entry stays shut while the session is usable -- otherwise the assertion below
            // would pass on a door that was never closed.
            Assert.Equal(WireToGateSessionReadiness.Ready, connected.Readiness);
            Assert.False(business.CanRequestResumeAfterRepair);

            const string demandId = "11111111-1111-4111-8111-111111111111";
            const string attemptId = "33333333-3333-4333-8333-333333333333";
            await session.Client.SendOperationResultAsync(
                $"operation-result:{attemptId}",
                attemptId,
                new WireToGateOperationResultPayload(
                    demandId,
                    attemptId,
                    "LOAD",
                    "FAILED",
                    [
                        new WireToGateSlotResultPayload(
                            1,
                            "FAILED",
                            "EMPTY",
                            "UNLOCKED",
                            "RESET",
                            ["SLOT_EMPTY_AFTER_LOAD"])
                    ],
                    DateTimeOffset.UtcNow,
                    "NONE",
                    new string('0', 64)),
                testToken);

            await WaitUntilAsync(
                () => session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
                testToken);

            Assert.Contains("SESSION_RECOVERY_REQUIRED", session.Current.ReasonCodes);
            Assert.True(session.Current.Connected);
            Assert.True(business.CanRequestResumeAfterRepair);
            Assert.Equal(0, io.UnlockCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(operatorVariable, previousOperator);
            Environment.SetEnvironmentVariable(proofVariable, previousProof);
        }
    }

    /// <summary>
    /// 8005-agv-control-server#5 那条卡死的路：服务端把这一次装载判成 <c>RecoveryRequired</c>，而车辆
    /// 认为它成功了——<c>MarkResultRecordedAsync</c> 已经把 <c>OperationContext</c> 与
    /// <c>UnsettledSlotOperationAttemptId</c> 清空。补偿入口原来只认这两样，于是**恰恰在需要补偿的
    /// 那一刻**申请不出来。现在它认已结算的那份身份，而 attempt 由服务端在
    /// <c>ExceptionRecoverySessionOpened</c> / <c>RecoveryActionAccepted</c> 里点名（protocol-v0.3.0），
    /// 两端对不上就整条拒掉、不擅自换作用域。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task CompensationIsRequestableAfterTheVehicleAlreadySettledTheLoad()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        const string operatorVariable = "W2G_G2_COMPENSATION_OPERATOR";
        const string proofVariable = "W2G_G2_COMPENSATION_PROOF";
        string? previousOperator = Environment.GetEnvironmentVariable(operatorVariable);
        string? previousProof = Environment.GetEnvironmentVariable(proofVariable);
        Environment.SetEnvironmentVariable(operatorVariable, "maintenance-003");
        Environment.SetEnvironmentVariable(proofVariable, "test-proof");

        try
        {
            const string demandId = "11111111-1111-4111-8111-111111111111";
            string attemptId = FakeControlServer.SlotOperationAttemptId;
            string journalPath = NewJournalPath();
            await SeedSettledLoadAsync(journalPath, demandId, attemptId, testToken);

            await using FakeControlServer server = new(IPAddress.Loopback)
            {
                RespondToRecoveryRequests = true,
                RecoverySessionSlotOperationAttemptId = FakeControlServer.SlotOperationAttemptId,
                SendReadinessAfterRecoveryAck = true,
                SendRecoveryRequiredReadinessAfterOperationResultAck = true
            };
            FakeIoModuleClient io = new();
            NullLogger logger = new();
            await using WireToGateSessionService session = new(
                CreateSessionOptions(server),
                io,
                new SqliteWireToGateJournal(journalPath),
                logger,
                new SystemClock(),
                new DelegateVehicleSafetySignalProvider(() => false),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(500));
            await using WireToGateBusinessService business = new(
                session,
                io,
                logger,
                new SystemClock(),
                () => false,
                new WireToGateSlotOperationExecutorOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(30)),
                operatorVariable,
                recoveryOptions: new WireToGateRecoveryOptions(
                    true,
                    proofVariable,
                    "MAINTENANCE_ADMINISTRATOR",
                    "CONFIGURED_PROOF"));

            business.Start();
            await session.Client.ConnectAndRecoverAsync(testToken);

            // 服务端宣布这一次会话需要恢复。车辆这边的 attempt 身份此时已经是结算过的那份。
            await session.Client.SendOperationResultAsync(
                $"operation-result:{attemptId}",
                attemptId,
                new WireToGateOperationResultPayload(
                    demandId,
                    attemptId,
                    "LOAD",
                    "FAILED",
                    [
                        new WireToGateSlotResultPayload(
                            1,
                            "FAILED",
                            "EMPTY",
                            "UNLOCKED",
                            "RESET",
                            ["SLOT_EMPTY_AFTER_LOAD"])
                    ],
                    DateTimeOffset.UtcNow,
                    "NONE",
                    new string('0', 64)),
                testToken);

            await WaitUntilAsync(
                () => session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
                testToken);
            await WaitUntilAsync(() => business.CanRequestLoadCompensation, testToken);

            Assert.True(await business.RequestLoadCompensationAsync(
                "现场确认装货无法继续，申请补偿清空目标仓位。",
                testToken));

            await WaitUntilAsync(
                () => server.ReceivedEnvelopes.Any(envelope =>
                    envelope.MessageType == "LoadCompensationRequested"),
                testToken);
            (int _, string _, string _, string wireLine) = Assert.Single(
                server.ReceivedEnvelopes,
                envelope => envelope.MessageType == "LoadCompensationRequested");
            using JsonDocument document = JsonDocument.Parse(wireLine);
            JsonElement payload = document.RootElement.GetProperty("payload");
            Assert.Equal(attemptId, payload.GetProperty("slotOperationAttemptId").GetString());
            Assert.Equal(demandId, payload.GetProperty("demandId").GetString());

            // 申请本身不碰仓门 IO：动作要等服务端把 LoadCompensationCommand 发回来。
            Assert.Equal(0, io.UnlockCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(operatorVariable, previousOperator);
            Environment.SetEnvironmentVariable(proofVariable, previousProof);
        }
    }

    /// <summary>
    /// 复现 <c>MarkResultRecordedAsync</c> 写完之后的日志状态：物理断点与在途 attempt 都已清空，
    /// 只剩下最近一次已结算装载的身份。
    /// </summary>
    private static async Task SeedSettledLoadAsync(
        string journalPath,
        string demandId,
        string attemptId,
        CancellationToken cancellationToken)
    {
        await using SqliteWireToGateJournal journal = new(journalPath);
        await journal.InitializeAsync(cancellationToken);
        WireToGateRecoveryState state = await journal.ReadRecoveryStateAsync(cancellationToken);
        await journal.WriteRecoveryStateAsync(
            state with
            {
                UnsettledSlotOperationAttemptId = null,
                ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ResultRecorded,
                OperationContext = null,
                LastCompletedLoadOperationContext = new WireToGateRecoveryOperationContext(
                    "99999999-9999-4999-8999-999999999999",
                    null,
                    1,
                    DateTimeOffset.UtcNow,
                    demandId,
                    "33333333-3333-4333-8333-333333333333",
                    attemptId,
                    OperationType.Load,
                    [1],
                    1,
                    true,
                    new string('0', 64))
            },
            cancellationToken);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task JourneySnapshotsAreProjectedAndHeartbeatDoesNotStealAsyncMessages()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendJourneySnapshotsAfterRecovery = true
        };
        string journalPath = NewJournalPath();
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, journalPath);

        await client.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(
            () => client.CurrentJourney.CanAcceptSublot
                && client.CurrentJourney.UpcomingStopPlan is not null,
            testToken);

        WireToGateJourneySnapshot journey = client.CurrentJourney;
        Assert.Equal("READY", journey.VehicleBusinessState?.Readiness);
        Assert.Equal("ST-01", journey.CurrentStopWorklist?.StationId);
        Assert.Equal("SUBLOT-001", Assert.Single(journey.CurrentStopWorklist!.Items).Sublot);
        Assert.Equal("MAP-01", Assert.Single(journey.UpcomingStopPlan!.Legs).MapId);
        await WaitUntilAsync(
            () => server.Received.Count(item => item.MessageType == "SnapshotAppliedAck") == 3,
            testToken);

        await client.SendHeartbeatAsync(testToken);
        await WaitUntilAsync(
            () => InboundMessageTypes(server).Contains("Heartbeat", StringComparer.Ordinal),
            testToken);
        Assert.Contains(server.Received, item => item.MessageType == "Heartbeat");
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task SameJourneyRevisionsWithStablePayloadAreAcceptedAcrossSessionGenerations()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendJourneySnapshotsAfterRecovery = true,
            ReplayJourneySnapshotsWithStableIdentity = true
        };
        string journalPath = NewJournalPath();
        const string onboardInstanceId = "0198f1a2-7c3d-4e5f-8a9b-c0de5a7e2001";
        FakeIoModuleClient io = new();

        await using (WireToGateSessionClient firstClient = CreateClient(
            server,
            io,
            journalPath,
            onboardInstanceId: onboardInstanceId))
        {
            await firstClient.ConnectAndRecoverAsync(testToken);
            await WaitUntilAsync(
                () => server.Received.Count(item => item.MessageType == "SnapshotAppliedAck") == 3,
                testToken);
        }

        await using WireToGateSessionClient secondClient = CreateClient(
            server,
            io,
            journalPath,
            capability: 2,
            safety: 2,
            onboardInstanceId: onboardInstanceId);
        int restoredProjectionChanges = 0;
        secondClient.JourneyChanged += (_, _) => restoredProjectionChanges++;
        WireToGateSessionSnapshot resumed = await secondClient.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(
            () => server.Received.Count(item => item.MessageType == "SnapshotAppliedAck") == 6,
            testToken);

        Assert.Equal(WireToGateSessionReadiness.Ready, resumed.Readiness);
        Assert.True(secondClient.CurrentJourney.CanAcceptSublot);
        Assert.Equal(3, restoredProjectionChanges);
        Assert.Equal(0, io.UnlockCount);
        Assert.Empty(server.StaleGenerationRejections);
        Assert.DoesNotContain(server.Received, item => item.MessageType == "ProtocolProblem");

        var sentByType = server.SentJourneyEnvelopes
            .GroupBy(item => item.MessageType, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.Connection).ToArray(),
                StringComparer.Ordinal);
        Assert.Equal(
            [
                "CurrentStopWorklistSnapshot",
                "UpcomingStopPlanSnapshot",
                "VehicleBusinessStateSnapshot"
            ],
            sentByType.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray());
        Assert.All(sentByType.Values, attempts =>
        {
            Assert.Equal(2, attempts.Length);
            WireToGateEnvelope first = WireToGateProtocolSerializer.DeserializeAndValidate(
                attempts[0].WireLine,
                "AGV-8005-01");
            WireToGateEnvelope second = WireToGateProtocolSerializer.DeserializeAndValidate(
                attempts[1].WireLine,
                "AGV-8005-01");
            Assert.Equal(first.MessageId, second.MessageId);
            Assert.NotEqual(first.SessionGeneration, second.SessionGeneration);
            Assert.NotEqual(
                WireToGateProtocolSerializer.ComputeContentSha256(first),
                WireToGateProtocolSerializer.ComputeContentSha256(second));
            Assert.Equal(
                WireToGateProtocolSerializer.ComputePayloadContentSha256(first),
                WireToGateProtocolSerializer.ComputePayloadContentSha256(second));
        });

        var secondConnectionAcks = server.ReceivedEnvelopes
            .Where(item => item.Connection == 2 && item.MessageType == "SnapshotAppliedAck")
            .ToArray();
        Assert.Equal(3, secondConnectionAcks.Length);
        foreach (var sent in server.SentJourneyEnvelopes.Where(item => item.Connection == 2))
        {
            var acknowledgement = Assert.Single(secondConnectionAcks, item =>
            {
                using JsonDocument document = JsonDocument.Parse(item.WireLine);
                return document.RootElement.GetProperty("correlationId").GetString() == sent.MessageId;
            });
            using JsonDocument acknowledgementDocument = JsonDocument.Parse(acknowledgement.WireLine);
            string? appliedContentSha256 = acknowledgementDocument.RootElement
                .GetProperty("payload")
                .GetProperty("appliedContentSha256")
                .GetString();
            Assert.Equal(
                WireToGateProtocolSerializer.ComputeSha256(Encoding.UTF8.GetBytes(sent.WireLine)),
                appliedContentSha256);
        }

        await using SqliteWireToGateJournal journal = new(journalPath);
        await journal.InitializeAsync(testToken);
        IReadOnlyList<WireToGateAppliedJourneySnapshot> persisted =
            await journal.ReadAppliedJourneySnapshotsAsync(testToken);
        Assert.Equal(3, persisted.Count);
        Assert.All(persisted, snapshot => Assert.Equal(
            WireToGateProtocolSerializer.ComputePayloadContentSha256(snapshot.PayloadJson),
            snapshot.MessageType switch
            {
                "VehicleBusinessStateSnapshot" => secondClient.CurrentJourney.VehicleBusinessState!.ContentSha256,
                "CurrentStopWorklistSnapshot" => secondClient.CurrentJourney.CurrentStopWorklist!.ContentSha256,
                "UpcomingStopPlanSnapshot" => secondClient.CurrentJourney.UpcomingStopPlan!.ContentSha256,
                _ => throw new InvalidDataException("Unexpected persisted journey snapshot type.")
            }));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task AppliedJourneyJournalUsesCanonicalPayloadForSameRevisionIdentity()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using SqliteWireToGateJournal journal = new(NewJournalPath());
        await journal.InitializeAsync(testToken);
        WireToGateAppliedJourneySnapshot original = new(
            "CurrentStopWorklistSnapshot",
            "00000000-0000-4000-8000-000000009201",
            7,
            new string('a', 64),
            "{\"stationId\":\"ST-01\",\"items\":[{\"sublot\":\"LOT-1\",\"count\":2}]}",
            DateTimeOffset.UtcNow);
        await journal.SaveAppliedJourneySnapshotAsync(original, testToken);
        WireToGateAppliedJourneySnapshot replay = original with
        {
            MessageId = "00000000-0000-4000-8000-000000009202",
            ContentSha256 = new string('b', 64),
            PayloadJson = "{\"items\":[{\"count\":2,\"sublot\":\"LOT-1\"}],\"stationId\":\"ST-01\"}",
            AppliedAt = original.AppliedAt.AddSeconds(1)
        };

        WireToGateAppliedJourneySnapshot accepted =
            await journal.SaveAppliedJourneySnapshotAsync(replay, testToken);

        Assert.Equal(original, accepted);
        WireToGateAppliedJourneySnapshot conflict = replay with
        {
            MessageId = "00000000-0000-4000-8000-000000009203",
            PayloadJson = "{\"items\":[{\"count\":2,\"sublot\":\"LOT-2\"}],\"stationId\":\"ST-01\"}"
        };
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => journal.SaveAppliedJourneySnapshotAsync(conflict, testToken));
        Assert.Equal("SNAPSHOT_REVISION_CONTENT_CONFLICT", failure.Message);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task DemandAcceptanceSnapshotsArePersistedBeforeAcknowledgement()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendDemandAcceptanceSnapshotsAfterRecovery = true
        };
        string journalPath = NewJournalPath();
        FakeIoModuleClient io = new();
        await using SqliteWireToGateJournal journal = new(journalPath);
        await using WireToGateSessionClient client = CreateClient(server, io, journalPath);

        await journal.InitializeAsync(testToken);
        await client.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(
            () => server.Received.Count(item => item.MessageType == "SnapshotAppliedAck") == 3,
            testToken);

        Assert.Equal("ST-01", client.CurrentJourney.CurrentStopWorklist?.StationId);
        Assert.Equal("TD-001", client.CurrentJourney.CurrentStopWorklist?.Items.Single().TransportDemandKey);
        Assert.Equal("ARRIVED", Assert.Single(client.CurrentJourney.UpcomingStopPlan!.Legs).State);

        IReadOnlyList<WireToGateAppliedJourneySnapshot> applied =
            await journal.ReadAppliedJourneySnapshotsAsync(testToken);
        Assert.Equal(
            [("CurrentStopWorklistSnapshot", 1L), ("UpcomingStopPlanSnapshot", 2L)],
            applied
                .OrderBy(item => item.MessageType, StringComparer.Ordinal)
                .Select(item => (item.MessageType, item.Revision))
                .ToArray());

        var acknowledgements = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SnapshotAppliedAck")
            .Select(item =>
            {
                using JsonDocument document = JsonDocument.Parse(item.WireLine);
                JsonElement root = document.RootElement;
                JsonElement payload = root.GetProperty("payload");
                return (
                    CorrelationId: root.GetProperty("correlationId").GetString()!,
                    SnapshotMessageId: payload.GetProperty("snapshotMessageId").GetString()!,
                    SnapshotKind: payload.GetProperty("snapshotKind").GetString()!,
                    AppliedRevision: payload.GetProperty("appliedRevision").GetInt64());
            })
            .ToArray();
        Assert.Equal(
            ["UPCOMING_STOP_PLAN", "CURRENT_STOP_WORKLIST", "UPCOMING_STOP_PLAN"],
            acknowledgements.Select(item => item.SnapshotKind).ToArray());
        Assert.All(
            acknowledgements,
            item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.CorrelationId));
                Assert.Equal(item.SnapshotMessageId, item.CorrelationId);
            });
        Assert.Equal([1L, 1L, 2L], acknowledgements.Select(item => item.AppliedRevision).ToArray());
        Assert.Equal(0, io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task PersistedDemandProjectionIsRestoredWhenServerDoesNotResendIt()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendDemandAcceptanceSnapshotsAfterRecovery = true,
            SendDemandAcceptanceSnapshotsOnlyFirstConnection = true
        };
        string journalPath = NewJournalPath();
        FakeIoModuleClient io = new();

        await using WireToGateSessionClient firstClient = CreateClient(server, io, journalPath);
        await firstClient.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(
            () => server.Received.Count(item => item.MessageType == "SnapshotAppliedAck") == 3,
            testToken);
        await firstClient.DisposeAsync();

        await using WireToGateSessionClient secondClient = CreateClient(
            server,
            io,
            journalPath,
            capability: 2,
            safety: 2);
        WireToGateSessionSnapshot resumed = await secondClient.ConnectAndRecoverAsync(testToken);

        Assert.Equal(WireToGateSessionReadiness.Ready, resumed.Readiness);
        Assert.Equal("ST-01", secondClient.CurrentJourney.CurrentStopWorklist?.StationId);
        Assert.Equal("ARRIVED", Assert.Single(secondClient.CurrentJourney.UpcomingStopPlan!.Legs).State);
        Assert.Equal(3, server.Received.Count(item => item.MessageType == "SnapshotAppliedAck"));
    }

    /// <summary>
    /// FR-001 AC-3 把可录入范围定义为「本次派车关联的任务集合」，而一趟车可以带着几个站点各自的
    /// 任务，所以判据是集合归属。操作员录入集合里的第二项与录入第一项一样合法——旧判据是「等于
    /// expectedSublot 这一个字符串」，那样的话除第一项外全都会以 SUBLOT_NOT_IN_WORKLIST 被拒。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public Task AnySublotInTheExpectedSetCanBeSubmittedNotOnlyTheFirst() =>
        RunWithSublotEntryAsync(
            ["SUBLOT-001", "SUBLOT-002", "SUBLOT-003"],
            async (server, business, testToken) =>
            {
                Assert.Equal(["SUBLOT-001", "SUBLOT-002", "SUBLOT-003"], business.ExpectedSublots);

                string messageId = await business.SubmitSublotAsync("SUBLOT-002", "SCANNER", testToken);

                Assert.NotEmpty(messageId);
                Assert.Contains(
                    server.ReceivedEnvelopes,
                    item => item.MessageType == "SublotSubmitted"
                        && item.WireLine.Contains("SUBLOT-002", StringComparison.Ordinal));
            });

    /// <summary>
    /// 放宽到集合不等于放开：范围外的子批仍然要拒（FR-001 AC-4），而且不能有任何东西发到服务端。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public Task SublotOutsideTheExpectedSetIsStillRefused() =>
        RunWithSublotEntryAsync(
            ["SUBLOT-001", "SUBLOT-002"],
            async (server, business, testToken) =>
            {
                InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await business.SubmitSublotAsync("SUBLOT-009", "SCANNER", testToken));

                Assert.Equal("SUBLOT_NOT_IN_WORKLIST", error.Message);
                Assert.DoesNotContain(server.Received, item => item.MessageType == "SublotSubmitted");
            });

    /// <summary>
    /// 上限是 8，八项本身合法。这条与 <see cref="NineExpectedSublotsFailClosed"/> 一起把边界钉在
    /// 8/9 之间——只测拒绝的那一半，上限写成 7 也会通过。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public Task EightExpectedSublotsAreWithinTheCap() =>
        RunWithSublotEntryAsync(
            ["S-1", "S-2", "S-3", "S-4", "S-5", "S-6", "S-7", "S-8"],
            (server, business, testToken) =>
            {
                _ = server;
                _ = testToken;
                Assert.Equal(8, business.ExpectedSublots.Count);
                return Task.CompletedTask;
            });

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public Task EmptyExpectedSublotsFailClosed() => AssertEntryRequestFailsClosedAsync([]);

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public Task NineExpectedSublotsFailClosed() =>
        AssertEntryRequestFailsClosedAsync(
            ["S-1", "S-2", "S-3", "S-4", "S-5", "S-6", "S-7", "S-8", "S-9"]);

    /// <summary>
    /// 重复项不是「无害的冗余」：同一个子批出现两次，录入之后哪一次算数是无定义的，而清单的修订号
    /// 只能表达一次录入。失败在解析处比失败在录入处便宜。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public Task DuplicateExpectedSublotsFailClosed() =>
        AssertEntryRequestFailsClosedAsync(["SUBLOT-001", "SUBLOT-001"]);

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public Task BlankExpectedSublotFailsClosed() =>
        AssertEntryRequestFailsClosedAsync(["SUBLOT-001", "   "]);

    /// <summary>
    /// 违反 schema 的 SublotEntryRequested 走的是失败关闭，不是 ProtocolProblem：
    /// TryCreateServerCommand 抛的 InvalidDataException 冒到读循环的总 catch，那里重置旅程投影并把
    /// 会话置为 Disconnected / SESSION_RECOVERY_REQUIRED。断言这条路径而不是断言一条问题消息。
    /// </summary>
    private static async Task AssertEntryRequestFailsClosedAsync(IReadOnlyList<string> expectedSublots)
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendJourneySnapshotsAfterRecovery = true,
            SendSublotEntryRequestAfterRecovery = true,
            ExpectedSublots = expectedSublots
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());

        await client.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(
            () => client.Current.Readiness == WireToGateSessionReadiness.Disconnected,
            testToken);

        Assert.False(client.IsReady);
        Assert.Contains("SESSION_RECOVERY_REQUIRED", client.Current.ReasonCodes);
    }

    private static async Task RunWithSublotEntryAsync(
        IReadOnlyList<string> expectedSublots,
        Func<FakeControlServer, WireToGateBusinessService, CancellationToken, Task> body)
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        const string operatorVariable = "W2G_G2_MULTI_DEMAND_OPERATOR";
        string? previousOperator = Environment.GetEnvironmentVariable(operatorVariable);
        Environment.SetEnvironmentVariable(operatorVariable, "operator-001");

        try
        {
            await using FakeControlServer server = new(IPAddress.Loopback)
            {
                SendReadinessAfterRecoveryAck = true,
                SendJourneySnapshotsAfterRecovery = true,
                SendSublotEntryRequestAfterRecovery = true,
                ExpectedSublots = expectedSublots
            };
            FakeIoModuleClient io = new();
            NullLogger logger = new();
            await using WireToGateSessionService session = new(
                CreateSessionOptions(server),
                io,
                new SqliteWireToGateJournal(NewJournalPath()),
                logger,
                new SystemClock(),
                new DelegateVehicleSafetySignalProvider(() => true),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(500));
            await using WireToGateBusinessService business = new(
                session,
                io,
                logger,
                new SystemClock(),
                () => true,
                new WireToGateSlotOperationExecutorOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(30)),
                operatorVariable);

            business.Start();
            await session.Client.ConnectAndRecoverAsync(testToken);
            await WaitUntilAsync(() => business.CanSubmitSublot, testToken);

            await body(server, business, testToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(operatorVariable, previousOperator);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task SameJourneyRevisionWithDifferentContentFailsClosed()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendJourneySnapshotsAfterRecovery = true,
            SendJourneyRevisionConflict = true
        };
        string journalPath = NewJournalPath();
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, journalPath);

        await client.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(
            () => client.Current.Readiness == WireToGateSessionReadiness.Disconnected,
            testToken);

        Assert.Equal(WireToGateJourneySnapshot.Empty, client.CurrentJourney);
        Assert.Contains(server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.False(client.IsReady);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task FormalSlotOperationCommandIsValidatedAndRaisedWithoutPhysicalSideEffect()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendSlotOperationCommandAfterRecovery = true
        };
        string journalPath = NewJournalPath();
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, journalPath);
        WireToGateServerCommand? received = null;
        client.ServerCommandReceived += (_, args) => received = args.Value;

        await client.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(() => received is not null, testToken);

        WireToGateSlotOperationCommand command = Assert.IsType<WireToGateSlotOperationCommand>(received);
        Assert.Equal(OperationType.Load, command.OperationType);
        Assert.Equal([1], command.Slots);
        Assert.Equal(0, io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task BusinessProgressUsesStableDurableMessageAndDoesNotDuplicateAfterAck()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        string journalPath = NewJournalPath();
        FakeIoModuleClient io = new();
        await using SqliteWireToGateJournal journal = new(journalPath);
        await using WireToGateSessionClient client = CreateClient(server, io, journalPath);

        await client.ConnectAndRecoverAsync(testToken);
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        string first = await client.SendOperationProgressAsync(
            "44444444-4444-4444-4444-444444444444",
            "PREPARING",
            [],
            [],
            observedAt,
            testToken);
        string second = await client.SendOperationProgressAsync(
            "44444444-4444-4444-4444-444444444444",
            "PREPARING",
            [],
            [],
            observedAt,
            testToken);

        Assert.Equal(first, second);
        Assert.Equal(1, server.Received.Count(item => item.MessageType == "OperationProgress"));
        Assert.Empty(await journal.ReadUnacknowledgedOutgoingAsync(testToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task DurableOutboxRejectsDifferentContentForSameDeduplicationKey()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        string journalPath = NewJournalPath();
        await using SqliteWireToGateJournal journal = new(journalPath);
        await journal.InitializeAsync(testToken);

        string messageId = "55555555-5555-5555-5555-555555555555";
        WireToGateDurableMessage original = new(
            "operation-result:66666666-6666-6666-6666-666666666666",
            "OperationResult",
            messageId,
            new string('a', 64),
            "{\"messageId\":\"55555555-5555-5555-5555-555555555555\"}",
            DateTimeOffset.UtcNow,
            false);
        await journal.SaveOutgoingBeforeSendAsync(original, testToken);

        WireToGateDurableMessage conflicting = original with
        {
            ContentSha256 = new string('b', 64),
            WireLine = "{\"messageId\":\"55555555-5555-5555-5555-555555555555\",\"changed\":true}"
        };
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => journal.SaveOutgoingBeforeSendAsync(conflicting, testToken));

        Assert.Equal("BUSINESS_ID_CONTENT_CONFLICT", exception.Message);
        WireToGateDurableMessage? stored = await journal.ReadOutgoingByDeduplicationKeyAsync(
            original.DeduplicationKey,
            testToken);
        Assert.NotNull(stored);
        Assert.Equal(original.ContentSha256, stored.ContentSha256);
        Assert.Equal(original.WireLine, stored.WireLine);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public async Task JournalEpochPersistsAcrossReopenAndDiffersForFreshJournal()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        string existingJournalPath = NewJournalPath();
        string firstEpoch;
        await using (SqliteWireToGateJournal first = new(existingJournalPath))
        {
            await first.InitializeAsync(testToken);
            firstEpoch = await first.ReadJournalEpochAsync(testToken);
        }

        string reopenedEpoch;
        await using (SqliteWireToGateJournal reopened = new(existingJournalPath))
        {
            await reopened.InitializeAsync(testToken);
            reopenedEpoch = await reopened.ReadJournalEpochAsync(testToken);
        }

        string freshEpoch;
        await using (SqliteWireToGateJournal fresh = new(NewJournalPath()))
        {
            await fresh.InitializeAsync(testToken);
            freshEpoch = await fresh.ReadJournalEpochAsync(testToken);
        }

        Assert.Equal(firstEpoch, reopenedEpoch);
        Assert.NotEqual(firstEpoch, freshEpoch);
        Assert.True(Guid.TryParseExact(firstEpoch, "D", out _));
        Assert.True(Guid.TryParseExact(freshEpoch, "D", out _));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task FreshJournalsNeverReuseSafetyStateChangedMessageIdentity()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        DateTimeOffset observedAt = new(2026, 8, 27, 8, 0, 0, TimeSpan.Zero);

        string firstMessageId;
        await using (WireToGateSessionClient firstClient = CreateClient(server, new FakeIoModuleClient(), NewJournalPath()))
        {
            await firstClient.ConnectAndRecoverAsync(testToken);
            firstMessageId = await firstClient.SendSafetyStateChangedAsync(
                1,
                observedAt,
                new WireToGateSafetySummaryPayload(true, true, true, true, false, []),
                [1],
                testToken);
        }

        string secondMessageId;
        await using (WireToGateSessionClient secondClient = CreateClient(server, new FakeIoModuleClient(), NewJournalPath()))
        {
            await secondClient.ConnectAndRecoverAsync(testToken);
            secondMessageId = await secondClient.SendSafetyStateChangedAsync(
                1,
                observedAt,
                new WireToGateSafetySummaryPayload(false, true, true, true, false, ["VEHICLE_NOT_STOPPED"]),
                [2],
                testToken);
        }

        Assert.NotEqual(firstMessageId, secondMessageId);
        var safetyMessages = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SafetyStateChanged")
            .ToArray();
        Assert.Equal(2, safetyMessages.Length);
        Assert.Equal([firstMessageId, secondMessageId], safetyMessages.Select(item => item.MessageId).ToArray());
        Assert.Equal(1, safetyMessages[0].Connection);
        Assert.Equal(2, safetyMessages[1].Connection);
        Assert.Equal(2, server.AcceptedSafetyStateChangedCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task LostSafetyStateChangedAckReplaysSameIdentityAndBusinessContentFromJournal()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            DropBeforeSafetyStateChangedAck = true,
            SendReadinessAfterRecoveryAck = true,
            SendReadinessAfterSafetyStateChangedAck = true
        };
        string journalPath = NewJournalPath();
        string onboardInstanceId = "0198f1a2-7c3d-4e5f-8a9b-c0de5a7e1002";
        FakeIoModuleClient io = new();
        DateTimeOffset observedAt = new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        WireToGateSafetySummaryPayload payload = new(
            false,
            false,
            true,
            true,
            true,
            ["VEHICLE_NOT_READY"]);

        await using (WireToGateSessionClient firstClient = CreateClient(
            server,
            io,
            journalPath,
            onboardInstanceId: onboardInstanceId))
        {
            await firstClient.ConnectAndRecoverAsync(testToken);
            await Assert.ThrowsAnyAsync<IOException>(() => firstClient.SendSafetyStateChangedAsync(
                1,
                observedAt,
                payload,
                [1, 2],
                testToken));
        }

        server.DropBeforeSafetyStateChangedAck = false;
        await using (WireToGateSessionClient secondClient = CreateClient(
            server,
            io,
            journalPath,
            onboardInstanceId: onboardInstanceId))
        {
            WireToGateSessionSnapshot resumed = await secondClient.ConnectAndRecoverAsync(testToken);
            Assert.Equal(WireToGateSessionReadiness.Ready, resumed.Readiness);
        }

        var replayed = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SafetyStateChanged")
            .ToArray();
        Assert.Equal(2, replayed.Length);
        Assert.Equal(replayed[0].MessageId, replayed[1].MessageId);
        WireToGateEnvelope[] envelopes = replayed
            .Select(item => WireToGateProtocolSerializer.DeserializeAndValidate(item.WireLine, "AGV-8005-01"))
            .ToArray();
        Assert.Equal([1L, 2L], envelopes.Select(item => item.SessionGeneration).ToArray());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(envelopes[0].Payload.GetRawText()),
            JsonNode.Parse(envelopes[1].Payload.GetRawText())));
        Assert.NotEqual(replayed[0].WireLine, replayed[1].WireLine);
        Assert.Equal(1, server.AcceptedSafetyStateChangedCount);
        Assert.Equal(0, io.UnlockCount);

        await using SqliteWireToGateJournal journal = new(journalPath);
        await journal.InitializeAsync(testToken);
        Assert.Empty(await journal.ReadUnacknowledgedOutgoingAsync(testToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task DelayedStoppedSafetyRevisionRecoversSessionToReadyWithoutIoSideEffects()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            RequireSafeSafetyForReadiness = true,
            SendReadinessAfterRecoveryAck = true,
            SendReadinessAfterSafetyStateChangedAck = true
        };
        bool vehicleStopped = false;
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(
            server,
            io,
            NewJournalPath(),
            vehicleStoppedProvider: () => vehicleStopped);

        WireToGateSessionSnapshot blocked = await client.ConnectAndRecoverAsync(testToken);

        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, blocked.Readiness);
        Assert.Contains("DEPARTURE_SAFETY_NOT_READY", blocked.ReasonCodes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendOperationProgressAsync(
            "44444444-4444-4444-4444-444444444444",
            "PREPARING",
            [],
            [],
            cancellationToken: testToken));

        vehicleStopped = true;
        await client.SendSafetyStateChangedAsync(
            2,
            DateTimeOffset.UtcNow,
            new WireToGateSafetySummaryPayload(true, true, true, true, false, []),
            [1, 2, 3, 4, 5, 6, 7, 8],
            testToken);
        await WaitUntilAsync(
            () => client.Current.Readiness == WireToGateSessionReadiness.Ready,
            testToken);

        Assert.Equal(2, client.Current.SafetyStateVersion);
        Assert.Empty(client.Current.ReasonCodes);
        Assert.Equal(1, server.AcceptedSafetyStateChangedCount);
        Assert.Equal(0, io.UnlockCount);
        Assert.Empty(server.StaleGenerationRejections);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task FailedThenStoppedProviderRefreshFlowsThroughBusinessServiceToReady()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            RequireSafeSafetyForReadiness = true,
            SendReadinessAfterRecoveryAck = true,
            SendReadinessAfterSafetyStateChangedAck = true
        };
        SequenceSafetyHandler handler = new()
        {
            ObservedAtOffset = TimeSpan.FromMilliseconds(100)
        };
        using HttpClient httpClient = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = new(
            new VehicleSafetySettings
            {
                Enabled = true,
                Endpoint = "http://control.test/api/onboard/v1/vehicle-safety",
                CredentialEnvironmentVariable = CredentialVariable,
                ExpectedVehicleKey = "AGV-8005-01",
                MaximumEvidenceAgeMs = 5_000,
                ClockSkewToleranceMs = 500,
                PollIntervalMs = 1_000,
                RequestTimeoutMs = 2_000
            },
            httpClient,
            startPolling: false,
            credentialReader: () => "g2-test-credential");
        FakeIoModuleClient io = new();
        NullLogger logger = new();
        await using WireToGateSessionService session = new(
            CreateSessionOptions(server),
            io,
            new SqliteWireToGateJournal(NewJournalPath()),
            logger,
            new SystemClock(),
            provider,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));
        await using WireToGateBusinessService business = new(
            session,
            io,
            logger,
            new SystemClock(),
            () => provider.Read().MotionState == VehicleMotionState.Stopped,
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(30)),
            "W2G_G2_OPERATOR_ID",
            provider,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));

        await provider.RefreshAsync(testToken);
        Assert.Equal(VehicleMotionState.Unknown, provider.Read().MotionState);
        WireToGateSessionSnapshot blocked = await session.Client.ConnectAndRecoverAsync(testToken);
        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, blocked.Readiness);
        DateTimeOffset rebasedAt = DateTimeOffset.UtcNow;
        await session.SendSafetyStateChangedAsync(
            10,
            rebasedAt,
            WireToGateSafetyEvaluator.Evaluate(
                io.CurrentSnapshot,
                provider.Read(),
                rebasedAt,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(500)),
            [1, 2, 3, 4, 5, 6, 7, 8],
            testToken);
        business.Start();
        await WaitUntilAsync(() => server.AcceptedSafetyStateChangedCount == 2, testToken);

        handler.ReturnStopped = true;
        await provider.RefreshAsync(testToken);
        await WaitUntilAsync(
            () => session.Current.Readiness == WireToGateSessionReadiness.Ready,
            testToken);

        Assert.Equal(VehicleMotionState.Stopped, provider.Read().MotionState);
        Assert.Equal(3, server.AcceptedSafetyStateChangedCount);
        Assert.True(session.Current.SafetyStateVersion >= 12);
        long[] changedVersions = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SafetyStateChanged")
            .Select(item => WireToGateProtocolSerializer.DeserializeAndValidate(
                item.WireLine,
                "AGV-8005-01"))
            .Select(envelope => envelope.Payload
                .GetProperty("safetyStateVersion")
                .GetInt64())
            .ToArray();
        Assert.Equal([10L, 11L, 12L], changedVersions);
        Assert.Empty(session.Current.ReasonCodes);
        Assert.Equal(0, io.UnlockCount);
        Assert.Empty(server.StaleGenerationRejections);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task BusinessSafetySendFailureDisconnectsAndReplaysPendingRevision()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            RequireSafeSafetyForReadiness = true,
            SendReadinessAfterRecoveryAck = true,
            SendReadinessAfterSafetyStateChangedAck = true
        };
        RecordedVehicleSafetySignalProvider provider = new(new VehicleSafetySignal(
            VehicleMotionState.Stopped,
            DateTimeOffset.UtcNow,
            "G2_TEST"));
        FakeIoModuleClient io = new();
        NullLogger logger = new();
        await using WireToGateSessionService session = new(
            CreateSessionOptions(server),
            io,
            new SqliteWireToGateJournal(NewJournalPath()),
            logger,
            new SystemClock(),
            provider,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));
        await using WireToGateBusinessService business = new(
            session,
            io,
            logger,
            new SystemClock(),
            () => provider.Read().MotionState == VehicleMotionState.Stopped,
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(30)),
            "W2G_G2_OPERATOR_ID",
            provider,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));

        business.Start();
        await session.Client.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(() => server.AcceptedSafetyStateChangedCount == 1, testToken);

        server.DropBeforeSafetyStateChangedAck = true;
        provider.Set(VehicleMotionState.Unknown, DateTimeOffset.UtcNow);
        await WaitUntilAsync(
            () => session.Current.Readiness == WireToGateSessionReadiness.Disconnected,
            testToken);

        server.DropBeforeSafetyStateChangedAck = false;
        WireToGateSessionSnapshot recovered = await session.Client.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(
            () => session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            testToken);
        // 重放之后还有第四条：换代要求重新全量上报一份，见
        // BusinessResendsSafetyStateAfterSessionGenerationChangeWhileVehicleIdle。
        await WaitUntilAsync(() => server.AcceptedSafetyStateChangedCount == 3, testToken);

        var changed = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SafetyStateChanged")
            .ToArray();
        Assert.Equal(4, changed.Length);
        Assert.Equal(changed[1].MessageId, changed[2].MessageId);
        Assert.NotEqual(changed[2].MessageId, changed[3].MessageId);
        Assert.Equal(3, server.AcceptedSafetyStateChangedCount);
        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, recovered.Readiness);
        Assert.Equal(0, io.UnlockCount);
        Assert.Empty(server.StaleGenerationRejections);
    }

    /// <summary>
    /// 会话换代之后，车静止不动——IO 快照与车辆安全信号一个字节都没变——车载端仍必须重新上报一份
    /// 安全快照。缺陷 20260908-session-recovery-required-never-clears-while-vehicle-idle 就是这条
    /// 不成立：_lastSafetySignature 是进程内去重状态，换代时不重置，于是服务端换代后手上那份
    /// departureSafe 永远等不到更新，readiness 卡在 RecoveryRequired /
    /// DEPARTURE_SAFETY_NOT_READY。真车上卡了 6 分 36 秒，直到有人重启车载客户端。
    ///
    /// 静止是关键条件。车一动安全签名自然会变，去重就跨过去了——那次五趟实跑里飞行途中掉进去的
    /// 那趟两分钟就自愈了，停着的那趟没有。
    ///
    /// 换代用「安全消息的 ack 丢了」制造，而不是现场那样的干净重连：FakeControlServer 跨重连保留
    /// 快照 revision 记忆（见 SameRevisionDifferentContentFailsClosedWithProtocolProblemReasonCode），
    /// 而重连必然换 sessionGeneration、整信封哈希必然变，所以干净重连在这个假服务端上一定会以
    /// SNAPSHOT_REVISION_CONTENT_CONFLICT 收场，建模不了。走重放这条路还多盖住一处：重放被服务端
    /// 认下之后，pending 对账会把 _lastSafetySignature 重新填上，换代重置必须排在它之后才有效。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    public async Task BusinessResendsSafetyStateAfterSessionGenerationChangeWhileVehicleIdle()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            DropBeforeSafetyStateChangedAck = true,
            SendReadinessAfterRecoveryAck = true,
            SendReadinessAfterSafetyStateChangedAck = true
        };
        RecordedVehicleSafetySignalProvider provider = new(new VehicleSafetySignal(
            VehicleMotionState.Stopped,
            DateTimeOffset.UtcNow,
            "G2_TEST"));
        FakeIoModuleClient io = new();
        NullLogger logger = new();
        await using WireToGateSessionService session = new(
            CreateSessionOptions(server),
            io,
            new SqliteWireToGateJournal(NewJournalPath()),
            logger,
            new SystemClock(),
            provider,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));
        await using WireToGateBusinessService business = new(
            session,
            io,
            logger,
            new SystemClock(),
            () => provider.Read().MotionState == VehicleMotionState.Stopped,
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(30)),
            "W2G_G2_OPERATOR_ID",
            provider,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));

        business.Start();
        await session.Client.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(
            () => session.Current.Readiness == WireToGateSessionReadiness.Disconnected,
            testToken);

        server.DropBeforeSafetyStateChangedAck = false;
        WireToGateSessionSnapshot recovered = await session.Client.ConnectAndRecoverAsync(testToken);
        Assert.Equal(2L, recovered.SessionGeneration!.Value);

        // 修复前这里会永远停在 1：重放被认下之后签名又变回原值，而车静止、签名不变，
        // 去重把重发挡住了。
        await WaitUntilAsync(() => server.AcceptedSafetyStateChangedCount == 2, testToken);

        var changed = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SafetyStateChanged")
            .ToArray();
        Assert.Equal(3, changed.Length);
        Assert.Equal([1, 2, 2], changed.Select(item => item.Connection).ToArray());
        // 前两条是同一条消息的重放，第三条才是换代后重新评估出来的。内容一样但版本变了，
        // 所以 messageId 不同——服务端按 messageId 去重，原样重放那条到不了任何地方。
        Assert.Equal(changed[0].MessageId, changed[1].MessageId);
        Assert.NotEqual(changed[1].MessageId, changed[2].MessageId);
        Assert.Equal(0, io.UnlockCount);
        Assert.Empty(server.StaleGenerationRejections);
    }

    private static string[] InboundMessageTypes(FakeControlServer server) =>
        server.Received
            .Select(item => item.MessageType)
            .Where(messageType => messageType != "CONNECTED")
            .ToArray();

    private static WireToGateSessionClient CreateClient(
        FakeControlServer server,
        FakeIoModuleClient io,
        string journalPath,
        long capability = 1,
        long safety = 1,
        string? onboardInstanceId = null,
        Func<bool>? vehicleStoppedProvider = null,
        TimeSpan? messageTimeout = null)
    {
        WireToGateSessionOptions options = CreateSessionOptions(
            server,
            capability,
            safety,
            onboardInstanceId,
            messageTimeout);
        return new WireToGateSessionClient(
            options,
            io,
            new SqliteWireToGateJournal(journalPath),
            new SystemClock(),
            new DelegateVehicleSafetySignalProvider(vehicleStoppedProvider ?? (() => true)),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));
    }

    private static WireToGateSessionOptions CreateSessionOptions(
        FakeControlServer server,
        long capability = 1,
        long safety = 1,
        string? onboardInstanceId = null,
        TimeSpan? messageTimeout = null) =>
        new(
            "127.0.0.1",
            server.Port,
            "AGV-8005-01",
            onboardInstanceId ?? Guid.NewGuid().ToString("D"),
            new string('a', 40),
            CredentialVariable,
            TimeSpan.FromSeconds(2),
            messageTimeout ?? TimeSpan.FromSeconds(2),
            capability,
            safety,
            "eight-slot-v1",
            "eight-slot-modbus-v1",
            SupportsBatchUnlock: false);

    private static string NewJournalPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "w2g-g2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "journal.db");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class SequenceSafetyHandler : HttpMessageHandler
    {
        public bool ReturnStopped { get; set; }

        public TimeSpan ObservedAtOffset { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            if (!ReturnStopped)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    vehicleKey = "AGV-8005-01",
                    motionState = "STOPPED",
                    observedAt = DateTimeOffset.UtcNow + ObservedAtOffset,
                    source = "CONTROL_SERVER",
                    reasonCodes = Array.Empty<string>()
                })
            });
        }
    }

    private sealed class DelegateVehicleSafetySignalProvider(Func<bool> isStopped)
        : IVehicleSafetySignalProvider
    {
        public VehicleSafetySignal Read() => new(
            isStopped() ? VehicleMotionState.Stopped : VehicleMotionState.Unknown,
            DateTimeOffset.UtcNow,
            "G2_TEST");
    }

    private sealed class NullLogger : IAppLogger
    {
        public event EventHandler<LogEntryEventArgs>? EntryWritten;

        public void Write(
            LogSeverity severity,
            string source,
            string message,
            Exception? exception = null)
        {
            _ = severity;
            _ = source;
            _ = message;
            _ = exception;
        }
    }
}
