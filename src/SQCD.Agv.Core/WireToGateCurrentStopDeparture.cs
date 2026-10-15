namespace SQCD.Agv.Core;

/// <summary>
/// One press of 「出发」 at a staging pickup stop (protocol 4.0.0 candidate, CP-0012, 8005-agv-onboard-hmi#349) or at a machine
/// pickup stop whose loads are done (CP-0014, 8005-agv-onboard-hmi#352), as the
/// journal holds it from before the request is sent until long after its result came back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Journaled before it is sent</b> (<c>JOURNAL_DEPARTURE_REQUEST_BEFORE_SENDING</c>), and every send of it -- the first,
/// a reconnect's replay, a resend after a restart -- is rebuilt from these fields, so the request goes out word for word
/// the same under the same <see cref="DepartureRequestId"/> and <see cref="MessageId"/>
/// (<c>NEVER_MINT_NEW_REQUEST_ID_FOR_SAME_PRESS</c>). The control server answers a changed copy under the same id with
/// <c>BUSINESS_ID_CONTENT_CONFLICT</c>; that is why <see cref="VerifiedAt"/> is kept instead of taken again.
/// </para>
/// <para>
/// <b>The request carries no press time and no validity</b> (protocol#13 review L4). A press that waits out a dropped
/// link is still the operator's press when it arrives, and the server judges it against the stop as it stands then. So
/// the screen says the departure is pending for as long as <see cref="Result"/> is <c>null</c>, however long that is.
/// </para>
/// </remarks>
/// <param name="StationId">The <c>stationId</c> of the worklist shown at the press.</param>
/// <param name="WorklistRevision">The <c>worklistRevision</c> of the worklist shown at the press.</param>
/// <param name="OperationSessionId">
/// The worklist's <c>operationSessionId</c>: <c>null</c> on an empty worklist, which is the cargo-holding wait -- the time
/// the operator most often departs.
/// </param>
public sealed record WireToGateCurrentStopDeparture(
    string DepartureRequestId,
    string MessageId,
    string StationId,
    long WorklistRevision,
    string? OperationSessionId,
    string OperatorId,
    string VerificationMethod,
    DateTimeOffset VerifiedAt)
{
    public string? MovementLegId { get; init; }

    /// <summary>The server's answer, once one has been journaled; <c>null</c> while the departure is pending.</summary>
    public WireToGateCurrentStopDepartureOutcome? Result { get; init; }
}

/// <summary>
/// The server's <c>CurrentStopDepartureResult</c> as journaled, before the vehicle acknowledges it.
/// </summary>
/// <param name="ResultMessageId">The result's own <c>messageId</c>, which the <c>DurableAck</c> names.</param>
/// <param name="ResultContentSha256">The hash the <c>DurableAck</c> carries; a replay must hash the same.</param>
/// <param name="ReasonCode"><c>null</c> on <c>ACCEPTED</c>; the problem's <c>reasonCode</c> on <c>REJECTED</c>.</param>
public sealed record WireToGateCurrentStopDepartureOutcome(
    string ResultMessageId,
    string ResultContentSha256,
    string Outcome,
    string? ReasonCode,
    long CurrentWorklistRevision,
    DateTimeOffset ReceivedAt)
{
    public const string Accepted = "ACCEPTED";

    public const string Rejected = "REJECTED";
}

/// <summary>
/// The server's <c>CurrentStopDepartureResult</c>, checked against its schema and handed to the business service, which
/// journals it and then acknowledges it (<c>durableBeforeAck</c>).
/// </summary>
/// <param name="ContentSha256">
/// The received envelope's content hash, computed the way the vehicle hashes every message it acknowledges
/// (<c>ComputeContentSha256</c>, as for <c>SnapshotAppliedAck</c>).
/// </param>
public sealed record WireToGateCurrentStopDepartureResult(
    string MessageId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string DepartureRequestId,
    string Outcome,
    string? ReasonCode,
    long CurrentWorklistRevision,
    string ContentSha256)
    : WireToGateServerCommand("CurrentStopDepartureResult", MessageId, null, SessionGeneration, SentAt);

/// <summary>Why 「出发」 cannot be pressed now, in the order the entry checks them.</summary>
public enum WireToGateCurrentStopDepartureUnavailability
{
    None,

    /// <summary>
    /// Neither a staging pickup stop nor a machine pickup stop whose loads are done: the entry is not shown at all. The name
    /// predates 8005-agv-onboard-hmi#352 and is kept.
    /// </summary>
    NotAtStagingPickupStop,

    /// <summary>A severe safety fault is latched on this vehicle (review L3: not a link problem, and not shown as one).</summary>
    SafetyFaultLatched,

    /// <summary>The session is not connected, or not Ready.</summary>
    SessionNotReady,

    /// <summary>No operator is signed in on this vehicle.</summary>
    NoOperator,

    /// <summary>
    /// A departure was sent and its result has not come back; or it was accepted and the stop has not moved on since, so a
    /// second press would only draw a refusal under the acceptance (review L1).
    /// </summary>
    AwaitingResult,

    /// <summary>A sublot was submitted and the server has not answered it with a load or a rejection yet.</summary>
    EntryInProgress,

    /// <summary>A slot operation runs, is unsettled, or its result is not yet acknowledged.</summary>
    SlotOperationInProgress,

    /// <summary>A load correction or a load cancellation is open.</summary>
    CorrectionOrCancellationOpen
}

/// <summary>
/// The facts the 「出发」 entry is judged on, read by the business service at one instant (review M3: the judgement itself is
/// <see cref="WireToGateCurrentStopDepartureGate.Judge"/>, a function of these alone, so every branch has its own test).
/// </summary>
/// <param name="DepartureAwaitingResult">A press is on file with no result, or one is being journaled right now.</param>
/// <param name="AcceptedAtThisStop">The press on file was accepted for the stop still on screen.</param>
/// <param name="EntryInProgress">A sublot went out and the server has not answered it with a load or a rejection.</param>
/// <param name="SlotOperationRunning">The executor holds a slot operation attempt.</param>
/// <param name="SlotOperationUnsettled">The journal names an unsettled slot operation attempt.</param>
/// <param name="ResultsAwaitingAcknowledgement">The journal holds results the server has not acknowledged.</param>
/// <param name="RecoveryVectorOpen">A load correction, compensation or other recovery vector is open.</param>
/// <param name="LoadCancellationOpen">A load cancellation, before or after any sublot, is open.</param>
public sealed record WireToGateCurrentStopDepartureFacts(
    bool SafetyFaultLatched,
    bool SessionReady,
    bool OperatorSignedIn,
    bool DepartureAwaitingResult,
    bool AcceptedAtThisStop,
    bool EntryInProgress,
    bool SlotOperationRunning,
    bool SlotOperationUnsettled,
    bool ResultsAwaitingAcknowledgement,
    bool RecoveryVectorOpen,
    bool LoadCancellationOpen)
{
    /// <summary>Everything that lets the press through; a test changes one fact at a time from here.</summary>
    public static WireToGateCurrentStopDepartureFacts Clear { get; } =
        new(false, true, true, false, false, false, false, false, false, false, false);
}

/// <summary>
/// Why 「出发」 cannot be pressed at a staging pickup stop, or <c>None</c>: the ticket's four conditions (8005-agv-onboard-hmi#349
/// item 1) and, before them, the ones that refuse any operator request. The first that holds decides, in the order of
/// <see cref="WireToGateCurrentStopDepartureUnavailability"/>.
/// </summary>
public static class WireToGateCurrentStopDepartureGate
{
    public static WireToGateCurrentStopDepartureUnavailability Judge(WireToGateCurrentStopDepartureFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.SafetyFaultLatched)
        {
            return WireToGateCurrentStopDepartureUnavailability.SafetyFaultLatched;
        }

        if (!facts.SessionReady)
        {
            return WireToGateCurrentStopDepartureUnavailability.SessionNotReady;
        }

        if (!facts.OperatorSignedIn)
        {
            return WireToGateCurrentStopDepartureUnavailability.NoOperator;
        }

        if (facts.DepartureAwaitingResult || facts.AcceptedAtThisStop)
        {
            return WireToGateCurrentStopDepartureUnavailability.AwaitingResult;
        }

        if (facts.EntryInProgress)
        {
            return WireToGateCurrentStopDepartureUnavailability.EntryInProgress;
        }

        if (facts.SlotOperationRunning || facts.SlotOperationUnsettled || facts.ResultsAwaitingAcknowledgement)
        {
            return WireToGateCurrentStopDepartureUnavailability.SlotOperationInProgress;
        }

        return facts.RecoveryVectorOpen || facts.LoadCancellationOpen
            ? WireToGateCurrentStopDepartureUnavailability.CorrectionOrCancellationOpen
            : WireToGateCurrentStopDepartureUnavailability.None;
    }

    /// <summary>
    /// Whether a settled press belongs to the stop now on screen, so its result is still shown there and an acceptance still
    /// holds the button (review M2, L1): the same station, and a worklist no newer than the revision the result names. The next
    /// trip back to the same station comes with a newer worklist, and shows nothing left over from this one.
    /// </summary>
    /// <remarks>
    /// The revision and not the operation session: the cargo-holding wait's empty worklist carries none, and a stop's
    /// worklist keeps its session while it moves on -- after which an acceptance must stop holding the button.
    /// </remarks>
    public static bool BelongsToStop(
        WireToGateCurrentStopDeparture departure,
        WireToGateCurrentStopWorklist worklist,
        WireToGateMovementLeg? currentLeg) =>
        currentLeg is null || currentLeg is not null ? BelongsToStop(departure, worklist) : false;

    public static bool BelongsToStop(WireToGateCurrentStopDeparture departure, WireToGateCurrentStopWorklist worklist)
    {
        ArgumentNullException.ThrowIfNull(departure);
        ArgumentNullException.ThrowIfNull(worklist);
        return departure.Result is { } result
            && string.Equals(departure.StationId, worklist.StationId, StringComparison.Ordinal)
            && worklist.Revision <= result.CurrentWorklistRevision;
    }
}

/// <summary>
/// What a press of 「出发」 would send: the worklist on screen, and who presses. The confirmation is bound to this record --
/// the press hands back the prompt it was shown and goes out only while it is still the current one.
/// </summary>
public sealed record WireToGateCurrentStopDeparturePrompt(
    string StationId,
    long WorklistRevision,
    string? OperationSessionId,
    int UnloadedItemCount,
    string OperatorId)
{
    /// <summary>
    /// At a machine pickup stop, the appended pickup stops the press puts back to be dispatched; 0 at the staging stop. Part of
    /// the prompt, so a stop appended under the confirmation makes it a different prompt and the press is not sent.
    /// </summary>
    public int PickupStopsAfter { get; init; }
}

/// <summary>
/// The 「出发」 entry as the business service offers it.
/// </summary>
/// <param name="Shown">
/// Whether the entry is on screen at all: at a staging pickup stop, and at a machine pickup stop once its loads are done
/// (<see cref="AtMachinePickupStop"/>).
/// </param>
/// <param name="Prompt">What a press now would send; <c>null</c> when it cannot be pressed.</param>
/// <param name="Last">
/// The latest press on file at this stop: pending while its result is <c>null</c>, otherwise the result to show. <c>null</c>
/// when there is none, or it was for another stop.
/// </param>
public sealed record WireToGateCurrentStopDepartureView(
    bool Shown,
    WireToGateCurrentStopDeparturePrompt? Prompt,
    WireToGateCurrentStopDepartureUnavailability Unavailability,
    WireToGateCurrentStopDeparture? Last)
{
    /// <summary>Shown at a machine pickup stop (8005-agv-onboard-hmi#352) rather than at the staging pickup stop.</summary>
    public bool AtMachinePickupStop { get; init; }

    public static WireToGateCurrentStopDepartureView Hidden { get; } =
        new(false, null, WireToGateCurrentStopDepartureUnavailability.NotAtStagingPickupStop, null);
}

/// <summary>
/// The one judgement of "the vehicle stands at a staging pickup stop" on this end (CP-0012, protocol wire notes "Staging
/// pickup stops"): a pickup stop at the fixed origin station of <c>STAGING_TO_WIRE</c>. Every rule of 8005-agv-onboard-hmi#349
/// applies there and nowhere else; machine pickups keep their screen unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>With items, the worklist says so itself:</b> every item is a <c>PICKUP</c> of <c>STAGING_TO_WIRE</c>
/// (the judgement of 8005-agv-onboard-hmi#348). No other task type picks up at the staging station.
/// </para>
/// <para>
/// <b>With no items it cannot</b>, and that is the cargo-holding wait: every load is done, the worklist is empty, and that is
/// exactly when the operator most often departs. Nothing on the wire names the staging station -- the control server does
/// not fill <c>publicStationFunction</c> -- so the vehicle remembers the stations where it has seen a staging pickup
/// worklist (<see cref="WireToGateRecoveryState.StagingPickupStationIds"/>, journaled so a restart in the wait keeps the
/// entry). An empty worklist counts there only while a loading phase is still open (<c>LOADING</c> or
/// <c>CARGO_HOLDING_WAIT</c>): that is the vehicle standing at the stop with its loading not yet over. A machine station
/// never enters that set, because a machine pickup never has a <c>STAGING_TO_WIRE</c> item.
/// </para>
/// <para>
/// <b>The server's word comes first.</b> When a plan leg at the worklist's station carries a <c>publicStationFunction</c>, that
/// decides: <c>WIRE_STAGING</c> is the staging station, any other function is not, and the two inferences above are not
/// consulted. Only when no leg there names a function -- the v2 control server leaves it <c>null</c> -- does the vehicle fall
/// back to them (8005-agv-onboard-hmi#349, coordinator's decision: <c>8005-agv-control-server#713</c> fills it at the staging
/// stop).
/// </para>
/// <para>
/// A non-business stop -- waiting point, charger, clearance -- is never one, whatever else arrived.
/// </para>
/// </remarks>
public static class WireToGateStagingPickupStop
{
    public const string StagingToWire = "STAGING_TO_WIRE";

    /// <summary>A worklist with items, every one a <c>STAGING_TO_WIRE</c> pickup.</summary>
    public static bool IsStagingPickupWorklist(WireToGateCurrentStopWorklist? worklist) =>
        worklist is { Items.Count: > 0 }
        && worklist.Items.All(item =>
            string.Equals(item.WorkType, StagingToWire, StringComparison.Ordinal)
            && string.Equals(item.StopRole, "PICKUP", StringComparison.Ordinal));

    public static bool Is(WireToGateJourneySnapshot journey, IReadOnlyCollection<string> knownStagingStationIds)
    {
        ArgumentNullException.ThrowIfNull(journey);
        ArgumentNullException.ThrowIfNull(knownStagingStationIds);
        if (journey.IsNonBusinessStop || journey.CurrentStopWorklist is not { } worklist)
        {
            return false;
        }

        bool loadingOpen = journey.VehicleBusinessState?.LoadingPhase?.State is "LOADING" or "CARGO_HOLDING_WAIT";
        if (StationFunctionOf(journey, worklist.StationId) is { } function)
        {
            return string.Equals(function, WireStaging, StringComparison.Ordinal)
                && (worklist.Items.Count > 0
                    ? worklist.Items.All(item => string.Equals(item.StopRole, "PICKUP", StringComparison.Ordinal))
                    : loadingOpen);
        }

        if (worklist.Items.Count > 0)
        {
            return IsStagingPickupWorklist(worklist);
        }

        return knownStagingStationIds.Contains(worklist.StationId, StringComparer.Ordinal) && loadingOpen;
    }

    public const string WireStaging = "WIRE_STAGING";

    /// <summary>
    /// Whether a new cancellation before any sublot may be offered at this stop: not at the staging pickup stop (CP-0012
    /// revision four, <c>REQ-0211</c>; 8005-agv-onboard-hmi#348), as at any machine pickup.
    /// </summary>
    /// <remarks>
    /// The same judgement as <see cref="Is"/>, so the stop that offers 「出发」 is exactly the stop that does not offer this:
    /// there, "this one is not loaded today" is the operator's 「出发」, which puts what was not loaded back to be dispatched,
    /// rather than a cancellation that ends the demand for good. A cancellation already sent is not this method's to judge:
    /// the business service lets it settle wherever the vehicle is.
    /// </remarks>
    public static bool OffersLoadCancellationBeforeAnySublot(
        WireToGateJourneySnapshot journey,
        IReadOnlyCollection<string> knownStagingStationIds) =>
        !Is(journey, knownStagingStationIds);

    /// <summary>
    /// The <c>publicStationFunction</c> the plan gives <paramref name="stationId"/>: the first one a leg at that station carries,
    /// whatever the leg's state -- the pickup legs may be <c>COMPLETED</c> during the cargo-holding wait. <c>null</c> when no leg
    /// there names one.
    /// </summary>
    public static string? StationFunctionOf(WireToGateJourneySnapshot journey, string stationId)
    {
        ArgumentNullException.ThrowIfNull(journey);
        return journey.UpcomingStopPlan?.Legs
            .OrderBy(leg => leg.Sequence)
            .Where(leg => string.Equals(leg.StationId, stationId, StringComparison.Ordinal))
            .Select(leg => leg.PublicStationFunction)
            .FirstOrDefault(function => function is not null);
    }
}

/// <summary>
/// The one judgement of "this machine pickup stop's loads are done and 「出发」 may be offered" on this end (CP-0014, REQ-0354;
/// protocol wire notes "<c>CurrentStopDepartureRequested</c> and <c>CurrentStopDepartureResult</c>"; 8005-agv-onboard-hmi#352).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not the loading phase.</b> The ticket's two waits look different there: the cargo-holding wait reads
/// <c>CARGO_HOLDING_WAIT</c>, the station departure wait alone reads <c>VEHICLE_FULL</c>, <c>CLOSED</c> -- or, on a journey that
/// does not hold cargo because every zone forbids en-route appends, still <c>LOADING</c>, since the server does not announce
/// that step (control-server <c>LoadingPhaseMachine.Announces</c>). So the phase decides nothing here.
/// </para>
/// <para>
/// <b>The worklist does.</b> It lists what the stop still has to load. Once the last load settles, the control server sends an
/// empty worklist ending <c>COMPLETED</c> (<c>RefreshDeadlineOfALoadedStopAsync</c>): every committed load is done, and the last
/// one was a load, so at least one basket went on board here. A stop whose last item ended otherwise -- cancelled,
/// compensated, released, expired -- says so in <c>stopEndedReason</c>, and a stop with nothing loaded is not "loaded and
/// done" (CP-0014 section 5, item 7).
/// </para>
/// <para>
/// <b>Or a basket loaded here</b> (<paramref name="basketLoadedHere"/> of <see cref="OffersDeparture"/>, read from
/// <see cref="WireToGateRecoveryState.HasLoadOnBoardAt"/>): the stop's last item may end otherwise -- A loaded, B cancelled
/// before any sublot -- and the stop is still loaded and done. The ending worklist carries <c>LOAD_CANCELLED</c> and no
/// operation session, so only the vehicle's own record of where A went on board can tell.
/// </para>
/// <para>
/// <b>Only while the vehicle stands there:</b> the current leg is the pickup leg at the worklist's station, <c>ARRIVED</c>. Once
/// the vehicle leaves, the plan moves past it.
/// </para>
/// <para>
/// <b>Never the staging station</b> (<see cref="WireToGateStagingPickupStop"/>): it keeps its own rule, CP-0012.
/// </para>
/// </remarks>
public static class WireToGateMachinePickupStop
{
    /// <summary>The <c>stopEndedReason</c> of a stop whose last item was loaded.</summary>
    public const string LoadsCompleted = "COMPLETED";

    /// <param name="basketLoadedHere">A load made at the worklist's station is still on board and not handed off.</param>
    public static bool OffersDeparture(
        WireToGateJourneySnapshot journey,
        IReadOnlyCollection<string> knownStagingStationIds,
        bool basketLoadedHere)
    {
        ArgumentNullException.ThrowIfNull(journey);
        ArgumentNullException.ThrowIfNull(knownStagingStationIds);
        if (journey.IsNonBusinessStop
            || journey.CurrentStopWorklist is not { Items.Count: 0 } worklist
            || !basketLoadedHere && !string.Equals(worklist.StopEndedReason, LoadsCompleted, StringComparison.Ordinal)
            || journey.CurrentLeg is not { } leg
            || !string.Equals(leg.LegType, "TO_PICKUP", StringComparison.Ordinal)
            || !string.Equals(leg.State, "ARRIVED", StringComparison.Ordinal)
            || !string.Equals(leg.StationId, worklist.StationId, StringComparison.Ordinal))
        {
            return false;
        }

        return WireToGateStagingPickupStop.StationFunctionOf(journey, worklist.StationId) is { } function
            ? !string.Equals(function, WireToGateStagingPickupStop.WireStaging, StringComparison.Ordinal)
            : !knownStagingStationIds.Contains(worklist.StationId, StringComparer.Ordinal);
    }

    /// <summary>
    /// The pickup stops after the one the vehicle stands at that are not done: the appended stops a press puts back to be
    /// dispatched (CP-0014, UC-002: the confirmation names how many).
    /// </summary>
    public static int PickupStopsAfterCurrent(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        return journey.CurrentLeg is { } current
            ? journey.UpcomingStopPlan!.Legs.Count(leg =>
                leg.Sequence > current.Sequence
                && string.Equals(leg.LegType, "TO_PICKUP", StringComparison.Ordinal)
                && !string.Equals(leg.State, "COMPLETED", StringComparison.Ordinal))
            : 0;
    }
}
