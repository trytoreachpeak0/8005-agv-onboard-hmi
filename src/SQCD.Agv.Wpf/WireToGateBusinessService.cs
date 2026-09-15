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
            // ADR-cross-0058 决策 3：期限归服务端。车这边不算期限，只读服务端随作业清单
            // 发来的那个时刻，而且每一轮重新读——新的一份作业清单可以改写它。
            () => session.CurrentJourney.CurrentStopWorklist?.StationDepartureDeadlineAt);
        _vectorExecutor = new WireToGateRecoveryVectorExecutor(
            ioModule,
            session.Journal,
            clock,
            executorOptions);
    }

    public event EventHandler<ValueChangedEventArgs<WireToGateSublotEntryRequest>>? SublotEntryRequested;

    /// <summary>
    /// The entry request was withdrawn because a newer worklist superseded it. Raised after the
    /// journey projection changed, so anything bound to <see cref="CanSubmitSublot"/> has to look again.
    /// </summary>
    public event EventHandler<ValueChangedEventArgs<WireToGateSublotEntryRequest>>? SublotEntryExpired;

    public event EventHandler<ValueChangedEventArgs<WireToGateOperatorEvent>>? OperatorEventPublished;

    public bool CanSubmitSublot =>
        _session.Current.Readiness == WireToGateSessionReadiness.Ready
        && Volatile.Read(ref _currentEntryRequest) is not null;

    /// <summary>
    /// 当前可录入的子批集合，没有待录入请求时为空。自动化用它挑一个来提交；人工录入走扫码。
    /// </summary>
    public IReadOnlyList<string> ExpectedSublots =>
        Volatile.Read(ref _currentEntryRequest)?.ExpectedSublots ?? [];

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
        CancellationToken cancellationToken = default) =>
        (await RequestResumeAfterRepairOutcomeAsync(reason, cancellationToken).ConfigureAwait(false))
            .Accepted;

    private async Task<WireToGateRecoveryRequestOutcome> RequestResumeAfterRepairOutcomeAsync(
        string reason,
        CancellationToken cancellationToken)
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
                return WireToGateRecoveryRequestOutcome.Refused("RECOVERY_RESUME_DISABLED");
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
                return WireToGateRecoveryRequestOutcome.Refused("RECOVERY_ACTION_ALREADY_SELECTED");
            }

            if (activeRecovery
                && !recoverySnapshot!.AllowedActions.Contains(
                    "RESUME_AFTER_REPAIR",
                    StringComparer.Ordinal))
            {
                PublishOperatorResponse(
                    "RECOVERY_BLOCKED",
                    "当前恢复会话不允许恢复原仓位操作。 ");
                return WireToGateRecoveryRequestOutcome.Refused("RECOVERY_ACTION_NOT_ALLOWED");
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
            if (!string.Equals(opened.RequestId, requestId, StringComparison.Ordinal)
                || !string.Equals(opened.EventId, eventId, StringComparison.Ordinal)
                || !string.Equals(opened.DemandId, context.DemandId, StringComparison.Ordinal)
                || !AttemptMatches(opened.SlotOperationAttemptId, context)
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
            if (!string.Equals(accepted.RecoveryActionId, actionId, StringComparison.Ordinal)
                || !string.Equals(
                    accepted.ExceptionRecoverySessionId,
                    opened.ExceptionRecoverySessionId,
                    StringComparison.Ordinal)
                || !AttemptMatches(accepted.SlotOperationAttemptId, context)
                || accepted.AcceptedAction != "RESUME_AFTER_REPAIR")
            {
                throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
            }

            PublishOperatorResponse(
                "RECOVERY_ACTION_SUBMITTED",
                "恢复申请已通过服务端授权，等待下发原操作续作命令。 ");
            return WireToGateRecoveryRequestOutcome.Succeeded;
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
            return WireToGateRecoveryRequestOutcome.Refused(exception.Message);
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
        // 判据是集合归属，不是与某一个期望值相等。FR-001 AC-3 允许录入的任务其目标站点与操作员
        // 当前物理站点不完全相同，只要它在本次派车范围内；范围外仍然要拒（AC-4），但那是「不在这
        // 个集合里」，不是「不等于那一个字符串」。
        if (!request.EntryMethods.Contains(entryMethod, StringComparer.Ordinal)
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
            request.DemandId,
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
        _session.JourneyChanged += OnJourneyChanged;
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
            _session.JourneyChanged -= OnJourneyChanged;
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

    /// <summary>
    /// A worklist at a newer revision than the entry request withdraws the request: the server sends
    /// every SublotEntryRequested with expiresOnRevisionChange=true. Until this existed the vehicle
    /// ignored that, and a stop the server had already ended on its own -- the station wait expired, an
    /// operator cancellation was authorised, a recovery ended the last demand -- went on offering
    /// sublot entry and 取消装货, whose press was refused and whose second press dropped the
    /// connection (2026-09-15, agv01).
    /// </summary>
    /// <remarks>
    /// Only a strictly newer revision withdraws. A new round at the same stop publishes its worklist
    /// and then its entry request at one new revision, so a worklist never withdraws the request it
    /// announces; and a journal restore on reconnect replays revisions the request was issued against.
    /// </remarks>
    private void OnJourneyChanged(object? sender, ValueChangedEventArgs<WireToGateJourneySnapshot> args)
    {
        if (_disposed || args.Value.CurrentStopWorklist is not { } worklist)
        {
            return;
        }

        WireToGateSublotEntryRequest? entry = Volatile.Read(ref _currentEntryRequest);
        if (entry is null
            || worklist.Revision <= entry.WorklistRevision
            || Interlocked.CompareExchange(ref _currentEntryRequest, null, entry) != entry)
        {
            return;
        }

        PublishOperatorEvent(
            $"sublot-entry-expired:{entry.MessageId}",
            "SUBLOT_ENTRY_EXPIRED",
            worklist.Items.Count == 0
                ? "本站已结束，录入请求已撤销。"
                : "作业清单已更新，旧的录入请求已撤销。");
        SublotEntryExpired?.Invoke(
            this,
            new ValueChangedEventArgs<WireToGateSublotEntryRequest>(entry));
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
                $"上次{(context.OperationType == OperationType.Load ? "装货" : "卸货")}操作未完成：{FormatSlots(context.Slots)}，需要管理员恢复。",
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
    /// 日志里那一次未结算的 attempt 若没有人在执行、也从没交过结果，就按实时 IO 把它交成一份
    /// 结果（8005-agv-program#40）。返回 true 表示这里接手了它。
    /// </summary>
    /// <remarks>
    /// 那样的 attempt 只可能出自上一个进程：它开了锁、在等操作员时没了。之后两端互相等——车辆
    /// 只在收到命令时结算 attempt，服务端没有结果就不让旅程停摆，恢复入口又要求旅程已停摆——
    /// 所以结果必须由车辆这一端主动交。
    ///
    /// 判「没有人在执行」靠的是本进程自己的在途集合，不是日志：执行器挂在服务生命周期上、不跟
    /// 连接走，断网重连时它还在跑，而那时握手上报的日志与进程重启后一字不差。服务端从报文里分不出
    /// 这两种情况，这也是这件事必须修在车辆这一端的原因。先占住在途集合再查结果，免得与同一
    /// attempt 的命令处理并发。
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
            // 与 HandleSlotOperationAsync 同一个去重键：结果一旦进了持久发件箱，要么已被确认，
            // 要么随握手重放，已经交过的结论不重做。执行中途判 UNKNOWN 的那份也在这里被认出来。
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
                ? $"上次{FormatOperationType(command)}在执行中中断，{FormatSlots(command.Slots)}已按实时状态确认完成，正在上报结果。"
                : $"上次{FormatOperationType(command)}在执行中中断：{FormatSlots(command.Slots)}，未再开锁，需要管理员恢复。";
            PublishOperation(command, finalStage, guidance, "interrupted-final");
            try
            {
                // 会话此刻是 RecoveryRequired——正是因为这一次 attempt 没了结——所以走允许
                // RecoveryRequired 的那条发送路径；报文与 HandleSlotOperationAsync 发的是同一种
                // OperationResult、同一个 messageId。
                await _session.SendRecoveryOperationResultAsync(
                    resultKey,
                    attemptId,
                    CreateOperationResultPayload(execution),
                    cancellationToken).ConfigureAwait(false);
                if (completedSuccessfully)
                {
                    await _executor.MarkResultRecordedAsync(attemptId, cancellationToken)
                        .ConfigureAwait(false);
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

    private static string FormatOperationType(WireToGateSlotOperationCommand command) =>
        command.OperationType == OperationType.Load ? "装货" : "卸货";

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
                        $"收到子批录入请求：{string.Join('、', sublot.ExpectedSublots)}。");
                    SublotEntryRequested?.Invoke(
                        this,
                        new ValueChangedEventArgs<WireToGateSublotEntryRequest>(sublot));
                    break;
                case WireToGateExceptionRecoverySessionSnapshot recoverySnapshot:
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
                case WireToGateRecoveryCommand recovery:
                    if (recovery.MessageType == "SublotRejected")
                    {
                        // 服务端拒收了这次录入：说清原因、撤掉录入请求、让界面再刷一次按钮。原来这里只清请求，
                        // 然后落到下面的通用分支，操作员看到的是「恢复动作被安全策略阻断」，与子批毫不相干
                        // （8005-agv-program#86：旅程已结束之后到达的扫码，服务端现在明确拒收）。
                        WireToGateSublotEntryRequest? rejectedEntry =
                            Interlocked.Exchange(ref _currentEntryRequest, null);
                        using System.Text.Json.JsonDocument rejected =
                            System.Text.Json.JsonDocument.Parse(recovery.PayloadJson);
                        System.Text.Json.JsonElement problem = rejected.RootElement.GetProperty("problem");
                        string reasonCode = problem.GetProperty("reasonCode").GetString() ?? "SUBLOT_REJECTED";
                        string? displayMessage =
                            problem.TryGetProperty("displayMessage", out System.Text.Json.JsonElement display)
                            && display.ValueKind == System.Text.Json.JsonValueKind.String
                                ? display.GetString()
                                : null;
                        PublishOperatorEvent(
                            $"sublot-rejected:{recovery.MessageId}",
                            "SUBLOT_REJECTED",
                            string.IsNullOrWhiteSpace(displayMessage)
                                ? $"服务端拒收子批：{reasonCode}。"
                                : $"服务端拒收子批：{displayMessage}（{reasonCode}）。");
                        if (rejectedEntry is not null)
                        {
                            SublotEntryExpired?.Invoke(
                                this,
                                new ValueChangedEventArgs<WireToGateSublotEntryRequest>(rejectedEntry));
                        }

                        break;
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
            async Task SendProgress(
                string phase,
                IReadOnlyList<int> active,
                IReadOnlyList<int> completed,
                int promptRound,
                CancellationToken progressToken)
            {
                PublishOperation(
                    original,
                    MapOperationStage(phase),
                    OperationGuidance(original, phase, active, completed, promptRound),
                    $"recovery:{command.RecoveryActionId}:{OperationDetailKey(phase, active, completed, promptRound)}");
                await _session.SendRecoveryOperationProgressAsync(
                    command.SlotOperationAttemptId,
                    phase,
                    active,
                    completed,
                    cancellationToken: progressToken).ConfigureAwait(false);
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
                $"准备执行{(command.OperationType == OperationType.Load ? "装货" : "卸货")}：{FormatSlots(command.Slots)}。",
                "initial");
            async Task SendProgress(
                string phase,
                IReadOnlyList<int> active,
                IReadOnlyList<int> completed,
                int promptRound,
                CancellationToken progressToken)
            {
                PublishOperation(
                    command,
                    MapOperationStage(phase),
                    OperationGuidance(command, phase, active, completed, promptRound),
                    OperationDetailKey(phase, active, completed, promptRound));
                await _session.SendOperationProgressAsync(
                    command.SlotOperationAttemptId,
                    phase,
                    active,
                    completed,
                    cancellationToken: progressToken).ConfigureAwait(false);
            }

            WireToGateOperationExecutionResult execution = await _executor
                .ExecuteAsync(command, SendProgress, cancellationToken)
                .ConfigureAwait(false);
            WireToGateOperationResultPayload payload = CreateOperationResultPayload(execution);
            bool completedSuccessfully = string.Equals(
                execution.OverallOutcome,
                "COMPLETED",
                StringComparison.Ordinal);
            // ADR-cross-0058 决策 5：确定失败不进恢复。现场没有一件事是不确定的——每个仓位
            // 都报得出已知的占用状态、已闭的门与已复位的开锁输出——所以它不需要管理员。
            // 它也不需要操作员取消：服务端收到这份结果就自己把需求判 Cancelled、结束本站
            // （8005-agv-program#39）；出厂配置下这一刻也根本没有取消按钮——恢复入口关着，
            // 而这次 attempt 没有结算（8005-agv-onboard-hmi#39）。把它和 UNKNOWN 混在一起
            // 显示，操作员会去找一个根本不必来的人。
            bool determinateFailure = string.Equals(
                execution.OverallOutcome,
                "FAILED",
                StringComparison.Ordinal);
            WireToGateHmiOperationStage finalStage = completedSuccessfully
                ? WireToGateHmiOperationStage.Completed
                : determinateFailure
                    ? WireToGateHmiOperationStage.StationDeadlineExpired
                    : WireToGateHmiOperationStage.RecoveryRequired;
            PublishOperation(
                command,
                finalStage,
                completedSuccessfully
                    ? $"{FormatSlots(command.Slots)}操作完成，正在上报结果。"
                    : determinateFailure
                        ? $"{FormatSlots(command.Slots)}本站期限已过，货物未交接，服务端会结束本站，不需要操作。"
                        : $"{FormatSlots(command.Slots)}操作未完成，需要恢复处理。",
                "final");
            try
            {
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
                }
                PublishOperatorEvent(
                    $"operation-result:{command.SlotOperationAttemptId}:{execution.OverallOutcome}",
                    completedSuccessfully
                        ? "OPERATION_COMPLETED"
                        : determinateFailure
                            ? "OPERATION_STATION_DEADLINE_EXPIRED"
                            : "OPERATION_RECOVERY_REQUIRED",
                    completedSuccessfully
                        ? $"{FormatSlots(command.Slots)}操作结果已被服务端确认。"
                        : determinateFailure
                            ? $"{FormatSlots(command.Slots)}本站期限已过、货物未交接，服务端已收到结果并会结束本站。不需要管理员恢复，也不需要取消装货。"
                            : $"{FormatSlots(command.Slots)}操作失败或状态未知，服务端已收到结果，等待管理员恢复。",
                    new WireToGateHmiOperationSnapshot(
                        command.SlotOperationAttemptId,
                        command.OperationType,
                        command.Slots,
                        finalStage,
                        completedSuccessfully
                            ? "操作完成。"
                            : determinateFailure
                                ? "本站期限已过，货物未交接，等待服务端结束本站。"
                                : "操作需要管理员恢复。",
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
                            : finalStage,
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

    // 去重键必须随提示轮次变化。重开第 2 轮的 phase、active、completed 与第 1 轮
    // 完全相同，不掺进轮次的话 PublishOperatorEvent 会静默吞掉，操作员从第 2 次起
    // 再也看不到任何提示——而 ADR-cross-0058 决策 1 明说重开不设上限。
    internal static string OperationDetailKey(
        string phase,
        IReadOnlyList<int> active,
        IReadOnlyList<int> completed,
        int promptRound) =>
        $"{phase}:{string.Join(',', active)}:{string.Join(',', completed)}:{promptRound}";

    // promptRound 数的是「第几轮等操作员」，重开与提示节拍到期都算一轮，
    // 所以 UNLOCKING 的文案不报次数——那个数字不等于重开次数。
    internal static string OperationGuidance(
        WireToGateSlotOperationCommand command,
        string phase,
        IReadOnlyList<int> active,
        IReadOnlyList<int> completed,
        int promptRound) => phase switch
        {
            "PREPARING" => $"正在检查{FormatSlots(command.Slots)}的安全条件。",
            "UNLOCKING" => promptRound == 0
                ? $"正在打开{FormatSlots(active)}。"
                : $"{FormatSlots(active)}的货物状态与预期不符，正在重新打开。",
            "WAITING_OPERATOR" => (command.OperationType == OperationType.Load
                ? $"请向{FormatSlots(active)}放入货物并关门。"
                : $"请从{FormatSlots(active)}取出货物并关门。")
                + (promptRound == 0 ? string.Empty : $"（第{promptRound + 1}次提示）"),
            "VERIFYING" => $"正在核对仓门、货物和输出状态；已完成 {completed.Count}/{command.Slots.Count}。",
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
