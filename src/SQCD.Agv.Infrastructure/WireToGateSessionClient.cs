using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    TimeSpan ConnectTimeout,
    TimeSpan MessageTimeout,
    long CapabilityVersion,
    long SafetyStateVersion,
    string SlotModelVersion,
    string ActiveSlotConfigurationVersion,
    bool SupportsBatchUnlock);

/// <summary>
/// 服务端 ack 过的一份告警快照：哪一代会话、哪些告警。
/// </summary>
/// <remarks>
/// 记内容不记序号：序号每抓一次就加一，内容才是「服务端手上是不是已经是当下的事实」要比的东西。
/// </remarks>
public sealed record OnboardAlarmPublication(long SessionGeneration, IReadOnlyList<AlarmEntry> Alarms);

public sealed class WireToGateSessionClient : IAsyncDisposable
{
    private readonly WireToGateSessionOptions _options;
    private readonly IIoModuleClient _ioModule;
    private readonly IWireToGateJournal _journal;
    private readonly IClock _clock;
    private readonly IVehicleSafetySignalProvider _vehicleSafetySignalProvider;
    private readonly OnboardAlarmBoard _alarmBoard;
    private readonly SlotConfigurationActivationCoordinator _activationCoordinator;
    private readonly TimeSpan _ioSnapshotMaxAge;
    private readonly TimeSpan _vehicleSafetyMaxAge;
    private readonly TimeSpan _vehicleSafetyClockSkewTolerance;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _journeyGate = new();
    private readonly Dictionary<string, (long Revision, string ContentSha256)> _journeyRevisions = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WireToGateEnvelope>> _responseWaiters = [];
    private readonly ConcurrentDictionary<string, string> _completedManualChargingResultFingerprints = [];
    private readonly SemaphoreSlim _alarmPublishGate = new(1, 1);
    private long _receiveLoopGeneration;
    private OnboardAlarmPublication? _acknowledgedAlarms;
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

    // The generation of the last session this client had, for a message put on file while none is up: the envelope
    // needs one to be valid, and the handshake rebinds it to its own before replaying it (onboard-hmi#127).
    private long _lastSessionGeneration;
    private bool _disposed;

    public WireToGateSessionClient(
        WireToGateSessionOptions options,
        IIoModuleClient ioModule,
        IWireToGateJournal journal,
        IClock clock,
        IVehicleSafetySignalProvider vehicleSafetySignalProvider,
        OnboardAlarmBoard alarmBoard,
        SlotConfigurationActivationCoordinator activationCoordinator,
        TimeSpan ioSnapshotMaxAge,
        TimeSpan vehicleSafetyMaxAge,
        TimeSpan vehicleSafetyClockSkewTolerance)
    {
        _options = options;
        _ioModule = ioModule;
        _journal = journal;
        _clock = clock;
        _vehicleSafetySignalProvider = vehicleSafetySignalProvider
            ?? throw new ArgumentNullException(nameof(vehicleSafetySignalProvider));
        _alarmBoard = alarmBoard ?? throw new ArgumentNullException(nameof(alarmBoard));
        _activationCoordinator = activationCoordinator
            ?? throw new ArgumentNullException(nameof(activationCoordinator));
        _ioSnapshotMaxAge = ioSnapshotMaxAge;
        _vehicleSafetyMaxAge = vehicleSafetyMaxAge;
        _vehicleSafetyClockSkewTolerance = vehicleSafetyClockSkewTolerance;
        if (_ioSnapshotMaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ioSnapshotMaxAge));
        }
        if (_vehicleSafetyMaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(vehicleSafetyMaxAge));
        }
        if (_vehicleSafetyClockSkewTolerance < TimeSpan.Zero
            || _vehicleSafetyClockSkewTolerance >= _vehicleSafetyMaxAge)
        {
            throw new ArgumentOutOfRangeException(nameof(vehicleSafetyClockSkewTolerance));
        }
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

    /// <summary>
    /// 服务端最近 ack 的那一份告警快照。握手里的与会话中途的都算；还没 ack 过任何一份时为 <c>null</c>。
    /// </summary>
    public OnboardAlarmPublication? LastAcknowledgedAlarmSnapshot => Volatile.Read(ref _acknowledgedAlarms);

    public WireToGateJourneySnapshot CurrentJourney => Volatile.Read(ref _journey);

    public event EventHandler<ValueChangedEventArgs<WireToGateSessionSnapshot>>? StateChanged;

    public event EventHandler<ValueChangedEventArgs<WireToGateJourneySnapshot>>? JourneyChanged;

    /// <summary>
    /// Formal WIRE_TO_GATE business messages received after session recovery.
    /// Handlers must treat the command as untrusted input and perform their own
    /// physical-state checks before causing side effects.
    /// </summary>
    public event EventHandler<ValueChangedEventArgs<WireToGateServerCommand>>? ServerCommandReceived;

    /// <summary>
    /// Sends one operator entry. Every call is a new entry and gets a messageId of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The messageId is minted per entry, not derived from the sublot.</b> It used to be
    /// <c>StableUuid(operationSessionId, worklistRevision, sublot)</c>, and the outbox key with it,
    /// so scanning the same sublot again after a rejection landed on the first entry's journal
    /// record: a different operator or <c>verifiedAt</c> threw <c>BUSINESS_ID_CONTENT_CONFLICT</c>
    /// here, and an identical one returned the old id without sending anything. Protocol 2.0.0 gives
    /// <c>SublotSubmitted</c> <c>businessDedupKeys: []</c> so that such a retry is a new submission
    /// rather than a conflict.
    /// </para>
    /// <para>
    /// <b>A resend of the same entry still keeps its id.</b> The durable record written below carries
    /// the messageId, and a reconnect replays that record, so an entry whose acknowledgement was lost
    /// goes out again under the id it first had. The outbox key is the messageId for the same reason:
    /// one entry, one record.
    /// </para>
    /// </remarks>
    public Task<string> SendSublotSubmittedAsync(
        string operationSessionId,
        string stationId,
        long worklistRevision,
        string sublot,
        string entryMethod,
        string operatorId,
        string verificationMethod,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default)
    {
        string messageId = Guid.NewGuid().ToString("D");
        return SendDurableAsync(
            "SublotSubmitted",
            $"sublot-submitted:{messageId}",
            messageId,
            null,
            new SublotSubmittedPayload(
                operationSessionId,
                stationId,
                worklistRevision,
                sublot,
                entryMethod,
                new WireToGateOperatorContextPayload(operatorId, verificationMethod, verifiedAt)),
            cancellationToken);
    }

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

    public Task<string> SendRecoveryOperationProgressAsync(
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
        return SendDurableCoreAsync(
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
            allowRecoveryRequired: true,
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

    public Task<string> SendRecoveryOperationResultAsync(
        string deduplicationKey,
        string messageId,
        WireToGateOperationResultPayload payload,
        CancellationToken cancellationToken = default) =>
        SendDurableCoreAsync(
            "OperationResult",
            deduplicationKey,
            messageId,
            null,
            payload,
            allowRecoveryRequired: true,
            cancellationToken);

    /// <summary>
    /// Sends the <c>OperationResult</c> already on file under <paramref name="deduplicationKey"/> once more, exactly
    /// as stored -- same messageId, correlation and payload -- and waits for its <c>DurableAck</c>. For a result whose
    /// ack was lost while the link stayed up: nothing else would send it before the next handshake
    /// (onboard-hmi#127). Allowed while RECOVERY_REQUIRED, like every other way this result is sent.
    /// </summary>
    public async Task<string> ResendOperationResultAsync(
        string deduplicationKey,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        WireToGateDurableMessage stored = await _journal
            .ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("DURABLE_OUTBOX_ROW_MISSING");
        WireToGateEnvelope envelope = WireToGateProtocolSerializer.DeserializeAndValidate(
            stored.WireLine.TrimEnd('\r', '\n'),
            _options.AgvId);
        if (!string.Equals(stored.MessageType, "OperationResult", StringComparison.Ordinal)
            || !string.Equals(envelope.MessageType, "OperationResult", StringComparison.Ordinal))
        {
            throw new InvalidDataException("DURABLE_OUTBOX_CONTENT_MISMATCH");
        }

        return await SendDurableCoreAsync(
            "OperationResult",
            deduplicationKey,
            stored.MessageId,
            envelope.CorrelationId,
            envelope.Payload,
            allowRecoveryRequired: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a <c>SlotOperationResumeCommand</c> the vehicle stopped before any door IO. A resume
    /// only ever arrives while the session is RECOVERY_REQUIRED, so the answer has to be sendable
    /// there too (8005-agv-onboard-hmi#119).
    /// </summary>
    public Task<string> SendSlotOperationResumeRejectedAsync(
        string deduplicationKey,
        string messageId,
        string resumeCommandMessageId,
        SlotOperationCommandRejectedPayload payload,
        CancellationToken cancellationToken = default) =>
        SendDurableCoreAsync(
            "SlotOperationCommandRejected",
            deduplicationKey,
            messageId,
            resumeCommandMessageId,
            payload,
            allowRecoveryRequired: true,
            cancellationToken);

    /// <summary>
    /// Refuses an original <c>SlotOperationCommand</c>. The vehicle refuses one exactly when its session is not
    /// Ready, so the answer has to be sendable while RECOVERY_REQUIRED, as the resume refusal above is
    /// (onboard-hmi#127). The server sends no new slot operation while not ready (ADR-cross-0028); this is the
    /// readiness flip race.
    /// </summary>
    public Task<string> SendSlotOperationRejectedAsync(
        string deduplicationKey,
        string messageId,
        string commandMessageId,
        SlotOperationCommandRejectedPayload payload,
        CancellationToken cancellationToken = default) =>
        SendDurableCoreAsync(
            "SlotOperationCommandRejected",
            deduplicationKey,
            messageId,
            commandMessageId,
            payload,
            allowRecoveryRequired: true,
            cancellationToken);

    public Task<ExceptionRecoverySessionOpenedPayload> RequestExceptionRecoverySessionAsync(
        string messageId,
        ExceptionRecoverySessionRequestedPayload payload,
        CancellationToken cancellationToken = default) =>
        SendRecoveryRequestAsync<ExceptionRecoverySessionOpenedPayload>(
            "ExceptionRecoverySessionRequested",
            messageId,
            payload,
            "ExceptionRecoverySessionOpened",
            cancellationToken);

    public Task<RecoveryActionAcceptedPayload> SubmitRecoveryActionAsync(
        string messageId,
        RecoveryActionSubmittedPayload payload,
        CancellationToken cancellationToken = default) =>
        SendRecoveryRequestAsync<RecoveryActionAcceptedPayload>(
            "RecoveryActionSubmitted",
            messageId,
            payload,
            "RecoveryActionAccepted",
            cancellationToken);

    /// <summary>
    /// Sends a hardware recovery record and returns the server's answer. <c>REJECTED</c> is an
    /// answer, not a failure: it comes back as the result, and nothing on this end changes for it.
    /// </summary>
    public async Task<HardwareRecoveryRecordResultPayload> SubmitHardwareRecoveryRecordAsync(
        string messageId,
        HardwareRecoveryRecordSubmittedPayload payload,
        CancellationToken cancellationToken = default)
    {
        ValidateHardwareRecoveryRecord(payload);
        HardwareRecoveryRecordResultPayload result = await SendRecoveryRequestAsync<
            HardwareRecoveryRecordResultPayload>(
                "HardwareRecoveryRecordSubmitted",
                messageId,
                payload,
                "HardwareRecoveryRecordResult",
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(result.RecordId, payload.RecordId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("CORRELATION_INVALID");
        }

        if (result.Outcome is not ("RECORDED" or "REJECTED") || result.RecoverySessionRevision < 0)
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }

        if (result.Problem is not null)
        {
            ValidateProblem(result.Problem);
        }

        return result;
    }

    public async Task<LoadCancellationAuthorizationPayload> RequestLoadCancellationStartAsync(
        string messageId,
        LoadCancellationStartRequestedPayload payload,
        CancellationToken cancellationToken = default)
    {
        ValidateLoadCancellationStartRequested(payload);
        LoadCancellationAuthorizationPayload authorization = await SendRecoveryRequestAsync<
            LoadCancellationAuthorizationPayload>(
                "LoadCancellationStartRequested",
                messageId,
                payload,
                "LoadCancellationAuthorization",
                cancellationToken).ConfigureAwait(false);
        ValidateLoadCancellationAuthorization(payload, authorization);
        return authorization;
    }

    public Task<string> RequestLoadCompensationAsync(
        string messageId,
        LoadCompensationRequestedPayload payload,
        CancellationToken cancellationToken = default)
    {
        ValidateLoadCompensationRequested(payload);
        return SendRequestWithoutResponseAsync(
            "LoadCompensationRequested",
            messageId,
            payload,
            cancellationToken);
    }

    public Task<string> RequestLoadCorrectionAsync(
        string messageId,
        LoadCorrectionRequestedPayload payload,
        CancellationToken cancellationToken = default)
    {
        ValidateLoadCorrectionRequested(payload);
        return SendRequestWithoutResponseAsync(
            "LoadCorrectionRequested",
            messageId,
            payload,
            cancellationToken);
    }

    public Task<string> SendLoadCancellationResultAsync(
        string deduplicationKey,
        string messageId,
        LoadCancellationResultPayload payload,
        CancellationToken cancellationToken = default)
    {
        ValidateLoadCancellationResult(payload);
        return SendDurableCoreAsync(
            "LoadCancellationResult",
            deduplicationKey,
            messageId,
            null,
            payload,
            allowRecoveryRequired: true,
            cancellationToken);
    }

    public Task<string> SendLoadCompensationResultAsync(
        string deduplicationKey,
        string messageId,
        LoadCompensationResultPayload payload,
        CancellationToken cancellationToken = default)
    {
        ValidateLoadCompensationResult(payload);
        return SendDurableCoreAsync(
            "LoadCompensationResult",
            deduplicationKey,
            messageId,
            null,
            payload,
            allowRecoveryRequired: true,
            cancellationToken);
    }

    public Task<string> SendLoadCorrectionResultAsync(
        string deduplicationKey,
        string messageId,
        LoadCorrectionResultPayload payload,
        CancellationToken cancellationToken = default)
    {
        ValidateLoadCorrectionResult(payload);
        return SendDurableCoreAsync(
            "LoadCorrectionResult",
            deduplicationKey,
            messageId,
            null,
            payload,
            allowRecoveryRequired: true,
            cancellationToken);
    }

    public Task<string> SendFaultCargoRecoveryResultAsync(
        string deduplicationKey,
        string messageId,
        FaultCargoRecoveryResultPayload payload,
        CancellationToken cancellationToken = default)
    {
        ValidateFaultCargoRecoveryResult(payload);
        return SendDurableCoreAsync(
            "FaultCargoRecoveryResult",
            deduplicationKey,
            messageId,
            null,
            payload,
            allowRecoveryRequired: true,
            cancellationToken);
    }

    public Task<string> SendForcedMechanicalRecoveryResultAsync(
        string deduplicationKey,
        string messageId,
        ForcedMechanicalRecoveryResultPayload payload,
        CancellationToken cancellationToken = default)
    {
        ValidateForcedMechanicalRecoveryResult(payload);
        return SendDurableCoreAsync(
            "ForcedMechanicalRecoveryResult",
            deduplicationKey,
            messageId,
            null,
            payload,
            allowRecoveryRequired: true,
            cancellationToken);
    }

    public Task<ManualChargingReturnToServiceResultPayload> RequestManualChargingReturnToServiceAsync(
        string messageId,
        ManualChargingReturnToServiceRequestedPayload payload,
        CancellationToken cancellationToken = default) =>
        SendManualChargingReturnToServiceRequestAsync(messageId, payload, cancellationToken);

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

        // 会话中途的告警发布先停下来。拿到这把锁再清零，一次已经通过检查、正在发送的告警快照就只可能落在
        // 旧连接上，插不进下面这次握手——握手里每条快照都直接读下一行等 ack。
        await _alarmPublishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _receiveLoopGeneration, 0);
        _alarmPublishGate.Release();

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

            // 上一个会话没等到 ack 的持久报文先补发，下面那份报告因此看到的是已经对齐的账。补发与否，
            // 握手都照常走完：真服务端每个新世代都从零开始，手上没有能力快照、安全快照和恢复状态报告，
            // 对补发只回一条 DurableAck，所以跳过快照的车会去等一条永远不来的 SessionReadiness，
            // 超时断开、再重连一次（8005-agv-control-server#33）。v2 服务端判 Ready 更要求本代次三者
            // 齐全，缺一样都出不来。
            //
            // 唯一不补发的持久报文是 RecoveryStateReport：它说的是发出它那次握手时的事实，而这次握手
            // 会发一份新的取代它。补发旧报告还会让服务端立即回一条 SessionReadiness、再重放它待确认的
            // 恢复命令，全都夹在这里还没发完的快照中间。
            IReadOnlyList<WireToGateDurableMessage> unacknowledged = await _journal
                .ReadUnacknowledgedOutgoingAsync(cancellationToken)
                .ConfigureAwait(false);
            List<WireToGateDurableMessage> supersededReports = [];
            foreach (WireToGateDurableMessage pending in unacknowledged)
            {
                if (string.Equals(pending.MessageType, "RecoveryStateReport", StringComparison.Ordinal))
                {
                    supersededReports.Add(pending);
                    continue;
                }

                await ReplayDurableOutgoingAsync(pending, generation, cancellationToken)
                    .ConfigureAwait(false);
            }

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
                    _activationCoordinator.ActiveConfiguration.ConfigurationVersion,
                    // 报**本机生效配置**的指纹，不是从配置项现算的那个：激活成功之后车装着的是哪
                    // 一版，只有生效配置存储说了算。从配置项现算会让每次激活之后两端立刻对不上。
                    _activationCoordinator.ActiveConfiguration.Fingerprint,
                    slotStates,
                    _options.SupportsBatchUnlock,
                    1),
                cancellationToken).ConfigureAwait(false);

            WireToGateSafetySummaryPayload safety = CreateSafetySummary(io);
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

            await SendOnboardAlarmSnapshotAsync(generation, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<WireToGateDurableMessage> pendingResultReplays =
                await SendRecoveryStateReportAsync(generation, io, cancellationToken).ConfigureAwait(false);
            // 这几份报告从来没收到过 DurableAck。这里标 Acknowledged 的意思是不再欠服务端这一份，与
            // ComputeContentSha256Async 本来就把恢复状态报告排除在待确认业务报文之外一致。
            foreach (WireToGateDurableMessage superseded in supersededReports)
            {
                await _journal
                    .MarkOutgoingAcknowledgedAsync(superseded.MessageId, superseded.ContentSha256, cancellationToken)
                    .ConfigureAwait(false);
            }

            WireToGateEnvelope readinessEnvelope = await ReadEnvelopeAsync(generation, cancellationToken)
                .ConfigureAwait(false);
            ThrowIfProtocolProblem(readinessEnvelope);
            ApplySessionReadiness(readinessEnvelope, requireExactConfiguredBaseline: true);
            StartReceiveLoop(generation);
            foreach (WireToGateDurableMessage pendingResult in pendingResultReplays)
            {
                await ReplayAcknowledgedResultAsync(pendingResult, generation, cancellationToken)
                    .ConfigureAwait(false);
            }

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

    private async Task<TResponse> SendRecoveryRequestAsync<TResponse>(
        string messageType,
        string messageId,
        object payload,
        string acceptedMessageType,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        RequireUuid(messageId, nameof(messageId));
        WireToGateSessionSnapshot current = Current;
        if (!current.Connected
            || current.SessionGeneration is null
            || current.Readiness is not (WireToGateSessionReadiness.Ready
                or WireToGateSessionReadiness.RecoveryRequired))
        {
            throw new InvalidOperationException("WIRE_TO_GATE_NOT_READY");
        }

        WireToGateEnvelope request = WireToGateProtocolSerializer.Create(
            messageType,
            messageId,
            null,
            _options.AgvId,
            current.SessionGeneration,
            _clock.Now.ToUniversalTime(),
            payload);
        TaskCompletionSource<WireToGateEnvelope> response = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_responseWaiters.TryAdd(messageId, response))
        {
            throw new InvalidOperationException("重复的恢复请求messageId。");
        }

        try
        {
            await SendEnvelopeAsync(request, cancellationToken).ConfigureAwait(false);
            WireToGateEnvelope responseEnvelope = await response.Task
                .WaitAsync(_options.MessageTimeout, cancellationToken)
                .ConfigureAwait(false);
            ThrowIfProtocolProblem(responseEnvelope);
            if (responseEnvelope.MessageType.EndsWith("Rejected", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(ReadRecoveryProblemReason(responseEnvelope));
            }

            WireToGateProtocolSerializer.RequireMessage(responseEnvelope, acceptedMessageType, messageId);
            return WireToGateProtocolSerializer.DeserializePayload<TResponse>(responseEnvelope);
        }
        finally
        {
            _responseWaiters.TryRemove(messageId, out _);
        }
    }

    private async Task<string> SendRequestWithoutResponseAsync(
        string messageType,
        string messageId,
        object payload,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        RequireUuid(messageId, nameof(messageId));
        WireToGateSessionSnapshot current = Current;
        if (!current.Connected
            || current.SessionGeneration is null
            || current.Readiness is not (WireToGateSessionReadiness.Ready
                or WireToGateSessionReadiness.RecoveryRequired))
        {
            throw new InvalidOperationException("WIRE_TO_GATE_NOT_READY");
        }

        WireToGateEnvelope request = WireToGateProtocolSerializer.Create(
            messageType,
            messageId,
            null,
            _options.AgvId,
            current.SessionGeneration,
            _clock.Now.ToUniversalTime(),
            payload);
        await SendEnvelopeAsync(request, cancellationToken).ConfigureAwait(false);
        return messageId;
    }

    private static string ReadRecoveryProblemReason(WireToGateEnvelope envelope)
    {
        if (envelope.Payload.TryGetProperty("problem", out JsonElement problem)
            && problem.TryGetProperty("reasonCode", out JsonElement reasonCode)
            && reasonCode.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(reasonCode.GetString()))
        {
            return reasonCode.GetString()!;
        }

        return "RECOVERY_REQUEST_REJECTED";
    }

    private async Task<ManualChargingReturnToServiceResultPayload>
        SendManualChargingReturnToServiceRequestAsync(
            string messageId,
            ManualChargingReturnToServiceRequestedPayload payload,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        RequireUuid(messageId, nameof(messageId));
        ValidateManualChargingRequest(payload);
        WireToGateSessionSnapshot current = Current;
        if (!current.Connected
            || current.SessionGeneration is null
            || current.Readiness is not (WireToGateSessionReadiness.Ready
                or WireToGateSessionReadiness.RecoveryRequired))
        {
            throw new InvalidOperationException("WIRE_TO_GATE_NOT_READY");
        }

        WireToGateEnvelope request = WireToGateProtocolSerializer.Create(
            "ManualChargingReturnToServiceRequested",
            messageId,
            null,
            _options.AgvId,
            current.SessionGeneration,
            _clock.Now.ToUniversalTime(),
            payload);
        TaskCompletionSource<WireToGateEnvelope> response = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_responseWaiters.TryAdd(messageId, response))
        {
            throw new InvalidOperationException("重复的人工充电返岗请求messageId。");
        }

        try
        {
            await SendEnvelopeAsync(request, cancellationToken).ConfigureAwait(false);
            WireToGateEnvelope responseEnvelope = await response.Task
                .WaitAsync(_options.MessageTimeout, cancellationToken)
                .ConfigureAwait(false);
            ThrowIfProtocolProblem(responseEnvelope);
            WireToGateProtocolSerializer.RequireMessage(
                responseEnvelope,
                "ManualChargingReturnToServiceResult",
                messageId);
            ManualChargingReturnToServiceResultPayload result =
                DeserializeManualChargingResult(responseEnvelope);
            if (!string.Equals(result.RequestId, payload.RequestId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("CORRELATION_INVALID");
            }

            return result;
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
            // An OperationResult is put on file even when it cannot go out now (durableBeforeSend): a load that
            // ends while the session is down or mid-handshake is replayed by the next handshake under the same
            // messageId (ADR-cross-0029 step 4), instead of being lost and settled again from the live IO
            // (onboard-hmi#127). Only OperationResult: SafetyStateChanged and the other durable messages keep
            // their own replay behaviour.
            if (string.Equals(messageType, "OperationResult", StringComparison.Ordinal))
            {
                await StoreDurableAsync(
                    messageType,
                    deduplicationKey,
                    messageId,
                    correlationId,
                    payload,
                    current.SessionGeneration ?? Volatile.Read(ref _lastSessionGeneration),
                    rebind: false,
                    cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("WIRE_TO_GATE_NOT_READY");
        }

        WireToGateDurableMessage stored = await StoreDurableAsync(
            messageType,
            deduplicationKey,
            messageId,
            correlationId,
            payload,
            current.SessionGeneration,
            rebind: true,
            cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// The outbox row for a durable message: the one already on file under this key, content-checked against
    /// the candidate (and rebound to <paramref name="generation"/> when <paramref name="rebind"/>), or a new one.
    /// </summary>
    /// <remarks>
    /// A new row is stamped with the generation at hand; the handshake rebinds every pending row to its own
    /// generation before replaying it.
    /// </remarks>
    private async Task<WireToGateDurableMessage> StoreDurableAsync(
        string messageType,
        string deduplicationKey,
        string messageId,
        string? correlationId,
        object payload,
        long? generation,
        bool rebind,
        CancellationToken cancellationToken)
    {
        WireToGateDurableMessage? existing = await _journal
            .ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken)
            .ConfigureAwait(false);
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
                generation,
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

            return rebind && generation is long current
                ? await RebindDurableMessageForSessionAsync(existing, current, cancellationToken)
                    .ConfigureAwait(false)
                : existing;
        }

        WireToGateEnvelope envelope = WireToGateProtocolSerializer.Create(
            messageType,
            messageId,
            correlationId,
            _options.AgvId,
            generation,
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
        return await _journal
            .SaveOutgoingBeforeSendAsync(durable, cancellationToken)
            .ConfigureAwait(false);
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
        _alarmPublishGate.Dispose();
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
        RequireSnapshotApplied(ackEnvelope, messageId, snapshotKind, revision, contentSha256);
    }

    private static void RequireSnapshotApplied(
        WireToGateEnvelope ackEnvelope,
        string messageId,
        string snapshotKind,
        long revision,
        string contentSha256)
    {
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

    /// <summary>
    /// 协议 v2 消息 7／8：收一次激活命令，核指纹，把结果报回去。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>结果走 RELIABLE，等 <c>DurableAck</c>。</b>REQ-0264 的「不能猜测成功」正是
    /// <c>PENDING_RESULT_REPLAY</c> 存在的理由：用 REQUEST/RESPONSE 就没有补报语义，断线即丢，服务端
    /// 除了猜没有别的可做。
    /// </para>
    /// <para>
    /// <b>补报不需要这一层记任何东西。</b>结果与生效配置在 #27 的原子文档里一起落盘；服务端重连后按
    /// <c>SLOT_CONFIGURATION</c> 这个恢复角色重发同一条命令，
    /// <see cref="SlotConfigurationActivationCoordinator.Activate"/> 认出这个 <c>activationId</c> 已经
    /// 有结果，原样返回，不产生第二次激活。所以这里对补报与首次是同一段代码。
    /// </para>
    /// </remarks>
    private async Task HandleSlotConfigurationActivationAsync(
        WireToGateEnvelope envelope,
        long generation,
        CancellationToken cancellationToken)
    {
        if (envelope.CorrelationId is not null)
        {
            throw new InvalidDataException("CORRELATION_INVALID");
        }

        SlotConfigurationActivationCommandPayload command = WireToGateProtocolSerializer
            .DeserializePayload<SlotConfigurationActivationCommandPayload>(envelope);
        RequireUuid(command.ActivationId, nameof(command.ActivationId));
        RequireSha256(command.TargetSlotConfigurationFingerprint, nameof(command.TargetSlotConfigurationFingerprint));
        if (string.IsNullOrWhiteSpace(command.TargetSlotConfigurationVersion))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }

        SlotConfigurationActivationResult result = _activationCoordinator.Activate(
            new SlotConfigurationActivationRequest(
                command.ActivationId,
                command.TargetSlotConfigurationVersion,
                command.TargetSlotConfigurationFingerprint));

        string messageId = Guid.NewGuid().ToString("D");
        WireToGateEnvelope report = WireToGateProtocolSerializer.Create(
            "SlotConfigurationActivationResult",
            messageId,
            null,
            _options.AgvId,
            generation,
            _clock.Now.ToUniversalTime(),
            new SlotConfigurationActivationResultPayload(
                result.ActivationId,
                result.Status == SlotConfigurationActivationStatus.Activated ? "ACTIVATED" : "REJECTED",
                result.ReasonCode is null
                    ? null
                    : new ProtocolProblem(
                        result.ReasonCode,
                        "payload.targetSlotConfigurationFingerprint",
                        null),
                _activationCoordinator.ActiveConfiguration.ConfigurationVersion,
                result.ResultingFingerprint,
                result.SettledAt));
        await SendEnvelopeAsync(report, cancellationToken).ConfigureAwait(false);

        // 直接读下一行，不走 _responseWaiters：这段代码跑在接收循环**里**，注册等待表然后 await 会
        // 让循环停在这里等一条只有循环自己才读得到的消息——死锁。握手期的
        // SendSnapshotAndRequireAckAsync 用的是同一种直接读，前提也一样：这条 RELIABLE 消息的下一行
        // 就是它的 DurableAck。
        WireToGateEnvelope ackEnvelope = await ReadEnvelopeAsync(generation, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfProtocolProblem(ackEnvelope);
        WireToGateProtocolSerializer.RequireMessage(ackEnvelope, "DurableAck", messageId);
    }

    /// <summary>
    /// 协议 v2 消息 9 <c>OnboardAlarmSnapshot</c>：把车载端当前全量告警报上去。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>SNAPSHOT，不是事件流。</b>每一份都是当下的全部告警，后一份整体取代前一份。事件流断线重连
    /// 那段正是 REQ-0269 禁止的东西：重连后不知道漏了什么，只能显示一个不确定新旧的旧值。所以握手里
    /// 就发一份——重连之后服务端手上立刻是当下的事实，不需要任何补发。
    /// </para>
    /// <para>
    /// 一份空快照也要发。它说的是「这台车此刻没有告警」，与「这台车从没报过」是两件事——后者在服务端
    /// 看板上显示的是「尚未收到该车快照」，那是一个拿不到事实的状态，不该由一台正常在线的车造成。
    /// </para>
    /// </remarks>
    private async Task SendOnboardAlarmSnapshotAsync(long generation, CancellationToken cancellationToken)
    {
        OnboardAlarmSnapshot snapshot = _alarmBoard.Capture();
        await SendSnapshotAndRequireAckAsync(
            "OnboardAlarmSnapshot",
            "ONBOARD_ALARM",
            snapshot.SnapshotSequence,
            generation,
            CreateOnboardAlarmSnapshotPayload(snapshot),
            cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _acknowledgedAlarms, new OnboardAlarmPublication(generation, snapshot.Alarms));
    }

    /// <summary>
    /// 会话中途报一份当下的全量告警，等服务端 ack。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只在接收循环跑着的时候发。</b>握手里每条快照都是「发出去、直接读下一行等 ack」，那一段里插进一份告警
    /// 快照，它的 ack 会被握手当成自己的下一行读走。所以握手还没走完、正在重连、或者接收循环已经失败时，这里
    /// 什么都不发，返回 <c>false</c>——重连握手自己会报一份当下的全量。
    /// </para>
    /// <para>
    /// <b>ack 走等待表</b>，与心跳同一种方式：这段代码跑在接收循环之外，下一行归接收循环读，它按
    /// <c>correlationId</c> 把 ack 交回这里。
    /// </para>
    /// <para>
    /// 检查与发送都在 <c>_alarmPublishGate</c> 里做完；重连一开始先拿到同一把锁再把接收循环的会话代清零，所以一次
    /// 已经通过检查的发送只可能落在旧连接上。
    /// </para>
    /// </remarks>
    /// <returns>服务端 ack 了这一份时为 <c>true</c>；会话此刻不能发时为 <c>false</c>。</returns>
    public async Task<bool> PublishAlarmSnapshotAsync(CancellationToken cancellationToken = default)
    {
        OnboardAlarmSnapshot? snapshot = null;
        long? generation = await PublishMidSessionSnapshotAsync(
            "OnboardAlarmSnapshot",
            "ONBOARD_ALARM",
            () =>
            {
                snapshot = _alarmBoard.Capture();
                return (snapshot.SnapshotSequence, CreateOnboardAlarmSnapshotPayload(snapshot));
            },
            cancellationToken).ConfigureAwait(false);
        if (generation is not long applied)
        {
            return false;
        }

        Volatile.Write(ref _acknowledgedAlarms, new OnboardAlarmPublication(applied, snapshot!.Alarms));
        return true;
    }

    /// <summary>
    /// 会话中途回应 <c>SafetyStateSnapshotRequested</c>：报一份此刻的 <c>SafetyStateSnapshot</c>，等服务端 ack
    /// （REQ-0358，onboard-hmi#109，与 control-server#142 配对）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>读数是此刻的 IO。</b>八个仓的 <c>lockState</c>、<c>physicalState</c>、<c>unlockOutputState</c> 与握手时一样由
    /// <see cref="CreateSlotStates"/> 从发送这一刻的 <c>CurrentSnapshot</c> 填，<c>safety</c> 摘要也是此刻求值；IO 离线
    /// 或读不到的仓填 <c>UNKNOWN</c>。服务端看板拿它给管理员看「门早就关了，锁却一直读未锁」这类读数。
    /// </para>
    /// <para>
    /// <b>版本号由调用方给，必须比已被接受的大。</b>快照内容每次都不同（observedAt、读数），同一个版本号换内容就是
    /// 冲突，服务端会拒。调用方与 <c>SafetyStateChanged</c> 共用同一个版本序列，ack 之后这里把已接受版本推进到它。
    /// </para>
    /// <para>
    /// 与告警快照一样只在接收循环跑着时发、ack 走等待表；服务端在 ack 后面紧跟的 <c>SessionReadiness</c> 由接收循环照常处理。
    /// </para>
    /// </remarks>
    /// <returns>服务端 ack 了这一份时为 <c>true</c>；会话此刻不能发时为 <c>false</c>。</returns>
    public async Task<bool> PublishSafetyStateSnapshotAsync(
        long safetyStateVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            safetyStateVersion,
            Volatile.Read(ref _acceptedSafetyStateVersion));
        long? generation = await PublishMidSessionSnapshotAsync(
            "SafetyStateSnapshot",
            "SAFETY_STATE",
            () =>
            {
                IoSnapshot io = _ioModule.CurrentSnapshot;
                return (safetyStateVersion, new SafetyStateSnapshotPayload(
                    safetyStateVersion,
                    _clock.Now.ToUniversalTime(),
                    CreateSafetySummary(io),
                    CreateSlotStates(io)));
            },
            cancellationToken).ConfigureAwait(false);
        if (generation is null)
        {
            return false;
        }

        AdvanceSafetyStateVersion(safetyStateVersion);
        WireToGateSessionSnapshot current = Current;
        Publish(current.Connected, current.SessionGeneration, current.Readiness, current.ReasonCodes);
        return true;
    }

    /// <summary>
    /// 会话中途发一份快照并等 <c>SnapshotAppliedAck</c>。规则见 <see cref="PublishAlarmSnapshotAsync"/>。
    /// </summary>
    /// <param name="capture">在锁里、确认能发之后才调用，取这一份的修订号与载荷。</param>
    /// <returns>ack 了时为发出这一份的会话代；会话此刻不能发时为 <c>null</c>。</returns>
    private async Task<long?> PublishMidSessionSnapshotAsync(
        string messageType,
        string snapshotKind,
        Func<(long Revision, object Payload)> capture,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _alarmPublishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool gateHeld = true;
        string? messageId = null;
        try
        {
            WireToGateSessionSnapshot current = Current;
            long generation = Interlocked.Read(ref _receiveLoopGeneration);
            TaskCompletionSource<Exception>? receiveFailure = _receiveFailure;
            if (!current.Connected
                || generation == 0
                || current.SessionGeneration != generation
                || receiveFailure is null
                || receiveFailure.Task.IsCompleted)
            {
                return null;
            }

            (long revision, object payload) = capture();
            WireToGateEnvelope envelope = WireToGateProtocolSerializer.Create(
                messageType,
                Guid.NewGuid().ToString("D"),
                null,
                _options.AgvId,
                generation,
                _clock.Now.ToUniversalTime(),
                payload);
            messageId = envelope.MessageId;
            string contentSha256 = WireToGateProtocolSerializer.ComputeContentSha256(envelope);
            TaskCompletionSource<WireToGateEnvelope> response = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_responseWaiters.TryAdd(messageId, response))
            {
                throw new InvalidOperationException($"重复的{messageType} messageId。");
            }

            await SendEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
            _alarmPublishGate.Release();
            gateHeld = false;

            WireToGateEnvelope ackEnvelope = await response.Task
                .WaitAsync(_options.MessageTimeout, cancellationToken)
                .ConfigureAwait(false);
            RequireSnapshotApplied(ackEnvelope, messageId, snapshotKind, revision, contentSha256);
            return generation;
        }
        finally
        {
            if (gateHeld)
            {
                _alarmPublishGate.Release();
            }

            if (messageId is not null)
            {
                _responseWaiters.TryRemove(messageId, out _);
            }
        }
    }

    private static OnboardAlarmSnapshotPayload CreateOnboardAlarmSnapshotPayload(OnboardAlarmSnapshot snapshot) =>
        new(snapshot.SnapshotSequence, snapshot.CapturedAt, [.. snapshot.Alarms.Select(ToWireAlarm)]);

    /// <summary>
    /// 车载端的告警模型翻成线上的 <c>AlarmEntry</c>。
    /// </summary>
    /// <remarks>
    /// <c>subjectType</c>／<c>subjectId</c> 是协议表达「这条告警是关于什么的」的方式，服务端凭它
    /// 把告警分到车载界面还是看板。<see cref="AlarmScope"/> 描述的是同一件事，所以映射是一一对应的，
    /// 不引入第三套词汇。
    /// </remarks>
    private static WireAlarmEntry ToWireAlarm(AlarmEntry alarm) => alarm.Scope switch
    {
        AlarmScope.CurrentStop => new WireAlarmEntry(
            StableAlarmId(alarm), alarm.AlarmCode, alarm.Severity, alarm.RaisedAt,
            "STATION", alarm.StationId, alarm.Message),
        AlarmScope.CurrentOperation => new WireAlarmEntry(
            StableAlarmId(alarm), alarm.AlarmCode, alarm.Severity, alarm.RaisedAt,
            "SLOT_OPERATION", alarm.SlotOperationAttemptId, alarm.Message),
        AlarmScope.CurrentVehicle when alarm.PhysicalSlotNumber is int slot => new WireAlarmEntry(
            StableAlarmId(alarm), alarm.AlarmCode, alarm.Severity, alarm.RaisedAt,
            "SLOT", slot.ToString(CultureInfo.InvariantCulture), alarm.Message),
        AlarmScope.CurrentVehicle => new WireAlarmEntry(
            StableAlarmId(alarm), alarm.AlarmCode, alarm.Severity, alarm.RaisedAt,
            "VEHICLE", null, alarm.Message),
        _ => new WireAlarmEntry(
            StableAlarmId(alarm), alarm.AlarmCode, alarm.Severity, alarm.RaisedAt,
            "FLEET", null, alarm.Message)
    };

    /// <summary>
    /// 一条告警的线上身份，由它的内容算出来。
    /// </summary>
    /// <remarks>
    /// 协议要求每条 <c>AlarmEntry</c> 带一个 <c>Id</c>，而 #28 的告警板不给告警发身份——它按告警码
    /// 保存当前全量告警，同一个码同时只有一条。用 <c>Guid.NewGuid()</c> 会让每一份快照里的同一条告警
    /// 都换一个身份，看上去像告警一直在重新发生。所以由内容派生：同一条告警在多份快照里是同一个 id，
    /// 变了就是另一条。
    /// </remarks>
    private static string StableAlarmId(AlarmEntry alarm)
    {
        string canonical = string.Join(
            '',
            alarm.AlarmCode,
            alarm.Severity,
            alarm.RaisedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            alarm.Scope.ToString(),
            alarm.StationId ?? string.Empty,
            alarm.SlotOperationAttemptId ?? string.Empty,
            alarm.PhysicalSlotNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)).AsSpan(0, 16)).ToString("D");
    }

    /// <summary>
    /// Sends this session's RecoveryStateReport and returns the acknowledged results it named as
    /// pending, for <see cref="ReplayAcknowledgedResultAsync"/> once the handshake is through.
    /// </summary>
    /// <remarks>
    /// Only results of the operation still unsettled are named, and only those this journal can send
    /// again: naming one it cannot replay would leave the control server waiting for it for good.
    /// </remarks>
    private async Task<IReadOnlyList<WireToGateDurableMessage>> SendRecoveryStateReportAsync(
        long generation,
        IoSnapshot io,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState recovery = await _journal.ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        List<(WireToGatePendingResult Pending, WireToGateDurableMessage Message)> replayable = [];
        foreach (WireToGatePendingResult pending in recovery.PendingResults.Where(item => string.Equals(
                     item.BusinessId,
                     recovery.UnsettledSlotOperationAttemptId,
                     StringComparison.Ordinal)))
        {
            WireToGateDurableMessage? message = await _journal
                .ReadOutgoingByMessageIdAsync(pending.MessageId, cancellationToken)
                .ConfigureAwait(false);
            if (message is { Acknowledged: true }
                && string.Equals(message.MessageType, pending.MessageType, StringComparison.Ordinal))
            {
                replayable.Add((pending, message));
            }
        }

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
                replayable.Select(item => new PendingResultPayload(
                    item.Pending.MessageType,
                    item.Pending.MessageId,
                    item.Pending.BusinessId,
                    item.Pending.ContentSha256)).ToArray(),
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
        return replayable.Select(item => item.Message).ToArray();
    }

    /// <summary>
    /// Sends an already acknowledged result again, rebound to this session's generation, after the
    /// RecoveryStateReport that named it as pending (CV-OPERATION-RESULT-UNKNOWN-RECONCILE,
    /// REPLAY_RESULT_ON_RECONNECT).
    /// </summary>
    /// <remarks>
    /// The journal row is left alone: it was acknowledged once and stays so, and the rebound line is
    /// derived from it again on every reconnect for as long as the operation stays unsettled. The
    /// DurableAck is awaited through the receive loop rather than read inline, because the control
    /// server replays its own pending commands right after the readiness line and one of them may
    /// arrive first.
    /// </remarks>
    private async Task ReplayAcknowledgedResultAsync(
        WireToGateDurableMessage result,
        long generation,
        CancellationToken cancellationToken)
    {
        WireToGateEnvelope stored = WireToGateProtocolSerializer.DeserializeAndValidate(
            result.WireLine.TrimEnd('\r', '\n'),
            _options.AgvId);
        if (stored.MessageId != result.MessageId
            || stored.MessageType != result.MessageType
            || WireToGateProtocolSerializer.ComputeContentSha256(stored) != result.ContentSha256)
        {
            throw new InvalidDataException("DURABLE_OUTBOX_CONTENT_MISMATCH");
        }

        WireToGateEnvelope rebound = WireToGateProtocolSerializer.RebindSessionGeneration(stored, generation);
        string contentSha256 = WireToGateProtocolSerializer.ComputeContentSha256(rebound);
        TaskCompletionSource<WireToGateEnvelope> response = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_responseWaiters.TryAdd(rebound.MessageId, response))
        {
            throw new InvalidOperationException("重复的WIRE_TO_GATE业务messageId。");
        }

        try
        {
            await SendLineAsync(WireToGateProtocolSerializer.SerializeLine(rebound), cancellationToken)
                .ConfigureAwait(false);
            WireToGateEnvelope ackEnvelope = await response.Task
                .WaitAsync(_options.MessageTimeout, cancellationToken)
                .ConfigureAwait(false);
            ThrowIfProtocolProblem(ackEnvelope);
            WireToGateProtocolSerializer.RequireMessage(ackEnvelope, "DurableAck", rebound.MessageId);
            DurableAckPayload ack = WireToGateProtocolSerializer.DeserializePayload<DurableAckPayload>(ackEnvelope);
            if (ack.AcceptedMessageId != rebound.MessageId
                || ack.AcceptedMessageType != rebound.MessageType
                || ack.AcceptedContentSha256 != contentSha256)
            {
                throw new InvalidDataException("CONTENT_HASH_MISMATCH");
            }
        }
        finally
        {
            _responseWaiters.TryRemove(rebound.MessageId, out _);
        }
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
        Interlocked.Exchange(ref _receiveLoopGeneration, generation);
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

                if (string.Equals(
                    envelope.MessageType,
                    "ManualChargingReturnToServiceResult",
                    StringComparison.Ordinal))
                {
                    ManualChargingReturnToServiceResultPayload result =
                        DeserializeManualChargingResult(envelope);
                    if (_responseWaiters.TryRemove(
                        envelope.CorrelationId!,
                        out TaskCompletionSource<WireToGateEnvelope>? manualResponse))
                    {
                        RememberManualChargingResult(envelope, result);
                        manualResponse.TrySetResult(envelope);
                        continue;
                    }

                    if (RememberManualChargingResult(envelope, result))
                    {
                        continue;
                    }

                    WireToGateRecoveryCommand manualCommand = new(
                        envelope.MessageType,
                        envelope.MessageId,
                        envelope.CorrelationId,
                        envelope.SessionGeneration!.Value,
                        envelope.SentAt,
                        envelope.Payload.GetRawText());
                    ServerCommandReceived?.Invoke(
                        this,
                        new ValueChangedEventArgs<WireToGateServerCommand>(manualCommand));
                    continue;
                }

                if (envelope.CorrelationId is not null
                    && _responseWaiters.TryRemove(envelope.CorrelationId, out TaskCompletionSource<WireToGateEnvelope>? response))
                {
                    response.TrySetResult(envelope);
                    continue;
                }

                // 协议 v2 消息 7。整条链路都在这一层里走完：收命令、核指纹、发结果，不经过应用层，
                // 因为 REQ-0265 说得很清楚——一次激活动作已经包含重新投运意图，中间没有第二道人工
                // 审批关卡，也就没有任何要交给界面去等的东西。
                if (string.Equals(
                    envelope.MessageType, "SlotConfigurationActivationCommand", StringComparison.Ordinal))
                {
                    await HandleSlotConfigurationActivationAsync(envelope, generation, stopping.Token)
                        .ConfigureAwait(false);
                    continue;
                }

                if (TryCreateServerCommand(envelope, out WireToGateServerCommand? command))
                {
                    ServerCommandReceived?.Invoke(
                        this,
                        new ValueChangedEventArgs<WireToGateServerCommand>(command!));
                    if (command is WireToGateExceptionRecoverySessionSnapshot { State: "CLOSED" } closedRecovery)
                    {
                        // Only the CLOSED revision is acknowledged (8005-agv-control-server#31). After
                        // every RecoveryStateReport the server replays each recovery session snapshot
                        // that is neither acknowledged nor fenced by a newer revision, and that replay
                        // is the only way a restarted client gets an open session back: the session
                        // lives in memory, and the journal keeps just its id. An acknowledged OPEN
                        // snapshot left a restarted HMI holding an id and no session. Nothing
                        // supersedes CLOSED, so unacknowledged it was replayed into every later
                        // session -- and a client that has it needs nothing more.
                        await SendSnapshotAppliedAckAsync(
                            envelope,
                            "EXCEPTION_RECOVERY_SESSION",
                            closedRecovery.RecoverySessionRevision,
                            WireToGateProtocolSerializer.ComputeContentSha256(envelope),
                            generation,
                            stopping.Token).ConfigureAwait(false);
                    }

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
            Interlocked.CompareExchange(ref _receiveLoopGeneration, 0, generation);
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
        string envelopeContentSha256 = WireToGateProtocolSerializer.ComputeContentSha256(envelope);
        try
        {
            string payloadContentSha256 = WireToGateProtocolSerializer.ComputePayloadContentSha256(envelope);
            if (envelope.CorrelationId is not null)
            {
                throw new InvalidDataException("CORRELATION_INVALID");
            }

            (long revision, string snapshotKind) = ApplyJourneyProjection(
                envelope,
                payloadContentSha256,
                cancellationToken);
            await _journal.SaveAppliedJourneySnapshotAsync(
                new WireToGateAppliedJourneySnapshot(
                    envelope.MessageType,
                    envelope.MessageId,
                    revision,
                    envelopeContentSha256,
                    envelope.Payload.GetRawText(),
                    _clock.Now.ToUniversalTime()),
                cancellationToken).ConfigureAwait(false);
            await SendSnapshotAppliedAckAsync(
                envelope,
                snapshotKind,
                revision,
                envelopeContentSha256,
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
                WireToGateProtocolSerializer.ComputePayloadContentSha256(snapshot.PayloadJson),
                cancellationToken);
            if (revision != snapshot.Revision)
            {
                throw new InvalidDataException("PERSISTED_SNAPSHOT_REVISION_MISMATCH");
            }
        }
    }

    private (long Revision, string SnapshotKind) ApplyJourneyProjection(
        WireToGateEnvelope envelope,
        string payloadContentSha256,
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
                        payloadContentSha256,
                        journey => journey with
                        {
                            VehicleBusinessState = new WireToGateVehicleBusinessState(
                                payload.VehicleBusinessStateRevision,
                                payload.Readiness,
                                payload.ActivePurpose,
                                payload.ManualChargingHold,
                                payload.BatteryState,
                                payload.ChargingCycleState,
                                payload.LoadingPhase is null
                                    ? null
                                    : new WireToGateLoadingPhase(
                                        payload.LoadingPhase.State,
                                        payload.LoadingPhase.CargoHoldingDeadlineAt,
                                        payload.LoadingPhase.ClosedReason),
                                payload.BlockingFacts
                                    .Select(item => new WireToGateBlockingFact(
                                        item.ReasonCode,
                                        item.SubjectType,
                                        item.SubjectId))
                                    .ToArray(),
                                payload.ObservedAt,
                                payloadContentSha256),
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
                        payloadContentSha256,
                        journey => journey with
                        {
                            CurrentStopWorklist = new WireToGateCurrentStopWorklist(
                                payload.StationId,
                                payload.WorklistRevision,
                                payload.OperationSessionId,
                                payload.StationDepartureDeadlineAt,
                                payload.Items.Select(ToCoreWorklistItem).ToArray(),
                                payloadContentSha256),
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
                        payloadContentSha256,
                        journey => journey with
                        {
                            UpcomingStopPlan = new WireToGateUpcomingStopPlan(
                                payload.PlanRevision,
                                payload.Legs.Select(ToCoreMovementLeg).ToArray(),
                                payloadContentSha256),
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
        string payloadContentSha256,
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
                    if (payloadContentSha256 != previous.ContentSha256)
                    {
                        throw new InvalidDataException("SNAPSHOT_REVISION_CONTENT_CONFLICT");
                    }

                    return;
                }
            }

            _journeyRevisions[messageType] = (revision, payloadContentSha256);
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
        await SendEnvelopeAsync(
                CreateProtocolProblem(rejected.MessageId, rejected.MessageType, generation, reasonCode),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a well-formed server command that no longer applies, with a ProtocolProblem correlated
    /// to it, and keeps the session.
    /// </summary>
    /// <remarks>
    /// The parse-time refusal above is for a command this client cannot accept at all and ends the
    /// session after it. A business refusal is different: the command was valid when it was sent and
    /// has since been overtaken -- a pre-departure check asking about a safety state the vehicle has
    /// already moved past is the case CV-PREDEPARTURE-SAFETY-EXPIRES names.
    /// </remarks>
    public async Task RejectServerCommandAsync(
        WireToGateServerCommand command,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        await SendEnvelopeAsync(
                CreateProtocolProblem(command.MessageId, command.MessageType, command.SessionGeneration, reasonCode),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private WireToGateEnvelope CreateProtocolProblem(
        string rejectedMessageId,
        string rejectedMessageType,
        long generation,
        string reasonCode) =>
        WireToGateProtocolSerializer.Create(
            "ProtocolProblem",
            Guid.NewGuid().ToString("D"),
            rejectedMessageId,
            _options.AgvId,
            generation,
            _clock.Now.ToUniversalTime(),
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
                expectedProtocolVersion = WireToGateRelease.ProtocolVersion,
                expectedProfileId = WireToGateRelease.ProfileId,
                expectedProtocolReleaseManifestSha256 = WireToGateRelease.ManifestSha256
            });

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
                    RequireUuid(payload.OperationSessionId, nameof(payload.OperationSessionId));

                    // expectedSublots is minItems 1, maxItems 8, uniqueItems, and each element
                    // minLength 1 -- read straight off the frozen schema rather than narrowed to
                    // the single-element form a one-demand dispatch happens to produce today, and
                    // with IsNullOrEmpty rather than IsNullOrWhiteSpace for the same reason: " " is
                    // schema-legal, no trimmed entry can ever match it, and refusing it would be an
                    // inbound check stricter than the contract.
                    if (string.IsNullOrWhiteSpace(payload.StationId)
                        || payload.WorklistRevision < 0
                        || payload.ExpectedSublots is null
                        || payload.ExpectedSublots.Count is < 1 or > 8
                        || payload.ExpectedSublots.Any(string.IsNullOrEmpty)
                        || payload.ExpectedSublots.Distinct(StringComparer.Ordinal).Count()
                            != payload.ExpectedSublots.Count
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
                        payload.OperationSessionId,
                        payload.StationId,
                        payload.WorklistRevision,
                        payload.ExpectedSublots,
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
            case "ExceptionRecoverySessionSnapshot":
                {
                    if (envelope.CorrelationId is not null)
                    {
                        throw new InvalidDataException("CORRELATION_INVALID");
                    }

                    ExceptionRecoverySessionSnapshotPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<ExceptionRecoverySessionSnapshotPayload>(envelope);
                    RequireUuid(
                        payload.ExceptionRecoverySessionId,
                        nameof(payload.ExceptionRecoverySessionId));
                    RequireUuid(payload.EventId, nameof(payload.EventId));
                    if (payload.RecoverySessionRevision < 0
                        || payload.State is not ("OPEN" or "ACTION_SELECTED" or "EXECUTING" or "CLOSED")
                        || string.IsNullOrWhiteSpace(payload.AdministratorId)
                        || payload.AdministratorRole is not ("MAINTENANCE_ADMINISTRATOR" or "SYSTEM_ADMINISTRATOR")
                        || payload.DemandId is not null
                            && !Guid.TryParseExact(payload.DemandId, "D", out _)
                        || payload.Slots is null
                        || payload.AllowedActions is null
                        || payload.BlockingFacts is null)
                    {
                        throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
                    }

                    ValidateSortedSlots(payload.Slots);
                    if (payload.AllowedActions.Any(string.IsNullOrWhiteSpace)
                        || payload.BlockingFacts.Any(fact =>
                            string.IsNullOrWhiteSpace(fact.ReasonCode)
                            || string.IsNullOrWhiteSpace(fact.SubjectType)))
                    {
                        throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
                    }

                    command = new WireToGateExceptionRecoverySessionSnapshot(
                        envelope.MessageId,
                        envelope.CorrelationId,
                        envelope.SessionGeneration!.Value,
                        envelope.SentAt,
                        payload.ExceptionRecoverySessionId,
                        payload.RecoverySessionRevision,
                        payload.State,
                        payload.AdministratorId,
                        payload.AdministratorRole,
                        payload.EventId,
                        payload.DemandId,
                        payload.SlotOperationAttemptId,
                        payload.Slots,
                        payload.SelectedAction,
                        payload.AllowedActions,
                        payload.BlockingFacts.Select(fact => new WireToGateRecoveryBlockingFact(
                            fact.ReasonCode,
                            fact.SubjectType,
                            fact.SubjectId)).ToArray());
                    return true;
                }
            case "SublotRejected":
                {
                    RequireCorrelatedProblem(envelope, typeof(SublotRejectedPayload));
                    SublotRejectedPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<SublotRejectedPayload>(envelope);

                    // demandId is nullable since 2.0.0 and null is a legal, meaningful value: a
                    // sublot outside the dispatch scope has no demand to be named against.
                    // Demanding a uuid here would refuse exactly the rejection the operator most
                    // needs to see.
                    if (payload.DemandId is not null)
                    {
                        RequireUuid(payload.DemandId, nameof(payload.DemandId));
                    }

                    RequireUuid(payload.OperationSessionId, nameof(payload.OperationSessionId));
                    ValidateProblem(payload.Problem);
                    // minLength 1, so empty is refused and whitespace is not: see the same
                    // reasoning on expectedSublots above.
                    if (payload.CurrentWorklistRevision < 0
                        || string.IsNullOrEmpty(payload.RejectedSublot))
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
            case "LoadCompensationCommand":
                {
                    if (envelope.CorrelationId is not null)
                    {
                        throw new InvalidDataException("CORRELATION_INVALID");
                    }

                    LoadCompensationCommandPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<LoadCompensationCommandPayload>(envelope);
                    RequireUuid(payload.RecoveryActionId, nameof(payload.RecoveryActionId));
                    RequireUuid(payload.ExceptionRecoverySessionId, nameof(payload.ExceptionRecoverySessionId));
                    RequireUuid(payload.DemandId, nameof(payload.DemandId));
                    RequireUuid(payload.SlotOperationAttemptId, nameof(payload.SlotOperationAttemptId));
                    RequireSha256(payload.CommandContentSha256, nameof(payload.CommandContentSha256));
                    ValidateSortedSlots(payload.Slots);
                    if (!string.Equals(payload.ExpectedFinalPhysicalState, "EMPTY", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
                    }

                    command = new WireToGateLoadCompensationCommand(
                        envelope.MessageId,
                        envelope.SessionGeneration!.Value,
                        envelope.SentAt,
                        payload.RecoveryActionId,
                        payload.ExceptionRecoverySessionId,
                        payload.DemandId,
                        payload.SlotOperationAttemptId,
                        payload.Slots,
                        payload.ExpectedFinalPhysicalState,
                        payload.CommandContentSha256);
                    return true;
                }
            case "LoadCorrectionCommand":
                {
                    if (envelope.CorrelationId is not null)
                    {
                        throw new InvalidDataException("CORRELATION_INVALID");
                    }

                    LoadCorrectionCommandPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<LoadCorrectionCommandPayload>(envelope);
                    RequireUuid(payload.CorrectionId, nameof(payload.CorrectionId));
                    RequireUuid(payload.DemandId, nameof(payload.DemandId));
                    RequireUuid(payload.SlotOperationAttemptId, nameof(payload.SlotOperationAttemptId));
                    RequireSha256(payload.CommandContentSha256, nameof(payload.CommandContentSha256));
                    ValidateSortedSlots(payload.Slots);
                    if (payload.ExpectedSequence is null
                        || !payload.ExpectedSequence.SequenceEqual(["EMPTY", "OCCUPIED"]))
                    {
                        throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
                    }

                    command = new WireToGateLoadCorrectionCommand(
                        envelope.MessageId,
                        envelope.SessionGeneration!.Value,
                        envelope.SentAt,
                        payload.CorrectionId,
                        payload.DemandId,
                        payload.SlotOperationAttemptId,
                        payload.Slots,
                        payload.ExpectedSequence,
                        payload.CommandContentSha256);
                    return true;
                }
            case "FaultCargoRecoveryCommand":
                {
                    if (envelope.CorrelationId is not null)
                    {
                        throw new InvalidDataException("CORRELATION_INVALID");
                    }

                    FaultCargoRecoveryCommandPayload payload =
                        WireToGateProtocolSerializer.DeserializePayload<FaultCargoRecoveryCommandPayload>(envelope);
                    RequireUuid(payload.ExceptionRecoverySessionId, nameof(payload.ExceptionRecoverySessionId));
                    RequireUuid(payload.RecoveryActionId, nameof(payload.RecoveryActionId));
                    RequireUuid(payload.DemandId, nameof(payload.DemandId));
                    RequireUuid(payload.HandoffId, nameof(payload.HandoffId));
                    RequireSha256(payload.CommandContentSha256, nameof(payload.CommandContentSha256));
                    ValidateSortedSlots(payload.Slots);

                    command = new WireToGateFaultCargoRecoveryCommand(
                        envelope.MessageId,
                        envelope.SessionGeneration!.Value,
                        envelope.SentAt,
                        payload.ExceptionRecoverySessionId,
                        payload.RecoveryActionId,
                        payload.DemandId,
                        payload.Slots,
                        payload.HandoffId,
                        payload.CommandContentSha256);
                    return true;
                }
            case "ForcedMechanicalRecoveryCommand":
                {
                    if (envelope.CorrelationId is not null)
                    {
                        throw new InvalidDataException("CORRELATION_INVALID");
                    }

                    ForcedMechanicalRecoveryCommandPayload payload =
                        WireToGateProtocolSerializer
                            .DeserializePayload<ForcedMechanicalRecoveryCommandPayload>(envelope);
                    RequireUuid(payload.ExceptionRecoverySessionId, nameof(payload.ExceptionRecoverySessionId));
                    RequireUuid(payload.RecoveryActionId, nameof(payload.RecoveryActionId));
                    if (payload.DemandId is not null)
                    {
                        RequireUuid(payload.DemandId, nameof(payload.DemandId));
                    }

                    if (payload.ForcedRecoveryGeneration < 0)
                    {
                        throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
                    }

                    RequireSha256(payload.CommandContentSha256, nameof(payload.CommandContentSha256));
                    ValidateSortedSlots(payload.Slots);

                    command = new WireToGateForcedMechanicalRecoveryCommand(
                        envelope.MessageId,
                        envelope.SessionGeneration!.Value,
                        envelope.SentAt,
                        payload.ExceptionRecoverySessionId,
                        payload.RecoveryActionId,
                        payload.DemandId,
                        payload.ForcedRecoveryGeneration,
                        payload.Slots,
                        payload.CommandContentSha256);
                    return true;
                }
            case "CapabilitySnapshotRequested":
            case "SafetyStateChanged":
            case "SafetyStateSnapshotRequested":
            case "ExceptionRecoverySessionRequested":
            case "ExceptionRecoverySessionOpened":
            case "ExceptionRecoverySessionRejected":
            case "RecoveryActionAccepted":
            case "RecoveryActionRejected":
            case "ManualChargingReturnToServiceRequested":
            case "ManualChargingReturnToServiceResult":
            case "HardwareRecoveryRecordSubmitted":
            // A result whose request already gave up waiting: logged, not a reason to drop the session.
            case "HardwareRecoveryRecordResult":
            case "RecoveryActionSubmitted":
            case "LoadCorrectionRequested":
            case "LoadCorrectionRejected":
            case "LoadCompensationRequested":
            case "LoadCompensationRejected":
            case "LoadCancellationStartRequested":
            case "LoadCancellationAuthorization":
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

    private static void ValidateHardwareRecoveryRecord(HardwareRecoveryRecordSubmittedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireUuid(payload.RecordId, nameof(payload.RecordId));
        RequireUuid(payload.ExceptionRecoverySessionId, nameof(payload.ExceptionRecoverySessionId));
        RequireUuid(payload.RecoveryActionId, nameof(payload.RecoveryActionId));
        ArgumentNullException.ThrowIfNull(payload.Operator);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.Operator.OperatorId);
        ValidateSortedSlots(payload.Slots);
        if (payload.AdministratorRole is not ("MAINTENANCE_ADMINISTRATOR" or "SYSTEM_ADMINISTRATOR")
            || !IsNonEmptyDistinct(payload.ChecksPerformed)
            || !IsNonEmptyDistinct(payload.ActionsPerformed)
            || payload.Observations is not { Count: > 0 }
            || payload.Observations.Any(string.IsNullOrEmpty))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }

        static bool IsNonEmptyDistinct(IReadOnlyList<string>? items) =>
            items is { Count: > 0 }
            && items.All(item => !string.IsNullOrEmpty(item))
            && items.Distinct(StringComparer.Ordinal).Count() == items.Count;
    }

    private static void ValidateManualChargingRequest(
        ManualChargingReturnToServiceRequestedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireUuid(payload.RequestId, nameof(payload.RequestId));
        if (payload.Administrator is null
            || string.IsNullOrWhiteSpace(payload.Administrator.OperatorId)
            || payload.Administrator.VerificationMethod is not ("BADGE" or "SESSION")
            || payload.Administrator.VerifiedAt == default
            || payload.AdministratorRole is not ("MAINTENANCE_ADMINISTRATOR" or "SYSTEM_ADMINISTRATOR")
            || string.IsNullOrWhiteSpace(payload.Reason)
            || payload.ObservedBatteryPercent is < 0 or > 100
            || payload.ObservedBatteryPercent is double batteryPercent
                && (double.IsNaN(batteryPercent) || double.IsInfinity(batteryPercent)))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }
    }

    private static void ValidateLoadCancellationStartRequested(
        LoadCancellationStartRequestedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireUuid(payload.CancellationId, nameof(payload.CancellationId));
        RequireUuid(payload.DemandId, nameof(payload.DemandId));
        if (payload.SlotOperationAttemptId is not null)
        {
            RequireUuid(payload.SlotOperationAttemptId, nameof(payload.SlotOperationAttemptId));
        }

        ValidateOperatorContext(payload.Operator);
        if (string.IsNullOrWhiteSpace(payload.Reason))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }
    }

    private static void ValidateLoadCancellationAuthorization(
        LoadCancellationStartRequestedPayload request,
        LoadCancellationAuthorizationPayload authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        RequireUuid(authorization.CancellationId, nameof(authorization.CancellationId));
        RequireUuid(authorization.DemandId, nameof(authorization.DemandId));
        if (!string.Equals(authorization.CancellationId, request.CancellationId, StringComparison.Ordinal)
            || !string.Equals(authorization.DemandId, request.DemandId, StringComparison.Ordinal)
            || authorization.SlotOperationAttemptId != request.SlotOperationAttemptId
            || authorization.Decision is not ("AUTHORIZED" or "REJECTED")
            || authorization.Slots is null
            || authorization.Slots.Count > 8
            || authorization.Slots.Any(slot => slot is < 1 or > 8)
            || authorization.Slots.Distinct().Count() != authorization.Slots.Count
            || !authorization.Slots.SequenceEqual(authorization.Slots.Order()))
        {
            throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
        }

        if (authorization.Problem is not null)
        {
            ValidateProblem(authorization.Problem);
        }
    }

    private static void ValidateLoadCompensationRequested(
        LoadCompensationRequestedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireUuid(payload.RecoveryActionId, nameof(payload.RecoveryActionId));
        RequireUuid(payload.ExceptionRecoverySessionId, nameof(payload.ExceptionRecoverySessionId));
        RequireUuid(payload.DemandId, nameof(payload.DemandId));
        RequireUuid(payload.SlotOperationAttemptId, nameof(payload.SlotOperationAttemptId));
        ValidateOperatorContext(payload.Operator);
    }

    private static void ValidateLoadCorrectionRequested(
        LoadCorrectionRequestedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireUuid(payload.CorrectionId, nameof(payload.CorrectionId));
        RequireUuid(payload.DemandId, nameof(payload.DemandId));
        RequireUuid(payload.SlotOperationAttemptId, nameof(payload.SlotOperationAttemptId));
        ValidateSortedSlots(payload.Slots);
        ValidateOperatorContext(payload.Operator);
        if (string.IsNullOrWhiteSpace(payload.Reason))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }
    }

    private static void ValidateLoadCancellationResult(
        LoadCancellationResultPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireUuid(payload.CancellationId, nameof(payload.CancellationId));
        RequireUuid(payload.DemandId, nameof(payload.DemandId));
        if (payload.SlotOperationAttemptId is not null)
        {
            RequireUuid(payload.SlotOperationAttemptId, nameof(payload.SlotOperationAttemptId));
        }

        if (payload.OverallOutcome is not ("ALL_EMPTY" or "FAILED" or "UNKNOWN"))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }

        ValidateSlotResults(payload.SlotResults, minimumCount: 0);
    }

    private static void ValidateLoadCompensationResult(
        LoadCompensationResultPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireUuid(payload.RecoveryActionId, nameof(payload.RecoveryActionId));
        RequireUuid(payload.DemandId, nameof(payload.DemandId));
        RequireUuid(payload.SlotOperationAttemptId, nameof(payload.SlotOperationAttemptId));
        if (payload.OverallOutcome is not ("ALL_EMPTY" or "FAILED" or "UNKNOWN"))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }

        ValidateSlotResults(payload.SlotResults);
    }

    private static void ValidateLoadCorrectionResult(
        LoadCorrectionResultPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireUuid(payload.CorrectionId, nameof(payload.CorrectionId));
        RequireUuid(payload.DemandId, nameof(payload.DemandId));
        RequireUuid(payload.SlotOperationAttemptId, nameof(payload.SlotOperationAttemptId));
        if (payload.OverallOutcome is not ("COMPLETED" or "FAILED" or "UNKNOWN"))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }

        ValidateSlotResults(payload.SlotResults);
    }

    private static void ValidateFaultCargoRecoveryResult(
        FaultCargoRecoveryResultPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireUuid(payload.ExceptionRecoverySessionId, nameof(payload.ExceptionRecoverySessionId));
        RequireUuid(payload.RecoveryActionId, nameof(payload.RecoveryActionId));
        RequireUuid(payload.DemandId, nameof(payload.DemandId));
        RequireUuid(payload.HandoffId, nameof(payload.HandoffId));
        if (payload.OverallOutcome is not ("HANDED_OFF" or "FAILED" or "UNKNOWN"))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }

        ValidateSlotResults(payload.SlotResults);
        ValidateOperatorContext(payload.Operator);
    }

    /// <summary>
    /// Validates <c>ForcedMechanicalRecoveryResult</c> against its schema before it is made
    /// durable.
    /// </summary>
    /// <remarks>
    /// The two proof flags are checked for <c>false</c> rather than simply written as <c>false</c>
    /// at the one call site.  The schema pins them with <c>{"const": false}</c>, so a caller that
    /// ever passed <c>true</c> would be building a message the control server must reject; catching
    /// it here keeps that failure on this side of the wire, where the journal has not yet recorded
    /// a claim the vehicle cannot support.
    /// </remarks>
    private static void ValidateForcedMechanicalRecoveryResult(
        ForcedMechanicalRecoveryResultPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireUuid(payload.ExceptionRecoverySessionId, nameof(payload.ExceptionRecoverySessionId));
        RequireUuid(payload.RecoveryActionId, nameof(payload.RecoveryActionId));
        if (payload.ForcedRecoveryGeneration < 0
            || payload.Outcome is not ("MECHANICALLY_ISOLATED" or "FAILED" or "UNKNOWN")
            || payload.ElectronicEmptyProven
            || payload.VehicleReadyProven)
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }

        ValidateSortedSlots(payload.Slots);
        ValidateOperatorContext(payload.Operator);
    }

    /// <summary>
    /// Validates a <c>slotResults</c> array against the frozen schema, with the lower bound the
    /// message's own.
    /// </summary>
    /// <remarks>
    /// <c>minItems</c> is 1 on every result message except <c>LoadCancellationResult</c>, where the
    /// 2.0.0 candidate lowered it to 0: a cancellation that arrives before any slot was opened has
    /// nothing to report per slot, and inventing an entry would be the vehicle claiming an
    /// observation it never made. The parameter exists so the difference is stated once per call
    /// site rather than by loosening the rule for everyone.
    /// </remarks>
    private static void ValidateSlotResults(
        IReadOnlyList<WireToGateSlotResultPayload> results,
        int minimumCount = 1)
    {
        if (results is null
            || results.Count < minimumCount
            || results.Count > 8
            || results.Select(result => result.SlotNo).Distinct().Count() != results.Count
            || results.Any(result =>
                result.SlotNo is < 1 or > 8
                || result.Outcome is not ("COMPLETED" or "FAILED" or "NOT_STARTED" or "UNKNOWN")
                || result.FinalPhysicalState is not ("EMPTY" or "OCCUPIED" or "UNKNOWN")
                || result.LockState is not ("LOCKED" or "UNLOCKED" or "UNKNOWN")
                || result.UnlockOutputState is not ("RESET" or "ACTIVE" or "UNKNOWN")
                || result.ReasonCodes is null
                || result.ReasonCodes.Distinct().Count() != result.ReasonCodes.Count
                || result.ReasonCodes.Any(code => !IsProtocolErrorCode(code))))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }
    }

    private static void ValidateOperatorContext(WireToGateOperatorContextPayload context)
    {
        if (context is null
            || string.IsNullOrWhiteSpace(context.OperatorId)
            || context.VerificationMethod is not ("BADGE" or "SESSION")
            || context.VerifiedAt == default)
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }
    }

    private static ManualChargingReturnToServiceResultPayload DeserializeManualChargingResult(
        WireToGateEnvelope envelope)
    {
        if (envelope.CorrelationId is null
            || !Guid.TryParseExact(envelope.CorrelationId, "D", out _))
        {
            throw new InvalidDataException("CORRELATION_INVALID");
        }

        if (!envelope.Payload.TryGetProperty("requestId", out _)
            || !envelope.Payload.TryGetProperty("outcome", out _)
            || !envelope.Payload.TryGetProperty("problem", out _)
            || !envelope.Payload.TryGetProperty("vehicleBusinessStateRevision", out _))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }

        ManualChargingReturnToServiceResultPayload payload =
            WireToGateProtocolSerializer.DeserializePayload<ManualChargingReturnToServiceResultPayload>(envelope);
        RequireUuid(payload.RequestId, nameof(payload.RequestId));
        if (payload.Outcome is not ("RETURNED_TO_ELIGIBILITY_EVALUATION" or "REJECTED")
            || payload.VehicleBusinessStateRevision < 0)
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }

        if (payload.Problem is not null)
        {
            ValidateProblem(payload.Problem);
        }

        return payload;
    }

    private bool RememberManualChargingResult(
        WireToGateEnvelope envelope,
        ManualChargingReturnToServiceResultPayload result)
    {
        string fingerprint = WireToGateProtocolSerializer.ComputePayloadContentSha256(envelope);
        if (_completedManualChargingResultFingerprints.TryGetValue(
            result.RequestId,
            out string? previousFingerprint))
        {
            if (!string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("BUSINESS_ID_CONTENT_CONFLICT");
            }

            return true;
        }

        if (_completedManualChargingResultFingerprints.TryAdd(result.RequestId, fingerprint))
        {
            return false;
        }

        if (_completedManualChargingResultFingerprints.TryGetValue(
            result.RequestId,
            out previousFingerprint)
            && string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return true;
        }

        throw new InvalidDataException("BUSINESS_ID_CONTENT_CONFLICT");
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
            leg.StopPurposeCategory,
            leg.DemandId,
            leg.PublicStationFunction,
            leg.Sequence,
            leg.StationId,
            leg.MapId,
            leg.State);

    /// <summary>
    /// The <c>loadingPhase</c> object exactly as the frozen schema declares it, including the
    /// <c>if/then/else</c> that ties <c>closedReason</c> to <c>state</c>.
    /// </summary>
    /// <remarks>
    /// The conditional is the schema's own, not a narrowing of it: <c>closedReason</c> is a string
    /// when <c>state</c> is <c>CLOSED</c> and <c>null</c> for every other state. A whole-object
    /// <c>null</c> is legal and means the vehicle has no loading phase right now.
    /// </remarks>
    private static bool IsSchemaLegalLoadingPhase(WireToGateLoadingPhasePayload? phase)
    {
        if (phase is null)
        {
            return true;
        }

        if (phase.State is not
            ("LOADING" or "CARGO_HOLDING_WAIT" or "VEHICLE_FULL" or "CLOSED"))
        {
            return false;
        }

        return phase.State is "CLOSED"
            ? phase.ClosedReason is ("VEHICLE_FULL" or "CARGO_HOLDING_TIMEOUT"
                or "WAITING_STATION_YIELD" or "PLANNED_LOADING_COMPLETE")
            : phase.ClosedReason is null;
    }

    private static void ValidateVehicleBusinessState(VehicleBusinessStateSnapshotPayload payload)
    {
        if (payload.VehicleBusinessStateRevision < 0
            || payload.Readiness is not ("READY" or "RECOVERY_REQUIRED")
            || payload.ActivePurpose is not (null
                or "TRANSPORT" or "CHARGING" or "CLEARING_MAINTENANCE" or "IDLE_RETURN")
            || payload.BatteryState is not
                ("SUFFICIENT" or "LOW" or "UNKNOWN" or "MANDATORY_CHARGE")
            || payload.ChargingCycleState is not ("NOT_CHARGING" or "ALLOCATED" or "EN_ROUTE"
                or "CHARGING" or "COMPLETE" or "UNABLE_TO_CHARGE" or "UNKNOWN")
            || !IsSchemaLegalLoadingPhase(payload.LoadingPhase)
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
        or "FORCED_RECOVERY_GENERATION_STALE"
        or "SLOT_CONFIGURATION_VERIFICATION_FAILED"
        or "SLOT_CONFIGURATION_FINGERPRINT_MISMATCH"
        or "RECOVERY_DEMAND_NOT_BLOCKED"
        or "RECOVERY_EVENT_MISMATCH"
        or "RECOVERY_DEMAND_MISMATCH"
        or "RECOVERY_OPERATOR_MISMATCH"
        or "RECOVERY_ACTION_ALREADY_SELECTED"
        or "RECOVERY_OPERATION_NOT_FOUND"
        or "PROVEN_RECOVERY_CHECKPOINT_REQUIRED"
        or "RECOVERY_ACTION_REQUIRED"
        or "RECOVERY_RESULT_REQUIRED"
        // Registered by the 2.0.0 candidate.  The three rejection codes are the control server's
        // verdicts on a sublot entry this vehicle forwarded; OPERATOR_TIMEOUT is never produced
        // here -- past the station departure deadline this onboard reopens rather than settling a
        // determinate failure -- but it is still a protocol code, and this predicate answers "is
        // this a protocol code", not "does this build emit it".
        or "SUBLOT_NOT_IN_DISPATCH_SCOPE"
        or "SUBLOT_BOX_COUNT_UNAVAILABLE"
        or "PACKAGE_CAPACITY_UNRESOLVED"
        or "OPERATOR_TIMEOUT";

    /// <summary>
    /// Checks an inbound worklist snapshot, and is <b>stricter than the frozen v2 schema on one
    /// count</b>: <c>items.maxItems</c> went to 8 and this still refuses more than one item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The item count is a business narrowing, not schema conformance -- a legal v2 payload
    /// carrying two items would be answered with <c>PROTOCOL_SCHEMA_INVALID</c>, which is the wrong
    /// verdict for it. It is left in place because the projection above it assumes a single demand
    /// (<c>WireToGateJourneySnapshot.CanAcceptSublot</c> requires exactly one item), so widening the
    /// check without widening that is how a crash gets introduced. It belongs to batch 7
    /// (multi-demand worklists, <c>8005-agv-onboard-hmi#61</c>); the control server emits at most
    /// one item until then, so it is not reachable.
    /// </para>
    /// <para>
    /// <c>workType</c> is checked against the schema's own enum -- the six MES literals -- and
    /// nothing narrower. Batch 6 (<c>8005-agv-onboard-hmi#115</c>) widened it from
    /// <c>WIRE_TO_GATE</c> alone: nothing above this reads <c>workType</c> to decide anything, so
    /// the widening opens no crash path, and a <c>STAGING_TO_WIRE</c> journey must not lock the
    /// session the way <c>8005-agv-onboard-hmi#38</c> did.
    /// </para>
    /// </remarks>
    internal static void ValidateCurrentStopWorklist(CurrentStopWorklistSnapshotPayload payload)
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
                || item.WorkType is not ("DIE_TO_WIRE_STAGING" or "DIE_TO_OVEN" or "WIRE_TO_GATE"
                    or "WIRE_TO_OPTICAL" or "STAGING_TO_WIRE" or "WIRE_TO_NITROGEN")
                || item.StopRole is not ("PICKUP" or "DROPOFF")
                || item.ExpectedBasketCount is < 1 or > 8))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }
    }

    /// <summary>
    /// Checks an inbound plan snapshot, and is <b>stricter than the frozen v2 schema on one
    /// count</b>: <c>legs.maxItems</c> went from 2 to 9 and this still refuses more than two.
    /// </summary>
    /// <remarks>
    /// Same shape of narrowing as the item count in <see cref="ValidateCurrentStopWorklist"/> and
    /// the same treatment: a three-leg plan is legal v2 and would be answered
    /// <c>PROTOCOL_SCHEMA_INVALID</c>. It belongs to batch 7 (multi-leg plans,
    /// <c>8005-agv-onboard-hmi#61</c>); the control server emits at most two legs until then, so the
    /// narrowing is not reachable. These two counts are the only narrowings left on the inbound
    /// journey snapshots.
    /// </remarks>
    private static void ValidateUpcomingStopPlan(UpcomingStopPlanSnapshotPayload payload)
    {
        if (payload.PlanRevision < 0
            || payload.Legs is null
            || payload.Legs.Count > 2
            || payload.Legs.Any(leg => leg is null)
            || payload.Legs.Select(leg => leg.Sequence).Distinct().Count() != payload.Legs.Count
            || payload.Legs.OrderBy(leg => leg.Sequence).Select((leg, index) => leg.Sequence == index + 1).Any(valid => !valid)
            || payload.Legs.Any(leg =>
                leg is null
                || !IsUuid(leg.MovementLegId)
                || leg.LegType is not (null or "TO_PICKUP" or "TO_DROPOFF")
                || leg.StopPurposeCategory is not ("BUSINESS" or "WAITING_POINT" or "CHARGER")
                || leg.DemandId is not null && !IsUuid(leg.DemandId)
                || leg.PublicStationFunction is not (null
                    or "WIRE_STAGING" or "OVEN" or "GATE" or "OPTICAL" or "NITROGEN")
                || string.IsNullOrWhiteSpace(leg.StationId)
                || string.IsNullOrWhiteSpace(leg.MapId)
                || leg.State is not ("PLANNED" or "ACTIVE" or "ARRIVED" or "COMPLETED" or "BLOCKED")))
        {
            throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        }
    }

    private static bool IsUuid(string? value) =>
        value is not null && Guid.TryParseExact(value, "D", out _);

    /// <summary>
    /// The content digest of the slot configuration this vehicle is actually running, required by
    /// protocol v2 on every <c>CapabilitySnapshot</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it is a digest of.</b> The two configured version names plus the slot numbers the
    /// onboard runtime actually exposes, rendered canonically and hashed. Those three are what
    /// "the active slot configuration" consists of on this build: the model the slots follow, the
    /// configuration selected for them, and which slots exist. Change any of them and the digest
    /// changes; change nothing and it is stable across restarts, which is what makes it usable as
    /// an identity rather than a nonce.
    /// </para>
    /// <para>
    /// <b>The slot numbers are invariant today</b> -- <see cref="CreateSlotStates"/> refuses
    /// anything but eight lockers, so that term is always <c>1..8</c> and the digest reduces to the
    /// two configured strings. It is in the input anyway because the term that would silently stop
    /// mattering if the slot model ever changed is exactly the one worth hashing, and because a
    /// digest whose inputs are stated is checkable while one whose inputs are implied is not.
    /// </para>
    /// <para>
    /// <b>What it deliberately is not.</b> It is not a verification result. <c>FP-C7</c>
    /// (configuration activation governance) is scheduled into batch 3 with slice
    /// <c>FP-IS-14</c>, and until it lands nothing on this end verifies that the physical slots
    /// match the configuration they claim -- <c>SlotConfigurationActivationCommand</c> and
    /// <c>SlotConfigurationActivationResult</c> are unimplemented and pinned as such in
    /// <c>ProtocolMessageSurfaceArchitectureTests</c>. When that slice lands, the digest's inputs
    /// are the thing to revisit, and the value will change.
    /// </para>
    /// <para>
    /// <b>Why not a constant.</b> The field is required and typed <c>Sha256</c>, so a fixed
    /// sixty-four-character string would be schema-valid and mean nothing -- the exact shape of
    /// "the identity moved to v2 but the payload did not" that this switch was supposed to close.
    /// The control server does not read the field today, so nothing outside this repository would
    /// have noticed.
    /// </para>
    /// </remarks>
    private static string ActiveSlotConfigurationFingerprint(
        WireToGateSessionOptions options,
        IReadOnlyList<ProtocolSlotState> slotStates)
    {
        string canonical =
            $"slotModelVersion={options.SlotModelVersion}\n"
            + $"activeSlotConfigurationVersion={options.ActiveSlotConfigurationVersion}\n"
            + $"slotNos={string.Join(',', slotStates.Select(slot => slot.SlotNo).Order())}\n";
        return WireToGateProtocolSerializer.ComputeSha256(Encoding.UTF8.GetBytes(canonical));
    }

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

    private WireToGateSafetySummaryPayload CreateSafetySummary(IoSnapshot snapshot) =>
        WireToGateSafetyEvaluator.Evaluate(
            snapshot,
            _vehicleSafetySignalProvider.Read(),
            _clock.Now,
            _ioSnapshotMaxAge,
            _vehicleSafetyMaxAge,
            _vehicleSafetyClockSkewTolerance);

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
            Stream stream = client.GetStream();
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
        Interlocked.Exchange(ref _receiveLoopGeneration, 0);
        _receiveStopping?.Cancel();
        _receiveStopping = null;
        _receiveFailure = null;
        foreach (KeyValuePair<string, TaskCompletionSource<WireToGateEnvelope>> waiter in _responseWaiters)
        {
            waiter.Value.TrySetException(new IOException("WIRE_TO_GATE连接已关闭。"));
        }
        _responseWaiters.Clear();
        _completedManualChargingResultFingerprints.Clear();

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
        if (generation is long current)
        {
            Volatile.Write(ref _lastSessionGeneration, current);
        }

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
        string ActiveSlotConfigurationFingerprint,
        IReadOnlyList<ProtocolSlotState> SlotStates,
        bool SupportsBatchUnlock,
        int OnboardJournalFormatVersion);

    private sealed record SlotConfigurationActivationCommandPayload(
        string ActivationId,
        string TargetSlotConfigurationVersion,
        string TargetSlotConfigurationFingerprint,
        string? ExpectedActiveSlotConfigurationVersion,
        OperatorContextPayload Administrator,
        DateTimeOffset IssuedAt);

    private sealed record OperatorContextPayload(
        string OperatorId,
        string VerificationMethod,
        DateTimeOffset VerifiedAt);

    private sealed record SlotConfigurationActivationResultPayload(
        string ActivationId,
        string Outcome,
        ProtocolProblem? Problem,
        string ActiveSlotConfigurationVersion,
        string ActiveSlotConfigurationFingerprint,
        DateTimeOffset VerifiedAt);

    private sealed record OnboardAlarmSnapshotPayload(
        long AlarmSnapshotRevision,
        DateTimeOffset ObservedAt,
        IReadOnlyList<WireAlarmEntry> Alarms);

    private sealed record WireAlarmEntry(
        string AlarmId,
        string Code,
        string Severity,
        DateTimeOffset RaisedAt,
        string SubjectType,
        string? SubjectId,
        string? DisplayMessage);

    private sealed record SafetyStateSnapshotPayload(
        long SafetyStateVersion,
        DateTimeOffset ObservedAt,
        WireToGateSafetySummaryPayload Safety,
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
