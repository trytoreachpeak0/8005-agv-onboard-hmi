using System.IO;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The operator's entry for <c>CV-MANUAL-STATION-CLEARANCE</c> (batch 9-16, <c>8005-agv-onboard-hmi#221</c>;
/// <c>REQ-0179</c>): while the control server says the vehicle is clearing a charger, a verified maintainer
/// confirms that the vehicle has been moved to a safe place and the charger is empty.
/// </summary>
/// <remarks>
/// <para>
/// <b>It sends a request and shows the answer, and that is all</b> (<c>NEVER_RELEASE_STATION_LOCALLY</c>).
/// Whatever the server decides, the journey projection, the recovery state and every slot stay as they were;
/// whether the vehicle takes work again and whether the charger is free are read from the business state and
/// the plan the server sends afterwards. This file reaches no IO, no door and no recovery state, which is what
/// lets the entry stay open under a fatal-fault latch (<c>REQ-0180</c>: a manual clearance neither restores
/// nor blocks the vehicle); <c>WireToGateStationClearanceTests</c> holds it to that.
/// </para>
/// <para>
/// <b>Three conditions, all read from what the server sent or from the maintainer check 「充电后返回服务」
/// makes.</b> The business state's <c>activePurpose</c> is <c>CLEARING_MAINTENANCE</c>; the maintenance switch
/// is on, the session can carry a request, an operator id and the administrator proof are configured
/// (<c>CanUseRecoveryOperator(requireProof: true)</c> -- the proof is checked to be configured and not sent,
/// the message has no field for it, and no role is judged here: the server decides who may confirm from the
/// operator id); and the plan names exactly one charger
/// (<see cref="WireToGateStationClearanceStation.Resolve"/>).
/// </para>
/// <para>
/// <b>The press is bound to what the operator read.</b> The dialog is built from a
/// <see cref="WireToGateStationClearancePrompt"/> and the press hands that prompt back. It is sent only if it is
/// still the current one; a plan that named another charger meanwhile, or a result that arrived meanwhile, is a
/// different prompt and the press is refused. Nothing here remembers that a warning was given: a restart starts
/// from no prompt at all, so the operator is asked again and nothing goes out on its own.
/// </para>
/// <para>
/// <b>An unanswered confirmation keeps its id.</b> A wait that ran out or a session that dropped leaves the
/// result unknown and nothing is resent. When the operator presses again for the same charger, the same
/// payload goes out again -- the same <c>confirmationRequestId</c>, the message's business dedup key -- under a
/// new messageId, because the server's inbox refuses a repeated messageId whose line differs in so much as
/// <c>sentAt</c>. An answer ends that -- a result of either kind, or a <c>ProtocolProblem</c> correlated to the
/// request, which says the server read it and did not take it: the next press is a new confirmation. The request is kept
/// in memory only (the release manifest gives the message <c>durableBeforeSend: false</c>); a restart forgets
/// it, and the press after a restart carries a new id.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string StationClearanceConfirmed = "CONFIRMED";

    private readonly object _stationClearanceGate = new();

    /// <summary>The request that went out, or may have, and has no answer yet. Guarded by the gate.</summary>
    private ManualStationClearanceConfirmationRequestedPayload? _stationClearanceUnanswered;

    /// <summary>What the last confirmation came to. Guarded by the gate.</summary>
    private WireToGateStationClearanceOutcome? _stationClearanceOutcome;

    /// <summary>1 while a confirmation is waiting for its answer: a second press sends nothing.</summary>
    private int _stationClearanceAwaitingAnswer;

    public WireToGateStationClearanceView StationClearance =>
        ReadStationClearance(Volatile.Read(ref _stationClearanceAwaitingAnswer) != 0);

    public bool CanConfirmStationClearance => StationClearance.Prompt is not null;

    /// <param name="shown">The prompt the operator confirmed, as the view handed it out.</param>
    public async Task<bool> ConfirmStationClearanceAsync(
        WireToGateStationClearancePrompt shown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _stationClearanceAwaitingAnswer, 1, 0) != 0)
        {
            PublishOperatorResponse(
                "STATION_CLEARANCE_BLOCKED",
                "上一次清桩确认还在等待服务端应答，本次未发送。");
            return false;
        }

        StationClearancePress press;
        try
        {
            press = await SendStationClearanceAsync(shown, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _stationClearanceAwaitingAnswer, 0);
        }

        // Published once the entry is on offer again, so a view that refreshes on this event reads the settled state.
        if (press.Kind is not null)
        {
            PublishOperatorResponse(press.Kind, press.Message);
        }

        return press.Confirmed;
    }

    private sealed record StationClearancePress(bool Confirmed, string? Kind, string Message)
    {
        public static StationClearancePress Blocked(string message) =>
            new(false, "STATION_CLEARANCE_BLOCKED", message);
    }

    private async Task<StationClearancePress> SendStationClearanceAsync(
        WireToGateStationClearancePrompt shown,
        CancellationToken cancellationToken)
    {
        ManualStationClearanceConfirmationRequestedPayload? request = null;
        bool firstSend = false;
        try
        {
            lock (_stationClearanceGate)
            {
                WireToGateStationClearanceView view = ReadStationClearance(awaitingAnswer: false);
                if (view.Prompt is null)
                {
                    return StationClearancePress.Blocked(DescribeUnavailableStationClearance(view));
                }

                if (view.Prompt != shown)
                {
                    return StationClearancePress.Blocked(
                        "清桩确认的内容在确认期间变了（原充电桩、确认人，或上一次提交已有结果），本次未发送，请按当前显示的内容重新确认。");
                }

                // The prompt says which it is, and it was just checked against this state: a resubmission is
                // the unanswered request as it went out the first time, anything else a new confirmation.
                firstSend = shown.ResubmittedConfirmationRequestId is null;
                request = firstSend
                    ? new ManualStationClearanceConfirmationRequestedPayload(
                        Guid.NewGuid().ToString("D"),
                        shown.StationId,
                        // The enumeration has no value for a charger; the property is required, so it goes out null.
                        null,
                        "STATION_EMPTY",
                        new WireToGateOperatorContextPayload(
                            shown.OperatorId,
                            GetProtocolVerificationMethod(),
                            _clock.Now.ToUniversalTime()),
                        _clock.Now.ToUniversalTime())
                    : _stationClearanceUnanswered!;
                _stationClearanceUnanswered = request;
            }

            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"提交人工清桩确认：confirmationRequestId={request.ConfirmationRequestId}，station={request.StationId}，"
                + $"operator={request.Operator.OperatorId}，resubmission={!firstSend}。");
            ManualStationClearanceConfirmationResultPayload result = await _session
                .ConfirmManualStationClearanceAsync(
                    // A resubmission is a new message about the same confirmation.
                    firstSend ? request.ConfirmationRequestId : Guid.NewGuid().ToString("D"),
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            WireToGateStationClearanceOutcome? settled = SettleStationClearance(result);
            return settled is null
                ? new StationClearancePress(result.Outcome == StationClearanceConfirmed, null, string.Empty)
                : DescribeSettledStationClearance(settled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkStationClearanceUnknown(request, null);
            throw;
        }
        catch (InvalidOperationException exception)
            when (firstSend && request is not null && exception.Message == "WIRE_TO_GATE_NOT_READY")
        {
            // Refused before anything was written: a press that never left is not remembered as the first.
            lock (_stationClearanceGate)
            {
                if (ReferenceEquals(_stationClearanceUnanswered, request))
                {
                    _stationClearanceUnanswered = null;
                }
            }

            return StationClearancePress.Blocked("会话未就绪，清桩确认未发送。");
        }
        catch (WireToGateRequestNotAcceptedException exception) when (request is not null)
        {
            // The server's own answer to this request: read, and not taken. That ends the id. Kept as unknown, every
            // later press would resend the id the server has just refused -- and a refusal such as
            // BUSINESS_ID_CONTENT_CONFLICT is about that id (8005-agv-onboard-hmi#221 review, S1).
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"服务端没有受理人工清桩确认：confirmationRequestId={request.ConfirmationRequestId}，reason={exception.ReasonCode}。");
            WireToGateStationClearanceOutcome? notAccepted = EndStationClearanceAsNotAccepted(
                request, exception.ReasonCode);
            return notAccepted is null
                ? new StationClearancePress(false, null, string.Empty)
                : new StationClearancePress(
                    false,
                    "STATION_CLEARANCE_NOT_ACCEPTED",
                    WireToGateStationClearanceText.StatusText(notAccepted));
        }
        catch (Exception exception)
        {
            // Every failure, not a list of them. The session fails a waiting request with whatever its receive loop
            // failed with, and that is not this method's to enumerate: a malformed message of any type read while
            // this press waits surfaces here as a JsonException. Escaping from here it reaches the window's
            // async void handler and from there the dispatcher's unhandled-exception path, which latches
            // UNHANDLED_UI_ERROR until the vehicle is restarted -- for a press that changed nothing
            // (8005-agv-onboard-hmi#221 review, S3).
            bool expected = exception is IOException
                or TimeoutException
                or InvalidOperationException
                or InvalidDataException;
            _logger.Write(
                expected ? LogSeverity.Warning : LogSeverity.Error,
                nameof(WireToGateBusinessService),
                $"人工清桩确认未完成：confirmationRequestId={request?.ConfirmationRequestId ?? "未生成"}，"
                + $"{exception.GetType().Name}：{exception.Message}。",
                exception);
            if (request is null)
            {
                return StationClearancePress.Blocked($"清桩确认未发送：{exception.Message}。");
            }

            WireToGateStationClearanceOutcome? unknown = MarkStationClearanceUnknown(
                request,
                exception is TimeoutException ? null : exception.Message);
            return unknown is null
                ? new StationClearancePress(false, null, string.Empty)
                : new StationClearancePress(
                    false,
                    "STATION_CLEARANCE_UNKNOWN",
                    WireToGateStationClearanceText.StatusText(unknown));
        }
    }

    private WireToGateStationClearanceView ReadStationClearance(bool awaitingAnswer)
    {
        WireToGateJourneySnapshot journey = _session.CurrentJourney;
        ManualStationClearanceConfirmationRequestedPayload? unanswered;
        WireToGateStationClearanceOutcome? outcome;
        lock (_stationClearanceGate)
        {
            unanswered = _stationClearanceUnanswered;
            outcome = _stationClearanceOutcome;
        }

        // Read against the journey at this instant, not against a field some handler cleared: the view model's
        // journey handler runs before this service's, so a result line kept until ours had run would be shown
        // once more over a business state that has already moved on.
        if (ServerSaysTheClearanceIsOver(journey))
        {
            outcome = null;
        }

        if (awaitingAnswer)
        {
            return new(null, WireToGateStationClearanceUnavailability.AwaitingAnswer, outcome);
        }

        if (!journey.IsClearingStop)
        {
            return new(null, WireToGateStationClearanceUnavailability.NotClearing, outcome);
        }

        if (!CanUseRecoveryOperator(requireProof: true)
            || Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable) is not { } operatorId)
        {
            return new(null, WireToGateStationClearanceUnavailability.NoVerifiedMaintainer, outcome);
        }

        if (WireToGateStationClearanceStation.Resolve(journey) is not { } stationId)
        {
            return new(null, WireToGateStationClearanceUnavailability.StationUnknown, outcome);
        }

        bool sameConfirmation = unanswered is not null
            && string.Equals(unanswered.StationId, stationId, StringComparison.Ordinal)
            && string.Equals(unanswered.Operator.OperatorId, operatorId, StringComparison.Ordinal);
        return new(
            new WireToGateStationClearancePrompt(
                stationId,
                operatorId,
                sameConfirmation ? unanswered!.ConfirmationRequestId : null),
            WireToGateStationClearanceUnavailability.None,
            outcome);
    }

    /// <summary>
    /// A business state that names another purpose. A projection with no business state at all -- a dropped
    /// session -- says nothing either way, and must not be read as the clearance having ended: that is when an
    /// unknown result most needs to stay on screen and keep its id.
    /// </summary>
    private static bool ServerSaysTheClearanceIsOver(WireToGateJourneySnapshot journey) =>
        journey.VehicleBusinessState is not null && !journey.IsClearingStop;

    /// <summary>
    /// Forgets the unanswered request and the last outcome once the server says the vehicle is no longer
    /// clearing, so a later clearance of the same charger starts from nothing.
    /// </summary>
    private void ForgetStationClearanceOnceTheServerSaysItIsOver(WireToGateJourneySnapshot journey)
    {
        if (!ServerSaysTheClearanceIsOver(journey))
        {
            return;
        }

        lock (_stationClearanceGate)
        {
            _stationClearanceUnanswered = null;
            _stationClearanceOutcome = null;
        }
    }

    /// <summary>
    /// Applies the server's answer to the confirmation this vehicle is waiting on. <c>null</c> when it is
    /// waiting on no such confirmation -- already settled by another copy of the answer, or never its own --
    /// and then nothing is shown a second time.
    /// </summary>
    private WireToGateStationClearanceOutcome? SettleStationClearance(
        ManualStationClearanceConfirmationResultPayload result)
    {
        lock (_stationClearanceGate)
        {
            if (_stationClearanceUnanswered is not { } unanswered
                || !string.Equals(
                    unanswered.ConfirmationRequestId, result.ConfirmationRequestId, StringComparison.Ordinal))
            {
                return null;
            }

            _stationClearanceUnanswered = null;
            _stationClearanceOutcome = new WireToGateStationClearanceOutcome(
                result.Outcome == StationClearanceConfirmed
                    ? WireToGateStationClearanceOutcomeKind.Confirmed
                    : WireToGateStationClearanceOutcomeKind.Rejected,
                unanswered.ConfirmationRequestId,
                unanswered.StationId,
                result.StationReleased,
                result.Problem?.ReasonCode);
            return _stationClearanceOutcome;
        }
    }

    private WireToGateStationClearanceOutcome? EndStationClearanceAsNotAccepted(
        ManualStationClearanceConfirmationRequestedPayload request,
        string reasonCode)
    {
        lock (_stationClearanceGate)
        {
            // Not if a result got here first through the late path: that one stands.
            if (!ReferenceEquals(_stationClearanceUnanswered, request))
            {
                return null;
            }

            _stationClearanceUnanswered = null;
            _stationClearanceOutcome = new WireToGateStationClearanceOutcome(
                WireToGateStationClearanceOutcomeKind.NotAccepted,
                request.ConfirmationRequestId,
                request.StationId,
                false,
                reasonCode);
            return _stationClearanceOutcome;
        }
    }

    private WireToGateStationClearanceOutcome? MarkStationClearanceUnknown(
        ManualStationClearanceConfirmationRequestedPayload? request,
        string? reason)
    {
        lock (_stationClearanceGate)
        {
            // Not if an answer got here first through the late path: that one stands.
            if (request is null || !ReferenceEquals(_stationClearanceUnanswered, request))
            {
                return null;
            }

            _stationClearanceOutcome = new WireToGateStationClearanceOutcome(
                WireToGateStationClearanceOutcomeKind.Unknown,
                request.ConfirmationRequestId,
                request.StationId,
                false,
                reason);
            return _stationClearanceOutcome;
        }
    }

    /// <summary>
    /// A result nothing was waiting for: its request's wait had run out, or the session it was sent in dropped.
    /// It settles the unknown it belongs to, once; any other is logged and left.
    /// </summary>
    private void HandleLateStationClearanceResult(WireToGateRecoveryCommand command)
    {
        // The session client has already validated the payload against the 2.0.0 shape.
        ManualStationClearanceConfirmationResultPayload result =
            JsonSerializer.Deserialize<ManualStationClearanceConfirmationResultPayload>(command.PayloadJson, JsonOptions)
            ?? throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        if (SettleStationClearance(result) is not { } settled)
        {
            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"收到未在等待的人工清桩确认结果，已忽略：confirmationRequestId={result.ConfirmationRequestId}，outcome={result.Outcome}。");
            return;
        }

        StationClearancePress press = DescribeSettledStationClearance(settled);
        PublishOperatorResponse(press.Kind!, press.Message);
    }

    private StationClearancePress DescribeSettledStationClearance(WireToGateStationClearanceOutcome settled)
    {
        bool confirmed = settled.Kind == WireToGateStationClearanceOutcomeKind.Confirmed;
        _logger.Write(
            confirmed ? LogSeverity.Information : LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"人工清桩确认结果：confirmationRequestId={settled.ConfirmationRequestId}，station={settled.StationId}，"
            + $"outcome={settled.Kind}，stationReleased={settled.StationReleased}，reason={settled.ReasonCode ?? "无"}。");
        return new StationClearancePress(
            confirmed,
            confirmed ? "STATION_CLEARANCE_CONFIRMED" : "STATION_CLEARANCE_REJECTED",
            WireToGateStationClearanceText.StatusText(settled));
    }

    private static string DescribeUnavailableStationClearance(WireToGateStationClearanceView view) =>
        view.Unavailability switch
        {
            WireToGateStationClearanceUnavailability.StationUnknown =>
                WireToGateStationClearanceText.NoticeText(view) + "本次未发送。",
            WireToGateStationClearanceUnavailability.NoVerifiedMaintainer =>
                "清桩确认需要已验证的维护人员和在线会话，本次未发送。",
            _ => "服务端没有说明车辆在清桩中，清桩确认未发送。"
        };
}
