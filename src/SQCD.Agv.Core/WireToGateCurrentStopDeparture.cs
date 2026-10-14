namespace SQCD.Agv.Core;

/// <summary>
/// One press of 「出发」 at a staging pickup stop (protocol 4.0.0 candidate, CP-0012, 8005-agv-onboard-hmi#349), as the
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

    /// <summary>Not a staging pickup stop: the entry is not shown at all. Machine pickups keep their screen.</summary>
    NotAtStagingPickupStop,

    /// <summary>The session is not Ready, or a severe safety fault is latched.</summary>
    SessionNotReady,

    /// <summary>No operator is signed in on this vehicle.</summary>
    NoOperator,

    /// <summary>A departure was sent and its result has not come back.</summary>
    AwaitingResult,

    /// <summary>A sublot was submitted and the server has not answered it with a load or a rejection yet.</summary>
    EntryInProgress,

    /// <summary>A slot operation runs, is unsettled, or its result is not yet acknowledged.</summary>
    SlotOperationInProgress,

    /// <summary>A load correction or a load cancellation is open.</summary>
    CorrectionOrCancellationOpen
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
    string OperatorId);

/// <summary>
/// The 「出发」 entry as the business service offers it.
/// </summary>
/// <param name="Shown">Whether the entry is on screen at all: only at a staging pickup stop.</param>
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
