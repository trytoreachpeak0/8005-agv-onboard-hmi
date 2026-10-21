using System.IO;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf;

/// <summary>
/// 「剩下的不装（已装 k/N 篮）」 on a multi-basket load (8005-agv-onboard-hmi#388, CP-0021 REQ-0367, protocol 6.0.0 candidate):
/// offer it while the executor offers it, hold the executor on the press, journal the request, send
/// <c>RemainingBasketsSkipRequested</c>, and act on the server's <c>RemainingBasketsSkipResult</c> -- end the load on
/// <c>AUTHORIZED</c>, go on loading on <c>REJECTED</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The vehicle never ends a load short on its own</b> (<c>NEVER_SKIP_LOCALLY</c>). The press holds the executor -- no
/// slot opens, 「重新开门」 is refused -- and nothing else happens until the server answers. Only an <c>AUTHORIZED</c> result
/// lets the executor report the skip.
/// </para>
/// <para>
/// <b>One press, one <c>skipRequestId</c>, journaled before it is sent.</b> A transport retry inside one session generation
/// sends the very same line; after a reconnect or a restart the request is resubmitted under a new messageId with the same
/// id and payload, until the server answers or the request is dropped. A new press is offered only once this one has
/// ended, and is a new request.
/// </para>
/// <para>
/// <b>When a pending request is dropped</b> (wire notes): a <c>ProtocolProblem</c> answers it; the run of its attempt ends
/// any other way -- an UNKNOWN, a fatal-fault latch, a slot fault declaration, a load cancellation, every slot loaded -- so an
/// <c>OperationResult</c> of the attempt is written. An <c>AUTHORIZED</c> that comes after is journaled, acknowledged and not
/// acted on. After a restart with the request pending, the interrupted settlement of its attempt waits for the answer: the
/// request is resubmitted, and the settlement runs once the answer is on file, as a skip on <c>AUTHORIZED</c> when the slots
/// still read so.
/// </para>
/// <para>
/// <b>Not offered</b> while an exception recovery session, a recovery vector, a load cancellation or a slot fault declaration
/// is on file, under a fatal-fault latch, or while the session cannot carry the request. No operator id is required (the
/// user's decision of 2026-10-21); one known at the press is sent.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string RemainingBasketsSkipResultType = "RemainingBasketsSkipResult";

    /// <summary>Serialises the press, the results and the drops, so each reads the record the last one left.</summary>
    private readonly SemaphoreSlim _remainingBasketsSkipGate = new(1, 1);

    /// <summary>1 from the press until its request is journaled and sent once: a second tap mints nothing.</summary>
    private int _remainingBasketsSkipPressing;

    /// <summary>1 while the loop that resends an unacknowledged request runs; at most one runs.</summary>
    private int _remainingBasketsSkipResendLoopRunning;

    /// <summary>The request and the session generation its <c>DurableAck</c> came back in, so a reconnect resubmits it.</summary>
    private RemainingBasketsSkipAcknowledgement? _remainingBasketsSkipAcknowledged;

    private sealed record RemainingBasketsSkipAcknowledgement(string SkipRequestId, long? SessionGeneration);

    /// <summary>How long the resend loop waits between looks at a request nothing has acknowledged.</summary>
    internal TimeSpan RemainingBasketsSkipResendInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>What 「剩下的不装」 would be pressed over now, or <c>null</c> when it is not offered.</summary>
    public WireToGateRemainingBasketsSkipOffer? RemainingBasketsSkipOffer => OfferedRemainingBasketsSkip();

    /// <summary>
    /// The state for the screen-state snapshot: <c>OFFERED:k/N</c> while the button is offered, <c>PENDING:k/N</c> while a
    /// press waits for the server, otherwise empty.
    /// </summary>
    public string RemainingBasketsSkipStatus
    {
        get
        {
            if (Volatile.Read(ref _lastRecoveryState).RemainingBasketsSkip is { IsPending: true } pending)
            {
                return $"PENDING:{pending.LoadedSlots.Count}/{pending.ExpectedBasketCount}";
            }

            return OfferedRemainingBasketsSkip() is { } offer
                ? $"OFFERED:{offer.LoadedSlots.Count}/{offer.ExpectedBasketCount}"
                : string.Empty;
        }
    }

    /// <summary>
    /// The operator confirmed 「剩下的不装」 over <paramref name="shown"/>, the offer the dialog was built from. It goes out
    /// only while that is still the offer: a slot loaded or a door opened under the dialog makes it another one.
    /// </summary>
    /// <returns>Whether the request is on file: sent, or journaled and waiting for the link.</returns>
    public async Task<bool> RequestRemainingBasketsSkipAsync(
        WireToGateRemainingBasketsSkipOffer shown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _remainingBasketsSkipPressing, 1, 0) != 0)
        {
            PublishOperatorResponse("REMAINING_BASKETS_SKIP_BLOCKED", WireToGateRemainingBasketsSkipText.PendingText);
            return false;
        }

        string? held = null;
        WireToGateRemainingBasketsSkip? journaled = null;
        try
        {
            if (OfferedRemainingBasketsSkip() is not { } offer || !offer.Equals(shown))
            {
                PublishOperatorResponse("REMAINING_BASKETS_SKIP_BLOCKED", WireToGateRemainingBasketsSkipText.ChangedText);
                return false;
            }

            string skipRequestId = Guid.NewGuid().ToString("D");
            if (!_executor.HoldForRemainingBasketsSkip(offer.SlotOperationAttemptId, skipRequestId, offer.LoadedSlots))
            {
                PublishOperatorResponse("REMAINING_BASKETS_SKIP_BLOCKED", WireToGateRemainingBasketsSkipText.ChangedText);
                return false;
            }

            held = skipRequestId;
            WireToGateRemainingBasketsSkip skip = new(
                skipRequestId,
                offer.DemandId,
                offer.OperationSessionId,
                offer.SlotOperationAttemptId,
                offer.LoadedSlots,
                offer.ExpectedBasketCount,
                OperatorAtSkipPress(),
                _clock.Now.ToUniversalTime());
            await _remainingBasketsSkipGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                WireToGateRecoveryState? written = await UpdateRecoveryStateCachedAsync(
                    state => string.Equals(
                            state.UnsettledSlotOperationAttemptId,
                            skip.SlotOperationAttemptId,
                            StringComparison.Ordinal)
                        && state.RemainingBasketsSkip is not { IsPending: true }
                            ? state with { RemainingBasketsSkip = skip }
                            : null,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(written?.RemainingBasketsSkip?.SkipRequestId, skipRequestId, StringComparison.Ordinal))
                {
                    PublishOperatorResponse("REMAINING_BASKETS_SKIP_BLOCKED", WireToGateRemainingBasketsSkipText.ChangedText);
                    return false;
                }
            }
            finally
            {
                _remainingBasketsSkipGate.Release();
            }

            journaled = skip;
            held = null;
            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"操作员按「剩下的不装」：skipRequestId={skip.SkipRequestId}，attempt={skip.SlotOperationAttemptId}，"
                + $"demand={skip.DemandId}，已装={string.Join(',', skip.LoadedSlots)}（{skip.LoadedSlots.Count}/{skip.ExpectedBasketCount}），"
                + $"operator={skip.Operator?.OperatorId ?? "null"}。");
            PublishOperatorResponse("REMAINING_BASKETS_SKIP_REQUESTED", WireToGateRemainingBasketsSkipText.PendingText);
            await SendRemainingBasketsSkipOnceAsync(skip, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Every failure: escaping from here reaches the window's async void handler (8005-agv-onboard-hmi#221 review S3).
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"「剩下的不装」未发送完：skipRequestId={journaled?.SkipRequestId ?? held ?? "未生成"}，"
                + $"{exception.GetType().Name}：{exception.Message}。",
                exception);
            if (journaled is null)
            {
                PublishOperatorResponse("REMAINING_BASKETS_SKIP_BLOCKED", "「剩下的不装」没有发出，仓门状态未变。请稍后重试。");
                return false;
            }

            EnsureRemainingBasketsSkipResendLoop();
            return true;
        }
        finally
        {
            // A hold with no request journaled behind it is let go here, so the run is never left waiting for nothing.
            if (held is not null)
            {
                _executor.DecideRemainingBasketsSkip(shown.SlotOperationAttemptId, held, null);
            }

            Volatile.Write(ref _remainingBasketsSkipPressing, 0);
        }
    }

    private WireToGateRemainingBasketsSkipOffer? OfferedRemainingBasketsSkip()
    {
        if (_executor.RemainingBasketsSkipOffer is not { } offer)
        {
            return null;
        }

        WireToGateRecoveryState state = Volatile.Read(ref _lastRecoveryState);
        WireToGateSessionSnapshot session = _session.Current;
        bool manualRecovery = state.ExceptionRecoverySessionId is not null
            || state.RecoveryVector is not null
            || state.SlotFaultDeclaration is not null;
        bool cancellationOpen = state.PendingLoadCancellation is not null || IsLoadCancellationBeforeSublotOpen;
        return manualRecovery
            || cancellationOpen
            || state.RemainingBasketsSkip is { IsPending: true }
            || _fatalFaultLatched()
            || !session.Connected
            || session.Readiness is not (WireToGateSessionReadiness.Ready or WireToGateSessionReadiness.RecoveryRequired)
                ? null
                : offer;
    }

    private WireToGateRemainingBasketsSkipOperator? OperatorAtSkipPress() =>
        Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable) is { } operatorId
        && !string.IsNullOrWhiteSpace(operatorId)
            ? new WireToGateRemainingBasketsSkipOperator(operatorId.Trim(), "SESSION", _clock.Now.ToUniversalTime())
            : null;

    private static RemainingBasketsSkipRequestedPayload SkipRequestPayload(WireToGateRemainingBasketsSkip skip) =>
        new(
            skip.SkipRequestId,
            skip.DemandId,
            skip.OperationSessionId,
            skip.SlotOperationAttemptId,
            [.. skip.LoadedSlots.Order()],
            skip.Operator is { } person
                ? new WireToGateOperatorContextPayload(person.OperatorId, person.VerificationMethod, person.VerifiedAt)
                : null);

    private enum RemainingBasketsSkipSend
    {
        Acknowledged,
        Refused,
        NotAcknowledged
    }

    /// <summary>
    /// Sends the pending request once and says how it went: acknowledged; refused by a <c>ProtocolProblem</c>, which drops it;
    /// or not acknowledged, which leaves it to the resend loop and the next session.
    /// </summary>
    private async Task<RemainingBasketsSkipSend> SendRemainingBasketsSkipOnceAsync(
        WireToGateRemainingBasketsSkip skip,
        CancellationToken cancellationToken)
    {
        long? generation = _session.Current.SessionGeneration;
        try
        {
            string messageId = await _session
                .SendRemainingBasketsSkipRequestedAsync(SkipRequestPayload(skip), cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(
                ref _remainingBasketsSkipAcknowledged,
                new RemainingBasketsSkipAcknowledgement(skip.SkipRequestId, generation));
            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"「剩下的不装」已被服务端收下：skipRequestId={skip.SkipRequestId}，messageId={messageId}，generation={generation}。");
            return RemainingBasketsSkipSend.Acknowledged;
        }
        catch (WireToGateRequestNotAcceptedException refusal)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"服务端以 ProtocolProblem 回绝「剩下的不装」：skipRequestId={skip.SkipRequestId}，reason={refusal.ReasonCode}。");
            await DropRemainingBasketsSkipAsync(skip.SkipRequestId, refusal.ReasonCode, cancellationToken)
                .ConfigureAwait(false);
            return RemainingBasketsSkipSend.Refused;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"「剩下的不装」这一次没有得到确认：skipRequestId={skip.SkipRequestId}，{exception.GetType().Name}：{exception.Message}。",
                exception);
            EnsureRemainingBasketsSkipResendLoop();
            return RemainingBasketsSkipSend.NotAcknowledged;
        }
    }

    /// <summary>
    /// Sends the pending request when the session is up and it has not been acknowledged in this session generation: after a
    /// reconnect (a new generation, so a new messageId) or a restart. Called on every readiness and by the resend loop.
    /// </summary>
    private async Task ResubmitPendingRemainingBasketsSkipAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _lastRecoveryState).RemainingBasketsSkip is not { IsPending: true } pending)
        {
            return;
        }

        WireToGateSessionSnapshot session = _session.Current;
        if (!session.Connected
            || session.Readiness is not (WireToGateSessionReadiness.Ready or WireToGateSessionReadiness.RecoveryRequired)
            || Volatile.Read(ref _remainingBasketsSkipAcknowledged) is { } acknowledged
                && string.Equals(acknowledged.SkipRequestId, pending.SkipRequestId, StringComparison.Ordinal)
                && acknowledged.SessionGeneration == session.SessionGeneration)
        {
            return;
        }

        try
        {
            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"重发未得到答复的「剩下的不装」：skipRequestId={pending.SkipRequestId}，generation={session.SessionGeneration}。");
            await SendRemainingBasketsSkipOnceAsync(pending, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void EnsureRemainingBasketsSkipResendLoop()
    {
        if (_disposed || Interlocked.CompareExchange(ref _remainingBasketsSkipResendLoopRunning, 1, 0) != 0)
        {
            return;
        }

        TrackTask(RunRemainingBasketsSkipResendLoopAsync(_stopping.Token));
    }

    private async Task RunRemainingBasketsSkipResendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RemainingBasketsSkipResendInterval, cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref _lastRecoveryState).RemainingBasketsSkip is not { IsPending: true })
                {
                    return;
                }

                await ResubmitPendingRemainingBasketsSkipAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref _remainingBasketsSkipResendLoopRunning, 0);
        }
    }

    /// <summary>
    /// Ends the pending request <paramref name="skipRequestId"/> without effect, for <paramref name="reasonCode"/>: journaled,
    /// the executor's hold let go, and the operator told. Nothing happens when it is no longer pending.
    /// </summary>
    private async Task DropRemainingBasketsSkipAsync(
        string skipRequestId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        string? attemptId = null;
        await _remainingBasketsSkipGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UpdateRecoveryStateCachedAsync(
                state =>
                {
                    attemptId = null;
                    if (state.RemainingBasketsSkip is not { IsPending: true } pending
                        || !string.Equals(pending.SkipRequestId, skipRequestId, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    attemptId = pending.SlotOperationAttemptId;
                    return state with { RemainingBasketsSkip = pending with { DroppedReason = reasonCode } };
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _remainingBasketsSkipGate.Release();
        }

        if (attemptId is null)
        {
            return;
        }

        if (!_executor.DecideRemainingBasketsSkip(attemptId, skipRequestId, null))
        {
            // No run holds it: after a restart the interrupted settlement waited for this request to end, and with it dropped
            // that settlement runs now, as on a rejection -- not only at the next session readiness (review M1 of PR #406).
            TrackTask(RestorePendingRecoveryOperationProjectionAsync(_stopping.Token));
        }

        _logger.Write(
            LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"「剩下的不装」未生效、已放弃：skipRequestId={skipRequestId}，attempt={attemptId}，reason={reasonCode}。");
        PublishOperatorResponse("REMAINING_BASKETS_SKIP_DROPPED", WireToGateRemainingBasketsSkipText.DroppedText(reasonCode));
    }

    /// <summary>
    /// The run of <paramref name="attemptId"/> ended with <paramref name="overallOutcome"/>: unless that is the skip itself, a
    /// request still pending for the attempt is dropped -- the operation ended another way and its result is being written.
    /// </summary>
    private async Task ConcludeRemainingBasketsSkipOnRunEndAsync(
        string attemptId,
        string? overallOutcome,
        CancellationToken cancellationToken)
    {
        if (overallOutcome == WireToGateSlotOperationExecutor.RemainingBasketsSkippedOutcome
            || Volatile.Read(ref _lastRecoveryState).RemainingBasketsSkip is not { IsPending: true } pending
            || !string.Equals(pending.SlotOperationAttemptId, attemptId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await DropRemainingBasketsSkipAsync(
                pending.SkipRequestId,
                overallOutcome is null ? "SLOT_OPERATION_TAKEN_OVER" : $"SLOT_OPERATION_{overallOutcome}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"操作结束时未能记下「剩下的不装」已放弃：skipRequestId={pending.SkipRequestId}，{exception.Message}。",
                exception);
        }
    }

    /// <summary>
    /// After a restart, the journal's unsettled attempt with a request still pending: the request is resubmitted and the
    /// settlement waits for its answer. True when it does, which the interrupted settlement takes as taken over.
    /// </summary>
    private async Task<bool> HoldInterruptedSettlementForRemainingBasketsSkipAsync(
        string attemptId,
        CancellationToken cancellationToken)
    {
        // The cached copy the restore has just refreshed, not another journal read: several G2 tests count the restore's reads
        // (the reason TakenOverSlotOperationAttemptId is read the same way above).
        if (Volatile.Read(ref _lastRecoveryState).RemainingBasketsSkip is not { IsPending: true } pending
            || !string.Equals(pending.SlotOperationAttemptId, attemptId, StringComparison.Ordinal))
        {
            return false;
        }

        PublishOperatorEvent(
            $"remaining-baskets-skip-resubmitted:{pending.SkipRequestId}",
            "REMAINING_BASKETS_SKIP_REQUESTED",
            WireToGateRemainingBasketsSkipText.PendingText);
        await ResubmitPendingRemainingBasketsSkipAsync(cancellationToken).ConfigureAwait(false);
        // Answered or dropped while it went out -- a ProtocolProblem comes back on the send itself: the settlement runs now,
        // in this pass. The answer's own restore met this pass's claim and did nothing (review M1 of PR #406).
        if (Volatile.Read(ref _lastRecoveryState).RemainingBasketsSkip is not { IsPending: true } still
            || !string.Equals(still.SkipRequestId, pending.SkipRequestId, StringComparison.Ordinal))
        {
            return false;
        }

        Volatile.Write(ref _settlementHeldForRemainingBasketsSkip, attemptId);
        EnsureRemainingBasketsSkipResendLoop();
        return true;
    }

    /// <summary>The attempt whose interrupted settlement waits for its 「剩下的不装」 to end, set while that pass holds its claim.</summary>
    private string? _settlementHeldForRemainingBasketsSkip;

    /// <summary>
    /// Called once the settlement's claim on <paramref name="attemptId"/> is released: when the request it waited for ended in
    /// the moment between the pass's last look and the release, the restore that ending asked for met the claim and did
    /// nothing, so it is asked for again here.
    /// </summary>
    private void ResumeSettlementHeldForRemainingBasketsSkip(string attemptId)
    {
        if (Interlocked.CompareExchange(ref _settlementHeldForRemainingBasketsSkip, null, attemptId) == attemptId
            && Volatile.Read(ref _lastRecoveryState).RemainingBasketsSkip is not { IsPending: true })
        {
            TrackTask(RestorePendingRecoveryOperationProjectionAsync(_stopping.Token));
        }
    }

    /// <summary>
    /// Journals the server's answer, acknowledges it (<c>durableBeforeAck</c>), and acts on it once: the executor ends the
    /// load on <c>AUTHORIZED</c> and goes on on <c>REJECTED</c>. A replay of an answer on file is acknowledged and changes
    /// nothing (<c>ADOPT_REPLAYED_SKIP_RESULT</c>); an answer to a request already dropped is journaled, acknowledged and not
    /// acted on; one that contradicts the answer on file is neither applied nor acknowledged.
    /// </summary>
    private async Task HandleRemainingBasketsSkipResultAsync(
        WireToGateRemainingBasketsSkipResult result,
        CancellationToken cancellationToken)
    {
        SkipResultDisposition disposition = SkipResultDisposition.Unknown;
        WireToGateRemainingBasketsSkip? onFile = null;
        await _remainingBasketsSkipGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WireToGateRemainingBasketsSkipOutcome outcome = new(
                result.MessageId,
                result.ContentSha256,
                result.Outcome,
                result.LoadedSlots,
                result.ReasonCode,
                _clock.Now.ToUniversalTime());
            await UpdateRecoveryStateCachedAsync(
                state =>
                {
                    disposition = SkipResultDisposition.Unknown;
                    onFile = null;
                    if (state.RemainingBasketsSkip is not { } skip
                        || !string.Equals(skip.SkipRequestId, result.SkipRequestId, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    onFile = skip;
                    if (skip.Result is { } existing)
                    {
                        disposition = SameSkipDecision(existing, outcome)
                            ? SkipResultDisposition.Replay
                            : SkipResultDisposition.Conflict;
                        return null;
                    }

                    if (!string.Equals(skip.DemandId, result.DemandId, StringComparison.Ordinal)
                        || !string.Equals(skip.SlotOperationAttemptId, result.SlotOperationAttemptId, StringComparison.Ordinal))
                    {
                        disposition = SkipResultDisposition.Conflict;
                        return null;
                    }

                    bool slotsAsAsked = result.Outcome != WireToGateRemainingBasketsSkipOutcome.Authorized
                        || skip.LoadedSlots.Order().SequenceEqual(result.LoadedSlots.Order());
                    disposition = skip.DroppedReason is not null
                        ? SkipResultDisposition.Late
                        : slotsAsAsked ? SkipResultDisposition.New : SkipResultDisposition.OtherSlots;
                    onFile = skip with
                    {
                        Result = outcome,
                        DroppedReason = slotsAsAsked ? skip.DroppedReason : "SLOT_SET_INVALID"
                    };
                    return state with { RemainingBasketsSkip = onFile };
                },
                cancellationToken).ConfigureAwait(false);

            if (disposition == SkipResultDisposition.Conflict)
            {
                _logger.Write(
                    LogSeverity.Error,
                    nameof(WireToGateBusinessService),
                    $"「剩下的不装」收到与记录不符的结果，未采用、未确认：skipRequestId={result.SkipRequestId}，"
                    + $"messageId={result.MessageId}，outcome={result.Outcome}，slots={string.Join(',', result.LoadedSlots)}，"
                    + $"reason={result.ReasonCode ?? "null"}。");
                return;
            }

            _logger.Write(
                disposition is SkipResultDisposition.New or SkipResultDisposition.Replay
                    ? LogSeverity.Information
                    : LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"收到「剩下的不装」结果：skipRequestId={result.SkipRequestId}，messageId={result.MessageId}，"
                + $"outcome={result.Outcome}，slots={string.Join(',', result.LoadedSlots)}，reason={result.ReasonCode ?? "null"}，"
                + $"disposition={disposition}"
                + (disposition == SkipResultDisposition.Late ? $"，申请已于此前放弃（{onFile?.DroppedReason}），不执行" : string.Empty)
                + "。");
            await _session
                .SendDurableAckAsync(result.MessageId, RemainingBasketsSkipResultType, result.ContentSha256, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
        {
            // Not acknowledged: the server sends it again, and that copy is recognised and acknowledged.
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"「剩下的不装」结果未能记下或确认，等服务端重发：skipRequestId={result.SkipRequestId}，"
                + $"{exception.GetType().Name}：{exception.Message}。",
                exception);
        }
        finally
        {
            _remainingBasketsSkipGate.Release();
        }

        if (onFile is null)
        {
            return;
        }

        switch (disposition)
        {
            case SkipResultDisposition.New when result.Outcome == WireToGateRemainingBasketsSkipOutcome.Authorized:
                PublishOperatorResponse(
                    "REMAINING_BASKETS_SKIP_AUTHORIZED",
                    WireToGateRemainingBasketsSkipText.AuthorizedText(result.LoadedSlots.Count));
                if (!_executor.DecideRemainingBasketsSkip(onFile.SlotOperationAttemptId, onFile.SkipRequestId, result.LoadedSlots))
                {
                    // No run holds it: the process restarted with the request pending, and the interrupted settlement
                    // waited for this answer. It runs now, and reports the skip if the slots still read so.
                    TrackTask(RestorePendingRecoveryOperationProjectionAsync(_stopping.Token));
                }

                break;
            case SkipResultDisposition.New:
                PublishOperatorResponse(
                    "REMAINING_BASKETS_SKIP_REJECTED",
                    WireToGateRemainingBasketsSkipText.RejectionText(result.ReasonCode ?? "ACTION_NOT_ALLOWED_IN_STATE"));
                if (!_executor.DecideRemainingBasketsSkip(onFile.SlotOperationAttemptId, onFile.SkipRequestId, null))
                {
                    TrackTask(RestorePendingRecoveryOperationProjectionAsync(_stopping.Token));
                }

                break;
            case SkipResultDisposition.OtherSlots:
                _executor.DecideRemainingBasketsSkip(onFile.SlotOperationAttemptId, onFile.SkipRequestId, null);
                PublishOperatorResponse(
                    "REMAINING_BASKETS_SKIP_DROPPED",
                    WireToGateRemainingBasketsSkipText.DroppedText("SLOT_SET_INVALID"));
                TrackTask(RestorePendingRecoveryOperationProjectionAsync(_stopping.Token));
                break;
        }
    }

    /// <summary>
    /// A result the operation finished on: <c>COMPLETED</c>, or a load ended on an authorized skip, which is a normal
    /// delivery of the baskets that are in (CP-0021) -- recorded, shown and acknowledged as completed work.
    /// </summary>
    private static bool IsCompletedOutcome(string? overallOutcome) =>
        overallOutcome is "COMPLETED" or WireToGateSlotOperationExecutor.RemainingBasketsSkippedOutcome;

    private static bool SameSkipDecision(
        WireToGateRemainingBasketsSkipOutcome first,
        WireToGateRemainingBasketsSkipOutcome again) =>
        string.Equals(first.Outcome, again.Outcome, StringComparison.Ordinal)
        && string.Equals(first.ReasonCode, again.ReasonCode, StringComparison.Ordinal)
        && first.LoadedSlots.Order().SequenceEqual(again.LoadedSlots.Order());

    private enum SkipResultDisposition
    {
        /// <summary>No request on file has this id: an older press, or one whose attempt has settled.</summary>
        Unknown,
        New,
        Replay,
        Conflict,

        /// <summary>The request was dropped before this answer came: journaled and not acted on.</summary>
        Late,

        /// <summary>An authorization naming other slots than the request did: journaled, not acted on, the request dropped.</summary>
        OtherSlots
    }
}
