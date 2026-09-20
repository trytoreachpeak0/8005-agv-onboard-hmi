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

public sealed partial class WireToGateG2Tests
{
    private const string CredentialVariable = "W2G_G2_TEST_CREDENTIAL";

    static WireToGateG2Tests()
    {
        Environment.SetEnvironmentVariable(CredentialVariable, "g2-test-credential");
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SESSION-RECOVERY-HAPPY")]
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
            // 协议 v2 起，握手里多一份 OnboardAlarmSnapshot：车一上线就把当下的全量告警报一次，
            // 服务端因此不需要任何补发就有当下的事实（REQ-0269）。
            [
                "SessionHello", "CapabilitySnapshot", "SafetyStateSnapshot", "OnboardAlarmSnapshot",
                "RecoveryStateReport"
            ],
            InboundMessageTypes(server));
        Assert.Equal(0, io.UnlockCount);
    }

    /// <summary>
    /// 恢复握手途中断线：<c>RecoveryStateReport</c> 已经写进 journal 发了出去，ack 没回来。下一条连接不补发这份旧
    /// 报告，照常走完整握手、发一份新的——恢复状态报告说的是发出它那次握手时的事实，新报告取代它，旧的在新报告被
    /// 确认之后标为已确认。补发旧报告在真服务端面前走不通：服务端按它重算就绪、立即回 <c>SessionReadiness</c>，再
    /// 重放待确认的恢复命令，全都夹在车还没发完的快照中间（8005-agv-control-server#33）。
    /// <c>CV-SESSION-RECONNECT-DURING-RECOVERY</c> 只列要紧的几条报文，快照的位置由
    /// <c>CV-SESSION-RECOVERY-HAPPY</c> 规定。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    public async Task ReconnectDuringRecoverySupersedesInterruptedReportWithFreshHandshake()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { DropBeforeRecoveryAck = true };
        string journalPath = NewJournalPath();
        FakeIoModuleClient io = new();
        await using SqliteWireToGateJournal journal = new(journalPath);
        await using WireToGateSessionClient client = CreateClient(server, io, journalPath);

        await journal.InitializeAsync(testToken);
        // 当下的恢复现场：一个未结清的装卸 attempt、已经开过锁、被强制恢复过一轮。被中断的那份旧报告
        // 说的是另一套事实（没有未结清 attempt、NONE、没开锁），所以「新报告取代旧报告」不只是换了个
        // messageId——下面逐字段断言两条连接发出去的都是**这一套**事实。
        const string unsettledAttemptId = "44444444-4444-4444-8444-444444444444";
        await journal.WriteRecoveryStateAsync(
            new WireToGateRecoveryState(
                unsettledAttemptId,
                WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                [3],
                7,
                []),
            testToken);
        string journalContentSha256 = await journal.ComputeContentSha256Async(testToken);
        string originalMessageId = Guid.NewGuid().ToString("D");
        string originalReportId = Guid.NewGuid().ToString("D");
        WireToGateEnvelope originalReport = WireToGateProtocolSerializer.Create(
            "RecoveryStateReport",
            originalMessageId,
            null,
            "AGV-8005-01",
            1,
            DateTimeOffset.UtcNow,
            new
            {
                reportId = originalReportId,
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

        // The fresh report names the unsettled attempt, so the server answers RECOVERY_REQUIRED, as the real one does
        // (onboard-hmi#128); until then this double answered READY over it. What is under test is the handshake.
        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, resumed.Readiness);
        Assert.Equal(
            [
                "CONNECTED", "SessionHello", "CapabilitySnapshot", "SafetyStateSnapshot", "OnboardAlarmSnapshot",
                "RecoveryStateReport",
                "CONNECTED", "SessionHello", "CapabilitySnapshot", "SafetyStateSnapshot", "OnboardAlarmSnapshot",
                "RecoveryStateReport"
            ],
            server.Received.Select(item => item.MessageType).ToArray());
        Assert.Equal(0, io.UnlockCount);
        Assert.Empty(server.StaleGenerationRejections);

        // 两条连接各自发了自己那份报告；被中断的那一份再也没有出去过。
        var reports = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "RecoveryStateReport")
            .ToArray();
        Assert.Equal([1, 2], reports.Select(item => item.Connection).ToArray());
        Assert.DoesNotContain(reports, item => item.MessageId == originalMessageId);
        Assert.NotEqual(reports[0].MessageId, reports[1].MessageId);

        // 两份新报告的内容：都点名当下这个未结清 attempt 与它的恢复现场，都不是被中断那份说的事实。
        // 报告排在换代之后，所以 sessionGeneration 逐条递增；journalContentSha256 两份相同，因为
        // ComputeContentSha256Async 本来就把恢复状态报告排除在待确认业务报文之外——被中断那一份躺在
        // outbox 里也不会改变它。
        WireToGateEnvelope[] reportEnvelopes = reports
            .Select(item => WireToGateProtocolSerializer.DeserializeAndValidate(item.WireLine, "AGV-8005-01"))
            .ToArray();
        Assert.Equal([1L, 2L], reportEnvelopes.Select(item => item.SessionGeneration).ToArray());
        JsonElement[] reportPayloads = reportEnvelopes.Select(item => item.Payload).ToArray();
        Assert.All(reportPayloads, payload =>
        {
            Assert.Equal(
                unsettledAttemptId,
                payload.GetProperty("unsettledSlotOperationAttemptId").GetString());
            Assert.Equal("ACTIVE_UNLOCK_SET", payload.GetProperty("provenRecoveryCheckpoint").GetString());
            Assert.Equal(
                [3],
                payload.GetProperty("activeUnlockSlots").EnumerateArray().Select(slot => slot.GetInt32()));
            Assert.Equal(7L, payload.GetProperty("forcedRecoveryGeneration").GetInt64());
            Assert.Empty(payload.GetProperty("pendingResults").EnumerateArray());
            Assert.Equal(journalContentSha256, payload.GetProperty("journalContentSha256").GetString());
            Assert.NotEqual(originalReportId, payload.GetProperty("reportId").GetString());
        });
        Assert.NotEqual(
            reportPayloads[0].GetProperty("reportId").GetString(),
            reportPayloads[1].GetProperty("reportId").GetString());

        WireToGateDurableMessage? stored = await journal.ReadOutgoingByDeduplicationKeyAsync(
            "recovery:0:interrupted",
            testToken);
        Assert.NotNull(stored);
        Assert.True(stored!.Acknowledged);
        Assert.Equal(originalContentSha256, stored.ContentSha256);
        Assert.Equal(originalWireLine, stored.WireLine);
        Assert.Empty(await journal.ReadUnacknowledgedOutgoingAsync(testToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-REPLACE-AND-ACK")]
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
            // 第二次连接的告警快照序号仍然是 1——告警板的序号活在进程里，新的客户端从头开始。它照样
            // 被采纳，因为采纳判据是 (会话代, 序号)：不这样的话，车重启之后它的告警就再也上不去，
            // 看板停在重启前那一批，正是 REQ-0269 禁止的旧值。
            [
                ("CapabilitySnapshot", 1L), ("SafetyStateSnapshot", 1L), ("OnboardAlarmSnapshot", 1L),
                ("CapabilitySnapshot", 2L), ("SafetyStateSnapshot", 2L), ("OnboardAlarmSnapshot", 1L)
            ],
            server.AppliedSnapshots.ToArray());
        Assert.Equal(0, io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-SAME-REVISION-CONFLICT")]
    public async Task SameRevisionDifferentContentFailsClosedWithProtocolProblemReasonCode()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            RetainSnapshotRevisionsAcrossSessions = true
        };
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
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-SAME-CONTENT")]
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
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
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
            "SESSION",
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
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
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

    /// <summary>
    /// CV-MANUAL-CHARGING-RETURN from the operator's entry: the business service sends the request with
    /// the configured administrator, reports what the control server decided, and leaves the manual
    /// charging hold exactly as the server last published it (REQUEST_RETURN_WITH_OPERATOR_CONTEXT,
    /// NEVER_CLEAR_HOLD_LOCALLY).
    /// </summary>
    /// <remarks>
    /// Before 2026-09-14 only the session client could send this request; nothing an operator could
    /// reach did, so the vector had no path from the HMI at all.
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
    [InlineData("RETURNED_TO_ELIGIBILITY_EVALUATION", true)]
    [InlineData("REJECTED", false)]
    public async Task TheManualChargingReturnEntrySendsTheAdministratorAndLeavesTheHoldToTheServer(
        string outcome,
        bool accepted)
    {
        const string operatorVariable = "W2G_G2_MANUAL_RETURN_OPERATOR";
        const string proofVariable = "W2G_G2_MANUAL_RETURN_PROOF";
        Environment.SetEnvironmentVariable(operatorVariable, "maintenance-007");
        Environment.SetEnvironmentVariable(proofVariable, "manual-return-proof");
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendJourneySnapshotsAfterRecovery = true,
            ManualChargingHoldInSnapshots = true,
            RespondToManualChargingReturnToServiceRequests = true,
            ManualChargingReturnToServiceOutcome = outcome,
            ManualChargingReturnToServiceProblem = accepted
                ? null
                : new WireToGateProblemPayload(
                    "SESSION_RECOVERY_REQUIRED",
                    "payload.requestId",
                    "The session has facts to reconcile before the vehicle can take work again.")
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionService session = CreateManualReturnSession(server, io);
        await using WireToGateBusinessService business = CreateManualReturnBusiness(
            session, io, operatorVariable, proofVariable);
        List<WireToGateOperatorEvent> events = [];
        business.OperatorEventPublished += (_, args) =>
        {
            lock (events)
            {
                events.Add(args.Value);
            }
        };

        business.Start();
        await session.Client.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(
            () => session.Client.CurrentJourney.VehicleBusinessState?.ManualChargingHold == true,
            testToken);

        Assert.True(business.CanRequestManualChargingReturnToService);
        Assert.Equal(
            accepted,
            await business.RequestManualChargingReturnToServiceAsync("手动充电结束，申请返回服务。", 86.5, testToken));

        var request = server.ReceivedEnvelopes.Single(item => item.MessageType == "ManualChargingReturnToServiceRequested");
        using JsonDocument document = JsonDocument.Parse(request.WireLine);
        JsonElement payload = document.RootElement.GetProperty("payload");
        Assert.Equal("maintenance-007", payload.GetProperty("administrator").GetProperty("operatorId").GetString());
        Assert.Equal("MAINTENANCE_ADMINISTRATOR", payload.GetProperty("administratorRole").GetString());
        Assert.Equal("手动充电结束，申请返回服务。", payload.GetProperty("reason").GetString());
        Assert.Equal(86.5, payload.GetProperty("observedBatteryPercent").GetDouble());
        Assert.Equal(payload.GetProperty("requestId").GetString(), request.MessageId);

        // The hold is the server's to lift, whatever it decided.
        Assert.True(session.Client.CurrentJourney.VehicleBusinessState!.ManualChargingHold);
        lock (events)
        {
            Assert.Contains(
                events,
                item => item.Kind == (accepted ? "MANUAL_CHARGING_RETURN_ACCEPTED" : "RECOVERY_BLOCKED"));
        }
    }

    /// <summary>
    /// REQUIRE_VERIFIED_ADMINISTRATOR on the vehicle's side: without the configured administrator
    /// proof the entry is not offered and pressing it anyway sends nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
    public async Task TheManualChargingReturnEntryIsNotOfferedWithoutAVerifiedAdministrator()
    {
        const string operatorVariable = "W2G_G2_MANUAL_RETURN_UNVERIFIED_OPERATOR";
        const string proofVariable = "W2G_G2_MANUAL_RETURN_UNSET_PROOF";
        Environment.SetEnvironmentVariable(operatorVariable, "maintenance-008");
        Environment.SetEnvironmentVariable(proofVariable, null);
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            RespondToManualChargingReturnToServiceRequests = true
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionService session = CreateManualReturnSession(server, io);
        await using WireToGateBusinessService business = CreateManualReturnBusiness(
            session, io, operatorVariable, proofVariable);

        business.Start();
        await session.Client.ConnectAndRecoverAsync(testToken);

        Assert.False(business.CanRequestManualChargingReturnToService);
        Assert.False(await business.RequestManualChargingReturnToServiceAsync("手动充电结束，申请返回服务。", null, testToken));
        Assert.DoesNotContain(server.Received, item => item.MessageType == "ManualChargingReturnToServiceRequested");
    }

    private static WireToGateSessionService CreateManualReturnSession(FakeControlServer server, FakeIoModuleClient io) =>
        new(
            CreateSessionOptions(server),
            io,
            new SqliteWireToGateJournal(NewJournalPath()),
            new NullLogger(),
            new SystemClock(),
            new DelegateVehicleSafetySignalProvider(() => true),
            new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));

    private static WireToGateBusinessService CreateManualReturnBusiness(
        WireToGateSessionService session,
        FakeIoModuleClient io,
        string operatorVariable,
        string proofVariable) =>
        new(
            session,
            io,
            new NullLogger(),
            new SystemClock(),
            () => true,
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

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
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
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
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
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
    [Trait("ProtocolVector", "CV-REQUEST-FIRST-RESULT-REPLAY")]
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
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SESSION-RECOVERY-HAPPY")]
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
                new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
                new SlotConfigurationActivationCoordinator(
                    new DocumentActiveSlotConfigurationStore(
                        new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                    TimeProvider.System),
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
    /// The recovery entry has to open on a session that turned RECOVERY_REQUIRED *while it was
    /// running*, not only on one that handshook that way. On the real vehicle the trigger is a
    /// refused load result: the server applies it, decides the session needs recovery, and appends
    /// one SessionReadiness line to the result ack. By then the vehicle has already cleared its own
    /// recovery state -- recording the result is what clears it -- so the entry has to open with no
    /// checkpoint and no server recovery-session snapshot to lean on.
    /// </summary>
    [Fact]
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
                new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
                new SlotConfigurationActivationCoordinator(
                    new DocumentActiveSlotConfigurationStore(
                        new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                    TimeProvider.System),
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
                            ["LOCK_NOT_CLOSED"])
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
    /// A door shut over an empty slot is reopened with no limit (ADR-cross-0058 decision 1), and the
    /// operator has to see every round of it. The HMI deduplicates its operation prompts by key, and a
    /// reopen repeats the previous round's phase and slot sets exactly -- a key without the round in
    /// it swallows every prompt after the first, and the vehicle reopens a door in silence.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task EveryReopenRoundReachesTheOperatorAsItsOwnPrompt()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendSlotOperationCommandAfterRecovery = true
        };
        FakeIoModuleClient io = new() { SimulateOperatorLoad = true, EmptyClosesBeforeLoad = 2 };
        NullLogger logger = new();
        await using WireToGateSessionService session = new(
            CreateSessionOptions(server),
            io,
            new SqliteWireToGateJournal(NewJournalPath()),
            logger,
            new SystemClock(),
            new DelegateVehicleSafetySignalProvider(() => true),
            new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
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
            "W2G_G2_REOPEN_PROMPT_OPERATOR");
        List<WireToGateOperatorEvent> prompts = [];
        bool completed = false;
        business.OperatorEventPublished += (_, args) =>
        {
            lock (prompts)
            {
                if (args.Value.Kind == "OPERATION_PROGRESS")
                {
                    prompts.Add(args.Value);
                }
                else if (args.Value.Kind == "OPERATION_COMPLETED")
                {
                    completed = true;
                }
            }
        };

        business.Start();
        await session.Client.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(() => completed, testToken);

        Assert.Equal(3, io.UnlockCount);
        string[] waiting;
        string[] unlocking;
        lock (prompts)
        {
            waiting = prompts
                .Where(item => item.Operation?.Stage == WireToGateHmiOperationStage.WaitingOperator)
                .Select(item => item.Message)
                .ToArray();
            unlocking = prompts
                .Where(item => item.Operation?.Stage == WireToGateHmiOperationStage.Unlocking)
                .Select(item => item.Message)
                .ToArray();
        }

        Assert.Equal(
            ["请在1号仓放入货物并关门。", "请在1号仓放入货物并关门。（第2次提示）", "请在1号仓放入货物并关门。（第3次提示）"],
            waiting);
        Assert.Equal(
            ["正在打开1号仓。", "1号仓关门时货物状态与预期不符，正在重新打开。", "1号仓关门时货物状态与预期不符，正在重新打开。"],
            unlocking);
    }

    /// <summary>
    /// REQ-0237 keeps load correction to the time before the vehicle leaves the pickup, and the
    /// control server now holds the vehicle there for a departure wait after the load commits. The
    /// operator can only use that wait if 「修正装货」 is offered as soon as the load is recorded.
    /// </summary>
    /// <remarks>
    /// The entry reads a cached copy of the recovery state, and recording the result wrote the
    /// journal without refreshing that copy -- the entry waited for some unrelated session event.
    /// On the real rig that event was the departure itself: the G3 FP-IS-02 run closed its second
    /// door at 21:29:33, the server sent the vehicle away at 21:29:55 after its 20 s wait, and the
    /// entry appeared at 21:29:57. This fake IO raises no snapshot events, so nothing else refreshes
    /// the copy here either.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task ACompletedLoadOffersTheCorrectionEntryAsSoonAsItsResultIsRecorded()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        const string operatorVariable = "W2G_G2_CORRECTION_ENTRY_OPERATOR";
        string? previousOperator = Environment.GetEnvironmentVariable(operatorVariable);
        Environment.SetEnvironmentVariable(operatorVariable, "operator-003");

        try
        {
            await using FakeControlServer server = new(IPAddress.Loopback)
            {
                SendReadinessAfterRecoveryAck = true,
                SendSlotOperationCommandAfterRecovery = true
            };
            FakeIoModuleClient io = new() { SimulateOperatorLoad = true };
            NullLogger logger = new();
            await using WireToGateSessionService session = new(
                CreateSessionOptions(server),
                io,
                new SqliteWireToGateJournal(NewJournalPath()),
                logger,
                new SystemClock(),
                new DelegateVehicleSafetySignalProvider(() => true),
                new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
                new SlotConfigurationActivationCoordinator(
                    new DocumentActiveSlotConfigurationStore(
                        new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                    TimeProvider.System),
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
                operatorVariable,
                recoveryOptions: new WireToGateRecoveryOptions(
                    true,
                    "W2G_G2_CORRECTION_ENTRY_PROOF",
                    "MAINTENANCE_ADMINISTRATOR",
                    "CONFIGURED_PROOF"));
            bool completed = false;
            business.OperatorEventPublished += (_, args) =>
            {
                if (args.Value.Kind == "OPERATION_COMPLETED")
                {
                    completed = true;
                }
            };

            business.Start();
            await session.Client.ConnectAndRecoverAsync(testToken);
            Assert.False(business.CanRequestLoadCorrection);

            await WaitUntilAsync(() => completed, testToken);
            Assert.Equal(1, io.UnlockCount);

            await WaitUntilAsync(() => business.CanRequestLoadCorrection, testToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(operatorVariable, previousOperator);
        }
    }

    /// <summary>
    /// CV-PREDEPARTURE-SAFETY-EXPIRES, the vehicle's half: a PreDepartureSafetyCheck that asks about a
    /// safety state older than the one this vehicle has already had accepted is spent. Answering it
    /// would report today's safety against yesterday's question, so the vehicle refuses it as
    /// PREDEPARTURE_CHECK_EXPIRED and keeps the session, and the control server asks again against
    /// the current version.
    /// </summary>
    /// <remarks>
    /// The second case is the control: a check that is not behind is answered as before, so the
    /// refusal cannot pass by refusing everything. Before 2026-09-13 the vehicle answered every check
    /// and neither end ever produced PREDEPARTURE_CHECK_EXPIRED.
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    [InlineData(0L, true)]
    [InlineData(1_000_000L, false)]
    public async Task ACheckAskedAboutAnOlderSafetyStateIsRefusedAsExpiredWithoutEndingTheSession(
        long expectedSafetyStateVersion,
        bool expired)
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            PreDepartureSafetyCheckExpectedVersionAfterRecovery = expectedSafetyStateVersion
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
            new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
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
            "W2G_G2_EXPIRED_CHECK_OPERATOR");

        business.Start();
        await session.Client.ConnectAndRecoverAsync(testToken);

        string answer = expired ? "ProtocolProblem" : "PreDepartureSafetyCheckResult";
        string mustNotAppear = expired ? "PreDepartureSafetyCheckResult" : "ProtocolProblem";
        await WaitUntilAsync(() => server.Received.Any(item => item.MessageType == answer), testToken);

        if (expired)
        {
            string line = server.ReceivedEnvelopes.First(envelope => envelope.MessageType == "ProtocolProblem").WireLine;
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement payload = document.RootElement.GetProperty("payload");
            Assert.Equal("PreDepartureSafetyCheck", payload.GetProperty("rejectedMessageType").GetString());
            Assert.Equal(
                "PREDEPARTURE_CHECK_EXPIRED",
                payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        }
        Assert.DoesNotContain(server.Received, item => item.MessageType == mustNotAppear);
        Assert.True(session.Current.Connected);
    }

    /// <summary>
    /// CV-OPERATION-RESULT-UNKNOWN-RECONCILE, the vehicle's half: a load that ends UNKNOWN is reported
    /// as UNKNOWN and acknowledged; after a restart the RecoveryStateReport names that result as
    /// pending, and once the report is acknowledged the vehicle replays the same result under the new
    /// session generation -- without touching a door again.
    /// </summary>
    /// <remarks>
    /// Before 2026-09-13 pendingResults was always empty, because nothing ever put a result in it, and
    /// an acknowledged result was never sent again: the control server had no way to reconcile from
    /// the vehicle's journal, which is what the vector's replay is for.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnUnknownResultIsReportedAsPendingAfterARestartAndReplayedOnceTheReportIsAcknowledged()
    {
        const string attemptId = "44444444-4444-4444-4444-444444444444";
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendSlotOperationCommandAfterRecovery = true
        };
        FakeIoModuleClient io = new() { LockerWaitTimesOut = true };
        NullLogger logger = new();
        string journalPath = NewJournalPath();

        WireToGateSessionService NewSession() => new(
            CreateSessionOptions(server),
            io,
            new SqliteWireToGateJournal(journalPath),
            logger,
            new SystemClock(),
            new DelegateVehicleSafetySignalProvider(() => true),
            new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));
        WireToGateBusinessService NewBusiness(WireToGateSessionService session) => new(
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
            "W2G_G2_UNKNOWN_RECONCILE_OPERATOR");

        await using (WireToGateSessionService session = NewSession())
        await using (WireToGateBusinessService business = NewBusiness(session))
        {
            // The same kind is also raised as soon as the journal holds the unsettled operation, long
            // before its result is sent; only the one after the server has the result means the
            // DurableAck came back.
            bool acknowledged = false;
            business.OperatorEventPublished += (_, args) =>
            {
                if (args.Value.Kind == "OPERATION_RECOVERY_REQUIRED"
                    && server.ReceivedEnvelopes.Any(item => item.MessageType == "OperationResult"))
                {
                    acknowledged = true;
                }
            };
            business.Start();
            await session.Client.ConnectAndRecoverAsync(testToken);
            await WaitUntilAsync(() => acknowledged, testToken);
        }

        int unlocksBeforeRestart = io.UnlockCount;
        server.SendSlotOperationCommandAfterRecovery = false;
        server.SimulateOnboardProcessRestart();
        await using (WireToGateSessionService session = NewSession())
        await using (WireToGateBusinessService business = NewBusiness(session))
        {
            business.Start();
            await session.Client.ConnectAndRecoverAsync(testToken);
            await WaitUntilAsync(
                () => server.ReceivedEnvelopes.Count(item => item.MessageType == "OperationResult") == 2,
                testToken);
            Assert.True(session.Current.Connected);
        }

        (int Connection, string MessageType, string MessageId, string WireLine)[] results = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "OperationResult")
            .ToArray();
        Assert.NotEqual(results[0].Connection, results[1].Connection);
        Assert.Equal(results[0].MessageId, results[1].MessageId);
        string[] afterRestart = server.ReceivedEnvelopes
            .Where(item => item.Connection == results[1].Connection)
            .Select(item => item.MessageType)
            .ToArray();
        Assert.True(
            Array.IndexOf(afterRestart, "RecoveryStateReport") < Array.IndexOf(afterRestart, "OperationResult"),
            string.Join(", ", afterRestart));

        WireToGateEnvelope first = WireToGateProtocolSerializer.DeserializeAndValidate(results[0].WireLine, "AGV-8005-01");
        WireToGateEnvelope replayed = WireToGateProtocolSerializer.DeserializeAndValidate(results[1].WireLine, "AGV-8005-01");
        Assert.Equal("UNKNOWN", first.Payload.GetProperty("overallOutcome").GetString());
        Assert.NotEqual(first.SessionGeneration, replayed.SessionGeneration);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(first.Payload.GetRawText()),
            System.Text.Json.Nodes.JsonNode.Parse(replayed.Payload.GetRawText())));

        WireToGateEnvelope report = server.ReceivedEnvelopes
            .Where(item => item.Connection == results[1].Connection && item.MessageType == "RecoveryStateReport")
            .Select(item => WireToGateProtocolSerializer.DeserializeAndValidate(item.WireLine, "AGV-8005-01"))
            .Single();
        Assert.Equal(attemptId, report.Payload.GetProperty("unsettledSlotOperationAttemptId").GetString());
        JsonElement pending = Assert.Single(report.Payload.GetProperty("pendingResults").EnumerateArray());
        Assert.Equal("OperationResult", pending.GetProperty("messageType").GetString());
        Assert.Equal(results[0].MessageId, pending.GetProperty("messageId").GetString());
        Assert.Equal(attemptId, pending.GetProperty("businessId").GetString());
        Assert.Equal(
            first.Payload.GetProperty("resultContentSha256").GetString(),
            pending.GetProperty("contentSha256").GetString());

        Assert.Equal(unlocksBeforeRestart, io.UnlockCount);
    }

    /// <summary>
    /// A drop-off journey is projected rather than refused.
    /// </summary>
    /// <remarks>
    /// Protocol v2 renamed the two values this exercises: <c>stopRole</c> <c>GATE</c> became
    /// <c>DROPOFF</c> and <c>legType</c> <c>TO_GATE</c> became <c>TO_DROPOFF</c>. The onboard's
    /// inbound validators still named the v1 spellings on 2026-09-09, which meant every drop-off
    /// snapshot the v2 control server sends would have been answered
    /// <c>PROTOCOL_SCHEMA_INVALID</c> -- and nothing said so, because every fixture in this suite
    /// sent the pick-up half, whose values v2 did not change.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task DropoffStopSnapshotsAreProjectedRatherThanRefused()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendJourneySnapshotsAfterRecovery = true,
            SendDropoffStopSnapshots = true
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());

        await client.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(
            () => client.CurrentJourney.CurrentStopWorklist is not null
                && client.CurrentJourney.UpcomingStopPlan is not null,
            testToken);

        WireToGateJourneySnapshot journey = client.CurrentJourney;
        Assert.Equal("DROPOFF", Assert.Single(journey.CurrentStopWorklist!.Items).StopRole);
        SQCD.Agv.Core.WireToGateMovementLeg leg = Assert.Single(journey.UpcomingStopPlan!.Legs);
        Assert.Equal("TO_DROPOFF", leg.LegType);
        Assert.Equal("BUSINESS", leg.StopPurposeCategory);
        Assert.Null(leg.PublicStationFunction);

        // v2 moved demandId into the leg; the plan's demands now come from there rather than from a
        // top-level field the payload no longer carries.
        Assert.Equal([leg.DemandId!], journey.UpcomingStopPlan.DemandIds);
        Assert.Equal(
            journey.CurrentStopWorklist.Items.Single().DemandId,
            Assert.Single(journey.UpcomingStopPlan.DemandIds));
        Assert.DoesNotContain(server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Equal(0, io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-REPLACE-AND-ACK")]
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
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
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
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-SAME-REVISION-CONFLICT")]
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
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
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
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
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
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-SAME-REVISION-CONFLICT")]
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
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
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

    /// <summary>
    /// 协议 v2 消息 9：握手里报一次当下的全量告警，服务端 ack。
    /// </summary>
    /// <remarks>
    /// 车载端在 <c>FP-IS-15</c> 上的义务是 <c>PUBLISH_COMPLETE_ALARM_SET</c> 与
    /// <c>NEVER_PUBLISH_STALE_ALARM_STATE</c>。快照而不是事件流，正是后一条的实现方式：每一份都是当下
    /// 的全部告警，后一份整体取代前一份，重连之后服务端手上立刻是当下的事实，不需要任何补发。所以它发
    /// 在握手里——重连即报，而不是等下一次告警变化才报。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    [Trait("ProtocolVector", "CV-ONBOARD-ALARM-SNAPSHOT")]
    public async Task TheOnboardAlarmSnapshotIsPublishedInTheHandshakeAndAckedUnderItsOwnKind()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());

        await client.ConnectAndRecoverAsync(testToken);

        // 握手的顺序：能力、安全态、告警，然后恢复报告。
        Assert.Equal(
            [
                "SessionHello", "CapabilitySnapshot", "SafetyStateSnapshot", "OnboardAlarmSnapshot",
                "RecoveryStateReport"
            ],
            InboundMessageTypes(server));

        WireToGateEnvelope snapshot = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "OnboardAlarmSnapshot")
            .Select(item => WireToGateProtocolSerializer.DeserializeAndValidate(item.WireLine, "AGV-8005-01"))
            .Single();
        JsonElement payload = snapshot.Payload;
        // 一份空快照也要发：它说的是「此刻没有告警」，与「从没报过」在服务端看板上是两种显示。
        Assert.Equal(JsonValueKind.Array, payload.GetProperty("alarms").ValueKind);
        Assert.Empty(payload.GetProperty("alarms").EnumerateArray());
        Assert.Equal(1, payload.GetProperty("alarmSnapshotRevision").GetInt64());
        // SNAPSHOT，不是 RESPONSE：它自己带 messageId，correlationId 是 null。
        Assert.Null(snapshot.CorrelationId);

        Assert.Contains(
            ("OnboardAlarmSnapshot", 1L),
            server.AppliedSnapshots.ToArray());
        Assert.Equal(0, io.UnlockCount);
    }

    /// <summary>
    /// 协议 v2 消息 9，会话中途：告警板变了就再报一份当下的全量，服务端按同一种 ack 回，会话照常。
    /// </summary>
    /// <remarks>
    /// 握手之前不发，是这条测试第一句断言的事：握手里每条快照都直接读下一行等 ack，那一段里插进一份告警
    /// 快照，它的 ack 会被握手当成自己的下一行读走。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    [Trait("ProtocolVector", "CV-ONBOARD-ALARM-SNAPSHOT")]
    public async Task AnAlarmRaisedMidSessionIsPublishedAsTheWholeCurrentSetAndTheSessionCarriesOn()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        FakeIoModuleClient io = new();
        OnboardAlarmBoard board = new("AGV-G2", TimeProvider.System);
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath(), alarmBoard: board);

        Assert.False(await client.PublishAlarmSnapshotAsync(testToken));

        await client.ConnectAndRecoverAsync(testToken);
        board.Raise(new AlarmEntry(
            OnboardAlarmCodes.IoModuleDisconnected,
            OnboardAlarmEvaluator.Critical,
            DateTimeOffset.UtcNow,
            AlarmScope.CurrentVehicle,
            "仓门控制模块离线。"));

        Assert.True(await client.PublishAlarmSnapshotAsync(testToken));

        JsonElement[] published = AlarmSnapshotPayloads(server);
        Assert.Equal(2, published.Length);
        Assert.Equal(2, published[1].GetProperty("alarmSnapshotRevision").GetInt64());
        Assert.Equal(
            [OnboardAlarmCodes.IoModuleDisconnected],
            published[1].GetProperty("alarms").EnumerateArray().Select(alarm => alarm.GetProperty("code").GetString()));
        Assert.Contains(("OnboardAlarmSnapshot", 2L), server.AppliedSnapshots.ToArray());

        OnboardAlarmPublication acknowledged =
            Assert.IsType<OnboardAlarmPublication>(client.LastAcknowledgedAlarmSnapshot);
        Assert.Equal(client.Current.SessionGeneration, acknowledged.SessionGeneration);
        Assert.Equal(board.Peek().Alarms, acknowledged.Alarms);

        // 这份 ack 由接收循环按 correlationId 交回，没有被当成未处理的消息打断会话：心跳照常往返。
        await client.SendHeartbeatAsync(testToken);
        Assert.Equal(0, io.UnlockCount);
    }

    /// <summary>
    /// 告警监视器：报的是它求值出来的全集，服务端手上已经是这一份时不再报，条件消失时报一份空的。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    [Trait("ProtocolVector", "CV-ONBOARD-ALARM-SNAPSHOT")]
    public async Task TheAlarmMonitorPublishesWhatItEvaluatedAndOnlyWhenTheServerDoesNotAlreadyHoldIt()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        FakeIoModuleClient io = new();
        OnboardAlarmBoard board = new("AGV-G2", TimeProvider.System);
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath(), alarmBoard: board);
        await client.ConnectAndRecoverAsync(testToken);

        bool ioConnected = false;
        await using OnboardAlarmMonitor monitor = new(
            board,
            () =>
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                IoSnapshot snapshot = io.CurrentSnapshot with { ObservedAt = now };
                return new OnboardAlarmInputs(
                    now,
                    ioConnected,
                    snapshot,
                    TimeSpan.FromSeconds(30),
                    null,
                    new OnboardSnapshot(
                        OnboardState.WaitingArrival,
                        false,
                        ioConnected,
                        null,
                        snapshot,
                        null,
                        false,
                        string.Empty,
                        null,
                        now),
                    null,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(500),
                    client.Current.ReasonCodes,
                    null);
            },
            client,
            new NullLogger(),
            TimeSpan.FromMinutes(1));

        await monitor.EvaluateOnceAsync(testToken);
        JsonElement[] afterRaise = AlarmSnapshotPayloads(server);
        Assert.Equal(2, afterRaise.Length);
        Assert.Equal(
            [OnboardAlarmCodes.IoModuleDisconnected],
            afterRaise[1].GetProperty("alarms").EnumerateArray().Select(alarm => alarm.GetProperty("code").GetString()));

        // 条件没变：服务端手上已经是这一份，不再报。
        await monitor.EvaluateOnceAsync(testToken);
        Assert.Equal(2, AlarmSnapshotPayloads(server).Length);

        // 条件消失：报一份空的，它说的是「此刻没有告警」。
        ioConnected = true;
        await monitor.EvaluateOnceAsync(testToken);
        JsonElement[] afterClear = AlarmSnapshotPayloads(server);
        Assert.Equal(3, afterClear.Length);
        Assert.Empty(afterClear[2].GetProperty("alarms").EnumerateArray());
        Assert.Equal(0, io.UnlockCount);
    }

    /// <summary>
    /// 协议 v2 消息 7／8 的正例：指纹相等，车切到那一版并把结果报回去。
    /// </summary>
    /// <remarks>
    /// 消息 7 不带配置内容，所以这一步不是「装上一份新配置」，而是确认「服务端批准的那一版就是你手上
    /// 这份」。切换动的只有版本名，指纹不变——它变了才说明车换了硬件事实，而车没有权力换。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task SlotConfigurationActivationVerifiesTheFingerprintAndReportsTheOutcome()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendSlotConfigurationActivationAfterRecovery = true
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());

        await client.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(() => server.ReceivedActivationResults.Count == 1, testToken);

        JsonElement payload = server.ReceivedActivationResults[0].GetProperty("payload");
        Assert.Equal("ACTIVATED", payload.GetProperty("outcome").GetString());
        Assert.Equal(
            "55555555-5555-4555-8555-555555555555",
            payload.GetProperty("activationId").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("problem").ValueKind);
        // 版本名换成服务端的，指纹不变——硬件事实一个字节没动。
        Assert.Equal("approved-v7", payload.GetProperty("activeSlotConfigurationVersion").GetString());
        Assert.Equal(
            G2SlotConfigurationFixtures.Approved().Fingerprint,
            payload.GetProperty("activeSlotConfigurationFingerprint").GetString());
        // 结果是 RELIABLE，不是 RESPONSE：它自己带 messageId，correlationId 是 null。
        Assert.Equal(
            JsonValueKind.Null,
            server.ReceivedActivationResults[0].GetProperty("correlationId").ValueKind);
        Assert.Equal(0, io.UnlockCount);
    }

    /// <summary>
    /// 指纹对不上就拒绝，报向量点名的那个稳定错误码，生效配置不动。
    /// </summary>
    /// <remarks>
    /// 服务端批准的那一版与车手上这份不是同一份硬件事实。车不该改口——协议里根本没有一条消息能把配置
    /// 内容送过来，所以「按服务端说的算」等于宣称自己装着从没收到过的东西。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task SlotConfigurationActivationIsRejectedWithTheStableCodeWhenTheFingerprintDisagrees()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendSlotConfigurationActivationAfterRecovery = true,
            SlotConfigurationActivationFingerprint = new string('b', 64)
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());

        await client.ConnectAndRecoverAsync(testToken);
        await WaitUntilAsync(() => server.ReceivedActivationResults.Count == 1, testToken);

        JsonElement payload = server.ReceivedActivationResults[0].GetProperty("payload");
        Assert.Equal("REJECTED", payload.GetProperty("outcome").GetString());
        Assert.Equal(
            "SLOT_CONFIGURATION_FINGERPRINT_MISMATCH",
            payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        // 报的是车此刻真正装着的那个指纹，不是服务端刚才说的那个——补报的价值就在于说出实情。
        Assert.Equal(
            G2SlotConfigurationFixtures.Approved().Fingerprint,
            payload.GetProperty("activeSlotConfigurationFingerprint").GetString());
        Assert.Equal(0, io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-SAME-CONTENT")]
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
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
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
                new WireToGateSafetySummaryPayload(false, true, true, true, false, ["VEHICLE_NOT_READY"]),
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
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-SAME-CONTENT")]
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
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
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
        Assert.Contains("DEPARTURE_UNSAFE", blocked.ReasonCodes);
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
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
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
            new OnboardAlarmBoard("AGV-G2", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
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
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-CONNECTION-LOSS-SAFE-FINISH")]
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
            new OnboardAlarmBoard("AGV-G2", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
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
        // BusinessResendsSafetyStateAfterSessionGenerationChangeWhileVehicleIdle。等的和断的是同一个
        // 对象——收到的 SafetyStateChanged 条数，不是服务端的受理计数。
        await WaitUntilAsync(() => ReceivedCount(server, "SafetyStateChanged") == 4, testToken);

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
    /// 8005-agv-control-server#33：装载结果被服务端收下、ack 丢了，车重连后补发。真服务端对补发只回
    /// <c>DurableAck</c>，不跟 <c>SessionReadiness</c>——新会话还没收到任何快照，就绪判定没有变化。v2 服务端
    /// 判 <c>Ready</c> 更要求本代次的能力快照、安全快照与恢复状态报告三者齐全。车若在补发之后就去等
    /// readiness，只能等到超时断开、再重连一次走完整握手。补发之后必须在同一条连接上把握手走完。
    /// 移植自 MVP 线 3ecb490＋a56a59d。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-SAME-CONTENT")]
    public async Task LostOperationResultAckIsReplayedAndTheSameConnectionCompletesTheHandshake()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            DropBeforeOperationResultAck = true,
            SendReadinessAfterRecoveryAck = true
        };
        string journalPath = NewJournalPath();
        const string onboardInstanceId = "0198f1a2-7c3d-4e5f-8a9b-c0de5a7e3301";
        const string demandId = "11111111-1111-4111-8111-111111111111";
        const string attemptId = "33333333-3333-4333-8333-333333333333";
        FakeIoModuleClient io = new();

        await using (WireToGateSessionClient firstClient = CreateClient(
            server,
            io,
            journalPath,
            onboardInstanceId: onboardInstanceId))
        {
            await firstClient.ConnectAndRecoverAsync(testToken);
            await Assert.ThrowsAnyAsync<IOException>(() => firstClient.SendOperationResultAsync(
                $"operation-result:{attemptId}",
                attemptId,
                new WireToGateOperationResultPayload(
                    demandId,
                    attemptId,
                    "LOAD",
                    "COMPLETED",
                    [new WireToGateSlotResultPayload(1, "COMPLETED", "OCCUPIED", "LOCKED", "RESET", [])],
                    DateTimeOffset.UtcNow,
                    "RESULT_RECORDED",
                    new string('0', 64)),
                testToken));
        }

        server.DropBeforeOperationResultAck = false;
        await using (WireToGateSessionClient secondClient = CreateClient(
            server,
            io,
            journalPath,
            onboardInstanceId: onboardInstanceId))
        {
            WireToGateSessionSnapshot resumed = await secondClient.ConnectAndRecoverAsync(testToken);
            Assert.Equal(WireToGateSessionReadiness.Ready, resumed.Readiness);
        }

        Assert.Equal(
            [
                "SessionHello", "OperationResult", "CapabilitySnapshot", "SafetyStateSnapshot",
                "OnboardAlarmSnapshot", "RecoveryStateReport"
            ],
            server.Received
                .Where(item => item.Connection == 2 && item.MessageType != "CONNECTED")
                .Select(item => item.MessageType)
                .ToArray());
        Assert.DoesNotContain(server.Received, item => item.Connection > 2);
        var results = server.ReceivedEnvelopes.Where(item => item.MessageType == "OperationResult").ToArray();
        Assert.Equal([1, 2], results.Select(item => item.Connection).ToArray());
        Assert.Equal(results[0].MessageId, results[1].MessageId);
        Assert.Empty(server.StaleGenerationRejections);
        Assert.Equal(0, io.UnlockCount);

        await using SqliteWireToGateJournal journal = new(journalPath);
        await journal.InitializeAsync(testToken);
        Assert.Empty(await journal.ReadUnacknowledgedOutgoingAsync(testToken));
    }

    /// <summary>
    /// 会话换代之后，车静止不动——IO 快照与车辆安全信号一个字节都没变——车载端仍必须重新上报一份
    /// 安全快照。缺陷 20260908-session-recovery-required-never-clears-while-vehicle-idle 就是这条
    /// 不成立：<c>_lastSafetySignature</c> 是进程内去重状态，换代时不重置，于是服务端换代后手上那份
    /// <c>departureSafe</c> 永远等不到更新，readiness 卡在 RecoveryRequired /
    /// DEPARTURE_SAFETY_NOT_READY。MVP 真车上卡了 6 分 36 秒，直到有人重启车载客户端。
    ///
    /// 静止是关键条件。车一动安全签名自然会变，去重就跨过去了——那次五趟实跑里飞行途中掉进去的
    /// 那趟两分钟就自愈了，停着的那趟没有。
    ///
    /// 换代用「安全消息的 ack 丢了」制造，而不是现场那样的干净重连。走重放这条路多盖住一处：重放被
    /// 服务端认下之后，pending 对账会把 <c>_lastSafetySignature</c> 重新填上，换代重置必须排在它之后
    /// 才有效。移植自 MVP 线 004891f。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
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
            new OnboardAlarmBoard("AGV-G2", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
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

        // 修复前这里会永远停在 2（第一条加它的重放），第三条永远不来：重放被认下之后签名又变回原值，
        // 而车静止、签名不变，去重把重发挡住了。等的和断的是同一个对象——收到的 SafetyStateChanged
        // 条数，不是服务端的受理计数——否则安全评估周期（500 ms）再转一轮多出一条就会把断言弄红。
        await WaitUntilAsync(() => ReceivedCount(server, "SafetyStateChanged") == 3, testToken);

        var changed = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SafetyStateChanged")
            .ToArray();
        Assert.Equal(3, changed.Length);
        Assert.Equal(2, server.AcceptedSafetyStateChangedCount);
        Assert.Equal([1, 2, 2], changed.Select(item => item.Connection).ToArray());
        // 前两条是同一条消息的重放，第三条才是换代后重新评估出来的。内容一样但版本变了，
        // 所以 messageId 不同——服务端按 messageId 去重，原样重放那条到不了任何地方。
        Assert.Equal(changed[0].MessageId, changed[1].MessageId);
        Assert.NotEqual(changed[1].MessageId, changed[2].MessageId);
        Assert.Equal(0, io.UnlockCount);
        Assert.Empty(server.StaleGenerationRejections);
    }

    private static int ReceivedCount(FakeControlServer server, string messageType) =>
        server.ReceivedEnvelopes.Count(item => item.MessageType == messageType);

    private static JsonElement[] AlarmSnapshotPayloads(FakeControlServer server) =>
        [.. server.ReceivedEnvelopes
            .Where(item => item.MessageType == "OnboardAlarmSnapshot")
            .Select(item => WireToGateProtocolSerializer.DeserializeAndValidate(item.WireLine, "AGV-8005-01").Payload)];

    /// <summary>
    /// An operation whose process died while the slot stood unlocked is settled from the live IO on
    /// the next session, and its result travels the pending-result path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap this closes was measured on the rig (8005-agv-program#40, ported from MVP
    /// <c>6846e98</c>): the vehicle only settles an attempt when a command or a recovery action
    /// arrives, the server does not judge a journey Blocked while no result has come, and the
    /// recovery entry needs the journey Blocked already. Nobody moves, and the vehicle stands at the
    /// pick-up point.
    /// </para>
    /// <para>
    /// No second unlock goes out (ADR-cross-0017) and the outcome follows the live readings: every
    /// opened slot at its final state means COMPLETED, anything else UNKNOWN (ADR-cross-0058
    /// decision 2). Here the operator shut the door without loading, so it is UNKNOWN -- and an
    /// UNKNOWN result stays pending until something settles the operation, which is what makes the
    /// next session report and replay it (CV-OPERATION-RESULT-UNKNOWN-RECONCILE).
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnOperationInterruptedWhileWaitingForTheOperatorIsSettledFromLiveIoOnTheNextSession()
    {
        const string attemptId = "44444444-4444-4444-4444-444444444444";
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendSlotOperationCommandAfterRecovery = true
        };
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        NullLogger logger = new();
        string journalPath = NewJournalPath();

        WireToGateSessionService NewSession() => new(
            CreateSessionOptions(server),
            io,
            new SqliteWireToGateJournal(journalPath),
            logger,
            new SystemClock(),
            new DelegateVehicleSafetySignalProvider(() => true),
            new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));
        WireToGateBusinessService NewBusiness(WireToGateSessionService session) => new(
            session,
            io,
            logger,
            new SystemClock(),
            () => true,
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(30)),
            "W2G_G2_INTERRUPTED_SETTLEMENT_OPERATOR");

        // First process: the command arrives, the slot is unlocked, and the process goes away while
        // it waits for an operator who never comes.
        await using (WireToGateSessionService session = NewSession())
        {
            WireToGateBusinessService business = NewBusiness(session);
            business.Start();
            await session.Client.ConnectAndRecoverAsync(testToken);
            await WaitUntilAsync(
                () => server.ReceivedEnvelopes.Any(item =>
                    item.MessageType == "OperationProgress"
                    && item.WireLine.Contains("WAITING_OPERATOR", StringComparison.Ordinal)),
                testToken);
            await business.DisposeAsync();
        }

        Assert.DoesNotContain(server.ReceivedEnvelopes, item => item.MessageType == "OperationResult");
        int unlocksBeforeRestart = io.UnlockCount;
        server.SendSlotOperationCommandAfterRecovery = false;
        server.SimulateOnboardProcessRestart();
        // The operator shut the door while nothing was running, and put nothing in.
        io.CloseDoor(0, cargo: false);

        // Second process: nothing sends another command, so the settlement has to come from the
        // vehicle's own side.
        await using (WireToGateSessionService session = NewSession())
        await using (WireToGateBusinessService business = NewBusiness(session))
        {
            // The same kind is raised as soon as the journal holds the unsettled operation, long
            // before its result is sent; only the one raised once the server has the result means the
            // DurableAck came back, and only then does a restart exercise the replay rather than the
            // unacknowledged-outbox path.
            bool acknowledged = false;
            business.OperatorEventPublished += (_, args) =>
            {
                if (args.Value.Kind == "OPERATION_RECOVERY_REQUIRED"
                    && server.ReceivedEnvelopes.Any(item => item.MessageType == "OperationResult"))
                {
                    acknowledged = true;
                }
            };
            business.Start();
            await session.Client.ConnectAndRecoverAsync(testToken);
            await WaitUntilAsync(() => acknowledged, testToken);
        }

        (int Connection, string MessageType, string MessageId, string WireLine) settled =
            server.ReceivedEnvelopes.Single(item => item.MessageType == "OperationResult");
        WireToGateEnvelope result = WireToGateProtocolSerializer.DeserializeAndValidate(
            settled.WireLine,
            "AGV-8005-01");
        Assert.Equal(attemptId, result.Payload.GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal("UNKNOWN", result.Payload.GetProperty("overallOutcome").GetString());
        JsonElement slot = Assert.Single(result.Payload.GetProperty("slotResults").EnumerateArray());
        Assert.Equal("UNKNOWN", slot.GetProperty("outcome").GetString());
        Assert.Equal("EMPTY", slot.GetProperty("finalPhysicalState").GetString());
        Assert.Equal("LOCKED", slot.GetProperty("lockState").GetString());
        Assert.Equal("RESET", slot.GetProperty("unlockOutputState").GetString());
        Assert.Equal(unlocksBeforeRestart, io.UnlockCount);

        // Third process: the unsettled attempt and its pending result are what the next report names,
        // and the same result is replayed once that report is acknowledged.
        server.SimulateOnboardProcessRestart();
        await using (WireToGateSessionService session = NewSession())
        await using (WireToGateBusinessService business = NewBusiness(session))
        {
            business.Start();
            await session.Client.ConnectAndRecoverAsync(testToken);
            await WaitUntilAsync(
                () => server.ReceivedEnvelopes.Count(item => item.MessageType == "OperationResult") == 2,
                testToken);
        }

        (int Connection, string MessageType, string MessageId, string WireLine)[] results =
            server.ReceivedEnvelopes.Where(item => item.MessageType == "OperationResult").ToArray();
        Assert.NotEqual(results[0].Connection, results[1].Connection);
        Assert.Equal(results[0].MessageId, results[1].MessageId);
        WireToGateEnvelope report = server.ReceivedEnvelopes
            .Where(item => item.Connection == results[1].Connection && item.MessageType == "RecoveryStateReport")
            .Select(item => WireToGateProtocolSerializer.DeserializeAndValidate(item.WireLine, "AGV-8005-01"))
            .Single();
        Assert.Equal(attemptId, report.Payload.GetProperty("unsettledSlotOperationAttemptId").GetString());
        JsonElement pending = Assert.Single(report.Payload.GetProperty("pendingResults").EnumerateArray());
        Assert.Equal("OperationResult", pending.GetProperty("messageType").GetString());
        Assert.Equal(results[0].MessageId, pending.GetProperty("messageId").GetString());
        Assert.Equal(attemptId, pending.GetProperty("businessId").GetString());
        Assert.Equal(
            result.Payload.GetProperty("resultContentSha256").GetString(),
            pending.GetProperty("contentSha256").GetString());
        Assert.Equal(unlocksBeforeRestart, io.UnlockCount);
    }

    /// <summary>
    /// The operator finished the job after the process died, so the settlement is a definite
    /// COMPLETED and nothing stays pending.
    /// </summary>
    /// <remarks>
    /// Reporting UNKNOWN here would send a perfectly good load into recovery and let a compensation
    /// empty the slot again. The completed result also settles the attempt, which is what clears the
    /// journal's unsettled entry -- so the next report names none and replays nothing.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnInterruptedOperationTheOperatorFinishedIsSettledAsCompletedAndLeavesNothingPending()
    {
        const string attemptId = "44444444-4444-4444-4444-444444444444";
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendSlotOperationCommandAfterRecovery = true
        };
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        NullLogger logger = new();
        string journalPath = NewJournalPath();

        WireToGateSessionService NewSession() => new(
            CreateSessionOptions(server),
            io,
            new SqliteWireToGateJournal(journalPath),
            logger,
            new SystemClock(),
            new DelegateVehicleSafetySignalProvider(() => true),
            new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));
        WireToGateBusinessService NewBusiness(WireToGateSessionService session) => new(
            session,
            io,
            logger,
            new SystemClock(),
            () => true,
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(30)),
            "W2G_G2_INTERRUPTED_COMPLETED_OPERATOR");

        await using (WireToGateSessionService session = NewSession())
        {
            WireToGateBusinessService business = NewBusiness(session);
            business.Start();
            await session.Client.ConnectAndRecoverAsync(testToken);
            await WaitUntilAsync(
                () => server.ReceivedEnvelopes.Any(item =>
                    item.MessageType == "OperationProgress"
                    && item.WireLine.Contains("WAITING_OPERATOR", StringComparison.Ordinal)),
                testToken);
            await business.DisposeAsync();
        }

        server.SendSlotOperationCommandAfterRecovery = false;
        server.SimulateOnboardProcessRestart();
        // The basket went in and the door was shut while nothing was running.
        io.CloseDoor(0, cargo: true);

        await using (WireToGateSessionService session = NewSession())
        await using (WireToGateBusinessService business = NewBusiness(session))
        {
            bool acknowledged = false;
            business.OperatorEventPublished += (_, args) =>
            {
                if (args.Value.Kind == "OPERATION_COMPLETED"
                    && server.ReceivedEnvelopes.Any(item => item.MessageType == "OperationResult"))
                {
                    acknowledged = true;
                }
            };
            business.Start();
            await session.Client.ConnectAndRecoverAsync(testToken);
            await WaitUntilAsync(() => acknowledged, testToken);
        }

        WireToGateEnvelope result = WireToGateProtocolSerializer.DeserializeAndValidate(
            server.ReceivedEnvelopes.Single(item => item.MessageType == "OperationResult").WireLine,
            "AGV-8005-01");
        Assert.Equal(attemptId, result.Payload.GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal("COMPLETED", result.Payload.GetProperty("overallOutcome").GetString());
        JsonElement slot = Assert.Single(result.Payload.GetProperty("slotResults").EnumerateArray());
        Assert.Equal("COMPLETED", slot.GetProperty("outcome").GetString());
        Assert.Equal("OCCUPIED", slot.GetProperty("finalPhysicalState").GetString());
        Assert.Equal("LOCKED", slot.GetProperty("lockState").GetString());
        Assert.Equal("RESET", slot.GetProperty("unlockOutputState").GetString());

        server.SimulateOnboardProcessRestart();
        await using (WireToGateSessionService session = NewSession())
        await using (WireToGateBusinessService business = NewBusiness(session))
        {
            business.Start();
            await session.Client.ConnectAndRecoverAsync(testToken);
            await WaitUntilAsync(
                () => server.ReceivedEnvelopes.Count(item =>
                    item.MessageType == "RecoveryStateReport") >= 3,
                testToken);
        }

        WireToGateEnvelope lastReport = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "RecoveryStateReport")
            .Select(item => WireToGateProtocolSerializer.DeserializeAndValidate(item.WireLine, "AGV-8005-01"))
            .Last();
        Assert.Equal(
            JsonValueKind.Null,
            lastReport.Payload.GetProperty("unsettledSlotOperationAttemptId").ValueKind);
        Assert.Empty(lastReport.Payload.GetProperty("pendingResults").EnumerateArray());
        Assert.Single(server.ReceivedEnvelopes, item => item.MessageType == "OperationResult");
    }

    /// <summary>
    /// Only the CLOSED recovery session snapshot is acknowledged
    /// (8005-agv-control-server#31, L2 real-onboard-compensate-then-reconnect).
    /// </summary>
    /// <remarks>
    /// <para>
    /// After every RecoveryStateReport the server replays each recovery session snapshot that is
    /// neither acknowledged nor fenced by a newer revision. The vehicle keeps the current recovery
    /// session in memory only -- the journal holds just its id -- so that replay is the only way a
    /// restarted HMI gets an open session back. Acknowledging OPEN left a restarted vehicle holding
    /// an id and no session: the recovery button threw RECOVERY_SESSION_STATE_PENDING and asking
    /// again was refused with RECOVERY_SESSION_ALREADY_OPEN.
    /// </para>
    /// <para>
    /// CLOSED is the last revision a session ever gets. Nothing supersedes it, so left unacknowledged
    /// it is replayed into every later session and leaves a row behind for each one -- and a vehicle
    /// that has it needs nothing more from it.
    /// </para>
    /// <para>
    /// The server sends OPEN and then CLOSED: by the time the answer to CLOSED arrives, OPEN has long
    /// been handled on the same stream, so an acknowledgement of it would have arrived first.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-REPLACE-AND-ACK")]
    public async Task OnlyTheClosedRecoverySessionSnapshotIsAcknowledged()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            RespondToRecoveryRequests = true,
            RecoverySessionSnapshotStatesAfterOpened = ["OPEN", "CLOSED"]
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);

        string requestId = "77777777-7777-4777-8777-777777777770";
        ExceptionRecoverySessionOpenedPayload opened = await client
            .RequestExceptionRecoverySessionAsync(
                requestId,
                new ExceptionRecoverySessionRequestedPayload(
                    requestId,
                    new WireToGateOperatorContextPayload(
                        "maintenance-001",
                        "SESSION",
                        DateTimeOffset.UtcNow),
                    "MAINTENANCE_ADMINISTRATOR",
                    "88888888-8888-4888-8888-888888888888",
                    "99999999-9999-4999-8999-999999999999",
                    [1, 2],
                    "repair complete",
                    "test-proof"),
                testToken);
        Assert.Equal(requestId, opened.RequestId);

        await WaitUntilAsync(() => server.SentRecoverySessionSnapshots.Count == 2, testToken);
        (string closedMessageId, string closedLine) = server.SentRecoverySessionSnapshots[1];
        await WaitUntilAsync(
            () => server.ReceivedEnvelopes.Any(item =>
                item.MessageType == "SnapshotAppliedAck"
                && CorrelationId(item.WireLine) == closedMessageId),
            testToken);

        (int Connection, string MessageType, string MessageId, string WireLine) acknowledgement =
            Assert.Single(server.ReceivedEnvelopes, item => item.MessageType == "SnapshotAppliedAck");
        using JsonDocument document = JsonDocument.Parse(acknowledgement.WireLine);
        JsonElement payload = document.RootElement.GetProperty("payload");
        Assert.Equal(closedMessageId, payload.GetProperty("snapshotMessageId").GetString());
        Assert.Equal("EXCEPTION_RECOVERY_SESSION", payload.GetProperty("snapshotKind").GetString());
        Assert.Equal(4, payload.GetProperty("appliedRevision").GetInt64());
        Assert.Equal(
            WireToGateProtocolSerializer.ComputeSha256(Encoding.UTF8.GetBytes(closedLine)),
            payload.GetProperty("appliedContentSha256").GetString());
        Assert.DoesNotContain(server.Received, item => item.MessageType == "ProtocolProblem");

        static string? CorrelationId(string wireLine)
        {
            using JsonDocument envelope = JsonDocument.Parse(wireLine);
            return envelope.RootElement.GetProperty("correlationId").GetString();
        }
    }

    /// <summary>
    /// An OPEN recovery session snapshot is left unacknowledged, so the server keeps replaying it and
    /// a restarted vehicle can get the session back (8005-agv-control-server#31, onboard-hmi#41 was
    /// the regression this replaced).
    /// </summary>
    /// <remarks>
    /// The barrier is the heartbeat: the vehicle answers what it reads on a stream in order, so an
    /// acknowledgement of the snapshot would have been written before a heartbeat the test sends
    /// afterwards. The server having the heartbeat and no acknowledgement is therefore the absence
    /// itself, not a race that has yet to finish.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-REPLACE-AND-ACK")]
    public async Task AnOpenRecoverySessionSnapshotIsLeftUnacknowledgedSoItKeepsBeingReplayed()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            RespondToRecoveryRequests = true,
            RecoverySessionSnapshotStatesAfterOpened = ["OPEN"]
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);

        string requestId = "77777777-7777-4777-8777-777777777771";
        await client.RequestExceptionRecoverySessionAsync(
            requestId,
            new ExceptionRecoverySessionRequestedPayload(
                requestId,
                new WireToGateOperatorContextPayload(
                    "maintenance-001",
                    "SESSION",
                    DateTimeOffset.UtcNow),
                "MAINTENANCE_ADMINISTRATOR",
                "88888888-8888-4888-8888-888888888888",
                "99999999-9999-4999-8999-999999999999",
                [1, 2],
                "repair complete",
                "test-proof"),
            testToken);
        await WaitUntilAsync(() => server.SentRecoverySessionSnapshots.Count == 1, testToken);

        await client.SendHeartbeatAsync(testToken);
        await WaitUntilAsync(
            () => server.ReceivedEnvelopes.Any(item => item.MessageType == "Heartbeat"),
            testToken);

        Assert.DoesNotContain(server.Received, item => item.MessageType == "SnapshotAppliedAck");
        Assert.DoesNotContain(server.Received, item => item.MessageType == "ProtocolProblem");
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
        TimeSpan? messageTimeout = null,
        OnboardAlarmBoard? alarmBoard = null)
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
            alarmBoard ?? new OnboardAlarmBoard("AGV-G2", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
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
