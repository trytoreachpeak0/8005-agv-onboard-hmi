namespace SQCD.Agv.Core;

public sealed record WireToGateVehicleBusinessState(
    long Revision,
    string Readiness,
    bool ManualChargingHold,
    string BatteryState,
    IReadOnlyList<WireToGateBlockingFact> BlockingFacts,
    DateTimeOffset ObservedAt,
    string ContentSha256);

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

public sealed record WireToGateCurrentStopWorklist(
    string StationId,
    long Revision,
    string? OperationSessionId,
    IReadOnlyList<WireToGateWorklistItem> Items,
    string ContentSha256);

public sealed record WireToGateMovementLeg(
    string MovementLegId,
    string LegType,
    int Sequence,
    string StationId,
    string MapId,
    string State);

public sealed record WireToGateUpcomingStopPlan(
    long Revision,
    string? DemandId,
    IReadOnlyList<WireToGateMovementLeg> Legs,
    string ContentSha256);

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

    public bool HasConsistentDemand
    {
        get
        {
            string? worklistDemand = CurrentStopWorklist?.Items.SingleOrDefault()?.DemandId;
            return worklistDemand is null
                || UpcomingStopPlan?.DemandId is null
                || string.Equals(worklistDemand, UpcomingStopPlan.DemandId, StringComparison.Ordinal);
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
