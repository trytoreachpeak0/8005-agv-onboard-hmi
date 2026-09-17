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
    private WireToGateExceptionRecoverySessionSnapshot? _recoverySessionSnapshot;
    private readonly object _recoverySessionAttemptGate = new();
    private (string ExceptionRecoverySessionId, string? SlotOperationAttemptId)? _recoverySessionAttempt;
    private string? _inconsistentRecoverySessionId;
    private WireToGateHmiOperationSnapshot? _currentOperationSnapshot;
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

    public bool CanSubmitSublot =>
        _session.Current.Readiness == WireToGateSessionReadiness.Ready
        && Volatile.Read(ref _currentEntryRequest) is not null;

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
    /// Read-only projection of the latest operation progress emitted by the
    /// business service. It never grants authority to perform business or IO
    /// actions.
    /// </summary>
    public WireToGateHmiOperationSnapshot? CurrentOperationSnapshot =>
        Volatile.Read(ref _currentOperationSnapshot);

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

    public async Task<bool> RequestResumeAfterRepairAsync(
        string reason = "现场维修完成，申请恢复原仓位操作。",
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
            string recoveryReason = (activeRecovery ? state.RecoveryReason : null) ?? reason;
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

            if (await TrySettleInterruptedOperationAsync(context, cancellationToken).ConfigureAwait(false))
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

    /// <summary>
    /// Settles the journal's unsettled attempt from the live IO when nobody is executing it and no
    /// result has ever been sent for it (8005-agv-program#40). Returns true when it took it over.
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
    private async Task<bool> TrySettleInterruptedOperationAsync(
        WireToGateRecoveryOperationContext context,
        CancellationToken cancellationToken)
    {
        string attemptId = context.SlotOperationAttemptId;
        lock (_operationAttemptGate)
        {
            if (!_operationAttempts.Add(attemptId))
            {
                return false;
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
                    .ConfigureAwait(false) is not null)
            {
                return false;
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

            return true;
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
                case WireToGateRecoveryCommand recovery:
                    if (recovery.MessageType == "SublotRejected")
                    {
                        Volatile.Write(ref _currentEntryRequest, null);
                    }
                    else if (recovery.MessageType is "LoadCorrectionRejected"
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
                PublishOperation(
                    original,
                    MapOperationStage(progress.Phase),
                    OperationGuidance(original, progress),
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

        try
        {
            PublishOperation(
                command,
                WireToGateHmiOperationStage.Preparing,
                $"准备执行{FormatOperationType(command.OperationType)}：{FormatSlots(command.Slots)}。",
                "initial");
            async Task SendProgress(WireToGateOperationProgress progress, CancellationToken progressToken)
            {
                PublishOperation(
                    command,
                    MapOperationStage(progress.Phase),
                    OperationGuidance(command, progress),
                    OperationDetailKey(progress));
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

            WireToGateOperationExecutionResult execution = await _executor
                .ExecuteAsync(command, SendProgress, cancellationToken)
                .ConfigureAwait(false);
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

                await _session.SendOperationResultAsync(
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
                // The result is already in the durable outbox.  A reconnect will replay
                // the same messageId/content; never execute the physical operation again.
                _logger.Write(
                    LogSeverity.Warning,
                    nameof(WireToGateBusinessService),
                    $"OperationResult暂未收到DurableAck：attempt={command.SlotOperationAttemptId}。",
                    exception);
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
        }
    }

    private void PublishOperation(
        WireToGateSlotOperationCommand command,
        WireToGateHmiOperationStage stage,
        string guidance,
        string detailKey)
    {
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
            operation);
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
        WireToGateHmiOperationSnapshot? operation = null)
    {
        if (operation is not null)
        {
            Volatile.Write(ref _currentOperationSnapshot, operation);
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

    // The text follows the cause the executor states, not a guess from the round number.
    private static string OperationGuidance(
        WireToGateSlotOperationCommand command,
        WireToGateOperationProgress progress) => progress.Phase switch
        {
            "PREPARING" => $"正在检查{FormatSlots(command.Slots)}的安全条件。",
            "UNLOCKING" => progress.Cause == WireToGatePromptCause.OppositeReopen
                ? $"{FormatSlots(progress.Active)}关门时货物状态与预期不符，正在重新打开。"
                : $"正在打开{FormatSlots(progress.Active)}。",
            "WAITING_OPERATOR" when progress.Cause == WireToGatePromptCause.ReopenHeldBySafety =>
                $"{FormatSlots(progress.Active)}关门时货物状态与预期不符，但车辆安全状态未确认（急停或未停稳），" +
                "暂不重新打开；安全状态恢复后自动打开。",
            "WAITING_OPERATOR" => (command.OperationType == OperationType.Load
                ? $"请向{FormatSlots(progress.Active)}放入货物并关门。"
                : $"请从{FormatSlots(progress.Active)}取出货物并关门。")
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
        await _session.SendDurableAsync(
            "SlotOperationCommandRejected",
            $"slot-operation-rejected:{command.SlotOperationAttemptId}:{reasonCode}",
            command.SlotOperationAttemptId,
            command.MessageId,
            payload,
            cancellationToken).ConfigureAwait(false);
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
