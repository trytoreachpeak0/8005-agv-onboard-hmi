namespace SQCD.Agv.Contracts;

public sealed record VehicleBusinessStateSnapshotPayload(
    long VehicleBusinessStateRevision,
    string Readiness,
    bool ManualChargingHold,
    string BatteryState,
    IReadOnlyList<WireToGateBlockingFactPayload> BlockingFacts,
    DateTimeOffset ObservedAt);

public sealed record WireToGateBlockingFactPayload(
    string ReasonCode,
    string SubjectType,
    string? SubjectId);

public sealed record CurrentStopWorklistSnapshotPayload(
    string StationId,
    long WorklistRevision,
    string? OperationSessionId,
    IReadOnlyList<WireToGateWorklistItem> Items);

public sealed record WireToGateWorklistItem(
    string DemandId,
    string TransportDemandKey,
    string Sublot,
    string WorkType,
    string StopRole,
    int ExpectedBasketCount);

public sealed record UpcomingStopPlanSnapshotPayload(
    long PlanRevision,
    string? DemandId,
    IReadOnlyList<WireToGateMovementLeg> Legs);

public sealed record WireToGateMovementLeg(
    string MovementLegId,
    string LegType,
    int Sequence,
    string StationId,
    string MapId,
    string State);
