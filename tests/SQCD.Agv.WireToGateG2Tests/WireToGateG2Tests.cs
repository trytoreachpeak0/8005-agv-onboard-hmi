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

    [Fact]
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
            ["VEHICLE_STATE_UNKNOWN"]);

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

        var changed = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SafetyStateChanged")
            .ToArray();
        Assert.Equal(3, changed.Length);
        Assert.Equal(changed[^2].MessageId, changed[^1].MessageId);
        Assert.Equal(2, server.AcceptedSafetyStateChangedCount);
        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, recovered.Readiness);
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
        Func<bool>? vehicleStoppedProvider = null)
    {
        WireToGateSessionOptions options = CreateSessionOptions(
            server,
            capability,
            safety,
            onboardInstanceId);
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
        string? onboardInstanceId = null) =>
        new(
            "127.0.0.1",
            server.Port,
            "AGV-8005-01",
            onboardInstanceId ?? Guid.NewGuid().ToString("D"),
            new string('a', 40),
            CredentialVariable,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
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
