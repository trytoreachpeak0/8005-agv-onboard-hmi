namespace SQCD.Agv.Core;

/// <summary>
/// The business projection of the server-owned vehicle business state.
/// </summary>
/// <remarks>
/// <see cref="ChargingCycleState"/> and <see cref="LoadingPhase"/> arrived with protocol 2.0.0 and
/// are carried, not consumed: cargo holding is batch 7 and the charging cycle is batch 9. Carrying
/// them is what lets those batches read a value instead of re-shaping the projection.
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
    /// meaning. Exposing the set rather than a single value is what keeps "the legs disagree"
    /// distinguishable from "the legs carry no demand": collapsing both to null is how
    /// <see cref="WireToGateJourneySnapshot.HasConsistentDemand"/> would read a self-contradictory
    /// plan as consistent.
    /// </remarks>
    public IReadOnlyList<string> DemandIds =>
    [
        .. Legs.Select(leg => leg.DemandId).OfType<string>().Distinct(StringComparer.Ordinal)
    ];

    /// <summary>
    /// The one demand this plan is for, or null when the legs carry none or disagree.
    /// </summary>
    /// <remarks>
    /// Convenience over <see cref="DemandIds"/> for the callers that only ever see single-demand
    /// plans. <b>Null is ambiguous here</b> -- it means "none" and "more than one" alike -- so a
    /// caller deciding whether to allow an operator action must read <see cref="DemandIds"/>
    /// instead.
    /// </remarks>
    public string? DemandId => DemandIds.Count == 1 ? DemandIds[0] : null;
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
    /// Whether the worklist and the plan name the same demand.
    /// </summary>
    /// <remarks>
    /// <b>A plan whose legs name more than one demand is never consistent</b>, whatever the
    /// worklist says. Before protocol v2 that case could not arise -- the snapshot carried one
    /// top-level <c>demandId</c> -- and reading the derived
    /// <see cref="WireToGateUpcomingStopPlan.DemandId"/> as "no demand" when the legs disagree
    /// would let a self-contradictory plan through the guard that
    /// <see cref="CanAcceptSublot"/> stands behind. Unreachable while the control server emits at
    /// most two legs of one demand; reachable once waiting points (<c>FP-C4</c>) and chargers
    /// (<c>FP-C1</c>) put more legs in the plan.
    /// </remarks>
    public bool HasConsistentDemand
    {
        get
        {
            IReadOnlyList<string> planDemands = UpcomingStopPlan?.DemandIds ?? [];
            if (planDemands.Count > 1)
            {
                return false;
            }

            string? worklistDemand = CurrentStopWorklist?.Items.SingleOrDefault()?.DemandId;
            return worklistDemand is null
                || planDemands.Count == 0
                || string.Equals(worklistDemand, planDemands[0], StringComparison.Ordinal);
        }
    }

    public bool CanAcceptSublot =>
        VehicleBusinessState?.Readiness == "READY"
        && VehicleBusinessState.ManualChargingHold is false
        && VehicleBusinessState.BatteryState == "SUFFICIENT"
        && CurrentStopWorklist?.Items.Count == 1
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
