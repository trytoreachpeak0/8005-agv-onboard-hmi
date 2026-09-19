using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf;

public sealed record WireToGateRecoveryOptions(
    bool ResumeAfterRepairEnabled,
    string AuthenticationProofEnvironmentVariable,
    string AdministratorRole,
    string VerificationMethod);

/// <summary>
/// Wires formal server commands to the safe physical executor.  The legacy rule
/// gateway remains available for development compatibility, but this service is
/// the production WIRE_TO_GATE path for server-frozen slot commands and safety
/// checks. Unsupported recovery commands remain fail-closed and are projected to
/// the operator instead of disappearing into the file log.
/// </summary>
public sealed partial class WireToGateBusinessService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WireToGateSessionService _session;
    private readonly IIoModuleClient _ioModule;
    private readonly IAppLogger _logger;
    private readonly IClock _clock;
    private readonly Func<bool> _vehicleStoppedProvider;
    private readonly IVehicleSafetySignalProvider _vehicleSafetySignalProvider;
    private readonly IObservableVehicleSafetySignalProvider? _observableVehicleSafetySignalProvider;
    private readonly string _operatorIdEnvironmentVariable;
    private readonly TimeSpan _ioSnapshotMaxAge;
    private readonly TimeSpan _vehicleSafetyMaxAge;
    private readonly TimeSpan _vehicleSafetyClockSkewTolerance;
    private readonly WireToGateRecoveryOptions _recoveryOptions;
    private readonly WireToGateSlotOperationExecutor _executor;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _taskGate = new();
    private readonly object _operationAttemptGate = new();
    private readonly HashSet<Task> _tasks = [];
    private readonly HashSet<string> _operationAttempts = new(StringComparer.Ordinal);
    private readonly OperatorEventDeduplicator _operatorEventDeduplicator = new();
    private readonly SemaphoreSlim _safetySendGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryRequestGate = new(1, 1);
    private WireToGateSublotEntryRequest? _currentEntryRequest;
    private WireToGateSublotRejection? _currentSublotRejection;
    private WireToGateExceptionRecoverySessionSnapshot? _recoverySessionSnapshot;
    private readonly object _recoverySessionAttemptGate = new();
    private (string ExceptionRecoverySessionId, string? SlotOperationAttemptId)? _recoverySessionAttempt;
    private string? _inconsistentRecoverySessionId;
    private WireToGateHmiOperationSnapshot? _currentOperationSnapshot;
    private readonly SlotExpectedActionWaitTracker _expectedActionWait = new();
    private LoadAwaitingOperator? _loadAwaitingOperator;
    private SafetyChangeWork? _pendingSafetyChange;
    private string? _lastSafetySignature;
    private long? _lastSafetyGeneration;
    private long _nextSafetyStateVersion;
    private int _safetyRefreshPending;
    private int _safetyRefreshWorkerActive;
    private bool _started;
    private bool _disposed;

    public WireToGateBusinessService(
        WireToGateSessionService session,
        IIoModuleClient ioModule,
        IAppLogger logger,
        IClock clock,
        Func<bool> vehicleStoppedProvider,
        WireToGateSlotOperationExecutorOptions executorOptions,
        string operatorIdEnvironmentVariable,
        IVehicleSafetySignalProvider? vehicleSafetySignalProvider = null,
        TimeSpan? vehicleSafetyMaxAge = null,
        TimeSpan? vehicleSafetyClockSkewTolerance = null,
        WireToGateRecoveryOptions? recoveryOptions = null)
    {
        _session = session;
        _ioModule = ioModule;
        _logger = logger;
        _clock = clock;
        _vehicleStoppedProvider = vehicleStoppedProvider;
        _vehicleSafetySignalProvider = vehicleSafetySignalProvider
            ?? new DelegateVehicleSafetySignalProvider(vehicleStoppedProvider);
        _observableVehicleSafetySignalProvider = _vehicleSafetySignalProvider
            as IObservableVehicleSafetySignalProvider;
        _operatorIdEnvironmentVariable = operatorIdEnvironmentVariable;
        _ioSnapshotMaxAge = executorOptions.IoSnapshotMaxAge;
        _vehicleSafetyMaxAge = vehicleSafetyMaxAge ?? _ioSnapshotMaxAge;
        _vehicleSafetyClockSkewTolerance = vehicleSafetyClockSkewTolerance ?? TimeSpan.Zero;
        _recoveryOptions = recoveryOptions ?? new WireToGateRecoveryOptions(
            ResumeAfterRepairEnabled: false,
            AuthenticationProofEnvironmentVariable: "CONTROL_SERVER_RECOVERY_PROOF",
            AdministratorRole: "MAINTENANCE_ADMINISTRATOR",
            VerificationMethod: "CONFIGURED_PROOF");
        if (_vehicleSafetyMaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(vehicleSafetyMaxAge));
        }
        if (_vehicleSafetyClockSkewTolerance < TimeSpan.Zero
            || _vehicleSafetyClockSkewTolerance >= _vehicleSafetyMaxAge)
        {
            throw new ArgumentOutOfRangeException(nameof(vehicleSafetyClockSkewTolerance));
        }
        _nextSafetyStateVersion = session.Current.SafetyStateVersion + 1;
        if (string.IsNullOrWhiteSpace(operatorIdEnvironmentVariable))
        {
            throw new ArgumentException("operatorIdEnvironmentVariable不能为空。", nameof(operatorIdEnvironmentVariable));
        }
        _executor = new WireToGateSlotOperationExecutor(
            ioModule,
            session.Journal,
            clock,
            executorOptions,
            IsReopenPermitted);
        _vectorExecutor = new WireToGateRecoveryVectorExecutor(
            ioModule,
            session.Journal,
            clock,
            executorOptions);
    }

    public event EventHandler<ValueChangedEventArgs<WireToGateSublotEntryRequest>>? SublotEntryRequested;

    public event EventHandler<ValueChangedEventArgs<WireToGateOperatorEvent>>? OperatorEventPublished;

    /// <remarks>
    /// Closed while a cancellation before any sublot is open: the control server starts no load
    /// while its cancellation record is open (control-server#83), so an entry submitted then would
    /// leave the operator waiting for a slot operation that never comes.
    /// </remarks>
    public bool CanSubmitSublot =>
        _session.Current.Readiness == WireToGateSessionReadiness.Ready
        && Volatile.Read(ref _currentEntryRequest) is not null
        && !IsLoadCancellationBeforeSublotOpen;

    /// <summary>
    /// The sublots the server's outstanding entry request will accept, or <c>null</c> when there is
    /// no outstanding request.
    /// </summary>
    /// <remarks>
    /// A set rather than a single value since protocol 2.0.0. A one-demand dispatch puts one
    /// element in it, but nothing downstream may read it as "the expected sublot".
    /// </remarks>
    public IReadOnlyList<string>? ExpectedSublots =>
        Volatile.Read(ref _currentEntryRequest)?.ExpectedSublots;

    /// <summary>
    /// The server's latest refusal of an entered sublot, or <c>null</c> once the operator has entered
    /// again, a slot operation has started, or the stop's operation session has moved on.
    /// </summary>
    public WireToGateSublotRejection? CurrentSublotRejection =>
        Volatile.Read(ref _currentSublotRejection);

    /// <summary>
    /// Read-only projection of the latest operation progress emitted by the
    /// business service. It never grants authority to perform business or IO
    /// actions.
    /// </summary>
    public WireToGateHmiOperationSnapshot? CurrentOperationSnapshot =>
        Volatile.Read(ref _currentOperationSnapshot);

    /// <summary>
    /// The slot the running LOAD or UNLOAD has been waiting on the operator for since its first unlock, for
    /// the expected-action-overdue alarm (REQ-0358, onboard-hmi#109); <c>null</c> when nothing waits.
    /// </summary>
    public SlotExpectedActionWait? CurrentExpectedActionWait => _expectedActionWait.Current;

    /// <summary>
    /// Whether an exception recovery session is already open or being acted on, so the reason it was opened
    /// with is the one that stands: a resume in an open session carries the persisted reason, and a retried
    /// recovery vector must repeat its first content exactly. The HMI locks the reason box while this holds,
    /// instead of taking a reason it would silently drop (onboard-hmi#109 review).
    /// </summary>
    public bool RecoveryReasonAlreadyGiven =>
        Volatile.Read(ref _recoverySessionSnapshot) is { State: not "CLOSED" }
        || Volatile.Read(ref _lastRecoveryState).RecoveryVector is not null;

    /// <summary>
    /// The countdown line's text once the station departure deadline has passed, or <c>null</c> for the
    /// generic one (8005-agv-onboard-hmi#78): a load cancellation in progress, or a load whose door is
    /// open or being reopened. The wording is <see cref="WireToGateStationDeadlineText"/>'s.
    /// </summary>
    public string? DescribeExpiredStationDeadline(StationDepartureCountdownContext context) =>
        WireToGateStationDeadlineText.CountdownOverride(
            context,
            IsLoadCancellationOpen,
            Volatile.Read(ref _loadAwaitingOperator)?.Slots);

    /// <summary>
    /// A load cancellation of either kind is between the operator's press and its settlement: asked and
    /// unanswered, or authorized and not yet acknowledged.
    /// </summary>
    private bool IsLoadCancellationOpen
    {
        get
        {
            WireToGateRecoveryState state = Volatile.Read(ref _lastRecoveryState);
            return state.PendingLoadCancellation is not null
                || state.RecoveryVector?.VectorType == WireToGateRecoveryVectorTypes.LoadCancellation;
        }
    }

    public bool CanRequestResumeAfterRepair
    {
        get
        {
            WireToGateExceptionRecoverySessionSnapshot? snapshot =
                Volatile.Read(ref _recoverySessionSnapshot);
            string? operatorId = Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable);
            string? proof = Environment.GetEnvironmentVariable(
                _recoveryOptions.AuthenticationProofEnvironmentVariable);
            WireToGateSessionSnapshot session = _session.Current;
            bool bootstrap = snapshot is null || snapshot.State == "CLOSED";
            bool active = IsActiveRecoverySnapshot(snapshot)
                && snapshot!.SelectedAction is not "RESUME_AFTER_REPAIR";
            return _recoveryOptions.ResumeAfterRepairEnabled
                && session.Connected
                && ((bootstrap
                        && session.Readiness == WireToGateSessionReadiness.RecoveryRequired)
                    || (active
                        && session.Readiness is (WireToGateSessionReadiness.Ready
                            or WireToGateSessionReadiness.RecoveryRequired)))
                && !string.IsNullOrWhiteSpace(operatorId)
                && !string.IsNullOrWhiteSpace(proof);
        }
    }

    /// <param name="reason">
    /// The administrator's reason for the exception recovery session (CP-0005 section 5, onboard-hmi#109).
    /// Blank keeps the fixed text every request carried before the reason could be entered.
    /// </param>
    public async Task<bool> RequestResumeAfterRepairAsync(
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _recoveryRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_recoveryOptions.ResumeAfterRepairEnabled)
            {
                PublishOperatorResponse(
                    "RECOVERY_BLOCKED",
                    "RESUME_AFTER_REPAIR功能未启用。请由维护人员完成配置后再操作。 ");
                return false;
            }

            WireToGateSessionSnapshot session = _session.Current;
            if (!session.Connected
                || session.Readiness is not (WireToGateSessionReadiness.Ready
                    or WireToGateSessionReadiness.RecoveryRequired))
            {
                throw new InvalidOperationException("WIRE_TO_GATE_NOT_READY");
            }

            WireToGateExceptionRecoverySessionSnapshot? recoverySnapshot =
                Volatile.Read(ref _recoverySessionSnapshot);
            bool activeRecovery = IsActiveRecoverySnapshot(recoverySnapshot);
            if (!activeRecovery
                && recoverySnapshot is not null
                && recoverySnapshot.State != "CLOSED")
            {
                throw new InvalidOperationException("RECOVERY_SESSION_NOT_READY");
            }

            if (!activeRecovery && session.Readiness != WireToGateSessionReadiness.RecoveryRequired)
            {
                throw new InvalidOperationException("RECOVERY_SESSION_NOT_READY");
            }

            if (activeRecovery
                && recoverySnapshot!.SelectedAction is "RESUME_AFTER_REPAIR")
            {
                PublishOperatorResponse(
                    "RECOVERY_ACTION_SUBMITTED",
                    "当前恢复会话已经提交恢复申请，等待服务端下发原操作续作命令。 ");
                return false;
            }

            if (activeRecovery
                && !recoverySnapshot!.AllowedActions.Contains(
                    "RESUME_AFTER_REPAIR",
                    StringComparer.Ordinal))
            {
                PublishOperatorResponse(
                    "RECOVERY_BLOCKED",
                    "当前恢复会话不允许恢复原仓位操作。 ");
                return false;
            }

            string operatorId = Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable)
                ?? throw new InvalidOperationException("WIRE_TO_GATE_OPERATOR_NOT_READY");
            string proof = Environment.GetEnvironmentVariable(
                _recoveryOptions.AuthenticationProofEnvironmentVariable)
                ?? throw new InvalidOperationException("RECOVERY_AUTHENTICATION_REQUIRED");
            if (string.IsNullOrWhiteSpace(operatorId) || string.IsNullOrWhiteSpace(proof))
            {
                throw new InvalidOperationException("RECOVERY_AUTHENTICATION_REQUIRED");
            }

            WireToGateRecoveryState state = await _session.Journal
                .ReadRecoveryStateAsync(cancellationToken)
                .ConfigureAwait(false);
            WireToGateRecoveryOperationContext context = state.OperationContext
                ?? throw new InvalidDataException("RECOVERY_OPERATION_CONTEXT_MISSING");
            if (activeRecovery
                && state.RecoveryOperatorId is not null
                && !string.Equals(state.RecoveryOperatorId, operatorId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("RECOVERY_OPERATOR_MISMATCH");
            }
            // A resume is by definition about the attempt still in flight, so there is no settled
            // fallback to choose between here -- but the server's name still has to agree with it,
            // the same way it does on every other recovery path.
            if (activeRecovery)
            {
                ObserveRecoverySessionAttempt(
                    recoverySnapshot!.ExceptionRecoverySessionId,
                    recoverySnapshot.SlotOperationAttemptId);
                RequireSameSlotOperationAttempt(
                    recoverySnapshot.SlotOperationAttemptId, context);
            }

            if (state.UnsettledSlotOperationAttemptId != context.SlotOperationAttemptId
                || activeRecovery
                    && (recoverySnapshot!.DemandId != context.DemandId
                        || !recoverySnapshot.Slots.SequenceEqual(context.Slots)))
            {
                throw new InvalidDataException("RECOVERY_SCOPE_MISMATCH");
            }

            if (!activeRecovery && state.ExceptionRecoverySessionId is not null)
            {
                throw new InvalidOperationException("RECOVERY_SESSION_STATE_PENDING");
            }

            // Without an open session this press is a new request with a new id, and nothing an
            // earlier press persisted binds it: that press was refused, or never reached the server,
            // or opened a session whose snapshot has not arrived -- and then the server refuses this
            // one with RECOVERY_SESSION_ALREADY_OPEN. Reusing the old id could only replay a refusal
            // or conflict with the bytes the server kept (see RequestRecoveryActionVectorCoreAsync).
            string requestId = activeRecovery
                ? state.RecoverySessionRequestId ?? Guid.NewGuid().ToString("D")
                : Guid.NewGuid().ToString("D");
            string eventId = activeRecovery ? recoverySnapshot!.EventId : requestId;
            string recoveryReason = (activeRecovery ? state.RecoveryReason : null)
                ?? ReasonOrDefault(reason, "现场维修完成，申请恢复原仓位操作。");
            string recoveryOperatorId = (activeRecovery ? state.RecoveryOperatorId : null) ?? operatorId;
            DateTimeOffset recoveryVerifiedAt = (activeRecovery ? state.RecoveryOperatorVerifiedAt : null)
                ?? _clock.Now.ToUniversalTime();
            state = state with
            {
                RecoverySessionRequestId = requestId,
                RecoveryReason = recoveryReason,
                RecoveryOperatorId = recoveryOperatorId,
                RecoveryOperatorVerifiedAt = recoveryVerifiedAt
            };
            await _session.Journal.WriteRecoveryStateAsync(state, cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = recoveryVerifiedAt;
            WireToGateOperatorContextPayload administrator = new(
                recoveryOperatorId,
                GetProtocolVerificationMethod(),
                now);
            ExceptionRecoverySessionOpenedPayload opened;
            if (activeRecovery)
            {
                WireToGateExceptionRecoverySessionSnapshot recovery = recoverySnapshot
                    ?? throw new InvalidOperationException("RECOVERY_SESSION_NOT_READY");
                if (state.ExceptionRecoverySessionId is not null
                    && !string.Equals(
                        state.ExceptionRecoverySessionId,
                        recovery.ExceptionRecoverySessionId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("RECOVERY_SESSION_SCOPE_MISMATCH");
                }

                opened = new ExceptionRecoverySessionOpenedPayload(
                    requestId,
                    recovery.ExceptionRecoverySessionId,
                    recovery.SentAt,
                    recovery.EventId,
                    recovery.DemandId,
                    recovery.SlotOperationAttemptId,
                    recovery.Slots,
                    recovery.RecoverySessionRevision);
            }
            else
            {
                opened = await _session
                    .RequestExceptionRecoverySessionAsync(
                        requestId,
                        new ExceptionRecoverySessionRequestedPayload(
                            requestId,
                            administrator,
                            _recoveryOptions.AdministratorRole,
                            eventId,
                            context.DemandId,
                            context.Slots,
                            recoveryReason,
                            proof),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            ObserveRecoverySessionAttempt(
                opened.ExceptionRecoverySessionId, opened.SlotOperationAttemptId);
            RequireSameSlotOperationAttempt(opened.SlotOperationAttemptId, context);
            if (!string.Equals(opened.RequestId, requestId, StringComparison.Ordinal)
                || !string.Equals(opened.EventId, eventId, StringComparison.Ordinal)
                || !string.Equals(opened.DemandId, context.DemandId, StringComparison.Ordinal)
                || !opened.Slots.SequenceEqual(context.Slots))
            {
                throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
            }

            // The action keeps its id across presses -- the server deduplicates by it and records
            // nothing when it refuses -- but every send is a message of its own.
            string actionId = state.RecoveryActionId ?? Guid.NewGuid().ToString("D");
            string actionMessageId = Guid.NewGuid().ToString("D");
            state = state with
            {
                ExceptionRecoverySessionId = opened.ExceptionRecoverySessionId,
                RecoveryActionId = actionId,
                RecoveryActionRequestId = actionMessageId
            };
            await _session.Journal.WriteRecoveryStateAsync(state, cancellationToken).ConfigureAwait(false);

            RecoveryActionAcceptedPayload accepted = await _session
                .SubmitRecoveryActionAsync(
                    actionMessageId,
                    new RecoveryActionSubmittedPayload(
                        actionId,
                        opened.ExceptionRecoverySessionId,
                        "RESUME_AFTER_REPAIR",
                        opened.EventId,
                        context.DemandId,
                        context.Slots,
                        administrator,
                        recoveryReason),
                    cancellationToken)
                .ConfigureAwait(false);
            ObserveRecoverySessionAttempt(
                accepted.ExceptionRecoverySessionId, accepted.SlotOperationAttemptId);
            RequireSameSlotOperationAttempt(accepted.SlotOperationAttemptId, context);
            if (!string.Equals(accepted.RecoveryActionId, actionId, StringComparison.Ordinal)
                || !string.Equals(
                    accepted.ExceptionRecoverySessionId,
                    opened.ExceptionRecoverySessionId,
                    StringComparison.Ordinal)
                || accepted.AcceptedAction != "RESUME_AFTER_REPAIR")
            {
                throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
            }

            PublishOperatorResponse(
                "RECOVERY_ACTION_SUBMITTED",
                "恢复申请已通过服务端授权，等待下发原操作续作命令。 ");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or TimeoutException
                or InvalidOperationException
                or InvalidDataException)
        {
            string recoveryId = Volatile.Read(ref _recoverySessionSnapshot)?.ExceptionRecoverySessionId
                ?? "unknown";
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"恢复申请未执行：session={recoveryId}，reason={exception.Message}。",
                exception);
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                $"恢复申请被阻断：{exception.Message}。请检查授权、现场安全条件和服务端状态。 ");
            return false;
        }
        finally
        {
            _recoveryRequestGate.Release();
        }
    }

    public async Task<string> SubmitSublotAsync(
        string sublot,
        string entryMethod,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sublot);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryMethod);
        WireToGateSublotEntryRequest request = Volatile.Read(ref _currentEntryRequest)
            ?? throw new InvalidOperationException("WIRE_TO_GATE_JOURNEY_NOT_READY");
        if (IsLoadCancellationBeforeSublotOpen)
        {
            throw new InvalidOperationException("LOAD_CANCELLATION_IN_PROGRESS");
        }

        // Protocol 2.0.0 replaced the request's single expectedSublot and its demandId with a set.
        // The local check moved with it: the request has to still be the one this worklist asked
        // for -- same operation session, same revision, same station -- and the entry has to be a
        // member of the set. Whether an entry outside the set belongs to some other demand is the
        // control server's judgement (SUBLOT_NOT_IN_DISPATCH_SCOPE); this end never binds a demand.
        WireToGateCurrentStopWorklist? worklist =
            _session.CurrentJourney.CurrentStopWorklist;
        if (!request.EntryMethods.Contains(entryMethod, StringComparer.Ordinal)
            || worklist is null
            || !string.Equals(
                worklist.OperationSessionId,
                request.OperationSessionId,
                StringComparison.Ordinal)
            || worklist.Revision != request.WorklistRevision
            || !string.Equals(worklist.StationId, request.StationId, StringComparison.Ordinal)
            || !request.ExpectedSublots.Contains(sublot.Trim(), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("SUBLOT_NOT_IN_WORKLIST");
        }

        string operatorId = Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable)
            ?? throw new InvalidOperationException("WIRE_TO_GATE_OPERATOR_NOT_READY");
        if (string.IsNullOrWhiteSpace(operatorId))
        {
            throw new InvalidOperationException("WIRE_TO_GATE_OPERATOR_NOT_READY");
        }

        // A new entry withdraws the previous rejection from the prompt area. It is cleared before
        // sending, not after: the server may refuse this entry too, and that rejection can arrive
        // before the send returns.
        Volatile.Write(ref _currentSublotRejection, null);
        string messageId = await _session.SendSublotSubmittedAsync(
            request.OperationSessionId,
            request.StationId,
            request.WorklistRevision,
            sublot.Trim(),
            entryMethod,
            operatorId,
            "SESSION",
            _clock.Now.ToUniversalTime(),
            cancellationToken).ConfigureAwait(false);
        PublishOperatorResponse(
            "SUBLOT_SUBMITTED",
            $"子批 {sublot.Trim()} 已提交，等待服务端下发仓位操作。");
        return messageId;
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_started)
        {
            return;
        }

        _started = true;
        _session.ServerCommandReceived += OnServerCommandReceived;
        _session.StateChanged += OnSessionStateChanged;
        _ioModule.SnapshotChanged += OnIoSnapshotChanged;
        if (_observableVehicleSafetySignalProvider is not null)
        {
            _observableVehicleSafetySignalProvider.SignalChanged += OnVehicleSafetySignalChanged;
        }
        RequestSafetyStateChange();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();
        if (_started)
        {
            _session.ServerCommandReceived -= OnServerCommandReceived;
            _session.StateChanged -= OnSessionStateChanged;
            _ioModule.SnapshotChanged -= OnIoSnapshotChanged;
            if (_observableVehicleSafetySignalProvider is not null)
            {
                _observableVehicleSafetySignalProvider.SignalChanged -= OnVehicleSafetySignalChanged;
            }
            _started = false;
        }

        Task[] tasks;
        lock (_taskGate)
        {
            tasks = _tasks.ToArray();
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                "停止WIRE_TO_GATE业务执行器时仍有未完成任务。",
                exception);
        }

        _stopping.Dispose();
        _safetySendGate.Dispose();
        _recoveryRequestGate.Dispose();
        await _executor.DisposeAsync().ConfigureAwait(false);
        await _vectorExecutor.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void OnServerCommandReceived(object? sender, ValueChangedEventArgs<WireToGateServerCommand> args)
    {
        if (_disposed)
        {
            return;
        }

        Task task = HandleCommandAsync(args.Value, _stopping.Token);
        lock (_taskGate)
        {
            _tasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_taskGate)
                {
                    _tasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnSessionStateChanged(object? sender, ValueChangedEventArgs<WireToGateSessionSnapshot> args)
    {
        _operatorEventDeduplicator.ResetOnNewGeneration(args.Value.SessionGeneration);

        if (args.Value.Readiness != WireToGateSessionReadiness.Ready)
        {
            Volatile.Write(ref _currentEntryRequest, null);
        }

        if (args.Value.Readiness is WireToGateSessionReadiness.Ready
            or WireToGateSessionReadiness.RecoveryRequired)
        {
            TrackTask(RestorePendingRecoveryOperationProjectionAsync(_stopping.Token));
        }

        if (CanPublishSafetyRevision(args.Value))
        {
            RequestSafetyStateChange();
        }
    }

    private async Task RestorePendingRecoveryOperationProjectionAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            WireToGateRecoveryState state = await _session.Journal
                .ReadRecoveryStateAsync(cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _lastRecoveryState, state);

            // A forced isolation is a device fact beside whatever else is on file, not instead of it:
            // it settled its own business side when it was acknowledged, and an operation or vector on
            // other slots is restored and settled below exactly as it would be without it. The
            // executors refuse anything that touches an isolated slot (#107).
            if (state.ForcedIsolation is { } isolation)
            {
                PublishOperatorEvent(
                    $"forced-isolation-restored:{isolation.RecoveryActionId}",
                    "OPERATION_RECOVERY_REQUIRED",
                    $"{FormatSlots(isolation.PhysicallyUnknownSlots)}经强制机械取出，物理状态未知，禁止操作；修复后请提交硬件恢复记录。 ");
            }

            if (state.RecoveryVector is { } vector)
            {
                if (WireToGateRecoveryVectorTypes.IsLoadCancellationBeforeSublot(vector))
                {
                    await RestoreLoadCancellationBeforeSublotAsync(vector, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                PublishRecoveryVectorRestored(vector);
                return;
            }

            WireToGateRecoveryOperationContext? context = state.OperationContext;
            if (context is null
                || !string.Equals(
                    state.UnsettledSlotOperationAttemptId,
                    context.SlotOperationAttemptId,
                    StringComparison.Ordinal))
            {
                return;
            }

            // Only a leftover that could not be settled here is restored as unfinished. The attempt this process is
            // executing reads exactly like one in the journal, and the server answers every safety change and every
            // mid-session snapshot with a SessionReadiness that lands here (onboard-hmi#120): restoring it would stop
            // its expected-action clock and flash ONBOARD_SLOT_OPERATION_UNFINISHED on every door change.
            InterruptedOperationSettlement settlement = await TrySettleInterruptedOperationAsync(
                context,
                cancellationToken).ConfigureAwait(false);
            if (settlement is not InterruptedOperationSettlement.NotSettled)
            {
                return;
            }

            WireToGateHmiOperationSnapshot operation = new(
                context.SlotOperationAttemptId,
                context.OperationType,
                context.Slots,
                WireToGateHmiOperationStage.RecoveryRequired,
                $"上次{FormatOperationType(context.OperationType)}操作未完成：{FormatSlots(context.Slots)}，需要管理员恢复。",
                _clock.Now.ToUniversalTime());
            PublishOperatorEvent(
                $"recovery-operation-restored:{context.SlotOperationAttemptId}",
                "OPERATION_RECOVERY_REQUIRED",
                operation.Guidance,
                operation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                "恢复未完成仓位操作的界面投影失败，保持恢复入口关闭。",
                exception);
        }
    }

    /// <summary>What <see cref="TrySettleInterruptedOperationAsync"/> made of the journal's unsettled attempt.</summary>
    private enum InterruptedOperationSettlement
    {
        /// <summary>
        /// This process is executing it (or already settling it): not a leftover. Nothing is settled and nothing is
        /// restored -- whoever holds it publishes its projections (onboard-hmi#120).
        /// </summary>
        InFlight,

        /// <summary>
        /// A leftover this call dealt with: settled from the live IO, recorded as the COMPLETED result the server has
        /// since acknowledged (onboard-hmi#124), or held off by an unanswered load cancellation (resent when it is
        /// this attempt's own).
        /// </summary>
        TakenOver,

        /// <summary>
        /// Finished: a COMPLETED result is in the durable outbox and only its DurableAck is outstanding, and sending
        /// it once more did not get one either (or no session could take it). Not restored as unfinished -- the
        /// handshake replays the result (onboard-hmi#124, onboard-hmi#127).
        /// </summary>
        ResultAwaitingAck,

        /// <summary>
        /// A leftover this call cannot settle, because a result has already been given for it. It stays unfinished
        /// until an administrator recovers it.
        /// </summary>
        NotSettled
    }

    /// <summary>
    /// Settles a completed attempt whose result the server has acknowledged, for the sender that gave up waiting
    /// for that acknowledgement (onboard-hmi#124): the same journal write and gate refresh as the formal load path,
    /// and the same confirmation for the HMI.
    /// </summary>
    private async Task RecordAcknowledgedCompletedResultAsync(
        WireToGateRecoveryOperationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await _executor.MarkResultRecordedAsync(context.SlotOperationAttemptId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException exception) when (exception.Message == "SLOT_OPERATION_CONFLICT")
        {
            // The server holds this result, but the journal has moved on to the next operation since the attempt
            // was read -- that operation's command came in the meantime. Its record is not this one's to touch,
            // and the HMI shows that operation now, not this one's completion (onboard-hmi#127).
            WireToGateRecoveryState current = await ReadRecoveryStateCachedAsync(cancellationToken)
                .ConfigureAwait(false);
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"迟到的DurableAck已取得，但日志里未结算的已是另一次仓位操作，不再补记本次结果：attempt={context.SlotOperationAttemptId}，当前未结算attempt={current.UnsettledSlotOperationAttemptId ?? "无"}。");
            return;
        }

        await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
        _logger.Write(
            LogSeverity.Information,
            nameof(WireToGateBusinessService),
            $"迟到的DurableAck已取得（重连补发或重发），补记已完成的仓位操作结果：attempt={context.SlotOperationAttemptId}。");
        PublishOperatorEvent(
            $"operation-result:{context.SlotOperationAttemptId}:COMPLETED",
            "OPERATION_COMPLETED",
            $"{FormatSlots(context.Slots)}操作结果已被服务端确认。",
            new WireToGateHmiOperationSnapshot(
                context.SlotOperationAttemptId,
                context.OperationType,
                context.Slots,
                WireToGateHmiOperationStage.Completed,
                "操作完成。",
                _clock.Now.ToUniversalTime()));
    }

    /// <summary>
    /// Sends a result that is on file but unacknowledged once more, exactly as stored, when the session can take it.
    /// True once its row is acknowledged -- by this send, or meanwhile by a handshake replay.
    /// </summary>
    private async Task<bool> TryResendUnacknowledgedResultAsync(
        WireToGateRecoveryOperationContext context,
        string resultKey,
        CancellationToken cancellationToken)
    {
        if (_session.Current.Readiness is not (WireToGateSessionReadiness.Ready
            or WireToGateSessionReadiness.RecoveryRequired))
        {
            return false;
        }

        try
        {
            await _session.ResendOperationResultAsync(resultKey, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
        {
            // A reconnect during the wait may have replayed and acknowledged the row already; its readiness found
            // this attempt claimed, so the recording falls to this call.
            if (await _session.Journal
                    .ReadOutgoingByDeduplicationKeyAsync(resultKey, cancellationToken)
                    .ConfigureAwait(false) is { Acknowledged: true })
            {
                return true;
            }

            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"OperationResult重发一次后仍未收到DurableAck，等待下一次握手补发：attempt={context.SlotOperationAttemptId}。",
                exception);
            return false;
        }
    }

    /// <summary>Whether a durable <c>OperationResult</c> reports the operation <c>COMPLETED</c>.</summary>
    private static bool IsCompletedOperationResult(WireToGateDurableMessage result)
    {
        using JsonDocument document = JsonDocument.Parse(result.WireLine);
        return document.RootElement.TryGetProperty("payload", out JsonElement payload)
            && payload.TryGetProperty("overallOutcome", out JsonElement outcome)
            && outcome.ValueKind == JsonValueKind.String
            && outcome.GetString() == "COMPLETED";
    }

    /// <summary>
    /// Settles the journal's unsettled attempt from the live IO when nobody is executing it and no
    /// result has ever been sent for it (8005-agv-program#40). The three outcomes are kept apart so that
    /// no caller can mistake the attempt this process is running for one a previous process left behind
    /// (onboard-hmi#120).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Such an attempt can only come from a previous process: it unlocked a slot and died while
    /// waiting for the operator. After that both ends wait for each other -- the vehicle settles an
    /// attempt only when a command arrives, the server does not stall the journey while no result has
    /// come, and the recovery entry requires the journey stalled -- so the result has to come from
    /// the vehicle's own side.
    /// </para>
    /// <para>
    /// "Nobody is executing it" is decided from this process's own in-flight set, not from the
    /// journal: the executor is tied to the service lifetime rather than to a connection, so during a
    /// reconnect it may still be running while the journal reads exactly as it does after a restart.
    /// The server cannot tell those two apart from the wire, which is why this belongs on the
    /// vehicle. The in-flight set is claimed before the result is looked up, so a command for the
    /// same attempt cannot race this.
    /// </para>
    /// <para>
    /// An UNKNOWN settlement is recorded as pending before it is sent, exactly like the formal path:
    /// the operation stays unsettled, so every later session reports the result and replays it until
    /// something settles the operation (CV-OPERATION-RESULT-UNKNOWN-RECONCILE).
    /// </para>
    /// </remarks>
    private async Task<InterruptedOperationSettlement> TrySettleInterruptedOperationAsync(
        WireToGateRecoveryOperationContext context,
        CancellationToken cancellationToken)
    {
        string attemptId = context.SlotOperationAttemptId;
        lock (_operationAttemptGate)
        {
            if (!_operationAttempts.Add(attemptId))
            {
                return InterruptedOperationSettlement.InFlight;
            }
        }

        try
        {
            // The same deduplication key HandleSlotOperationAsync uses: once a result is in the
            // durable outbox it has either been acknowledged or is replayed by the handshake, and a
            // conclusion already given is never redone. An UNKNOWN decided mid-execution is
            // recognised here too.
            string resultKey = $"operation-result:{attemptId}";
            if (await _session.Journal
                    .ReadOutgoingByDeduplicationKeyAsync(resultKey, cancellationToken)
                    .ConfigureAwait(false) is { } sent)
            {
                // FAILED and UNKNOWN stay unfinished until an administrator recovers them. One the server has not
                // acknowledged yet is still sent once more, as below: it is what the server waits for to reconcile
                // the session, and one put on file mid-handshake missed that handshake's replay (onboard-hmi#127).
                if (!IsCompletedOperationResult(sent))
                {
                    if (!sent.Acknowledged)
                    {
                        _ = await TryResendUnacknowledgedResultAsync(context, resultKey, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return InterruptedOperationSettlement.NotSettled;
                }

                // A COMPLETED result whose DurableAck has not come back is finished work, not an unfinished
                // operation (onboard-hmi#124). While a session can take it, it is sent once more under the same
                // key and messageId: an ack lost with the link still up has nothing else to send it before the next
                // handshake, and a result put on file mid-handshake missed that handshake's replay (onboard-hmi#127).
                // Otherwise the handshake replays it, and until then the HMI keeps the RESULT_ACK_PENDING prompt.
                if (!sent.Acknowledged)
                {
                    if (!await TryResendUnacknowledgedResultAsync(context, resultKey, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return InterruptedOperationSettlement.ResultAwaitingAck;
                    }

                    await RecordAcknowledgedCompletedResultAsync(context, cancellationToken).ConfigureAwait(false);
                    return InterruptedOperationSettlement.TakenOver;
                }

                // Acknowledged since -- by the handshake's replay, which runs before the readiness that brought
                // this call here -- but never recorded, because its sender's wait for the ack ran out first.
                // Recorded now, exactly as the sender would have.
                await RecordAcknowledgedCompletedResultAsync(context, cancellationToken).ConfigureAwait(false);
                return InterruptedOperationSettlement.TakenOver;
            }

            // An unanswered load cancellation over this attempt: its conclusion is that cancellation's,
            // not this settlement's (1acb018 redone, onboard-hmi#78) -- the executor was aborted by it or
            // went with the process, and the server may already have authorized it. Resent with the first
            // press's content while this claim keeps a re-sent command for the attempt from running. One
            // left over from anything else only keeps the settlement away, as the executor does.
            if ((await _session.Journal.ReadRecoveryStateAsync(cancellationToken).ConfigureAwait(false))
                .PendingLoadCancellation is { } pending)
            {
                if (string.Equals(pending.SlotOperationAttemptId, attemptId, StringComparison.Ordinal))
                {
                    await ResendUnansweredLoadCancellationAsync(pending, cancellationToken)
                        .ConfigureAwait(false);
                }

                return InterruptedOperationSettlement.TakenOver;
            }

            WireToGateOperationExecutionResult execution = await _executor
                .SettleInterruptedAsync(cancellationToken)
                .ConfigureAwait(false);
            WireToGateOperationResultPayload payload = CreateOperationResultPayload(execution);
            WireToGateSlotOperationCommand command = context.ToCommand();
            bool completedSuccessfully = string.Equals(
                execution.OverallOutcome,
                "COMPLETED",
                StringComparison.Ordinal);
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"上次仓位操作在执行中中断，未再输出开锁，按实时IO结算：attempt={attemptId}，outcome={execution.OverallOutcome}，checkpoint={execution.JournalCheckpoint}。");
            WireToGateHmiOperationStage finalStage = completedSuccessfully
                ? WireToGateHmiOperationStage.Completed
                : WireToGateHmiOperationStage.RecoveryRequired;
            string guidance = completedSuccessfully
                ? $"上次{FormatOperationType(command.OperationType)}在执行中中断，{FormatSlots(command.Slots)}已按实时状态确认完成，正在上报结果。"
                : $"上次{FormatOperationType(command.OperationType)}在执行中中断：{FormatSlots(command.Slots)}，未再开锁，需要管理员恢复。";
            PublishOperation(command, finalStage, guidance, "interrupted-final");
            try
            {
                if (!completedSuccessfully
                    && !string.Equals(execution.JournalCheckpoint, "NONE", StringComparison.Ordinal))
                {
                    await _executor.RecordPendingResultAsync(
                        attemptId,
                        new WireToGatePendingResult(
                            "OperationResult",
                            attemptId,
                            attemptId,
                            payload.ResultContentSha256),
                        cancellationToken).ConfigureAwait(false);
                }

                // The session is RecoveryRequired at this moment -- precisely because this attempt
                // was never settled -- so this takes the send path that allows it. The message is the
                // same OperationResult under the same messageId HandleSlotOperationAsync would send.
                await _session.SendRecoveryOperationResultAsync(
                    resultKey,
                    attemptId,
                    payload,
                    cancellationToken).ConfigureAwait(false);
                if (completedSuccessfully)
                {
                    await _executor.MarkResultRecordedAsync(attemptId, cancellationToken)
                        .ConfigureAwait(false);
                    // Same refresh as the formal load path: recording the result is what makes the
                    // load correctable, and the CanRequest* gates read a cached copy.
                    await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
                }

                PublishOperatorEvent(
                    $"interrupted-operation-result:{attemptId}:{execution.OverallOutcome}",
                    completedSuccessfully ? "OPERATION_COMPLETED" : "OPERATION_RECOVERY_REQUIRED",
                    guidance,
                    new WireToGateHmiOperationSnapshot(
                        attemptId,
                        command.OperationType,
                        command.Slots,
                        finalStage,
                        guidance,
                        _clock.Now.ToUniversalTime()));
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
            {
                _logger.Write(
                    LogSeverity.Warning,
                    nameof(WireToGateBusinessService),
                    $"中断操作的结算结果暂未收到DurableAck：attempt={attemptId}。",
                    exception);
                PublishOperatorEvent(
                    $"interrupted-operation-result-pending:{attemptId}",
                    "RESULT_ACK_PENDING",
                    "中断操作的结算结果已持久化，等待服务端确认；不会再次执行仓门IO。");
            }

            return InterruptedOperationSettlement.TakenOver;
        }
        finally
        {
            lock (_operationAttemptGate)
            {
                _operationAttempts.Remove(attemptId);
            }
        }
    }

    private static string FormatOperationType(OperationType operationType) =>
        operationType == OperationType.Load ? "装货" : "卸货";

    private void OnIoSnapshotChanged(object? sender, ValueChangedEventArgs<IoSnapshot> args)
    {
        _ = args;
        RequestSafetyStateChange();
    }

    private void OnVehicleSafetySignalChanged(
        object? sender,
        ValueChangedEventArgs<VehicleSafetySignal> args)
    {
        _ = args;
        RequestSafetyStateChange();
    }

    private void RequestSafetyStateChange()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Exchange(ref _safetyRefreshPending, 1);
        if (Interlocked.CompareExchange(ref _safetyRefreshWorkerActive, 1, 0) == 0)
        {
            TrackTask(ProcessSafetyStateChangesAsync(_stopping.Token));
        }
    }

    private async Task ProcessSafetyStateChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_disposed && Interlocked.Exchange(ref _safetyRefreshPending, 0) == 1)
            {
                await QueueSafetyStateChangeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _safetyRefreshWorkerActive, 0);
            if (!_disposed
                && Volatile.Read(ref _safetyRefreshPending) == 1
                && Interlocked.CompareExchange(ref _safetyRefreshWorkerActive, 1, 0) == 0)
            {
                TrackTask(ProcessSafetyStateChangesAsync(cancellationToken));
            }
        }
    }

    private void TrackTask(Task task)
    {
        lock (_taskGate)
        {
            _tasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_taskGate)
                {
                    _tasks.Remove(completed);
                }
                if (completed.IsFaulted && completed.Exception is not null)
                {
                    _logger.Write(
                        LogSeverity.Error,
                        nameof(WireToGateBusinessService),
                        "WIRE_TO_GATE后台任务异常，相关操作已保持故障安全阻塞。",
                        completed.Exception.GetBaseException());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task QueueSafetyStateChangeAsync(CancellationToken cancellationToken)
    {
        if (_disposed || !CanPublishSafetyRevision(_session.Current))
        {
            return;
        }

        await _safetySendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || !CanPublishSafetyRevision(_session.Current))
            {
                return;
            }

            WireToGateSessionSnapshot current = _session.Current;
            if (_pendingSafetyChange is not null
                && current.SafetyStateVersion >= _pendingSafetyChange.Version)
            {
                _lastSafetySignature = _pendingSafetyChange.Signature;
                _pendingSafetyChange = null;
            }
            // 换代必须重新全量上报一次安全快照（ADR-cross-0022「连接时全量同步，变化时可靠增量」
            // 的前半句，它在重连时同样适用）。签名去重是进程内状态而会话不是：服务端重启后新会话
            // 手上没有上一代的快照，车载端进程没重启、签名照旧，那份快照就永远不会重发，服务端的
            // readiness 一直卡在 DEPARTURE_SAFETY_NOT_READY。车静止时安全签名恒定，正是它永远
            // 跨不过下面那道 return 的时候——也就是最需要重发的那一种。
            //
            // 这一段必须排在上面的 pending 对账之后：SafetyStateVersion 是进程内单调递增的，
            // 跨代不回退，所以换代之后它仍可能不小于 pending 的版本，让对账把签名恢复回去。
            // _nextSafetyStateVersion 会随换代自动重新基线（紧接着的几行），签名不会，
            // 这个不对称就是缺陷本身。
            if (_lastSafetyGeneration != current.SessionGeneration)
            {
                _lastSafetyGeneration = current.SessionGeneration;
                _lastSafetySignature = null;
            }

            _nextSafetyStateVersion = Math.Max(
                _nextSafetyStateVersion,
                checked(current.SafetyStateVersion + 1));

            SafetyEvaluation evaluation = EvaluateSafety(_ioModule.CurrentSnapshot);
            string signature = evaluation.Signature;
            if (_pendingSafetyChange is null
                && string.Equals(_lastSafetySignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            _pendingSafetyChange ??= new SafetyChangeWork(
                _nextSafetyStateVersion,
                evaluation.ObservedAt,
                evaluation.Safety,
                [1, 2, 3, 4, 5, 6, 7, 8],
                signature);
            SafetyChangeWork pending = _pendingSafetyChange;
            await _session.SendSafetyStateChangedAsync(
                pending.Version,
                pending.ObservedAt,
                pending.Safety,
                pending.AffectedSlots,
                cancellationToken).ConfigureAwait(false);
            _lastSafetySignature = pending.Signature;
            _nextSafetyStateVersion = Math.Max(
                checked(pending.Version + 1),
                checked(_session.Current.SafetyStateVersion + 1));
            _pendingSafetyChange = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Write(
                LogSeverity.Error,
                nameof(WireToGateBusinessService),
                "SafetyStateChanged发送失败，正在断开会话并以同一版本和内容重试。",
                exception);
            try
            {
                await _session.Client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception disconnectException)
            {
                _logger.Write(
                    LogSeverity.Warning,
                    nameof(WireToGateBusinessService),
                    "安全状态发送失败后断开会话时发生异常。",
                    disconnectException);
            }
        }
        finally
        {
            _safetySendGate.Release();
        }
    }

    /// <summary>
    /// Answers the server's mid-session <c>SafetyStateSnapshotRequested</c> with a snapshot of the live IO
    /// (REQ-0358, onboard-hmi#109, paired with control-server#142). The dashboard asks for one when an
    /// expected-action-overdue alarm appears, to show the administrator the lock, light curtain and unlock
    /// output readings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a request for readings, not a recovery message: before this it fell through to the recovery
    /// branch, which answered nothing and told the operator beside the stuck slot that a recovery was blocked.
    /// </para>
    /// <para>
    /// The version is the next one of the sequence <see cref="QueueSafetyStateChangeAsync"/> uses, taken under
    /// the same gate: the content differs every time, so reusing the accepted version would be a revision
    /// conflict, and a change sent concurrently must not overtake it with a lower one. A failed answer is only
    /// logged -- the server asks again on the next change, and a snapshot is advisory, so it does not cost the
    /// session the way a lost SafetyStateChanged does.
    /// </para>
    /// </remarks>
    private async Task AnswerSafetyStateSnapshotRequestAsync(CancellationToken cancellationToken)
    {
        await _safetySendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WireToGateSessionSnapshot current = _session.Current;
            if (_pendingSafetyChange is not null
                && current.SafetyStateVersion >= _pendingSafetyChange.Version)
            {
                _lastSafetySignature = _pendingSafetyChange.Signature;
                _pendingSafetyChange = null;
            }

            // A change still unacknowledged is waiting to be resent, under its own version and content, on
            // the session its failure is tearing down. A snapshot with a higher version now would make that
            // resend a regression, so the request goes unanswered; the next session starts with a full one.
            if (_pendingSafetyChange is not null)
            {
                _logger.Write(
                    LogSeverity.Warning,
                    nameof(WireToGateBusinessService),
                    "有未确认的SafetyStateChanged等待重发，本次不回应服务端的快照请求。");
                return;
            }

            long version = Math.Max(_nextSafetyStateVersion, checked(current.SafetyStateVersion + 1));
            // Spent before sending, whatever happens next: an ack that times out may be for a snapshot the
            // server did apply, and the next safety state under this same version with other content would be
            // a revision conflict. A skipped version is not a regression.
            _nextSafetyStateVersion = checked(version + 1);
            if (!await _session.PublishSafetyStateSnapshotAsync(version, cancellationToken).ConfigureAwait(false))
            {
                _logger.Write(
                    LogSeverity.Warning,
                    nameof(WireToGateBusinessService),
                    "会话此刻不能发送SafetyStateSnapshot，未回应服务端的快照请求。");
                return;
            }

            _nextSafetyStateVersion = Math.Max(
                _nextSafetyStateVersion,
                checked(_session.Current.SafetyStateVersion + 1));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or TimeoutException
            or InvalidDataException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                "回应服务端的SafetyStateSnapshot请求失败。",
                exception);
        }
        finally
        {
            _safetySendGate.Release();
        }
    }

    private static bool CanPublishSafetyRevision(WireToGateSessionSnapshot session) =>
        session.Connected
        && session.SessionGeneration is not null
        && session.Readiness is WireToGateSessionReadiness.Ready
            or WireToGateSessionReadiness.RecoveryRequired;

    private static bool IsActiveRecoverySnapshot(
        WireToGateExceptionRecoverySessionSnapshot? snapshot) =>
        snapshot is not null
        && snapshot.State is "OPEN" or "ACTION_SELECTED" or "EXECUTING"
        && snapshot.AllowedActions.Contains("RESUME_AFTER_REPAIR", StringComparer.Ordinal);

    private async Task HandleCommandAsync(
        WireToGateServerCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (command)
            {
                case WireToGateSublotEntryRequest sublot:
                    Volatile.Write(ref _currentEntryRequest, sublot);
                    // A request for another operation session means the stop moved on, and the
                    // last stop's rejection no longer describes anything in front of the operator.
                    // A resend for the same session keeps it: that is the "scan again" case.
                    if (Volatile.Read(ref _currentSublotRejection) is { } shown
                        && !string.Equals(
                            shown.OperationSessionId,
                            sublot.OperationSessionId,
                            StringComparison.Ordinal))
                    {
                        Interlocked.CompareExchange(ref _currentSublotRejection, null, shown);
                    }

                    PublishOperatorEvent(
                        $"sublot-requested:{sublot.MessageId}",
                        "SUBLOT_ENTRY_REQUESTED",
                        $"收到子批录入请求：{string.Join("、", sublot.ExpectedSublots)}。");
                    SublotEntryRequested?.Invoke(
                        this,
                        new ValueChangedEventArgs<WireToGateSublotEntryRequest>(sublot));
                    break;
                case WireToGateExceptionRecoverySessionSnapshot recoverySnapshot:
                    try
                    {
                        ObserveRecoverySessionAttempt(
                            recoverySnapshot.ExceptionRecoverySessionId,
                            recoverySnapshot.SlotOperationAttemptId);
                    }
                    catch (InvalidDataException exception)
                    {
                        // The snapshot is still stored below: the session exists and the operator
                        // should see its state. What it may no longer do is authorize anything --
                        // the compensation gate and every request path refuse this session from now on.
                        _logger.Write(
                            LogSeverity.Warning,
                            nameof(WireToGateBusinessService),
                            $"恢复会话 {recoverySnapshot.ExceptionRecoverySessionId} 前后给出的 slotOperationAttemptId 不一致：reason={exception.Message}。",
                            exception);
                        PublishOperatorEvent(
                            $"recovery-session-attempt-inconsistent:{recoverySnapshot.ExceptionRecoverySessionId}",
                            "RECOVERY_BLOCKED",
                            $"恢复会话被阻断：{exception.Message}。服务端前后给出的装货作业身份不一致，请联系管理员。 ");
                    }

                    Volatile.Write(
                        ref _recoverySessionSnapshot,
                        recoverySnapshot.State == "CLOSED" ? null : recoverySnapshot);
                    if (recoverySnapshot.State == "CLOSED")
                    {
                        await ForgetClosedRecoverySessionAsync(
                                recoverySnapshot.ExceptionRecoverySessionId,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    PublishOperatorEvent(
                        $"recovery-session-snapshot:{recoverySnapshot.ExceptionRecoverySessionId}:{recoverySnapshot.RecoverySessionRevision}",
                        "RECOVERY_SESSION_UPDATED",
                        recoverySnapshot.State == "CLOSED"
                            ? "服务端恢复会话已关闭。"
                            : $"收到服务端恢复会话状态：{recoverySnapshot.State}。 ");
                    break;
                case WireToGateSlotOperationCommand operation:
                    await HandleSlotOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                    break;
                case WireToGatePreDepartureSafetyCheck safetyCheck:
                    await HandlePreDepartureSafetyCheckAsync(safetyCheck, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case WireToGateSlotOperationResumeCommand resume:
                    await HandleBlockedResumeAsync(resume, cancellationToken).ConfigureAwait(false);
                    break;
                case WireToGateLoadCompensationCommand compensation:
                    await HandleLoadCompensationCommandAsync(compensation, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case WireToGateLoadCorrectionCommand correction:
                    await HandleLoadCorrectionCommandAsync(correction, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case WireToGateFaultCargoRecoveryCommand faultCargo:
                    await HandleFaultCargoRecoveryCommandAsync(faultCargo, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case WireToGateForcedMechanicalRecoveryCommand forcedRecovery:
                    await HandleForcedMechanicalRecoveryCommandAsync(forcedRecovery, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case WireToGateRecoveryCommand { MessageType: "SublotRejected" } rejection:
                    HandleSublotRejected(rejection);
                    break;
                case WireToGateRecoveryCommand { MessageType: "SafetyStateSnapshotRequested" }:
                    await AnswerSafetyStateSnapshotRequestAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case WireToGateRecoveryCommand recovery:
                    if (recovery.MessageType is "LoadCorrectionRejected"
                        or "LoadCompensationRejected")
                    {
                        await HandleRecoveryVectorRejectedAsync(recovery, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                    WireToGateRecoverySafetyDecision decision =
                        WireToGateRecoverySafetyPolicy.Evaluate(
                            WireToGateRecoverySafetyFacts.Unknown);
                    _logger.Write(
                        LogSeverity.Warning,
                        nameof(WireToGateBusinessService),
                        $"收到服务端恢复消息：{recovery.MessageType}，恢复动作被安全策略阻断：{decision.ReasonCode}。");
                    PublishOperatorEvent(
                        $"recovery-blocked:{recovery.MessageId}",
                        "RECOVERY_BLOCKED",
                        $"收到恢复消息 {recovery.MessageType}，当前安全条件不允许执行：{decision.ReasonCode}。");
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Write(
                LogSeverity.Error,
                nameof(WireToGateBusinessService),
                $"处理服务端业务消息失败：{command.MessageType}。已保持物理安全阻塞。",
                exception);
        }
    }

    /// <summary>
    /// The server refused an entered sublot after revalidating it (BR-013). The reason goes to the
    /// operator as itself; this is a business answer, not a recovery message, so the recovery safety
    /// policy is never consulted (8005-agv-onboard-hmi#77).
    /// </summary>
    private void HandleSublotRejected(WireToGateRecoveryCommand command)
    {
        // The session client has already validated the payload against the 2.0.0 shape.
        SublotRejectedPayload payload =
            JsonSerializer.Deserialize<SublotRejectedPayload>(command.PayloadJson, JsonOptions)
            ?? throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");

        // Same operation session, same worklist revision: the request the operator scanned against
        // still stands, so they can scan again straight away. Anything else means the worklist moved
        // and the server owes a new request; the old one must not accept another entry meanwhile.
        // The compare-exchange keeps a request that arrived after this rejection was sent.
        WireToGateSublotEntryRequest? request = Volatile.Read(ref _currentEntryRequest);
        bool keep = request is not null
            && string.Equals(request.OperationSessionId, payload.OperationSessionId, StringComparison.Ordinal)
            && request.WorklistRevision == payload.CurrentWorklistRevision;
        if (!keep && request is not null)
        {
            Interlocked.CompareExchange(ref _currentEntryRequest, null, request);
        }

        WireToGateSublotRejection rejection = new(
            command.MessageId,
            payload.DemandId,
            payload.OperationSessionId,
            payload.Problem.ReasonCode,
            payload.CurrentWorklistRevision,
            payload.RejectedSublot,
            EntryRequestKept: keep,
            _clock.Now.ToUniversalTime());
        Volatile.Write(ref _currentSublotRejection, rejection);
        _logger.Write(
            LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"子批被服务端拒收：sublot={payload.RejectedSublot}，reason={payload.Problem.ReasonCode}，demandId={payload.DemandId ?? "null"}，currentWorklistRevision={payload.CurrentWorklistRevision}。");
        PublishOperatorEvent(
            $"sublot-rejected:{command.MessageId}",
            "SUBLOT_REJECTED",
            WireToGateSublotRejectionText.Describe(rejection)
                + WireToGateSublotRejectionText.NextStep(rejection.EntryRequestKept));
    }

    private async Task HandleBlockedResumeAsync(
        WireToGateSlotOperationResumeCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleBlockedResumeCoreAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WireToGateResumeNotStartedException exception)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"恢复命令未执行：attempt={command.SlotOperationAttemptId}，reason={exception.Message}。",
                exception);
            PublishOperatorEvent(
                $"resume-command-failed:{command.MessageId}",
                "RECOVERY_BLOCKED",
                $"恢复命令被阻断：{exception.Message}。未执行仓门IO。 ");
            await SendResumeRejectedAsync(
                    command,
                    ResumeNotStartedReasonCode(exception.Message),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        // Everything else stays local, as before onboard-hmi#119: an IOException or TimeoutException
        // may come from after the first pulse, and so may an InvalidDataException the executor did not
        // classify as "not started". Where the refusal cannot be placed before the door IO, the server
        // hears about the operation through the existing interrupted-settlement result instead.
        catch (Exception exception) when (
            exception is IOException
                or TimeoutException
                or InvalidOperationException
                or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"恢复命令未执行：attempt={command.SlotOperationAttemptId}，reason={exception.Message}。",
                exception);
            PublishOperatorEvent(
                $"resume-command-failed:{command.MessageId}",
                "RECOVERY_BLOCKED",
                $"恢复命令被阻断：{exception.Message}。未执行仓门IO。 ");
        }
    }

    private async Task HandleBlockedResumeCoreAsync(
        WireToGateSlotOperationResumeCommand command,
        CancellationToken cancellationToken)
    {
        // A resume this vehicle has already refused stays refused: the server may have closed the
        // resume workflow on that rejection, so running the same command later, because the gate
        // would pass now, would open doors for a workflow nobody is waiting on. The answer is the
        // rejection on file, unchanged (8005-agv-onboard-hmi#119).
        WireToGateDurableMessage? refused = await _session.Journal
            .ReadOutgoingByDeduplicationKeyAsync(ResumeRejectedKey(command), cancellationToken)
            .ConfigureAwait(false);
        if (refused is not null)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"收到已拒绝过的SlotOperationResumeCommand：attempt={command.SlotOperationAttemptId}，messageId={command.MessageId}，重发原拒绝，未执行物理动作。");
            PublishOperatorEvent(
                $"resume-command:{command.MessageId}",
                "RECOVERY_BLOCKED",
                "恢复命令此前已被拒绝，已重发原拒绝，未执行仓门IO。");
            using JsonDocument wire = JsonDocument.Parse(refused.WireLine);
            SlotOperationCommandRejectedPayload payload =
                wire.RootElement.GetProperty("payload").Deserialize<SlotOperationCommandRejectedPayload>(JsonOptions)
                ?? throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
            await SendResumeRejectedCoreAsync(command, payload, cancellationToken).ConfigureAwait(false);
            // Idempotent. A journal still naming this session is one a stop, or a failed write,
            // left behind after the rejection was already on file.
            await ReleaseRefusedResumeSessionAsync(command, cancellationToken).ConfigureAwait(false);
            return;
        }

        WireToGateRecoveryState state = await _session.Journal
            .ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        VehicleSafetySignal vehicle = ReadVehicleSafety();
        DateTimeOffset now = _clock.Now;
        bool fresh = snapshot.IsConnected
            && SafetyRules.IsSnapshotFresh(snapshot, now, _ioSnapshotMaxAge)
            && vehicle.IsFresh(
                now,
                _vehicleSafetyMaxAge,
                _vehicleSafetyClockSkewTolerance);
        bool targetsKnown = fresh
            && command.Slots.All(slot =>
                TryGetLocker(snapshot, slot, out LockerSnapshot? locker)
                && locker is { IsKnown: true });
        bool targetsLocked = targetsKnown
            && command.Slots.All(slot =>
                TryGetLocker(snapshot, slot, out LockerSnapshot? locker)
                && locker is { IsLocked: true });
        bool outputsReset = targetsKnown
            && command.Slots.All(slot =>
                TryGetLocker(snapshot, slot, out LockerSnapshot? locker)
                && locker is { UnlockOutputRaw: false });
        bool recoverySessionAuthorized =
            _recoveryOptions.ResumeAfterRepairEnabled
            && string.Equals(
                state.ExceptionRecoverySessionId,
                command.ExceptionRecoverySessionId,
                StringComparison.Ordinal)
            && string.Equals(
                state.RecoveryActionId,
                command.RecoveryActionId,
                StringComparison.Ordinal)
            && state.OperationContext is not null
            && string.Equals(
                state.OperationContext.DemandId,
                command.DemandId,
                StringComparison.Ordinal)
            && state.OperationContext.Slots.SequenceEqual(command.Slots);
        WireToGateRecoverySafetyDecision decision =
            WireToGateRecoverySafetyPolicy.Evaluate(
                new WireToGateRecoverySafetyFacts(
                    RecoverySessionAuthorized: recoverySessionAuthorized,
                    RecoveryStatePersisted: WireToGateRecoverySafetyPolicy.MatchesPersistedResumeState(
                        state,
                        command.SlotOperationAttemptId,
                        command.ProvenRecoveryCheckpoint),
                    VehicleStopped: vehicle.MotionState == VehicleMotionState.Stopped,
                    VehicleSignalFresh: fresh,
                    AllTargetSlotsKnown: targetsKnown,
                    AllTargetSlotsLocked: targetsLocked,
                    AllUnlockOutputsReset: outputsReset));
        if (!decision.Allowed)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"收到SlotOperationResumeCommand但未执行物理动作：attempt={command.SlotOperationAttemptId}，reason={decision.ReasonCode}。");
            PublishOperatorEvent(
                $"resume-command:{command.MessageId}",
                "RECOVERY_BLOCKED",
                $"恢复命令被安全门禁阻断：{decision.ReasonCode}。");
            await SendResumeRejectedAsync(command, decision.ReasonCode, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string recoveryResultKey =
            $"recovery-operation-result:{command.SlotOperationAttemptId}:{command.RecoveryActionId}";
        WireToGateDurableMessage? existingResult = await _session.Journal
            .ReadOutgoingByDeduplicationKeyAsync(recoveryResultKey, cancellationToken)
            .ConfigureAwait(false);
        if (existingResult is not null)
        {
            PublishOperatorEvent(
                $"recovery-result-replay:{command.RecoveryActionId}",
                "OPERATION_REPLAY",
                "恢复结果已存在，保持原恢复结果重放，未再次执行仓门IO。");
            return;
        }

        lock (_operationAttemptGate)
        {
            if (!_operationAttempts.Add(command.SlotOperationAttemptId))
            {
                return;
            }
        }

        try
        {
            WireToGateSlotOperationCommand original = state.OperationContext!.ToCommand();
            PublishOperation(
                original,
                WireToGateHmiOperationStage.Preparing,
                $"恢复原操作：{FormatSlots(command.Slots)}。",
                $"recovery:{command.RecoveryActionId}:start");
            async Task SendProgress(WireToGateOperationProgress progress, CancellationToken progressToken)
            {
                PublishOperationProgress(
                    original,
                    progress,
                    $"recovery:{command.RecoveryActionId}:{OperationDetailKey(progress)}");
                await SendProgressLoggingFailuresAsync(
                    () => _session.SendRecoveryOperationProgressAsync(
                        command.SlotOperationAttemptId,
                        progress.Phase,
                        progress.Active,
                        progress.Completed,
                        cancellationToken: progressToken),
                    command.SlotOperationAttemptId,
                    progress,
                    progressToken).ConfigureAwait(false);
            }

            WireToGateOperationExecutionResult execution = await _executor
                .ResumeAsync(command, SendProgress, cancellationToken)
                .ConfigureAwait(false);
            WireToGateOperationResultPayload payload = CreateOperationResultPayload(execution);
            string resultMessageId = StableUuid(recoveryResultKey);
            bool completedSuccessfully = execution.OverallOutcome == "COMPLETED";
            PublishOperation(
                original,
                completedSuccessfully
                    ? WireToGateHmiOperationStage.Completed
                    : WireToGateHmiOperationStage.RecoveryRequired,
                completedSuccessfully
                    ? "恢复后的仓位操作已完成，正在上报替换结果。"
                    : "恢复后的仓位操作仍未完成，需要继续人工恢复。",
                $"recovery:{command.RecoveryActionId}:final");
            try
            {
                if (!completedSuccessfully && execution.JournalCheckpoint != "NONE")
                {
                    await _executor.RecordPendingResultAsync(
                        command.SlotOperationAttemptId,
                        new WireToGatePendingResult(
                            "OperationResult",
                            resultMessageId,
                            command.SlotOperationAttemptId,
                            payload.ResultContentSha256),
                        cancellationToken).ConfigureAwait(false);
                }

                await _session.SendRecoveryOperationResultAsync(
                    recoveryResultKey,
                    resultMessageId,
                    payload,
                    cancellationToken).ConfigureAwait(false);
                if (completedSuccessfully && execution.JournalCheckpoint != "NONE")
                {
                    await _executor.MarkResultRecordedAsync(
                        command.SlotOperationAttemptId,
                        cancellationToken).ConfigureAwait(false);
                    // Same refresh as the formal load path: a resumed load that completes is the
                    // last completed load, and the correction entry must see it now.
                    await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
                }

                PublishOperatorEvent(
                    $"recovery-result:{command.RecoveryActionId}:{execution.OverallOutcome}",
                    completedSuccessfully ? "OPERATION_COMPLETED" : "OPERATION_RECOVERY_REQUIRED",
                    completedSuccessfully
                        ? "恢复后的原操作结果已上报。"
                        : "恢复后的原操作仍未完成，结果已上报并保持故障安全。");
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
            {
                _logger.Write(
                    LogSeverity.Warning,
                    nameof(WireToGateBusinessService),
                    $"恢复OperationResult暂未收到DurableAck：attempt={command.SlotOperationAttemptId}。",
                    exception);
                PublishOperatorEvent(
                    $"recovery-result-pending:{command.RecoveryActionId}",
                    "RESULT_ACK_PENDING",
                    "恢复结果已持久化，等待服务端确认；不会重复执行仓门IO。");
            }
        }
        finally
        {
            lock (_operationAttemptGate)
            {
                _operationAttempts.Remove(command.SlotOperationAttemptId);
            }
        }
    }

    private static bool TryGetLocker(
        IoSnapshot snapshot,
        int physicalSlot,
        out LockerSnapshot? locker)
    {
        locker = snapshot.Lockers.FirstOrDefault(
            item => item.PhysicalNumber == physicalSlot);
        return locker is not null;
    }

    private async Task HandleSlotOperationAsync(
        WireToGateSlotOperationCommand command,
        CancellationToken cancellationToken)
    {
        if (_session.Current.Readiness != WireToGateSessionReadiness.Ready)
        {
            await SendOperationRejectedAsync(command, "ACTION_NOT_ALLOWED_IN_STATE", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string operationDeduplicationKey = $"operation-result:{command.SlotOperationAttemptId}";
        WireToGateDurableMessage? existingResult = await _session.Journal
            .ReadOutgoingByDeduplicationKeyAsync(operationDeduplicationKey, cancellationToken)
            .ConfigureAwait(false);
        if (existingResult is not null)
        {
            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"忽略重复SlotOperationCommand：attempt={command.SlotOperationAttemptId}，保留原OperationResult重放。");
            PublishOperatorEvent(
                $"operation-replay:{command.SlotOperationAttemptId}",
                "OPERATION_REPLAY",
                "收到重复仓位命令，已保持原结果重放，未再次执行仓门IO。");
            return;
        }

        lock (_operationAttemptGate)
        {
            if (!_operationAttempts.Add(command.SlotOperationAttemptId))
            {
                _logger.Write(
                    LogSeverity.Information,
                    nameof(WireToGateBusinessService),
                    $"忽略并发重复SlotOperationCommand：attempt={command.SlotOperationAttemptId}。");
                return;
            }
        }

        // The vehicle is taking this stop's load in hand, so a rejection still on show describes an
        // entry the stop has moved past; left up, it would sit beside a load in progress. Cleared
        // before the first progress event so the prompt area never shows both.
        if (Volatile.Read(ref _currentSublotRejection) is { } shownRejection)
        {
            Interlocked.CompareExchange(ref _currentSublotRejection, null, shownRejection);
        }

        bool restoreAfterRelease = false;
        bool resultUnacknowledged = false;
        try
        {
            if (await IsAttemptTakenOverAsync(command, cancellationToken).ConfigureAwait(false))
            {
                // A restore that ran into this claim got InFlight and left a leftover attempt alone, and this
                // branch does not settle it either; nothing else would look again before the next session state
                // change (onboard-hmi#124). Run once more after the claim is released -- the settlement is claimed
                // again and the projection is published under its own key, so a second run cannot repeat either.
                // Only for a leftover: an attempt a recovery vector owns (an in-flight load cancellation) is that
                // vector's to project, and a restore would show it as an unfinished vector mid-execution.
                WireToGateRecoveryState takenOver = await _session.Journal
                    .ReadRecoveryStateAsync(cancellationToken)
                    .ConfigureAwait(false);
                restoreAfterRelease = takenOver.RecoveryVector is null
                    && string.Equals(
                        takenOver.UnsettledSlotOperationAttemptId,
                        command.SlotOperationAttemptId,
                        StringComparison.Ordinal);
                _logger.Write(
                    LogSeverity.Information,
                    nameof(WireToGateBusinessService),
                    $"忽略重复SlotOperationCommand：attempt={command.SlotOperationAttemptId}已开始且未结算，或已由装货取消接手，未再次执行仓门IO。");
                PublishOperatorEvent(
                    $"operation-taken-over-replay:{command.MessageId}",
                    "OPERATION_REPLAY",
                    "收到已开始或已取消的仓位命令的重发，未再次执行仓门IO。");
                return;
            }

            PublishOperation(
                command,
                WireToGateHmiOperationStage.Preparing,
                $"准备执行{FormatOperationType(command.OperationType)}：{FormatSlots(command.Slots)}。",
                "initial");
            async Task SendProgress(WireToGateOperationProgress progress, CancellationToken progressToken)
            {
                if (progress.Phase == "PREPARING")
                {
                    // The executor has journaled the operation by now. The entry gates read a cached
                    // copy, and the in-flight load cancellation has to be offered while the door is
                    // open (onboard-hmi#78), so the copy is refreshed before the event that makes the
                    // HMI read the gates again.
                    await ReadRecoveryStateCachedAsync(progressToken).ConfigureAwait(false);
                }

                PublishOperationProgress(command, progress, OperationDetailKey(progress));
                await SendProgressLoggingFailuresAsync(
                    () => _session.SendOperationProgressAsync(
                        command.SlotOperationAttemptId,
                        progress.Phase,
                        progress.Active,
                        progress.Completed,
                        cancellationToken: progressToken),
                    command.SlotOperationAttemptId,
                    progress,
                    progressToken).ConfigureAwait(false);
            }

            WireToGateOperationExecutionResult execution;
            try
            {
                execution = await _executor
                    .ExecuteAsync(command, SendProgress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Aborted by an authorized load cancellation (onboard-hmi#78). No result: the attempt's
                // conclusion is that cancellation's, which takes the slots over from here.
                _logger.Write(
                    LogSeverity.Information,
                    nameof(WireToGateBusinessService),
                    $"装货已被授权的装货取消中止，不上报OperationResult：attempt={command.SlotOperationAttemptId}。");
                return;
            }

            WireToGateOperationResultPayload payload = CreateOperationResultPayload(execution);
            bool completedSuccessfully = string.Equals(
                execution.OverallOutcome,
                "COMPLETED",
                StringComparison.Ordinal);
            PublishOperation(
                command,
                completedSuccessfully
                    ? WireToGateHmiOperationStage.Completed
                    : WireToGateHmiOperationStage.RecoveryRequired,
                completedSuccessfully
                    ? $"{FormatSlots(command.Slots)}操作完成，正在上报结果。"
                    : $"{FormatSlots(command.Slots)}操作未完成，需要恢复处理。",
                "final");
            try
            {
                if (!completedSuccessfully
                    && !string.Equals(execution.JournalCheckpoint, "NONE", StringComparison.Ordinal))
                {
                    // Kept pending until the operation settles: every later session reports it and
                    // replays it (CV-OPERATION-RESULT-UNKNOWN-RECONCILE).
                    await _executor.RecordPendingResultAsync(
                        command.SlotOperationAttemptId,
                        new WireToGatePendingResult(
                            "OperationResult",
                            command.SlotOperationAttemptId,
                            command.SlotOperationAttemptId,
                            payload.ResultContentSha256),
                        cancellationToken).ConfigureAwait(false);
                }

                // The send path that allows RecoveryRequired, as the interrupted settlement uses: after a reconnect
                // mid-load the new session is RecoveryRequired precisely because this attempt is unsettled, and the
                // server grants READY only once this result arrives (ADR-cross-0028 "result replay", ADR-cross-0029
                // step 4; onboard-hmi#127). Same key and messageId as that path, so it is the same message.
                await _session.SendRecoveryOperationResultAsync(
                    operationDeduplicationKey,
                    command.SlotOperationAttemptId,
                    payload,
                    cancellationToken).ConfigureAwait(false);
                if (completedSuccessfully
                    && !string.Equals(execution.JournalCheckpoint, "NONE", StringComparison.Ordinal))
                {
                    await _executor.MarkResultRecordedAsync(
                        command.SlotOperationAttemptId,
                        cancellationToken).ConfigureAwait(false);
                    // Recording the result is what makes the load correctable
                    // (LastCompletedLoadOperationContext), and the CanRequest* gates read a cached
                    // copy. Without this refresh the correction entry waited for an unrelated session
                    // event -- on the real rig, the departure that ends the correction window
                    // (G3 FP-IS-02, 2026-09-13). The operator event below re-evaluates the gates.
                    await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
                }
                PublishOperatorEvent(
                    $"operation-result:{command.SlotOperationAttemptId}:{execution.OverallOutcome}",
                    completedSuccessfully ? "OPERATION_COMPLETED" : "OPERATION_RECOVERY_REQUIRED",
                    completedSuccessfully
                        ? $"{FormatSlots(command.Slots)}操作结果已被服务端确认。"
                        : $"{FormatSlots(command.Slots)}操作失败或状态未知，服务端已收到结果，等待管理员恢复。",
                    new WireToGateHmiOperationSnapshot(
                        command.SlotOperationAttemptId,
                        command.OperationType,
                        command.Slots,
                        completedSuccessfully
                            ? WireToGateHmiOperationStage.Completed
                            : WireToGateHmiOperationStage.RecoveryRequired,
                        completedSuccessfully ? "操作完成。" : "操作需要管理员恢复。",
                        execution.ObservedAt));
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
            {
                // The result is in the durable outbox whichever way this failed: a timeout or an IO failure came after
                // it was written, and WIRE_TO_GATE_NOT_READY (InvalidOperationException) -- the session down or still
                // in its handshake -- writes it before refusing (durableBeforeSend, onboard-hmi#127). The next
                // handshake replays the same messageId/content. Whether a result only waits for its ack is still read
                // from the outbox, never from having landed here (onboard-hmi#124): an IO failure of the journal
                // itself leaves no row, and then the next restore settles the attempt from the live IO.
                // Either way, never execute the physical operation again.
                _logger.Write(
                    LogSeverity.Warning,
                    nameof(WireToGateBusinessService),
                    $"OperationResult暂未收到DurableAck：attempt={command.SlotOperationAttemptId}。",
                    exception);
                resultUnacknowledged = true;
                PublishOperatorEvent(
                    $"operation-result-pending:{command.SlotOperationAttemptId}",
                    "RESULT_ACK_PENDING",
                    "操作已安全结束，但结果确认暂未收到；系统将保持同一结果重放，不会重复执行IO。",
                    new WireToGateHmiOperationSnapshot(
                        command.SlotOperationAttemptId,
                        command.OperationType,
                        command.Slots,
                        completedSuccessfully
                            ? WireToGateHmiOperationStage.Reporting
                            : WireToGateHmiOperationStage.RecoveryRequired,
                        "结果等待确认，禁止重复操作仓门。",
                        execution.ObservedAt));
            }
        }
        finally
        {
            lock (_operationAttemptGate)
            {
                _operationAttempts.Remove(command.SlotOperationAttemptId);
            }

            // A result left unacknowledged while a session is up -- its ack lost with the link intact, or
            // the handshake that brought the session up already past its replay -- would otherwise wait for the next
            // session state change to be sent again. The restore sends it once more under the same messageId, after
            // this claim is released so that it is not InFlight to itself (onboard-hmi#127). A session that is down
            // gets it from the next handshake, whose readiness runs the restore anyway.
            if (restoreAfterRelease
                || resultUnacknowledged
                    && _session.Current.Readiness is WireToGateSessionReadiness.Ready
                        or WireToGateSessionReadiness.RecoveryRequired)
            {
                TrackTask(RestorePendingRecoveryOperationProjectionAsync(_stopping.Token));
            }
        }
    }

    /// <summary>
    /// Publishes one executor progress report, and records whether it leaves a load's doors open or
    /// being reopened -- what the countdown line reads once the deadline has passed.
    /// </summary>
    private void PublishOperationProgress(
        WireToGateSlotOperationCommand command,
        WireToGateOperationProgress progress,
        string detailKey)
    {
        WireToGateHmiOperationStage stage = MapOperationStage(progress.Phase);
        string guidance = OperationGuidance(command, progress, StationDeadlinePassed());
        PublishOperation(command, stage, guidance, detailKey, progress.Active);
        if (command.OperationType == OperationType.Load
            && stage is WireToGateHmiOperationStage.Unlocking or WireToGateHmiOperationStage.WaitingOperator)
        {
            Volatile.Write(
                ref _loadAwaitingOperator,
                new LoadAwaitingOperator(command.SlotOperationAttemptId, progress.Active.ToArray()));
        }
    }

    /// <summary>
    /// Whether the server's station departure deadline has passed. Read from the latest worklist every
    /// time: the server replaces the deadline as a whole, and the vehicle never extends or voids it.
    /// </summary>
    private bool StationDeadlinePassed() =>
        _session.CurrentJourney.CurrentStopWorklist?.StationDepartureDeadlineAt is { } deadline
        && _clock.Now >= deadline;

    private void PublishOperation(
        WireToGateSlotOperationCommand command,
        WireToGateHmiOperationStage stage,
        string guidance,
        string detailKey,
        IReadOnlyList<int>? activeSlots = null)
    {
        if (stage is not (WireToGateHmiOperationStage.Unlocking or WireToGateHmiOperationStage.WaitingOperator))
        {
            ForgetLoadAwaitingOperator(command.SlotOperationAttemptId);
        }

        WireToGateHmiOperationSnapshot operation = new(
            command.SlotOperationAttemptId,
            command.OperationType,
            command.Slots,
            stage,
            guidance,
            _clock.Now.ToUniversalTime());
        Volatile.Write(ref _currentOperationSnapshot, operation);
        PublishOperatorEvent(
            $"operation-stage:{command.SlotOperationAttemptId}:{stage}:{detailKey}",
            "OPERATION_PROGRESS",
            guidance,
            operation,
            activeSlots);
    }

    // 操作员事件分两类，走两条路，别混：
    //
    // 「广播」是状态推送——服务端重放同一条命令、快照轮询重复读到同一版本，都会让同一句话
    // 被推很多遍，去重是它存在的理由（见 OperatorEventDeduplicator）。键必须带 MessageId /
    // SlotOperationAttemptId / RecoveryActionId 这类每次唯一的标识，否则它去掉的就不是重复
    // 而是后来的真事件。
    //
    // 「应答」是对操作员按下某个按钮的直接回答。人按一次就该被回答一次，去重在这里没有任何
    // 好处：它的产生源是人手，天然稀疏，刷不了屏；而一旦吞掉，界面上是彻底的沉默——按钮没反应，
    // 没有报错也没有日志能让操作员看见。这条路因此完全不去重。
    private void PublishOperatorResponse(string kind, string message) =>
        RaiseOperatorEvent(kind, message, operation: null);

    private void PublishOperatorEvent(
        string deduplicationKey,
        string kind,
        string message,
        WireToGateHmiOperationSnapshot? operation = null,
        IReadOnlyList<int>? activeSlots = null)
    {
        if (operation is not null)
        {
            Volatile.Write(ref _currentOperationSnapshot, operation);
            // Only an executor progress report names active slots; every other projection -- a result,
            // UNKNOWN, a cancellation or recovery vector taking over -- stops the wait clock.
            _expectedActionWait.Observe(operation, activeSlots);
        }

        if (!_operatorEventDeduplicator.ShouldPublish(deduplicationKey))
        {
            return;
        }

        RaiseOperatorEvent(kind, message, operation);
    }

    private void RaiseOperatorEvent(
        string kind,
        string message,
        WireToGateHmiOperationSnapshot? operation) =>
        OperatorEventPublished?.Invoke(
            this,
            new ValueChangedEventArgs<WireToGateOperatorEvent>(
                new WireToGateOperatorEvent(
                    _clock.Now.ToUniversalTime(),
                    kind,
                    message,
                    operation)));

    private static WireToGateHmiOperationStage MapOperationStage(string phase) => phase switch
    {
        "PREPARING" => WireToGateHmiOperationStage.Preparing,
        "UNLOCKING" => WireToGateHmiOperationStage.Unlocking,
        "WAITING_OPERATOR" => WireToGateHmiOperationStage.WaitingOperator,
        "VERIFYING" => WireToGateHmiOperationStage.Verifying,
        "SAFE_FINISH" => WireToGateHmiOperationStage.Reporting,
        "PAUSED" => WireToGateHmiOperationStage.RecoveryRequired,
        _ => WireToGateHmiOperationStage.Preparing
    };

    // The prompt round has to be part of the key. A reopen's second round repeats the first one's
    // phase, active and completed sets exactly, so without it PublishOperatorEvent swallows every
    // prompt after the first -- and ADR-cross-0058 decision 1 puts no limit on reopening.
    private static string OperationDetailKey(WireToGateOperationProgress progress) =>
        $"{progress.Phase}:{string.Join(',', progress.Active)}:{string.Join(',', progress.Completed)}:{progress.PromptRound}";

    /// <summary>
    /// A progress message that cannot be sent -- the connection dropped while the operator is still at
    /// the door -- is logged and dropped. It must not reach the executor, where it would be read as
    /// a slot whose state is unknown; UNKNOWN comes from IO readings only. Cancellation of the
    /// operation itself still passes.
    /// </summary>
    private async Task SendProgressLoggingFailuresAsync(
        Func<Task> send,
        string slotOperationAttemptId,
        WireToGateOperationProgress progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await send().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"仓位操作进度未能发送，不影响仓位判定：attempt={slotOperationAttemptId}，" +
                $"phase={progress.Phase}，round={progress.PromptRound}，error={exception.GetType().Name}。");
        }
    }

    /// <summary>
    /// The executor asks this before every automatic reopen pulse. Nothing on this vehicle reads the
    /// emergency stop, so the vehicle safety fact stands in for it: RIoT reporting an emergency state
    /// other than OK leaves the control server's projection UNKNOWN (RIOT_EMERGENCY_NOT_OK), and a
    /// stale or unreadable projection is treated the same way.
    /// </summary>
    private bool IsReopenPermitted() =>
        ReadVehicleSafety().IsStoppedAndFresh(
            _clock.Now,
            _vehicleSafetyMaxAge,
            _vehicleSafetyClockSkewTolerance);

    // The text follows the cause the executor states, not a guess from the round number. Past the
    // station departure deadline a load's prompt names the way out as well (program#55,
    // onboard-hmi#78): the executor keeps reopening, and only the operator's cancel ends the load.
    internal static string OperationGuidance(
        WireToGateSlotOperationCommand command,
        WireToGateOperationProgress progress,
        bool deadlinePassed) => progress.Phase switch
        {
            "PREPARING" => $"正在检查{FormatSlots(command.Slots)}的安全条件。",
            "UNLOCKING" => progress.Cause == WireToGatePromptCause.OppositeReopen
                ? $"{FormatSlots(progress.Active)}关门时货物状态与预期不符，正在重新打开。"
                : $"正在打开{FormatSlots(progress.Active)}。",
            "WAITING_OPERATOR" when progress.Cause == WireToGatePromptCause.ReopenHeldBySafety =>
                $"{FormatSlots(progress.Active)}关门时货物状态与预期不符，但车辆安全状态未确认（急停或未停稳），" +
                "暂不重新打开；安全状态恢复后自动打开。",
            "WAITING_OPERATOR" => (command.OperationType == OperationType.Load
                ? deadlinePassed
                    ? WireToGateStationDeadlineText.LoadPrompt(progress.Active)
                    : $"请在{FormatSlots(progress.Active)}放入货物并关门。"
                : $"请在{FormatSlots(progress.Active)}取出货物并关门。")
                + (progress.PromptRound == 0 ? string.Empty : $"（第{progress.PromptRound + 1}次提示）"),
            "VERIFYING" => $"正在核对仓门、货物和输出状态；已完成 {progress.Completed.Count}/{command.Slots.Count}。",
            "SAFE_FINISH" => "全部目标仓已达到安全收尾状态，正在上报结果。",
            _ => $"正在处理{FormatSlots(command.Slots)}。"
        };

    private static string FormatSlots(IReadOnlyList<int> slots) =>
        string.Join("、", slots.Order()) + "号仓";

    private static string StableUuid(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString("D");
    }

    private async Task HandlePreDepartureSafetyCheckAsync(
        WireToGatePreDepartureSafetyCheck command,
        CancellationToken cancellationToken)
    {
        // CV-PREDEPARTURE-SAFETY-EXPIRES. The check names the safety state version it is asking about.
        // Once this vehicle has had a later version accepted, the question is about a state that no
        // longer holds: answering would report today's safety against it. Refused as
        // PREDEPARTURE_CHECK_EXPIRED, session kept; the control server retires the check and asks
        // again against the current version.
        long acceptedSafetyStateVersion = _session.Current.SafetyStateVersion;
        if (command.ExpectedSafetyStateVersion < acceptedSafetyStateVersion)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"出发前安全检查已过期：check={command.PreDepartureSafetyCheckId}，" +
                $"询问的安全版本={command.ExpectedSafetyStateVersion}，本端已被接受的版本={acceptedSafetyStateVersion}。" +
                "回PREDEPARTURE_CHECK_EXPIRED，不作答。");
            await _session.RejectServerCommandAsync(command, "PREDEPARTURE_CHECK_EXPIRED", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        SafetyEvaluation evaluation = EvaluateSafety(_ioModule.CurrentSnapshot);
        bool safe = evaluation.Safety.DepartureSafe;
        long safetyStateVersion = _session.Current.SafetyStateVersion;
        await _session.SendPreDepartureSafetyCheckResultAsync(
            command.PreDepartureSafetyCheckId,
            safe ? "SAFE" : evaluation.Safety.UnknownPresent ? "UNKNOWN" : "UNSAFE",
            evaluation.ObservedAt,
            safetyStateVersion,
            evaluation.ObservedAt.AddSeconds(2),
            evaluation.Safety,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendOperationRejectedAsync(
        WireToGateSlotOperationCommand command,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var payload = new SlotOperationCommandRejectedPayload(
            command.SlotOperationAttemptId,
            new WireToGateProblemPayload(reasonCode, null, null),
            _session.Current.CapabilityVersion,
            null);
        // Called only while the session is not Ready, so it takes the send path that allows RecoveryRequired
        // (onboard-hmi#127); the Ready-only path could never send it. Now that it is actually written, its messageId
        // is derived from its own key, as the resume rejection's is: the attempt id is the OperationResult's
        // messageId, the outbox holds one row per messageId, and a rejection holding it would keep the result of
        // the same attempt, issued again once the session is ready, out of the outbox for good.
        string deduplicationKey = $"slot-operation-rejected:{command.SlotOperationAttemptId}:{reasonCode}";
        await _session.SendSlotOperationRejectedAsync(
            deduplicationKey,
            StableUuid(deduplicationKey),
            command.MessageId,
            payload,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a <c>SlotOperationResumeCommand</c> refused before any door IO, so the control server's
    /// resume workflow gets an answer instead of waiting on one forever (8005-agv-onboard-hmi#119).
    /// </summary>
    /// <remarks>
    /// Correlated to the refused command's messageId, as the protocol's
    /// <c>REQUIRED_ORIGINAL_MESSAGE_ID</c> asks, and keyed by it too: the rejection of the original
    /// <c>SlotOperationCommand</c> for the same attempt is keyed by attempt and reason only, and the
    /// two must never meet on one key.
    /// </remarks>
    private async Task SendResumeRejectedAsync(
        WireToGateSlotOperationResumeCommand command,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        // Forgotten before the rejection is put on file, not after. The server settles the resume
        // command on the rejection alone and does not send it again, so a stop between the two
        // writes in the other order would leave a journal naming a closed session with nothing left
        // to arrive and clear it. In this order a stop in between leaves the resume unanswered: the
        // server sends it again, and it is refused afresh against a journal already cleared.
        await ReleaseRefusedResumeSessionAsync(command, cancellationToken).ConfigureAwait(false);
        await SendResumeRejectedCoreAsync(
                command,
                new SlotOperationCommandRejectedPayload(
                    command.SlotOperationAttemptId,
                    new WireToGateProblemPayload(reasonCode, null, null),
                    _session.Current.CapabilityVersion,
                    null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets the recovery session and action a refused resume belonged to, keeping the unsettled
    /// operation it was about.
    /// </summary>
    /// <remarks>
    /// The rejection ends that resume on the vehicle's side, and the server closes the session on it
    /// (control-server#187). Nothing else clears these fields -- only a recorded result does -- so
    /// without this every recovery entry would refuse locally with RECOVERY_SESSION_STATE_PENDING and
    /// the vehicle could never open the next session, as the real rig showed (onboard-hmi#119).
    /// The attempt, its operation context and its proven checkpoint stay: the load is still
    /// unsettled and the next session recovers it. Only a journal still naming this very session and
    /// action is touched, so a refusal of a command about some other session changes nothing.
    /// </remarks>
    private Task ReleaseRefusedResumeSessionAsync(
        WireToGateSlotOperationResumeCommand command,
        CancellationToken cancellationToken) =>
        ForgetRecoverySessionAsync(
            command.ExceptionRecoverySessionId,
            command.RecoveryActionId,
            released => released,
            $"续行命令已拒绝，但清除恢复会话记录失败：attempt={command.SlotOperationAttemptId}。",
            cancellationToken);

    /// <summary>
    /// Forgets one recovery session -- its identity, action, request ids, reason and operator -- and
    /// whatever <paramref name="release"/> adds to the same write, keeping the unsettled operation
    /// (onboard-hmi#119, #123).
    /// </summary>
    /// <param name="exceptionRecoverySessionId">The session to forget; a journal naming any other is left alone.</param>
    /// <param name="recoveryActionId">
    /// The action inside it, when the caller knows which; a journal naming another action is left
    /// alone. <c>null</c> for a caller that speaks for the whole session, such as its CLOSED snapshot.
    /// </param>
    /// <param name="release">
    /// Given the state with the session fields already cleared, returns what to write -- or
    /// <c>null</c> to write nothing, when the rest of the journal shows this session is not one that
    /// may be forgotten yet.
    /// </param>
    /// <remarks>
    /// <para>
    /// Idempotent, and failure is logged rather than thrown: every caller has already answered the
    /// server, and each of the other callers -- the refusal, its replay, the CLOSED snapshot -- gets
    /// another chance to clear what a failed write left behind.
    /// </para>
    /// <para>
    /// The guard and the write are one journal update, never a read followed by a write. A result
    /// recorded in between -- the resume's own, or the late one of onboard-hmi#124's
    /// acknowledged-completed path -- would otherwise be written over with the state read before it,
    /// putting a settled attempt back as unsettled (onboard-hmi#123 review A). Inside the update the
    /// journal read is the latest: a result already recorded has cleared the session, so the guard
    /// fails and nothing is written; a result recorded afterwards writes the settled state it wants.
    /// </para>
    /// </remarks>
    private async Task ForgetRecoverySessionAsync(
        string? exceptionRecoverySessionId,
        string? recoveryActionId,
        Func<WireToGateRecoveryState, WireToGateRecoveryState?> release,
        string failureLog,
        CancellationToken cancellationToken)
    {
        if (exceptionRecoverySessionId is null)
        {
            return;
        }

        try
        {
            WireToGateRecoveryState? written = await _session.Journal.UpdateRecoveryStateAsync(
                    state =>
                        !string.Equals(
                            state.ExceptionRecoverySessionId,
                            exceptionRecoverySessionId,
                            StringComparison.Ordinal)
                        || recoveryActionId is not null
                            && !string.Equals(state.RecoveryActionId, recoveryActionId, StringComparison.Ordinal)
                            ? null
                            : release(state with
                            {
                                ExceptionRecoverySessionId = null,
                                RecoveryActionId = null,
                                RecoverySessionRequestId = null,
                                RecoveryActionRequestId = null,
                                RecoveryReason = null,
                                RecoveryOperatorId = null,
                                RecoveryOperatorVerifiedAt = null
                            }),
                    cancellationToken)
                .ConfigureAwait(false);
            if (written is not null)
            {
                Volatile.Write(ref _lastRecoveryState, written);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                failureLog,
                exception);
        }
    }

    /// <summary>
    /// The fallback for a session forgotten nowhere else: the server closed it, so it is over whatever
    /// the vehicle still has on file (coordinator review of onboard-hmi#119).
    /// </summary>
    /// <remarks>
    /// Covers the refusal whose release write failed: the answer still reached the server, the server
    /// closed the session on it, and no command for it will come again. Guarded by the session id. A
    /// recovery vector that may have acted is never forgotten here -- its result settles it, and a
    /// closed session does not make an unproven slot proven.
    /// </remarks>
    private Task ForgetClosedRecoverySessionAsync(
        string exceptionRecoverySessionId,
        CancellationToken cancellationToken) =>
        ForgetRecoverySessionAsync(
            exceptionRecoverySessionId,
            recoveryActionId: null,
            released => released.RecoveryVector is not { } vector
                ? released
                : vector.ExceptionRecoverySessionId == exceptionRecoverySessionId
                    ? ForgetRefusedVector(released, vector)
                    : null,
            $"恢复会话已关闭，但清除本地恢复会话记录失败：session={exceptionRecoverySessionId}。",
            cancellationToken);

    private static string ResumeRejectedKey(WireToGateSlotOperationResumeCommand command) =>
        $"slot-operation-resume-rejected:{command.SlotOperationAttemptId}:{command.MessageId}";

    private async Task SendResumeRejectedCoreAsync(
        WireToGateSlotOperationResumeCommand command,
        SlotOperationCommandRejectedPayload payload,
        CancellationToken cancellationToken)
    {
        string deduplicationKey = ResumeRejectedKey(command);
        try
        {
            await _session.SendSlotOperationResumeRejectedAsync(
                deduplicationKey,
                StableUuid(deduplicationKey),
                command.MessageId,
                payload,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
        {
            // Saved before it was sent, so an unacknowledged rejection is replayed with the rest of the
            // outbox on the next session, and a resend of the command sends it again from here. One that
            // never reached the outbox is not waiting for an acknowledgement; it is refused afresh when
            // the server sends the resume again.
            bool onFile = await _session.Journal
                .ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken)
                .ConfigureAwait(false) is not null;
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                onFile
                    ? $"续行命令的拒绝已写入发件箱，暂未收到DurableAck：attempt={command.SlotOperationAttemptId}，reason={payload.Problem.ReasonCode}。"
                    : $"续行命令的拒绝未能写入发件箱：attempt={command.SlotOperationAttemptId}，reason={payload.Problem.ReasonCode}。",
                exception);
        }
    }

    /// <summary>
    /// The registered protocol code for an executor refusal before any door IO. The slot codes are
    /// in the registry as they are; the executor's own recovery codes are not, and go to the
    /// registered code that says the same thing.
    /// </summary>
    /// <remarks>
    /// Every code it emits is written out as <c>return "CODE";</c>, never passed through from its
    /// input: ReasonCodeRegistryArchitectureTests scans this method by name for exactly that shape,
    /// so an unregistered code added here fails the build's tests.
    /// </remarks>
    private static string ResumeNotStartedReasonCode(string localCode)
    {
        switch (localCode)
        {
            case "SLOT_STATE_UNKNOWN":
                return "SLOT_STATE_UNKNOWN";
            case "LOCK_NOT_CLOSED":
                return "LOCK_NOT_CLOSED";
            case "UNLOCK_OUTPUT_NOT_RESET":
                return "UNLOCK_OUTPUT_NOT_RESET";
            case "SLOT_OPERATION_CONFLICT":
                return "SLOT_OPERATION_CONFLICT";
            case "SLOT_SET_INVALID":
                return "SLOT_SET_INVALID";
            case "RECOVERY_OPERATION_CONTEXT_MISSING":
                // Nothing on file to resume: the same answer the safety gate gives an unpersisted state.
                return "RECOVERY_SESSION_NOT_OPEN";
            default:
                // RECOVERY_STATE_MISMATCH, RECOVERY_COMMAND_INVALID and the field-shape refusals: the
                // command does not describe the scope the vehicle has on file.
                return "RECOVERY_SCOPE_MISMATCH";
        }
    }

    private static WireToGateOperationResultPayload CreateOperationResultPayload(
        WireToGateOperationExecutionResult result)
    {
        var withoutHash = new
        {
            result.DemandId,
            result.SlotOperationAttemptId,
            OperationType = result.OperationType == OperationType.Load ? "LOAD" : "UNLOAD",
            result.OverallOutcome,
            result.SlotResults,
            result.ObservedAt,
            result.JournalCheckpoint
        };
        string resultHash = WireToGateProtocolSerializer.ComputeSha256(
            JsonSerializer.SerializeToUtf8Bytes(withoutHash, JsonOptions));
        return new WireToGateOperationResultPayload(
            result.DemandId,
            result.SlotOperationAttemptId,
            result.OperationType == OperationType.Load ? "LOAD" : "UNLOAD",
            result.OverallOutcome,
            result.SlotResults.Select(slot => new WireToGateSlotResultPayload(
                slot.SlotNo,
                slot.Outcome,
                slot.FinalPhysicalState,
                slot.LockState,
                slot.UnlockOutputState,
                slot.ReasonCodes)).ToArray(),
            result.ObservedAt,
            result.JournalCheckpoint,
            resultHash);
    }

    private VehicleSafetySignal ReadVehicleSafety()
    {
        try
        {
            VehicleSafetySignal signal = _vehicleSafetySignalProvider.Read();
            return signal with
            {
                Source = string.IsNullOrWhiteSpace(signal.Source) ? "UNKNOWN" : signal.Source
            };
        }
        catch (Exception)
        {
            _logger.Write(
                LogSeverity.Error,
                nameof(WireToGateBusinessService),
                "读取车辆停稳信号失败，按未停稳处理。凭据和异常详情不会写入日志。");
            return new VehicleSafetySignal(
                VehicleMotionState.Unknown,
                DateTimeOffset.MinValue,
                "READ_ERROR");
        }
    }

    private SafetyEvaluation EvaluateSafety(IoSnapshot snapshot)
    {
        DateTimeOffset observedAt = _clock.Now;
        WireToGateSafetySummaryPayload safety = WireToGateSafetyEvaluator.Evaluate(
            snapshot,
            ReadVehicleSafety(),
            observedAt,
            _ioSnapshotMaxAge,
            _vehicleSafetyMaxAge,
            _vehicleSafetyClockSkewTolerance);
        string signature = JsonSerializer.Serialize(
            new
            {
                safety,
                slots = snapshot.Lockers
                    .OrderBy(locker => locker.PhysicalNumber)
                    .Select(locker => new
                    {
                        locker.PhysicalNumber,
                        locker.UnlockOutputRaw,
                        locker.LockFeedbackRaw,
                        locker.LightCurtainRaw
                    })
                    .ToArray()
            },
            JsonOptions);
        return new SafetyEvaluation(observedAt, safety, signature);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>The slots of a load whose doors are open or being reopened, waiting on the operator.</summary>
    private sealed record LoadAwaitingOperator(string SlotOperationAttemptId, IReadOnlyList<int> Slots);

    private sealed record SafetyEvaluation(
        DateTimeOffset ObservedAt,
        WireToGateSafetySummaryPayload Safety,
        string Signature);

    private sealed record SafetyChangeWork(
        long Version,
        DateTimeOffset ObservedAt,
        WireToGateSafetySummaryPayload Safety,
        IReadOnlyList<int> AffectedSlots,
        string Signature);

    private sealed class DelegateVehicleSafetySignalProvider : IVehicleSafetySignalProvider
    {
        private readonly Func<bool> _provider;

        public DelegateVehicleSafetySignalProvider(Func<bool> provider)
        {
            _provider = provider;
        }

        public VehicleSafetySignal Read() =>
            new(
                _provider() ? VehicleMotionState.Stopped : VehicleMotionState.Moving,
                DateTimeOffset.UtcNow,
                "LEGACY_BOOLEAN_ADAPTER");
    }
}
