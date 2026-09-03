using System.IO;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf;

/// <summary>
/// Wires formal server commands to the safe physical executor.  The legacy rule
/// gateway remains available for development compatibility, but this service is
/// the production WIRE_TO_GATE path for server-frozen slot commands and safety
/// checks. Unsupported recovery commands remain fail-closed and are projected to
/// the operator instead of disappearing into the file log.
/// </summary>
public sealed class WireToGateBusinessService : IAsyncDisposable
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
    private readonly WireToGateSlotOperationExecutor _executor;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _taskGate = new();
    private readonly object _operationAttemptGate = new();
    private readonly HashSet<Task> _tasks = [];
    private readonly HashSet<string> _operationAttempts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _publishedOperatorEventKeys = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _safetySendGate = new(1, 1);
    private WireToGateSublotEntryRequest? _currentEntryRequest;
    private SafetyChangeWork? _pendingSafetyChange;
    private string? _lastSafetySignature;
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
        TimeSpan? vehicleSafetyClockSkewTolerance = null)
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
            executorOptions);
    }

    public event EventHandler<ValueChangedEventArgs<WireToGateSublotEntryRequest>>? SublotEntryRequested;

    public event EventHandler<ValueChangedEventArgs<WireToGateOperatorEvent>>? OperatorEventPublished;

    public bool CanSubmitSublot =>
        _session.Current.Readiness == WireToGateSessionReadiness.Ready
        && Volatile.Read(ref _currentEntryRequest) is not null;

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
        if (!request.EntryMethods.Contains(entryMethod, StringComparer.Ordinal)
            || !string.Equals(request.ExpectedSublot, sublot.Trim(), StringComparison.Ordinal))
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
        PublishOperatorEvent(
            $"sublot-submitted:{messageId}",
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
        await _executor.DisposeAsync().ConfigureAwait(false);
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
        if (args.Value.Readiness != WireToGateSessionReadiness.Ready)
        {
            Volatile.Write(ref _currentEntryRequest, null);
        }

        if (CanPublishSafetyRevision(args.Value))
        {
            RequestSafetyStateChange();
        }
    }

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
                        $"收到子批录入请求：{sublot.ExpectedSublot}。");
                    SublotEntryRequested?.Invoke(
                        this,
                        new ValueChangedEventArgs<WireToGateSublotEntryRequest>(sublot));
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
                case WireToGateRecoveryCommand recovery:
                    if (recovery.MessageType == "SublotRejected")
                    {
                        Volatile.Write(ref _currentEntryRequest, null);
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
        WireToGateRecoverySafetyDecision decision =
            WireToGateRecoverySafetyPolicy.Evaluate(
                new WireToGateRecoverySafetyFacts(
                    // The command is emitted only after ControlServer has opened an
                    // authenticated exception-recovery session and accepted the
                    // recoveryActionId. Transport/session validation plus the formal IDs
                    // are the authorization proof available to the onboard peer.
                    RecoverySessionAuthorized: true,
                    RecoveryStatePersisted: WireToGateRecoverySafetyPolicy.MatchesPersistedResumeState(
                        state,
                        command.SlotOperationAttemptId,
                        command.ProvenRecoveryCheckpoint),
                    VehicleStopped: vehicle.MotionState == VehicleMotionState.Stopped,
                    VehicleSignalFresh: fresh,
                    AllTargetSlotsKnown: targetsKnown,
                    AllTargetSlotsLocked: targetsLocked,
                    AllUnlockOutputsReset: outputsReset));
        _logger.Write(
            LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"收到SlotOperationResumeCommand但未执行物理动作：attempt={command.SlotOperationAttemptId}，reason={decision.ReasonCode}。");
        PublishOperatorEvent(
            $"resume-command:{command.MessageId}",
            decision.Allowed ? "RECOVERY_AUTHORIZED" : "RECOVERY_BLOCKED",
            decision.Allowed
                ? "恢复命令已通过安全检查，但当前版本没有可安全收敛的续作结果路径，未执行第二次IO。"
                : $"恢复命令被安全门禁阻断：{decision.ReasonCode}。");
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
                CancellationToken progressToken)
            {
                PublishOperation(
                    command,
                    MapOperationStage(phase),
                    OperationGuidance(command, phase, active, completed),
                    $"{phase}:{string.Join(',', active)}:{string.Join(',', completed)}");
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
                await _session.SendOperationResultAsync(
                    operationDeduplicationKey,
                    command.SlotOperationAttemptId,
                    payload,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(execution.JournalCheckpoint, "NONE", StringComparison.Ordinal))
                {
                    await _executor.MarkResultRecordedAsync(
                        command.SlotOperationAttemptId,
                        cancellationToken).ConfigureAwait(false);
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
        PublishOperatorEvent(
            $"operation-stage:{command.SlotOperationAttemptId}:{stage}:{detailKey}",
            "OPERATION_PROGRESS",
            guidance,
            new WireToGateHmiOperationSnapshot(
                command.SlotOperationAttemptId,
                command.OperationType,
                command.Slots,
                stage,
                guidance,
                _clock.Now.ToUniversalTime()));
    }

    private void PublishOperatorEvent(
        string deduplicationKey,
        string kind,
        string message,
        WireToGateHmiOperationSnapshot? operation = null)
    {
        lock (_operationAttemptGate)
        {
            if (!_publishedOperatorEventKeys.Add(deduplicationKey))
            {
                return;
            }
        }

        OperatorEventPublished?.Invoke(
            this,
            new ValueChangedEventArgs<WireToGateOperatorEvent>(
                new WireToGateOperatorEvent(
                    _clock.Now.ToUniversalTime(),
                    kind,
                    message,
                    operation)));
    }

    private static WireToGateHmiOperationStage MapOperationStage(string phase) => phase switch
    {
        "PREPARING" => WireToGateHmiOperationStage.Preparing,
        "UNLOCKING" => WireToGateHmiOperationStage.Unlocking,
        "WAITING_OPERATOR" => WireToGateHmiOperationStage.WaitingOperator,
        "VERIFYING" => WireToGateHmiOperationStage.Verifying,
        "SAFE_FINISH" => WireToGateHmiOperationStage.Reporting,
        _ => WireToGateHmiOperationStage.Preparing
    };

    private static string OperationGuidance(
        WireToGateSlotOperationCommand command,
        string phase,
        IReadOnlyList<int> active,
        IReadOnlyList<int> completed) => phase switch
        {
            "PREPARING" => $"正在检查{FormatSlots(command.Slots)}的安全条件。",
            "UNLOCKING" => $"正在打开{FormatSlots(active)}。",
            "WAITING_OPERATOR" => command.OperationType == OperationType.Load
                ? $"请向{FormatSlots(active)}放入货物并关门。"
                : $"请从{FormatSlots(active)}取出货物并关门。",
            "VERIFYING" => $"正在核对仓门、货物和输出状态；已完成 {completed.Count}/{command.Slots.Count}。",
            "SAFE_FINISH" => "全部目标仓已达到安全收尾状态，正在上报结果。",
            _ => $"正在处理{FormatSlots(command.Slots)}。"
        };

    private static string FormatSlots(IReadOnlyList<int> slots) =>
        string.Join("、", slots.Order()) + "号仓";

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
