using System.IO;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The operator's entry for <c>CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION</c> (batch 9-17, <c>8005-agv-onboard-hmi#222</c>;
/// <c>REQ-0176</c>): while the control server says the vehicle is charging at a charger, a verified maintainer reports
/// what they observed when it did not charge.
/// </summary>
/// <remarks>
/// <para>
/// <b>It sends a request and shows the answer, and that is all</b> (<c>NEVER_DECIDE_CHARGING_POLICY_LOCALLY</c>).
/// Whatever the server answers -- either outcome, any <c>chargingPolicyDecision</c> or none -- the journey projection,
/// the sublot entry, the recovery state and every slot stay as they were; where the vehicle goes next and whether it
/// takes work is read from the business state and the plan the server sends afterwards. This file reaches no IO, no
/// door and no recovery state; <c>WireToGateUnableToChargeTests</c> holds it to that, and that is what lets the entry
/// stay outside the nine recovery entries and open under a fatal-fault latch, as the station clearance does
/// (<c>8005-agv-onboard-hmi#221</c>).
/// </para>
/// <para>
/// <b>Off unless <c>wireToGate.unableToChargeEntryEnabled</c> is on</b> (8005-agv-onboard-hmi#222 review, item 1): a
/// control server without <c>8005-agv-control-server#410</c> has no case for this message and closes the connection on
/// it, and nothing on the wire says which kind of server the vehicle is talking to.
/// </para>
/// <para>
/// <b>Then three conditions, all read from what the server sent or from the maintainer check 「充电后返回服务」 makes.</b>
/// The business state's <c>activePurpose</c> is <c>CHARGING</c>; the maintenance switch is on, the session can carry
/// a request, an operator id and the administrator proof are configured (<c>CanUseRecoveryOperator(requireProof:
/// true)</c> -- the proof is checked to be configured and not sent, the message has no field for it, and no role is
/// judged here: whether the press is a confirmation or a report the server decides from the operator id,
/// <c>8005-agv-control-server#410</c>); and the plan's current leg is a charger
/// (<see cref="WireToGateUnableToCharge.ResolveCharger"/>). Nothing narrower: not the leg's state, not the charging
/// cycle's, not which observed condition fits -- those are the server's to judge, and it answers each.
/// </para>
/// <para>
/// <b>The press is bound to what the operator read</b>, the charger, the operator and the observed condition
/// together, and is refused if that is no longer one of the current prompts.
/// </para>
/// <para>
/// <b>An unanswered confirmation keeps its id</b> and its condition. A wait that ran out or a session that dropped
/// leaves the result unknown and nothing is resent; the entry then offers that one confirmation again -- same
/// <c>confirmationRequestId</c>, same payload, new messageId -- and no other condition until it is answered. A
/// result of either kind, or a <c>ProtocolProblem</c> correlated to the request, ends it. The request is kept in
/// memory only (<c>durableBeforeSend: false</c>, the same decision as the station clearance); a restart forgets it.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string UnableToChargeConfirmed = "CONFIRMED";

    private readonly object _unableToChargeGate = new();

    /// <summary>The request that went out, or may have, and has no answer yet. Guarded by the gate.</summary>
    private UnableToChargeFieldConfirmationRequestedPayload? _unableToChargeUnanswered;

    /// <summary>What the last confirmation came to. Guarded by the gate.</summary>
    private WireToGateUnableToChargeOutcome? _unableToChargeOutcome;

    /// <summary>
    /// The unanswered request the server's snapshot ended while its press was still waiting. Guarded by the gate.
    /// </summary>
    /// <remarks>
    /// The vector itself sends the result and then a business state snapshot, and the receive loop hands the result
    /// to the waiting press and goes straight on to the snapshot. When that snapshot ends the charging claim, its
    /// journey handler forgets the request before the press has read its own result. The press still tells the
    /// operator what the server recorded and decided: it settles against this reference, and writes nothing to the
    /// result line, which belongs to a charging claim that is over (8005-agv-onboard-hmi#222 review, item 2). The
    /// clearing a confirmation leads to does not end the claim (8005-agv-onboard-hmi#242), so there the press settles
    /// the request as usual and the line stays.
    /// </remarks>
    private UnableToChargeFieldConfirmationRequestedPayload? _unableToChargeForgotten;

    /// <summary>
    /// The server has said this charging claim went on into the clearing of an unable-to-charge, so a
    /// <c>CHARGING</c> purpose after it is a new attempt. Guarded by the gate.
    /// </summary>
    private bool _unableToChargeClearingSeen;

    /// <summary>1 while a confirmation is waiting for its answer: a second press sends nothing.</summary>
    private int _unableToChargeAwaitingAnswer;

    public WireToGateUnableToChargeView UnableToCharge =>
        ReadUnableToCharge(Volatile.Read(ref _unableToChargeAwaitingAnswer) != 0);

    public bool CanConfirmUnableToCharge => UnableToCharge.Prompts.Count > 0;

    /// <param name="shown">The prompt the operator confirmed, as the view handed it out.</param>
    public async Task<bool> ConfirmUnableToChargeAsync(
        WireToGateUnableToChargePrompt shown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _unableToChargeAwaitingAnswer, 1, 0) != 0)
        {
            PublishOperatorResponse(
                "UNABLE_TO_CHARGE_BLOCKED",
                "上一次现场确认充不上还在等待服务端应答，本次未发送。");
            return false;
        }

        UnableToChargePress press;
        try
        {
            press = await SendUnableToChargeAsync(shown, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _unableToChargeAwaitingAnswer, 0);
        }

        // Published once the entry is on offer again, so a view that refreshes on this event reads the settled state.
        if (press.Kind is not null)
        {
            PublishOperatorResponse(press.Kind, press.Message);
        }

        return press.Confirmed;
    }

    private sealed record UnableToChargePress(bool Confirmed, string? Kind, string Message)
    {
        public static UnableToChargePress Blocked(string message) =>
            new(false, "UNABLE_TO_CHARGE_BLOCKED", message);
    }

    private async Task<UnableToChargePress> SendUnableToChargeAsync(
        WireToGateUnableToChargePrompt shown,
        CancellationToken cancellationToken)
    {
        UnableToChargeFieldConfirmationRequestedPayload? request = null;
        bool firstSend = false;
        try
        {
            lock (_unableToChargeGate)
            {
                WireToGateUnableToChargeView view = ReadUnableToCharge(awaitingAnswer: false);
                if (view.Prompts.Count == 0)
                {
                    return UnableToChargePress.Blocked(DescribeUnavailableUnableToCharge(view));
                }

                if (!view.Prompts.Contains(shown))
                {
                    return UnableToChargePress.Blocked(
                        "现场确认的内容在确认期间变了（充电桩、确认人，或上一次提交已有结果），本次未发送，请按当前显示的内容重新确认。");
                }

                // The prompt says which it is, and it was just found among the current ones: a resubmission is the
                // unanswered request as it went out the first time, anything else a new confirmation.
                firstSend = shown.ResubmittedConfirmationRequestId is null;
                request = firstSend
                    ? new UnableToChargeFieldConfirmationRequestedPayload(
                        Guid.NewGuid().ToString("D"),
                        shown.ChargerStationId,
                        shown.ObservedCondition,
                        // ReadOperatorContext()'s three values, with the operator id the prompt was bound to: the
                        // view built the prompt from that same variable and was just read again under this lock.
                        new WireToGateOperatorContextPayload(
                            shown.OperatorId,
                            GetProtocolVerificationMethod(),
                            _clock.Now.ToUniversalTime()),
                        _clock.Now.ToUniversalTime())
                    : _unableToChargeUnanswered!;
                _unableToChargeUnanswered = request;
            }

            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"提交现场确认充不上：confirmationRequestId={request.ConfirmationRequestId}，charger={request.ChargerStationId}，"
                + $"observedCondition={request.ObservedCondition}，operator={request.Operator.OperatorId}，"
                + $"resubmission={!firstSend}。");
            UnableToChargeFieldConfirmationResultPayload result = await _session
                .ConfirmUnableToChargeAsync(
                    // A resubmission is a new message about the same confirmation.
                    firstSend ? request.ConfirmationRequestId : Guid.NewGuid().ToString("D"),
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            // Settled against this press's own request, not against the shared field a snapshot may have cleared.
            WireToGateUnableToChargeOutcome? settled = ApplyOwnUnableToChargeOutcome(
                request,
                new WireToGateUnableToChargeOutcome(
                    result.Outcome == UnableToChargeConfirmed
                        ? WireToGateUnableToChargeOutcomeKind.Confirmed
                        : WireToGateUnableToChargeOutcomeKind.Rejected,
                    request.ConfirmationRequestId,
                    request.ChargerStationId,
                    request.ObservedCondition,
                    result.ChargingPolicyDecision,
                    result.Problem?.ReasonCode),
                endsTheRequest: true);
            return settled is null
                ? new UnableToChargePress(result.Outcome == UnableToChargeConfirmed, null, string.Empty)
                : DescribeSettledUnableToCharge(settled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkUnableToChargeUnknown(request, null);
            throw;
        }
        catch (InvalidOperationException exception)
            when (firstSend && request is not null && exception.Message == "WIRE_TO_GATE_NOT_READY")
        {
            // Refused before anything was written: a press that never left is not remembered as the first.
            lock (_unableToChargeGate)
            {
                if (ReferenceEquals(_unableToChargeUnanswered, request))
                {
                    _unableToChargeUnanswered = null;
                }
            }

            return UnableToChargePress.Blocked("会话未就绪，现场确认充不上未发送。");
        }
        catch (WireToGateRequestNotAcceptedException exception) when (request is not null)
        {
            // The server's own answer to this request: read, and not taken. That ends the id, as for the station
            // clearance (8005-agv-onboard-hmi#221 review, S1).
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"服务端没有受理现场确认充不上：confirmationRequestId={request.ConfirmationRequestId}，reason={exception.ReasonCode}。");
            WireToGateUnableToChargeOutcome? notAccepted = EndUnableToChargeAsNotAccepted(
                request, exception.ReasonCode);
            return notAccepted is null
                ? new UnableToChargePress(false, null, string.Empty)
                : new UnableToChargePress(
                    false,
                    "UNABLE_TO_CHARGE_NOT_ACCEPTED",
                    WireToGateUnableToChargeText.StatusText(notAccepted));
        }
        catch (Exception exception)
        {
            // Every failure, not a list of them, for the reason the station clearance gives: the session fails a
            // waiting request with whatever its receive loop failed with, and escaping from here it would reach the
            // window's async void handler and latch UNHANDLED_UI_ERROR for a press that changed nothing.
            bool expected = exception is IOException
                or TimeoutException
                or InvalidOperationException
                or InvalidDataException;
            _logger.Write(
                expected ? LogSeverity.Warning : LogSeverity.Error,
                nameof(WireToGateBusinessService),
                $"现场确认充不上未完成：confirmationRequestId={request?.ConfirmationRequestId ?? "未生成"}，"
                + $"{exception.GetType().Name}：{exception.Message}。",
                exception);
            if (request is null)
            {
                return UnableToChargePress.Blocked($"现场确认充不上未发送：{exception.Message}。");
            }

            WireToGateUnableToChargeOutcome? unknown = MarkUnableToChargeUnknown(
                request,
                exception is TimeoutException ? null : exception.Message);
            return unknown is null
                ? new UnableToChargePress(false, null, string.Empty)
                : new UnableToChargePress(
                    false,
                    "UNABLE_TO_CHARGE_UNKNOWN",
                    WireToGateUnableToChargeText.StatusText(unknown));
        }
    }

    private WireToGateUnableToChargeView ReadUnableToCharge(bool awaitingAnswer)
    {
        WireToGateJourneySnapshot journey = _session.CurrentJourney;
        UnableToChargeFieldConfirmationRequestedPayload? unanswered;
        WireToGateUnableToChargeOutcome? outcome;
        bool clearingSeen;
        lock (_unableToChargeGate)
        {
            unanswered = _unableToChargeUnanswered;
            outcome = _unableToChargeOutcome;
            clearingSeen = _unableToChargeClearingSeen;
        }

        // Read against the journey at this instant, not against a field some handler cleared: the view model's
        // journey handler runs before this service's (the station clearance's reasoning).
        outcome = ServerSaysTheChargingIsOver(journey, clearingSeen) ? null : AsShownNow(outcome, journey);

        // Off unless configured: a control server without 8005-agv-control-server#410 ends the session on this
        // message, and nothing on the wire says which kind of server this is.
        if (!_recoveryOptions.UnableToChargeEntryEnabled)
        {
            return new([], WireToGateUnableToChargeUnavailability.Disabled, null);
        }

        if (awaitingAnswer)
        {
            return new([], WireToGateUnableToChargeUnavailability.AwaitingAnswer, outcome);
        }

        if (!WireToGateUnableToCharge.IsCharging(journey))
        {
            return new([], WireToGateUnableToChargeUnavailability.NotCharging, outcome);
        }

        if (!CanUseRecoveryOperator(requireProof: true)
            || Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable) is not { } operatorId)
        {
            return new([], WireToGateUnableToChargeUnavailability.NoVerifiedMaintainer, outcome);
        }

        if (WireToGateUnableToCharge.ResolveCharger(journey) is not { } chargerStationId)
        {
            return new([], WireToGateUnableToChargeUnavailability.StationUnknown, outcome);
        }

        bool sameConfirmation = unanswered is not null
            && string.Equals(unanswered.ChargerStationId, chargerStationId, StringComparison.Ordinal)
            && string.Equals(unanswered.Operator.OperatorId, operatorId, StringComparison.Ordinal);
        WireToGateUnableToChargePrompt[] prompts = sameConfirmation
            // Only the unanswered one: another condition now would be a second confirmation of the same attempt
            // while the first may already have been taken.
            ? [new(chargerStationId, operatorId, unanswered!.ObservedCondition, unanswered.ConfirmationRequestId)]
            : [
                .. WireToGateUnableToCharge.ObservedConditions
                    .Select(condition => new WireToGateUnableToChargePrompt(chargerStationId, operatorId, condition, null))
            ];
        return new(prompts, WireToGateUnableToChargeUnavailability.None, outcome);
    }

    /// <summary>
    /// The server says the charging claim this confirmation belongs to is over. A projection with no business state
    /// at all -- a dropped session -- says nothing either way, and must not be read as the charging having ended:
    /// that is when an unknown result most needs to stay on screen and keep its id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>CHARGING</c> going on to <c>CLEARING_MAINTENANCE</c> is the same claim, not its end</b>
    /// (8005-agv-onboard-hmi#242). A confirmation the server takes moves the vehicle into the clearing at once
    /// (<c>chargingCycleState=UNABLE_TO_CHARGE</c>, the same path as the system's own confirmation,
    /// 8005-agv-control-server#410), and the business state saying so arrives right behind the result; read as the
    /// end, it took the result line -- that the server recorded it, and its decision -- off the screen within a
    /// second. The claim ends when the purpose leaves both, or when the clearing's charging cycle is no longer
    /// <c>UNABLE_TO_CHARGE</c>.
    /// </para>
    /// <para>
    /// <b>A <c>CHARGING</c> purpose after the clearing is a new attempt</b>, which starts from nothing:
    /// <paramref name="clearingSeen"/> says a clearing of this claim was seen. A session that dropped through the
    /// end of the clearing would otherwise carry the old result into the next charging.
    /// </para>
    /// </remarks>
    private static bool ServerSaysTheChargingIsOver(WireToGateJourneySnapshot journey, bool clearingSeen) =>
        journey.VehicleBusinessState is { } state
        && (state.ActivePurpose == "CHARGING"
            ? clearingSeen
            : !IsTheClearingOfAnUnableToCharge(state));

    private static bool IsTheClearingOfAnUnableToCharge(WireToGateVehicleBusinessState state) =>
        state is { ActivePurpose: "CLEARING_MAINTENANCE", ChargingCycleState: "UNABLE_TO_CHARGE" };

    private static bool IsTheClearingOfAnUnableToCharge(WireToGateJourneySnapshot journey) =>
        journey.VehicleBusinessState is { } state && IsTheClearingOfAnUnableToCharge(state);

    /// <summary>
    /// The last outcome as it may be shown against this journey, within a claim the server has not ended;
    /// <c>null</c> when it no longer belongs on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Another charger is another attempt</b>: an outcome about a charger other than the plan's current one is not
    /// shown, whatever the purpose says (8005-agv-onboard-hmi#242 review, N1). A plan that names no charger says
    /// nothing either way.
    /// </para>
    /// <para>
    /// <b>Into the clearing go only a confirmation and an unknown</b> (8005-agv-onboard-hmi#242 review, S2, S3). A
    /// rejection or a refusal shown there would contradict the clearing the server has since entered on its own
    /// confirmation -- 「服务端未确认充不上」 beside a charging cell that reads 充不上电. An unknown stays, for a late
    /// result can still settle it, and is marked so its line does not ask for a resubmission the closed entry no
    /// longer allows.
    /// </para>
    /// </remarks>
    private static WireToGateUnableToChargeOutcome? AsShownNow(
        WireToGateUnableToChargeOutcome? outcome,
        WireToGateJourneySnapshot journey)
    {
        if (outcome is null
            || (WireToGateUnableToCharge.ResolveCharger(journey) is { } charger
                && !string.Equals(charger, outcome.ChargerStationId, StringComparison.Ordinal)))
        {
            return null;
        }

        return !IsTheClearingOfAnUnableToCharge(journey)
            ? outcome
            : outcome.Kind switch
            {
                WireToGateUnableToChargeOutcomeKind.Confirmed => outcome,
                WireToGateUnableToChargeOutcomeKind.Unknown => outcome with { InClearing = true },
                _ => null
            };
    }

    /// <summary>
    /// Forgets the unanswered request and the last outcome once the server says the charging claim is over, so a
    /// later charging attempt at the same charger starts from nothing.
    /// </summary>
    private void ForgetUnableToChargeOnceTheServerSaysItIsOver(WireToGateJourneySnapshot journey)
    {
        lock (_unableToChargeGate)
        {
            if (!ServerSaysTheChargingIsOver(journey, _unableToChargeClearingSeen))
            {
                if (IsTheClearingOfAnUnableToCharge(journey))
                {
                    _unableToChargeClearingSeen = true;
                }

                // What no longer belongs on screen is not kept either, so a plan that comes back to the old charger
                // does not bring a rejection back with it.
                if (AsShownNow(_unableToChargeOutcome, journey) is null)
                {
                    _unableToChargeOutcome = null;
                }

                return;
            }

            if (_unableToChargeUnanswered is not null)
            {
                _unableToChargeForgotten = _unableToChargeUnanswered;
            }

            _unableToChargeUnanswered = null;
            _unableToChargeOutcome = null;
            _unableToChargeClearingSeen = false;
        }
    }

    /// <summary>
    /// Applies what this press's own request came to. <c>null</c> when another path has already told the operator --
    /// a result that arrived through the late path after this press's wait ran out. A request the server's snapshot
    /// ended meanwhile is still told to the operator, and nothing is written to the result line.
    /// </summary>
    private WireToGateUnableToChargeOutcome? ApplyOwnUnableToChargeOutcome(
        UnableToChargeFieldConfirmationRequestedPayload request,
        WireToGateUnableToChargeOutcome outcome,
        bool endsTheRequest)
    {
        lock (_unableToChargeGate)
        {
            if (ReferenceEquals(_unableToChargeUnanswered, request))
            {
                if (endsTheRequest)
                {
                    _unableToChargeUnanswered = null;
                }

                _unableToChargeOutcome = outcome;
                return outcome;
            }

            if (ReferenceEquals(_unableToChargeForgotten, request))
            {
                _unableToChargeForgotten = null;
                return outcome with { ChargingEnded = true };
            }

            return null;
        }
    }

    /// <summary>
    /// Applies the server's answer to the confirmation this vehicle is waiting on. <c>null</c> when it is waiting
    /// on no such confirmation -- already settled by another copy of the answer, or never its own -- and then
    /// nothing is shown a second time.
    /// </summary>
    /// <remarks>
    /// The one thing the answer changes on this vehicle is this record: what the result line says. Nothing else is
    /// written for either outcome or any decision (<c>NEVER_DECIDE_CHARGING_POLICY_LOCALLY</c>).
    /// </remarks>
    private WireToGateUnableToChargeOutcome? SettleUnableToCharge(UnableToChargeFieldConfirmationResultPayload result)
    {
        lock (_unableToChargeGate)
        {
            if (_unableToChargeUnanswered is not { } unanswered
                || !string.Equals(
                    unanswered.ConfirmationRequestId, result.ConfirmationRequestId, StringComparison.Ordinal))
            {
                return null;
            }

            _unableToChargeUnanswered = null;
            _unableToChargeOutcome = new WireToGateUnableToChargeOutcome(
                result.Outcome == UnableToChargeConfirmed
                    ? WireToGateUnableToChargeOutcomeKind.Confirmed
                    : WireToGateUnableToChargeOutcomeKind.Rejected,
                unanswered.ConfirmationRequestId,
                unanswered.ChargerStationId,
                unanswered.ObservedCondition,
                result.ChargingPolicyDecision,
                result.Problem?.ReasonCode);
            return _unableToChargeOutcome;
        }
    }

    private WireToGateUnableToChargeOutcome? EndUnableToChargeAsNotAccepted(
        UnableToChargeFieldConfirmationRequestedPayload request,
        string reasonCode)
    {
        // Not if a result got here first through the late path: that one stands.
        return ApplyOwnUnableToChargeOutcome(
            request,
            new WireToGateUnableToChargeOutcome(
                WireToGateUnableToChargeOutcomeKind.NotAccepted,
                request.ConfirmationRequestId,
                request.ChargerStationId,
                request.ObservedCondition,
                null,
                reasonCode),
            endsTheRequest: true);
    }

    private WireToGateUnableToChargeOutcome? MarkUnableToChargeUnknown(
        UnableToChargeFieldConfirmationRequestedPayload? request,
        string? reason)
    {
        // Not if an answer got here first through the late path: that one stands. The request stays unanswered.
        WireToGateUnableToChargeOutcome? unknown = request is null
            ? null
            : ApplyOwnUnableToChargeOutcome(
                request,
                new WireToGateUnableToChargeOutcome(
                    WireToGateUnableToChargeOutcomeKind.Unknown,
                    request.ConfirmationRequestId,
                    request.ChargerStationId,
                    request.ObservedCondition,
                    null,
                    reason),
                endsTheRequest: false);

        // The operator record says what the line says: in the clearing, nothing about resubmitting.
        return unknown is not null && IsTheClearingOfAnUnableToCharge(_session.CurrentJourney)
            ? unknown with { InClearing = true }
            : unknown;
    }

    /// <summary>
    /// A result nothing was waiting for: its request's wait had run out, or the session it was sent in dropped.
    /// It settles the unknown it belongs to, once; any other is logged and left.
    /// </summary>
    private void HandleLateUnableToChargeResult(WireToGateRecoveryCommand command)
    {
        // The session client has already validated the payload against the 2.0.0 shape.
        UnableToChargeFieldConfirmationResultPayload result =
            JsonSerializer.Deserialize<UnableToChargeFieldConfirmationResultPayload>(command.PayloadJson, JsonOptions)
            ?? throw new InvalidDataException("PROTOCOL_SCHEMA_INVALID");
        if (SettleUnableToCharge(result) is not { } settled)
        {
            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"收到未在等待的现场确认充不上结果，已忽略：confirmationRequestId={result.ConfirmationRequestId}，outcome={result.Outcome}。");
            return;
        }

        UnableToChargePress press = DescribeSettledUnableToCharge(settled);
        PublishOperatorResponse(press.Kind!, press.Message);
    }

    private UnableToChargePress DescribeSettledUnableToCharge(WireToGateUnableToChargeOutcome settled)
    {
        bool confirmed = settled.Kind == WireToGateUnableToChargeOutcomeKind.Confirmed;
        _logger.Write(
            confirmed ? LogSeverity.Information : LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"现场确认充不上结果：confirmationRequestId={settled.ConfirmationRequestId}，charger={settled.ChargerStationId}，"
            + $"outcome={settled.Kind}，chargingPolicyDecision={settled.ChargingPolicyDecision ?? "无"}，"
            + $"reason={settled.ReasonCode ?? "无"}。");
        return new UnableToChargePress(
            confirmed,
            confirmed ? "UNABLE_TO_CHARGE_CONFIRMED" : "UNABLE_TO_CHARGE_REJECTED",
            WireToGateUnableToChargeText.StatusText(settled));
    }

    private static string DescribeUnavailableUnableToCharge(WireToGateUnableToChargeView view) =>
        view.Unavailability switch
        {
            WireToGateUnableToChargeUnavailability.StationUnknown =>
                WireToGateUnableToChargeText.NoticeText(view) + "本次未发送。",
            WireToGateUnableToChargeUnavailability.NoVerifiedMaintainer =>
                "现场确认充不上需要已验证的维护人员和在线会话，本次未发送。",
            WireToGateUnableToChargeUnavailability.Disabled =>
                "现场确认充不上入口未启用（wireToGate.unableToChargeEntryEnabled），本次未发送。",
            _ => "服务端没有说明车辆在充电用途上，现场确认充不上未发送。"
        };
}
