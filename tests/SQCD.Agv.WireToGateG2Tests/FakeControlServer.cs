using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    private readonly Dictionary<string, (long Generation, long Revision, string ContentSha256)> _appliedSnapshots
        = new();
    private readonly Dictionary<string, (string Payload, long Generation)> _acceptedSafetyStateChanges =
        new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private long _sessionGeneration;
    private static readonly string[] OpenRecoverySessionAllowedActions =
        ["RESUME_AFTER_REPAIR", "FORCED_MECHANICAL_RECOVERY"];

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

    /// <summary>
    /// 收下 OperationResult、在 DurableAck 发出去之前断开连接：服务端已经把结果收下了，车却听不到。
    /// 这正是真装置 L2 场景 real-onboard-durable-ack-lost 用代理打开的那扇窗
    /// （8005-agv-control-server#33）。
    /// </summary>
    public bool DropBeforeOperationResultAck { get; set; }

    /// <summary>
    /// 跨同一车载实例的多个会话保留已采纳的快照修订号，于是重连时同修订号不同内容的快照会被判
    /// SNAPSHOT_REVISION_CONTENT_CONFLICT。真服务端不这样做——它只在一个会话之内比对修订号——所以
    /// 除非这条测试要证的就是「车载端收到这个 ProtocolProblem 之后 fail-closed」，否则别打开。
    /// </summary>
    public bool RetainSnapshotRevisionsAcrossSessions { get; set; }

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

    /// <summary>
    /// Sends the drop-off half of a journey: <c>stopRole "DROPOFF"</c> and
    /// <c>legType "TO_DROPOFF"</c>.
    /// </summary>
    /// <remarks>
    /// These are the two values protocol v2 renamed -- v1 called them <c>GATE</c> and
    /// <c>TO_GATE</c> -- and the onboard's inbound validators checked for the v1 spellings until
    /// 2026-09-09. Every other snapshot this double sends is the pick-up half, whose values did not
    /// change, so without this flag the whole suite stayed green while the onboard would have
    /// refused every drop-off the v2 control server sends.
    /// </remarks>
    public bool SendDropoffStopSnapshots { get; set; }

    public bool ReplayJourneySnapshotsWithStableIdentity { get; set; }

    /// <summary>
    /// The <c>stationDepartureDeadlineAt</c> this fake puts on every worklist snapshot.
    /// </summary>
    /// <remarks>
    /// Required and nullable since protocol 2.0.0, and <c>null</c> by default because that is what
    /// the control server sends when a stop carries no deadline for continuing to load -- the case
    /// every existing scenario here is written for. A test that needs a countdown sets it.
    /// </remarks>
    public DateTimeOffset? StationDepartureDeadlineAt { get; set; }

    /// <summary>
    /// The <c>operationSessionId</c> this fake puts on the worklist snapshot and on the sublot
    /// entry request, so the two agree the way the real server makes them agree.
    /// </summary>
    /// <remarks>
    /// <c>null</c> -- no operation session -- is what the existing scenarios here run under, and
    /// the worklist schema allows it. The entry request's own <c>operationSessionId</c> is not
    /// nullable, so a test that drives an entry sets this.
    /// </remarks>
    public string? OperationSessionId { get; set; }

    /// <summary>
    /// When set, a <c>SublotEntryRequested</c> naming these sublots follows the journey snapshots.
    /// </summary>
    /// <remarks>
    /// A set of 1 to 8, per the 2.0.0 schema. Protocol 1.0.0 had a single <c>expectedSublot</c> and
    /// a <c>demandId</c> here; both are gone, and the vehicle checks membership instead.
    /// </remarks>
    public IReadOnlyList<string>? SublotEntryExpectedSublots { get; set; }

    /// <summary>
    /// When set, every <c>SublotSubmitted</c> is answered with a <c>SublotRejected</c> carrying this
    /// reason code.
    /// </summary>
    /// <remarks>
    /// The 2.0.0 rejection names the refused sublot itself and allows a <c>null</c> demandId,
    /// because <c>SUBLOT_NOT_IN_DISPATCH_SCOPE</c> is by definition a sublot with no demand to name
    /// it against. The vehicle-side display of this is <c>8005-agv-onboard-hmi#77</c>
    /// (<c>SublotRejectedAfterEntryG2Tests</c>); what this fake owes is the shape.
    /// </remarks>
    public string? RejectSublotSubmissionsWith { get; set; }

    /// <summary>
    /// When set, the <c>rejectedSublot</c> this fake puts on its rejections instead of the sublot the
    /// vehicle submitted.
    /// </summary>
    /// <remarks>
    /// The vehicle trims what it submits, so echoing the submission can never produce the
    /// whitespace-only value the schema's <c>minLength: 1</c> still allows. This is how a test gets
    /// that legal-but-odd value onto the wire.
    /// </remarks>
    public string? RejectedSublotOverride { get; set; }

    /// <summary>
    /// The <c>currentWorklistRevision</c> this fake puts on its rejections. The entry request it
    /// sends is at revision 1, so the default says "the worklist did not move".
    /// </summary>
    /// <remarks>
    /// The vehicle keeps its entry request when the rejection names the revision the request was
    /// made at and drops it otherwise (<c>8005-agv-onboard-hmi#77</c>), so both sides of that rule
    /// need a way onto the wire.
    /// </remarks>
    public long RejectionWorklistRevision { get; set; } = 1;

    /// <summary>
    /// The <c>demandId</c> this fake puts on its rejections; <c>null</c>, the out-of-scope shape,
    /// by default.
    /// </summary>
    public string? RejectionDemandId { get; set; }

    /// <summary>
    /// After each <c>SublotRejected</c>, send the same entry request again, the way the real server
    /// keeps an entry open within one worklist revision.
    /// </summary>
    /// <remarks>
    /// The vehicle keeps its entry request when a rejection names the revision it was made at, and
    /// clears it otherwise (<c>8005-agv-onboard-hmi#77</c>); this is the server's own resend on top of
    /// that. Scanning the same sublot again after a rejection is exactly the case
    /// <c>businessDedupKeys: []</c> exists for.
    /// </remarks>
    public bool ResendSublotEntryRequestAfterRejection { get; set; }

    /// <summary>
    /// After each <c>SublotRejected</c>, send a <c>SlotOperationCommand</c> without waiting for another
    /// entry: the stop moves on to loading while the vehicle still holds the rejection.
    /// </summary>
    /// <remarks>
    /// A rescan already withdraws the rejection before it is sent, so this is the only way a test
    /// reaches a load starting with a rejection still on show (<c>8005-agv-onboard-hmi#77</c> review).
    /// </remarks>
    public bool SendSlotOperationCommandAfterRejection { get; set; }

    /// <summary>
    /// Drop the connection on receiving a <c>SublotSubmitted</c>, before acknowledging it, so the
    /// vehicle has to send it again on the next connection.
    /// </summary>
    public bool DropBeforeSublotSubmittedAck { get; set; }

    /// <summary>
    /// <c>SublotSubmitted</c> messageIds that arrived again carrying a different submission.
    /// </summary>
    /// <remarks>
    /// The real server binds a messageId to what it first carried (<c>ProtocolInbox</c>) and treats a
    /// different submission under the same id as a conflict. What it does not do for this message is
    /// deduplicate by business key: <c>SublotSubmitted</c> has <c>businessDedupKeys: []</c> in 2.0.0,
    /// so two submissions of the same sublot under two ids are two submissions, not a conflict. The
    /// binding here is on the payload and <c>sentAt</c> -- a reconnect replay rebinds only the session
    /// generation, so a faithful replay binds equal.
    /// </remarks>
    public IReadOnlyList<string> SublotSubmissionConflicts { get; private set; } = [];

    private readonly Dictionary<string, string> _sublotSubmissionContents = new(StringComparer.Ordinal);

    private bool BindSublotSubmission(string messageId, JsonElement root)
    {
        string content = root.GetProperty("sentAt").GetRawText() + root.GetProperty("payload").GetRawText();
        lock (_sync)
        {
            if (_sublotSubmissionContents.TryGetValue(messageId, out string? bound))
            {
                if (string.Equals(bound, content, StringComparison.Ordinal))
                {
                    return true;
                }

                SublotSubmissionConflicts = [.. SublotSubmissionConflicts, messageId];
                return false;
            }

            _sublotSubmissionContents.Add(messageId, content);
            return true;
        }
    }

    /// <summary>
    /// The <c>slotOperationAttemptId</c> this fake puts on all three recovery messages.
    /// </summary>
    /// <remarks>
    /// <b><c>null</c> by default, and that default is load-bearing.</b> On the MVP line the same
    /// field was seeded with a fixed constant, which immediately broke every case running under a
    /// different attempt -- the double stopped being able to represent "this recovery names your
    /// attempt" and "this recovery names none" as different things. A scenario that needs the server
    /// to name an attempt sets this to that attempt, and one that needs a mismatch sets it to
    /// another.
    /// </remarks>
    public string? RecoverySlotOperationAttemptId { get; set; }

    /// <summary>
    /// Per-message overrides of <see cref="RecoverySlotOperationAttemptId"/>, keyed by message type
    /// (<c>ExceptionRecoverySessionOpened</c>, <c>ExceptionRecoverySessionSnapshot</c>,
    /// <c>RecoveryActionAccepted</c>). A key present with a <c>null</c> value sends <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The real server derives all three from one source, so they agree; a disagreement between
    /// them is a server defect the vehicle has to refuse (8005-agv-program#95, commit
    /// <c>6ed3564</c>). This is how a test puts that defect on the wire.
    /// </remarks>
    public IReadOnlyDictionary<string, string?> RecoveryAttemptIdByMessageType { get; set; } =
        new Dictionary<string, string?>();

    private string? RecoveryAttemptIdFor(string messageType) =>
        RecoveryAttemptIdByMessageType.TryGetValue(messageType, out string? overridden)
            ? overridden
            : RecoverySlotOperationAttemptId;

    public bool SendSlotOperationCommandAfterRecovery { get; set; }

    /// <summary>
    /// When set, a PreDepartureSafetyCheck asking about this safety state version is sent after the
    /// recovery handshake. A version below the one the vehicle has had accepted is a check that has
    /// already expired.
    /// </summary>
    public long? PreDepartureSafetyCheckExpectedVersionAfterRecovery { get; set; }

    public const string PreDepartureSafetyCheckIdAfterRecovery = "55555555-5555-4555-8555-555555555555";

    /// <summary>恢复完成后下发一次仓位配置激活（协议 v2 消息 7）。</summary>
    public bool SendSlotConfigurationActivationAfterRecovery { get; set; }

    /// <summary>
    /// 下发的目标指纹。
    /// </summary>
    /// <remarks>
    /// 默认是那份已批准八仓事实的指纹，也就是车手上那份会算出来的值——正例走这个。要走
    /// <c>SLOT_CONFIGURATION_FINGERPRINT_MISMATCH</c> 那条分支，把它换成别的值。
    /// </remarks>
    public string SlotConfigurationActivationFingerprint { get; set; } =
        G2SlotConfigurationFixtures.Approved().Fingerprint;

    /// <summary>下发的目标版本名。</summary>
    public string SlotConfigurationActivationVersion { get; set; } = "approved-v7";

    /// <summary>车报上来的那些激活结果，按到达顺序。</summary>
    public IReadOnlyList<JsonElement> ReceivedActivationResults => _activationResults;

    private readonly List<JsonElement> _activationResults = [];

    public bool RespondToRecoveryRequests { get; set; }

    /// <summary>
    /// ExceptionRecoverySessionOpened is followed by one ExceptionRecoverySessionSnapshot per state
    /// listed here, in order, shaped the way the real server sends them ("OPEN" or "CLOSED"). Empty
    /// sends none, which is what every test did before 8005-agv-control-server#31 -- and why none of
    /// them saw how the vehicle acknowledges one.
    /// </summary>
    public IReadOnlyList<string> RecoverySessionSnapshotStatesAfterOpened { get; set; } = [];

    /// <summary>
    /// The recovery session snapshots this server wrote, exactly as they went on the wire.
    /// </summary>
    public IReadOnlyList<(string MessageId, string WireLine)> SentRecoverySessionSnapshots
    {
        get;
        private set;
    } = [];

    /// <summary>
    /// 设了就用 <c>ExceptionRecoverySessionRejected</c> 拒绝每一个恢复会话请求，原因码是这个值。
    /// </summary>
    public string? RecoverySessionRejectionReasonCode { get; set; }

    /// <summary>
    /// 带着别的字节重复到达的恢复请求 messageId。真服务端的 <c>ProtocolInbox</c> 把 messageId 绑死在
    /// 它第一次带来的整行字节上（<c>WireContentHash.Sha256(line)</c>），之后内容不同就抛
    /// <c>ProtocolContentConflictException</c> 并掐掉连接。替身照做——不照做的话，车辆复用一个
    /// messageId 在 G2 里永远是绿的，而车辆每次发送的 <c>sentAt</c> 都是新的，到了真服务端必然冲突。
    /// 取消与修正在工作流那一层撞上的 id（见 <see cref="BindRecoveryWorkflowContent"/>）也记在这里。
    /// </summary>
    public IReadOnlyList<string> RecoveryRequestConflicts { get; private set; } = [];

    private readonly Dictionary<string, string> _recoveryRequestLines = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _recoveryWorkflowContents = new(StringComparer.Ordinal);

    /// <summary>
    /// 应答 <c>LoadCancellationStartRequested</c>；不设就当这个替身的服务端不认这条消息。
    /// </summary>
    public bool RespondToLoadCancellationRequests { get; set; }

    public string LoadCancellationDecision { get; set; } = "AUTHORIZED";

    /// <summary>
    /// 授权时回给车载端的仓位。默认是在途装货那两个仓，与 <c>RecoveryVectorHarness</c> 种下的
    /// 装货操作一致。
    /// </summary>
    public IReadOnlyList<int> LoadCancellationAuthorizedSlots { get; set; } = [1, 2];

    /// <summary>
    /// 这么多次取消请求受理了却不回应答：服务端已经授权（内容已按 <c>cancellationId</c> 绑定），车辆等到
    /// <c>messageTimeout</c> 也没收下。真车上是应答途中断线或超时。
    /// </summary>
    public int LoadCancellationAuthorizationsToDrop { get; set; }

    /// <summary>
    /// 扫码前取消（请求里 <c>slotOperationAttemptId</c> 为 null）授权时回给车载端的仓位。默认空，与服务端
    /// control-server#83 的授权同形：那时还没有发过任何仓位操作，没有要证明清空的仓。测试设成非空，造的是
    /// 与车载端对不上的授权。
    /// </summary>
    public IReadOnlyList<int> LoadCancellationBeforeSublotAuthorizedSlots { get; set; } = [];

    /// <summary>
    /// 扫码前取消授权里回的 <c>slotOperationAttemptId</c>。默认 null，即照抄请求；测试设成一个 attempt，
    /// 造的是服务端说出了车载端从没收到过的仓位操作。
    /// </summary>
    public string? LoadCancellationBeforeSublotAuthorizedAttemptId { get; set; }

    /// <summary>
    /// 这么多条 <c>LoadCancellationResult</c> 收下了却不回 <c>DurableAck</c>，也不断连接：车辆等到
    /// <c>messageTimeout</c>，结果留在自己的日志里等补发。
    /// </summary>
    public int LoadCancellationResultAcksToDrop { get; set; }

    public IReadOnlyList<string> ReceivedLoadCancellationAttemptIds =>
        [.. _receivedLoadCancellationAttemptIds];

    private readonly ConcurrentQueue<string> _receivedLoadCancellationAttemptIds = new();

    private int _judgedRecoveryRequests;

    /// <summary>
    /// 已经判过的恢复请求行数，冲突的也算。车辆发出的是没有应答的请求时（修正），测试靠它等替身读完那一行。
    /// </summary>
    public int JudgedRecoveryRequests => Volatile.Read(ref _judgedRecoveryRequests);

    /// <summary>
    /// 接过另一个替身落过库的那部分记忆——<c>ProtocolInbox</c> 的行与 <c>RecoveryWorkflows</c> 的内容——
    /// 让一次车载端重启面对的仍是「同一个服务端」。
    /// </summary>
    public void AdoptDurableRecoveryMemoryFrom(FakeControlServer previous)
    {
        lock (previous._sync)
        {
            lock (_sync)
            {
                foreach ((string messageId, string line) in previous._recoveryRequestLines)
                {
                    _recoveryRequestLines[messageId] = line;
                }

                foreach ((string workflowId, string content) in previous._recoveryWorkflowContents)
                {
                    _recoveryWorkflowContents[workflowId] = content;
                }
            }
        }
    }

    private bool JudgeRecoveryRequest(
        string messageType,
        string messageId,
        string wireLine,
        JsonElement root)
    {
        try
        {
            return BindRecoveryRequestLine(messageId, wireLine)
                && BindRecoveryWorkflowContent(messageType, root);
        }
        finally
        {
            Interlocked.Increment(ref _judgedRecoveryRequests);
        }
    }

    /// <summary>
    /// messageId 之外真服务端还有一道：取消与修正落成 <c>RecoveryWorkflows</c> 行，主键是
    /// <c>cancellationId</c>/<c>correctionId</c>，之后同一个 id 带来的 payload 字节不同就抛
    /// <c>Recovery workflow id was replayed with different content.</c> 并掐连接
    /// （<c>OnboardRecoveryCoordinator.UpsertSimpleWorkflowAsync</c>）。拒绝不落行，所以只绑定会被受理的请求。
    /// 替身不照做的话，换了 messageId、却每次按下都带新 <c>verifiedAt</c> 的重试在 G2 里是绿的。
    /// </summary>
    private bool BindRecoveryWorkflowContent(string messageType, JsonElement root)
    {
        JsonElement payload = root.GetProperty("payload");
        string? workflowId = messageType switch
        {
            "LoadCancellationStartRequested" when RespondToLoadCancellationRequests
                && LoadCancellationDecision == "AUTHORIZED" =>
                payload.GetProperty("cancellationId").GetString(),
            "LoadCorrectionRequested" => payload.GetProperty("correctionId").GetString(),
            _ => null
        };
        if (workflowId is null)
        {
            return true;
        }

        string content = payload.GetRawText();
        lock (_sync)
        {
            if (_recoveryWorkflowContents.TryGetValue(workflowId, out string? boundContent))
            {
                if (string.Equals(boundContent, content, StringComparison.Ordinal))
                {
                    return true;
                }

                RecoveryRequestConflicts = [.. RecoveryRequestConflicts, workflowId];
                return false;
            }

            _recoveryWorkflowContents.Add(workflowId, content);
            return true;
        }
    }

    /// <summary>
    /// 让替身假装早就收到过这个 messageId 的恢复请求：车辆日志里存着的请求身份，服务端那边
    /// 已经带着另一份内容落过库。
    /// </summary>
    public void PreloadRecoveryRequestLine(string messageId, string wireLine)
    {
        lock (_sync)
        {
            _recoveryRequestLines[messageId] = wireLine;
        }
    }

    private bool BindRecoveryRequestLine(string messageId, string wireLine)
    {
        lock (_sync)
        {
            if (_recoveryRequestLines.TryGetValue(messageId, out string? boundLine))
            {
                if (string.Equals(boundLine, wireLine, StringComparison.Ordinal))
                {
                    return true;
                }

                RecoveryRequestConflicts = [.. RecoveryRequestConflicts, messageId];
                return false;
            }

            _recoveryRequestLines.Add(messageId, wireLine);
            return true;
        }
    }

    public bool RespondToManualChargingReturnToServiceRequests { get; set; }

    /// <summary>
    /// The vehicle business state snapshots say the vehicle is held for manual charging. The real
    /// control server publishes false today; this is what a test needs to show the onboard never
    /// clears a hold on its own authority.
    /// </summary>
    public bool ManualChargingHoldInSnapshots { get; set; }

    public string ManualChargingReturnToServiceOutcome { get; set; } =
        "RETURNED_TO_ELIGIBILITY_EVALUATION";

    public WireToGateProblemPayload? ManualChargingReturnToServiceProblem { get; set; }

    public long ManualChargingReturnToServiceVehicleBusinessStateRevision { get; set; } = 1;

    public int ManualChargingReturnToServiceResponseCopies { get; set; } = 1;

    public bool SendResumeCommandAfterRecoveryAction { get; set; }

    /// <summary>
    /// Issues the recovery vector command the accepted action calls for, immediately after
    /// <c>RecoveryActionAccepted</c>, the way the real server does once it has authorized one.
    /// </summary>
    public bool SendRecoveryVectorCommandAfterRecoveryAction { get; set; }

    /// <summary>
    /// The <c>forcedRecoveryGeneration</c> a <c>ForcedMechanicalRecoveryCommand</c> carries.
    /// </summary>
    /// <remarks>
    /// The real server bumps this when it authorizes a forced recovery and fences everything it
    /// issued under an older number.  Setting it below what the onboard has already persisted is
    /// how a test produces the stale command <c>REFUSE_STALE_FORCED_RECOVERY_GENERATION</c> is
    /// about.
    /// </remarks>
    public long ForcedRecoveryGeneration { get; set; } = 1;

    /// <summary>
    /// The <c>slotOperationAttemptId</c> the <c>commandContentSha256</c> is computed over.
    /// </summary>
    /// <remarks>
    /// The real server takes this from the persisted slot operation the recovery is scoped to; this
    /// double has no such store, so the test states it. Note that the onboard does not currently
    /// compare the digest it receives against the one it computes -- it validates the field's shape
    /// and discards it -- so getting this wrong is invisible today. It is set correctly anyway,
    /// because a double that puts a knowingly wrong digest on the wire would stop being a model of
    /// the peer the moment that comparison is turned on.
    /// </remarks>
    public string? RecoveryVectorSlotOperationAttemptId { get; set; }

    /// <summary>
    /// Issues <c>FaultCargoRecoveryCommand</c> naming this handoff instead of the one the recovery
    /// action derives, modelling a server that authorized a different handoff.
    /// </summary>
    public string? FaultCargoRecoveryHandoffIdOverride { get; set; }

    /// <summary>
    /// Issues the recovery vector command over these slots instead of the ones the recovery action
    /// named, modelling a server that authorized a different slot set.
    /// </summary>
    public IReadOnlyList<int>? RecoveryVectorSlotsOverride { get; set; }

    /// <summary>
    /// The onboard process restarts on the same journal. Its session client starts again from the
    /// configured capability and safety baselines, and the real control server answers each new
    /// session with the versions that session's snapshots reported; only a reconnect of the same
    /// running client carries the versions accepted before.
    /// </summary>
    public void SimulateOnboardProcessRestart()
    {
        lock (_sync)
        {
            _acceptedCapabilityVersion = 0;
            _acceptedSafetyStateVersion = 0;
        }
    }

    private ConnectionContext? _latestSession;

    /// <summary>
    /// Sends the <c>SlotOperationCommand</c> that <see cref="SendSlotOperationCommandAfterRecovery"/>
    /// sends -- the same attempt, under a new messageId -- once more on the latest session. The real
    /// control server's outbox does this about once a second until the <c>OperationResult</c> arrives
    /// (8005-agv-onboard-hmi#78, the <c>1086c4a</c> case).
    /// </summary>
    public Task ResendSlotOperationCommandAsync()
    {
        ConnectionContext context = Volatile.Read(ref _latestSession)
            ?? throw new InvalidOperationException("No session has been accepted yet.");
        return SendSlotOperationCommandAsync(context);
    }

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

    /// <summary>
    /// Every envelope this double put on the wire, journey snapshots included.
    /// </summary>
    /// <remarks>
    /// <see cref="SentJourneyEnvelopes"/> is the three journey snapshots only, and the tests that
    /// own snapshot revision behaviour want exactly those. A check on what the double sends has to
    /// see all of it -- the handshake, the acknowledgements and the recovery responses are just as
    /// able to drift into a shape the real control server would never send.
    /// </remarks>
    public IReadOnlyList<(int Connection, string MessageType, string MessageId, string WireLine)> SentEnvelopes
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

    private sealed class ConnectionContext : IDisposable
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

        /// <summary>
        /// One line at a time: a test can write on a session (<see cref="ResendSlotOperationCommandAsync"/>)
        /// while the connection loop is answering on it.
        /// </summary>
        public readonly SemaphoreSlim WriteGate = new(1, 1);

        public void Dispose() => WriteGate.Dispose();
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
                acceptedCapabilityVersion = _acceptedCapabilityVersion;
                acceptedSafetyStateVersion = _acceptedSafetyStateVersion;
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

                if (messageType is "ExceptionRecoverySessionRequested" or "RecoveryActionSubmitted"
                        or "LoadCancellationStartRequested" or "LoadCorrectionRequested"
                    && !JudgeRecoveryRequest(messageType, messageId, line, root))
                {
                    context.Client.Close();
                    return;
                }

                switch (messageType)
                {
                    case "SessionHello":
                        await HandleSessionHelloAsync(context, root).ConfigureAwait(false);
                        break;
                    case "CapabilitySnapshot":
                    case "SafetyStateSnapshot":
                    // 协议 v2 消息 9。真服务端收下它并回 SnapshotAppliedAck（snapshotKind
                    // ONBOARD_ALARM）；这个假服务端不跟上的话，车载端握手会卡在等 ack 上，而那是假车
                    // 与真服务端行为不一致造成的红，不是车载端的问题。
                    case "OnboardAlarmSnapshot":
                        await HandleSnapshotAsync(context, line, messageType, root).ConfigureAwait(false);
                        break;
                    case "RecoveryStateReport":
                        await HandleRecoveryStateReportAsync(context, line, root).ConfigureAwait(false);
                        break;
                    case "Heartbeat":
                        await WriteEnvelopeAsync(context, CreateHeartbeatAck(context, root)).ConfigureAwait(false);
                        break;
                    // 协议 v2 消息 8。RELIABLE，所以要 DurableAck——用 RESPONSE 就没有补报语义，断线
                    // 即丢，服务端除了猜没有别的可做，而 REQ-0264 要的恰恰是不能猜。
                    case "SlotConfigurationActivationResult":
                        lock (_sync)
                        {
                            _activationResults.Add(root.Clone());
                        }
                        await WriteEnvelopeAsync(context, CreateDurableAck(context, root)).ConfigureAwait(false);
                        break;
                    case "OperationResult":
                        if (DropBeforeOperationResultAck)
                        {
                            context.Client.Close();
                            return;
                        }

                        await WriteEnvelopeAsync(context, CreateDurableAck(context, root)).ConfigureAwait(false);
                        if (SendRecoveryRequiredReadinessAfterOperationResultAck)
                        {
                            await WriteEnvelopeAsync(
                                context,
                                CreateRecoveryRequiredSessionReadiness(context)).ConfigureAwait(false);
                        }

                        break;
                    case "SublotSubmitted" when DropBeforeSublotSubmittedAck:
                        BindSublotSubmission(messageId, root);
                        context.Client.Close();
                        return;
                    case "SublotSubmitted" when !BindSublotSubmission(messageId, root):
                        await WriteEnvelopeAsync(context, CreateProtocolProblem(
                            context,
                            messageId,
                            messageType,
                            "MESSAGE_ID_CONTENT_CONFLICT")).ConfigureAwait(false);
                        context.Client.Close();
                        return;
                    case "SublotSubmitted" when RejectSublotSubmissionsWith is { } reasonCode:
                        await WriteEnvelopeAsync(
                            context, CreateDurableAck(context, root)).ConfigureAwait(false);
                        await WriteEnvelopeAsync(context, CreateEnvelope(
                            context,
                            "SublotRejected",
                            messageId,
                            new
                            {
                                demandId = RejectionDemandId,
                                operationSessionId = root.GetProperty("payload")
                                    .GetProperty("operationSessionId").GetString(),
                                problem = new
                                {
                                    reasonCode,
                                    fieldPath = (string?)null,
                                    displayMessage = (string?)null
                                },
                                currentWorklistRevision = RejectionWorklistRevision,
                                rejectedSublot = RejectedSublotOverride
                                    ?? root.GetProperty("payload").GetProperty("sublot").GetString()
                            })).ConfigureAwait(false);
                        if (ResendSublotEntryRequestAfterRejection)
                        {
                            await SendSublotEntryRequestAsync(context).ConfigureAwait(false);
                        }

                        if (SendSlotOperationCommandAfterRejection)
                        {
                            await SendSlotOperationCommandAsync(context).ConfigureAwait(false);
                        }

                        break;
                    case "LoadCancellationResult" when LoadCancellationResultAcksToDrop > 0:
                        LoadCancellationResultAcksToDrop--;
                        break;
                    case "SublotSubmitted":
                    case "OperationProgress":
                    case "PreDepartureSafetyCheckResult":
                    case "SlotOperationCommandRejected":
                    // The five recovery results below are durable in exactly the same way as the
                    // four above; they are listed so a test can drive the whole O_TO_C surface
                    // rather than only the part an earlier test happened to need.
                    case "LoadCancellationResult":
                    case "LoadCompensationResult":
                    case "LoadCorrectionResult":
                    case "FaultCargoRecoveryResult":
                    case "ForcedMechanicalRecoveryResult":
                        await WriteEnvelopeAsync(context, CreateDurableAck(context, root)).ConfigureAwait(false);
                        break;
                    case "ExceptionRecoverySessionRequested" when RespondToRecoveryRequests:
                        await HandleRecoverySessionRequestAsync(context, root).ConfigureAwait(false);
                        break;
                    case "RecoveryActionSubmitted" when RespondToRecoveryRequests:
                        await HandleRecoveryActionSubmittedAsync(context, root).ConfigureAwait(false);
                        break;
                    case "LoadCancellationStartRequested" when RespondToLoadCancellationRequests:
                        await HandleLoadCancellationStartRequestedAsync(context, root)
                            .ConfigureAwait(false);
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
            context.Dispose();
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
            // 真服务端只在一个会话之内比对快照修订号：BeginSessionRecoveryAsync 每个新世代都会清空，
            // 所以重连之后的那次握手是从零采纳的。这里原来跨同一实例的重连一直留着记忆，正是因为这个
            // 差别，车载端「补发之后跳过快照」的握手才只在这个替身上过得去、在真服务端上过不去
            // （8005-agv-control-server#33）。
            string onboardInstanceId =
                hello.GetProperty("payload").GetProperty("onboardInstanceId").GetString() ?? string.Empty;
            if (!RetainSnapshotRevisionsAcrossSessions
                || !string.Equals(onboardInstanceId, _lastAcceptedInstanceId, StringComparison.Ordinal))
            {
                _appliedSnapshots.Clear();
            }

            _lastAcceptedInstanceId = onboardInstanceId;
        }

        context.Generation = generation;
        Volatile.Write(ref _latestSession, context);
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
        long revision = messageType switch
        {
            "CapabilitySnapshot" => snapshot.GetProperty("payload").GetProperty("capabilityVersion").GetInt64(),
            "OnboardAlarmSnapshot" =>
                snapshot.GetProperty("payload").GetProperty("alarmSnapshotRevision").GetInt64(),
            _ => snapshot.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64()
        };
        string contentSha256 = WireToGateProtocolSerializer.ComputeSha256(Encoding.UTF8.GetBytes(line));
        if (messageType == "CapabilitySnapshot")
        {
            context.CapabilityVersion = revision;
            lock (_sync)
            {
                _acceptedCapabilityVersion = revision;
            }
        }
        // 显式判 SafetyStateSnapshot，不用 else：告警快照带的是它自己的 alarmSnapshotRevision，
        // 落进 else 会把它当成安全态版本记下去，握手随后就报 HANDSHAKE_SEQUENCE_INVALID。
        else if (messageType == "SafetyStateSnapshot")
        {
            context.SafetyStateVersion = revision;
            lock (_sync)
            {
                _acceptedSafetyStateVersion = Math.Max(_acceptedSafetyStateVersion, revision);
            }
        }

        // **只有告警快照按会话代分域。**它的修订号是车载端告警板的进程内计数，车一重启就从 1 重新
        // 开始，真服务端因此按 (会话代, 序号) 这一对采纳。能力与安全态快照的修订号来自配置，跨代
        // 可比，同一修订号换了内容仍然是冲突——把它们也按代分域会让那条 fail-closed 的规则在每次
        // 重连时失效。
        long generation = messageType == "OnboardAlarmSnapshot"
            ? snapshot.GetProperty("sessionGeneration").GetInt64()
            : 0L;
        string? problemCode = null;
        bool apply;
        lock (_sync)
        {
            if (_appliedSnapshots.TryGetValue(
                messageType, out (long Generation, long Revision, string ContentSha256) applied))
            {
                bool sameGeneration = generation == applied.Generation;
                if (generation < applied.Generation || (sameGeneration && revision < applied.Revision))
                {
                    problemCode = "SNAPSHOT_REVISION_REGRESSION";
                }
                else if (sameGeneration && revision == applied.Revision && contentSha256 != applied.ContentSha256)
                {
                    problemCode = "SNAPSHOT_REVISION_CONTENT_CONFLICT";
                }

                apply = generation > applied.Generation || (sameGeneration && revision > applied.Revision);
            }
            else
            {
                apply = true;
            }

            if (apply)
            {
                _appliedSnapshots[messageType] = (generation, revision, contentSha256);
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
            snapshotKind = messageType switch
            {
                "CapabilitySnapshot" => "CAPABILITY",
                "OnboardAlarmSnapshot" => "ONBOARD_ALARM",
                _ => "SAFETY_STATE"
            },
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

            if (PreDepartureSafetyCheckExpectedVersionAfterRecovery is long expectedSafetyStateVersion)
            {
                await WriteEnvelopeAsync(context, CreateEnvelope(
                    context,
                    "PreDepartureSafetyCheck",
                    correlationId: null,
                    new
                    {
                        preDepartureSafetyCheckId = PreDepartureSafetyCheckIdAfterRecovery,
                        demandId = "11111111-1111-4111-8111-111111111111",
                        movementLegId = "22222222-2222-4222-8222-222222222222",
                        expectedSafetyStateVersion,
                        targetStationId = "ST-GATE"
                    })).ConfigureAwait(false);
            }

            if (SendSlotConfigurationActivationAfterRecovery)
            {
                await SendSlotConfigurationActivationCommandAsync(context).ConfigureAwait(false);
            }
        }

        if (drop)
        {
            context.Client.Close();
        }
    }

    /// <summary>
    /// 协议 v2 消息 7 <c>SlotConfigurationActivationCommand</c>。
    /// </summary>
    /// <remarks>
    /// 它**不带配置内容**——整个协议里没有一条消息带仓位 IO 绑定。带的是版本号与指纹，车拿自己手上那份
    /// 算指纹与之比对，相等才切换。
    /// </remarks>
    private Task SendSlotConfigurationActivationCommandAsync(ConnectionContext context) =>
        WriteEnvelopeAsync(context, CreateEnvelope(
            context,
            "SlotConfigurationActivationCommand",
            correlationId: null,
            new
            {
                activationId = "55555555-5555-4555-8555-555555555555",
                targetSlotConfigurationVersion = SlotConfigurationActivationVersion,
                targetSlotConfigurationFingerprint = SlotConfigurationActivationFingerprint,
                expectedActiveSlotConfigurationVersion = (string?)null,
                administrator = new
                {
                    operatorId = "op-g2",
                    verificationMethod = "BADGE",
                    verifiedAt = "2026-09-09T12:00:00Z"
                },
                issuedAt = "2026-09-09T12:00:00Z"
            }));

    private async Task HandleRecoverySessionRequestAsync(
        ConnectionContext context,
        JsonElement request)
    {
        JsonElement payload = request.GetProperty("payload");
        if (RecoverySessionRejectionReasonCode is { } rejectionReasonCode)
        {
            await WriteEnvelopeAsync(
                context,
                CreateEnvelope(
                    context,
                    "ExceptionRecoverySessionRejected",
                    request.GetProperty("messageId").GetString(),
                    new
                    {
                        requestId = payload.GetProperty("requestId").GetString(),
                        problem = new
                        {
                            reasonCode = rejectionReasonCode,
                            fieldPath = "payload",
                            displayMessage = "The recovery session request was refused."
                        }
                    }))
                .ConfigureAwait(false);
            return;
        }

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
                    slotOperationAttemptId = RecoveryAttemptIdFor("ExceptionRecoverySessionOpened"),
                    slots = payload.GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()).ToArray(),
                    recoverySessionRevision = 1
                }))
            .ConfigureAwait(false);

        // The real server's shapes: an OPEN session offers actions and blocks on choosing one; a
        // CLOSED one offers nothing, blocks on nothing, and is the last revision a session ever gets.
        foreach (string state in RecoverySessionSnapshotStatesAfterOpened)
        {
            bool closed = state switch
            {
                "OPEN" => false,
                "CLOSED" => true,
                _ => throw new InvalidOperationException(
                    $"No real-server shape for a {state} recovery session snapshot.")
            };
            WireToGateEnvelope snapshot = CreateEnvelope(
                context,
                "ExceptionRecoverySessionSnapshot",
                correlationId: null,
                new
                {
                    exceptionRecoverySessionId = sessionId,
                    recoverySessionRevision = closed ? 4 : 1,
                    state,
                    administratorId = "maintenance-001",
                    administratorRole = "MAINTENANCE_ADMINISTRATOR",
                    eventId = payload.GetProperty("eventId").GetString(),
                    demandId = payload.TryGetProperty("demandId", out JsonElement snapshotDemandId)
                        ? snapshotDemandId.GetString()
                        : null,
                    slotOperationAttemptId = RecoveryAttemptIdFor("ExceptionRecoverySessionSnapshot"),
                    slots = payload.GetProperty("slots").EnumerateArray()
                        .Select(item => item.GetInt32())
                        .ToArray(),
                    selectedAction = closed ? "COMPENSATE_LOAD_ALL_EMPTY" : null,
                    allowedActions = closed ? Array.Empty<string>() : OpenRecoverySessionAllowedActions,
                    blockingFacts = closed
                        ? []
                        : new[]
                        {
                            new
                            {
                                reasonCode = "RECOVERY_ACTION_REQUIRED",
                                subjectType = "EXCEPTION_RECOVERY_SESSION",
                                subjectId = sessionId
                            }
                        }
                });
            string line = WireToGateProtocolSerializer.Serialize(snapshot);
            lock (_sync)
            {
                SentRecoverySessionSnapshots = [.. SentRecoverySessionSnapshots, (snapshot.MessageId, line)];
            }

            await WriteEnvelopeAsync(context, snapshot).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 应答 <c>LoadCancellationStartRequested</c>。授权时回的 <c>slots</c> 取
    /// <see cref="LoadCancellationAuthorizedSlots"/>——在途取消的车载端拿它与自己那条装货操作的
    /// 仓位逐个比对，对不上按 <c>RECOVERY_RESPONSE_SCOPE_MISMATCH</c> 处理。
    /// </summary>
    private async Task HandleLoadCancellationStartRequestedAsync(
        ConnectionContext context,
        JsonElement request)
    {
        JsonElement payload = request.GetProperty("payload");
        string cancellationId = payload.GetProperty("cancellationId").GetString()!;
        string demandId = payload.GetProperty("demandId").GetString()!;
        JsonElement attempt = payload.GetProperty("slotOperationAttemptId");
        string? attemptId = attempt.ValueKind == JsonValueKind.Null ? null : attempt.GetString();
        _receivedLoadCancellationAttemptIds.Enqueue(attemptId ?? "(null)");
        if (LoadCancellationAuthorizationsToDrop > 0)
        {
            LoadCancellationAuthorizationsToDrop--;
            return;
        }

        bool authorized = LoadCancellationDecision == "AUTHORIZED";
        // 扫码前取消（attempt 为 null）按 control-server#83 应答：授权的仓位集合为空，attempt 照抄 null。
        bool beforeSublot = attemptId is null;
        await WriteEnvelopeAsync(
            context,
            CreateEnvelope(
                context,
                "LoadCancellationAuthorization",
                request.GetProperty("messageId").GetString(),
                new
                {
                    cancellationId,
                    decision = LoadCancellationDecision,
                    demandId,
                    slotOperationAttemptId = beforeSublot
                        ? LoadCancellationBeforeSublotAuthorizedAttemptId
                        : attemptId,
                    slots = !authorized
                        ? []
                        : beforeSublot
                            ? LoadCancellationBeforeSublotAuthorizedSlots
                            : LoadCancellationAuthorizedSlots,
                    problem = authorized
                        ? null
                        : new
                        {
                            reasonCode = "ACTION_NOT_ALLOWED_IN_STATE",
                            fieldPath = "payload.demandId",
                            displayMessage = "当前状态不允许取消装货。"
                        }
                }))
            .ConfigureAwait(false);
    }

    private async Task HandleRecoveryActionSubmittedAsync(
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
                    slotOperationAttemptId = RecoveryAttemptIdFor("RecoveryActionAccepted"),
                    acceptedAction = payload.GetProperty("action").GetString(),
                    recoverySessionRevision = 2,
                    acceptedAt = DateTimeOffset.UtcNow
                }))
            .ConfigureAwait(false);

        if (SendRecoveryVectorCommandAfterRecoveryAction)
        {
            await SendRecoveryVectorCommandAsync(context, payload, sessionId, actionId)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Issues the command the accepted recovery action authorizes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two derivations here restate what the real control server computes, because the onboard
    /// verifies both and a double that skipped them would be testing nothing.  The
    /// <c>handoffId</c> is <c>SHA-256("{recoveryActionId}|fault-cargo-handoff")</c> shaped as a
    /// UUIDv5 -- <c>OnboardRecoveryCoordinator.StableGuid(workflow.WorkflowId, ...)</c>, where
    /// <c>WorkflowId</c> is the recovery action id.  The <c>commandContentSha256</c> is
    /// <c>RecoveryCommandHash.ForRecoveryAction</c>: the five parts joined by <c>|</c>, hashed, and
    /// lowercased.
    /// </para>
    /// <para>
    /// The onboard derives the same two values independently and neither end sends the other its
    /// inputs, so these copies are not a shortcut around the product code -- they are the peer's
    /// half of an agreement that nothing in either repository currently pins.
    /// </para>
    /// </remarks>
    private async Task SendRecoveryVectorCommandAsync(
        ConnectionContext context,
        JsonElement submitted,
        string sessionId,
        string actionId)
    {
        string action = submitted.GetProperty("action").GetString()!;
        string? demandId = submitted.TryGetProperty("demandId", out JsonElement demand)
            && demand.ValueKind == JsonValueKind.String
                ? demand.GetString()
                : null;
        int[] slots = RecoveryVectorSlotsOverride is { } overridden
            ? [.. overridden]
            : [.. submitted.GetProperty("slots").EnumerateArray().Select(item => item.GetInt32())];
        string attemptId = RecoveryVectorSlotOperationAttemptId ?? string.Empty;

        switch (action)
        {
            case "FAULT_CARGO_HANDOFF":
                await WriteEnvelopeAsync(
                    context,
                    CreateEnvelope(
                        context,
                        "FaultCargoRecoveryCommand",
                        null,
                        new
                        {
                            exceptionRecoverySessionId = sessionId,
                            recoveryActionId = actionId,
                            demandId,
                            slots,
                            handoffId = FaultCargoRecoveryHandoffIdOverride
                                ?? FakeControlServerIdentifiers.StableUuid(
                                    $"{actionId}|fault-cargo-handoff"),
                            commandContentSha256 = FakeControlServerIdentifiers.RecoveryActionContentSha256(
                                actionId, demandId ?? string.Empty, attemptId, slots, 0)
                        }))
                    .ConfigureAwait(false);
                break;
            case "COMPENSATE_LOAD_ALL_EMPTY":
                await WriteEnvelopeAsync(
                    context,
                    CreateEnvelope(
                        context,
                        "LoadCompensationCommand",
                        null,
                        new
                        {
                            recoveryActionId = actionId,
                            exceptionRecoverySessionId = sessionId,
                            demandId,
                            slotOperationAttemptId = attemptId,
                            slots,
                            expectedFinalPhysicalState = "EMPTY",
                            commandContentSha256 =
                                FakeControlServerIdentifiers.LoadCompensationContentSha256(
                                    actionId,
                                    demandId ?? string.Empty,
                                    attemptId,
                                    slots)
                        }))
                    .ConfigureAwait(false);
                break;
            case "FORCED_MECHANICAL_RECOVERY":
                await WriteEnvelopeAsync(
                    context,
                    CreateEnvelope(
                        context,
                        "ForcedMechanicalRecoveryCommand",
                        null,
                        new
                        {
                            exceptionRecoverySessionId = sessionId,
                            recoveryActionId = actionId,
                            demandId,
                            forcedRecoveryGeneration = ForcedRecoveryGeneration,
                            slots,
                            commandContentSha256 = FakeControlServerIdentifiers.RecoveryActionContentSha256(
                                actionId,
                                demandId ?? string.Empty,
                                attemptId,
                                slots,
                                ForcedRecoveryGeneration)
                        }))
                    .ConfigureAwait(false);
                break;
        }
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
        bool replayedIntoLaterSession;
        lock (_sync)
        {
            bool accepted = _acceptedSafetyStateChanges.TryGetValue(
                messageId,
                out (string Payload, long Generation) first);
            conflict = accepted && !string.Equals(first.Payload, payloadJson, StringComparison.Ordinal);
            replayedIntoLaterSession = accepted && !conflict && first.Generation < context.Generation;
            if (!accepted)
            {
                _acceptedSafetyStateChanges.Add(messageId, (payloadJson, context.Generation));
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
        // 真服务端对「补发进后一个会话」的报文按首次受理作答（RebindDurableAckAsync）：只回 ack，
        // 因为一个还没走完握手的世代没有任何就绪变化可宣告（8005-agv-control-server#33）。
        if (SendReadinessAfterSafetyStateChangedAck && !replayedIntoLaterSession)
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
                // The wire value, not the control server's internal DEPARTURE_SAFETY_NOT_READY: the real
                // server maps it through ProtocolErrorCodes.ToSessionReadinessReasonCode, and only
                // DEPARTURE_UNSAFE is in the protocol's error registry.
                reasonCodes = ready ? Array.Empty<string>() : ["DEPARTURE_UNSAFE"],
                acceptedCapabilityVersion = Math.Max(context.CapabilityVersion, context.AcceptedCapabilityVersion),
                acceptedSafetyStateVersion = Math.Max(context.SafetyStateVersion, context.AcceptedSafetyStateVersion),
                vehicleBusinessStateRevision = 0
            });
    }

    /// <summary>
    /// The entry methods <c>SublotEntryRequested</c> freezes as a <c>const</c> array in its schema.
    /// </summary>
    private static readonly string[] FrozenEntryMethods = ["SCANNER", "KEYBOARD"];

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

    private async Task SendSlotOperationCommandAsync(ConnectionContext context)
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
        string stopRole = SendDropoffStopSnapshots ? "DROPOFF" : "PICKUP";
        string legType = SendDropoffStopSnapshots ? "TO_DROPOFF" : "TO_PICKUP";
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
                activePurpose = "TRANSPORT",
                manualChargingHold = ManualChargingHoldInSnapshots,
                batteryState = "SUFFICIENT",
                chargingCycleState = "NOT_CHARGING",
                loadingPhase = (object?)null,
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
                operationSessionId = OperationSessionId,
                stationDepartureDeadlineAt = StationDepartureDeadlineAt,
                items = new[]
                {
                    new
                    {
                        demandId,
                        transportDemandKey = "TD-001",
                        sublot = "SUBLOT-001",
                        workType = "WIRE_TO_GATE",
                        stopRole,
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
                legs = new[] { Leg(movementLegId, legType, demandId, "ACTIVE") }
            })).ConfigureAwait(false);

        await SendSublotEntryRequestAsync(context).ConfigureAwait(false);

        if (SendJourneyRevisionConflict)
        {
            await WriteJourneyEnvelopeAsync(context, CreateJourneyEnvelope(
                context,
                "CurrentStopWorklistSnapshot",
                new
                {
                    stationId = "ST-01",
                    worklistRevision = 1,
                    operationSessionId = OperationSessionId,
                    stationDepartureDeadlineAt = StationDepartureDeadlineAt,
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

    private async Task SendSublotEntryRequestAsync(ConnectionContext context)
    {
        if (SublotEntryExpectedSublots is not { } expectedSublots)
        {
            return;
        }

        await WriteEnvelopeAsync(context, CreateEnvelope(
            context,
            "SublotEntryRequested",
            correlationId: null,
            new
            {
                operationSessionId = OperationSessionId,
                stationId = "ST-01",
                worklistRevision = 1,
                expectedSublots,
                entryMethods = FrozenEntryMethods,
                expiresOnRevisionChange = true
            })).ConfigureAwait(false);
    }

    /// <summary>
    /// One <c>UpcomingStopPlanSnapshot</c> leg in the shape protocol v2 froze.
    /// </summary>
    /// <remarks>
    /// The three plan snapshots this double sends used to spell the leg out inline, so v2's three
    /// new required properties had to be added in three places and the shape could drift between
    /// them. One builder makes the next protocol change one edit.
    /// </remarks>
    private static object Leg(string movementLegId, string legType, string demandId, string state) =>
        new
        {
            movementLegId,
            legType,
            stopPurposeCategory = "BUSINESS",
            demandId,
            publicStationFunction = (string?)null,
            sequence = 1,
            stationId = "ST-01",
            mapId = "MAP-01",
            state
        };

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

        Record(context, envelope, wireLine);
        await WriteLineAsync(context, wireLine).ConfigureAwait(false);
    }

    private async Task SendDemandAcceptanceSnapshotsAsync(ConnectionContext context)
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
                legs = new[] { Leg(movementLegId, "TO_PICKUP", demandId, "ACTIVE") }
            })).ConfigureAwait(false);
        await WriteEnvelopeAsync(context, CreateEnvelope(
            context,
            "CurrentStopWorklistSnapshot",
            null,
            new
            {
                stationId = "ST-01",
                worklistRevision = 1,
                operationSessionId = OperationSessionId,
                stationDepartureDeadlineAt = StationDepartureDeadlineAt,
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
                legs = new[] { Leg(movementLegId, "TO_PICKUP", demandId, "ARRIVED") }
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
                expectedProtocolVersion = WireToGateRelease.ProtocolVersion,
                expectedProfileId = WireToGateRelease.ProfileId,
                expectedProtocolReleaseManifestSha256 = WireToGateRelease.ManifestSha256
            });

    private static async Task WriteLineAsync(ConnectionContext context, string wireLine)
    {
        await context.WriteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await context.Writer.WriteLineAsync(wireLine).ConfigureAwait(false);
        }
        finally
        {
            context.WriteGate.Release();
        }
    }
    private async Task WriteEnvelopeAsync(ConnectionContext context, WireToGateEnvelope envelope)
    {
        string wireLine = WireToGateProtocolSerializer.Serialize(envelope);
        Record(context, envelope, wireLine);
        await WriteLineAsync(context, wireLine).ConfigureAwait(false);
    }

    private void Record(ConnectionContext context, WireToGateEnvelope envelope, string wireLine)
    {
        lock (_sync)
        {
            var sent = SentEnvelopes.ToList();
            sent.Add((context.ConnectionIndex, envelope.MessageType, envelope.MessageId, wireLine));
            SentEnvelopes = sent;
        }
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

/// <summary>
/// The two identifier derivations the control server and this onboard each perform independently.
/// </summary>
/// <remarks>
/// Neither end sends the other its inputs: the control server derives the handoff id from the
/// recovery workflow and the content digest from the authorized scope, and the onboard derives both
/// again from what it already holds. That agreement is load-bearing -- a fault cargo command whose
/// handoff id or digest differs is refused -- and nothing in either repository pins the two
/// implementations together, so this double has to restate the server's half to stand in for it at
/// all. Keeping the two here, named for what they mirror, is the closest this repository can get to
/// making that dependency visible from the test side.
/// </remarks>
internal static class FakeControlServerIdentifiers
{
    /// <summary>Mirrors <c>OnboardRecoveryCoordinator.StableGuid(identity, purpose)</c>.</summary>
    public static string StableUuid(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString("D");
    }

    /// <summary>Mirrors <c>WireToGateRecoveryCommandHash.ForLoadCompensation</c>: four parts, no
    /// forced recovery generation.</summary>
    public static string LoadCompensationContentSha256(
        string recoveryActionId,
        string demandId,
        string slotOperationAttemptId,
        IReadOnlyList<int> slots) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
                '|',
                recoveryActionId,
                demandId,
                slotOperationAttemptId,
                JsonSerializer.Serialize(slots)))))
            .ToLowerInvariant();

    /// <summary>Mirrors <c>RecoveryCommandHash.ForRecoveryAction</c>.</summary>
    public static string RecoveryActionContentSha256(
        string recoveryActionId,
        string demandId,
        string slotOperationAttemptId,
        IReadOnlyList<int> slots,
        long forcedRecoveryGeneration) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
                '|',
                recoveryActionId,
                demandId,
                slotOperationAttemptId,
                JsonSerializer.Serialize(slots),
                forcedRecoveryGeneration.ToString(CultureInfo.InvariantCulture)))))
            .ToLowerInvariant();
}
