using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

public sealed record WireToGateSessionOptions(
    string Host,
    int Port,
    string AgvId,
    string OnboardInstanceId,
    string OnboardBuildCommit,
    string CredentialEnvironmentVariable,
    bool UseTls,
    string? ServerCertificateSha256,
    TimeSpan ConnectTimeout,
    TimeSpan MessageTimeout,
    long CapabilityVersion,
    long SafetyStateVersion,
    string SlotModelVersion,
    string ActiveSlotConfigurationVersion,
    bool SupportsBatchUnlock);

public sealed class WireToGateSessionClient : IAsyncDisposable
{
    private readonly WireToGateSessionOptions _options;
    private readonly IIoModuleClient _ioModule;
    private readonly IWireToGateJournal _journal;
    private readonly IClock _clock;
    private readonly Func<bool> _vehicleStoppedProvider;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _journeyGate = new();
    private readonly Dictionary<string, (long Revision, string ContentSha256)> _journeyRevisions = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WireToGateEnvelope>> _responseWaiters = [];
    private TcpClient? _client;
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private WireToGateSessionSnapshot _current;
    private WireToGateJourneySnapshot _journey;
    private CancellationTokenSource? _receiveStopping;
    private TaskCompletionSource<Exception>? _receiveFailure;
    private string? _journalEpoch;
    private long _acceptedCapabilityVersion;
    private long _acceptedSafetyStateVersion;
    private bool _disposed;

    public WireToGateSessionClient(
        WireToGateSessionOptions options,
        IIoModuleClient ioModule,
        IWireToGateJournal journal,
        IClock clock,
        Func<bool> vehicleStoppedProvider)
    {
        _options = options;
        _ioModule = ioModule;
        _journal = journal;
        _clock = clock;
        _vehicleStoppedProvider = vehicleStoppedProvider;
        ValidateOptions(options);
        _acceptedCapabilityVersion = options.CapabilityVersion;
        _acceptedSafetyStateVersion = options.SafetyStateVersion;
        _current = new WireToGateSessionSnapshot(
            false,
            null,
            WireToGateSessionReadiness.Disconnected,
            [],
            options.CapabilityVersion,
            options.SafetyStateVersion,
            clock.Now);
        _journey = WireToGateJourneySnapshot.Empty;
    }

    public WireToGateSessionSnapshot Current => Volatile.Read(ref _current);

    public bool IsConnected => Current.Connected;

    public bool IsReady => Current.Readiness == WireToGateSessionReadiness.Ready;

    public WireToGateJourneySnapshot CurrentJourney => Volatile.Read(ref _journey);

    public event EventHandler<ValueChangedEventArgs<WireToGateSessionSnapshot>>? StateChanged;

    public event EventHandler<ValueChangedEventArgs<WireToGateJourneySnapshot>>? JourneyChanged;

    /// <summary>
    /// Formal WIRE_TO_GATE business messages received after session recovery.
    /// Handlers must treat the command as untrusted input and perform their own
    /// physical-state checks before causing side effects.
    /// </summary>
    public event EventHandler<ValueChangedEventArgs<WireToGateServerCommand>>? ServerCommandReceived;

    public Task<string> SendSublotSubmittedAsync(
        string demandId,
        string operationSessionId,
        string stationId,
        long worklistRevision,
        string sublot,
        string entryMethod,
        string operatorId,
        string verificationMethod,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default) =>
        SendDurableAsync(
            "SublotSubmitted",
            $"sublot:{demandId}:{operationSessionId}:{worklistRevision}:{sublot}",
            StableUuid($"sublot:{demandId}:{operationSessionId}:{worklistRevision}:{sublot}"),
            null,
            new SublotSubmittedPayload(
                demandId,
                operationSessionId,
                stationId,
                worklistRevision,
                sublot,
                entryMethod,
                new WireToGateOperatorContextPayload(operatorId, verificationMethod, verifiedAt)),
            cancellationToken);

    public Task<string> SendOperationProgressAsync(
        string slotOperationAttemptId,
        string phase,
        IReadOnlyList<int> activeUnlockSlots,
        IReadOnlyList<int> completedSlots,
        DateTimeOffset? observedAt = null,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset effectiveObservedAt = (observedAt ?? _clock.Now).ToUniversalTime();
        string deduplicationKey =
            $"operation-progress:{slotOperationAttemptId}:{phase}:{string.Join(',', activeUnlockSlots)}:{string.Join(',', completedSlots)}:{effectiveObservedAt:O}";
        return SendDurableAsync(
            "OperationProgress",
            deduplicationKey,
            StableUuid(deduplicationKey),
            null,
            new OperationProgressPayload(
                slotOperationAttemptId,
                phase,
                activeUnlockSlots.Order().ToArray(),
                completedSlots.Order().ToArray(),
                effectiveObservedAt),
            cancellationToken);
    }

    public Task<string> SendOperationResultAsync(
        string deduplicationKey,
        string messageId,
        WireToGateOperationResultPayload payload,
        CancellationToken cancellationToken = default) =>
        SendDurableAsync(
            "OperationResult",
            deduplicationKey,
            messageId,
            null,
            payload,
            cancellationToken);

    public Task<string> SendPreDepartureSafetyCheckResultAsync(
        string preDepartureSafetyCheckId,
        string outcome,
        DateTimeOffset observedAt,
        long safetyStateVersion,
        DateTimeOffset validUntil,
        WireToGateSafetySummaryPayload safety,
        CancellationToken cancellationToken = default) =>
        SendDurableAsync(
            "PreDepartureSafetyCheckResult",
            $"predeparture:{preDepartureSafetyCheckId}:{safetyStateVersion}:{outcome}",
            StableUuid($"predeparture:{preDepartureSafetyCheckId}:{safetyStateVersion}:{outcome}"),
            preDepartureSafetyCheckId,
            new PreDepartureSafetyCheckResultPayload(
                preDepartureSafetyCheckId,
                outcome,
                observedAt,
                safetyStateVersion,
                validUntil,
                safety),
            cancellationToken);

    public async Task<string> SendSafetyStateChangedAsync(
        long safetyStateVersion,
        DateTimeOffset observedAt,
        WireToGateSafetySummaryPayload safety,
        IReadOnlyList<int> affectedSlots,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(safetyStateVersion);

        int[] sortedSlots = affectedSlots.Order().ToArray();
        if (sortedSlots.Any(slot => slot is < 1 or > 8)
            || sortedSlots.Distinct().Count() != sortedSlots.Length)
        {
            throw new InvalidDataException("SLOT_SET_INVALID");
        }

        string journalEpoch = Volatile.Read(ref _journalEpoch)
            ?? throw new InvalidOperationException("WIRE_TO_GATE_JOURNAL_NOT_READY");
        // The epoch supplies strict cross-journal uniqueness.  The version and
        // observation time keep retries of one in-memory work item stable while
        // allowing a later observation to remain a distinct event even if a
        // runtime is still using the same server baseline version.
        string deduplicationKey =
            $"safety-state-changed:{journalEpoch}:{safetyStateVersion}:{observedAt.ToUniversalTime():O}";
        string messageId = await SendDurableCoreAsync(
            "SafetyStateChanged",
            deduplicationKey,
            StableUuid(deduplicationKey),
            null,
            new SafetyStateChangedPayload(
                safetyStateVersion,
                observedAt.ToUniversalTime(),
                safety,
                sortedSlots),
            allowRecoveryRequired: true,
            cancellationToken).ConfigureAwait(false);
        AdvanceSafetyStateVersion(safetyStateVersion);
        WireToGateSessionSnapshot current = Current;
        Publish(
            current.Connected,
            current.SessionGeneration,
            current.Readiness,
            current.ReasonCodes);
        return messageId;
    }

    public async Task<WireToGateSessionSnapshot> ConnectAndRecoverAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string credentialProof = Environment.GetEnvironmentVariable(_options.CredentialEnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"未设置凭据环境变量{_options.CredentialEnvironmentVariable}。");
        if (string.IsNullOrWhiteSpace(credentialProof))
        {
            throw new InvalidOperationException("WIRE_TO_GATE凭据不能为空。");
        }

        Publish(false, null, WireToGateSessionReadiness.Recovering, []);
        await _journal.InitializeAsync(cancellationToken).ConfigureAwait(false);
        string journalEpoch = await _journal.ReadJournalEpochAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _journalEpoch, journalEpoch);

        try
        {
            await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await RestorePersistedJourneyProjectionAsync(cancellationToken).ConfigureAwait(false);
            string helloMessageId = Guid.NewGuid().ToString("D");
            WireToGateEnvelope hello = WireToGateProtocolSerializer.Create(
                "SessionHello",
                helloMessageId,
                null,
                _options.AgvId,
                null,
                _clock.Now.ToUniversalTime(),
                new SessionHelloPayload(
                    _options.OnboardInstanceId,
                    _options.OnboardBuildCommit,
                    WireToGateRelease.ProtocolVersion,
                    WireToGateRelease.ProfileId,
                    WireToGateRelease.Identity,
                    credentialProof));
            await SendEnvelopeAsync(hello, cancellationToken).ConfigureAwait(false);

            WireToGateEnvelope response = await ReadEnvelopeAsync(null, cancellationToken).ConfigureAwait(false);
            if (response.MessageType == "SessionRejected")
            {
                WireToGateProtocolSerializer.RequireMessage(response, "SessionRejected", helloMessageId);
                SessionRejectedPayload rejected = WireToGateProtocolSerializer
                    .DeserializePayload<SessionRejectedPayload>(response);
                throw new InvalidDataException(rejected.Problem.ReasonCode);
            }

            WireToGateProtocolSerializer.RequireMessage(response, "SessionAccepted", helloMessageId);
            if (response.SessionGeneration is null)
            {
                throw new InvalidDataException("SessionAccepted缺少sessionGeneration。");
            }

            long generation = response.SessionGeneration.Value;
            SessionAcceptedPayload accepted = WireToGateProtocolSerializer
                .DeserializePayload<SessionAcceptedPayload>(response);
            if (accepted.SessionGeneration != generation)
            {
                throw new InvalidDataException("STALE_SESSION_GENERATION");
            }

            RequireUuid(accepted.ServerInstanceId, nameof(accepted.ServerInstanceId));
            WireToGateProtocolSerializer.RequireExactReleaseIdentity(accepted.AcceptedProtocolReleaseIdentity);
            Publish(true, generation, WireToGateSessionReadiness.Recovering, []);

            IReadOnlyList<WireToGateDurableMessage> unacknowledged = await _journal
                .ReadUnacknowledgedOutgoingAsync(cancellationToken)
                .ConfigureAwait(false);
            bool resumingInterruptedRecovery = unacknowledged.Count > 0;
            if (resumingInterruptedRecovery)
            {
                foreach (WireToGateDurableMessage pending in unacknowledged)
                {
                    await ReplayDurableOutgoingAsync(pending, generation, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                IoSnapshot io = _ioModule.CurrentSnapshot;
                ProtocolSlotState[] slotStates = CreateSlotStates(io);
                await SendSnapshotAndRequireAckAsync(
                    "CapabilitySnapshot",
                    "CAPABILITY",
                    _options.CapabilityVersion,
                    generation,
                    new CapabilitySnapshotPayload(
                        _options.CapabilityVersion,
                        _clock.Now.ToUniversalTime(),
                        _options.SlotModelVersion,
                        _options.ActiveSlotConfigurationVersion,
                        slotStates,
                        _options.SupportsBatchUnlock,
                        1),
                    cancellationToken).ConfigureAwait(false);

                ProtocolSafetySummary safety = CreateSafetySummary(io, slotStates);
                long safetyStateVersion = Volatile.Read(ref _acceptedSafetyStateVersion);
                await SendSnapshotAndRequireAckAsync(
                    "SafetyStateSnapshot",
                    "SAFETY_STATE",
                    safetyStateVersion,
                    generation,
                    new SafetyStateSnapshotPayload(
                        safetyStateVersion,
                        _clock.Now.ToUniversalTime(),
                        safety,
                        slotStates),
                    cancellationToken).ConfigureAwait(false);

                await SendRecoveryStateReportAsync(generation, io, cancellationToken).ConfigureAwait(false);
            }

            WireToGateEnvelope readinessEnvelope = await ReadEnvelopeAsync(generation, cancellationToken)
                .ConfigureAwait(false);
            ThrowIfProtocolProblem(readinessEnvelope);
            ApplySessionReadiness(
                readinessEnvelope,
                requireExactConfiguredBaseline: !resumingInterruptedRecovery);
            StartReceiveLoop(generation);
            return Current;
        }
        catch
        {
            await CloseConnectionAsync().ConfigureAwait(false);
            Publish(false, null, WireToGateSessionReadiness.Disconnected, ["SESSION_RECOVERY_REQUIRED"]);
            throw;
        }
    }

    public async Task SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        WireToGateSessionSnapshot current = Current;
        if (!current.Connected || current.SessionGeneration is null)
        {
            throw new InvalidOperationException("WIRE_TO_GATE会话尚未建立。");
        }

        TaskCompletionSource<Exception>? receiveFailure = _receiveFailure;
        if (receiveFailure?.Task.IsCompletedSuccessfully == true)
        {
            throw receiveFailure.Task.Result;
        }

        string messageId = Guid.NewGuid().ToString("D");
        WireToGateEnvelope heartbeat = WireToGateProtocolSerializer.Create(
            "Heartbeat",
            messageId,
            null,
            _options.AgvId,
            current.SessionGeneration,
            _clock.Now.ToUniversalTime(),
            new HeartbeatPayload(current.CapabilityVersion, current.SafetyStateVersion));
        TaskCompletionSource<WireToGateEnvelope> response = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_responseWaiters.TryAdd(messageId, response))
        {
            throw new InvalidOperationException("重复的WIRE_TO_GATE心跳messageId。");
        }

        try
        {
            await SendEnvelopeAsync(heartbeat, cancellationToken).ConfigureAwait(false);
            WireToGateEnvelope ackEnvelope = await response.Task
                .WaitAsync(_options.MessageTimeout, cancellationToken)
                .ConfigureAwait(false);
            ThrowIfProtocolProblem(ackEnvelope);
            WireToGateProtocolSerializer.RequireMessage(ackEnvelope, "HeartbeatAck", messageId);
            HeartbeatAckPayload ack = WireToGateProtocolSerializer.DeserializePayload<HeartbeatAckPayload>(ackEnvelope);
            if (ack.ReceivedHeartbeatMessageId != messageId)
            {
                throw new InvalidDataException("CORRELATION_INVALID");
            }
        }
        finally
        {
            _responseWaiters.TryRemove(messageId, out _);
        }
    }

    /// <summary>
    /// Persists a business message before writing it to the socket and keeps the
    /// same message identity/content until the server durably acknowledges it.
    /// This is the common path for operation progress/results and safety results;
    /// reconnect recovery replays the same outbox row automatically.
    /// </summary>
    public Task<string> SendDurableAsync(
        string messageType,
        string deduplicationKey,
        string messageId,
        string? correlationId,
        object payload,
        CancellationToken cancellationToken = default) =>
        SendDurableCoreAsync(
            messageType,
            deduplicationKey,
            messageId,
            correlationId,
            payload,
            allowRecoveryRequired: false,
            cancellationToken);

    private async Task<string> SendDurableCoreAsync(
        string messageType,
        string deduplicationKey,
        string messageId,
        string? correlationId,
        object payload,
        bool allowRecoveryRequired,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);
        RequireUuid(messageId, nameof(messageId));

        WireToGateSessionSnapshot current = Current;
        bool readinessAllowed = current.Readiness == WireToGateSessionReadiness.Ready
            || allowRecoveryRequired
                && current.Readiness == WireToGateSessionReadiness.RecoveryRequired;
        if (!current.Connected
            || current.SessionGeneration is null
            || !readinessAllowed)
        {
            throw new InvalidOperationException("WIRE_TO_GATE_NOT_READY");
        }

        WireToGateDurableMessage? existing = await _journal
            .ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken)
            .ConfigureAwait(false);
        WireToGateDurableMessage stored;
        if (existing is not null)
        {
            WireToGateEnvelope existingEnvelope = WireToGateProtocolSerializer.DeserializeAndValidate(
                existing.WireLine.TrimEnd('\r', '\n'),
                _options.AgvId);
            WireToGateEnvelope candidateEnvelope = WireToGateProtocolSerializer.Create(
                messageType,
                messageId,
                correlationId,
                _options.AgvId,
                current.SessionGeneration,
                existingEnvelope.SentAt,
                payload);
            if (existing.MessageType != messageType
                || existing.MessageId != messageId
                || existingEnvelope.MessageType != messageType
                || existingEnvelope.MessageId != messageId
                || existingEnvelope.CorrelationId != correlationId
                || !JsonNode.DeepEquals(
                    JsonNode.Parse(existingEnvelope.Payload.GetRawText()),
                    JsonNode.Parse(candidateEnvelope.Payload.GetRawText())))
            {
                throw new InvalidDataException("BUSINESS_ID_CONTENT_CONFLICT");
            }

            stored = await RebindDurableMessageForSessionAsync(
                existing,
                current.SessionGeneration.Value,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            WireToGateEnvelope envelope = WireToGateProtocolSerializer.Create(
                messageType,
                messageId,
                correlationId,
                _options.AgvId,
                current.SessionGeneration,
                _clock.Now.ToUniversalTime(),
                payload);
            string contentSha256 = WireToGateProtocolSerializer.ComputeContentSha256(envelope);
            WireToGateDurableMessage durable = new(
                deduplicationKey,
                messageType,
                messageId,
                contentSha256,
                WireToGateProtocolSerializer.SerializeLine(envelope),
                _clock.Now.ToUniversalTime(),
                false);
            stored = await _journal
                .SaveOutgoingBeforeSendAsync(durable, cancellationToken)
                .ConfigureAwait(false);
        }

        // A previously acknowledged business key is already complete.  Returning
        // here is important for UI retries: a retry must not create another side
        // effect or another message identity.
        if (stored.Acknowledged)
        {
            return stored.MessageId;
        }

        TaskCompletionSource<WireToGateEnvelope> response = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_responseWaiters.TryAdd(stored.MessageId, response))
        {
            throw new InvalidOperationException("重复的WIRE_TO_GATE业务messageId。");
        }

        try
        {
            await SendLineAsync(stored.WireLine, cancellationToken).ConfigureAwait(false);
            WireToGateEnvelope ackEnvelope = await response.Task
                .WaitAsync(_options.MessageTimeout, cancellationToken)
                .ConfigureAwait(false);
            ThrowIfProtocolProblem(ackEnvelope);
            WireToGateProtocolSerializer.RequireMessage(ackEnvelope, "DurableAck", stored.MessageId);
            DurableAckPayload ack = WireToGateProtocolSerializer
                .DeserializePayload<DurableAckPayload>(ackEnvelope);
            if (ack.AcceptedMessageId != stored.MessageId
                || ack.AcceptedMessageType != stored.MessageType
                || ack.AcceptedContentSha256 != stored.ContentSha256)
            {
                throw new InvalidDataException("CONTENT_HASH_MISMATCH");
            }

            await _journal.MarkOutgoingAcknowledgedAsync(
                stored.MessageId,
                stored.ContentSha256,
                cancellationToken).ConfigureAwait(false);
            return stored.MessageId;
        }
        finally
        {
            _responseWaiters.TryRemove(stored.MessageId, out _);
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            await CloseConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            Publish(false, null, WireToGateSessionReadiness.Disconnected, ["SESSION_RECOVERY_REQUIRED"]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CloseConnectionAsync().ConfigureAwait(false);
        _sendGate.Dispose();
        Publish(false, null, WireToGateSessionReadiness.Disconnected, []);
        GC.SuppressFinalize(this);
    }

    private async Task SendSnapshotAndRequireAckAsync(
        string messageType,
        string snapshotKind,
        long revision,
        long generation,
        object payload,
        CancellationToken cancellationToken)
    {
        string messageId = Guid.NewGuid().ToString("D");
        WireToGateEnvelope snapshot = WireToGateProtocolSerializer.Create(
            messageType,
            messageId,
            null,
            _options.AgvId,
            generation,
            _clock.Now.ToUniversalTime(),
            payload);
        string contentSha256 = WireToGateProtocolSerializer.ComputeContentSha256(snapshot);
        await SendEnvelopeAsync(snapshot, cancellationToken).ConfigureAwait(false);

        WireToGateEnvelope ackEnvelope = await ReadEnvelopeAsync(generation, cancellationToken).ConfigureAwait(false);
        ThrowIfProtocolProblem(ackEnvelope);
        WireToGateProtocolSerializer.RequireMessage(ackEnvelope, "SnapshotAppliedAck", messageId);
        SnapshotAppliedAckPayload ack = WireToGateProtocolSerializer
            .DeserializePayload<SnapshotAppliedAckPayload>(ackEnvelope);
        if (ack.SnapshotMessageId != messageId
            || ack.SnapshotKind != snapshotKind
            || ack.AppliedRevision != revision
            || ack.AppliedContentSha256 != contentSha256)
        {
            throw new InvalidDataException("CONTENT_HASH_MISMATCH");
        }
    }

    private async Task SendRecoveryStateReportAsync(
        long generation,
        IoSnapshot io,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState recovery = await _journal.ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        string journalSha256 = await _journal.ComputeContentSha256Async(cancellationToken).ConfigureAwait(false);
        int[] observedActiveSlots = io.Lockers
            .Where(locker => locker.UnlockOutputRaw is true)
            .Select(locker => locker.PhysicalNumber)
            .Concat(recovery.ActiveUnlockSlots)
            .Distinct()
            .Order()
            .ToArray();
        string messageId = Guid.NewGuid().ToString("D");
        string reportId = Guid.NewGuid().ToString("D");
        WireToGateEnvelope report = WireToGateProtocolSerializer.Create(
            "RecoveryStateReport",
            messageId,
            null,
            _options.AgvId,
            generation,
            _clock.Now.ToUniversalTime(),
            new RecoveryStateReportPayload(
                reportId,
                _clock.Now.ToUniversalTime(),
                recovery.UnsettledSlotOperationAttemptId,
                ToProtocolCheckpoint(recovery.ProvenRecoveryCheckpoint),
                observedActiveSlots,
                recovery.ForcedRecoveryGeneration,
                recovery.PendingResults.Select(item => new PendingResultPayload(
                    item.MessageType,
                    item.MessageId,
                    item.BusinessId,
                    item.ContentSha256)).ToArray(),
                journalSha256));
        string contentSha256 = WireToGateProtocolSerializer.ComputeContentSha256(report);
        string wireLine = WireToGateProtocolSerializer.SerializeLine(report);
        WireToGateDurableMessage durable = new(
            $"recovery:{generation}:{reportId}",
            report.MessageType,
            report.MessageId,
            contentSha256,
            wireLine,
            _clock.Now.ToUniversalTime(),
            false);
        WireToGateDurableMessage stored = await _journal
            .SaveOutgoingBeforeSendAsync(durable, cancellationToken)
            .ConfigureAwait(false);
        await SendLineAsync(stored.WireLine, cancellationToken).ConfigureAwait(false);

        WireToGateEnvelope ackEnvelope = await ReadEnvelopeAsync(generation, cancellationToken).ConfigureAwait(false);
        ThrowIfProtocolProblem(ackEnvelope);
        WireToGateProtocolSerializer.RequireMessage(ackEnvelope, "DurableAck", messageId);
        DurableAckPayload ack = WireToGateProtocolSerializer.DeserializePayload<DurableAckPayload>(ackEnvelope);
        if (ack.AcceptedMessageId != messageId
            || ack.AcceptedMessageType != "RecoveryStateReport"
            || ack.AcceptedContentSha256 != contentSha256)
        {
            throw new InvalidDataException("CONTENT_HASH_MISMATCH");
        }

        await _journal.MarkOutgoingAcknowledgedAsync(messageId, contentSha256, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReplayDurableOutgoingAsync(
        WireToGateDurableMessage pending,
        long generation,
        CancellationToken cancellationToken)
    {
        string storedLine = pending.WireLine.TrimEnd('\r', '\n');
        WireToGateEnvelope storedEnvelope = WireToGateProtocolSerializer.DeserializeAndValidate(
            storedLine,
            _options.AgvId);
        string storedContentSha256 = WireToGateProtocolSerializer.ComputeContentSha256(storedEnvelope);
        if (storedEnvelope.MessageId != pending.MessageId
            || storedEnvelope.MessageType != pending.MessageType
            || storedContentSha256 != pending.ContentSha256)
        {
            throw new InvalidDataException("DURABLE_OUTBOX_CONTENT_MISMATCH");
        }

        WireToGateDurableMessage rebound = await RebindDurableMessageForSessionAsync(
            pending,
            generation,
            cancellationToken).ConfigureAwait(false);
        if (rebound.Acknowledged)
        {
            return;
        }

        await SendLineAsync(rebound.WireLine, cancellationToken).ConfigureAwait(false);
        WireToGateEnvelope ackEnvelope = await ReadEnvelopeAsync(generation, cancellationToken).ConfigureAwait(false);
        ThrowIfProtocolProblem(ackEnvelope);
        WireToGateProtocolSerializer.RequireMessage(ackEnvelope, "DurableAck", rebound.MessageId);
        DurableAckPayload ack = WireToGateProtocolSerializer.DeserializePayload<DurableAckPayload>(ackEnvelope);
        if (ack.AcceptedMessageId != rebound.MessageId
            || ack.AcceptedMessageType != rebound.MessageType
            || ack.AcceptedContentSha256 != rebound.ContentSha256)
        {
            throw new InvalidDataException("CONTENT_HASH_MISMATCH");
        }

        await _journal.MarkOutgoingAcknowledgedAsync(
            rebound.MessageId,
            rebound.ContentSha256,
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(rebound.MessageType, "SafetyStateChanged", StringComparison.Ordinal))
        {
            SafetyStateChangedPayload payload = WireToGateProtocolSerializer
                .DeserializePayload<SafetyStateChangedPayload>(storedEnvelope);
            AdvanceSafetyStateVersion(payload.SafetyStateVersion);
        }
    }

    private async Task<WireToGateDurableMessage> RebindDurableMessageForSessionAsync(
        WireToGateDurableMessage pending,
        long generation,
        CancellationToken cancellationToken)
    {
        WireToGateEnvelope storedEnvelope = WireToGateProtocolSerializer.DeserializeAndValidate(
            pending.WireLine.TrimEnd('\r', '\n'),
            _options.AgvId);
        if (storedEnvelope.MessageId != pending.MessageId
            || storedEnvelope.MessageType != pending.MessageType
            || WireToGateProtocolSerializer.ComputeContentSha256(storedEnvelope) != pending.ContentSha256)
        {
            throw new InvalidDataException("DURABLE_OUTBOX_CONTENT_MISMATCH");
        }

        WireToGateEnvelope reboundEnvelope = WireToGateProtocolSerializer.RebindSessionGeneration(
            storedEnvelope,
            generation);
        WireToGateDurableMessage rebound = pending with
        {
            ContentSha256 = WireToGateProtocolSerializer.ComputeContentSha256(reboundEnvelope),
            WireLine = WireToGateProtocolSerializer.SerializeLine(reboundEnvelope),
            Acknowledged = false
        };
        return await _journal.ReplaceOutgoingForReplayAsync(
            pending,
            rebound,
            cancellationToken).ConfigureAwait(false);
    }

    private void StartReceiveLoop(long generation)
    {
        StreamReader reader = _reader ?? throw new IOException("WIRE_TO_GATE连接不可用。");
        CancellationTokenSource stopping = new();
        TaskCompletionSource<Exception> failure = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _receiveStopping = stopping;
        _receiveFailure = failure;
        _ = Task.Run(() => ReceiveLoopAsync(reader, generation, stopping, failure), stopping.Token);
    }

    private async Task ReceiveLoopAsync(
        StreamReader reader,
        long generation,
        CancellationTokenSource stopping,
        TaskCompletionSource<Exception> failure)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(stopping.Token).ConfigureAwait(false);
                if (line is null)
                {
                    throw new EndOfStreamException("ControlServer在旅程会话期间关闭了连接。");
                }

                stopping.Token.ThrowIfCancellationRequested();

                WireToGateEnvelope envelope = WireToGateProtocolSerializer.DeserializeAndValidate(
                    line,
                    _options.AgvId,
                    generation);
                if (IsJourneySnapshot(envelope.MessageType))
                {
                    await ApplyJourneySnapshotAsync(envelope, generation, stopping.Token)
                        .ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(envelope.MessageType, "SessionReadiness", StringComparison.Ordinal))
                {
                    ApplySessionReadiness(envelope, requireExactConfiguredBaseline: false);
                    continue;
                }

                if (TryCreateServerCommand(envelope, out WireToGateServerCommand? command))
                {
                    ServerCommandReceived?.Invoke(
                        this,
                        new ValueChangedEventArgs<WireToGateServerCommand>(command!));
                    continue;
                }

                if (envelope.CorrelationId is not null
                    && _responseWaiters.TryRemove(envelope.CorrelationId, out TaskCompletionSource<WireToGateEnvelope>? response))
                {
                    response.TrySetResult(envelope);
                    continue;
                }

                ThrowIfProtocolProblem(envelope);
                throw new InvalidDataException($"收到未处理的WIRE_TO_GATE消息：{envelope.MessageType}。");
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure.TrySetResult(exception);
            foreach (KeyValuePair<string, TaskCompletionSource<WireToGateEnvelope>> waiter in _responseWaiters)
            {
                waiter.Value.TrySetException(exception);
            }

            ResetJourneyProjection();
            Publish(false, null, WireToGateSessionReadiness.Disconnected, ["SESSION_RECOVERY_REQUIRED"]);
        }
    }

    private async Task ApplyJourneySnapshotAsync(
        WireToGateEnvelope envelope,
        long generation,
        CancellationToken cancellationToken)
    {
        string contentSha256 = WireToGateProtocolSerializer.ComputeContentSha256(envelope);
        try
        {
            if (envelope.CorrelationId is not null)
            {
                throw new InvalidDataException("CORRELATION_INVALID");
            }

            (long revision, string snapshotKind) = ApplyJourneyProjection(
                envelope,
                contentSha256,
                cancellationToken);
            await _journal.SaveAppliedJourneySnapshotAsync(
                new WireToGateAppliedJourneySnapshot(
                    envelope.MessageType,
                    envelope.MessageId,
                    revision,
                    contentSha256,
                    envelope.Payload.GetRawText(),
                    _clock.Now.ToUniversalTime()),
                cancellationToken).ConfigureAwait(false);
            await SendSnapshotAppliedAckAsync(
                envelope,
                snapshotKind,
                revision,
                contentSha256,
                generation,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            await SendProtocolProblemAsync(
                envelope,
                generation,
                exception.Message,
                cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (JsonException exception)
        {
            InvalidDataException invalid = new("PROTOCOL_SCHEMA_INVALID", exception);
            await SendProtocolProblemAsync(
                envelope,
                generation,
                invalid.Message,
                cancellationToken).ConfigureAwait(false);
            throw invalid;
        }
    }

    private async Task RestorePersistedJourneyProjectionAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<WireToGateAppliedJourneySnapshot> snapshots = await _journal
            .ReadAppliedJourneySnapshotsAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (WireToGateAppliedJourneySnapshot snapshot in snapshots)
        {
            using JsonDocument payloadDocument = JsonDocument.Parse(snapshot.PayloadJson);
            WireToGateEnvelope envelope = new(
                WireToGateRelease.ProtocolVersion,
                WireToGateRelease.ProfileId,
                WireToGateRelease.ReleaseVersion,
                WireToGateRelease.ManifestSha256,
                snapshot.MessageType,
                snapshot.MessageId,
                null,
                _options.AgvId,
                null,
                snapshot.AppliedAt,
                payloadDocument.RootElement.Clone());
            (long revision, _) = ApplyJourneyProjection(
                envelope,
                snapshot.ContentSha256,
                cancellationToken);
            if (revision != snapshot.Revision)
            {
                throw new InvalidDataException("PERSISTED_SNAPSHOT_REVISION_MISMATCH");
            }
        }
    }

    private (long Revision, string SnapshotKind) ApplyJourneyProjection(
        WireToGateEnvelope envelope,
        string contentSha256,
        CancellationToken cancellationToken)
    {
        switch (envelope.MessageType)
        {
            case "VehicleBusinessStateSnapshot":
                {
                    VehicleBusinessStateSnapshotPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<VehicleBusinessStateSnapshotPayload>(envelope);
                    ValidateVehicleBusinessState(payload);
                    ApplyJourneyRevision(
                        envelope.MessageType,
                        payload.VehicleBusinessStateRevision,
                        contentSha256,
                        journey => journey with
                        {
                            VehicleBusinessState = new WireToGateVehicleBusinessState(
                                payload.VehicleBusinessStateRevision,
                                payload.Readiness,
                                payload.ManualChargingHold,
                                payload.BatteryState,
                                payload.BlockingFacts
                                    .Select(item => new WireToGateBlockingFact(
                                        item.ReasonCode,
                                        item.SubjectType,
                                        item.SubjectId))
                                    .ToArray(),
                                payload.ObservedAt,
                                contentSha256),
                            UpdatedAt = _clock.Now
                        },
                        cancellationToken);
                    return (payload.VehicleBusinessStateRevision, "VEHICLE_BUSINESS_STATE");
                }
            case "CurrentStopWorklistSnapshot":
                {
                    CurrentStopWorklistSnapshotPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<CurrentStopWorklistSnapshotPayload>(envelope);
                    ValidateCurrentStopWorklist(payload);
                    ApplyJourneyRevision(
                        envelope.MessageType,
                        payload.WorklistRevision,
                        contentSha256,
                        journey => journey with
                        {
                            CurrentStopWorklist = new WireToGateCurrentStopWorklist(
                                payload.StationId,
                                payload.WorklistRevision,
                                payload.OperationSessionId,
                                payload.Items.Select(ToCoreWorklistItem).ToArray(),
                                contentSha256),
                            UpdatedAt = _clock.Now
                        },
                        cancellationToken);
                    return (payload.WorklistRevision, "CURRENT_STOP_WORKLIST");
                }
            case "UpcomingStopPlanSnapshot":
                {
                    UpcomingStopPlanSnapshotPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<UpcomingStopPlanSnapshotPayload>(envelope);
                    ValidateUpcomingStopPlan(payload);
                    ApplyJourneyRevision(
                        envelope.MessageType,
                        payload.PlanRevision,
                        contentSha256,
                        journey => journey with
                        {
                            UpcomingStopPlan = new WireToGateUpcomingStopPlan(
                                payload.PlanRevision,
                                payload.DemandId,
                                payload.Legs.Select(ToCoreMovementLeg).ToArray(),
                                contentSha256),
                            UpdatedAt = _clock.Now
                        },
                        cancellationToken);
                    return (payload.PlanRevision, "UPCOMING_STOP_PLAN");
                }
            default:
                throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }
    }

    private async Task SendSnapshotAppliedAckAsync(
        WireToGateEnvelope snapshot,
        string snapshotKind,
        long revision,
        string contentSha256,
        long generation,
        CancellationToken cancellationToken)
    {
        WireToGateEnvelope ack = WireToGateProtocolSerializer.Create(
            "SnapshotAppliedAck",
            Guid.NewGuid().ToString("D"),
            snapshot.MessageId,
            _options.AgvId,
            generation,
            _clock.Now.ToUniversalTime(),
            new SnapshotAppliedAckPayload(
                snapshot.MessageId,
                snapshotKind,
                revision,
                contentSha256));
        await SendEnvelopeAsync(ack, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyJourneyRevision(
        string messageType,
        long revision,
        string contentSha256,
        Func<WireToGateJourneySnapshot, WireToGateJourneySnapshot> apply,
        CancellationToken cancellationToken)
    {
        WireToGateJourneySnapshot? updated = null;
        lock (_journeyGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_journeyRevisions.TryGetValue(messageType, out (long Revision, string ContentSha256) previous))
            {
                if (revision < previous.Revision)
                {
                    throw new InvalidDataException("SNAPSHOT_REVISION_REGRESSION");
                }

                if (revision == previous.Revision)
                {
                    if (contentSha256 != previous.ContentSha256)
                    {
                        throw new InvalidDataException("SNAPSHOT_REVISION_CONTENT_CONFLICT");
                    }

                    return;
                }
            }

            _journeyRevisions[messageType] = (revision, contentSha256);
            updated = apply(_journey);
            Volatile.Write(ref _journey, updated);
        }

        JourneyChanged?.Invoke(this, new ValueChangedEventArgs<WireToGateJourneySnapshot>(updated));
    }

    private void ResetJourneyProjection()
    {
        bool changed;
        lock (_journeyGate)
        {
            changed = _journeyRevisions.Count > 0 || _journey != WireToGateJourneySnapshot.Empty;
            _journeyRevisions.Clear();
            Volatile.Write(ref _journey, WireToGateJourneySnapshot.Empty);
        }

        if (changed)
        {
            JourneyChanged?.Invoke(
                this,
                new ValueChangedEventArgs<WireToGateJourneySnapshot>(WireToGateJourneySnapshot.Empty));
        }
    }

    private async Task SendProtocolProblemAsync(
        WireToGateEnvelope rejected,
        long generation,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        WireToGateEnvelope problem = WireToGateProtocolSerializer.Create(
            "ProtocolProblem",
            Guid.NewGuid().ToString("D"),
            rejected.MessageId,
            _options.AgvId,
            generation,
            _clock.Now.ToUniversalTime(),
            new
            {
                rejectedMessageId = rejected.MessageId,
                rejectedMessageType = rejected.MessageType,
                problem = new
                {
                    reasonCode,
                    fieldPath = (string?)null,
                    displayMessage = (string?)null
                },
                expectedProtocolVersion = WireToGateRelease.ProtocolVersion,
                expectedProfileId = WireToGateRelease.ProfileId,
                expectedProtocolReleaseManifestSha256 = WireToGateRelease.ManifestSha256
            });
        await SendEnvelopeAsync(problem, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsJourneySnapshot(string messageType) => messageType switch
    {
        "VehicleBusinessStateSnapshot" => true,
        "CurrentStopWorklistSnapshot" => true,
        "UpcomingStopPlanSnapshot" => true,
        _ => false
    };

    private static bool TryCreateServerCommand(
        WireToGateEnvelope envelope,
        out WireToGateServerCommand? command)
    {
        command = null;
        switch (envelope.MessageType)
        {
            case "SublotEntryRequested":
                {
                    if (envelope.CorrelationId is not null)
                    {
                        throw new InvalidDataException("CORRELATION_INVALID");
                    }

                    SublotEntryRequestedPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<SublotEntryRequestedPayload>(envelope);
                    RequireUuid(payload.DemandId, nameof(payload.DemandId));
                    RequireUuid(payload.OperationSessionId, nameof(payload.OperationSessionId));
                    if (string.IsNullOrWhiteSpace(payload.StationId)
                        || payload.WorklistRevision < 0
                        || string.IsNullOrWhiteSpace(payload.ExpectedSublot)
                        || payload.EntryMethods is null
                        || payload.EntryMethods.SequenceEqual(["SCANNER", "KEYBOARD"]) is false
                        || payload.ExpiresOnRevisionChange is false)
                    {
                        throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
                    }

                    command = new WireToGateSublotEntryRequest(
                        envelope.MessageId,
                        envelope.SessionGeneration!.Value,
                        envelope.SentAt,
                        payload.DemandId,
                        payload.OperationSessionId,
                        payload.StationId,
                        payload.WorklistRevision,
                        payload.ExpectedSublot,
                        payload.EntryMethods,
                        payload.ExpiresOnRevisionChange);
                    return true;
                }
            case "SlotOperationCommand":
                {
                    SlotOperationCommandPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<SlotOperationCommandPayload>(envelope);
                    RequireUuid(payload.DemandId, nameof(payload.DemandId));
                    RequireUuid(payload.OperationSessionId, nameof(payload.OperationSessionId));
                    RequireUuid(payload.SlotOperationAttemptId, nameof(payload.SlotOperationAttemptId));
                    RequireSha256(payload.CommandContentSha256, nameof(payload.CommandContentSha256));
                    if (payload.OperationType is not ("LOAD" or "UNLOAD")
                        || payload.ExpectedFinalPhysicalState is not ("OCCUPIED" or "EMPTY")
                        || payload.Slots is null
                        || payload.ExpectedBasketCount != payload.Slots.Count)
                    {
                        throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
                    }

                    ValidateSortedSlots(payload.Slots);
                    bool isLoad = payload.OperationType == "LOAD";
                    if ((isLoad && envelope.CorrelationId is null)
                        || (!isLoad && envelope.CorrelationId is not null)
                        || (isLoad && payload.ExpectedFinalPhysicalState != "OCCUPIED")
                        || (!isLoad && payload.ExpectedFinalPhysicalState != "EMPTY"))
                    {
                        throw new InvalidDataException("CORRELATION_INVALID");
                    }

                    command = new WireToGateSlotOperationCommand(
                        envelope.MessageId,
                        envelope.CorrelationId,
                        envelope.SessionGeneration!.Value,
                        envelope.SentAt,
                        payload.DemandId,
                        payload.OperationSessionId,
                        payload.SlotOperationAttemptId,
                        isLoad ? OperationType.Load : OperationType.Unload,
                        payload.Slots,
                        payload.ExpectedBasketCount,
                        isLoad,
                        payload.CommandContentSha256);
                    return true;
                }
            case "SlotOperationResumeCommand":
                {
                    if (envelope.CorrelationId is not null)
                    {
                        throw new InvalidDataException("CORRELATION_INVALID");
                    }

                    SlotOperationResumeCommandPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<SlotOperationResumeCommandPayload>(envelope);
                    RequireUuid(payload.ExceptionRecoverySessionId, nameof(payload.ExceptionRecoverySessionId));
                    RequireUuid(payload.RecoveryActionId, nameof(payload.RecoveryActionId));
                    RequireUuid(payload.DemandId, nameof(payload.DemandId));
                    RequireUuid(payload.SlotOperationAttemptId, nameof(payload.SlotOperationAttemptId));
                    RequireSha256(payload.CommandContentSha256, nameof(payload.CommandContentSha256));
                    ValidateSortedSlots(payload.Slots);
                    WireToGateRecoveryCheckpoint checkpoint = payload.ProvenRecoveryCheckpoint switch
                    {
                        "PREPARED" => WireToGateRecoveryCheckpoint.Prepared,
                        "ACTIVE_UNLOCK_SET" => WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                        "SAFE_FINISH_REACHED" => WireToGateRecoveryCheckpoint.SafeFinishReached,
                        _ => throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID")
                    };

                    command = new WireToGateSlotOperationResumeCommand(
                        envelope.MessageId,
                        envelope.SessionGeneration!.Value,
                        envelope.SentAt,
                        payload.ExceptionRecoverySessionId,
                        payload.RecoveryActionId,
                        payload.DemandId,
                        payload.SlotOperationAttemptId,
                        checkpoint,
                        payload.Slots,
                        payload.CommandContentSha256);
                    return true;
                }
            case "PreDepartureSafetyCheck":
                {
                    if (envelope.CorrelationId is not null)
                    {
                        throw new InvalidDataException("CORRELATION_INVALID");
                    }

                    PreDepartureSafetyCheckPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<PreDepartureSafetyCheckPayload>(envelope);
                    RequireUuid(payload.PreDepartureSafetyCheckId, nameof(payload.PreDepartureSafetyCheckId));
                    RequireUuid(payload.DemandId, nameof(payload.DemandId));
                    RequireUuid(payload.MovementLegId, nameof(payload.MovementLegId));
                    if (payload.ExpectedSafetyStateVersion < 0
                        || string.IsNullOrWhiteSpace(payload.TargetStationId))
                    {
                        throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
                    }

                    command = new WireToGatePreDepartureSafetyCheck(
                        envelope.MessageId,
                        envelope.SessionGeneration!.Value,
                        envelope.SentAt,
                        payload.PreDepartureSafetyCheckId,
                        payload.DemandId,
                        payload.MovementLegId,
                        payload.ExpectedSafetyStateVersion,
                        payload.TargetStationId);
                    return true;
                }
            case "SublotRejected":
                {
                    RequireCorrelatedProblem(envelope, typeof(SublotRejectedPayload));
                    SublotRejectedPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<SublotRejectedPayload>(envelope);
                    RequireUuid(payload.DemandId, nameof(payload.DemandId));
                    RequireUuid(payload.OperationSessionId, nameof(payload.OperationSessionId));
                    ValidateProblem(payload.Problem);
                    if (payload.CurrentWorklistRevision < 0)
                    {
                        throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
                    }

                    command = new WireToGateRecoveryCommand(
                        envelope.MessageType,
                        envelope.MessageId,
                        envelope.CorrelationId,
                        envelope.SessionGeneration!.Value,
                        envelope.SentAt,
                        envelope.Payload.GetRawText());
                    return true;
                }
            case "SlotOperationCommandRejected":
                {
                    RequireCorrelatedProblem(envelope, typeof(SlotOperationCommandRejectedPayload));
                    SlotOperationCommandRejectedPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<SlotOperationCommandRejectedPayload>(envelope);
                    RequireUuid(payload.SlotOperationAttemptId, nameof(payload.SlotOperationAttemptId));
                    ValidateProblem(payload.Problem);
                    if (payload.ObservedCapabilityVersion < 0
                        || payload.ConflictingContentSha256 is not null
                            && !IsSha256(payload.ConflictingContentSha256))
                    {
                        throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
                    }

                    command = new WireToGateRecoveryCommand(
                        envelope.MessageType,
                        envelope.MessageId,
                        envelope.CorrelationId,
                        envelope.SessionGeneration!.Value,
                        envelope.SentAt,
                        envelope.Payload.GetRawText());
                    return true;
                }
            case "CapabilitySnapshotRequested":
            case "SafetyStateChanged":
            case "SafetyStateSnapshotRequested":
            case "ExceptionRecoverySessionRequested":
            case "ExceptionRecoverySessionOpened":
            case "ExceptionRecoverySessionRejected":
            case "ExceptionRecoverySessionSnapshot":
            case "FaultCargoRecoveryCommand":
            case "ForcedMechanicalRecoveryCommand":
            case "ManualChargingReturnToServiceRequested":
            case "HardwareRecoveryRecordSubmitted":
            case "RecoveryActionSubmitted":
            case "LoadCorrectionRequested":
            case "LoadCompensationRequested":
            case "LoadCancellationStartRequested":
                command = new WireToGateRecoveryCommand(
                    envelope.MessageType,
                    envelope.MessageId,
                    envelope.CorrelationId,
                    envelope.SessionGeneration!.Value,
                    envelope.SentAt,
                    envelope.Payload.GetRawText());
                return true;
            default:
                return false;
        }
    }

    private static void RequireCorrelatedProblem(
        WireToGateEnvelope envelope,
        Type payloadType)
    {
        _ = payloadType;
        if (envelope.CorrelationId is null || !Guid.TryParseExact(envelope.CorrelationId, "D", out _))
        {
            throw new InvalidDataException("CORRELATION_INVALID");
        }
    }

    private static void ValidateProblem(WireToGateProblemPayload problem)
    {
        if (problem is null
            || !IsProtocolErrorCode(problem.ReasonCode)
            || problem.FieldPath is not null && string.IsNullOrWhiteSpace(problem.FieldPath)
            || problem.DisplayMessage is not null && string.IsNullOrWhiteSpace(problem.DisplayMessage))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }
    }

    private static void ValidateSortedSlots(IReadOnlyList<int>? slots)
    {
        if (slots is null
            || slots.Count is < 1 or > 8
            || slots.Any(slot => slot is < 1 or > 8)
            || slots.Distinct().Count() != slots.Count
            || !slots.SequenceEqual(slots.Order()))
        {
            throw new InvalidDataException("SLOT_SET_INVALID");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void RequireSha256(string value, string name)
    {
        if (!IsSha256(value))
        {
            throw new InvalidDataException($"{name}必须是64位SHA-256十六进制字符串。");
        }
    }

    private static SQCD.Agv.Core.WireToGateWorklistItem ToCoreWorklistItem(
        SQCD.Agv.Contracts.WireToGateWorklistItem item) =>
        new SQCD.Agv.Core.WireToGateWorklistItem(
            item.DemandId,
            item.TransportDemandKey,
            item.Sublot,
            item.WorkType,
            item.StopRole,
            item.ExpectedBasketCount);

    private static SQCD.Agv.Core.WireToGateMovementLeg ToCoreMovementLeg(
        SQCD.Agv.Contracts.WireToGateMovementLeg leg) =>
        new SQCD.Agv.Core.WireToGateMovementLeg(
            leg.MovementLegId,
            leg.LegType,
            leg.Sequence,
            leg.StationId,
            leg.MapId,
            leg.State);

    private static void ValidateVehicleBusinessState(VehicleBusinessStateSnapshotPayload payload)
    {
        if (payload.VehicleBusinessStateRevision < 0
            || payload.Readiness is not ("READY" or "RECOVERY_REQUIRED")
            || payload.BatteryState is not ("SUFFICIENT" or "LOW" or "UNKNOWN")
            || payload.BlockingFacts is null
            || payload.BlockingFacts.Distinct().Count() != payload.BlockingFacts.Count
            || payload.BlockingFacts.Any(fact =>
                fact is null
                || !IsProtocolErrorCode(fact.ReasonCode)
                || string.IsNullOrWhiteSpace(fact.SubjectType)
                || fact.SubjectId is not null && string.IsNullOrWhiteSpace(fact.SubjectId)))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }
    }

    private static bool IsProtocolErrorCode(string value) => value is
        "PROTOCOL_ENVELOPE_INVALID"
        or "PROTOCOL_SCHEMA_INVALID"
        or "UNSUPPORTED_PROTOCOL_VERSION"
        or "PROTOCOL_RELEASE_IDENTITY_MISMATCH"
        or "UNKNOWN_MESSAGE_TYPE"
        or "PROFILE_MESSAGE_NOT_ALLOWED"
        or "MESSAGE_ID_CONTENT_CONFLICT"
        or "CORRELATION_INVALID"
        or "CONTENT_HASH_MISMATCH"
        or "VEHICLE_CREDENTIAL_INVALID"
        or "AGV_ID_MISMATCH"
        or "STALE_SESSION_GENERATION"
        or "DUPLICATE_ACTIVE_SESSION"
        or "HANDSHAKE_SEQUENCE_INVALID"
        or "CAPABILITY_VERSION_GAP"
        or "SAFETY_STATE_VERSION_GAP"
        or "SNAPSHOT_REVISION_REGRESSION"
        or "SNAPSHOT_REVISION_CONTENT_CONFLICT"
        or "SESSION_RECOVERY_REQUIRED"
        or "BUSINESS_ID_CONTENT_CONFLICT"
        or "VEHICLE_NOT_READY"
        or "DEMAND_NOT_CURRENT"
        or "OPERATION_SESSION_MISMATCH"
        or "STATION_MISMATCH"
        or "WORKLIST_REVISION_STALE"
        or "SUBLOT_MISMATCH"
        or "SLOT_SET_INVALID"
        or "EXPECTED_BASKET_COUNT_MISMATCH"
        or "SLOT_OPERATION_CONFLICT"
        or "ACTION_NOT_ALLOWED_IN_STATE"
        or "MANUAL_CHARGING_HOLD_ACTIVE"
        or "CAPABILITY_UNKNOWN"
        or "SLOT_INOPERABLE"
        or "SLOT_STATE_UNKNOWN"
        or "LOCK_NOT_CLOSED"
        or "UNLOCK_OUTPUT_NOT_RESET"
        or "DEPARTURE_UNSAFE"
        or "PREDEPARTURE_CHECK_EXPIRED"
        or "RECOVERY_SESSION_NOT_OPEN"
        or "RECOVERY_SCOPE_MISMATCH"
        or "RECOVERY_CHECKPOINT_NOT_UNIQUE"
        or "RECOVERY_AUTHENTICATION_FAILED"
        or "FORCED_RECOVERY_GENERATION_STALE";

    private static void ValidateCurrentStopWorklist(CurrentStopWorklistSnapshotPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.StationId)
            || payload.WorklistRevision < 0
            || payload.OperationSessionId is not null && !IsUuid(payload.OperationSessionId)
            || payload.Items is null
            || payload.Items.Count > 1
            || payload.Items.Any(item =>
                item is null
                || !IsUuid(item.DemandId)
                || string.IsNullOrWhiteSpace(item.TransportDemandKey)
                || string.IsNullOrWhiteSpace(item.Sublot)
                || item.WorkType != "WIRE_TO_GATE"
                || item.StopRole is not ("PICKUP" or "GATE")
                || item.ExpectedBasketCount is < 1 or > 8))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }
    }

    private static void ValidateUpcomingStopPlan(UpcomingStopPlanSnapshotPayload payload)
    {
        if (payload.PlanRevision < 0
            || payload.DemandId is not null && !IsUuid(payload.DemandId)
            || payload.Legs is null
            || payload.Legs.Count > 2
            || payload.Legs.Any(leg => leg is null)
            || payload.Legs.Select(leg => leg.Sequence).Distinct().Count() != payload.Legs.Count
            || payload.Legs.OrderBy(leg => leg.Sequence).Select((leg, index) => leg.Sequence == index + 1).Any(valid => !valid)
            || payload.Legs.Any(leg =>
                leg is null
                || !IsUuid(leg.MovementLegId)
                || leg.LegType is not ("TO_PICKUP" or "TO_GATE")
                || string.IsNullOrWhiteSpace(leg.StationId)
                || string.IsNullOrWhiteSpace(leg.MapId)
                || leg.State is not ("PLANNED" or "ACTIVE" or "ARRIVED" or "COMPLETED" or "BLOCKED")))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }
    }

    private static bool IsUuid(string? value) =>
        value is not null && Guid.TryParseExact(value, "D", out _);

    private static ProtocolSlotState[] CreateSlotStates(IoSnapshot snapshot)
    {
        if (snapshot.Lockers.Count != 8)
        {
            throw new InvalidDataException("CapabilitySnapshot必须包含8个仓位。");
        }

        return snapshot.Lockers
            .OrderBy(locker => locker.PhysicalNumber)
            .Select(locker =>
            {
                bool known = snapshot.IsConnected && locker.IsKnown;
                return new ProtocolSlotState(
                    locker.PhysicalNumber,
                    known ? "OPERABLE" : "UNKNOWN",
                    "ENABLED",
                    known ? locker.HasCargo ? "OCCUPIED" : "EMPTY" : "UNKNOWN",
                    known ? locker.IsLocked ? "LOCKED" : "UNLOCKED" : "UNKNOWN",
                    known ? locker.UnlockOutputRaw is true ? "ACTIVE" : "RESET" : "UNKNOWN",
                    known ? [] : ["SLOT_STATE_UNKNOWN"]);
            })
            .ToArray();
    }

    private ProtocolSafetySummary CreateSafetySummary(
        IoSnapshot snapshot,
        IReadOnlyList<ProtocolSlotState> slots)
    {
        bool vehicleStopped = _vehicleStoppedProvider();
        bool unknownPresent = !snapshot.IsConnected || slots.Any(slot =>
            slot.PhysicalState == "UNKNOWN"
            || slot.LockState == "UNKNOWN"
            || slot.UnlockOutputState == "UNKNOWN");
        bool allLocked = slots.All(slot => slot.LockState == "LOCKED");
        bool allOutputsReset = slots.All(slot => slot.UnlockOutputState == "RESET");
        List<string> reasonCodes = [];
        if (unknownPresent)
        {
            reasonCodes.Add("SLOT_STATE_UNKNOWN");
        }
        if (!allLocked)
        {
            reasonCodes.Add("LOCK_NOT_CLOSED");
        }
        if (!allOutputsReset)
        {
            reasonCodes.Add("UNLOCK_OUTPUT_NOT_RESET");
        }
        if (!vehicleStopped)
        {
            reasonCodes.Add("ACTION_NOT_ALLOWED_IN_STATE");
        }

        bool departureSafe = vehicleStopped && !unknownPresent && allLocked && allOutputsReset;
        return new ProtocolSafetySummary(
            departureSafe,
            vehicleStopped,
            allLocked,
            allOutputsReset,
            unknownPresent,
            reasonCodes.Distinct(StringComparer.Ordinal).ToArray());
    }

    private async Task OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await CloseConnectionAsync().ConfigureAwait(false);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConnectTimeout);

        TcpClient client = new();
        try
        {
            await client.ConnectAsync(_options.Host, _options.Port, timeout.Token).ConfigureAwait(false);
            client.NoDelay = true;
            Stream stream = await CreateTransportStreamAsync(client, timeout.Token).ConfigureAwait(false);
            _client = client;
            _stream = stream;
            _reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4_096, true);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), 4_096, true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<Stream> CreateTransportStreamAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        NetworkStream network = client.GetStream();
        if (!_options.UseTls)
        {
            return network;
        }

        SslStream ssl = new(network, false, ValidateServerCertificate);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = _options.Host,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                | System.Security.Authentication.SslProtocols.Tls13
        }, cancellationToken).ConfigureAwait(false);
        return ssl;
    }

    private bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        _ = sender;
        _ = chain;
        if (certificate is null || string.IsNullOrWhiteSpace(_options.ServerCertificateSha256))
        {
            return false;
        }

        string actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
        string expected = _options.ServerCertificateSha256.Replace(":", string.Empty, StringComparison.Ordinal);
        bool trusted = sslPolicyErrors is SslPolicyErrors.None
            or SslPolicyErrors.RemoteCertificateNameMismatch;
        return trusted && actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendEnvelopeAsync(
        WireToGateEnvelope envelope,
        CancellationToken cancellationToken) =>
        await SendLineAsync(WireToGateProtocolSerializer.SerializeLine(envelope), cancellationToken)
            .ConfigureAwait(false);

    private async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        StreamWriter writer = _writer ?? throw new IOException("WIRE_TO_GATE连接不可用。");
        string normalized = line.EndsWith('\n') ? line[..^1] : line;
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(normalized.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<WireToGateEnvelope> ReadEnvelopeAsync(
        long? expectedGeneration,
        CancellationToken cancellationToken)
    {
        StreamReader reader = _reader ?? throw new IOException("WIRE_TO_GATE连接不可用。");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.MessageTimeout);
        string? line;
        try
        {
            line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待WIRE_TO_GATE消息超时。", exception);
        }

        if (line is null)
        {
            throw new EndOfStreamException("ControlServer在会话恢复期间关闭了连接。");
        }

        return WireToGateProtocolSerializer.DeserializeAndValidate(
            line,
            _options.AgvId,
            expectedGeneration);
    }

    private async ValueTask CloseConnectionAsync()
    {
        _receiveStopping?.Cancel();
        _receiveStopping = null;
        _receiveFailure = null;
        foreach (KeyValuePair<string, TaskCompletionSource<WireToGateEnvelope>> waiter in _responseWaiters)
        {
            waiter.Value.TrySetException(new IOException("WIRE_TO_GATE连接已关闭。"));
        }
        _responseWaiters.Clear();

        ResetJourneyProjection();

        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }
        _reader?.Dispose();
        _reader = null;
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
        _client?.Dispose();
        _client = null;
    }

    private void Publish(
        bool connected,
        long? generation,
        WireToGateSessionReadiness readiness,
        IReadOnlyList<string> reasonCodes)
    {
        WireToGateSessionSnapshot snapshot = new(
            connected,
            generation,
            readiness,
            reasonCodes,
            Volatile.Read(ref _acceptedCapabilityVersion),
            Volatile.Read(ref _acceptedSafetyStateVersion),
            _clock.Now);
        Volatile.Write(ref _current, snapshot);
        StateChanged?.Invoke(this, new ValueChangedEventArgs<WireToGateSessionSnapshot>(snapshot));
    }

    private void ApplySessionReadiness(
        WireToGateEnvelope envelope,
        bool requireExactConfiguredBaseline)
    {
        WireToGateProtocolSerializer.RequireMessage(envelope, "SessionReadiness");
        if (envelope.CorrelationId is not null)
        {
            throw new InvalidDataException("CORRELATION_INVALID");
        }

        SessionReadinessPayload readiness = WireToGateProtocolSerializer
            .DeserializePayload<SessionReadinessPayload>(envelope);
        long currentSafetyVersion = Volatile.Read(ref _acceptedSafetyStateVersion);
        bool invalidVersion = readiness.AcceptedCapabilityVersion != _options.CapabilityVersion
            || readiness.AcceptedSafetyStateVersion < currentSafetyVersion
            || requireExactConfiguredBaseline
                && readiness.AcceptedSafetyStateVersion != currentSafetyVersion;
        if (invalidVersion)
        {
            throw new InvalidDataException("HANDSHAKE_SEQUENCE_INVALID");
        }

        WireToGateSessionReadiness mappedReadiness = readiness.Readiness switch
        {
            "READY" => WireToGateSessionReadiness.Ready,
            "RECOVERY_REQUIRED" => WireToGateSessionReadiness.RecoveryRequired,
            _ => throw new InvalidDataException("SessionReadiness.readiness无效。")
        };
        AdvanceSafetyStateVersion(readiness.AcceptedSafetyStateVersion);
        Publish(true, envelope.SessionGeneration, mappedReadiness, readiness.ReasonCodes);
    }

    private void AdvanceSafetyStateVersion(long safetyStateVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(safetyStateVersion);
        long current;
        do
        {
            current = Volatile.Read(ref _acceptedSafetyStateVersion);
            if (safetyStateVersion <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(
            ref _acceptedSafetyStateVersion,
            safetyStateVersion,
            current) != current);
    }

    private static void ThrowIfProtocolProblem(WireToGateEnvelope envelope)
    {
        if (!string.Equals(envelope.MessageType, "ProtocolProblem", StringComparison.Ordinal))
        {
            return;
        }

        ProtocolProblemPayload problem = WireToGateProtocolSerializer
            .DeserializePayload<ProtocolProblemPayload>(envelope);
        throw new InvalidDataException(problem.Problem.ReasonCode);
    }

    private static string ToProtocolCheckpoint(WireToGateRecoveryCheckpoint checkpoint) => checkpoint switch
    {
        WireToGateRecoveryCheckpoint.None => "NONE",
        WireToGateRecoveryCheckpoint.Prepared => "PREPARED",
        WireToGateRecoveryCheckpoint.ActiveUnlockSet => "ACTIVE_UNLOCK_SET",
        WireToGateRecoveryCheckpoint.SafeFinishReached => "SAFE_FINISH_REACHED",
        WireToGateRecoveryCheckpoint.ResultRecorded => "RESULT_RECORDED",
        _ => throw new ArgumentOutOfRangeException(nameof(checkpoint))
    };

    private static void ValidateOptions(WireToGateSessionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host)
            || options.Port is < 1 or > 65_535
            || string.IsNullOrWhiteSpace(options.AgvId)
            || !Guid.TryParseExact(options.OnboardInstanceId, "D", out _)
            || options.OnboardBuildCommit.Length != 40
            || options.OnboardBuildCommit.Any(character => !Uri.IsHexDigit(character))
            || string.IsNullOrWhiteSpace(options.CredentialEnvironmentVariable)
            || options.ConnectTimeout <= TimeSpan.Zero
            || options.MessageTimeout <= TimeSpan.Zero
            || options.CapabilityVersion < 0
            || options.SafetyStateVersion < 0
            || string.IsNullOrWhiteSpace(options.SlotModelVersion)
            || string.IsNullOrWhiteSpace(options.ActiveSlotConfigurationVersion))
        {
            throw new InvalidDataException("WIRE_TO_GATE会话配置无效。");
        }

        if (!options.UseTls && !IsLoopback(options.Host))
        {
            throw new InvalidDataException("非TLS WIRE_TO_GATE连接只允许loopback地址。");
        }

        if (options.UseTls
            && (options.ServerCertificateSha256 is null
                || options.ServerCertificateSha256.Replace(":", string.Empty, StringComparison.Ordinal).Length != 64))
        {
            throw new InvalidDataException("TLS连接必须配置服务器证书SHA-256指纹。");
        }
    }

    private static bool IsLoopback(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }

    private static void RequireUuid(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out _))
        {
            throw new InvalidDataException($"{name}必须是标准UUID。");
        }
    }

    private static string StableUuid(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        // Mark the deterministic value as a UUIDv5-shaped identifier while keeping
        // the exact bytes stable across retries and process restarts.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString("D");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record SessionHelloPayload(
        string OnboardInstanceId,
        string OnboardBuildCommit,
        int SupportedProtocolVersion,
        string ProfileId,
        ProtocolReleaseIdentity ProtocolReleaseIdentity,
        string CredentialProof);

    private sealed record SessionAcceptedPayload(
        long SessionGeneration,
        string ServerInstanceId,
        string ServerBuildCommit,
        ProtocolReleaseIdentity AcceptedProtocolReleaseIdentity,
        DateTimeOffset AcceptedAt);

    private sealed record ProtocolProblem(string ReasonCode, string? FieldPath, string? DisplayMessage);

    private sealed record ProtocolProblemPayload(
        string RejectedMessageId,
        string? RejectedMessageType,
        ProtocolProblem Problem,
        int ExpectedProtocolVersion,
        string ExpectedProfileId,
        string ExpectedProtocolReleaseManifestSha256);

    private sealed record SessionRejectedPayload(
        ProtocolProblem Problem,
        int ExpectedProtocolVersion,
        ProtocolReleaseIdentity? ExpectedProtocolReleaseIdentity);

    private sealed record ProtocolSlotState(
        int SlotNo,
        string Operability,
        string AdministrativeAvailability,
        string PhysicalState,
        string LockState,
        string UnlockOutputState,
        IReadOnlyList<string> ReasonCodes);

    private sealed record CapabilitySnapshotPayload(
        long CapabilityVersion,
        DateTimeOffset ObservedAt,
        string SlotModelVersion,
        string ActiveSlotConfigurationVersion,
        IReadOnlyList<ProtocolSlotState> SlotStates,
        bool SupportsBatchUnlock,
        int OnboardJournalFormatVersion);

    private sealed record ProtocolSafetySummary(
        bool DepartureSafe,
        bool VehicleStopped,
        bool AllTargetSlotsLocked,
        bool AllUnlockOutputsReset,
        bool UnknownPresent,
        IReadOnlyList<string> ReasonCodes);

    private sealed record SafetyStateSnapshotPayload(
        long SafetyStateVersion,
        DateTimeOffset ObservedAt,
        ProtocolSafetySummary Safety,
        IReadOnlyList<ProtocolSlotState> SlotStates);

    private sealed record SnapshotAppliedAckPayload(
        string SnapshotMessageId,
        string SnapshotKind,
        long AppliedRevision,
        string AppliedContentSha256);

    private sealed record PendingResultPayload(
        string MessageType,
        string MessageId,
        string BusinessId,
        string ContentSha256);

    private sealed record RecoveryStateReportPayload(
        string ReportId,
        DateTimeOffset ObservedAt,
        string? UnsettledSlotOperationAttemptId,
        string ProvenRecoveryCheckpoint,
        IReadOnlyList<int> ActiveUnlockSlots,
        long ForcedRecoveryGeneration,
        IReadOnlyList<PendingResultPayload> PendingResults,
        string JournalContentSha256);

    private sealed record DurableAckPayload(
        string AcceptedMessageId,
        string AcceptedMessageType,
        string AcceptedContentSha256,
        DateTimeOffset DurablyAcceptedAt);

    private sealed record SessionReadinessPayload(
        string Readiness,
        DateTimeOffset DecidedAt,
        IReadOnlyList<string> ReasonCodes,
        long AcceptedCapabilityVersion,
        long AcceptedSafetyStateVersion,
        long VehicleBusinessStateRevision);

    private sealed record HeartbeatPayload(long CapabilityVersion, long SafetyStateVersion);

    private sealed record HeartbeatAckPayload(string ReceivedHeartbeatMessageId, DateTimeOffset ServerTime);
}
