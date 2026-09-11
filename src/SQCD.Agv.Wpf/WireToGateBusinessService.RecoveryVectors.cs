using System.IO;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

public sealed partial class WireToGateBusinessService
{
    private const string CompensateLoadAction = "COMPENSATE_LOAD_ALL_EMPTY";
    private const string FaultCargoHandoffAction = "FAULT_CARGO_HANDOFF";

    private readonly WireToGateRecoveryVectorExecutor _vectorExecutor;
    private WireToGateRecoveryState _lastRecoveryState = WireToGateRecoveryState.Empty;

    public bool CanRequestLoadCancellation =>
        (CanUseRecoveryOperator(requireProof: false)
            && HasRecoveryVectorOrLoadOperation(WireToGateRecoveryVectorTypes.LoadCancellation))
        || (CanUseStopOperator() && HasSublotEntryPending);

    /// <summary>
    /// The stop is waiting for a sublot and nothing has been commanded to a slot yet. This is
    /// exactly the moment an operator discovers the stop has nothing to load, and until now it was
    /// the one moment with no way out: the entry stayed open, the journey held the vehicle and the
    /// pickup station, and the cancellation button only appeared once a load was already underway.
    /// The entry request carries the demand, so nothing else is needed to raise it.
    /// </summary>
    private bool HasSublotEntryPending =>
        Volatile.Read(ref _lastRecoveryState).RecoveryVector is null
        && Volatile.Read(ref _lastRecoveryState).OperationContext is null
        && CanSubmitSublot;

    public bool CanRequestLoadCompensation =>
        CanUseRecoveryOperator(requireProof: true)
        && CanRequestRecoveryAction(
            CompensateLoadAction,
            WireToGateRecoveryVectorTypes.LoadCompensation);

    public bool CanRequestLoadCorrection =>
        CanUseRecoveryOperator(requireProof: false)
        && HasRecoveryVectorOrCompletedLoad(WireToGateRecoveryVectorTypes.LoadCorrection);

    public bool CanRequestFaultCargoHandoff =>
        CanUseRecoveryOperator(requireProof: true)
        && CanRequestRecoveryAction(
            FaultCargoHandoffAction,
            WireToGateRecoveryVectorTypes.FaultCargoHandoff);

    public async Task<bool> RequestLoadCancellationAsync(
        string reason = "现场确认装货取消，申请将目标仓位清空。",
        CancellationToken cancellationToken = default) =>
        (await RequestRecoveryAsync(
                OnboardAutomationRecoveryActions.LoadCancellation,
                reason,
                cancellationToken)
            .ConfigureAwait(false)).Accepted;

    public async Task<bool> RequestLoadCompensationAsync(
        string reason = "现场确认装货无法继续，申请补偿清空目标仓位。",
        CancellationToken cancellationToken = default) =>
        (await RequestRecoveryAsync(
                OnboardAutomationRecoveryActions.CompensateLoadAllEmpty,
                reason,
                cancellationToken)
            .ConfigureAwait(false)).Accepted;

    public async Task<bool> RequestLoadCorrectionAsync(
        string reason = "现场确认需要修正已完成的装货结果。",
        CancellationToken cancellationToken = default) =>
        (await RequestRecoveryAsync(
                OnboardAutomationRecoveryActions.LoadCorrection,
                reason,
                cancellationToken)
            .ConfigureAwait(false)).Accepted;

    public async Task<bool> RequestFaultCargoHandoffAsync(
        string reason = "现场确认故障仓货物需要交接处理。",
        CancellationToken cancellationToken = default) =>
        (await RequestRecoveryAsync(
                OnboardAutomationRecoveryActions.FaultCargoHandoff,
                reason,
                cancellationToken)
            .ConfigureAwait(false)).Accepted;

    /// <summary>
    /// Whether the HMI would currently offer the button for <paramref name="action"/>. The
    /// automation face gates on this and on nothing looser, so a script is offered exactly the
    /// buttons an operator is. That matters beyond symmetry: for compensation and fault-cargo
    /// handoff this predicate is the only place <c>recoveryResumeEnabled</c> is enforced -- their
    /// request paths never re-check it.
    /// </summary>
    public bool CanRequestRecovery(string action) => action switch
    {
        OnboardAutomationRecoveryActions.ResumeAfterRepair => CanRequestResumeAfterRepair,
        OnboardAutomationRecoveryActions.CompensateLoadAllEmpty => CanRequestLoadCompensation,
        OnboardAutomationRecoveryActions.FaultCargoHandoff => CanRequestFaultCargoHandoff,
        OnboardAutomationRecoveryActions.LoadCancellation => CanRequestLoadCancellation,
        OnboardAutomationRecoveryActions.LoadCorrection => CanRequestLoadCorrection,
        _ => false
    };

    /// <summary>
    /// Names the first precondition keeping <paramref name="action"/>'s button hidden, for a caller
    /// with no screen to look at. The order is the order they would have to be fixed in. When all
    /// of them hold it is the recovery state itself that does not admit the action, and the code
    /// says no more than that.
    /// </summary>
    public string DiagnoseRecoveryUnavailable(string action)
    {
        WireToGateSessionSnapshot session = _session.Current;
        if (!session.Connected
            || session.Readiness is not (WireToGateSessionReadiness.Ready
                or WireToGateSessionReadiness.RecoveryRequired))
        {
            return "WIRE_TO_GATE_NOT_READY";
        }

        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable)))
        {
            return "WIRE_TO_GATE_OPERATOR_NOT_READY";
        }

        // Cancelling a stop with nothing loaded is the one request that does not need the recovery
        // window (see CanUseStopOperator). Reaching here for it means that short path is not open
        // either, and every other path to a cancellation does need the window.
        if (!_recoveryOptions.ResumeAfterRepairEnabled)
        {
            return "RECOVERY_RESUME_DISABLED";
        }

        bool requiresProof = action is OnboardAutomationRecoveryActions.ResumeAfterRepair
            or OnboardAutomationRecoveryActions.CompensateLoadAllEmpty
            or OnboardAutomationRecoveryActions.FaultCargoHandoff;
        if (requiresProof
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                _recoveryOptions.AuthenticationProofEnvironmentVariable)))
        {
            return "RECOVERY_AUTHENTICATION_REQUIRED";
        }

        return "RECOVERY_ACTION_NOT_AVAILABLE";
    }

    /// <summary>
    /// The request behind each recovery button, keeping the refusal's reason code instead of
    /// collapsing it into a boolean. The buttons drop the code -- the operator reads it from the
    /// operator event -- and the automation face returns it.
    /// </summary>
    public Task<WireToGateRecoveryRequestOutcome> RequestRecoveryAsync(
        string action,
        string reason,
        CancellationToken cancellationToken = default) => action switch
        {
            OnboardAutomationRecoveryActions.ResumeAfterRepair =>
                RequestResumeAfterRepairOutcomeAsync(reason, cancellationToken),
            OnboardAutomationRecoveryActions.CompensateLoadAllEmpty => RunRecoveryRequestAsync(
                WireToGateRecoveryVectorTypes.LoadCompensation,
                () => RequestRecoveryActionVectorCoreAsync(
                    CompensateLoadAction,
                    WireToGateRecoveryVectorTypes.LoadCompensation,
                    reason,
                    cancellationToken),
                cancellationToken),
            OnboardAutomationRecoveryActions.FaultCargoHandoff => RunRecoveryRequestAsync(
                WireToGateRecoveryVectorTypes.FaultCargoHandoff,
                () => RequestRecoveryActionVectorCoreAsync(
                    FaultCargoHandoffAction,
                    WireToGateRecoveryVectorTypes.FaultCargoHandoff,
                    reason,
                    cancellationToken),
                cancellationToken),
            OnboardAutomationRecoveryActions.LoadCancellation => RunRecoveryRequestAsync(
                WireToGateRecoveryVectorTypes.LoadCancellation,
                () => RequestLoadCancellationCoreAsync(reason, cancellationToken),
                cancellationToken),
            OnboardAutomationRecoveryActions.LoadCorrection => RunRecoveryRequestAsync(
                WireToGateRecoveryVectorTypes.LoadCorrection,
                () => RequestLoadCorrectionCoreAsync(reason, cancellationToken),
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

    private async Task<WireToGateRecoveryRequestOutcome> RunRecoveryRequestAsync(
        string vectorType,
        Func<Task<WireToGateRecoveryRequestOutcome>> action,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _recoveryRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
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
                $"恢复向量{vectorType}未执行：reason={exception.Message}。",
                exception);
            PublishOperatorEvent(
                $"recovery-vector-request-failed:{vectorType}:{exception.Message}",
                "RECOVERY_BLOCKED",
                $"恢复向量被阻断：{exception.Message}。请确认车辆停稳、仓门状态和服务端授权。 ");
            return WireToGateRecoveryRequestOutcome.Refused(exception.Message);
        }
        finally
        {
            _recoveryRequestGate.Release();
        }
    }

    /// <summary>
    /// Cancelling a stop that has nothing to load is ordinary stop work, not recovery. It opens no
    /// slot, needs no authentication proof, and leaves no physical state behind for anyone to
    /// reconcile. Gating it on <c>ResumeAfterRepairEnabled</c> -- which ships false, so the entry
    /// would never appear on a production vehicle -- would leave the defect this path exists to
    /// fix exactly where it was. The session still has to be usable and the operator still has to
    /// be identified.
    /// </summary>
    private bool CanUseStopOperator()
    {
        WireToGateSessionSnapshot session = _session.Current;
        return session.Connected
            && session.Readiness == WireToGateSessionReadiness.Ready
            && !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable));
    }

    private bool CanUseRecoveryOperator(bool requireProof)
    {
        if (!_recoveryOptions.ResumeAfterRepairEnabled)
        {
            return false;
        }

        WireToGateSessionSnapshot session = _session.Current;
        if (!session.Connected
            || session.Readiness is not (WireToGateSessionReadiness.Ready
                or WireToGateSessionReadiness.RecoveryRequired))
        {
            return false;
        }

        string? operatorId = Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(operatorId))
        {
            return false;
        }

        return !requireProof
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                _recoveryOptions.AuthenticationProofEnvironmentVariable));
    }

    private bool HasRecoveryVectorOrLoadOperation(string vectorType)
    {
        WireToGateRecoveryState state = Volatile.Read(ref _lastRecoveryState);
        if (state.RecoveryVector is { } vector)
        {
            return vector.VectorType == vectorType;
        }

        return state.OperationContext is { OperationType: OperationType.Load } context
            && string.Equals(
                state.UnsettledSlotOperationAttemptId,
                context.SlotOperationAttemptId,
                StringComparison.Ordinal);
    }

    private bool HasRecoveryVectorOrCompletedLoad(string vectorType)
    {
        WireToGateRecoveryState state = Volatile.Read(ref _lastRecoveryState);
        return state.RecoveryVector?.VectorType == vectorType
            || state.LastCompletedLoadOperationContext is not null;
    }

    private bool CanRequestRecoveryAction(string action, string vectorType)
    {
        WireToGateRecoveryState state = Volatile.Read(ref _lastRecoveryState);
        if (state.RecoveryVector is { } vector)
        {
            return vector.VectorType == vectorType;
        }

        if (FindRecoveryLoadOperation(state) is not { } context)
        {
            return false;
        }

        WireToGateExceptionRecoverySessionSnapshot? snapshot =
            Volatile.Read(ref _recoverySessionSnapshot);
        if (snapshot is null || snapshot.State == "CLOSED")
        {
            return _session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired;
        }

        return snapshot.SelectedAction is null
            && snapshot.AllowedActions.Contains(action, StringComparer.Ordinal)
            && string.Equals(snapshot.DemandId, context.DemandId, StringComparison.Ordinal)
            && snapshot.Slots.SequenceEqual(context.Slots);
    }

    private async Task<WireToGateRecoveryRequestOutcome> RequestLoadCancellationCoreAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        // 先把在途的仓位操作停下来，再读日志。ADR-cross-0058 决策 1 之后目标态闭环没有
        // 自然终点，而取消向量走的是另一个执行器、另一把锁——不中止就会有两个执行器同时
        // 驱动同一个 IO 模块，一个还在循环脉冲开锁，另一个在验证仓位清空。
        //
        // 中止排在请求授权之前，代价是服务端若拒绝这次取消，操作已经停了、仓门可能还开着。
        // 那种局面操作员就在车前，看得见也关得上，而且被拒绝的取消本来就要人处理；反过来
        // 让两个执行器对同一把机械锁并发发脉冲，是没人看得见的。
        _executor.AbortActiveOperation();
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state.RecoveryVector is { } existingVector)
        {
            if (existingVector.VectorType != WireToGateRecoveryVectorTypes.LoadCancellation)
            {
                throw new InvalidDataException("RECOVERY_VECTOR_CONFLICT");
            }

            return await ExecuteRecoveryVectorAndReportAsync(
                    existingVector,
                    correction: false,
                    cancellationToken,
                    result => SendRecoveryVectorResultAsync(
                        existingVector,
                        $"recovery-vector-result:{WireToGateRecoveryVectorTypes.LoadCancellation}:{existingVector.PrimaryId}",
                        result,
                        cancellationToken))
                .ConfigureAwait(false);
        }

        // Nothing commanded to a slot: the cancellation is the short path below, which finishes at
        // the authorization. Deciding it here rather than inside the long path keeps
        // RequireUnsettledLoadOperation as the hard guard it is -- reaching it without an operation
        // is still a bug, just no longer this one.
        if (state.OperationContext is null && state.UnsettledSlotOperationAttemptId is null)
        {
            return await RequestLoadCancellationBeforeLoadAsync(reason, cancellationToken)
                .ConfigureAwait(false);
        }

        WireToGateRecoveryOperationContext operation = RequireUnsettledLoadOperation(state);
        WireToGateOperatorContextPayload operatorContext = ReadOperatorContext();
        string cancellationId = StableUuid(
            $"{operation.DemandId}|{operation.SlotOperationAttemptId}|load-cancellation");
        LoadCancellationStartRequestedPayload request = new(
            cancellationId,
            operation.DemandId,
            operation.SlotOperationAttemptId,
            operatorContext,
            RequireReason(reason));
        LoadCancellationAuthorizationPayload authorization = await _session
            .RequestLoadCancellationStartAsync(cancellationId, request, cancellationToken)
            .ConfigureAwait(false);
        if (authorization.Decision == "REJECTED")
        {
            PublishOperatorEvent(
                $"load-cancellation-rejected:{cancellationId}",
                "RECOVERY_BLOCKED",
                $"服务端拒绝装货取消：{authorization.Problem?.ReasonCode ?? "ACTION_NOT_ALLOWED_IN_STATE"}。 ");
            return WireToGateRecoveryRequestOutcome.Refused(
                authorization.Problem?.ReasonCode ?? "ACTION_NOT_ALLOWED_IN_STATE");
        }

        if (authorization.Slots.Count == 0
            || !authorization.Slots.SequenceEqual(operation.Slots))
        {
            throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
        }

        WireToGateRecoveryVectorContext vector = new(
            WireToGateRecoveryVectorTypes.LoadCancellation,
            authorization.CancellationId,
            null,
            authorization.DemandId,
            authorization.SlotOperationAttemptId,
            null,
            authorization.Slots,
            null,
            operatorContext.OperatorId,
            operatorContext.VerificationMethod,
            operatorContext.VerifiedAt);
        await WriteRecoveryVectorPreparedAsync(state, vector, cancellationToken)
            .ConfigureAwait(false);
        PublishOperatorEvent(
            $"load-cancellation-authorized:{vector.PrimaryId}",
            "RECOVERY_VECTOR_AUTHORIZED",
            $"装货取消已获服务端授权，开始将{FormatSlots(vector.Slots)}清空。 ");
        return await ExecuteRecoveryVectorAndReportAsync(
                vector,
                correction: false,
                cancellationToken,
                result => SendRecoveryVectorResultAsync(
                    vector,
                    $"recovery-vector-result:{WireToGateRecoveryVectorTypes.LoadCancellation}:{vector.PrimaryId}",
                    result,
                    cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels a stop that has nothing to load, before any slot operation was commanded. It is a
    /// far shorter path than the cancellation that interrupts a load, and deliberately so: no door
    /// was opened, so there is no emptiness to prove, no recovery vector to journal and no
    /// LoadCancellationResult to send -- that message could not carry this case anyway, its
    /// slotResults being minItems 1. The authorization ends the journey on the server, and the
    /// operator sees the stop clear.
    /// </summary>
    private async Task<WireToGateRecoveryRequestOutcome> RequestLoadCancellationBeforeLoadAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        WireToGateSublotEntryRequest request = Volatile.Read(ref _currentEntryRequest)
            ?? throw new InvalidOperationException("WIRE_TO_GATE_JOURNEY_NOT_READY");
        WireToGateOperatorContextPayload operatorContext = ReadOperatorContext();
        string cancellationId = StableUuid($"{request.DemandId}|before-load|load-cancellation");
        LoadCancellationStartRequestedPayload payload = new(
            cancellationId,
            request.DemandId,
            null,
            operatorContext,
            RequireReason(reason));
        LoadCancellationAuthorizationPayload authorization = await _session
            .RequestLoadCancellationStartAsync(cancellationId, payload, cancellationToken)
            .ConfigureAwait(false);
        if (authorization.Decision == "REJECTED")
        {
            PublishOperatorEvent(
                $"load-cancellation-rejected:{cancellationId}",
                "RECOVERY_BLOCKED",
                $"服务端拒绝取消本站装货：{authorization.Problem?.ReasonCode ?? "ACTION_NOT_ALLOWED_IN_STATE"}。 ");
            return WireToGateRecoveryRequestOutcome.Refused(
                authorization.Problem?.ReasonCode ?? "ACTION_NOT_ALLOWED_IN_STATE");
        }

        // An authorization naming slots or an attempt would mean the server matched this request to
        // a load that is actually underway, and clearing those slots is not what this path does.
        if (authorization.Slots.Count != 0 || authorization.SlotOperationAttemptId is not null)
        {
            throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
        }

        PublishOperatorEvent(
            $"load-cancellation-before-load:{cancellationId}",
            "RECOVERY_VECTOR_AUTHORIZED",
            "本站装货已取消，车辆可以接下一单。 ");
        return WireToGateRecoveryRequestOutcome.Succeeded;
    }

    private async Task<WireToGateRecoveryRequestOutcome> RequestLoadCorrectionCoreAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken)
            .ConfigureAwait(false);
        WireToGateRecoveryVectorContext? existingVector = state.RecoveryVector;
        if (existingVector is not null
            && existingVector.VectorType != WireToGateRecoveryVectorTypes.LoadCorrection)
        {
            throw new InvalidDataException("RECOVERY_VECTOR_CONFLICT");
        }

        WireToGateRecoveryVectorContext vector;
        if (existingVector is not null)
        {
            vector = existingVector;
        }
        else
        {
            WireToGateRecoveryOperationContext operation =
                state.LastCompletedLoadOperationContext
                ?? throw new InvalidOperationException("LOAD_CORRECTION_OPERATION_NOT_AVAILABLE");
            WireToGateOperatorContextPayload operatorContext = ReadOperatorContext();
            string correctionId = StableUuid(
                $"{operation.DemandId}|{operation.SlotOperationAttemptId}|load-correction");
            vector = new(
                WireToGateRecoveryVectorTypes.LoadCorrection,
                correctionId,
                null,
                operation.DemandId,
                operation.SlotOperationAttemptId,
                null,
                operation.Slots,
                null,
                operatorContext.OperatorId,
                operatorContext.VerificationMethod,
                operatorContext.VerifiedAt);
            await WriteRecoveryVectorPreparedAsync(state, vector, cancellationToken)
                .ConfigureAwait(false);
        }

        WireToGateOperatorContextPayload context = RequirePersistedOperator(vector);
        string requestId = StableUuid($"{vector.PrimaryId}|load-correction-request");
        await _session.RequestLoadCorrectionAsync(
                requestId,
                new LoadCorrectionRequestedPayload(
                    vector.PrimaryId,
                    vector.DemandId,
                    vector.SlotOperationAttemptId
                        ?? throw new InvalidDataException("RECOVERY_COMMAND_INVALID"),
                    vector.Slots,
                    context,
                    RequireReason(reason)),
                cancellationToken)
            .ConfigureAwait(false);
        PublishOperatorEvent(
            $"load-correction-requested:{vector.PrimaryId}",
            "RECOVERY_VECTOR_REQUESTED",
            $"已提交{FormatSlots(vector.Slots)}装货修正请求，等待服务端下发修正命令。 ");
        return WireToGateRecoveryRequestOutcome.Succeeded;
    }

    private async Task<WireToGateRecoveryRequestOutcome> RequestRecoveryActionVectorCoreAsync(
        string action,
        string vectorType,
        string reason,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken)
            .ConfigureAwait(false);
        WireToGateRecoveryOperationContext operation = FindRecoveryLoadOperation(state)
            ?? throw new InvalidOperationException("RECOVERY_OPERATION_CONTEXT_MISSING");

        WireToGateRecoveryVectorContext? vector = state.RecoveryVector;
        if (vector is not null && vector.VectorType != vectorType)
        {
            throw new InvalidDataException("RECOVERY_VECTOR_CONFLICT");
        }

        WireToGateExceptionRecoverySessionSnapshot? snapshot =
            Volatile.Read(ref _recoverySessionSnapshot);
        if (vector is not null)
        {
            if (snapshot is null || snapshot.State == "CLOSED")
            {
                throw new InvalidOperationException("RECOVERY_SESSION_STATE_PENDING");
            }

            ValidateRecoverySessionSnapshot(snapshot, operation);
            if (snapshot.SelectedAction is not null
                && !string.Equals(snapshot.SelectedAction, action, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("RECOVERY_ACTION_ALREADY_SELECTED");
            }
        }

        string? proof = null;
        WireToGateOperatorContextPayload operatorContext;
        string actionReason;
        string requestId;
        string eventId;
        ExceptionRecoverySessionOpenedPayload opened;
        string actionId;

        // Every recovery request below leaves with a messageId of its own, never one an earlier
        // press already used. The server's ProtocolInbox binds a messageId to the exact bytes it
        // first carried and to its first answer, and no press here reproduces those bytes -- sentAt
        // is always new, and so is the session generation after a reconnect. Reusing one therefore
        // ends in one of two ways: the old answer comes back (a refusal stays a refusal forever) or
        // the content conflicts and the server drops the connection. The logical identity lives in
        // the payload instead -- recoveryActionId, which the server deduplicates by business content
        // and records nothing for when it refuses.
        string actionMessageId = Guid.NewGuid().ToString("D");

        if (vector is not null)
        {
            // The action was prepared and sent, and no answer came back. If the server did accept
            // it, the retry must match the accepted business content exactly, so it carries the
            // persisted operator and reason rather than this press's.
            operatorContext = RequirePersistedOperator(vector);
            actionReason = state.RecoveryReason ?? RequireReason(reason);
            opened = new(
                state.RecoverySessionRequestId
                    ?? throw new InvalidDataException("RECOVERY_SESSION_REQUEST_MISSING"),
                vector.ExceptionRecoverySessionId
                    ?? throw new InvalidDataException("RECOVERY_SESSION_SCOPE_MISMATCH"),
                snapshot!.SentAt,
                snapshot.EventId,
                operation.DemandId,
                snapshot.SlotOperationAttemptId,
                operation.Slots,
                snapshot.RecoverySessionRevision);
            requestId = opened.RequestId;
            eventId = opened.EventId;
            actionId = vector.PrimaryId;
        }
        else
        {
            operatorContext = ReadOperatorContext();
            actionReason = RequireReason(reason);
            proof = ReadRecoveryProof();
            bool activeSession = snapshot is not null && snapshot.State != "CLOSED";
            if (!activeSession && _session.Current.Readiness != WireToGateSessionReadiness.RecoveryRequired)
            {
                throw new InvalidOperationException("RECOVERY_SESSION_NOT_READY");
            }

            // A session request is rebuilt from this press -- operator, verifiedAt, reason -- so it is
            // a new message and takes a new id; deriving the id from the attempt meant that one
            // refusal refused that attempt for good. Whether an earlier press did open a session is
            // the snapshot's to say, and while it is still on its way the server answers a second
            // request with RECOVERY_SESSION_ALREADY_OPEN rather than opening another.
            requestId = activeSession
                ? state.RecoverySessionRequestId ?? Guid.NewGuid().ToString("D")
                : Guid.NewGuid().ToString("D");
            eventId = activeSession ? snapshot!.EventId : requestId;
            if (activeSession)
            {
                ValidateRecoverySessionSnapshot(snapshot!, operation);
                opened = new(
                    requestId,
                    snapshot!.ExceptionRecoverySessionId,
                    snapshot.SentAt,
                    snapshot.EventId,
                    snapshot.DemandId,
                    snapshot.SlotOperationAttemptId,
                    snapshot.Slots,
                    snapshot.RecoverySessionRevision);
            }
            else
            {
                if (state.ExceptionRecoverySessionId is not null)
                {
                    throw new InvalidOperationException("RECOVERY_SESSION_STATE_PENDING");
                }

                await WriteRecoveryStateCachedAsync(
                        state with
                        {
                            RecoverySessionRequestId = requestId,
                            RecoveryReason = actionReason,
                            RecoveryOperatorId = operatorContext.OperatorId,
                            RecoveryOperatorVerifiedAt = operatorContext.VerifiedAt
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                opened = await _session.RequestExceptionRecoverySessionAsync(
                        requestId,
                        new ExceptionRecoverySessionRequestedPayload(
                            requestId,
                            operatorContext,
                            _recoveryOptions.AdministratorRole,
                            eventId,
                            operation.DemandId,
                            operation.Slots,
                            actionReason,
                            proof),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            ValidateOpenedRecoverySession(opened, requestId, eventId, operation);
            actionId = state.RecoveryActionId
                ?? StableUuid($"{opened.ExceptionRecoverySessionId}|{action}");
            string? handoffId = action == FaultCargoHandoffAction
                ? StableUuid($"{actionId}|fault-cargo-handoff")
                : null;
            vector = new(
                vectorType,
                actionId,
                opened.ExceptionRecoverySessionId,
                operation.DemandId,
                operation.SlotOperationAttemptId,
                handoffId,
                operation.Slots,
                null,
                operatorContext.OperatorId,
                operatorContext.VerificationMethod,
                operatorContext.VerifiedAt);
            state = state with
            {
                RecoverySessionRequestId = requestId,
                ExceptionRecoverySessionId = opened.ExceptionRecoverySessionId,
                RecoveryActionId = actionId,
                RecoveryActionRequestId = actionMessageId,
                RecoveryReason = actionReason,
                RecoveryOperatorId = operatorContext.OperatorId,
                RecoveryOperatorVerifiedAt = operatorContext.VerifiedAt
            };
            await WriteRecoveryVectorPreparedAsync(state, vector, cancellationToken)
                .ConfigureAwait(false);
        }

        if (snapshot is not null
            && string.Equals(snapshot.SelectedAction, action, StringComparison.Ordinal))
        {
            if (action == CompensateLoadAction)
            {
                await SendLoadCompensationRequestAsync(vector, cancellationToken)
                    .ConfigureAwait(false);
            }

            PublishOperatorEvent(
                $"recovery-action-already-accepted:{actionId}",
                "RECOVERY_ACTION_SUBMITTED",
                $"恢复动作 {action} 已被服务端接受，等待车载端收到对应命令。 ");
            return WireToGateRecoveryRequestOutcome.Succeeded;
        }

        RecoveryActionAcceptedPayload accepted;
        try
        {
            accepted = await _session.SubmitRecoveryActionAsync(
                    actionMessageId,
                    new RecoveryActionSubmittedPayload(
                        actionId,
                        opened.ExceptionRecoverySessionId,
                        action,
                        opened.EventId,
                        operation.DemandId,
                        operation.Slots,
                        operatorContext,
                        actionReason),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (
            !string.Equals(exception.Message, "WIRE_TO_GATE_NOT_READY", StringComparison.Ordinal)
                && !exception.Message.Contains("messageId", StringComparison.OrdinalIgnoreCase))
        {
            await ClearRejectedRecoveryActionVectorAsync(actionId, cancellationToken)
                .ConfigureAwait(false);
            PublishOperatorEvent(
                $"recovery-action-rejected:{actionId}",
                "RECOVERY_BLOCKED",
                $"服务端拒绝恢复动作 {action}：{exception.Message}。未执行仓门IO。 ");
            throw;
        }
        if (!string.Equals(accepted.RecoveryActionId, actionId, StringComparison.Ordinal)
            || !string.Equals(
                accepted.ExceptionRecoverySessionId,
                opened.ExceptionRecoverySessionId,
                StringComparison.Ordinal)
            || !AttemptMatches(accepted.SlotOperationAttemptId, operation)
            || !string.Equals(accepted.AcceptedAction, action, StringComparison.Ordinal))
        {
            throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
        }

        if (action == CompensateLoadAction)
        {
            await SendLoadCompensationRequestAsync(vector, cancellationToken)
                .ConfigureAwait(false);
        }

        PublishOperatorEvent(
            $"recovery-action-submitted:{actionId}",
            "RECOVERY_ACTION_SUBMITTED",
            $"恢复动作 {action} 已通过服务端授权，等待车载端收到对应命令。 ");
        return WireToGateRecoveryRequestOutcome.Succeeded;
    }

    private async Task ClearRejectedRecoveryActionVectorAsync(
        string actionId,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state.RecoveryVector is not { } vector
            || vector.PrimaryId != actionId)
        {
            return;
        }

        await WriteRecoveryStateCachedAsync(
                state with
                {
                    RecoveryVector = null,
                    ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.Prepared,
                    ActiveUnlockSlots = [],
                    CompletedSlots = [],
                    SlotResults = [],
                    RecoveryActionId = null,
                    RecoveryActionRequestId = null,
                    RecoveryResultObservedAt = null
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sent again whenever the operator presses while the server already holds the accepted action,
    /// so each send carries a messageId of its own (see <c>RequestRecoveryActionVectorCoreAsync</c>).
    /// The server authorizes by recoveryActionId and binds its compensation command only once;
    /// another request simply re-sends the persisted command.
    /// </summary>
    private async Task SendLoadCompensationRequestAsync(
        WireToGateRecoveryVectorContext vector,
        CancellationToken cancellationToken)
    {
        await _session.RequestLoadCompensationAsync(
                Guid.NewGuid().ToString("D"),
                new LoadCompensationRequestedPayload(
                    vector.PrimaryId,
                    vector.ExceptionRecoverySessionId
                        ?? throw new InvalidDataException("RECOVERY_SESSION_SCOPE_MISMATCH"),
                    vector.DemandId,
                    vector.SlotOperationAttemptId
                        ?? throw new InvalidDataException("RECOVERY_COMMAND_INVALID"),
                    RequirePersistedOperator(vector)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleLoadCompensationCommandAsync(
        WireToGateLoadCompensationCommand command,
        CancellationToken cancellationToken)
    {
        await HandleRecoveryVectorCommandAsync(
                command.MessageId,
                WireToGateRecoveryVectorTypes.LoadCompensation,
                command.RecoveryActionId,
                command.ExceptionRecoverySessionId,
                command.DemandId,
                command.SlotOperationAttemptId,
                null,
                command.Slots,
                state => WireToGateRecoveryCommandHash.ForLoadCompensation(
                    command.RecoveryActionId,
                    command.DemandId,
                    command.SlotOperationAttemptId,
                    command.Slots),
                correction: false,
                resultKey: $"recovery-vector-result:{WireToGateRecoveryVectorTypes.LoadCompensation}:{command.RecoveryActionId}",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleLoadCorrectionCommandAsync(
        WireToGateLoadCorrectionCommand command,
        CancellationToken cancellationToken)
    {
        await HandleRecoveryVectorCommandAsync(
                command.MessageId,
                WireToGateRecoveryVectorTypes.LoadCorrection,
                command.CorrectionId,
                null,
                command.DemandId,
                command.SlotOperationAttemptId,
                null,
                command.Slots,
                state => WireToGateRecoveryCommandHash.ForLoadCorrection(
                    command.CorrectionId,
                    command.DemandId,
                    command.SlotOperationAttemptId,
                    command.Slots),
                correction: true,
                resultKey: $"recovery-vector-result:{WireToGateRecoveryVectorTypes.LoadCorrection}:{command.CorrectionId}",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleFaultCargoRecoveryCommandAsync(
        WireToGateFaultCargoRecoveryCommand command,
        CancellationToken cancellationToken)
    {
        await HandleRecoveryVectorCommandAsync(
                command.MessageId,
                WireToGateRecoveryVectorTypes.FaultCargoHandoff,
                command.RecoveryActionId,
                command.ExceptionRecoverySessionId,
                command.DemandId,
                null,
                command.HandoffId,
                command.Slots,
                state => WireToGateRecoveryCommandHash.ForRecoveryAction(
                    command.RecoveryActionId,
                    command.DemandId,
                    state.OperationContext?.SlotOperationAttemptId ?? string.Empty,
                    command.Slots,
                    state.ForcedRecoveryGeneration),
                correction: false,
                resultKey: $"recovery-vector-result:{WireToGateRecoveryVectorTypes.FaultCargoHandoff}:{command.RecoveryActionId}",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleRecoveryVectorRejectedAsync(
        WireToGateRecoveryCommand command,
        CancellationToken cancellationToken)
    {
        string? primaryId = null;
        string reason = "RECOVERY_REQUEST_REJECTED";
        try
        {
            using JsonDocument document = JsonDocument.Parse(command.PayloadJson);
            JsonElement payload = document.RootElement;
            string idProperty = command.MessageType == "LoadCorrectionRejected"
                ? "correctionId"
                : "recoveryActionId";
            if (payload.TryGetProperty(idProperty, out JsonElement id)
                && id.ValueKind == JsonValueKind.String)
            {
                primaryId = id.GetString();
            }

            if (payload.TryGetProperty("problem", out JsonElement problem)
                && problem.TryGetProperty("reasonCode", out JsonElement reasonCode)
                && reasonCode.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(reasonCode.GetString()))
            {
                reason = reasonCode.GetString()!;
            }
        }
        catch (JsonException)
        {
            // The transport parser already validated the envelope. If a future
            // rejection shape cannot be projected, retain the fail-closed path.
        }

        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state.RecoveryVector is { } vector
            && (primaryId is null || vector.PrimaryId == primaryId))
        {
            bool compensation = command.MessageType == "LoadCompensationRejected";
            await WriteRecoveryStateCachedAsync(
                    state with
                    {
                        RecoveryVector = null,
                        ProvenRecoveryCheckpoint = compensation
                            ? WireToGateRecoveryCheckpoint.Prepared
                            : WireToGateRecoveryCheckpoint.ResultRecorded,
                        ActiveUnlockSlots = [],
                        CompletedSlots = [],
                        SlotResults = [],
                        UnsettledSlotOperationAttemptId = compensation
                            ? vector.SlotOperationAttemptId
                            : null,
                        OperationContext = compensation ? state.OperationContext : null,
                        ExceptionRecoverySessionId = compensation
                            ? state.ExceptionRecoverySessionId
                            : null,
                        RecoveryActionId = null,
                        RecoveryActionRequestId = null,
                        RecoveryReason = compensation ? state.RecoveryReason : null,
                        RecoveryOperatorId = compensation ? state.RecoveryOperatorId : null,
                        RecoveryOperatorVerifiedAt = compensation
                            ? state.RecoveryOperatorVerifiedAt
                            : null,
                        RecoveryResultObservedAt = null
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        PublishOperatorEvent(
            $"recovery-vector-rejected:{command.MessageType}:{primaryId ?? command.MessageId}",
            "RECOVERY_BLOCKED",
            $"服务端拒绝恢复向量请求：{reason}。未执行仓门IO。 ");
    }

    private async Task HandleRecoveryVectorCommandAsync(
        string commandMessageId,
        string vectorType,
        string primaryId,
        string? exceptionRecoverySessionId,
        string demandId,
        string? slotOperationAttemptId,
        string? handoffId,
        IReadOnlyList<int> slots,
        Func<WireToGateRecoveryState, string> expectedHash,
        bool correction,
        string resultKey,
        CancellationToken cancellationToken)
    {
        await _recoveryRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string operationKey = $"recovery-vector:{vectorType}:{primaryId}";
        try
        {
            WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken)
                .ConfigureAwait(false);
            WireToGateDurableMessage? existingResult = await _session.Journal
                .ReadOutgoingByDeduplicationKeyAsync(resultKey, cancellationToken)
                .ConfigureAwait(false);
            if (existingResult is not null)
            {
                PublishOperatorEvent(
                    $"recovery-vector-result-replay:{vectorType}:{primaryId}",
                    "OPERATION_REPLAY",
                    $"恢复向量 {vectorType} 的结果已存在，忽略重复命令，未再次执行仓门IO。 ");
                return;
            }

            WireToGateRecoveryVectorContext context = await BindRecoveryVectorCommandAsync(
                    state,
                    vectorType,
                    primaryId,
                    exceptionRecoverySessionId,
                    demandId,
                    slotOperationAttemptId,
                    handoffId,
                    slots,
                    expectedHash,
                    correction,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!TryClaimOperation(operationKey))
            {
                return;
            }

            try
            {
                EnsureVehicleStoppedAndFresh();
                _ = await ExecuteRecoveryVectorAndReportAsync(
                        context,
                        correction,
                        cancellationToken,
                        result => SendRecoveryVectorResultAsync(
                            context,
                            resultKey,
                            result,
                            cancellationToken))
                    .ConfigureAwait(false);
            }
            finally
            {
                ReleaseOperation(operationKey);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
                $"服务端恢复命令未执行：type={vectorType}，message={commandMessageId}，reason={exception.Message}。",
                exception);
            PublishOperatorEvent(
                $"recovery-vector-command-failed:{vectorType}:{primaryId}:{exception.Message}",
                "RECOVERY_BLOCKED",
                $"恢复命令被阻断：{exception.Message}。未重复执行仓门IO。 ");
        }
        finally
        {
            _recoveryRequestGate.Release();
        }
    }

    private async Task<WireToGateRecoveryVectorContext> BindRecoveryVectorCommandAsync(
        WireToGateRecoveryState state,
        string vectorType,
        string primaryId,
        string? exceptionRecoverySessionId,
        string demandId,
        string? slotOperationAttemptId,
        string? handoffId,
        IReadOnlyList<int> slots,
        Func<WireToGateRecoveryState, string> expectedHash,
        bool correction,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryVectorContext context = state.RecoveryVector
            ?? throw new InvalidDataException("RECOVERY_VECTOR_CONTEXT_MISSING");
        if (context.SlotOperationAttemptId is null)
        {
            throw new InvalidDataException("RECOVERY_COMMAND_INVALID");
        }

        string boundSlotOperationAttemptId = slotOperationAttemptId ?? context.SlotOperationAttemptId;
        if (slotOperationAttemptId is null
            && vectorType != WireToGateRecoveryVectorTypes.FaultCargoHandoff)
        {
            throw new InvalidDataException("RECOVERY_SCOPE_MISMATCH");
        }

        if (context.VectorType != vectorType
            || context.PrimaryId != primaryId
            || context.ExceptionRecoverySessionId != exceptionRecoverySessionId
            || context.DemandId != demandId
            || context.SlotOperationAttemptId != boundSlotOperationAttemptId
            || context.HandoffId != handoffId
            || !context.Slots.SequenceEqual(slots)
            || state.UnsettledSlotOperationAttemptId != boundSlotOperationAttemptId)
        {
            throw new InvalidDataException("RECOVERY_SCOPE_MISMATCH");
        }

        if (correction)
        {
            WireToGateRecoveryOperationContext lastLoad =
                state.LastCompletedLoadOperationContext
                ?? throw new InvalidDataException("LOAD_CORRECTION_OPERATION_NOT_AVAILABLE");
            if (lastLoad.DemandId != demandId
                || lastLoad.SlotOperationAttemptId != boundSlotOperationAttemptId
                || !lastLoad.Slots.SequenceEqual(slots))
            {
                throw new InvalidDataException("RECOVERY_SCOPE_MISMATCH");
            }
        }
        else
        {
            WireToGateRecoveryOperationContext operation =
                state.OperationContext
                ?? state.LastCompletedLoadOperationContext
                ?? throw new InvalidDataException("RECOVERY_OPERATION_CONTEXT_MISSING");
            if (operation.OperationType != OperationType.Load
                || operation.DemandId != demandId
                || operation.SlotOperationAttemptId != boundSlotOperationAttemptId
                || !operation.Slots.SequenceEqual(slots))
            {
                throw new InvalidDataException("RECOVERY_SCOPE_MISMATCH");
            }
        }

        if (context.CommandContentSha256 is not null
            && !string.Equals(
                context.CommandContentSha256,
                expectedHash(state),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("RECOVERY_COMMAND_HASH_MISMATCH");
        }

        string commandHash = expectedHash(state);
        if (context.CommandContentSha256 is null)
        {
            context = context with { CommandContentSha256 = commandHash };
            await WriteRecoveryStateCachedAsync(
                    state with { RecoveryVector = context },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return context;
    }

    private async Task<WireToGateRecoveryRequestOutcome> ExecuteRecoveryVectorAndReportAsync(
        WireToGateRecoveryVectorContext context,
        bool correction,
        CancellationToken cancellationToken,
        Func<WireToGateRecoveryVectorExecutionResult, Task>? sendResult = null)
    {
        EnsureVehicleStoppedAndFresh();
        PublishRecoveryVectorOperation(
            context,
            WireToGateHmiOperationStage.Preparing,
            $"准备执行恢复向量：{FormatSlots(context.Slots)}。",
            "start");
        async Task SendProgress(
            string phase,
            IReadOnlyList<int> active,
            IReadOnlyList<int> completed,
            CancellationToken progressToken)
        {
            PublishRecoveryVectorOperation(
                context,
                MapOperationStage(phase),
                RecoveryVectorGuidance(context, phase, active, completed),
                $"{phase}:{string.Join(',', active)}:{string.Join(',', completed)}");
            if (context.SlotOperationAttemptId is not null)
            {
                await _session.SendRecoveryOperationProgressAsync(
                        context.SlotOperationAttemptId,
                        phase,
                        active,
                        completed,
                        cancellationToken: progressToken)
                    .ConfigureAwait(false);
            }
        }

        WireToGateRecoveryVectorExecutionResult result = correction
            ? await _vectorExecutor.ExecuteCorrectionAsync(context, SendProgress, cancellationToken)
                .ConfigureAwait(false)
            : await _vectorExecutor.ExecuteClearAsync(context, SendProgress, cancellationToken)
                .ConfigureAwait(false);
        bool success = result.OverallOutcome == "COMPLETED";
        PublishRecoveryVectorOperation(
            context,
            success
                ? WireToGateHmiOperationStage.Reporting
                : WireToGateHmiOperationStage.RecoveryRequired,
            success
                ? "物理状态已达到安全收尾条件，正在上报恢复结果。"
                : "恢复向量未完成，已保持故障安全并准备上报未知/失败结果。",
            "final");

        if (sendResult is not null)
        {
            try
            {
                await sendResult(result).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or InvalidOperationException)
            {
                _logger.Write(
                    LogSeverity.Warning,
                    nameof(WireToGateBusinessService),
                    $"恢复向量结果暂未收到DurableAck：type={context.VectorType}，id={context.PrimaryId}。",
                    exception);
                PublishOperatorEvent(
                    $"recovery-vector-result-pending:{context.VectorType}:{context.PrimaryId}",
                    "RESULT_ACK_PENDING",
                    "恢复结果已持久化，等待服务端确认；不会重复执行仓门IO。 ");
                return WireToGateRecoveryRequestOutcome.Refused("RESULT_ACK_PENDING");
            }
        }

        if (success)
        {
            await CompleteRecoveryVectorStateAsync(context, cancellationToken).ConfigureAwait(false);
            PublishRecoveryVectorOperation(
                context,
                WireToGateHmiOperationStage.Completed,
                "恢复向量结果已确认，目标仓位已回到安全状态。",
                "completed");
            PublishOperatorEvent(
                $"recovery-vector-completed:{context.VectorType}:{context.PrimaryId}",
                "RECOVERY_VECTOR_COMPLETED",
                $"恢复向量 {context.VectorType} 已完成并收到服务端确认。 ");
            return WireToGateRecoveryRequestOutcome.Succeeded;
        }

        PublishOperatorEvent(
            $"recovery-vector-recovery-required:{context.VectorType}:{context.PrimaryId}",
            "OPERATION_RECOVERY_REQUIRED",
            "恢复结果已上报，但物理状态仍未达到可确认条件；请保持车辆停稳并等待下一步处理。 ");
        return WireToGateRecoveryRequestOutcome.Refused("OPERATION_RECOVERY_REQUIRED");
    }

    private Task<string> SendRecoveryVectorResultAsync(
        WireToGateRecoveryVectorContext context,
        string resultKey,
        WireToGateRecoveryVectorExecutionResult result,
        CancellationToken cancellationToken)
    {
        string messageId = StableUuid(resultKey);
        WireToGateSlotResultPayload[] slotResults = result.SlotResults
            .OrderBy(item => item.SlotNo)
            .Select(item => new WireToGateSlotResultPayload(
                item.SlotNo,
                item.Outcome,
                item.FinalPhysicalState,
                item.LockState,
                item.UnlockOutputState,
                item.ReasonCodes))
            .ToArray();
        return context.VectorType switch
        {
            WireToGateRecoveryVectorTypes.LoadCancellation => _session
                .SendLoadCancellationResultAsync(
                    resultKey,
                    messageId,
                    new LoadCancellationResultPayload(
                        context.PrimaryId,
                        context.DemandId,
                        context.SlotOperationAttemptId,
                        result.OverallOutcome == "COMPLETED" ? "ALL_EMPTY" : result.OverallOutcome,
                        slotResults,
                        result.ObservedAt),
                    cancellationToken),
            WireToGateRecoveryVectorTypes.LoadCompensation => _session
                .SendLoadCompensationResultAsync(
                    resultKey,
                    messageId,
                    new LoadCompensationResultPayload(
                        context.PrimaryId,
                        context.DemandId,
                        context.SlotOperationAttemptId
                            ?? throw new InvalidDataException("RECOVERY_COMMAND_INVALID"),
                        result.OverallOutcome == "COMPLETED" ? "ALL_EMPTY" : result.OverallOutcome,
                        slotResults,
                        result.ObservedAt),
                    cancellationToken),
            WireToGateRecoveryVectorTypes.LoadCorrection => _session
                .SendLoadCorrectionResultAsync(
                    resultKey,
                    messageId,
                    new LoadCorrectionResultPayload(
                        context.PrimaryId,
                        context.DemandId,
                        context.SlotOperationAttemptId
                            ?? throw new InvalidDataException("RECOVERY_COMMAND_INVALID"),
                        result.OverallOutcome,
                        slotResults,
                        result.ObservedAt),
                    cancellationToken),
            WireToGateRecoveryVectorTypes.FaultCargoHandoff => _session
                .SendFaultCargoRecoveryResultAsync(
                    resultKey,
                    messageId,
                    new FaultCargoRecoveryResultPayload(
                        context.ExceptionRecoverySessionId
                            ?? throw new InvalidDataException("RECOVERY_SESSION_SCOPE_MISMATCH"),
                        context.PrimaryId,
                        context.DemandId,
                        context.HandoffId
                            ?? throw new InvalidDataException("RECOVERY_COMMAND_INVALID"),
                        result.OverallOutcome == "COMPLETED" ? "HANDED_OFF" : result.OverallOutcome,
                        slotResults,
                        RequirePersistedOperator(context),
                        result.ObservedAt),
                    cancellationToken),
            _ => throw new InvalidDataException("RECOVERY_VECTOR_TYPE_INVALID")
        };
    }

    private async Task CompleteRecoveryVectorStateAsync(
        WireToGateRecoveryVectorContext context,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state.RecoveryVector is not { } current
            || current.VectorType != context.VectorType
            || current.PrimaryId != context.PrimaryId)
        {
            throw new InvalidDataException("RECOVERY_STATE_MISMATCH");
        }

        await WriteRecoveryStateCachedAsync(
                state with
                {
                    UnsettledSlotOperationAttemptId = null,
                    ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ResultRecorded,
                    ActiveUnlockSlots = [],
                    CompletedSlots = [],
                    SlotResults = [],
                    OperationContext = null,
                    ExceptionRecoverySessionId = null,
                    RecoveryActionId = null,
                    RecoverySessionRequestId = null,
                    RecoveryActionRequestId = null,
                    RecoveryReason = null,
                    RecoveryOperatorId = null,
                    RecoveryOperatorVerifiedAt = null,
                    RecoveryResultObservedAt = null,
                    RecoveryVector = null
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteRecoveryVectorPreparedAsync(
        WireToGateRecoveryState state,
        WireToGateRecoveryVectorContext context,
        CancellationToken cancellationToken)
    {
        await WriteRecoveryStateCachedAsync(
                state with
                {
                    UnsettledSlotOperationAttemptId = context.SlotOperationAttemptId,
                    ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.Prepared,
                    ActiveUnlockSlots = [],
                    CompletedSlots = [],
                    SlotResults = [],
                    RecoveryVector = context,
                    ExceptionRecoverySessionId = context.ExceptionRecoverySessionId
                        ?? state.ExceptionRecoverySessionId,
                    RecoveryActionId = context.VectorType is
                        WireToGateRecoveryVectorTypes.LoadCompensation
                        or WireToGateRecoveryVectorTypes.FaultCargoHandoff
                        ? context.PrimaryId
                        : state.RecoveryActionId,
                    RecoveryOperatorId = context.OperatorId ?? state.RecoveryOperatorId,
                    RecoveryOperatorVerifiedAt = context.OperatorVerifiedAt
                        ?? state.RecoveryOperatorVerifiedAt,
                    RecoveryResultObservedAt = null
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WireToGateRecoveryState> ReadRecoveryStateCachedAsync(
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await _session.Journal
            .ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(ref _lastRecoveryState, state);
        return state;
    }

    private async Task WriteRecoveryStateCachedAsync(
        WireToGateRecoveryState state,
        CancellationToken cancellationToken)
    {
        await _session.Journal.WriteRecoveryStateAsync(state, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _lastRecoveryState, state);
    }

    /// <summary>
    /// The load this recovery is about.  The armed operation is preferred, but an operation whose
    /// result the vehicle already recorded is still a valid subject: the server may judge that same
    /// attempt <c>RecoveryRequired</c> while the vehicle believes it finished, and that is exactly
    /// the state a compensation exists for.  <c>MarkResultRecordedAsync</c> keeps the settled
    /// identity in <see cref="WireToGateRecoveryState.LastCompletedLoadOperationContext"/> for this
    /// case, so nothing here is guessed -- the identity is read, never reconstructed.
    /// </summary>
    private static WireToGateRecoveryOperationContext? FindRecoveryLoadOperation(
        WireToGateRecoveryState state) =>
        state.OperationContext is { OperationType: OperationType.Load } armed
            && state.UnsettledSlotOperationAttemptId == armed.SlotOperationAttemptId
            ? armed
            : state.LastCompletedLoadOperationContext;

    /// <summary>
    /// The server names the attempt every recovery message is scoped to (protocol-v0.3.0).  It is
    /// the authority -- the vehicle's own copy is its belief about an operation the server has
    /// judged differently -- so a disagreement is never resolved silently in the vehicle's favour;
    /// the request is refused and the operator sees the scope mismatch.  A null means the server
    /// has no station operation for this session, which leaves the local identity unchallenged.
    /// </summary>
    private static bool AttemptMatches(
        string? serverSlotOperationAttemptId,
        WireToGateRecoveryOperationContext operation) =>
        serverSlotOperationAttemptId is null
        || string.Equals(
            serverSlotOperationAttemptId,
            operation.SlotOperationAttemptId,
            StringComparison.Ordinal);

    private static WireToGateRecoveryOperationContext RequireUnsettledLoadOperation(
        WireToGateRecoveryState state)
    {
        WireToGateRecoveryOperationContext operation = state.OperationContext
            ?? throw new InvalidOperationException("RECOVERY_OPERATION_CONTEXT_MISSING");
        if (operation.OperationType != OperationType.Load
            || state.UnsettledSlotOperationAttemptId != operation.SlotOperationAttemptId)
        {
            throw new InvalidOperationException("RECOVERY_OPERATION_CONTEXT_MISSING");
        }

        return operation;
    }

    private WireToGateOperatorContextPayload ReadOperatorContext()
    {
        string operatorId = Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable)
            ?? throw new InvalidOperationException("WIRE_TO_GATE_OPERATOR_NOT_READY");
        if (string.IsNullOrWhiteSpace(operatorId))
        {
            throw new InvalidOperationException("WIRE_TO_GATE_OPERATOR_NOT_READY");
        }

        return new(operatorId, GetProtocolVerificationMethod(), _clock.Now.ToUniversalTime());
    }

    private string ReadRecoveryProof()
    {
        string proof = Environment.GetEnvironmentVariable(
                _recoveryOptions.AuthenticationProofEnvironmentVariable)
            ?? throw new InvalidOperationException("RECOVERY_AUTHENTICATION_REQUIRED");
        if (string.IsNullOrWhiteSpace(proof))
        {
            throw new InvalidOperationException("RECOVERY_AUTHENTICATION_REQUIRED");
        }

        return proof;
    }

    private string GetProtocolVerificationMethod() =>
        _recoveryOptions.VerificationMethod is "BADGE" or "SESSION"
            ? _recoveryOptions.VerificationMethod
            : "SESSION";

    private static string RequireReason(string reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? throw new InvalidOperationException("RECOVERY_REASON_REQUIRED")
            : reason.Trim();

    private static WireToGateOperatorContextPayload RequirePersistedOperator(
        WireToGateRecoveryVectorContext context) =>
        context.OperatorId is not null
            && context.OperatorVerificationMethod is not null
            && context.OperatorVerifiedAt is DateTimeOffset verifiedAt
            ? new(context.OperatorId, context.OperatorVerificationMethod, verifiedAt)
            : throw new InvalidDataException("RECOVERY_OPERATOR_CONTEXT_MISSING");

    private static void ValidateRecoverySessionSnapshot(
        WireToGateExceptionRecoverySessionSnapshot snapshot,
        WireToGateRecoveryOperationContext operation)
    {
        if (snapshot.State == "CLOSED"
            || !string.Equals(snapshot.DemandId, operation.DemandId, StringComparison.Ordinal)
            || !AttemptMatches(snapshot.SlotOperationAttemptId, operation)
            || !snapshot.Slots.SequenceEqual(operation.Slots))
        {
            throw new InvalidDataException("RECOVERY_SCOPE_MISMATCH");
        }
    }

    private static void ValidateOpenedRecoverySession(
        ExceptionRecoverySessionOpenedPayload opened,
        string requestId,
        string eventId,
        WireToGateRecoveryOperationContext operation)
    {
        if (!string.Equals(opened.RequestId, requestId, StringComparison.Ordinal)
            || !string.Equals(opened.EventId, eventId, StringComparison.Ordinal)
            || !string.Equals(opened.DemandId, operation.DemandId, StringComparison.Ordinal)
            || !AttemptMatches(opened.SlotOperationAttemptId, operation)
            || !opened.Slots.SequenceEqual(operation.Slots))
        {
            throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
        }
    }

    private void EnsureVehicleStoppedAndFresh()
    {
        DateTimeOffset now = _clock.Now;
        VehicleSafetySignal signal = ReadVehicleSafety();
        if (!signal.IsStoppedAndFresh(
                now,
                _vehicleSafetyMaxAge,
                _vehicleSafetyClockSkewTolerance))
        {
            throw new InvalidOperationException("VEHICLE_NOT_READY");
        }
    }

    private bool TryClaimOperation(string key)
    {
        lock (_operationAttemptGate)
        {
            return _operationAttempts.Add(key);
        }
    }

    private void ReleaseOperation(string key)
    {
        lock (_operationAttemptGate)
        {
            _operationAttempts.Remove(key);
        }
    }

    private void PublishRecoveryVectorOperation(
        WireToGateRecoveryVectorContext context,
        WireToGateHmiOperationStage stage,
        string guidance,
        string detailKey)
    {
        WireToGateHmiOperationSnapshot operation = new(
            context.SlotOperationAttemptId ?? context.PrimaryId,
            context.VectorType == WireToGateRecoveryVectorTypes.LoadCorrection
                ? OperationType.Load
                : OperationType.Unload,
            context.Slots,
            stage,
            guidance,
            _clock.Now.ToUniversalTime());
        PublishOperatorEvent(
            $"recovery-vector-stage:{context.VectorType}:{context.PrimaryId}:{stage}:{detailKey}",
            "OPERATION_PROGRESS",
            guidance,
            operation);
    }

    private void PublishRecoveryVectorRestored(
        WireToGateRecoveryVectorContext context)
    {
        PublishRecoveryVectorOperation(
            context,
            WireToGateHmiOperationStage.RecoveryRequired,
            $"恢复向量 {context.VectorType} 尚未完成：{FormatSlots(context.Slots)}，请保持车辆停稳。",
            "restored");
    }

    private static string RecoveryVectorGuidance(
        WireToGateRecoveryVectorContext context,
        string phase,
        IReadOnlyList<int> active,
        IReadOnlyList<int> completed) => phase switch
        {
            "PREPARING" => $"正在检查{FormatSlots(context.Slots)}的安全条件。",
            "UNLOCKING" => $"正在打开{FormatSlots(active)}。",
            "WAITING_OPERATOR" when context.VectorType == WireToGateRecoveryVectorTypes.LoadCorrection =>
                $"请先从{FormatSlots(active)}取出原货物，再按提示重新放入并关门。",
            "WAITING_OPERATOR" => $"请清空{FormatSlots(active)}并关门。",
            "VERIFYING" => $"正在核对仓门、货物和输出状态；已完成 {completed.Count}/{context.Slots.Count}。",
            "SAFE_FINISH" => "全部目标仓已达到安全收尾状态，正在上报恢复结果。",
            "PAUSED" => "恢复向量已暂停，物理状态未知，禁止重复操作仓门。",
            _ => $"正在处理{FormatSlots(context.Slots)}。"
        };

}
