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
    private const string ForcedMechanicalRecoveryAction = "FORCED_MECHANICAL_RECOVERY";

    private readonly WireToGateRecoveryVectorExecutor _vectorExecutor;
    private WireToGateRecoveryState _lastRecoveryState = WireToGateRecoveryState.Empty;

    /// <summary>
    /// Whether the load cancellation entry is offered: over a load in flight, or before any sublot was
    /// entered. Neither is behind <c>recoveryResumeEnabled</c>.
    /// </summary>
    /// <remarks>
    /// The load in flight was a recovery entry until 8005-agv-onboard-hmi#78. program#55 made the
    /// operator's cancel the only way to give a load up -- past the deadline an empty door is reopened
    /// with no limit -- so it has to be there as shipped, and it is the station operator's step, the
    /// same as the cancellation before any sublot (#76). Compensation, correction, resume and the other
    /// recovery vectors stay behind the switch.
    /// </remarks>
    public bool CanRequestLoadCancellation =>
        (CanUseStationOperator()
            && HasRecoveryVectorOrLoadOperation(WireToGateRecoveryVectorTypes.LoadCancellation))
        || CanRequestLoadCancellationBeforeSublot();

    /// <summary>
    /// Whether a cancellation before any sublot has gone out and is not settled: sent and not
    /// refused, or authorized and its result not yet acknowledged.
    /// </summary>
    /// <remarks>
    /// Read from the cached recovery state, which the press writes before the request leaves. A
    /// refusal forgets the pending entry and an acknowledged result clears both, so either answer
    /// reopens sublot entry -- the first only if the stop is still waiting for one.
    /// </remarks>
    public bool IsLoadCancellationBeforeSublotOpen
    {
        get
        {
            WireToGateRecoveryState state = Volatile.Read(ref _lastRecoveryState);
            return state.PendingLoadCancellation is { SlotOperationAttemptId: null }
                || state.RecoveryVector is { } vector
                    && WireToGateRecoveryVectorTypes.IsLoadCancellationBeforeSublot(vector);
        }
    }

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

    /// <remarks>
    /// Shut while an earlier forced recovery's slots are still physically unknown: the vehicle keeps
    /// one isolation, and a second forced recovery would replace it -- making those slots operable
    /// again with no hardware recovery record (REQ-0242). The server holds the whole vehicle until
    /// that record arrives anyway, so this closes nothing the server leaves open.
    /// </remarks>
    public bool CanRequestForcedMechanicalRecovery =>
        CanUseRecoveryOperator(requireProof: true)
        && Volatile.Read(ref _lastRecoveryState).ForcedIsolation is null
        && CanRequestRecoveryAction(
            ForcedMechanicalRecoveryAction,
            WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery);

    /// <summary>
    /// Whether an authorized forced mechanical recovery is waiting for the operator who asked for it
    /// to confirm the isolation and the manual extraction.
    /// </summary>
    public bool CanConfirmForcedMechanicalRecovery =>
        CanUseRecoveryOperator(requireProof: true)
        && AwaitingForcedConfirmation(Volatile.Read(ref _lastRecoveryState)) is { } vector
        && string.Equals(
            vector.OperatorId,
            Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable),
            StringComparison.Ordinal);

    /// <summary>
    /// The slots left physically unknown by an acknowledged forced mechanical recovery (REQ-0241),
    /// ascending; empty when there are none.
    /// </summary>
    public IReadOnlyList<int> PhysicallyUnknownSlots =>
        Volatile.Read(ref _lastRecoveryState).ForcedIsolation?.PhysicallyUnknownSlots ?? [];

    public Task<bool> RequestLoadCancellationAsync(
        string reason = "现场确认装货取消，申请将目标仓位清空。",
        CancellationToken cancellationToken = default) =>
        RunRecoveryRequestAsync(
            WireToGateRecoveryVectorTypes.LoadCancellation,
            () => RequestLoadCancellationCoreAsync(reason, cancellationToken),
            cancellationToken);

    /// <param name="reason">
    /// The administrator's reason for the exception recovery session -- who judged, the fault category, what
    /// was seen (CP-0005 section 5, onboard-hmi#109). Blank keeps this action's fixed text.
    /// </param>
    public Task<bool> RequestLoadCompensationAsync(
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        RunRecoveryRequestAsync(
            WireToGateRecoveryVectorTypes.LoadCompensation,
            () => RequestRecoveryActionVectorCoreAsync(
                CompensateLoadAction,
                WireToGateRecoveryVectorTypes.LoadCompensation,
                ReasonOrDefault(reason, "现场确认装货无法继续，申请补偿清空目标仓位。"),
                cancellationToken),
            cancellationToken);

    public Task<bool> RequestLoadCorrectionAsync(
        string reason = "现场确认需要修正已完成的装货结果。",
        CancellationToken cancellationToken = default) =>
        RunRecoveryRequestAsync(
            WireToGateRecoveryVectorTypes.LoadCorrection,
            () => RequestLoadCorrectionCoreAsync(reason, cancellationToken),
            cancellationToken);

    /// <param name="reason">As for <see cref="RequestLoadCompensationAsync"/>.</param>
    public Task<bool> RequestFaultCargoHandoffAsync(
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        RunRecoveryRequestAsync(
            WireToGateRecoveryVectorTypes.FaultCargoHandoff,
            () => RequestRecoveryActionVectorCoreAsync(
                FaultCargoHandoffAction,
                WireToGateRecoveryVectorTypes.FaultCargoHandoff,
                ReasonOrDefault(reason, "现场确认故障仓货物需要交接处理。"),
                cancellationToken),
            cancellationToken);

    /// <param name="reason">As for <see cref="RequestLoadCompensationAsync"/>.</param>
    public Task<bool> RequestForcedMechanicalRecoveryAsync(
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        RunRecoveryRequestAsync(
            WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery,
            () => RequestRecoveryActionVectorCoreAsync(
                ForcedMechanicalRecoveryAction,
                WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery,
                ReasonOrDefault(reason, "现场确认仓门无法电动解锁，申请强制机械恢复。"),
                cancellationToken),
            cancellationToken);

    /// <summary>
    /// The operator's confirmation that the vehicle was isolated -- power cut, brake held -- and a
    /// qualified person opened the slots by hand (REQ-0241). Only this reports
    /// <c>MECHANICALLY_ISOLATED</c>; the vehicle itself proves nothing here and sends no unlock.
    /// </summary>
    public Task<bool> ConfirmForcedMechanicalRecoveryAsync(
        CancellationToken cancellationToken = default) =>
        RunRecoveryRequestAsync(
            WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery,
            () => ConfirmForcedMechanicalRecoveryCoreAsync(cancellationToken),
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
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                $"恢复向量被阻断：{exception.Message}。请确认车辆停稳、仓门状态和服务端授权。 ");
            return false;
        }
        finally
        {
            _recoveryRequestGate.Release();
        }
    }

    private bool CanUseRecoveryOperator(bool requireProof) =>
        _recoveryOptions.ResumeAfterRepairEnabled
        && CanUseStationOperator()
        && (!requireProof
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                _recoveryOptions.AuthenticationProofEnvironmentVariable)));

    /// <summary>
    /// An operator with an id at a vehicle whose session can carry a request. No maintenance switch and
    /// no proof: this is what a station operator's step needs.
    /// </summary>
    private bool CanUseStationOperator()
    {
        WireToGateSessionSnapshot session = _session.Current;
        if (!session.Connected
            || session.Readiness is not (WireToGateSessionReadiness.Ready
                or WireToGateSessionReadiness.RecoveryRequired))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable));
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

    /// <summary>
    /// The cancellation before any sublot is entered (ADR-cross-0046, first case; onboard-hmi#76):
    /// the server's entry request is outstanding, nothing was commanded for its demand, and no other
    /// cancellation is open. Or the one this entry already started is waiting for its result to be
    /// acknowledged, and pressing again reports it again.
    /// </summary>
    /// <remarks>
    /// Not behind <c>recoveryResumeEnabled</c>, and needing no proof: an operator at the station who
    /// finds nothing to load cancels as an ordinary step before leaving, not as maintenance. What it
    /// can never do is open a door -- the server authorizes it with no slots, and the vehicle's
    /// whole answer is <c>ALL_EMPTY</c> with nothing per slot.
    /// </remarks>
    private bool CanRequestLoadCancellationBeforeSublot()
    {
        WireToGateSessionSnapshot session = _session.Current;
        if (!session.Connected
            || string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable)))
        {
            return false;
        }

        WireToGateRecoveryState state = Volatile.Read(ref _lastRecoveryState);
        if (state.RecoveryVector is { } vector)
        {
            return WireToGateRecoveryVectorTypes.IsLoadCancellationBeforeSublot(vector)
                && session.Readiness is WireToGateSessionReadiness.Ready
                    or WireToGateSessionReadiness.RecoveryRequired;
        }

        return session.Readiness == WireToGateSessionReadiness.Ready
            && state.PendingLoadCancellation?.SlotOperationAttemptId is null
            && FindLoadCancellationBeforeSublot(state) is not null;
    }

    private sealed record LoadCancellationBeforeSublotTarget(string CancellationId, string DemandId);

    /// <summary>
    /// The demand a cancellation before any sublot would cancel, or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Protocol 2.0.0 took <c>demandId</c> off the entry request, so the demand is read from the
    /// worklist the request belongs to -- same operation session, revision and station, the check
    /// <c>SubmitSublotAsync</c> makes -- and only when that worklist names exactly one demand. With
    /// more than one the vehicle would be choosing which demand to cancel, and choosing a demand is
    /// never this end's (<c>NEVER_DISCOVER_SELECT_OR_BIND_DEMAND</c>).
    /// </para>
    /// <para>
    /// "Nothing commanded" is read from what this vehicle holds: no slot operation unsettled or
    /// running, and neither the armed nor the last settled load belonging to this demand. The server
    /// makes the same judgement from its side and refuses a cancellation once a load command exists.
    /// </para>
    /// <para>
    /// The cancellationId is derived from the demand and the operation session, so every press at
    /// this stop -- across a lost answer and a restart -- asks about the same cancellation.
    /// </para>
    /// </remarks>
    private LoadCancellationBeforeSublotTarget? FindLoadCancellationBeforeSublot(
        WireToGateRecoveryState state)
    {
        if (Volatile.Read(ref _currentEntryRequest) is not { } request
            || _session.CurrentJourney.CurrentStopWorklist is not { } worklist
            || !string.Equals(
                worklist.OperationSessionId,
                request.OperationSessionId,
                StringComparison.Ordinal)
            || worklist.Revision != request.WorklistRevision
            || !string.Equals(worklist.StationId, request.StationId, StringComparison.Ordinal)
            || worklist.Items is not [{ } item])
        {
            return null;
        }

        bool running;
        lock (_operationAttemptGate)
        {
            running = _operationAttempts.Count > 0;
        }

        if (running
            || state.UnsettledSlotOperationAttemptId is not null
            || string.Equals(state.OperationContext?.DemandId, item.DemandId, StringComparison.Ordinal)
            || string.Equals(
                state.LastCompletedLoadOperationContext?.DemandId,
                item.DemandId,
                StringComparison.Ordinal))
        {
            return null;
        }

        return new(
            StableUuid($"{item.DemandId}|{request.OperationSessionId}|load-cancellation-before-sublot"),
            item.DemandId);
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

        if (FindRecoveryOperation(state, action) is not { } context)
        {
            return false;
        }

        WireToGateExceptionRecoverySessionSnapshot? snapshot =
            Volatile.Read(ref _recoverySessionSnapshot);
        if (snapshot is null || snapshot.State == "CLOSED")
        {
            return _session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired;
        }

        // The subject is #80's and only #80's; the server's attempt id is an extra condition on it,
        // never a way to pick a different one. A disagreement greys the entry out here and is
        // refused loudly on the request path.
        return snapshot.SelectedAction is null
            && !IsInconsistentRecoverySession(snapshot.ExceptionRecoverySessionId)
            && snapshot.AllowedActions.Contains(action, StringComparer.Ordinal)
            && string.Equals(snapshot.DemandId, context.DemandId, StringComparison.Ordinal)
            && snapshot.Slots.SequenceEqual(context.Slots)
            && IsSameSlotOperationAttempt(snapshot.SlotOperationAttemptId, context);
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

            if (WireToGateRecoveryVectorTypes.IsLoadCancellationBeforeSublot(existingVector))
            {
                return await ReportLoadCancellationBeforeSublotAsync(existingVector, cancellationToken)
                    .ConfigureAwait(false);
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

        if (state.UnsettledSlotOperationAttemptId is null)
        {
            return await RequestLoadCancellationBeforeSublotAsync(state, reason, cancellationToken)
                .ConfigureAwait(false);
        }

        WireToGateRecoveryOperationContext operation = RequireUnsettledLoadOperation(state);
        string cancellationId = InFlightLoadCancellationId(operation.DemandId, operation.SlotOperationAttemptId);

        if (await AskForLoadCancellationAsync(
                state,
                cancellationId,
                operation.DemandId,
                operation.SlotOperationAttemptId,
                reason,
                cancellationToken)
            .ConfigureAwait(false) is not var (authorization, operatorContext))
        {
            return false;
        }

        if (authorization.Slots.Count == 0
            || !authorization.Slots.SequenceEqual(operation.Slots))
        {
            throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
        }

        // The abort channel (onboard-hmi#78): the load's closed loop stops before the cancellation
        // takes its slots over, so no two executors drive one lock. Only after the authorization: a
        // refused cancellation leaves the load running, and the operator can still finish it.
        await _executor.AbortOperationAsync(operation.SlotOperationAttemptId, cancellationToken)
            .ConfigureAwait(false);
        ForgetLoadAwaitingOperator(operation.SlotOperationAttemptId);
        WireToGateRecoveryState aborted = await ReadRecoveryStateCachedAsync(cancellationToken)
            .ConfigureAwait(false);
        // Whatever the load left in its active unlock set may be standing open; the cancellation
        // waits for the operator there instead of pulsing it (ADR-cross-0046).
        IReadOnlyList<int> handedOverOpenSlots = string.Equals(
                aborted.UnsettledSlotOperationAttemptId,
                operation.SlotOperationAttemptId,
                StringComparison.Ordinal)
            ? aborted.ActiveUnlockSlots
            : [];

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
        await WriteRecoveryVectorPreparedAsync(
                aborted with { PendingLoadCancellation = null },
                vector,
                cancellationToken,
                handedOverOpenSlots)
            .ConfigureAwait(false);
        PublishOperatorResponse(
            "RECOVERY_VECTOR_AUTHORIZED",
            $"装货取消已获服务端授权，装货已停止，开始将{FormatSlots(vector.Slots)}清空。 ");
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
    /// Sends one <c>LoadCancellationStartRequested</c> and returns its authorization, or <c>null</c>
    /// once a refusal has been shown to the operator.
    /// </summary>
    /// <remarks>
    /// Shared by the cancellation of a load in flight and the one before any sublot, which differ only
    /// in the attempt they name: the content of a retry is the first press's
    /// (<see cref="RecallOrRecordLoadCancellationAsync"/>) and every send takes a new messageId.
    /// </remarks>
    private async Task<(LoadCancellationAuthorizationPayload Authorization, WireToGateOperatorContextPayload Operator)?>
        AskForLoadCancellationAsync(
            WireToGateRecoveryState state,
            string cancellationId,
            string demandId,
            string? slotOperationAttemptId,
            string reason,
            CancellationToken cancellationToken)
    {
        // Only a press that can actually send becomes the first press. One refused for readiness
        // leaves no bytes on the wire, and remembering its operator would have the next press repeat
        // a verification the server never saw.
        RequireSessionReadyToSend();
        bool recalled = string.Equals(
            state.PendingLoadCancellation?.CancellationId,
            cancellationId,
            StringComparison.Ordinal);
        WireToGatePendingLoadCancellation pending = await RecallOrRecordLoadCancellationAsync(
                state,
                cancellationId,
                slotOperationAttemptId,
                reason,
                cancellationToken)
            .ConfigureAwait(false);
        if (slotOperationAttemptId is null)
        {
            // Published before the send, which waits for the answer: sublot entry closes from here
            // (CanSubmitSublot), and the operator sees why while the request is out.
            PublishOperatorResponse(
                "RECOVERY_VECTOR_REQUESTED",
                "已申请取消本站装货，等待服务端答复；取消结束前暂停扫码。 ");
        }

        WireToGateOperatorContextPayload operatorContext = OperatorOf(pending);
        LoadCancellationStartRequestedPayload request = new(
            cancellationId,
            demandId,
            slotOperationAttemptId,
            operatorContext,
            pending.Reason);
        // A messageId of its own for every send, as in RequestRecoveryActionVectorCoreAsync: the
        // identity the server keeps is cancellationId, which stays in the payload.
        LoadCancellationAuthorizationPayload authorization;
        try
        {
            authorization = await _session
                .RequestLoadCancellationStartAsync(
                    Guid.NewGuid().ToString("D"),
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (
            !recalled
                && string.Equals(exception.Message, "WIRE_TO_GATE_NOT_READY", StringComparison.Ordinal))
        {
            // The session dropped between the check above and the send; the client refuses before
            // writing anything, so what this press recorded was never a first press either. An entry
            // recalled from an earlier press did go out and stays.
            await ForgetLoadCancellationRequestAsync(cancellationId, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
        if (authorization.Decision == "REJECTED")
        {
            await ForgetLoadCancellationRequestAsync(cancellationId, cancellationToken)
                .ConfigureAwait(false);
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                $"服务端拒绝装货取消：{authorization.Problem?.ReasonCode ?? "ACTION_NOT_ALLOWED_IN_STATE"}。 ");
            return null;
        }

        return (authorization, operatorContext);
    }

    /// <summary>
    /// The cancellation before any sublot is entered: ask, and on an authorization naming no slot and
    /// no attempt, report <c>ALL_EMPTY</c> with no slot results. No door is opened on any path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An authorization that names a slot is not a narrower or wider version of this cancellation
    /// but a different one -- this vehicle commanded nothing, so there is no slot the server could
    /// mean -- and is refused before anything is journaled. The attempt is held to the request's
    /// <c>null</c> by <c>ValidateLoadCancellationAuthorization</c> already; both refusals reach the
    /// operator as <c>RECOVERY_RESPONSE_SCOPE_MISMATCH</c>.
    /// </para>
    /// <para>
    /// The unanswered request stays on file until the result is acknowledged, not merely until it is
    /// authorized: the entry request and the pending cancellation go together, once the server has
    /// the result. Whether the stop is then over is the server's to say in its next snapshot; nothing
    /// here marks the task cancelled.
    /// </para>
    /// </remarks>
    private async Task<bool> RequestLoadCancellationBeforeSublotAsync(
        WireToGateRecoveryState state,
        string reason,
        CancellationToken cancellationToken)
    {
        LoadCancellationBeforeSublotTarget target = FindLoadCancellationBeforeSublot(state)
            ?? throw new InvalidOperationException("RECOVERY_OPERATION_CONTEXT_MISSING");
        if (state.PendingLoadCancellation?.SlotOperationAttemptId is not null)
        {
            throw new InvalidDataException("RECOVERY_VECTOR_CONFLICT");
        }

        if (await AskForLoadCancellationAsync(
                state,
                target.CancellationId,
                target.DemandId,
                null,
                reason,
                cancellationToken)
            .ConfigureAwait(false) is not var (authorization, operatorContext))
        {
            return false;
        }

        if (authorization.Slots.Count != 0)
        {
            throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
        }

        WireToGateRecoveryVectorContext vector = new(
            WireToGateRecoveryVectorTypes.LoadCancellation,
            authorization.CancellationId,
            null,
            authorization.DemandId,
            null,
            null,
            [],
            null,
            operatorContext.OperatorId,
            operatorContext.VerificationMethod,
            operatorContext.VerifiedAt);
        WireToGateRecoveryState authorized = await ReadRecoveryStateCachedAsync(cancellationToken)
            .ConfigureAwait(false);
        await WriteRecoveryVectorPreparedAsync(authorized, vector, cancellationToken)
            .ConfigureAwait(false);
        PublishOperatorResponse(
            "RECOVERY_VECTOR_AUTHORIZED",
            "装货取消已获服务端授权。本站尚未录入子批、没有要清空的仓位，不会打开仓门，正在上报结果。 ");
        return await ReportLoadCancellationBeforeSublotAsync(vector, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reports the authorized cancellation before any sublot as <c>ALL_EMPTY</c> with no slot
    /// results, and settles it once the server acknowledges.
    /// </summary>
    /// <remarks>
    /// The result goes through the recovery vector executor's empty-slot branch, which touches no IO
    /// and journals the observation time, so a press repeated after a lost acknowledgement sends the
    /// same bytes. The vehicle-stopped check the other vectors make is not made here: it guards door
    /// IO, and there is none.
    /// </remarks>
    private async Task<bool> ReportLoadCancellationBeforeSublotAsync(
        WireToGateRecoveryVectorContext vector,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryVectorExecutionResult result = await _vectorExecutor
            .ExecuteClearAsync(vector, progress: null, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await SendRecoveryVectorResultAsync(
                    vector,
                    LoadCancellationResultKey(vector),
                    result,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"扫码前装货取消的结果暂未收到DurableAck：cancellation={vector.PrimaryId}。",
                exception);
            PublishOperatorEvent(
                $"recovery-vector-result-pending:{vector.VectorType}:{vector.PrimaryId}",
                "RESULT_ACK_PENDING",
                "装货取消结果已持久化，等待服务端确认；没有打开任何仓门。 ");
            return false;
        }

        await SettleLoadCancellationBeforeSublotAsync(vector, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Clears the journal and the outstanding entry request once the result is acknowledged.
    /// </summary>
    /// <remarks>
    /// The entry request is dropped unless it provably belongs to another demand -- a worklist under
    /// the same operation session that does not name this one. The task itself is left as the server
    /// last described it: ending the stop, or not, arrives as the server's next snapshot.
    /// </remarks>
    private async Task SettleLoadCancellationBeforeSublotAsync(
        WireToGateRecoveryVectorContext vector,
        CancellationToken cancellationToken)
    {
        await CompleteRecoveryVectorStateAsync(vector, cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _currentEntryRequest) is { } request
            && !(_session.CurrentJourney.CurrentStopWorklist is { } worklist
                && string.Equals(
                    worklist.OperationSessionId,
                    request.OperationSessionId,
                    StringComparison.Ordinal)
                && worklist.Items.All(item => !string.Equals(
                    item.DemandId,
                    vector.DemandId,
                    StringComparison.Ordinal))))
        {
            Interlocked.CompareExchange(ref _currentEntryRequest, null, request);
        }

        PublishOperatorEvent(
            $"recovery-vector-completed:{vector.VectorType}:{vector.PrimaryId}",
            "RECOVERY_VECTOR_COMPLETED",
            "装货取消结果已被服务端确认，未打开任何仓门；本站任务以服务端下发的状态为准。 ");
    }

    /// <summary>
    /// On a session coming up, settles a cancellation before any sublot whose result the handshake
    /// has already had acknowledged.
    /// </summary>
    /// <remarks>
    /// An unacknowledged result is replayed during the handshake, before the session is ready, so by
    /// the time this runs the journal says whether the server has it. Without this the vector would
    /// outlive the stop: once the server ends the stop it sends no further entry request, and the
    /// vector would refuse every later cancellation as a conflict.
    /// </remarks>
    private async Task RestoreLoadCancellationBeforeSublotAsync(
        WireToGateRecoveryVectorContext vector,
        CancellationToken cancellationToken)
    {
        WireToGateDurableMessage? result = await _session.Journal
            .ReadOutgoingByDeduplicationKeyAsync(LoadCancellationResultKey(vector), cancellationToken)
            .ConfigureAwait(false);
        if (result is not { Acknowledged: true })
        {
            PublishOperatorEvent(
                $"load-cancellation-before-sublot-restored:{vector.PrimaryId}",
                "RESULT_ACK_PENDING",
                "装货取消已获服务端授权，结果尚未得到服务端确认；可再按一次「取消装货」补报，不会打开仓门。 ");
            return;
        }

        await _recoveryRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken)
                .ConfigureAwait(false);
            if (state.RecoveryVector is { } current
                && WireToGateRecoveryVectorTypes.IsLoadCancellationBeforeSublot(current)
                && current.PrimaryId == vector.PrimaryId)
            {
                await SettleLoadCancellationBeforeSublotAsync(current, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _recoveryRequestGate.Release();
        }
    }

    private static string LoadCancellationResultKey(WireToGateRecoveryVectorContext vector) =>
        $"recovery-vector-result:{WireToGateRecoveryVectorTypes.LoadCancellation}:{vector.PrimaryId}";

    /// <summary>The cancellationId every press over this load in flight asks about.</summary>
    private static string InFlightLoadCancellationId(string demandId, string slotOperationAttemptId) =>
        StableUuid($"{demandId}|{slotOperationAttemptId}|load-cancellation");

    /// <summary>
    /// Whether a <c>SlotOperationCommand</c> names an attempt that is no longer a new command for this
    /// vehicle: started and unsettled with nobody running it, or taken over by a load cancellation --
    /// open, or settled and acknowledged already (1086c4a redone, onboard-hmi#78).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The control server's outbox re-sends the command until it has the result, and an aborted load
    /// sends none: its conclusion is the cancellation's. Executed again, the command would find the door
    /// open and make up a refusal, or find it shut and unlock it without authorization.
    /// </para>
    /// <para>
    /// A settled cancellation leaves nothing in the recovery state, so it is recognised by its result in
    /// the durable outbox: the cancellationId is derived from the demand and the attempt, and so is the
    /// result's key. The caller has claimed the attempt already, so "nobody running it" is this claim.
    /// </para>
    /// </remarks>
    private async Task<bool> IsAttemptTakenOverAsync(
        WireToGateSlotOperationCommand command,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await _session.Journal.ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(
                state.UnsettledSlotOperationAttemptId,
                command.SlotOperationAttemptId,
                StringComparison.Ordinal)
            || string.Equals(
                state.RecoveryVector?.SlotOperationAttemptId,
                command.SlotOperationAttemptId,
                StringComparison.Ordinal))
        {
            return true;
        }

        string cancellationResultKey =
            $"recovery-vector-result:{WireToGateRecoveryVectorTypes.LoadCancellation}:"
            + InFlightLoadCancellationId(command.DemandId, command.SlotOperationAttemptId);
        return await _session.Journal
            .ReadOutgoingByDeduplicationKeyAsync(cancellationResultKey, cancellationToken)
            .ConfigureAwait(false) is not null;
    }

    private void ForgetLoadAwaitingOperator(string slotOperationAttemptId)
    {
        if (Volatile.Read(ref _loadAwaitingOperator) is { } waiting
            && string.Equals(waiting.SlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal))
        {
            Interlocked.CompareExchange(ref _loadAwaitingOperator, null, waiting);
        }
    }

    /// <summary>
    /// After a restart, sends again the load cancellation the operator pressed over this attempt and
    /// never had an answer to, instead of settling the attempt as interrupted (1acb018 redone,
    /// onboard-hmi#78).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The executor was aborted by the cancellation or went with the process; either way the server may
    /// already have authorized it, and an interrupted settlement would report the attempt UNKNOWN and
    /// send the stop to recovery (ADR-cross-0046: the original command is neither withdrawn nor
    /// rewritten). The request repeats the first press's content from the journal, and an
    /// authorization is carried out as if it had arrived the first time.
    /// </para>
    /// <para>
    /// Called by the interrupted settlement, which has established that nobody is running the attempt
    /// and that no result was ever sent for it. The request path takes the recovery request gate, so a
    /// press already in progress finishes first and this one then finds the entry answered.
    /// </para>
    /// </remarks>
    private async Task ResendUnansweredLoadCancellationAsync(
        WireToGatePendingLoadCancellation pending,
        CancellationToken cancellationToken)
    {
        PublishOperatorEvent(
            $"load-cancellation-resent:{pending.CancellationId}",
            "RECOVERY_VECTOR_REQUESTED",
            "上次按下的装货取消没有收到服务端答复，已按首次内容重新申请；不会再执行原装货。 ");
        await RunRecoveryRequestAsync(
                WireToGateRecoveryVectorTypes.LoadCancellation,
                () => RequestLoadCancellationCoreAsync(pending.Reason, cancellationToken),
                cancellationToken)
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
        string correctionReason;
        if (existingVector is not null)
        {
            // Pressed again while the correction command is on its way. The server compares this
            // request's whole payload with the one it accepted, so the reason is the first press's;
            // the operator already comes from the journaled vector.
            vector = existingVector;
            correctionReason = state.RecoveryReason ?? RequireReason(reason);
        }
        else
        {
            WireToGateRecoveryOperationContext operation =
                state.LastCompletedLoadOperationContext
                ?? throw new InvalidOperationException("LOAD_CORRECTION_OPERATION_NOT_AVAILABLE");
            WireToGateOperatorContextPayload operatorContext = ReadOperatorContext();
            string correctionId = StableUuid(
                $"{operation.DemandId}|{operation.SlotOperationAttemptId}|load-correction");
            correctionReason = RequireReason(reason);
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
            await WriteRecoveryVectorPreparedAsync(
                    state with { RecoveryReason = correctionReason },
                    vector,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        WireToGateOperatorContextPayload context = RequirePersistedOperator(vector);
        // A messageId of its own for every send, as in RequestRecoveryActionVectorCoreAsync: the
        // identity the server keeps is correctionId.
        await _session.RequestLoadCorrectionAsync(
                Guid.NewGuid().ToString("D"),
                new LoadCorrectionRequestedPayload(
                    vector.PrimaryId,
                    vector.DemandId,
                    vector.SlotOperationAttemptId
                        ?? throw new InvalidDataException("RECOVERY_COMMAND_INVALID"),
                    vector.Slots,
                    context,
                    correctionReason),
                cancellationToken)
            .ConfigureAwait(false);
        PublishOperatorResponse(
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
        if (action == ForcedMechanicalRecoveryAction && state.ForcedIsolation is not null)
        {
            throw new InvalidOperationException("HARDWARE_RECOVERY_RECORD_REQUIRED");
        }

        WireToGateRecoveryOperationContext operation = FindRecoveryOperation(state, action)
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

            ObserveRecoverySessionAttempt(
                snapshot.ExceptionRecoverySessionId, snapshot.SlotOperationAttemptId);
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
                ObserveRecoverySessionAttempt(
                    snapshot!.ExceptionRecoverySessionId, snapshot.SlotOperationAttemptId);
                ValidateRecoverySessionSnapshot(snapshot, operation);
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

            ObserveRecoverySessionAttempt(
                opened.ExceptionRecoverySessionId, opened.SlotOperationAttemptId);
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

            PublishOperatorResponse(
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
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                $"服务端拒绝恢复动作 {action}：{exception.Message}。未执行仓门IO。 ");
            throw;
        }
        ObserveRecoverySessionAttempt(
            accepted.ExceptionRecoverySessionId, accepted.SlotOperationAttemptId);
        RequireSameSlotOperationAttempt(accepted.SlotOperationAttemptId, operation);
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
            await SendLoadCompensationRequestAsync(vector, cancellationToken)
                .ConfigureAwait(false);
        }

        PublishOperatorResponse(
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
                forcedRecoveryGeneration: null,
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
                forcedRecoveryGeneration: null,
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
                forcedRecoveryGeneration: null,
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

    /// <summary>
    /// Handles <c>ForcedMechanicalRecoveryCommand</c>, the command half of
    /// <c>CV-FORCED-MECHANICAL-RECOVERY</c>.
    /// </summary>
    /// <remarks>
    /// The command's <c>demandId</c> is nullable on the wire, but this onboard only ever asks for a
    /// forced mechanical recovery over a bound load or unload, both of which carry a demand, so a
    /// command that carries no demand cannot be the authorization for the vector this end
    /// prepared.  Refusing is the same judgement <c>HANDOFF_ONLY_ON_AUTHORIZED_COMMAND</c> makes
    /// for the sibling vector: an unscoped command is not a narrower authorization, it is a
    /// different one.
    /// </remarks>
    private async Task HandleForcedMechanicalRecoveryCommandAsync(
        WireToGateForcedMechanicalRecoveryCommand command,
        CancellationToken cancellationToken)
    {
        // Checked here rather than as a throwing argument expression: an exception raised while
        // evaluating the arguments would be thrown before HandleRecoveryVectorCommandAsync is
        // entered, so it would miss that method's InvalidDataException handler and reach the
        // dispatcher's catch-all instead -- the operator would get a generic failure log rather
        // than the RECOVERY_BLOCKED event every other refusal on this path publishes.
        if (command.DemandId is not { } demandId)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"强制机械恢复命令未携带 demandId，与本端已绑定的仓位作业范围不符："
                    + $"message={command.MessageId}。未执行仓门IO。");
            PublishOperatorEvent(
                $"forced-recovery-demand-missing:{command.RecoveryActionId}",
                "RECOVERY_BLOCKED",
                "强制机械恢复命令未指明需求单，无法与本端待结算的仓位作业对应，已拒绝执行，"
                    + "未重复执行仓门IO。 ");
            return;
        }

        await HandleRecoveryVectorCommandAsync(
                command.MessageId,
                WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery,
                command.RecoveryActionId,
                command.ExceptionRecoverySessionId,
                demandId,
                null,
                null,
                command.Slots,
                command.ForcedRecoveryGeneration,
                state => WireToGateRecoveryCommandHash.ForRecoveryAction(
                    command.RecoveryActionId,
                    demandId,
                    state.OperationContext?.SlotOperationAttemptId ?? string.Empty,
                    command.Slots,
                    command.ForcedRecoveryGeneration),
                correction: false,
                resultKey: $"recovery-vector-result:{WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery}:{command.RecoveryActionId}",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <remarks>
    /// <para>
    /// The same operator who asked for the forced recovery confirms it: the action and its outcome
    /// are one person's account of what was done at the vehicle.
    /// </para>
    /// <para>
    /// The observation time is written before the result goes out, so a press after a lost
    /// acknowledgement repeats the durable result byte for byte instead of making a second one.
    /// Once the server acknowledges it the business side is settled -- the server has cancelled the
    /// operation and needs no OperationResult for it -- and the device side begins:
    /// <see cref="SettleForcedIsolationAsync"/>.
    /// </para>
    /// </remarks>
    private async Task<bool> ConfirmForcedMechanicalRecoveryCoreAsync(
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken)
            .ConfigureAwait(false);
        WireToGateRecoveryVectorContext context = AwaitingForcedConfirmation(state)
            ?? throw new InvalidOperationException("FORCED_RECOVERY_NOT_AUTHORIZED");
        if (!string.Equals(
                ReadOperatorContext().OperatorId,
                context.OperatorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("RECOVERY_OPERATOR_MISMATCH");
        }

        DateTimeOffset observedAt = state.RecoveryResultObservedAt ?? _clock.Now.ToUniversalTime();
        if (state.RecoveryResultObservedAt is null)
        {
            await WriteRecoveryStateCachedAsync(
                    state with { RecoveryResultObservedAt = observedAt },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string resultKey =
            $"recovery-vector-result:{WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery}:{context.PrimaryId}";
        try
        {
            await SendRecoveryVectorResultAsync(
                    context,
                    resultKey,
                    new WireToGateRecoveryVectorExecutionResult(
                        context.VectorType,
                        context.PrimaryId,
                        context.ExceptionRecoverySessionId,
                        context.DemandId,
                        context.SlotOperationAttemptId,
                        null,
                        "MECHANICALLY_ISOLATED",
                        [],
                        observedAt,
                        "NONE"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"强制机械取出结果暂未收到DurableAck：id={context.PrimaryId}。",
                exception);
            PublishOperatorEvent(
                $"recovery-vector-result-pending:{context.VectorType}:{context.PrimaryId}",
                "RESULT_ACK_PENDING",
                "强制机械取出结果已持久化，等待服务端确认；可再次按确认重发，不会输出开锁。 ");
            return false;
        }

        await SettleForcedIsolationAsync(context, cancellationToken).ConfigureAwait(false);
        PublishRecoveryVectorOperation(
            context,
            WireToGateHmiOperationStage.RecoveryRequired,
            $"强制机械取出已上报；{FormatSlots(context.Slots)}物理状态未知，禁止操作，等待提交硬件恢复记录。",
            "isolated");
        PublishOperatorEvent(
            $"forced-recovery-isolated:{context.PrimaryId}",
            "RECOVERY_VECTOR_COMPLETED",
            $"强制机械取出已由服务端确认；{FormatSlots(context.Slots)}物理状态未知，修复后请提交硬件恢复记录。 ");
        return true;
    }

    /// <summary>
    /// The forced mechanical recovery this vehicle holds an authorization for and has not yet
    /// reported, or <c>null</c>. Authorized means bound: the command was checked against the
    /// prepared vector and its hash stamped on it.
    /// </summary>
    private static WireToGateRecoveryVectorContext? AwaitingForcedConfirmation(
        WireToGateRecoveryState state) =>
        state.RecoveryVector is
        {
            VectorType: WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery,
            CommandContentSha256: not null
        } vector
            ? vector
            : null;

    private void PublishForcedConfirmationAwaited(WireToGateRecoveryVectorContext context)
    {
        string guidance =
            $"强制机械取出已授权：请先断电、抱闸隔离车辆，再由有资质人员以机械方式开锁或拆卸，取出{FormatSlots(context.Slots)}的货物；"
            + "系统不会输出开锁。完成后由申请人按「已隔离并完成机械取出」确认。";
        PublishRecoveryVectorOperation(
            context,
            WireToGateHmiOperationStage.RecoveryRequired,
            guidance,
            "awaiting-confirmation");
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

    /// <param name="forcedRecoveryGeneration">
    /// The generation the command was issued under, for
    /// <see cref="WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery"/>; <c>null</c> for the
    /// four vectors the control server does not fence by generation.
    /// </param>
    /// <remarks>
    /// <paramref name="forcedRecoveryGeneration"/> is required rather than defaulted so that a
    /// sixth vector cannot inherit "no fence" by saying nothing.  An unfenced default is the shape
    /// that fails open, and every caller passing it explicitly is what makes the four <c>null</c>s
    /// a decision on the record instead of an omission.
    /// </remarks>
    private async Task HandleRecoveryVectorCommandAsync(
        string commandMessageId,
        string vectorType,
        string primaryId,
        string? exceptionRecoverySessionId,
        string demandId,
        string? slotOperationAttemptId,
        string? handoffId,
        IReadOnlyList<int> slots,
        long? forcedRecoveryGeneration,
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
            // REFUSE_STALE_FORCED_RECOVERY_GENERATION.  Refusing happens before the replay
            // short-circuit and before binding, so a fenced command reaches neither the journal
            // nor the IO path: the control server has already moved past this generation and
            // reissued under a newer one, and executing it now would unlock a slot set the server
            // no longer believes is in scope.
            //
            // Raising the fence is the opposite, and lives in BindRecoveryVectorCommandAsync
            // after the command has been proved to name this vector and this scope. Raising it
            // here would let one unvalidated command carrying an absurd generation park the fence
            // above every genuine one the server can still issue -- fail-closed, permanent, and
            // reachable from a single malformed message.
            if (forcedRecoveryGeneration is { } generation
                && generation < state.ForcedRecoveryGeneration)
            {
                _logger.Write(
                    LogSeverity.Warning,
                    nameof(WireToGateBusinessService),
                    $"恢复向量命令被代际栅栏拒绝：code=FORCED_RECOVERY_GENERATION_STALE，"
                        + $"type={vectorType}，message={commandMessageId}，operationId={primaryId}，"
                        + $"attempt={state.UnsettledSlotOperationAttemptId}，"
                        + $"slots={FormatSlots(slots)}，命令代={generation}，"
                        + $"已持久代={state.ForcedRecoveryGeneration}。未执行仓门IO。");
                PublishOperatorEvent(
                    $"forced-recovery-generation-stale:{primaryId}:{generation}",
                    "RECOVERY_BLOCKED",
                    $"强制机械恢复命令的代际 {generation} 已过期（当前 "
                        + $"{state.ForcedRecoveryGeneration}），已拒绝执行，未重复执行仓门IO。 ");
                return;
            }

            WireToGateDurableMessage? existingResult = await _session.Journal
                .ReadOutgoingByDeduplicationKeyAsync(resultKey, cancellationToken)
                .ConfigureAwait(false);
            if (existingResult is not null)
            {
                PublishOperatorEvent(
                    $"recovery-vector-result-replay:{vectorType}:{primaryId}",
                    "OPERATION_REPLAY",
                    $"恢复向量 {vectorType} 的结果已存在，忽略重复命令，未再次执行仓门IO。 ");
                // Idempotent. A journal still holding this vector untouched is one a stop, or a failed
                // write, left behind after its refusal was already on file (onboard-hmi#123).
                if (vectorType is WireToGateRecoveryVectorTypes.LoadCompensation
                        or WireToGateRecoveryVectorTypes.FaultCargoHandoff
                    && state.RecoveryVector is { } onFile
                    && onFile.VectorType == vectorType
                    && onFile.PrimaryId == primaryId)
                {
                    await ReleaseRefusedVectorAsync(onFile, cancellationToken).ConfigureAwait(false);
                }

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
                    forcedRecoveryGeneration,
                    expectedHash,
                    correction,
                    cancellationToken)
                .ConfigureAwait(false);
            if (vectorType == WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery)
            {
                // REQ-0241: the vehicle stops sending unlock DOs here and does nothing physical at
                // all. The bind above put the authorization on disk; what comes next is the people at
                // the vehicle, and the operator's confirmation is what reports it.
                PublishForcedConfirmationAwaited(context);
                return;
            }

            if (!TryClaimOperation(operationKey))
            {
                return;
            }

            try
            {
                // Both motion checks answer a refusal the same way: the vehicle may start moving
                // between this one and the one the execution makes before it starts, and a refusal
                // there with no result would leave the server's session EXECUTING (onboard-hmi#129 C-2).
                Func<Task>? reportRefused =
                    vectorType is WireToGateRecoveryVectorTypes.LoadCompensation
                        or WireToGateRecoveryVectorTypes.FaultCargoHandoff
                        ? () => ReportRefusedBeforeUnlockAsync(context, resultKey, cancellationToken)
                        : null;
                try
                {
                    EnsureVehicleStoppedAndFresh();
                }
                catch (InvalidOperationException) when (reportRefused is not null)
                {
                    // Reported, then rethrown: the log line and the RECOVERY_BLOCKED event below
                    // are the operator's account of the refusal and stay exactly as they were.
                    await reportRefused().ConfigureAwait(false);
                    throw;
                }

                bool completed = await ExecuteRecoveryVectorAndReportAsync(
                        context,
                        correction,
                        cancellationToken,
                        result => SendRecoveryVectorResultAsync(
                            context,
                            resultKey,
                            result,
                            cancellationToken),
                        reportRefused)
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

    /// <summary>
    /// Answers a compensation or fault cargo command the vehicle refused before any unlock with its
    /// result, <c>FAILED</c> (onboard-hmi#123).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until #123 this refusal went out as a log line and an operator event and nothing on the wire.
    /// The server's workflow then waited in <c>AwaitingResult</c> for a result that never came, and
    /// since control-server#187 refuses the same action a second time there was no way on from there
    /// short of a reconnect. A <c>FAILED</c> recovery result is what the server closes the session on
    /// (control-server#169), so that is the answer owed.
    /// </para>
    /// <para>
    /// Before or after the unlock is the executor's to say, from the journal: it answers
    /// <c>null</c> for anything but a vector nothing has been done for, and then nothing is sent --
    /// what the vector already did is settled the way it always was, and a refusal is never
    /// claimed over it. The result goes through the same durable send as every vector result, under
    /// the same key, so a command issued again finds it and is answered as a replay.
    /// </para>
    /// <para>
    /// Only <see cref="EnsureVehicleStoppedAndFresh"/> is answered this way. A command that fails to
    /// bind names a vector this end did not prepare, and answering it would put a result on record
    /// for an action the two ends disagree about; the forced mechanical recovery never reaches the
    /// motion check at all, because it never unlocks.
    /// </para>
    /// </remarks>
    private async Task ReportRefusedBeforeUnlockAsync(
        WireToGateRecoveryVectorContext context,
        string resultKey,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryVectorExecutionResult? refused = await _vectorExecutor
            .RefuseBeforeUnlockAsync(context, "VEHICLE_NOT_READY", cancellationToken)
            .ConfigureAwait(false);
        if (refused is null)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"恢复向量已开始执行，车辆未就绪不按开锁前被拒上报：type={context.VectorType}，"
                    + $"id={context.PrimaryId}。");
            return;
        }

        try
        {
            await SendRecoveryVectorResultAsync(context, resultKey, refused, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException)
        {
            // Saved before it is sent, so a result that is on file goes out with the outbox on the next
            // session even though this send never heard back. One that never reached the outbox did not
            // go anywhere: the vector stays, and the command -- which the server sends again while it
            // has no result -- is refused afresh.
            bool onFile = await _session.Journal
                .ReadOutgoingByDeduplicationKeyAsync(resultKey, cancellationToken)
                .ConfigureAwait(false) is not null;
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                onFile
                    ? $"开锁前被拒的恢复向量结果已写入发件箱，暂未收到DurableAck：type={context.VectorType}，id={context.PrimaryId}。"
                    : $"开锁前被拒的恢复向量结果未能写入发件箱：type={context.VectorType}，id={context.PrimaryId}。",
                exception);
            if (!onFile)
            {
                return;
            }

            PublishOperatorEvent(
                $"recovery-vector-result-pending:{context.VectorType}:{context.PrimaryId}",
                "RESULT_ACK_PENDING",
                "恢复结果已持久化，等待服务端确认；不会重复执行仓门IO。 ");
        }

        await ReleaseRefusedVectorAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets a vector refused before any unlock, and the recovery session it belonged to, as soon as
    /// its result is in the outbox -- keeping the unsettled operation the vector was about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server closes the session on the <c>FAILED</c> result (control-server#169). Nothing else
    /// clears the vector or the session's identity -- only a completed vector does -- so without this
    /// the entry stays lit and every press is refused locally with
    /// <c>RECOVERY_SESSION_STATE_PENDING</c>: the server would no longer be stuck, the vehicle would.
    /// The session fields go through the same <see cref="ForgetRecoverySessionAsync"/> the refused
    /// resume of onboard-hmi#119 uses, with the same session and action guard.
    /// </para>
    /// <para>
    /// The order is the other way round from #119's -- answer on file first, forget second -- and has
    /// to be. A refused resume can be refused again against a cleared journal; a recovery command
    /// cannot be answered at all without its prepared vector, so forgetting first and stopping before
    /// the answer would leave the next copy of the command refused with
    /// <c>RECOVERY_VECTOR_CONTEXT_MISSING</c> and the server waiting for good. The gap this order leaves
    /// -- the answer on file, the vehicle stopped before forgetting -- is closed by the other two
    /// callers: a replay of the command, and the session's CLOSED snapshot.
    /// </para>
    /// <para>
    /// It does not wait for the acknowledgement. An unacknowledged result is replayed with the outbox
    /// on the next session and the server closes the session on it, but nothing sends the command
    /// again, so a release that waited would never come.
    /// </para>
    /// </remarks>
    private Task<bool> ReleaseRefusedVectorAsync(
        WireToGateRecoveryVectorContext context,
        CancellationToken cancellationToken) =>
        ForgetRecoverySessionAsync(
            context.ExceptionRecoverySessionId,
            context.PrimaryId,
            released => released.RecoveryVector is { } vector
                && vector.VectorType == context.VectorType
                && vector.PrimaryId == context.PrimaryId
                    ? ForgetRefusedVector(released, vector)
                    : null,
            $"开锁前被拒的恢复向量结果已写入发件箱，但清除向量与恢复会话记录失败：type={context.VectorType}，"
                + $"id={context.PrimaryId}。",
            cancellationToken);

    /// <summary>
    /// <paramref name="state"/> without <paramref name="vector"/>, when the journal shows the vector
    /// did nothing; <c>null</c> when it may have acted.
    /// </summary>
    /// <remarks>
    /// "Did nothing" is the same reading <c>RefuseBeforeUnlockAsync</c> makes: prepared, no active
    /// unlock set, no slot counted complete. Such a vector leaves no trace to settle, so its slot
    /// results, observation time and checkpoint go with it; the attempt stays unsettled under its own
    /// operation context. A vector that may have acted is settled by its result, never forgotten.
    /// </remarks>
    private static WireToGateRecoveryState? ForgetRefusedVector(
        WireToGateRecoveryState state,
        WireToGateRecoveryVectorContext vector) =>
        state.ProvenRecoveryCheckpoint == WireToGateRecoveryCheckpoint.Prepared
        && state.ActiveUnlockSlots.Count == 0
        && state.CompletedSlots.Count == 0
            ? state with
            {
                UnsettledSlotOperationAttemptId =
                    state.OperationContext?.SlotOperationAttemptId == vector.SlotOperationAttemptId
                        ? vector.SlotOperationAttemptId
                        : null,
                SlotResults = [],
                RecoveryVector = null,
                RecoveryResultObservedAt = null
            }
            : null;

    private async Task<WireToGateRecoveryVectorContext> BindRecoveryVectorCommandAsync(
        WireToGateRecoveryState state,
        string vectorType,
        string primaryId,
        string? exceptionRecoverySessionId,
        string demandId,
        string? slotOperationAttemptId,
        string? handoffId,
        IReadOnlyList<int> slots,
        long? forcedRecoveryGeneration,
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
            && vectorType is not (WireToGateRecoveryVectorTypes.FaultCargoHandoff
                or WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery))
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
            // Read through the same helper the entry and the request path use. A settled load is a
            // valid subject here for the same reason it is there -- the server may judge
            // RecoveryRequired the attempt the vehicle just reported COMPLETED -- and reading it any
            // other way is how an entry opens on one subject while the bind refuses a different one.
            WireToGateRecoveryOperationContext operation = FindRecoveryOperation(
                    state,
                    vectorType == WireToGateRecoveryVectorTypes.LoadCompensation
                        ? CompensateLoadAction
                        : null)
                ?? throw new InvalidDataException("RECOVERY_OPERATION_CONTEXT_MISSING");
            if (operation.DemandId != demandId
                || operation.SlotOperationAttemptId != boundSlotOperationAttemptId
                || !operation.Slots.SequenceEqual(slots))
            {
                throw new InvalidDataException("RECOVERY_SCOPE_MISMATCH");
            }
        }

        // The generation is stamped on the durable context at first bind and required to match on
        // every rebind.  A second command for the same recoveryActionId under a different
        // generation is not a retransmission of this one: the server reissues under a new action
        // when it bumps, so a differing generation here means the two ends disagree about what is
        // being authorized.
        if (context.ForcedRecoveryGeneration is { } boundGeneration
            && boundGeneration != forcedRecoveryGeneration)
        {
            throw new InvalidDataException("RECOVERY_SCOPE_MISMATCH");
        }

        string commandHash = expectedHash(state);

        if (context.CommandContentSha256 is not null
            && !string.Equals(
                context.CommandContentSha256,
                commandHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("RECOVERY_COMMAND_HASH_MISMATCH");
        }

        if (context.CommandContentSha256 is null
            || context.ForcedRecoveryGeneration != forcedRecoveryGeneration)
        {
            context = context with
            {
                CommandContentSha256 = commandHash,
                ForcedRecoveryGeneration = forcedRecoveryGeneration
            };

            // The fence rises here and nowhere else: every scope comparison above has passed, so
            // this generation came from a command that really does authorize this vector. It goes
            // out in the same write as the stamped context, because a generation persisted
            // without the context it belongs to would fence the vehicle against work that
            // nothing recorded.
            await WriteRecoveryStateCachedAsync(
                    state with
                    {
                        ForcedRecoveryGeneration =
                            forcedRecoveryGeneration is { } authorizedGeneration
                                && authorizedGeneration > state.ForcedRecoveryGeneration
                                    ? authorizedGeneration
                                    : state.ForcedRecoveryGeneration,
                        RecoveryVector = context
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return context;
    }

    private async Task<bool> ExecuteRecoveryVectorAndReportAsync(
        WireToGateRecoveryVectorContext context,
        bool correction,
        CancellationToken cancellationToken,
        Func<WireToGateRecoveryVectorExecutionResult, Task>? sendResult = null,
        Func<Task>? reportRefusedBeforeUnlock = null)
    {
        try
        {
            EnsureVehicleStoppedAndFresh();
        }
        catch (InvalidOperationException) when (reportRefusedBeforeUnlock is not null)
        {
            await reportRefusedBeforeUnlock().ConfigureAwait(false);
            throw;
        }

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
            // REPORT_FORCED_RECOVERY_OUTCOME.  No slot results: this message's schema carries only
            // the slot set, because a forced mechanical recovery is a human opening a locker by hand
            // and no electronic reading proves anything about what was done. The outcome is the
            // operator's confirmation, never an executor's finish (onboard-hmi#107). The two proof
            // flags are constants for the same reason -- see ForcedMechanicalRecoveryResultPayload.
            WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery => _session
                .SendForcedMechanicalRecoveryResultAsync(
                    resultKey,
                    messageId,
                    new ForcedMechanicalRecoveryResultPayload(
                        context.ExceptionRecoverySessionId
                            ?? throw new InvalidDataException("RECOVERY_SESSION_SCOPE_MISMATCH"),
                        context.PrimaryId,
                        context.ForcedRecoveryGeneration
                            ?? throw new InvalidDataException("RECOVERY_COMMAND_INVALID"),
                        result.OverallOutcome,
                        context.Slots,
                        RequirePersistedOperator(context),
                        result.ObservedAt,
                        ElectronicEmptyProven: false,
                        VehicleReadyProven: false),
                    cancellationToken),
            _ => throw new InvalidDataException("RECOVERY_VECTOR_TYPE_INVALID")
        };
    }

    private Task CompleteRecoveryVectorStateAsync(
        WireToGateRecoveryVectorContext context,
        CancellationToken cancellationToken) =>
        SettleRecoveryVectorStateAsync(context, isolation: null, cancellationToken);

    /// <summary>
    /// Settles the business side of an acknowledged <c>MECHANICALLY_ISOLATED</c> exactly as a
    /// completed vector does -- no attempt, no operation context, no recovery session fields, and no
    /// OperationResult, because the server has already cancelled the operation -- and records the
    /// device side in the same write: the whole slot set is physically unknown (REQ-0241).
    /// </summary>
    private Task SettleForcedIsolationAsync(
        WireToGateRecoveryVectorContext context,
        CancellationToken cancellationToken) =>
        SettleRecoveryVectorStateAsync(
            context,
            new WireToGateForcedIsolation(
                context.ExceptionRecoverySessionId
                    ?? throw new InvalidDataException("RECOVERY_SESSION_SCOPE_MISMATCH"),
                context.PrimaryId,
                context.Slots.Order().ToArray()),
            cancellationToken);

    private async Task SettleRecoveryVectorStateAsync(
        WireToGateRecoveryVectorContext context,
        WireToGateForcedIsolation? isolation,
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

        // Never replace an isolation that is still standing: its slots would become operable with no
        // hardware recovery record. The request path already refuses a second forced recovery; this
        // is the write that must not happen whatever led here.
        if (isolation is not null
            && state.ForcedIsolation is { } standing
            && standing.RecoveryActionId != isolation.RecoveryActionId)
        {
            throw new InvalidDataException("HARDWARE_RECOVERY_RECORD_REQUIRED");
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
                    RecoveryVector = null,
                    PendingLoadCancellation = null,
                    ForcedIsolation = isolation ?? state.ForcedIsolation
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <param name="handedOverOpenSlots">
    /// The doors an aborted load may have left open, handed to a load cancellation as the prepared
    /// vector's active unlock set (<c>WireToGateRecoveryVectorExecutor.HandedOverOpenSlots</c>).
    /// </param>
    private async Task WriteRecoveryVectorPreparedAsync(
        WireToGateRecoveryState state,
        WireToGateRecoveryVectorContext context,
        CancellationToken cancellationToken,
        IReadOnlyList<int>? handedOverOpenSlots = null)
    {
        await WriteRecoveryStateCachedAsync(
                state with
                {
                    UnsettledSlotOperationAttemptId = context.SlotOperationAttemptId,
                    ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.Prepared,
                    ActiveUnlockSlots = handedOverOpenSlots?.ToArray() ?? [],
                    CompletedSlots = [],
                    SlotResults = [],
                    RecoveryVector = context,
                    ExceptionRecoverySessionId = context.ExceptionRecoverySessionId
                        ?? state.ExceptionRecoverySessionId,
                    RecoveryActionId = context.VectorType is
                        WireToGateRecoveryVectorTypes.LoadCompensation
                        or WireToGateRecoveryVectorTypes.FaultCargoHandoff
                        or WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery
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

    /// <summary>Test seam: the cached recovery state every entry gate reads (onboard-hmi#129).</summary>
    internal WireToGateRecoveryState CachedRecoveryStateForTest => Volatile.Read(ref _lastRecoveryState);

    /// <summary>Test seam: one <see cref="ReadRecoveryStateCachedAsync"/>, the refresh after a result is recorded.</summary>
    internal Task<WireToGateRecoveryState> RefreshCachedRecoveryStateForTestAsync(
        CancellationToken cancellationToken) =>
        ReadRecoveryStateCachedAsync(cancellationToken);

    /// <summary>Test seam: one <see cref="WriteRecoveryStateCachedAsync"/>.</summary>
    internal Task WriteCachedRecoveryStateForTestAsync(
        WireToGateRecoveryState state,
        CancellationToken cancellationToken) =>
        WriteRecoveryStateCachedAsync(state, cancellationToken);

    /// <summary>
    /// Reads the journal's recovery state and caches it, inside the journal step (onboard-hmi#129): see
    /// <see cref="CacheRecoveryState"/>.
    /// </summary>
    private async Task<WireToGateRecoveryState> ReadRecoveryStateCachedAsync(
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState? read = null;
        await _session.Journal
            .UpdateRecoveryStateAsync(
                static _ => null,
                state =>
                {
                    read = state;
                    CacheRecoveryState(state);
                },
                cancellationToken)
            .ConfigureAwait(false);
        // Never an empty state on a journal that answered: empty reads as "nothing to recover", which
        // is what the restored projection and every entry gate would then show.
        return read ?? throw new InvalidDataException("RECOVERY_STATE_NOT_READ");
    }

    /// <summary>Writes the recovery state and caches it, inside the journal step (onboard-hmi#129).</summary>
    private async Task WriteRecoveryStateCachedAsync(
        WireToGateRecoveryState state,
        CancellationToken cancellationToken)
    {
        await _session.Journal
            .UpdateRecoveryStateAsync(_ => state, CacheRecoveryState, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The one writer of the cached recovery state the entry gates read, handed to the journal as the
    /// settled callback of <see cref="IWireToGateJournal.UpdateRecoveryStateAsync(Func{WireToGateRecoveryState, WireToGateRecoveryState?}, Action{WireToGateRecoveryState}, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// Called inside the journal's step, so the cache takes the journal's states in the journal's
    /// order. Written after the step, a read or write that finished first could still cache last and
    /// put an older state over a newer one until the next read (onboard-hmi#123 review follow-up).
    /// </remarks>
    private void CacheRecoveryState(WireToGateRecoveryState state) =>
        Volatile.Write(ref _lastRecoveryState, state);

    /// <summary>
    /// The operation this recovery is about, or null when there is none.
    /// </summary>
    /// <param name="action">
    /// The recovery action asked for. <c>COMPENSATE_LOAD_ALL_EMPTY</c> is about a load and nothing
    /// else; a fault cargo handoff and a forced mechanical recovery are about whichever operation
    /// left cargo behind, a load or an unload (onboard-hmi#107, REQ-0240, REQ-0241). <c>null</c>
    /// stands for any action but compensation.
    /// </param>
    /// <remarks>
    /// <para>
    /// Whatever is armed and unsettled is the subject -- for compensation only if it is a load, and
    /// otherwise nothing. Only once the vehicle has finished with its armed operation does the
    /// settled load take over -- an operation whose result the vehicle already recorded is still a
    /// valid subject, because the server may judge that same attempt <c>RecoveryRequired</c> while
    /// the vehicle believes it finished, and that is exactly the state a compensation exists for.
    /// <c>MarkResultRecordedAsync</c> keeps the settled identity in
    /// <see cref="WireToGateRecoveryState.LastCompletedLoadOperationContext"/> for this case, so
    /// nothing here is guessed -- the identity is read, never reconstructed.
    /// </para>
    /// <para>
    /// An armed unload is therefore never a settled load's stand-in, however recent that load is.
    /// The ordinary sequence at the gate produces exactly that pair -- the load completed, the unload
    /// is running with a door open -- and falling back there would open an entry on a load nobody is
    /// asking about, then overwrite the journal when it was taken:
    /// <see cref="WriteRecoveryVectorPreparedAsync"/> rewrites the unsettled attempt to the vector's
    /// and empties the active unlock set, losing the record of the door standing open right now. The
    /// unload is the subject itself for the two actions that take cargo out of it. Until #107 it was
    /// no subject at all, which left an unload whose lock could not be repaired with only "resume
    /// after repair" -- no way out.
    /// </para>
    /// <para>
    /// Every caller reads the subject through this one helper -- the entry gates, the request path
    /// and the bind path -- because an entry that opens on a subject the bind path then refuses is
    /// the failure this exists to prevent.
    /// </para>
    /// </remarks>
    private static WireToGateRecoveryOperationContext? FindRecoveryOperation(
        WireToGateRecoveryState state,
        string? action) =>
        state.OperationContext is { } armed
            && string.Equals(
                state.UnsettledSlotOperationAttemptId,
                armed.SlotOperationAttemptId,
                StringComparison.Ordinal)
                ? armed.OperationType == OperationType.Load || action != CompensateLoadAction
                    ? armed
                    : null
                : state.LastCompletedLoadOperationContext;

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

    /// <summary>
    /// The operator and reason a load cancellation goes out with, fixed by the first press that
    /// sends it. The cancellationId is derived from the demand and the attempt, so every press asks
    /// about the same cancellation, and the server compares each request's whole payload with the
    /// one it authorized first (<c>UpsertSimpleWorkflowAsync</c>). A press retrying an unanswered
    /// request -- timed out, the answer lost on the way, the process restarted in between -- must
    /// repeat that content, or the server takes it for a different request under the same id and
    /// drops the connection. The journal is what outlives a restart, so the content is written there
    /// once the session has been found ready and immediately before the send; the caller checks
    /// readiness first and forgets an entry this press wrote if the client still refuses it as not
    /// ready, so a press that never left is never remembered as the first.
    ///
    /// The messageId is no part of this; every send takes a new one. The server's ProtocolInbox binds
    /// a messageId to the exact bytes it first carried, and sentAt is new on every press, so reusing
    /// one ends in a content conflict or in a replay of the first answer.
    /// </summary>
    private async Task<WireToGatePendingLoadCancellation> RecallOrRecordLoadCancellationAsync(
        WireToGateRecoveryState state,
        string cancellationId,
        string? slotOperationAttemptId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (state.PendingLoadCancellation is { } unanswered
            && string.Equals(unanswered.CancellationId, cancellationId, StringComparison.Ordinal))
        {
            return unanswered;
        }

        WireToGateOperatorContextPayload operatorContext = ReadOperatorContext();
        WireToGatePendingLoadCancellation pending = new(
            cancellationId,
            slotOperationAttemptId,
            operatorContext.OperatorId,
            operatorContext.VerificationMethod,
            operatorContext.VerifiedAt,
            RequireReason(reason));
        // Read again right before the write: the load's executor may have written its own checkpoints
        // since this press read the state, and writing the older copy back would undo them.
        WireToGateRecoveryState current = await _session.Journal.ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        await WriteRecoveryStateCachedAsync(
                current with { PendingLoadCancellation = pending },
                cancellationToken)
            .ConfigureAwait(false);
        return pending;
    }

    /// <summary>
    /// Either answer settles a load cancellation: a refusal records nothing on the server, and an
    /// authorization is journaled as the vector it starts. The next press is a new request, built
    /// from that press.
    /// </summary>
    private async Task ForgetLoadCancellationRequestAsync(
        string cancellationId,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(
                state.PendingLoadCancellation?.CancellationId,
                cancellationId,
                StringComparison.Ordinal))
        {
            await WriteRecoveryStateCachedAsync(
                    state with { PendingLoadCancellation = null },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The same readiness <c>WireToGateSessionClient.SendRecoveryRequestAsync</c> demands before it
    /// writes a byte, asked here so that nothing is journaled for a request that cannot leave.
    /// </summary>
    private void RequireSessionReadyToSend()
    {
        WireToGateSessionSnapshot session = _session.Current;
        if (!session.Connected
            || session.SessionGeneration is null
            || session.Readiness is not (WireToGateSessionReadiness.Ready
                or WireToGateSessionReadiness.RecoveryRequired))
        {
            throw new InvalidOperationException("WIRE_TO_GATE_NOT_READY");
        }
    }

    private static WireToGateOperatorContextPayload OperatorOf(
        WireToGatePendingLoadCancellation pending) =>
        new(pending.OperatorId, pending.OperatorVerificationMethod, pending.OperatorVerifiedAt);

    /// <summary>
    /// The administrator's reason, trimmed, or the action's fixed text when none was entered. The fixed text
    /// is what every session request carried before the reason could be entered, so an empty box changes
    /// nothing on the wire.
    /// </summary>
    private static string ReasonOrDefault(string? entered, string fixedText) =>
        string.IsNullOrWhiteSpace(entered) ? fixedText : entered.Trim();

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

    /// <summary>
    /// Refuses a recovery response that names an attempt other than the one in scope.
    /// </summary>
    /// <remarks>
    /// Applied to all three of the server's recovery messages, because the scope they agree on is
    /// what the compensation request is later sent under. A <c>null</c> here is the server naming
    /// no attempt, which challenges nothing.
    /// </remarks>
    /// <summary>
    /// Fixes the first <c>slotOperationAttemptId</c> a recovery session gives -- <c>null</c>
    /// included -- and refuses any later message of the same session that gives a different one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule is the fill rule in <c>8005-agv-program#95</c> (commit <c>6ed3564</c>): the value
    /// follows from the session's <c>demandId</c> and whether that demand had a slot operation, and
    /// no slot operation starts while a recovery session is open, so the three messages of one
    /// session "should give the same value; a disagreement means two sources, and is a defect".
    /// </para>
    /// <para>
    /// It is a different check from <see cref="RequireSameSlotOperationAttempt"/>. That one is the
    /// vehicle's record against the server's name, and a <c>null</c> name challenges nothing. This
    /// one is the server against itself, and a <c>null</c> after a name -- or a name after a
    /// <c>null</c> -- is exactly the disagreement it exists for. Once a session disagrees it stays
    /// refused: a later message agreeing with one of the two values does not say which was right.
    /// </para>
    /// <para>
    /// Held in memory. After a restart the server re-sends the session's snapshot, so the first value
    /// is re-established from the wire rather than from a journal that could itself be stale.
    /// </para>
    /// </remarks>
    private void ObserveRecoverySessionAttempt(
        string exceptionRecoverySessionId,
        string? slotOperationAttemptId)
    {
        lock (_recoverySessionAttemptGate)
        {
            if (string.Equals(
                    _inconsistentRecoverySessionId,
                    exceptionRecoverySessionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
            }

            if (_recoverySessionAttempt is not { } first
                || !string.Equals(
                    first.ExceptionRecoverySessionId,
                    exceptionRecoverySessionId,
                    StringComparison.Ordinal))
            {
                _recoverySessionAttempt = (exceptionRecoverySessionId, slotOperationAttemptId);
                return;
            }

            if (!string.Equals(
                    first.SlotOperationAttemptId,
                    slotOperationAttemptId,
                    StringComparison.Ordinal))
            {
                _inconsistentRecoverySessionId = exceptionRecoverySessionId;
                throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
            }
        }
    }

    private bool IsInconsistentRecoverySession(string exceptionRecoverySessionId)
    {
        lock (_recoverySessionAttemptGate)
        {
            return string.Equals(
                _inconsistentRecoverySessionId,
                exceptionRecoverySessionId,
                StringComparison.Ordinal);
        }
    }

    private static void RequireSameSlotOperationAttempt(
        string? serverNamedSlotOperationAttemptId,
        WireToGateRecoveryOperationContext operation)
    {
        if (!IsSameSlotOperationAttempt(serverNamedSlotOperationAttemptId, operation))
        {
            throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
        }
    }

    private static bool IsSameSlotOperationAttempt(
        string? serverNamedSlotOperationAttemptId,
        WireToGateRecoveryOperationContext operation) =>
        serverNamedSlotOperationAttemptId is null
        || string.Equals(
            serverNamedSlotOperationAttemptId,
            operation.SlotOperationAttemptId,
            StringComparison.Ordinal);

    private static void ValidateRecoverySessionSnapshot(
        WireToGateExceptionRecoverySessionSnapshot snapshot,
        WireToGateRecoveryOperationContext operation)
    {
        RequireSameSlotOperationAttempt(snapshot.SlotOperationAttemptId, operation);
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
        RequireSameSlotOperationAttempt(opened.SlotOperationAttemptId, operation);
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
        if (AwaitingForcedConfirmation(Volatile.Read(ref _lastRecoveryState)) is { } awaiting
            && awaiting.PrimaryId == context.PrimaryId)
        {
            PublishForcedConfirmationAwaited(awaiting);
            return;
        }

        PublishRecoveryVectorOperation(
            context,
            WireToGateHmiOperationStage.RecoveryRequired,
            $"恢复向量 {context.VectorType} 尚未完成：{FormatSlots(context.Slots)}，请保持车辆停稳。",
            "restored");
    }

    internal static string RecoveryVectorGuidance(
        WireToGateRecoveryVectorContext context,
        string phase,
        IReadOnlyList<int> active,
        IReadOnlyList<int> completed) => phase switch
        {
            "PREPARING" => $"正在检查{FormatSlots(context.Slots)}的安全条件。",
            "UNLOCKING" => $"正在打开{FormatSlots(active)}。",
            "WAITING_OPERATOR" when context.VectorType == WireToGateRecoveryVectorTypes.LoadCorrection =>
                $"请先从{FormatSlots(active)}取出原货物，再按提示重新放入并关门。",
            "WAITING_OPERATOR" => $"请在{FormatSlots(active)}取出货物并关门。",
            "VERIFYING" => $"正在核对仓门、货物和输出状态；已完成 {completed.Count}/{context.Slots.Count}。",
            "SAFE_FINISH" => "全部目标仓已达到安全收尾状态，正在上报恢复结果。",
            "PAUSED" => "恢复向量已暂停，物理状态未知，禁止重复操作仓门。",
            _ => $"正在处理{FormatSlots(context.Slots)}。"
        };

}
