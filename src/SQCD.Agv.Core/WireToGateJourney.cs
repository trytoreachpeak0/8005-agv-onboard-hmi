namespace SQCD.Agv.Core;

/// <summary>
/// The business projection of the server-owned vehicle business state.
/// </summary>
/// <remarks>
/// <see cref="ChargingCycleState"/> and <see cref="LoadingPhase"/> arrived with protocol 2.0.0.
/// <see cref="LoadingPhase"/> is read by the vehicle's display since batch 7-13
/// (<c>8005-agv-onboard-hmi#134</c>: cargo holding, the vehicle full, why loading closed) and never
/// decides anything here -- those judgements are the control server's. The charging cycle is
/// carried, not consumed, until batch 9.
/// </remarks>
public sealed record WireToGateVehicleBusinessState(
    long Revision,
    string Readiness,
    string? ActivePurpose,
    bool ManualChargingHold,
    string BatteryState,
    string ChargingCycleState,
    WireToGateLoadingPhase? LoadingPhase,
    IReadOnlyList<WireToGateBlockingFact> BlockingFacts,
    DateTimeOffset ObservedAt,
    string ContentSha256);

/// <summary>
/// Where this stop's loading stands, as projected from protocol 2.0.0's <c>loadingPhase</c>.
/// </summary>
public sealed record WireToGateLoadingPhase(
    string State,
    DateTimeOffset? CargoHoldingDeadlineAt,
    string? ClosedReason);

public sealed record WireToGateBlockingFact(
    string ReasonCode,
    string SubjectType,
    string? SubjectId);

public sealed record WireToGateWorklistItem(
    string DemandId,
    string TransportDemandKey,
    string Sublot,
    string WorkType,
    string StopRole,
    int ExpectedBasketCount);

/// <summary>
/// The business projection of the current stop worklist.
/// </summary>
/// <remarks>
/// <see cref="StationDepartureDeadlineAt"/> is carried through from protocol 2.0.0 rather than
/// consumed here. <c>null</c> means this stop has no deadline for continuing to load. The display
/// that counts down to it is <c>8005-agv-onboard-hmi#75</c>; this record is what gives that ticket
/// something to read.
/// </remarks>
public sealed record WireToGateCurrentStopWorklist(
    string StationId,
    long Revision,
    string? OperationSessionId,
    DateTimeOffset? StationDepartureDeadlineAt,
    IReadOnlyList<WireToGateWorklistItem> Items,
    string ContentSha256);

public sealed record WireToGateMovementLeg(
    string MovementLegId,
    string? LegType,
    string StopPurposeCategory,
    string? DemandId,
    string? PublicStationFunction,
    int Sequence,
    string StationId,
    string MapId,
    string State);

public sealed record WireToGateUpcomingStopPlan(
    long Revision,
    IReadOnlyList<WireToGateMovementLeg> Legs,
    string ContentSha256)
{
    /// <summary>
    /// The distinct demand ids the legs carry, in leg order and without nulls.
    /// </summary>
    /// <remarks>
    /// Protocol v2 moved <c>demandId</c> out of the snapshot's top level and into the leg, because
    /// <c>legs.maxItems</c> went from 2 to 9 and one demand id for a nine-leg plan has no defined
    /// meaning. There is deliberately no single-value convenience: one read "none" and "more than
    /// one" alike as null, and batch 7-13 (<c>8005-agv-onboard-hmi#134</c>) removed it once it had
    /// no production caller left.
    /// </remarks>
    public IReadOnlyList<string> DemandIds =>
    [
        .. Legs.Select(leg => leg.DemandId).OfType<string>().Distinct(StringComparer.Ordinal)
    ];
}

public sealed record WireToGateJourneySnapshot(
    WireToGateVehicleBusinessState? VehicleBusinessState,
    WireToGateCurrentStopWorklist? CurrentStopWorklist,
    WireToGateUpcomingStopPlan? UpcomingStopPlan,
    DateTimeOffset UpdatedAt)
{
    public static WireToGateJourneySnapshot Empty { get; } = new(
        null,
        null,
        null,
        DateTimeOffset.MinValue);

    public bool HasAuthoritativeWorklist => CurrentStopWorklist is not null;

    /// <summary>
    /// The leg the vehicle is on: the first leg, by <c>sequence</c>, that is not <c>COMPLETED</c>.
    /// </summary>
    /// <remarks>
    /// The one reading of "the current leg" on this end (batch 8-22, <c>8005-agv-onboard-hmi#217</c>): the
    /// direction shown while no worklist has arrived and the waiting-point judgement below both take it
    /// from here, so the two can never disagree about which leg the vehicle is on.
    /// </remarks>
    public WireToGateMovementLeg? CurrentLeg => UpcomingStopPlan?.Legs
        .OrderBy(leg => leg.Sequence)
        .FirstOrDefault(leg => leg.State != "COMPLETED");

    /// <summary>
    /// Whether the vehicle is on an idle return to a waiting point: the current leg's
    /// <c>stopPurposeCategory</c> is <c>WAITING_POINT</c>, or the business state's <c>activePurpose</c> is
    /// <c>IDLE_RETURN</c> (batch 8-22, <c>8005-agv-onboard-hmi#217</c>; vector
    /// <c>CV-WAITING-POINT-IDLE-RETURN</c>, <c>TREAT_WAITING_POINT_AS_NON_BUSINESS_STOP</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both facts are the server's, read as sent; nothing is inferred from a station id or a station name.
    /// Either one is enough, because the two snapshots arrive separately and the vehicle must not be
    /// offered a load in the window between them.
    /// </para>
    /// <para>
    /// <b>Nothing local remembers it.</b> A plan with a business leg and a <c>TRANSPORT</c> purpose puts this
    /// back to false on the snapshot that carries them, with no further condition (<c>REQ-0293</c>: the
    /// vehicle takes part in transport again once the idle return is released).
    /// </para>
    /// </remarks>
    public bool IsWaitingPointStop =>
        CurrentLeg?.StopPurposeCategory == "WAITING_POINT"
        || VehicleBusinessState?.ActivePurpose == "IDLE_RETURN";

    /// <summary>
    /// A worklist with items at a waiting point: a contradiction on the server's side, since a waiting
    /// point carries no business role (<c>REQ-0289</c>). Nothing is loaded; the vehicle logs it.
    /// </summary>
    public bool HasWorklistItemsAtWaitingPoint =>
        IsWaitingPointStop && CurrentStopWorklist?.Items.Count > 0;

    /// <summary>
    /// Whether every demand the worklist names is one the plan names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A set relation since batch 7-13 (<c>8005-agv-onboard-hmi#134</c>). A stop may carry up to
    /// eight demands and a plan up to nine legs, so "the worklist's demand equals the plan's" no
    /// longer has a meaning; what still does is that nothing on this stop's worklist is absent from
    /// the journey the server planned. The plan keeps its completed legs and the one the vehicle is at
    /// (control server <c>JourneyPlanBuilder</c>), so a demand being worked here is always on it.
    /// </para>
    /// <para>
    /// A plan whose legs carry no demand -- waiting points and chargers only -- lets the worklist
    /// through, as before; a worklist with no item is trivially consistent and is refused by
    /// <see cref="CanAcceptSublot"/> instead. Nothing here reads a single item or a single demand,
    /// so no size of either can throw.
    /// </para>
    /// <para>
    /// That pass-through is not what keeps a waiting point from being loaded: since batch 8-22
    /// (<c>8005-agv-onboard-hmi#217</c>, <c>NEVER_LOAD_AT_WAITING_POINT</c>) <see cref="CanAcceptSublot"/> refuses
    /// on <see cref="IsWaitingPointStop"/> first. This property stays a statement about demands only.
    /// </para>
    /// </remarks>
    public bool HasConsistentDemand
    {
        get
        {
            IReadOnlyList<string> planDemands = UpcomingStopPlan?.DemandIds ?? [];
            IReadOnlyList<WireToGateWorklistItem> items = CurrentStopWorklist?.Items ?? [];
            return planDemands.Count == 0
                || items.All(item => planDemands.Contains(item.DemandId, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Whether this journey projection admits a sublot entry at all.
    /// </summary>
    /// <remarks>
    /// Never at a waiting point, whatever worklist arrived with it (<c>NEVER_LOAD_AT_WAITING_POINT</c>,
    /// <c>8005-agv-onboard-hmi#217</c>).
    /// </remarks>
    public bool CanAcceptSublot =>
        !IsWaitingPointStop
        && VehicleBusinessState?.Readiness == "READY"
        && VehicleBusinessState.ManualChargingHold is false
        && VehicleBusinessState.BatteryState == "SUFFICIENT"
        && CurrentStopWorklist?.Items.Count >= 1
        && HasConsistentDemand;

    public bool CanAcceptSublotAt(DateTimeOffset now, TimeSpan maxAge)
    {
        if (maxAge <= TimeSpan.Zero || !CanAcceptSublot)
        {
            return false;
        }

        DateTimeOffset[] observations = new[]
        {
            UpdatedAt,
            VehicleBusinessState?.ObservedAt ?? DateTimeOffset.MinValue
        };
        return observations.All(observed => observed != DateTimeOffset.MinValue
            && observed <= now
            && now - observed <= maxAge);
    }
}
