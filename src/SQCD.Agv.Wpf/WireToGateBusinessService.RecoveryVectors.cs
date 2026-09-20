using System.Diagnostics;
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
    /// The demand the entries that fall back to the last settled load are about -- compensation,
    /// fault cargo handoff, forced mechanical recovery -- or <c>null</c> when they are not falling
    /// back (onboard-hmi#135).
    /// </summary>
    /// <remarks>
    /// <b>Read through <see cref="FindRecoveryOperation"/>, not beside it.</b> Whether the subject is
    /// the armed operation or the last settled load is that helper's rule, and the question here is
    /// only which of the two it picked -- so this compares its answer with the settled load rather
    /// than re-deriving "is anything armed", which is how two copies of one rule drift apart. With
    /// something armed the entries are about that operation, which the screen already names; the
    /// subject is the last settled load exactly when there is nothing armed, and then the operator
    /// has no other way to tell which demand a compensation is about.
    /// </remarks>
    public string? RecoveryFallbackDemandId
    {
        get
        {
            WireToGateRecoveryState state = Volatile.Read(ref _lastRecoveryState);
            return FindRecoveryOperation(state, null) is { } subject
                && ReferenceEquals(subject, state.LastCompletedLoadOperationContext)
                ? subject.DemandId
                : null;
        }
    }

    /// <summary>
    /// The slots left physically unknown by an acknowledged forced mechanical recovery (REQ-0241),
    /// ascending; empty when there are none.
    /// </summary>
    public IReadOnlyList<int> PhysicallyUnknownSlots =>
        Volatile.Read(ref _lastRecoveryState).ForcedIsolation?.PhysicallyUnknownSlots ?? [];

    /// <summary>The reason a press with nothing typed sends.</summary>
    public const string LoadCancellationDefaultReason = "现场确认装货取消，申请将目标仓位清空。";

    public Task<bool> RequestLoadCancellationAsync(
        string reason = LoadCancellationDefaultReason,
        CancellationToken cancellationToken = default) =>
        RequestLoadCancellationAsync(reason, null, cancellationToken);

    /// <param name="selectedDemandId">
    /// The demand the operator picked in the worklist, for a cancellation before any sublot at a stop
    /// that carries more than one (onboard-hmi#135). Ignored where the subject is not the operator's
    /// to choose: a load in flight is its own subject, one worklist item is that item, and a press
    /// repeating a request already sent takes its subject from the journal.
    /// </param>
    public Task<bool> RequestLoadCancellationAsync(
        string reason,
        string? selectedDemandId,
        CancellationToken cancellationToken) =>
        RunRecoveryRequestAsync(
            WireToGateRecoveryVectorTypes.LoadCancellation,
            () => RequestLoadCancellationCoreAsync(reason, selectedDemandId, cancellationToken),
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
            && FindLoadCancellationBeforeSublot(state, null).Availability
                is not LoadCancellationBeforeSublotAvailability.None;
    }

    /// <summary>
    /// Whether the entry is offered but waiting for the operator to pick a demand in the worklist:
    /// this stop carries more than one and nothing has gone out yet (onboard-hmi#135).
    /// </summary>
    /// <remarks>
    /// Read with no pick on purpose. The question is whether a pick is <i>needed</i>, which is a fact
    /// about the stop and the journal, not about what is highlighted on screen -- the view decides
    /// from this plus its own selection whether the button can be pressed. A load in flight is its
    /// own subject, so this is false while that cancellation is the one on offer.
    /// </remarks>
    public bool IsLoadCancellationDemandSelectionRequired =>
        !(CanUseStationOperator()
            && HasRecoveryVectorOrLoadOperation(WireToGateRecoveryVectorTypes.LoadCancellation))
        && CanRequestLoadCancellationBeforeSublot()
        && FindLoadCancellationBeforeSublot(Volatile.Read(ref _lastRecoveryState), null).Availability
            is LoadCancellationBeforeSublotAvailability.SelectionRequired;

    private sealed record LoadCancellationBeforeSublotTarget(string CancellationId, string DemandId);

    /// <summary>Why a cancellation before any sublot has no subject, when it has none.</summary>
    private enum LoadCancellationBeforeSublotAvailability
    {
        /// <summary>The entry does not apply at all, and is not offered.</summary>
        None,

        /// <summary>The subject is settled; <c>Target</c> names it.</summary>
        Ready,

        /// <summary>More than one demand at this stop and the operator has picked none yet.</summary>
        SelectionRequired,

        /// <summary>The picked demand is not in this stop's worklist. Nothing has gone out for it.</summary>
        SelectionMissing,

        /// <summary>
        /// The demand of the cancellation already sent is not in this stop's worklist any more.
        /// </summary>
        SentSelectionMissing
    }

    /// <param name="SubjectFromJournal">
    /// Whether <paramref name="Target"/> was recovered from the journaled <c>PendingLoadCancellation</c>
    /// rather than chosen now. Carried out of the search rather than inferred afterwards from
    /// "the pick and the target differ": they also differ when the pick names a demand this stop no
    /// longer has, and telling the operator "this press is a resend" there would name a request that
    /// was never sent.
    /// </param>
    private sealed record LoadCancellationBeforeSublotOutcome(
        LoadCancellationBeforeSublotAvailability Availability,
        LoadCancellationBeforeSublotTarget? Target,
        bool SubjectFromJournal = false);

    /// <summary>
    /// The demand a cancellation before any sublot would cancel, or why there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Protocol 2.0.0 took <c>demandId</c> off the entry request, and a stop's worklist carries its
    /// <c>operationSessionId</c> at the top with nothing per item, so the vehicle cannot read off
    /// which item the outstanding request belongs to. Where the stop names one demand, that is the
    /// one. Where it names several, <b>the operator picks in the worklist and the server validates
    /// the pick</b> (batch 7-06, <c>control-server#211</c>): the pick is a person's, made from the
    /// list the server itself sent, and this end still discovers no demand, selects none of its own
    /// accord and binds none (<c>FP-IS-01</c>'s <c>NEVER_DISCOVER_SELECT_OR_BIND_DEMAND</c>). What
    /// the vehicle must never do is choose one <i>for</i> the operator -- picking the first row, or
    /// re-deriving a subject after a restart -- and that is what
    /// <see cref="LoadCancellationBeforeSublotAvailability.SelectionRequired"/> and
    /// <see cref="LoadCancellationBeforeSublotAvailability.SentSelectionMissing"/> exist to prevent.
    /// </para>
    /// <para>
    /// <b>A cancellation already sent keeps its own subject.</b> Its demand is recovered by deriving
    /// each item's cancellationId and matching the journaled <c>PendingLoadCancellation</c>, not from
    /// whatever is picked now: the server compares a retry's whole payload with the one it first
    /// accepted, so a retry naming another demand is a different request under the same id. No field
    /// is added to the recovery state for this -- the derivation is the record.
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
    /// <param name="selectedDemandId">
    /// The demand picked in the worklist, or <c>null</c> when nothing is picked -- which is also what
    /// the entry gates and a retry after a restart pass, neither having a pick to offer.
    /// </param>
    private LoadCancellationBeforeSublotOutcome FindLoadCancellationBeforeSublot(
        WireToGateRecoveryState state,
        string? selectedDemandId)
    {
        if (Volatile.Read(ref _currentEntryRequest) is not { } request
            || _session.CurrentJourney.CurrentStopWorklist is not { } worklist
            || !string.Equals(
                worklist.OperationSessionId,
                request.OperationSessionId,
                StringComparison.Ordinal)
            || worklist.Revision != request.WorklistRevision
            || !string.Equals(worklist.StationId, request.StationId, StringComparison.Ordinal)
            || worklist.Items.Count == 0)
        {
            return NoLoadCancellationBeforeSublot;
        }

        bool running;
        lock (_operationAttemptGate)
        {
            running = _operationAttempts.Count > 0;
        }

        if (running || state.UnsettledSlotOperationAttemptId is not null)
        {
            return NoLoadCancellationBeforeSublot;
        }

        if (state.PendingLoadCancellation is { SlotOperationAttemptId: null } sent)
        {
            Core.WireToGateWorklistItem? sentItem = worklist.Items.FirstOrDefault(
                item => string.Equals(
                    LoadCancellationBeforeSublotId(item.DemandId, request.OperationSessionId),
                    sent.CancellationId,
                    StringComparison.Ordinal));
            return sentItem is null
                ? new(LoadCancellationBeforeSublotAvailability.SentSelectionMissing, null)
                : JudgeLoadCancellationBeforeSublot(state, sentItem, request) with
                {
                    SubjectFromJournal = true
                };
        }

        // The pick is checked against this worklist BEFORE the "exactly one item" case, not after.
        // Reading one item as "then that is the subject" would cancel it while the operator has a
        // demand picked that this stop no longer carries -- the window between the session layer
        // taking a new snapshot and the view clearing a selection that is gone (onboard-hmi#135
        // review). One item is a case of "nobody had to pick", never a reason to ignore a pick.
        Core.WireToGateWorklistItem? picked = selectedDemandId is null
            ? null
            : worklist.Items.FirstOrDefault(
                item => string.Equals(item.DemandId, selectedDemandId, StringComparison.Ordinal));
        if (selectedDemandId is not null && picked is null)
        {
            return new(LoadCancellationBeforeSublotAvailability.SelectionMissing, null);
        }

        if (picked is not null)
        {
            return JudgeLoadCancellationBeforeSublot(state, picked, request);
        }

        return worklist.Items.Count == 1
            ? JudgeLoadCancellationBeforeSublot(state, worklist.Items[0], request)
            : new(LoadCancellationBeforeSublotAvailability.SelectionRequired, null);
    }

    private static readonly LoadCancellationBeforeSublotOutcome NoLoadCancellationBeforeSublot =
        new(LoadCancellationBeforeSublotAvailability.None, null);

    /// <summary>
    /// The local exclusions, applied to whichever item is the subject: this end asks about a demand
    /// nothing has been commanded for, and says so before the server has to.
    /// </summary>
    private static LoadCancellationBeforeSublotOutcome JudgeLoadCancellationBeforeSublot(
        WireToGateRecoveryState state,
        Core.WireToGateWorklistItem item,
        WireToGateSublotEntryRequest request) =>
        string.Equals(state.OperationContext?.DemandId, item.DemandId, StringComparison.Ordinal)
            || string.Equals(
                state.LastCompletedLoadOperationContext?.DemandId,
                item.DemandId,
                StringComparison.Ordinal)
            ? NoLoadCancellationBeforeSublot
            : new(
                LoadCancellationBeforeSublotAvailability.Ready,
                new(
                    LoadCancellationBeforeSublotId(item.DemandId, request.OperationSessionId),
                    item.DemandId));

    /// <summary>The cancellationId every press over this demand at this stop asks about.</summary>
    private static string LoadCancellationBeforeSublotId(string demandId, string operationSessionId) =>
        StableUuid($"{demandId}|{operationSessionId}|load-cancellation-before-sublot");

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
        string? selectedDemandId,
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
            return await RequestLoadCancellationBeforeSublotAsync(
                    state,
                    reason,
                    selectedDemandId,
                    cancellationToken)
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
                vector,
                cancellationToken,
                handedOverOpenSlots,
                clearPendingLoadCancellation: true)
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
        string? selectedDemandId,
        CancellationToken cancellationToken)
    {
        if (state.PendingLoadCancellation?.SlotOperationAttemptId is not null)
        {
            throw new InvalidDataException("RECOVERY_VECTOR_CONFLICT");
        }

        LoadCancellationBeforeSublotOutcome outcome =
            FindLoadCancellationBeforeSublot(state, selectedDemandId);
        // Three of the four "no subject" cases are the operator's to resolve, not faults: they are
        // said in words and nothing is sent. Only "does not apply at all" stays an exception, which
        // is what a press on an entry that was never offered has always been.
        if (outcome is not { Availability: LoadCancellationBeforeSublotAvailability.Ready, Target: { } target })
        {
            switch (outcome.Availability)
            {
                case LoadCancellationBeforeSublotAvailability.SelectionRequired:
                    PublishOperatorResponse(
                        "RECOVERY_BLOCKED",
                        "本站有多条任务，请先在清单中选择要取消的任务，再按「取消装货」。 ");
                    return false;
                case LoadCancellationBeforeSublotAvailability.SelectionMissing:
                    PublishOperatorResponse(
                        "RECOVERY_BLOCKED",
                        "所选任务已不在本站清单，请重新选择要取消的任务。 ");
                    return false;
                case LoadCancellationBeforeSublotAvailability.SentSelectionMissing:
                    // Deliberately not retried against another demand: the one asked about is the one
                    // the server answers for. What IS dropped is this end's wait for it.
                    //
                    // Without that, an unanswered cancellation whose demand has left the worklist --
                    // including one left behind by an earlier stop, since a request that throws or
                    // times out clears nothing -- would match no item here for the rest of the
                    // journey, so nothing would ever be sent and nothing would ever clear it. And
                    // CanSubmitSublot is `&& !IsLoadCancellationBeforeSublotOpen`, so that would shut
                    // sublot entry at every later stop too. Before this ticket the next stop derived
                    // its own cancellationId and the server's answer cleared the entry; reading the
                    // journal first took that way out away, so it is given back here (review of
                    // onboard-hmi#135). Safe because a demand that left this stop's worklist does not
                    // come back under the same operation session -- the id a later press derives is a
                    // new one, not a second version of this one.
                    if (state.PendingLoadCancellation is { CancellationId: { } staleId })
                    {
                        await ForgetLoadCancellationRequestAsync(staleId, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    PublishOperatorResponse(
                        "RECOVERY_BLOCKED",
                        "原选择的任务已不在本站清单，取消结果以服务端为准；本机不再等待它，可以继续扫码或重新选择。 ");
                    return false;
                default:
                    throw new InvalidOperationException("RECOVERY_OPERATION_CONTEXT_MISSING");
            }
        }

        if (outcome.SubjectFromJournal
            && selectedDemandId is not null
            && !string.Equals(selectedDemandId, target.DemandId, StringComparison.Ordinal))
        {
            // The subject came from the journal, so this press is the resend of a cancellation that
            // is still waiting for its answer -- and the operator has a different row highlighted.
            // Said out loud rather than left to be inferred: otherwise they press for the row they
            // picked and get, correctly but invisibly, the earlier one.
            PublishOperatorResponse(
                "RECOVERY_VECTOR_REQUESTED",
                "本站已有一次取消在等服务端答复，这一次按下是它的重发；取消的仍是先前选中的那条任务，"
                + "不是此刻选中的这条。 ");
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
        // Read for its side effect alone: it refreshes the cached recovery state every entry gate
        // reads. The write below takes its own base from inside the journal step, so no copy of the
        // state is needed here any more (onboard-hmi#136).
        await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
        await WriteRecoveryVectorPreparedAsync(vector, cancellationToken)
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
                () => RequestLoadCancellationCoreAsync(pending.Reason, null, cancellationToken),
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
                    vector,
                    cancellationToken,
                    recoveryReason: correctionReason)
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

                // This write owns the four request fields; everything else is what the journal holds
                // when the step runs, not what this press read above (onboard-hmi#136 point 7).
                await UpdateRecoveryStateCachedAsync(
                        current => current with
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
            // The four fields the vector's context carries -- the session, the action, the operator
            // and the verification time -- are taken from it inside the write. These three it does not
            // carry, and on this branch nothing else writes them, so they are passed.
            await WriteRecoveryVectorPreparedAsync(
                    vector,
                    cancellationToken,
                    recoveryReason: actionReason,
                    recoverySessionRequestId: requestId,
                    recoveryActionRequestId: actionMessageId)
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

        // The vector check above is asked again inside the step, against the state the journal holds
        // then: the vector can be replaced between the read and the write, and clearing one this
        // rejection is not about would strand whatever replaced it (onboard-hmi#136 point 7).
        await UpdateRecoveryStateCachedAsync(
                current => current.RecoveryVector is { } rejected
                    && rejected.PrimaryId == actionId
                    ? current with
                    {
                        RecoveryVector = null,
                        ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.Prepared,
                        ActiveUnlockSlots = [],
                        CompletedSlots = [],
                        SlotResults = [],
                        RecoveryActionId = null,
                        RecoveryActionRequestId = null,
                        RecoveryResultObservedAt = null
                    }
                    : null,
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
            // Owns RecoveryResultObservedAt alone. Written from the copy above it put that copy back
            // over anything that landed since (onboard-hmi#136 point 7). The "not stamped yet" check is
            // asked again inside the step so a stamp that landed in between is not replaced -- a retry
            // has to report the first observation -- and the reported time is then that stamp, so what
            // goes on the wire is what the journal holds.
            DateTimeOffset? alreadyStamped = null;
            await UpdateRecoveryStateCachedAsync(
                    current =>
                    {
                        // Assigned unconditionally on entry, so an earlier evaluation of this same
                        // change function cannot leave a stale stamp behind (see point 6).
                        alreadyStamped = current.RecoveryResultObservedAt;
                        return alreadyStamped is null
                            ? current with { RecoveryResultObservedAt = observedAt }
                            : null;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            observedAt = alreadyStamped ?? observedAt;
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
            // The vector check above is asked again inside the step: the rejection is about the vector
            // that was on file when it was read, and clearing whatever replaced it since would strand
            // that one (onboard-hmi#136 point 7). The fields kept on the compensation path are taken
            // from the state the journal holds then, not from the copy read above.
            //
            // Scope: when the rejection names no primaryId there is nothing to compare, so a vector
            // that replaced the rejected one in the meantime would still be cleared. That window is
            // whatever elapses between this read and this write, and closing it would need the
            // rejection to identify its vector -- a protocol matter, not this one. The re-check above
            // protects the case where the rejection does name one.
            await UpdateRecoveryStateCachedAsync(
                    current => current.RecoveryVector is not { } onFile
                        || (primaryId is not null && onFile.PrimaryId != primaryId)
                        ? null
                        : current with
                        {
                            RecoveryVector = null,
                            ProvenRecoveryCheckpoint = compensation
                            ? WireToGateRecoveryCheckpoint.Prepared
                            : WireToGateRecoveryCheckpoint.ResultRecorded,
                            ActiveUnlockSlots = [],
                            CompletedSlots = [],
                            SlotResults = [],
                            UnsettledSlotOperationAttemptId = compensation
                            ? onFile.SlotOperationAttemptId
                            : null,
                            OperationContext = compensation ? current.OperationContext : null,
                            ExceptionRecoverySessionId = compensation
                            ? current.ExceptionRecoverySessionId
                            : null,
                            RecoveryActionId = null,
                            RecoveryActionRequestId = null,
                            RecoveryReason = compensation ? current.RecoveryReason : null,
                            RecoveryOperatorId = compensation ? current.RecoveryOperatorId : null,
                            RecoveryOperatorVerifiedAt = compensation
                            ? current.RecoveryOperatorVerifiedAt
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

            WireToGateRecoveryVectorContext context;
            try
            {
                context = await BindRecoveryVectorCommandAsync(
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
            }
            catch (InvalidDataException exception) when (IsBindScopeRefusal(exception.Message))
            {
                // Answered, then rethrown: the log line and the RECOVERY_BLOCKED event below are the
                // operator's account of the refusal and stay exactly as they were, with the specific
                // reason the wire cannot carry (onboard-hmi#145 (b)).
                await AnswerUnbindableCommandAsync(
                        state,
                        vectorType,
                        primaryId,
                        exceptionRecoverySessionId,
                        demandId,
                        slotOperationAttemptId,
                        handoffId,
                        slots,
                        forcedRecoveryGeneration,
                        resultKey,
                        exception.Message,
                        cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
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
                        reportRefused,
                        () => ForgetSettledRecoveryVectorAsync(context, cancellationToken))
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

    /// <summary>
    /// The three bind refusals that are answered on the wire (onboard-hmi#145 (b)).
    /// </summary>
    /// <remarks>
    /// Each of them says the same thing about the command -- it names a recovery this end did not
    /// prepare, or did not prepare this way -- and that is a thing the server's workflow is entitled to
    /// hear.
    /// <para>
    /// <b>The other three are excluded by scope, not by anything about them.</b>
    /// <c>RECOVERY_COMMAND_INVALID</c>, <c>RECOVERY_OPERATION_CONTEXT_MISSING</c> and
    /// <c>LOAD_CORRECTION_OPERATION_NOT_AVAILABLE</c> leave the server waiting in exactly the same way,
    /// and could be answered in exactly the same way: every identity the answer needs comes from the
    /// command's own parameters, and the last two happen while the prepared vector -- operator included
    /// -- is still on file. onboard-hmi#145 named three codes, so three is what this does; the three
    /// left silent are on batch 7's residual risk list. An earlier version of this comment claimed they
    /// could not be answered without inventing an identity, which is not true and would have sent the
    /// next reader looking for a technical obstacle that is not there.
    /// </para>
    /// </remarks>
    private static bool IsBindScopeRefusal(string message) => message is
        "RECOVERY_VECTOR_CONTEXT_MISSING"
        or "RECOVERY_SCOPE_MISMATCH"
        or "RECOVERY_COMMAND_HASH_MISMATCH";

    /// <summary>
    /// The one protocol reason code the three bind refusals go out under.
    /// </summary>
    /// <remarks>
    /// <c>SlotResult.reasonCodes</c> is an array of <c>ErrorCode</c>, a closed enum, and
    /// <c>RECOVERY_SCOPE_MISMATCH</c> is the only one of the three local exception messages that is in
    /// it -- <c>RECOVERY_VECTOR_CONTEXT_MISSING</c> and <c>RECOVERY_COMMAND_HASH_MISMATCH</c> have never
    /// been protocol codes. It is the right one to collapse them onto rather than a stand-in: all three
    /// say the command's recovery scope and this end's do not agree. The distinction between them is
    /// kept where it can be carried -- the log line and the <c>RECOVERY_BLOCKED</c> event.
    /// </remarks>
    private const string BindRefusedReasonCode = "RECOVERY_SCOPE_MISMATCH";

    /// <summary>
    /// Answers a recovery command that did not bind with a <c>FAILED</c> result built from the command
    /// itself, so the server's workflow stops waiting (onboard-hmi#145 (b)).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until #145 this went out as a log line and an operator event and nothing on the wire. #123 chose
    /// that deliberately: answering a command this end never prepared would put a result on record for
    /// an action the two ends disagree about. What that reading missed is that the server is not asking
    /// what happened to the slots -- it is asking whether this vehicle carried out its command -- and
    /// <c>FAILED</c> with every slot <c>NOT_STARTED</c> answers exactly that, claiming nothing else.
    /// The server closes the session on it and leaves the demand blocked for recovery
    /// (control-server#169), which is what the disagreement deserved; without it the workflow sits in
    /// <c>AwaitingResult</c> and the session in <c>EXECUTING</c> until a reconnect or a person.
    /// </para>
    /// <para>
    /// <b>Every identity comes from the command, not from this end.</b> The server matches a result to
    /// its workflow by those very fields, and a bind refusal is by definition a case where this end's
    /// differ -- so echoing the command's is what makes the answer land on the workflow that is waiting
    /// for it, and answering with this end's would be answering nothing.
    /// </para>
    /// <para>
    /// Two commands are left unanswered rather than answered wrongly. A command naming no slots has no
    /// slot results to carry, and the schema requires at least one. And the fault cargo handoff and the
    /// forced mechanical recovery must name the operator who authorized them, which exists on this end
    /// only on a prepared vector: for the one refusal that has no vector, the only operator available
    /// would be whoever happens to be at the vehicle now, and signing a refusal with them is a worse
    /// record than none. Those stay as they were -- log line, operator event, silence.
    /// </para>
    /// <para>
    /// The answer goes through the same durable send under the same key as a real result for this
    /// vector, so a command sent again finds it and is answered as a replay: one result per recovery
    /// action, with the <c>messageId</c> derived from that key, however many copies of the command
    /// arrive.
    /// </para>
    /// </remarks>
    private async Task AnswerUnbindableCommandAsync(
        WireToGateRecoveryState state,
        string vectorType,
        string primaryId,
        string? exceptionRecoverySessionId,
        string demandId,
        string? slotOperationAttemptId,
        string? handoffId,
        IReadOnlyList<int> slots,
        long? forcedRecoveryGeneration,
        string resultKey,
        string refusal,
        CancellationToken cancellationToken)
    {
        WireToGateOperatorContextPayload? operatorContext = PersistedOperatorOrNull(state);
        bool operatorRequired = vectorType is WireToGateRecoveryVectorTypes.FaultCargoHandoff
            or WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery;
        if (slots.Count == 0 || (operatorRequired && operatorContext is null))
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"未绑定的恢复命令无法回结果，只记录拒绝：type={vectorType}，id={primaryId}，"
                    + $"reason={refusal}，"
                    + (slots.Count == 0 ? "命令未指明仓位。" : "本端没有该恢复动作的操作员签名。"),
                null);
            return;
        }

        WireToGateRecoveryVectorContext answering = new(
            vectorType,
            primaryId,
            exceptionRecoverySessionId,
            demandId,
            slotOperationAttemptId,
            handoffId,
            slots.Order().ToArray(),
            CommandContentSha256: null,
            operatorContext?.OperatorId,
            operatorContext?.VerificationMethod,
            operatorContext?.VerifiedAt)
        {
            ForcedRecoveryGeneration = forcedRecoveryGeneration
        };

        // NOT_STARTED with every reading UNKNOWN, because that is what this end knows: it refused
        // before reading a single locker, let alone driving one.
        WireToGateRecoveryVectorExecutionResult refused = new(
            vectorType,
            primaryId,
            exceptionRecoverySessionId,
            demandId,
            slotOperationAttemptId,
            handoffId,
            "FAILED",
            [.. answering.Slots.Select(slot => new WireToGateSlotExecutionResult(
                slot,
                "NOT_STARTED",
                "UNKNOWN",
                "UNKNOWN",
                "UNKNOWN",
                [BindRefusedReasonCode]))],
            _clock.Now.ToUniversalTime(),
            "PREPARED");

        try
        {
            await SendRecoveryVectorResultAsync(answering, resultKey, refused, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or TimeoutException
                or InvalidOperationException
                or InvalidDataException)
        {
            // Saved before it is sent, so an answer on file goes out with the outbox on the next
            // session even though this send never heard back. One that never reached the outbox --
            // the save itself failed, or building the payload threw -- did not go anywhere and never
            // will: the two cases need different words, because "waiting for the acknowledgement"
            // tells whoever reads the log that the next session will carry it, and for the second one
            // that is false. Read back rather than inferred from the exception type, the way
            // ReportRefusedBeforeUnlockAsync does. What saves the second case is the command itself:
            // the server sends it again while it has no result, and it is refused, and answered, afresh.
            bool onFile = await _session.Journal
                .ReadOutgoingByDeduplicationKeyAsync(resultKey, cancellationToken)
                .ConfigureAwait(false) is not null;
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                onFile
                    ? $"未绑定的恢复命令的 FAILED 结果已写入发件箱，暂未收到DurableAck：type={vectorType}，"
                        + $"id={primaryId}，reason={refusal}。"
                    : $"未绑定的恢复命令的 FAILED 结果未能写入发件箱，不会补发：type={vectorType}，"
                        + $"id={primaryId}，reason={refusal}。",
                exception);

            // No RESULT_ACK_PENDING here, unlike the sibling: this path rethrows into the handler that
            // publishes RECOVERY_BLOCKED, and telling the operator "the result is saved, waiting for
            // the server" beside "the command was blocked" is two answers to one question. Whether the
            // answer reached the outbox is the log's to say, and it now says it.
        }
    }

    /// <summary>
    /// The operator recorded on the prepared vector, or <c>null</c> when there is none to speak for.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="RequirePersistedOperator"/> this never throws: its caller answers a command
    /// that may name no vector at all, and "there is nobody on file" is one of the answers it has to
    /// act on rather than an error.
    /// </remarks>
    private static WireToGateOperatorContextPayload? PersistedOperatorOrNull(
        WireToGateRecoveryState state) =>
        state.RecoveryVector is
        {
            OperatorId: { } operatorId,
            OperatorVerificationMethod: { } method,
            OperatorVerifiedAt: { } verifiedAt
        }
            ? new(operatorId, method, verifiedAt)
            : null;

    /// <summary>
    /// Forgets a vector whose non-<c>COMPLETED</c> result the server has acknowledged, and the
    /// recovery session it belonged to, keeping the unsettled operation and everything the vector's
    /// IO left behind (onboard-hmi#145).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server closes the session on a <c>FAILED</c> or an <c>UNKNOWN</c> result exactly as it does
    /// on a completed one (control-server#169). Nothing on this end did: the two fields that refuse
    /// every later recovery entry -- the prepared vector and the session's identity -- were cleared
    /// only by <see cref="CompleteRecoveryVectorStateAsync"/>, on <c>COMPLETED</c>. So the vehicle sat
    /// in <c>RECOVERY_SESSION_STATE_PENDING</c> with the server no longer listening, and the only way
    /// on was to clear the journal by hand.
    /// </para>
    /// <para>
    /// Neither clearing already here fits. <see cref="SettleRecoveryVectorStateAsync"/> also drops the
    /// attempt and its operation context, which is right for a vector that finished and wrong here:
    /// the load is still unsettled -- that is what <c>FAILED</c> and <c>UNKNOWN</c> say -- and dropping
    /// its context would refuse the next recovery with <c>RECOVERY_OPERATION_CONTEXT_MISSING</c>
    /// instead. <see cref="ForgetRefusedVector"/> is guarded on the vector having done nothing, which
    /// is the opposite of this case and is why the CLOSED fallback leaves these behind.
    /// </para>
    /// <para>
    /// What the vector's IO left -- the active unlock set, the completed slots, the slot results and
    /// the proven checkpoint -- stays. A door this vector may have left open is what the next session's
    /// handshake reports from <c>ActiveUnlockSlots</c>, so clearing it would be telling the server no
    /// door is open; the next prepared vector resets all four anyway.
    /// </para>
    /// <para>
    /// Guard and write are one journal update, never a read then a write: a second result for the same
    /// vector, or the session's CLOSED snapshot, may be doing the same thing at the same moment, and
    /// whichever lands second reads a journal whose vector no longer matches and writes nothing. The
    /// session fields go only while the journal still names this vector's session, so one that has
    /// moved on to another session keeps its own.
    /// </para>
    /// <para>
    /// <b>The order on disk is: result into the outbox, sent, its <c>DurableAck</c> recorded against
    /// the outbox row, then this.</b> Two writes, and the pair is not atomic. Answering the server
    /// comes first on purpose: a record cleared before the answer is on file would leave the next copy
    /// of the command refused with <c>RECOVERY_VECTOR_CONTEXT_MISSING</c> and nothing to rebuild the
    /// answer from. What the order leaves open is the window between the two -- acknowledged, not yet
    /// forgotten -- where a crash leaves a settled result with the record still on file. That window
    /// exists on the <c>COMPLETED</c> path in exactly the same shape and has since long before this,
    /// so closing it belongs to onboard-hmi#150, which closes it on both paths at once. Closing it here
    /// for one path only would leave the two behaving differently for no stated reason.
    /// </para>
    /// </remarks>
    private async Task ForgetSettledRecoveryVectorAsync(
        WireToGateRecoveryVectorContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await _session.Journal.UpdateRecoveryStateAsync(
                    state => state.RecoveryVector is not { } vector
                        || !string.Equals(vector.VectorType, context.VectorType, StringComparison.Ordinal)
                        || !string.Equals(vector.PrimaryId, context.PrimaryId, StringComparison.Ordinal)
                            ? null
                            : ForgetSettledVector(state, context),
                    CacheRecoveryState,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"恢复向量结果已收到服务端确认，但清除向量与恢复会话记录失败：type={context.VectorType}，"
                    + $"id={context.PrimaryId}。",
                exception);
        }
    }

    /// <summary>
    /// <paramref name="state"/> without the vector, and without the recovery session when the journal
    /// still names the one <paramref name="context"/> belonged to.
    /// </summary>
    /// <remarks>
    /// A load correction carries no session at all, so the session fields are not its to clear; every
    /// other vector's are cleared under the same guard <see cref="ForgetRecoverySessionAsync"/> applies,
    /// which is what keeps one vector's settlement from forgetting another session's record.
    /// </remarks>
    private static WireToGateRecoveryState ForgetSettledVector(
        WireToGateRecoveryState state,
        WireToGateRecoveryVectorContext context)
    {
        WireToGateRecoveryState cleared = state with
        {
            RecoveryVector = null,
            RecoveryResultObservedAt = null
        };
        return context.ExceptionRecoverySessionId is { } session
            && string.Equals(state.ExceptionRecoverySessionId, session, StringComparison.Ordinal)
                ? cleared with
                {
                    ExceptionRecoverySessionId = null,
                    RecoveryActionId = null,
                    RecoverySessionRequestId = null,
                    RecoveryActionRequestId = null,
                    RecoveryReason = null,
                    RecoveryOperatorId = null,
                    RecoveryOperatorVerifiedAt = null
                }
                : cleared;
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
            // The generation is compared against the one the journal holds when the step runs, not
            // against the copy read above: the fence only ever rises, and a concurrent forced recovery
            // that raised it higher must not be brought back down by this write
            // (onboard-hmi#136 point 7).
            await UpdateRecoveryStateCachedAsync(
                    current => current with
                    {
                        ForcedRecoveryGeneration =
                            forcedRecoveryGeneration is { } authorizedGeneration
                                && authorizedGeneration > current.ForcedRecoveryGeneration
                                    ? authorizedGeneration
                                    : current.ForcedRecoveryGeneration,
                        RecoveryVector = context
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return context;
    }

    /// <param name="releaseSettledFailure">
    /// Run once a non-<c>COMPLETED</c> result has been acknowledged, to forget the vector the way the
    /// <c>COMPLETED</c> branch below forgets its own (onboard-hmi#145 (a)). Only the server's command
    /// path passes it: the load cancellation the operator starts keeps its vector on purpose, because
    /// pressing the entry again re-executes that very vector rather than preparing a new one.
    /// </param>
    private async Task<bool> ExecuteRecoveryVectorAndReportAsync(
        WireToGateRecoveryVectorContext context,
        bool correction,
        CancellationToken cancellationToken,
        Func<WireToGateRecoveryVectorExecutionResult, Task>? sendResult = null,
        Func<Task>? reportRefusedBeforeUnlock = null,
        Func<Task>? releaseSettledFailure = null)
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

        // Forgotten before the operator is told, so the entry the message sends them to is already
        // pressable when they read it. A write that fails is logged and nothing more: the answer is
        // on file either way, and the operator event is owed whatever the journal did.
        if (releaseSettledFailure is not null)
        {
            await releaseSettledFailure().ConfigureAwait(false);
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

        // Both checks above are asked again inside the step, against what the journal holds then
        // (onboard-hmi#136 point 7). The isolation one especially: "never replace an isolation that is
        // still standing" is a claim about the journal at the moment of the write, and one raised
        // between the read and the write is exactly the case it exists for.
        bool vectorChanged = false;
        bool isolationStands = false;
        WireToGateRecoveryState? settled = await UpdateRecoveryStateCachedAsync(
                journalled =>
                {
                    // Reset on entry, for the reason given in WireToGateBusinessService's point 6: a
                    // change function may be evaluated more than once for the same step, and a flag
                    // left set by an earlier evaluation would throw over a successful write.
                    vectorChanged = false;
                    isolationStands = false;
                    if (journalled.RecoveryVector is not { } onFile
                        || onFile.VectorType != context.VectorType
                        || onFile.PrimaryId != context.PrimaryId)
                    {
                        vectorChanged = true;
                        return null;
                    }

                    if (isolation is not null
                        && journalled.ForcedIsolation is { } standing
                        && standing.RecoveryActionId != isolation.RecoveryActionId)
                    {
                        isolationStands = true;
                        return null;
                    }

                    return journalled with
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
                        ForcedIsolation = isolation ?? journalled.ForcedIsolation
                    };
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (settled is null)
        {
            // Those two flags are the only ways the change function returns null; a third reason code
            // here could never be emitted, and an unemittable code gets investigated as a real fault.
            throw isolationStands ? new InvalidDataException("HARDWARE_RECOVERY_RECORD_REQUIRED")
                : vectorChanged ? new InvalidDataException("RECOVERY_STATE_MISMATCH")
                : new UnreachableException();
        }
    }

    /// <param name="handedOverOpenSlots">
    /// The doors an aborted load may have left open, handed to a load cancellation as the prepared
    /// vector's active unlock set (<c>WireToGateRecoveryVectorExecutor.HandedOverOpenSlots</c>).
    /// </param>
    /// <param name="clearPendingLoadCancellation">
    /// Clears the operator's unanswered load cancellation as part of this write: the aborted load's
    /// cancellation is the very request this vector answers, so it is settled by preparing it. Callers
    /// used to express this by handing in a modified copy of the state.
    /// </param>
    /// <param name="recoveryReason">
    /// The reason to record with the vector, where this vector brings its own; <c>null</c> keeps the
    /// one the journal holds. Also a copy-modifying caller before onboard-hmi#136.
    /// </param>
    /// <param name="recoverySessionRequestId">
    /// The id of the session request this vector was authorized under, and
    /// <paramref name="recoveryActionRequestId"/> the message id of the action that asked for it.
    /// Both <c>null</c> keep what the journal holds.
    /// </param>
    /// <remarks>
    /// The three ids and the reason are parameters rather than fields taken from
    /// <paramref name="context"/> because the vector's context does not carry them, and on the
    /// active-session branch of a recovery action request nothing else writes them: the press reuses
    /// the session the server already opened, so the write that would have recorded them
    /// (<c>RequestRecoveryActionVectorCoreAsync</c>'s no-session branch) never runs. They used to
    /// arrive as part of a modified copy of the state the caller handed in; that copy is gone with
    /// onboard-hmi#136, and leaving them out cost the operator the retry --
    /// <c>RECOVERY_SESSION_REQUEST_MISSING</c> on the second press, with no way back.
    /// </remarks>
    private async Task WriteRecoveryVectorPreparedAsync(
        WireToGateRecoveryVectorContext context,
        CancellationToken cancellationToken,
        IReadOnlyList<int>? handedOverOpenSlots = null,
        bool clearPendingLoadCancellation = false,
        string? recoveryReason = null,
        string? recoverySessionRequestId = null,
        string? recoveryActionRequestId = null)
    {
        // Merged against what the journal holds when the step runs. The caller used to hand in the
        // state it had read and this write put that copy back whole, undoing whatever landed in
        // between -- an executor checkpoint, a released session, a pending result
        // (onboard-hmi#136 point 7). The three fields the vector's context fills fall back to the
        // journal's own values, not to that copy's.
        await UpdateRecoveryStateCachedAsync(
                current => current with
                {
                    UnsettledSlotOperationAttemptId = context.SlotOperationAttemptId,
                    ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.Prepared,
                    ActiveUnlockSlots = handedOverOpenSlots?.ToArray() ?? [],
                    CompletedSlots = [],
                    SlotResults = [],
                    RecoveryVector = context,
                    ExceptionRecoverySessionId = context.ExceptionRecoverySessionId
                        ?? current.ExceptionRecoverySessionId,
                    RecoveryActionId = context.VectorType is
                        WireToGateRecoveryVectorTypes.LoadCompensation
                        or WireToGateRecoveryVectorTypes.FaultCargoHandoff
                        or WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery
                        ? context.PrimaryId
                        : current.RecoveryActionId,
                    RecoveryOperatorId = context.OperatorId ?? current.RecoveryOperatorId,
                    RecoveryOperatorVerifiedAt = context.OperatorVerifiedAt
                        ?? current.RecoveryOperatorVerifiedAt,
                    RecoveryResultObservedAt = null,
                    RecoveryReason = recoveryReason ?? current.RecoveryReason,
                    RecoverySessionRequestId = recoverySessionRequestId
                        ?? current.RecoverySessionRequestId,
                    RecoveryActionRequestId = recoveryActionRequestId
                        ?? current.RecoveryActionRequestId,
                    PendingLoadCancellation = clearPendingLoadCancellation
                        ? null
                        : current.PendingLoadCancellation
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

    /// <summary>Test seam: one <see cref="UpdateRecoveryStateCachedAsync"/>.</summary>
    internal Task<WireToGateRecoveryState?> UpdateCachedRecoveryStateForTestAsync(
        Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
        CancellationToken cancellationToken) =>
        UpdateRecoveryStateCachedAsync(change, cancellationToken);

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

    /// <summary>
    /// Applies <paramref name="change"/> to the recovery state and caches the result, inside the
    /// journal step (onboard-hmi#129). Returns what was written, or <c>null</c> when
    /// <paramref name="change"/> wrote nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It takes a change function and not a state on purpose</b> (onboard-hmi#136). Its predecessor
    /// <c>WriteRecoveryStateCachedAsync</c> took a whole record and handed the journal
    /// <c>_ =&gt; state</c>: the atomic method with the old semantics, throwing away the newest state it
    /// exists to hand over. Its twelve callers each read the state, decided outside the lock and wrote
    /// the whole record back, so whatever landed in between was silently undone -- a checkpoint the
    /// executor wrote, a recovery session released by a CLOSED snapshot, a pending result recorded by a
    /// late acknowledgement. Nothing threw and nothing logged.
    /// </para>
    /// <para>
    /// A caller writes only the fields it owns and takes every other field from the state it is handed.
    /// <c>RecoveryStateWriteFunnelArchitectureTests</c> holds that line.
    /// </para>
    /// </remarks>
    private async Task<WireToGateRecoveryState?> UpdateRecoveryStateCachedAsync(
        Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
        CancellationToken cancellationToken) =>
        await _session.Journal
            .UpdateRecoveryStateAsync(change, CacheRecoveryState, cancellationToken)
            .ConfigureAwait(false);

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
        // Merged under the journal's lock, which is what the read-again-right-before-the-write above
        // was reaching for: the load's executor may write its own checkpoints while this press runs,
        // and a whole-value write put the older copy back over them. Narrowing the gap made that
        // unlikely; writing inside the step makes it impossible (onboard-hmi#136 point 7).
        await UpdateRecoveryStateCachedAsync(
                current => current with { PendingLoadCancellation = pending },
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
            // Asked again inside the step: a second press can journal its own cancellation between
            // the read and the write, and clearing that one would lose a request already sent
            // (onboard-hmi#136 point 7).
            await UpdateRecoveryStateCachedAsync(
                    current => string.Equals(
                        current.PendingLoadCancellation?.CancellationId,
                        cancellationId,
                        StringComparison.Ordinal)
                        ? current with { PendingLoadCancellation = null }
                        : null,
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

    /// <remarks>
    /// Forwards rather than releasing here, because giving a claim up is also what pays an owed recovery entry
    /// (onboard-hmi#156), and a vector execution is the longest-held claim there is -- it has the doors for as
    /// long as the compensation takes. A debt recorded while one runs and not paid when it ends would wait for
    /// the next slot operation, on a vehicle that is in recovery precisely because there may not be one.
    /// </remarks>
    private void ReleaseOperation(string key) => ReleaseInFlightAttempt(key);

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
