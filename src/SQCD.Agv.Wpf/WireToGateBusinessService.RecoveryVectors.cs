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
        CanUseRecoveryOperator(requireProof: false)
        && HasRecoveryVectorOrLoadOperation(WireToGateRecoveryVectorTypes.LoadCancellation);

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

    public Task<bool> RequestLoadCancellationAsync(
        string reason = "现场确认装货取消，申请将目标仓位清空。",
        CancellationToken cancellationToken = default) =>
        RunRecoveryRequestAsync(
            WireToGateRecoveryVectorTypes.LoadCancellation,
            () => RequestLoadCancellationCoreAsync(reason, cancellationToken),
            cancellationToken);

    public Task<bool> RequestLoadCompensationAsync(
        string reason = "现场确认装货无法继续，申请补偿清空目标仓位。",
        CancellationToken cancellationToken = default) =>
        RunRecoveryRequestAsync(
            WireToGateRecoveryVectorTypes.LoadCompensation,
            () => RequestRecoveryActionVectorCoreAsync(
                CompensateLoadAction,
                WireToGateRecoveryVectorTypes.LoadCompensation,
                reason,
                cancellationToken),
            cancellationToken);

    public Task<bool> RequestLoadCorrectionAsync(
        string reason = "现场确认需要修正已完成的装货结果。",
        CancellationToken cancellationToken = default) =>
        RunRecoveryRequestAsync(
            WireToGateRecoveryVectorTypes.LoadCorrection,
            () => RequestLoadCorrectionCoreAsync(reason, cancellationToken),
            cancellationToken);

    public Task<bool> RequestFaultCargoHandoffAsync(
        string reason = "现场确认故障仓货物需要交接处理。",
        CancellationToken cancellationToken = default) =>
        RunRecoveryRequestAsync(
            WireToGateRecoveryVectorTypes.FaultCargoHandoff,
            () => RequestRecoveryActionVectorCoreAsync(
                FaultCargoHandoffAction,
                WireToGateRecoveryVectorTypes.FaultCargoHandoff,
                reason,
                cancellationToken),
            cancellationToken);

    private async Task<bool> RunRecoveryRequestAsync(
        string vectorType,
        Func<Task<bool>> action,
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
            return false;
        }
        finally
        {
            _recoveryRequestGate.Release();
        }
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

        if (state.OperationContext is not { OperationType: OperationType.Load } context
            || state.UnsettledSlotOperationAttemptId != context.SlotOperationAttemptId)
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

    private async Task<bool> RequestLoadCancellationCoreAsync(
        string reason,
        CancellationToken cancellationToken)
    {
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
            return false;
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

    private async Task<bool> RequestLoadCorrectionCoreAsync(
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
        return true;
    }

    private async Task<bool> RequestRecoveryActionVectorCoreAsync(
        string action,
        string vectorType,
        string reason,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken)
            .ConfigureAwait(false);
        WireToGateRecoveryOperationContext? operation = state.OperationContext;
        if (operation is not { OperationType: OperationType.Load }
            || state.UnsettledSlotOperationAttemptId != operation.SlotOperationAttemptId)
        {
            throw new InvalidOperationException("RECOVERY_OPERATION_CONTEXT_MISSING");
        }

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
        string requestId;
        string eventId;
        ExceptionRecoverySessionOpenedPayload opened;
        string actionId;
        string actionMessageId;

        if (vector is not null)
        {
            operatorContext = RequirePersistedOperator(vector);
            opened = new(
                state.RecoverySessionRequestId
                    ?? throw new InvalidDataException("RECOVERY_SESSION_REQUEST_MISSING"),
                vector.ExceptionRecoverySessionId
                    ?? throw new InvalidDataException("RECOVERY_SESSION_SCOPE_MISMATCH"),
                snapshot!.SentAt,
                snapshot.EventId,
                operation.DemandId,
                operation.Slots,
                snapshot.RecoverySessionRevision);
            requestId = opened.RequestId;
            eventId = opened.EventId;
            actionId = vector.PrimaryId;
            actionMessageId = state.RecoveryActionRequestId
                ?? StableUuid($"{actionId}|recovery-action");
        }
        else
        {
            operatorContext = ReadOperatorContext();
            proof = ReadRecoveryProof();
            bool activeSession = snapshot is not null && snapshot.State != "CLOSED";
            if (!activeSession && _session.Current.Readiness != WireToGateSessionReadiness.RecoveryRequired)
            {
                throw new InvalidOperationException("RECOVERY_SESSION_NOT_READY");
            }

            requestId = state.RecoverySessionRequestId
                ?? StableUuid($"{operation.SlotOperationAttemptId}|exception-recovery-session");
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
                            RecoveryReason = RequireReason(reason),
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
                            RequireReason(reason),
                            proof),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            ValidateOpenedRecoverySession(opened, requestId, eventId, operation);
            actionId = state.RecoveryActionId
                ?? StableUuid($"{opened.ExceptionRecoverySessionId}|{action}");
            actionMessageId = state.RecoveryActionRequestId
                ?? StableUuid($"{actionId}|recovery-action");
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
                ExceptionRecoverySessionId = opened.ExceptionRecoverySessionId,
                RecoveryActionId = actionId,
                RecoveryActionRequestId = actionMessageId,
                RecoveryReason = RequireReason(reason),
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
                await SendLoadCompensationRequestAsync(
                        vector,
                        StableUuid($"{actionId}|load-compensation-request"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            PublishOperatorEvent(
                $"recovery-action-already-accepted:{actionId}",
                "RECOVERY_ACTION_SUBMITTED",
                $"恢复动作 {action} 已被服务端接受，等待车载端收到对应命令。 ");
            return true;
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
                        RequireReason(reason)),
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
            || !string.Equals(accepted.AcceptedAction, action, StringComparison.Ordinal))
        {
            throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
        }

        if (action == CompensateLoadAction)
        {
            await SendLoadCompensationRequestAsync(
                    vector,
                    StableUuid($"{actionId}|load-compensation-request"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        PublishOperatorEvent(
            $"recovery-action-submitted:{actionId}",
            "RECOVERY_ACTION_SUBMITTED",
            $"恢复动作 {action} 已通过服务端授权，等待车载端收到对应命令。 ");
        return true;
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

    private async Task SendLoadCompensationRequestAsync(
        WireToGateRecoveryVectorContext vector,
        string requestId,
        CancellationToken cancellationToken)
    {
        await _session.RequestLoadCompensationAsync(
                requestId,
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
                bool completed = await ExecuteRecoveryVectorAndReportAsync(
                        context,
                        correction,
                        cancellationToken,
                        result => SendRecoveryVectorResultAsync(
                            context,
                            resultKey,
                            result,
                            cancellationToken))
                    .ConfigureAwait(false);
                _ = completed;
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

    private async Task<bool> ExecuteRecoveryVectorAndReportAsync(
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
                return false;
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
            return true;
        }

        PublishOperatorEvent(
            $"recovery-vector-recovery-required:{context.VectorType}:{context.PrimaryId}",
            "OPERATION_RECOVERY_REQUIRED",
            "恢复结果已上报，但物理状态仍未达到可确认条件；请保持车辆停稳并等待下一步处理。 ");
        return false;
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
