using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SQCD.Agv.Contracts;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed class FakeControlServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly int[] SingleSlot = [1];
    private static readonly DateTimeOffset StableJourneyObservedAt =
        new(2026, 8, 29, 6, 30, 0, TimeSpan.Zero);
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;
    private readonly List<string> _identityValidations = [];
    private readonly Dictionary<string, (long Revision, string ContentSha256)> _appliedSnapshots = new();
    private readonly Dictionary<string, string> _acceptedSafetyStateChanges = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private long _sessionGeneration;
    private int _recoveryAckCount;
    private string? _lastAcceptedInstanceId;
    private long _acceptedCapabilityVersion;
    private long _acceptedSafetyStateVersion;
    private bool _latestSafetyDepartureSafe;
    private int _demandSnapshotSendCount;

    public FakeControlServer(IPAddress address)
    {
        _listener = new TcpListener(address, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stopping.Token));
    }

    public int Port { get; }

    public bool DropAfterRecoveryAck { get; set; }

    public bool DropBeforeRecoveryAck { get; set; }

    public bool DropBeforeSafetyStateChangedAck { get; set; }

    public bool SendReadinessAfterRecoveryAck { get; set; }

    public bool SendReadinessAfterSafetyStateChangedAck { get; set; }

    /// <summary>
    /// Appends one RECOVERY_REQUIRED SessionReadiness line to the OperationResult ack, the way the
    /// real ControlServer does when applying a refused result moves the session into recovery.
    /// Every other mid-session readiness here rides a SafetyStateChanged ack, so this shape --
    /// readiness announced on a business ack the vehicle is already awaiting -- had no coverage.
    /// </summary>
    public bool SendRecoveryRequiredReadinessAfterOperationResultAck { get; set; }

    public bool RequireSafeSafetyForReadiness { get; set; }

    public bool SendJourneySnapshotsAfterRecovery { get; set; }

    public bool SendDemandAcceptanceSnapshotsAfterRecovery { get; set; }

    public bool SendDemandAcceptanceSnapshotsOnlyFirstConnection { get; set; }

    public bool SendJourneyRevisionConflict { get; set; }

    public bool ReplayJourneySnapshotsWithStableIdentity { get; set; }

    public bool SendSlotOperationCommandAfterRecovery { get; set; }

    public bool RespondToRecoveryRequests { get; set; }

    public bool RespondToManualChargingReturnToServiceRequests { get; set; }

    public string ManualChargingReturnToServiceOutcome { get; set; } =
        "RETURNED_TO_ELIGIBILITY_EVALUATION";

    public WireToGateProblemPayload? ManualChargingReturnToServiceProblem { get; set; }

    public long ManualChargingReturnToServiceVehicleBusinessStateRevision { get; set; } = 1;

    public int ManualChargingReturnToServiceResponseCopies { get; set; } = 1;

    public bool SendResumeCommandAfterRecoveryAction { get; set; }

    public long InitialAcceptedCapabilityVersion { get; set; }

    public long InitialAcceptedSafetyStateVersion { get; set; }

    public IReadOnlyList<string> IdentityValidationResults
    {
        get
        {
            lock (_sync)
            {
                return _identityValidations.ToArray();
            }
        }
    }

    public IReadOnlyList<(int Connection, string MessageType)> Received { get; private set; } = [];

    public IReadOnlyList<(int Connection, string MessageType, string MessageId, string WireLine)> ReceivedEnvelopes
    {
        get;
        private set;
    } = [];

    public IReadOnlyList<(int Connection, string MessageType, string MessageId, string WireLine)> SentJourneyEnvelopes
    {
        get;
        private set;
    } = [];

    public IReadOnlyList<(int Connection, string MessageType, string MessageId, string ReasonCode)> StaleGenerationRejections
    {
        get;
        private set;
    } = [];

    public IReadOnlyList<(string Kind, long Revision)> AppliedSnapshots { get; private set; } = [];

    public int AcceptedSafetyStateChangedCount
    {
        get
        {
            lock (_sync)
            {
                return _acceptedSafetyStateChanges.Count;
            }
        }
    }

    private sealed class ConnectionContext
    {
        public required int ConnectionIndex;
        public required TcpClient Client;
        public required StreamReader Reader;
        public required StreamWriter Writer;
        public required ConcurrentQueue<string> ReceivedOrder;
        public string AgvId = string.Empty;
        public long Generation;
        public long CapabilityVersion;
        public long SafetyStateVersion;
        public long AcceptedCapabilityVersion;
        public long AcceptedSafetyStateVersion;
    }

    private async Task AcceptLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            int connectionIndex = NextConnectionIndex();
            long acceptedCapabilityVersion;
            long acceptedSafetyStateVersion;
            lock (_sync)
            {
                acceptedCapabilityVersion = _acceptedCapabilityVersion != 0
                    ? _acceptedCapabilityVersion
                    : InitialAcceptedCapabilityVersion;
                acceptedSafetyStateVersion = _acceptedSafetyStateVersion != 0
                    ? _acceptedSafetyStateVersion
                    : InitialAcceptedSafetyStateVersion;
            }

            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            ConnectionContext context = new()
            {
                ConnectionIndex = connectionIndex,
                Client = client,
                Reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4_096, leaveOpen: true),
                Writer = new StreamWriter(stream, new UTF8Encoding(false), 4_096, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                },
                ReceivedOrder = new ConcurrentQueue<string>(),
                AcceptedCapabilityVersion = acceptedCapabilityVersion,
                AcceptedSafetyStateVersion = acceptedSafetyStateVersion
            };
            RecordConnection(connectionIndex, context.ReceivedOrder);
            _ = Task.Run(() => HandleConnectionAsync(connectionIndex, context, stoppingToken), stoppingToken);
        }
    }

    private int _connectionCount;

    private int NextConnectionIndex()
    {
        lock (_sync)
        {
            _connectionCount++;
            _recoveryAckCount = 0;
            return _connectionCount;
        }
    }

    private void RecordMessage(
        int connectionIndex,
        ConcurrentQueue<string> order,
        string messageType,
        string messageId,
        string wireLine)
    {
        order.Enqueue(messageType);
        lock (_sync)
        {
            var list = Received.ToList();
            list.Add((connectionIndex, messageType));
            Received = list;
            var envelopes = ReceivedEnvelopes.ToList();
            envelopes.Add((connectionIndex, messageType, messageId, wireLine));
            ReceivedEnvelopes = envelopes;
        }
    }

    private void RecordConnection(int connectionIndex, ConcurrentQueue<string> order)
    {
        lock (_sync)
        {
            var list = Received.ToList();
            list.Add((connectionIndex, "CONNECTED"));
            Received = list;
        }
    }

    private bool HasCurrentSessionGeneration(
        ConnectionContext context,
        JsonElement envelope,
        int connectionIndex,
        string messageType,
        string messageId)
    {
        bool matches = envelope.TryGetProperty("sessionGeneration", out JsonElement generation)
            && generation.ValueKind == JsonValueKind.Number
            && generation.GetInt64() == context.Generation;
        if (!matches)
        {
            lock (_sync)
            {
                var rejections = StaleGenerationRejections.ToList();
                rejections.Add((connectionIndex, messageType, messageId, "STALE_SESSION_GENERATION"));
                StaleGenerationRejections = rejections;
            }
        }

        return matches;
    }

    private async Task HandleConnectionAsync(
        int connectionIndex,
        ConnectionContext context,
        CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                string? line = await context.Reader.ReadLineAsync(stoppingToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                string messageType = root.GetProperty("messageType").GetString()!;
                string messageId = root.GetProperty("messageId").GetString()!;
                RecordMessage(connectionIndex, context.ReceivedOrder, messageType, messageId, line);

                if (!string.Equals(messageType, "SessionHello", StringComparison.Ordinal)
                    && !HasCurrentSessionGeneration(context, root, connectionIndex, messageType, messageId))
                {
                    await WriteEnvelopeAsync(context, CreateProtocolProblem(
                        context,
                        messageId,
                        messageType,
                        "STALE_SESSION_GENERATION")).ConfigureAwait(false);
                    continue;
                }

                switch (messageType)
                {
                    case "SessionHello":
                        await HandleSessionHelloAsync(context, root).ConfigureAwait(false);
                        break;
                    case "CapabilitySnapshot":
                    case "SafetyStateSnapshot":
                        await HandleSnapshotAsync(context, line, messageType, root).ConfigureAwait(false);
                        break;
                    case "RecoveryStateReport":
                        await HandleRecoveryStateReportAsync(context, line, root).ConfigureAwait(false);
                        break;
                    case "Heartbeat":
                        await WriteEnvelopeAsync(context, CreateHeartbeatAck(context, root)).ConfigureAwait(false);
                        break;
                    case "OperationResult":
                        await WriteEnvelopeAsync(context, CreateDurableAck(context, root)).ConfigureAwait(false);
                        if (SendRecoveryRequiredReadinessAfterOperationResultAck)
                        {
                            await WriteEnvelopeAsync(
                                context,
                                CreateRecoveryRequiredSessionReadiness(context)).ConfigureAwait(false);
                        }

                        break;
                    case "SublotSubmitted":
                    case "OperationProgress":
                    case "PreDepartureSafetyCheckResult":
                    case "SlotOperationCommandRejected":
                        await WriteEnvelopeAsync(context, CreateDurableAck(context, root)).ConfigureAwait(false);
                        break;
                    case "ExceptionRecoverySessionRequested" when RespondToRecoveryRequests:
                        await HandleRecoverySessionRequestAsync(context, root).ConfigureAwait(false);
                        break;
                    case "RecoveryActionSubmitted" when RespondToRecoveryRequests:
                        await HandleRecoveryActionSubmittedAsync(context, root).ConfigureAwait(false);
                        break;
                    case "ManualChargingReturnToServiceRequested"
                        when RespondToManualChargingReturnToServiceRequests:
                        await HandleManualChargingReturnToServiceRequestedAsync(context, root)
                            .ConfigureAwait(false);
                        break;
                    case "SafetyStateChanged":
                        await HandleSafetyStateChangedAsync(context, root).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            context.Writer.Dispose();
            context.Reader.Dispose();
            context.Client.Dispose();
        }
    }

    private async Task HandleSessionHelloAsync(ConnectionContext context, JsonElement hello)
    {
        context.AgvId = hello.GetProperty("agvId").GetString()!;
        JsonElement identity = hello.GetProperty("payload").GetProperty("protocolReleaseIdentity");
        bool identityMatches =
            identity.GetProperty("repository").GetString() == WireToGateRelease.Repository
            && identity.GetProperty("releaseVersion").GetString() == WireToGateRelease.ReleaseVersion
            && identity.GetProperty("tag").GetString() == WireToGateRelease.Tag
            && identity.GetProperty("commit").GetString() == WireToGateRelease.Commit
            && identity.GetProperty("manifestSha256").GetString() == WireToGateRelease.ManifestSha256
            && identity.GetProperty("schemaBundleSha256").GetString() == WireToGateRelease.SchemaBundleSha256
            && identity.GetProperty("vectorsSha256").GetString() == WireToGateRelease.VectorsSha256;
        lock (_sync)
        {
            _identityValidations.Add(identityMatches ? "PASS" : "FAIL");
        }

        if (!identityMatches)
        {
            await WriteEnvelopeAsync(context, CreateProtocolProblem(
                context,
                hello.GetProperty("messageId").GetString()!,
                "SessionHello",
                "PROTOCOL_RELEASE_IDENTITY_MISMATCH")).ConfigureAwait(false);
            return;
        }

        long generation;
        lock (_sync)
        {
            generation = ++_sessionGeneration;
            // A different runtime instance starts a fresh snapshot revision
            // baseline, mirroring how real ControlServer accepts new journal
            // generations.  The same instance reconnecting (same journal)
            // keeps server-side revision memory so same-revision conflicts
            // stay enforced.
            string onboardInstanceId =
                hello.GetProperty("payload").GetProperty("onboardInstanceId").GetString() ?? string.Empty;
            if (!string.Equals(onboardInstanceId, _lastAcceptedInstanceId, StringComparison.Ordinal))
            {
                _appliedSnapshots.Clear();
                _lastAcceptedInstanceId = onboardInstanceId;
            }
        }

        context.Generation = generation;
        var payload = new
        {
            sessionGeneration = generation,
            serverInstanceId = Guid.NewGuid().ToString("D"),
            serverBuildCommit = new string('a', 40),
            acceptedProtocolReleaseIdentity = WireToGateRelease.Identity,
            acceptedAt = DateTimeOffset.UtcNow
        };
        await WriteEnvelopeAsync(context, CreateEnvelope(
            context,
            "SessionAccepted",
            correlationId: hello.GetProperty("messageId").GetString()!,
            payload)).ConfigureAwait(false);
    }

    private async Task HandleSnapshotAsync(ConnectionContext context, string line, string messageType, JsonElement snapshot)
    {
        long revision = messageType == "CapabilitySnapshot"
            ? snapshot.GetProperty("payload").GetProperty("capabilityVersion").GetInt64()
            : snapshot.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64();
        string contentSha256 = WireToGateProtocolSerializer.ComputeSha256(Encoding.UTF8.GetBytes(line));
        if (messageType == "CapabilitySnapshot")
        {
            context.CapabilityVersion = revision;
            lock (_sync)
            {
                _acceptedCapabilityVersion = revision;
            }
        }
        else
        {
            context.SafetyStateVersion = revision;
            lock (_sync)
            {
                _acceptedSafetyStateVersion = Math.Max(_acceptedSafetyStateVersion, revision);
            }
        }

        string? problemCode = null;
        bool apply;
        lock (_sync)
        {
            if (_appliedSnapshots.TryGetValue(messageType, out (long Revision, string ContentSha256) applied))
            {
                if (revision < applied.Revision)
                {
                    problemCode = "SNAPSHOT_REVISION_REGRESSION";
                }
                else if (revision == applied.Revision && contentSha256 != applied.ContentSha256)
                {
                    problemCode = "SNAPSHOT_REVISION_CONTENT_CONFLICT";
                }

                apply = revision > applied.Revision;
            }
            else
            {
                apply = true;
            }

            if (apply)
            {
                _appliedSnapshots[messageType] = (revision, contentSha256);
                var appliedList = AppliedSnapshots.ToList();
                appliedList.Add((messageType, revision));
                AppliedSnapshots = appliedList;
            }
        }

        if (problemCode is not null)
        {
            await WriteEnvelopeAsync(context, CreateProtocolProblem(
                context,
                snapshot.GetProperty("messageId").GetString()!,
                messageType,
                problemCode)).ConfigureAwait(false);
            return;
        }

        if (messageType == "SafetyStateSnapshot")
        {
            bool departureSafe = snapshot
                .GetProperty("payload")
                .GetProperty("safety")
                .GetProperty("departureSafe")
                .GetBoolean();
            lock (_sync)
            {
                _latestSafetyDepartureSafe = departureSafe;
            }
        }

        var ackPayload = new
        {
            snapshotMessageId = snapshot.GetProperty("messageId").GetString()!,
            snapshotKind = messageType == "CapabilitySnapshot" ? "CAPABILITY" : "SAFETY_STATE",
            appliedRevision = revision,
            appliedContentSha256 = contentSha256
        };
        await WriteEnvelopeAsync(context, CreateEnvelope(
            context,
            "SnapshotAppliedAck",
            correlationId: snapshot.GetProperty("messageId").GetString()!,
            ackPayload)).ConfigureAwait(false);
    }

    private async Task HandleRecoveryStateReportAsync(ConnectionContext context, string line, JsonElement report)
    {
        if (DropBeforeRecoveryAck)
        {
            context.Client.Close();
            return;
        }

        string messageId = report.GetProperty("messageId").GetString()!;
        string contentSha256 = WireToGateProtocolSerializer.ComputeSha256(Encoding.UTF8.GetBytes(line));
        var ackPayload = new
        {
            acceptedMessageId = messageId,
            acceptedMessageType = "RecoveryStateReport",
            acceptedContentSha256 = contentSha256,
            durablyAcceptedAt = DateTimeOffset.UtcNow
        };
        await WriteEnvelopeAsync(context, CreateEnvelope(
            context,
            "DurableAck",
            correlationId: messageId,
            ackPayload)).ConfigureAwait(false);

        bool drop;
        bool sendReadiness;
        lock (_sync)
        {
            _recoveryAckCount++;
            sendReadiness = SendReadinessAfterRecoveryAck && _recoveryAckCount == 1;
            drop = DropAfterRecoveryAck;
        }

        if (sendReadiness)
        {
            await WriteEnvelopeAsync(context, CreateSessionReadiness(context)).ConfigureAwait(false);
            bool sendDemandSnapshots = SendDemandAcceptanceSnapshotsAfterRecovery;
            if (sendDemandSnapshots && SendDemandAcceptanceSnapshotsOnlyFirstConnection)
            {
                sendDemandSnapshots = Interlocked.Increment(ref _demandSnapshotSendCount) == 1;
            }

            if (sendDemandSnapshots)
            {
                await SendDemandAcceptanceSnapshotsAsync(context).ConfigureAwait(false);
            }
            else if (SendJourneySnapshotsAfterRecovery)
            {
                await SendJourneySnapshotsAsync(context).ConfigureAwait(false);
            }

            if (SendSlotOperationCommandAfterRecovery)
            {
                await SendSlotOperationCommandAsync(context).ConfigureAwait(false);
            }
        }

        if (drop)
        {
            context.Client.Close();
        }
    }

    private static async Task HandleRecoverySessionRequestAsync(
        ConnectionContext context,
        JsonElement request)
    {
        JsonElement payload = request.GetProperty("payload");
        string sessionId = "77777777-7777-4777-8777-777777777777";
        await WriteEnvelopeAsync(
            context,
            CreateEnvelope(
                context,
                "ExceptionRecoverySessionOpened",
                request.GetProperty("messageId").GetString(),
                new
                {
                    requestId = payload.GetProperty("requestId").GetString(),
                    exceptionRecoverySessionId = sessionId,
                    openedAt = DateTimeOffset.UtcNow,
                    eventId = payload.GetProperty("eventId").GetString(),
                    demandId = payload.TryGetProperty("demandId", out JsonElement demandId)
                        ? demandId.GetString()
                        : null,
                    slots = payload.GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()).ToArray(),
                    recoverySessionRevision = 1
                }))
            .ConfigureAwait(false);
    }

    private static async Task HandleRecoveryActionSubmittedAsync(
        ConnectionContext context,
        JsonElement request)
    {
        JsonElement payload = request.GetProperty("payload");
        string sessionId = payload.GetProperty("exceptionRecoverySessionId").GetString()!;
        string actionId = payload.GetProperty("recoveryActionId").GetString()!;
        await WriteEnvelopeAsync(
            context,
            CreateEnvelope(
                context,
                "RecoveryActionAccepted",
                request.GetProperty("messageId").GetString(),
                new
                {
                    recoveryActionId = actionId,
                    exceptionRecoverySessionId = sessionId,
                    acceptedAction = payload.GetProperty("action").GetString(),
                    recoverySessionRevision = 2,
                    acceptedAt = DateTimeOffset.UtcNow
                }))
            .ConfigureAwait(false);
    }

    private async Task HandleManualChargingReturnToServiceRequestedAsync(
        ConnectionContext context,
        JsonElement request)
    {
        JsonElement payload = request.GetProperty("payload");
        string messageId = request.GetProperty("messageId").GetString()!;
        string requestId = payload.GetProperty("requestId").GetString()!;
        int responseCopies = Math.Max(0, ManualChargingReturnToServiceResponseCopies);
        for (int index = 0; index < responseCopies; index++)
        {
            await WriteEnvelopeAsync(
                context,
                CreateEnvelope(
                    context,
                    "ManualChargingReturnToServiceResult",
                    messageId,
                    new
                    {
                        requestId,
                        outcome = ManualChargingReturnToServiceOutcome,
                        problem = ManualChargingReturnToServiceProblem,
                        vehicleBusinessStateRevision = ManualChargingReturnToServiceVehicleBusinessStateRevision
                    }))
                .ConfigureAwait(false);
        }
    }

    private async Task HandleSafetyStateChangedAsync(ConnectionContext context, JsonElement message)
    {
        string messageId = message.GetProperty("messageId").GetString()!;
        JsonElement payload = message.GetProperty("payload");
        string payloadJson = payload.GetRawText();
        long safetyStateVersion = payload.GetProperty("safetyStateVersion").GetInt64();
        bool departureSafe = payload.GetProperty("safety").GetProperty("departureSafe").GetBoolean();
        bool conflict;
        lock (_sync)
        {
            conflict = _acceptedSafetyStateChanges.TryGetValue(messageId, out string? acceptedPayload)
                && !string.Equals(acceptedPayload, payloadJson, StringComparison.Ordinal);
            if (!conflict && acceptedPayload is null)
            {
                _acceptedSafetyStateChanges.Add(messageId, payloadJson);
            }
        }

        if (conflict)
        {
            await WriteEnvelopeAsync(context, CreateProtocolProblem(
                context,
                messageId,
                "SafetyStateChanged",
                "MESSAGE_ID_CONTENT_CONFLICT")).ConfigureAwait(false);
            return;
        }

        context.SafetyStateVersion = Math.Max(context.SafetyStateVersion, safetyStateVersion);
        lock (_sync)
        {
            _acceptedSafetyStateVersion = Math.Max(_acceptedSafetyStateVersion, safetyStateVersion);
            _latestSafetyDepartureSafe = departureSafe;
        }

        // The inbox accepts the work first.  Closing here models a DurableAck
        // lost after durable acceptance, so replay must retain identity/content
        // and must not produce a second business acceptance.
        if (DropBeforeSafetyStateChangedAck)
        {
            context.Client.Close();
            return;
        }

        await WriteEnvelopeAsync(context, CreateDurableAck(context, message)).ConfigureAwait(false);
        if (SendReadinessAfterSafetyStateChangedAck)
        {
            await WriteEnvelopeAsync(context, CreateSessionReadiness(context)).ConfigureAwait(false);
        }
    }

    private static WireToGateEnvelope CreateEnvelope(
        ConnectionContext context,
        string messageType,
        string? correlationId,
        object payload) =>
        WireToGateProtocolSerializer.Create(
            messageType,
            Guid.NewGuid().ToString("D"),
            correlationId,
            context.AgvId,
            context.Generation,
            DateTimeOffset.UtcNow,
            payload);

    private WireToGateEnvelope CreateSessionReadiness(ConnectionContext context)
    {
        bool ready;
        lock (_sync)
        {
            ready = !RequireSafeSafetyForReadiness || _latestSafetyDepartureSafe;
        }

        return CreateEnvelope(
            context,
            "SessionReadiness",
            correlationId: null,
            new
            {
                readiness = ready ? "READY" : "RECOVERY_REQUIRED",
                decidedAt = DateTimeOffset.UtcNow,
                reasonCodes = ready ? Array.Empty<string>() : ["DEPARTURE_SAFETY_NOT_READY"],
                acceptedCapabilityVersion = Math.Max(context.CapabilityVersion, context.AcceptedCapabilityVersion),
                acceptedSafetyStateVersion = Math.Max(context.SafetyStateVersion, context.AcceptedSafetyStateVersion),
                vehicleBusinessStateRevision = 0
            });
    }

    private static readonly string[] RecoveryRequiredReasonCodes = ["SESSION_RECOVERY_REQUIRED"];

    /// <summary>
    /// Shaped after OnboardMessageProcessor.SessionReadinessLine: no correlationId even though it
    /// trails an ack, and the protocol reason code the server maps OPERATION_RECOVERY_REQUIRED onto.
    /// </summary>
    private static WireToGateEnvelope CreateRecoveryRequiredSessionReadiness(ConnectionContext context) =>
        CreateEnvelope(
            context,
            "SessionReadiness",
            correlationId: null,
            new
            {
                readiness = "RECOVERY_REQUIRED",
                decidedAt = DateTimeOffset.UtcNow,
                reasonCodes = RecoveryRequiredReasonCodes,
                acceptedCapabilityVersion = Math.Max(context.CapabilityVersion, context.AcceptedCapabilityVersion),
                acceptedSafetyStateVersion = Math.Max(context.SafetyStateVersion, context.AcceptedSafetyStateVersion),
                vehicleBusinessStateRevision = 0
            });

    private static WireToGateEnvelope CreateHeartbeatAck(ConnectionContext context, JsonElement heartbeat)
    {
        var payload = new
        {
            receivedHeartbeatMessageId = heartbeat.GetProperty("messageId").GetString()!,
            serverTime = DateTimeOffset.UtcNow
        };
        return CreateEnvelope(
            context,
            "HeartbeatAck",
            heartbeat.GetProperty("messageId").GetString()!,
            payload);
    }

    private static WireToGateEnvelope CreateDurableAck(ConnectionContext context, JsonElement message)
    {
        string wireLine = message.GetRawText();
        return CreateEnvelope(
            context,
            "DurableAck",
            message.GetProperty("messageId").GetString()!,
            new
            {
                acceptedMessageId = message.GetProperty("messageId").GetString()!,
                acceptedMessageType = message.GetProperty("messageType").GetString()!,
                acceptedContentSha256 = WireToGateProtocolSerializer.ComputeSha256(
                    Encoding.UTF8.GetBytes(wireLine)),
                durablyAcceptedAt = DateTimeOffset.UtcNow
            });
    }

    private static async Task SendSlotOperationCommandAsync(ConnectionContext context)
    {
        await WriteEnvelopeAsync(
            context,
            CreateEnvelope(
                context,
                "SlotOperationCommand",
                Guid.NewGuid().ToString("D"),
                new
                {
                    demandId = "11111111-1111-1111-1111-111111111111",
                    operationSessionId = "33333333-3333-3333-3333-333333333333",
                    slotOperationAttemptId = "44444444-4444-4444-4444-444444444444",
                    operationType = "LOAD",
                    slots = SingleSlot,
                    expectedBasketCount = 1,
                    expectedFinalPhysicalState = "OCCUPIED",
                    commandContentSha256 = new string('0', 64)
                }))
            .ConfigureAwait(false);
    }

    private async Task SendJourneySnapshotsAsync(ConnectionContext context)
    {
        string demandId = "11111111-1111-1111-1111-111111111111";
        string movementLegId = "22222222-2222-2222-2222-222222222222";
        DateTimeOffset observedAt = ReplayJourneySnapshotsWithStableIdentity
            ? StableJourneyObservedAt
            : DateTimeOffset.UtcNow;
        await WriteJourneyEnvelopeAsync(context, CreateJourneyEnvelope(
            context,
            "VehicleBusinessStateSnapshot",
            new
            {
                vehicleBusinessStateRevision = 1,
                readiness = "READY",
                manualChargingHold = false,
                batteryState = "SUFFICIENT",
                blockingFacts = Array.Empty<object>(),
                observedAt
            })).ConfigureAwait(false);
        await WriteJourneyEnvelopeAsync(context, CreateJourneyEnvelope(
            context,
            "CurrentStopWorklistSnapshot",
            new
            {
                stationId = "ST-01",
                worklistRevision = 1,
                operationSessionId = (string?)null,
                items = new[]
                {
                    new
                    {
                        demandId,
                        transportDemandKey = "TD-001",
                        sublot = "SUBLOT-001",
                        workType = "WIRE_TO_GATE",
                        stopRole = "PICKUP",
                        expectedBasketCount = 2
                    }
                }
            })).ConfigureAwait(false);
        await WriteJourneyEnvelopeAsync(context, CreateJourneyEnvelope(
            context,
            "UpcomingStopPlanSnapshot",
            new
            {
                planRevision = 1,
                demandId,
                legs = new[]
                {
                    new
                    {
                        movementLegId,
                        legType = "TO_PICKUP",
                        sequence = 1,
                        stationId = "ST-01",
                        mapId = "MAP-01",
                        state = "ACTIVE"
                    }
                }
            })).ConfigureAwait(false);

        if (SendJourneyRevisionConflict)
        {
            await WriteJourneyEnvelopeAsync(context, CreateJourneyEnvelope(
                context,
                "CurrentStopWorklistSnapshot",
                new
                {
                    stationId = "ST-01",
                    worklistRevision = 1,
                    operationSessionId = (string?)null,
                    items = new[]
                    {
                        new
                        {
                            demandId,
                            transportDemandKey = "TD-001",
                            sublot = "CONFLICTING-SUBLOT",
                            workType = "WIRE_TO_GATE",
                            stopRole = "PICKUP",
                            expectedBasketCount = 2
                        }
                    }
                })).ConfigureAwait(false);
        }
    }

    private WireToGateEnvelope CreateJourneyEnvelope(
        ConnectionContext context,
        string messageType,
        object payload)
    {
        string messageId = ReplayJourneySnapshotsWithStableIdentity
            ? messageType switch
            {
                "VehicleBusinessStateSnapshot" => "00000000-0000-4000-8000-000000009101",
                "CurrentStopWorklistSnapshot" => "00000000-0000-4000-8000-000000009102",
                "UpcomingStopPlanSnapshot" => "00000000-0000-4000-8000-000000009103",
                _ => throw new InvalidDataException("Unsupported journey snapshot type.")
            }
            : Guid.NewGuid().ToString("D");
        return WireToGateProtocolSerializer.Create(
            messageType,
            messageId,
            correlationId: null,
            context.AgvId,
            context.Generation,
            DateTimeOffset.UtcNow,
            payload);
    }

    private async Task WriteJourneyEnvelopeAsync(
        ConnectionContext context,
        WireToGateEnvelope envelope)
    {
        string wireLine = WireToGateProtocolSerializer.Serialize(envelope);
        lock (_sync)
        {
            var sent = SentJourneyEnvelopes.ToList();
            sent.Add((context.ConnectionIndex, envelope.MessageType, envelope.MessageId, wireLine));
            SentJourneyEnvelopes = sent;
        }

        await context.Writer.WriteLineAsync(wireLine).ConfigureAwait(false);
    }

    private static async Task SendDemandAcceptanceSnapshotsAsync(ConnectionContext context)
    {
        string demandId = "11111111-1111-1111-1111-111111111111";
        string movementLegId = "22222222-2222-2222-2222-222222222222";
        await WriteEnvelopeAsync(context, CreateEnvelope(
            context,
            "UpcomingStopPlanSnapshot",
            null,
            new
            {
                planRevision = 1,
                demandId,
                legs = new[]
                {
                    new
                    {
                        movementLegId,
                        legType = "TO_PICKUP",
                        sequence = 1,
                        stationId = "ST-01",
                        mapId = "MAP-01",
                        state = "ACTIVE"
                    }
                }
            })).ConfigureAwait(false);
        await WriteEnvelopeAsync(context, CreateEnvelope(
            context,
            "CurrentStopWorklistSnapshot",
            null,
            new
            {
                stationId = "ST-01",
                worklistRevision = 1,
                operationSessionId = (string?)null,
                items = new[]
                {
                    new
                    {
                        demandId,
                        transportDemandKey = "TD-001",
                        sublot = "SUBLOT-001",
                        workType = "WIRE_TO_GATE",
                        stopRole = "PICKUP",
                        expectedBasketCount = 2
                    }
                }
            })).ConfigureAwait(false);
        await WriteEnvelopeAsync(context, CreateEnvelope(
            context,
            "UpcomingStopPlanSnapshot",
            null,
            new
            {
                planRevision = 2,
                demandId,
                legs = new[]
                {
                    new
                    {
                        movementLegId,
                        legType = "TO_PICKUP",
                        sequence = 1,
                        stationId = "ST-01",
                        mapId = "MAP-01",
                        state = "ARRIVED"
                    }
                }
            })).ConfigureAwait(false);
    }

    private static WireToGateEnvelope CreateProtocolProblem(
        ConnectionContext context,
        string rejectedMessageId,
        string rejectedMessageType,
        string reasonCode) =>
        CreateEnvelope(
            context,
            "ProtocolProblem",
            rejectedMessageId,
            new
            {
                rejectedMessageId,
                rejectedMessageType,
                problem = new
                {
                    reasonCode,
                    fieldPath = (string?)null,
                    displayMessage = (string?)null
                },
                expectedProtocolVersion = 1,
                expectedProfileId = WireToGateRelease.ProfileId,
                expectedProtocolReleaseManifestSha256 = WireToGateRelease.ManifestSha256
            });

    private static async Task WriteEnvelopeAsync(ConnectionContext context, WireToGateEnvelope envelope)
    {
        await context.Writer.WriteLineAsync(WireToGateProtocolSerializer.Serialize(envelope)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _listener.Stop();
        _stopping.Dispose();
    }
}
