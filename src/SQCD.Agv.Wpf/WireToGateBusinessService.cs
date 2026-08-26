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
/// checks.  Unknown recovery commands are intentionally logged and left blocked.
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
    private readonly string _operatorIdEnvironmentVariable;
    private readonly TimeSpan _ioSnapshotMaxAge;
    private readonly WireToGateSlotOperationExecutor _executor;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _taskGate = new();
    private readonly object _operationAttemptGate = new();
    private readonly HashSet<Task> _tasks = [];
    private readonly HashSet<string> _operationAttempts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _safetySendGate = new(1, 1);
    private WireToGateSublotEntryRequest? _currentEntryRequest;
    private SafetyChangeWork? _pendingSafetyChange;
    private string? _lastSafetySignature;
    private long _nextSafetyStateVersion;
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
        IVehicleSafetySignalProvider? vehicleSafetySignalProvider = null)
    {
        _session = session;
        _ioModule = ioModule;
        _logger = logger;
        _clock = clock;
        _vehicleStoppedProvider = vehicleStoppedProvider;
        _vehicleSafetySignalProvider = vehicleSafetySignalProvider
            ?? new DelegateVehicleSafetySignalProvider(vehicleStoppedProvider);
        _operatorIdEnvironmentVariable = operatorIdEnvironmentVariable;
        _ioSnapshotMaxAge = executorOptions.IoSnapshotMaxAge;
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

        return await _session.SendSublotSubmittedAsync(
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
        TrackTask(QueueSafetyStateChangeAsync(_ioModule.CurrentSnapshot, _stopping.Token));
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
        else
        {
            TrackTask(QueueSafetyStateChangeAsync(_ioModule.CurrentSnapshot, _stopping.Token));
        }
    }

    private void OnIoSnapshotChanged(object? sender, ValueChangedEventArgs<IoSnapshot> args) =>
        TrackTask(QueueSafetyStateChangeAsync(args.Value, _stopping.Token));

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
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task QueueSafetyStateChangeAsync(
        IoSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (_disposed || _session.Current.Readiness != WireToGateSessionReadiness.Ready)
        {
            return;
        }

        await _safetySendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || _session.Current.Readiness != WireToGateSessionReadiness.Ready)
            {
                return;
            }

            SafetyEvaluation evaluation = EvaluateSafety(snapshot);
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
            _nextSafetyStateVersion = pending.Version + 1;
            _pendingSafetyChange = null;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                "SafetyStateChanged暂未收到DurableAck，将使用同一版本和内容重试。",
                exception);
        }
        finally
        {
            _safetySendGate.Release();
        }
    }

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
            && vehicle.IsFresh(now, _ioSnapshotMaxAge);
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
                    RecoverySessionAuthorized: false,
                    RecoveryStatePersisted: state.ProvenRecoveryCheckpoint is not WireToGateRecoveryCheckpoint.None,
                    VehicleStopped: vehicle.MotionState == VehicleMotionState.Stopped,
                    VehicleSignalFresh: fresh,
                    AllTargetSlotsKnown: targetsKnown,
                    AllTargetSlotsLocked: targetsLocked,
                    AllUnlockOutputsReset: outputsReset));
        _logger.Write(
            LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"收到SlotOperationResumeCommand但未执行物理动作：attempt={command.SlotOperationAttemptId}，reason={decision.ReasonCode}。");
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
            async Task SendProgress(
                string phase,
                IReadOnlyList<int> active,
                IReadOnlyList<int> completed,
                CancellationToken progressToken) =>
                await _session.SendOperationProgressAsync(
                    command.SlotOperationAttemptId,
                    phase,
                    active,
                    completed,
                    cancellationToken: progressToken).ConfigureAwait(false);

            WireToGateOperationExecutionResult execution = await _executor
                .ExecuteAsync(command, SendProgress, cancellationToken)
                .ConfigureAwait(false);
            WireToGateOperationResultPayload payload = CreateOperationResultPayload(execution);
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

    private async Task HandlePreDepartureSafetyCheckAsync(
        WireToGatePreDepartureSafetyCheck command,
        CancellationToken cancellationToken)
    {
        SafetyEvaluation evaluation = EvaluateSafety(_ioModule.CurrentSnapshot);
        bool safe = evaluation.Safety.DepartureSafe;
        await _session.SendPreDepartureSafetyCheckResultAsync(
            command.PreDepartureSafetyCheckId,
            safe ? "SAFE" : evaluation.Safety.UnknownPresent ? "UNKNOWN" : "UNSAFE",
            evaluation.ObservedAt,
            _nextSafetyStateVersion - 1,
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
        catch (Exception exception)
        {
            _logger.Write(
                LogSeverity.Error,
                nameof(WireToGateBusinessService),
                "读取车辆停稳信号失败，按未停稳处理。",
                exception);
            return new VehicleSafetySignal(
                VehicleMotionState.Unknown,
                DateTimeOffset.MinValue,
                "READ_ERROR");
        }
    }

    private SafetyEvaluation EvaluateSafety(IoSnapshot snapshot)
    {
        DateTimeOffset observedAt = _clock.Now;
        bool fresh = snapshot.IsConnected
            && SafetyRules.IsSnapshotFresh(snapshot, observedAt, _ioSnapshotMaxAge);
        bool unknown = !fresh
            || snapshot.Lockers.Count != 8
            || snapshot.Lockers.Any(locker => !locker.IsKnown);
        bool allLocked = !unknown && snapshot.Lockers.All(locker => locker.IsLocked);
        bool allOutputsReset = !unknown && snapshot.Lockers.All(locker => locker.UnlockOutputRaw is false);
        VehicleSafetySignal vehicleSignal = ReadVehicleSafety();
        bool vehicleFresh = vehicleSignal.IsFresh(observedAt, _ioSnapshotMaxAge);
        bool vehicleStopped = vehicleSignal.MotionState == VehicleMotionState.Stopped && vehicleFresh;
        bool vehicleUnknown = !vehicleFresh || vehicleSignal.MotionState == VehicleMotionState.Unknown;
        List<string> reasons = [];
        if (unknown)
        {
            reasons.Add("SLOT_STATE_UNKNOWN");
        }

        if (!allLocked)
        {
            reasons.Add("LOCK_NOT_CLOSED");
        }

        if (!allOutputsReset)
        {
            reasons.Add("UNLOCK_OUTPUT_NOT_RESET");
        }

        if (vehicleUnknown)
        {
            reasons.Add("VEHICLE_STATE_UNKNOWN");
        }
        else if (!vehicleStopped)
        {
            reasons.Add("ACTION_NOT_ALLOWED_IN_STATE");
        }

        WireToGateSafetySummaryPayload safety = new(
            !unknown && allLocked && allOutputsReset && vehicleStopped,
            vehicleStopped,
            allLocked,
            allOutputsReset,
            unknown || vehicleUnknown,
            reasons.Distinct(StringComparer.Ordinal).ToArray());
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
