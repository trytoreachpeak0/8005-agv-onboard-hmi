using System.IO;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The operator's 「出发」 at a staging pickup stop (protocol 4.0.0 candidate, CP-0012, 8005-agv-onboard-hmi#349): journal the
/// press, send <c>CurrentStopDepartureRequested</c>, and when <c>CurrentStopDepartureResult</c> comes back journal it and
/// acknowledge it. The departure itself travels in the server's snapshots.
/// </summary>
/// <remarks>
/// <para>
/// <b>The vehicle never departs on its own</b> (<c>NEVER_DEPART_LOCALLY</c>). Nothing here moves the vehicle, opens a door or
/// touches the IO: the press is a request, and what happens next -- the loading phase closing with <c>OPERATOR_DEPARTED</c>,
/// the departure safety check, or the journey closing in place with nothing loaded -- is read from the snapshots as sent
/// (<c>DISPLAY_DEPARTURE_FROM_SNAPSHOTS</c>).
/// </para>
/// <para>
/// <b>One press, one id, for good.</b> The press is journaled in <see cref="WireToGateRecoveryState.CurrentStopDeparture"/>
/// before anything is sent (<c>JOURNAL_DEPARTURE_REQUEST_BEFORE_SENDING</c>), and the outbox row is put on file even when the
/// link is down. A reconnect replays that row under the same messageId; a restart between the journal and the outbox sends it
/// from the journal (<see cref="ResumePendingCurrentStopDepartureAsync"/>). No new id is ever minted for the same press
/// (<c>REUSE_PENDING_DEPARTURE_REQUEST_ID</c>), and a new press is not offered while one waits for its result.
/// </para>
/// <para>
/// <b>Offered only at a staging pickup stop</b> (<c>OFFER_DEPART_ONLY_AT_STAGING_PICKUP_STOP</c>,
/// <see cref="WireToGateStagingPickupStop"/>), and not while a slot operation, an entry or a correction or cancellation is
/// open (<c>NEVER_OFFER_DEPART_DURING_SLOT_OPERATION_OR_CORRECTION</c>). The server checks all of it again and refuses in its
/// own order; this end's checks only spare the operator a refusal they could have seen coming.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string CurrentStopDepartureResultType = "CurrentStopDepartureResult";

    /// <summary>One result at a time, so a replay arriving while the first copy is still being journaled finds it on file.</summary>
    private readonly SemaphoreSlim _currentStopDepartureResultGate = new(1, 1);

    /// <summary>1 from the press until it is journaled and handed to the session, so a second tap cannot mint a second id.</summary>
    private int _currentStopDepartureSending;

    /// <summary>
    /// The operation session of the sublot this vehicle submitted and the server has not answered yet: the entry is "being
    /// processed" (8005-agv-onboard-hmi#349 item 1). Set once a submission goes out; cleared by the load command or the
    /// rejection that answers it, by the stop ending, and by the session leaving Ready. In memory only: after a restart the
    /// server has answered or resends, and it refuses a departure while a slot operation runs anyway.
    /// </summary>
    private string? _sublotAwaitingAnswer;

    /// <summary>1 while the loop that resends an unacknowledged press runs (review M1); at most one runs.</summary>
    private int _currentStopDepartureResendLoopRunning;

    /// <summary>
    /// 1 while an automatic send of the press on file is under way -- the resend loop's, or the one after a restart. Its own
    /// flag, not the press's <see cref="_currentStopDepartureSending"/>: an automatic send changes nothing that belongs to a
    /// press (the lesson of 8005-agv-onboard-hmi#236 and #239, where automatic paths recorded a press or refreshed its window).
    /// A press cannot overlap it anyway, since the entry is shut while a press is pending.
    /// </summary>
    private int _currentStopDepartureResending;

    /// <summary>
    /// How long the resend loop waits between looks at a press whose request has not been acknowledged (review M1). Not a
    /// protocol timer: a resend goes out under the same messageId, and the server takes a repeat as the same message.
    /// </summary>
    internal TimeSpan CurrentStopDepartureResendInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The 「出发」 entry as it stands now.</summary>
    public WireToGateCurrentStopDepartureView CurrentStopDeparture => ReadCurrentStopDeparture();

    /// <summary>
    /// Sends the departure the operator confirmed. <paramref name="shown"/> is the prompt the confirmation was read from; it
    /// goes out only while it is still the current one -- a worklist that changed under the dialog makes it a different
    /// prompt, and the press is refused rather than sent for what the screen turned into.
    /// </summary>
    /// <returns>Whether the press is on file: sent, or journaled and waiting for the link to come back.</returns>
    public async Task<bool> RequestCurrentStopDepartureAsync(
        WireToGateCurrentStopDeparturePrompt shown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _currentStopDepartureSending, 1, 0) != 0)
        {
            PublishOperatorResponse("CURRENT_STOP_DEPARTURE_BLOCKED", "上一次出发请求正在发送，本次未重复发送。");
            return false;
        }

        WireToGateCurrentStopDeparture? journaled = null;
        try
        {
            WireToGateCurrentStopDepartureView view = ReadCurrentStopDeparture(ignoreSending: true);
            if (view.Prompt is null)
            {
                string why = WireToGateCurrentStopDepartureText.UnavailableText(view.Unavailability);
                PublishOperatorResponse(
                    "CURRENT_STOP_DEPARTURE_BLOCKED",
                    $"现在不能出发，本次未发送：{(why.Length > 0 ? why : WireToGateCurrentStopDepartureText.StatusText(view.Last))}。");
                return false;
            }

            if (view.Prompt != shown)
            {
                PublishOperatorResponse(
                    "CURRENT_STOP_DEPARTURE_BLOCKED",
                    "确认期间屏上的任务清单变了，这次出发没有发送。请核对屏上当前内容，仍需出发时重新按「出发」。");
                return false;
            }

            WireToGateCurrentStopDeparture departure = new(
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"),
                shown.StationId,
                shown.WorklistRevision,
                shown.OperationSessionId,
                shown.OperatorId,
                "SESSION",
                _clock.Now.ToUniversalTime());
            WireToGateRecoveryState? written = await UpdateRecoveryStateCachedAsync(
                state => state.CurrentStopDeparture is { Result: null }
                    ? null
                    : state with { CurrentStopDeparture = departure },
                cancellationToken).ConfigureAwait(false);
            if (written?.CurrentStopDeparture != departure)
            {
                PublishOperatorResponse("CURRENT_STOP_DEPARTURE_BLOCKED", WireToGateCurrentStopDepartureText.PendingText + "，本次未重复发送。");
                return false;
            }

            journaled = departure;
            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"操作员按出发：departureRequestId={departure.DepartureRequestId}，messageId={departure.MessageId}，"
                + $"station={departure.StationId}，worklistRevision={departure.WorklistRevision}，"
                + $"operationSession={departure.OperationSessionId ?? "null"}，unloadedItems={shown.UnloadedItemCount}，"
                + $"operator={departure.OperatorId}。");
            switch (await SendCurrentStopDepartureOnceAsync(departure, cancellationToken).ConfigureAwait(false))
            {
                case DepartureSend.Acknowledged:
                    PublishOperatorResponse("CURRENT_STOP_DEPARTURE_SENT", WireToGateCurrentStopDepartureText.PendingText + "。");
                    return true;
                case DepartureSend.Refused:
                    // Settled as refused and shown; the press is over.
                    return false;
                case DepartureSend.NotAcknowledged:
                    EnsureCurrentStopDepartureResendLoop();
                    PublishOperatorResponse("CURRENT_STOP_DEPARTURE_PENDING", WireToGateCurrentStopDepartureText.NotAcknowledgedText);
                    return true;
                default:
                    EnsureCurrentStopDepartureResendLoop();
                    PublishOperatorResponse("CURRENT_STOP_DEPARTURE_PENDING", WireToGateCurrentStopDepartureText.NotSentText);
                    return true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Every failure, not a list of them: escaping from here reaches the window's async void handler and latches
            // UNHANDLED_UI_ERROR for a press that changed nothing (the lesson of 8005-agv-onboard-hmi#221 review S3).
            bool expected = exception is IOException
                or TimeoutException
                or InvalidOperationException
                or InvalidDataException;
            _logger.Write(
                expected ? LogSeverity.Warning : LogSeverity.Error,
                nameof(WireToGateBusinessService),
                $"出发请求未发送完：departureRequestId={journaled?.DepartureRequestId ?? "未生成"}，"
                + $"{exception.GetType().Name}：{exception.Message}。",
                exception);
            if (journaled is null)
            {
                PublishOperatorResponse("CURRENT_STOP_DEPARTURE_BLOCKED", "出发请求没有发出，车辆状态未变。请稍后重试。");
                return false;
            }

            // On file in the journal: the resend loop and the next session send it under the same id, and the screen keeps
            // saying it is pending until the result comes back.
            EnsureCurrentStopDepartureResendLoop();
            PublishOperatorResponse("CURRENT_STOP_DEPARTURE_PENDING", WireToGateCurrentStopDepartureText.NotSentText);
            return true;
        }
        finally
        {
            Volatile.Write(ref _currentStopDepartureSending, 0);
        }
    }

    private WireToGateCurrentStopDepartureView ReadCurrentStopDeparture(bool ignoreSending = false)
    {
        WireToGateJourneySnapshot journey = _session.CurrentJourney;
        WireToGateRecoveryState state = Volatile.Read(ref _lastRecoveryState);
        if (!WireToGateStagingPickupStop.Is(journey, state.StagingPickupStationIds ?? []))
        {
            return WireToGateCurrentStopDepartureView.Hidden;
        }

        WireToGateCurrentStopWorklist worklist = journey.CurrentStopWorklist!;
        // A pending press is shown wherever the vehicle is; a settled one only at the stop it was for (review M2).
        WireToGateCurrentStopDeparture? last = state.CurrentStopDeparture is { } departure
            && (departure.Result is null || WireToGateCurrentStopDepartureGate.BelongsToStop(departure, worklist))
                ? departure
                : null;
        WireToGateCurrentStopDepartureUnavailability unavailability = WireToGateCurrentStopDepartureGate.Judge(
            ReadCurrentStopDepartureFacts(
                state,
                ignoreSending,
                acceptedAtThisStop: last?.Result?.Outcome == WireToGateCurrentStopDepartureOutcome.Accepted));
        if (unavailability != WireToGateCurrentStopDepartureUnavailability.None
            || Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable) is not { } operatorId)
        {
            return new(true, null, unavailability, last);
        }

        return new(
            true,
            new WireToGateCurrentStopDeparturePrompt(
                worklist.StationId,
                worklist.Revision,
                worklist.OperationSessionId,
                worklist.Items.Count,
                operatorId.Trim()),
            WireToGateCurrentStopDepartureUnavailability.None,
            last);
    }

    /// <summary>
    /// The facts <see cref="WireToGateCurrentStopDepartureGate.Judge"/> decides on, read now (review M3: the judgement lives in
    /// that function alone, where each branch has its own test).
    /// </summary>
    private WireToGateCurrentStopDepartureFacts ReadCurrentStopDepartureFacts(
        WireToGateRecoveryState state,
        bool ignoreSending,
        bool acceptedAtThisStop)
    {
        WireToGateSessionSnapshot session = _session.Current;
        bool running;
        lock (_operationAttemptGate)
        {
            running = _operationAttempts.Count > 0;
        }

        return new WireToGateCurrentStopDepartureFacts(
            SafetyFaultLatched: _fatalFaultLatched(),
            SessionReady: session.Connected && session.Readiness == WireToGateSessionReadiness.Ready,
            OperatorSignedIn: !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable)),
            DepartureAwaitingResult: state.CurrentStopDeparture is { Result: null }
                || !ignoreSending && Volatile.Read(ref _currentStopDepartureSending) != 0,
            AcceptedAtThisStop: acceptedAtThisStop,
            EntryInProgress: Volatile.Read(ref _sublotAwaitingAnswer) is not null,
            SlotOperationRunning: running,
            SlotOperationUnsettled: state.UnsettledSlotOperationAttemptId is not null,
            ResultsAwaitingAcknowledgement: state.PendingResults.Count > 0,
            RecoveryVectorOpen: state.RecoveryVector is not null,
            LoadCancellationOpen: state.PendingLoadCancellation is not null || IsLoadCancellationBeforeSublotOpen);
    }

    private enum DepartureSend
    {
        /// <summary>The server holds the request; its result comes on its own.</summary>
        Acknowledged,

        /// <summary>The server answered the request itself with a refusal; it is settled as refused.</summary>
        Refused,

        /// <summary>Written to the link, and no acknowledgement came back in time.</summary>
        NotAcknowledged,

        /// <summary>Not written to the link -- no session to carry it, or the link went -- but on file to go out later.</summary>
        NotSent
    }

    /// <summary>
    /// Sends the press on file once, under its own id, and says how it went. A <c>ProtocolProblem</c> answering the request --
    /// whichever reason, given up for good or not -- settles the press as refused with that code (review M1): left pending, it
    /// would hold the button shut with nothing ever answering it.
    /// </summary>
    private async Task<DepartureSend> SendCurrentStopDepartureOnceAsync(
        WireToGateCurrentStopDeparture departure,
        CancellationToken cancellationToken)
    {
        try
        {
            await _session.SendCurrentStopDepartureRequestedAsync(departure, cancellationToken).ConfigureAwait(false);
            return DepartureSend.Acknowledged;
        }
        catch (WireToGateRequestNotAcceptedException refusal)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"服务端以 ProtocolProblem 回绝出发请求：departureRequestId={departure.DepartureRequestId}，"
                + $"messageId={departure.MessageId}，reason={refusal.ReasonCode}。");
            await SettleCurrentStopDepartureLocallyAsync(
                departure.MessageId,
                string.Empty,
                refusal.ReasonCode,
                cancellationToken).ConfigureAwait(false);
            return DepartureSend.Refused;
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
                $"出发请求这一次没有得到确认：departureRequestId={departure.DepartureRequestId}，"
                + $"{exception.GetType().Name}：{exception.Message}。",
                exception);
            return exception is TimeoutException ? DepartureSend.NotAcknowledged : DepartureSend.NotSent;
        }
    }

    /// <summary>
    /// Starts the loop that resends a press nothing has acknowledged (review M1), unless one runs. The handshake does not replay
    /// a departure request and the pass over stale rows only sends rows of an older generation, so a request whose
    /// acknowledgement was lost while the link stayed up had nothing to send it again until the next disconnect.
    /// </summary>
    private void EnsureCurrentStopDepartureResendLoop()
    {
        if (_disposed || Interlocked.CompareExchange(ref _currentStopDepartureResendLoopRunning, 1, 0) != 0)
        {
            return;
        }

        TrackTask(RunCurrentStopDepartureResendLoopAsync(_stopping.Token));
    }

    private async Task RunCurrentStopDepartureResendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(CurrentStopDepartureResendInterval, cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref _lastRecoveryState).CurrentStopDeparture is not { Result: null } pending)
                {
                    return;
                }

                WireToGateSessionSnapshot session = _session.Current;
                if (!session.Connected
                    || session.Readiness != WireToGateSessionReadiness.Ready
                    || Interlocked.CompareExchange(ref _currentStopDepartureResending, 1, 0) != 0)
                {
                    continue;
                }

                try
                {
                    WireToGateDurableMessage? row = await _session.Journal
                        .ReadOutgoingByDeduplicationKeyAsync(
                            WireToGateSessionClient.CurrentStopDepartureKey(pending.DepartureRequestId),
                            cancellationToken)
                        .ConfigureAwait(false);
                    // Acknowledged: the server holds it, and its result -- or its replay of it -- is the server's to send.
                    // Given up: the abandonment has settled it.
                    if (row is { Acknowledged: true } or { Abandoned: true })
                    {
                        return;
                    }

                    // A row of an older generation is the pass over stale rows' to send once the session is up; sending it
                    // from here too would put a second copy on the wire beside that pass's. Only this generation's row --
                    // the link stayed up and the acknowledgement was lost -- has no other sender.
                    if (row is not null
                        && session.SessionGeneration is long generation
                        && WireToGateProtocolSerializer
                            .DeserializeAndValidate(row.WireLine.TrimEnd('\r', '\n'), _session.Client.AgvId)
                            .SessionGeneration is long rowGeneration
                        && rowGeneration < generation)
                    {
                        continue;
                    }

                    _logger.Write(
                        LogSeverity.Information,
                        nameof(WireToGateBusinessService),
                        $"重发未得到确认的出发请求：departureRequestId={pending.DepartureRequestId}，messageId={pending.MessageId}。");
                    if (await SendCurrentStopDepartureOnceAsync(pending, cancellationToken).ConfigureAwait(false)
                        is DepartureSend.Acknowledged or DepartureSend.Refused)
                    {
                        return;
                    }
                }
                finally
                {
                    Volatile.Write(ref _currentStopDepartureResending, 0);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"出发请求的重发循环停止，下次会话就绪时再起：{exception.GetType().Name}：{exception.Message}。",
                exception);
        }
        finally
        {
            Volatile.Write(ref _currentStopDepartureResendLoopRunning, 0);
        }
    }

    /// <summary>
    /// Journals the server's answer, then acknowledges it (<c>durableBeforeAck</c>). A replay of an answer already on file
    /// -- the server resending because the acknowledgement was lost -- is acknowledged again and changes nothing
    /// (<c>ADOPT_REPLAYED_DEPARTURE_RESULT</c>).
    /// </summary>
    private async Task HandleCurrentStopDepartureResultAsync(
        WireToGateCurrentStopDepartureResult result,
        CancellationToken cancellationToken)
    {
        await _currentStopDepartureResultGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DepartureResultDisposition disposition = DepartureResultDisposition.Unknown;
            WireToGateCurrentStopDepartureOutcome outcome = new(
                result.MessageId,
                result.ContentSha256,
                result.Outcome,
                result.ReasonCode,
                result.CurrentWorklistRevision,
                _clock.Now.ToUniversalTime());
            await UpdateRecoveryStateCachedAsync(
                state =>
                {
                    // Reset on entry: a change function may be evaluated more than once for one step.
                    disposition = DepartureResultDisposition.Unknown;
                    if (state.CurrentStopDeparture is not { } departure
                        || !string.Equals(departure.DepartureRequestId, result.DepartureRequestId, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    if (departure.Result is { } existing)
                    {
                        disposition = SameDecision(existing, outcome)
                            ? DepartureResultDisposition.Replay
                            : DepartureResultDisposition.Conflict;
                        return null;
                    }

                    disposition = DepartureResultDisposition.New;
                    return state with { CurrentStopDeparture = departure with { Result = outcome } };
                },
                cancellationToken).ConfigureAwait(false);

            if (disposition == DepartureResultDisposition.Conflict)
            {
                // The server's contradiction: one request, two decisions. The first stays; this one is neither applied nor
                // acknowledged, so it is not taken as held.
                _logger.Write(
                    LogSeverity.Error,
                    nameof(WireToGateBusinessService),
                    $"同一出发请求收到内容不同的结果，未采用、未确认：departureRequestId={result.DepartureRequestId}，"
                    + $"messageId={result.MessageId}，outcome={result.Outcome}，reason={result.ReasonCode ?? "null"}，"
                    + $"currentWorklistRevision={result.CurrentWorklistRevision}。");
                return;
            }

            _logger.Write(
                disposition == DepartureResultDisposition.Unknown ? LogSeverity.Warning : LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"收到出发结果：departureRequestId={result.DepartureRequestId}，messageId={result.MessageId}，"
                + $"outcome={result.Outcome}，reason={result.ReasonCode ?? "null"}，"
                + $"currentWorklistRevision={result.CurrentWorklistRevision}，disposition={disposition}。");
            await _session
                .SendDurableAckAsync(result.MessageId, CurrentStopDepartureResultType, result.ContentSha256, cancellationToken)
                .ConfigureAwait(false);
            if (disposition == DepartureResultDisposition.New)
            {
                PublishOperatorResponse(
                    result.Outcome == WireToGateCurrentStopDepartureOutcome.Accepted
                        ? "CURRENT_STOP_DEPARTURE_ACCEPTED"
                        : "CURRENT_STOP_DEPARTURE_REJECTED",
                    WireToGateCurrentStopDepartureText.StatusText(
                        Volatile.Read(ref _lastRecoveryState).CurrentStopDeparture));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
        {
            // Not acknowledged: the server sends it again, and that copy is journaled or recognised and acknowledged.
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"出发结果未能记下或确认，等服务端重发：departureRequestId={result.DepartureRequestId}，"
                + $"messageId={result.MessageId}，{exception.GetType().Name}：{exception.Message}。",
                exception);
        }
        finally
        {
            _currentStopDepartureResultGate.Release();
        }
    }

    /// <summary>
    /// The same decision, judged on what the server decided rather than on the envelope: a replay is sent again under a newer
    /// session generation and a new <c>sentAt</c>, so its hash differs while its answer does not.
    /// </summary>
    private static bool SameDecision(WireToGateCurrentStopDepartureOutcome first, WireToGateCurrentStopDepartureOutcome again) =>
        string.Equals(first.Outcome, again.Outcome, StringComparison.Ordinal)
        && string.Equals(first.ReasonCode, again.ReasonCode, StringComparison.Ordinal)
        && first.CurrentWorklistRevision == again.CurrentWorklistRevision;

    private enum DepartureResultDisposition
    {
        /// <summary>No press on file has this id: an older press already settled, or a journal that was reset.</summary>
        Unknown,
        New,
        Replay,
        Conflict
    }

    /// <summary>
    /// After a restart, or a session that came back: a press journaled but never put in the outbox -- the process went away in
    /// between -- is sent from the journal under its own id. One that is in the outbox is the handshake's to replay.
    /// </summary>
    private async Task ResumePendingCurrentStopDepartureAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _lastRecoveryState).CurrentStopDeparture is not { Result: null }
            || Interlocked.CompareExchange(ref _currentStopDepartureResending, 1, 0) != 0)
        {
            return;
        }

        try
        {
            WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
            if (state.CurrentStopDeparture is not { Result: null } pending)
            {
                return;
            }

            if (await _session.Journal
                    .ReadOutgoingByDeduplicationKeyAsync(
                        WireToGateSessionClient.CurrentStopDepartureKey(pending.DepartureRequestId),
                        cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                _logger.Write(
                    LogSeverity.Information,
                    nameof(WireToGateBusinessService),
                    $"补发日志簿里未进发件箱的出发请求：departureRequestId={pending.DepartureRequestId}，messageId={pending.MessageId}。");
                if (await SendCurrentStopDepartureOnceAsync(pending, cancellationToken).ConfigureAwait(false)
                    is DepartureSend.Acknowledged or DepartureSend.Refused)
                {
                    return;
                }
            }

            // In the outbox and unacknowledged: an older generation's row goes out with the pass over stale rows; one of this
            // generation -- the session came back without a reconnect -- has only this loop (review M1).
            EnsureCurrentStopDepartureResendLoop();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"补发出发请求未完成，下次会话就绪时再试：{exception.GetType().Name}：{exception.Message}。",
                exception);
        }
        finally
        {
            Volatile.Write(ref _currentStopDepartureResending, 0);
        }
    }

    /// <summary>
    /// A departure request the server refused for good (<c>MANUAL_REVIEW</c>, such as <c>BUSINESS_ID_CONTENT_CONFLICT</c>) and
    /// the outbox gave up: settled as refused, so the screen stops saying it is pending and the operator can press again.
    /// </summary>
    private Task SettleAbandonedCurrentStopDepartureAsync(
        WireToGateDurableMessageAbandonment abandoned,
        CancellationToken cancellationToken) =>
        SettleCurrentStopDepartureLocallyAsync(
            abandoned.MessageId,
            abandoned.ContentSha256,
            abandoned.ReasonCode,
            cancellationToken);

    /// <summary>
    /// Settles the pending press sent as <paramref name="messageId"/> as refused with <paramref name="reasonCode"/>, when the
    /// server refused the request itself rather than answering it with a result: given up for good, or answered with a
    /// <c>ProtocolProblem</c> (review M1). The screen stops saying it is pending, shows the code as it came, and the operator
    /// can press again. <paramref name="contentSha256"/> is the request's own hash when the outbox gave one, otherwise empty:
    /// no result message exists to name.
    /// </summary>
    private async Task SettleCurrentStopDepartureLocallyAsync(
        string messageId,
        string contentSha256,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        try
        {
            bool settled = false;
            await UpdateRecoveryStateCachedAsync(
                state =>
                {
                    settled = false;
                    if (state.CurrentStopDeparture is not { Result: null } pending
                        || !string.Equals(pending.MessageId, messageId, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    settled = true;
                    return state with
                    {
                        CurrentStopDeparture = pending with
                        {
                            Result = new WireToGateCurrentStopDepartureOutcome(
                                messageId,
                                contentSha256,
                                WireToGateCurrentStopDepartureOutcome.Rejected,
                                reasonCode,
                                pending.WorklistRevision,
                                _clock.Now.ToUniversalTime())
                        }
                    };
                },
                cancellationToken).ConfigureAwait(false);
            if (settled)
            {
                PublishOperatorResponse(
                    "CURRENT_STOP_DEPARTURE_REJECTED",
                    WireToGateCurrentStopDepartureText.RejectionText(reasonCode));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"被服务端回绝的出发请求未能记为已拒绝：messageId={messageId}，reason={reasonCode}，{exception.Message}。",
                exception);
        }
    }

    /// <summary>
    /// Remembers the station of a staging pickup worklist, so the cargo-holding wait there is still known for one after a
    /// restart (<see cref="WireToGateRecoveryState.StagingPickupStationIds"/>). Not awaited by the journey handler: every
    /// failure is logged here, and the next staging worklist tries again.
    /// </summary>
    private async Task RememberStagingPickupStationAsync(WireToGateJourneySnapshot journey)
    {
        if (!WireToGateStagingPickupStop.IsStagingPickupWorklist(journey.CurrentStopWorklist)
            || Volatile.Read(ref _lastRecoveryState).StagingPickupStationIds?.Contains(
                journey.CurrentStopWorklist!.StationId,
                StringComparer.Ordinal) == true)
        {
            return;
        }

        try
        {
            await UpdateRecoveryStateCachedAsync(
                state =>
                {
                    WireToGateRecoveryState next = state.WithStagingPickupStationOf(journey);
                    return ReferenceEquals(next, state) ? null : next;
                },
                _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"未能记下待送点站号：station={journey.CurrentStopWorklist?.StationId}，{exception.Message}。",
                exception);
        }
    }

    /// <summary>Clears the "entry being processed" mark once its stop has ended or moved on.</summary>
    private void ForgetSublotAwaitingAnswerOnceTheStopEnds(WireToGateJourneySnapshot journey)
    {
        if (Volatile.Read(ref _sublotAwaitingAnswer) is not { } operationSessionId)
        {
            return;
        }

        if (journey.CurrentStopWorklist is not { } worklist
            || worklist.Items.Count == 0
            || !string.Equals(worklist.OperationSessionId, operationSessionId, StringComparison.Ordinal))
        {
            Interlocked.CompareExchange(ref _sublotAwaitingAnswer, null, operationSessionId);
        }
    }
}
