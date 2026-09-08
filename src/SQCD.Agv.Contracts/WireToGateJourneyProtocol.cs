namespace SQCD.Agv.Contracts;

/// <summary>
/// The C_TO_O vehicle business state snapshot, shaped by protocol v2's
/// <c>VehicleBusinessStateSnapshot.schema.json</c>.
/// </summary>
/// <remarks>
/// <c>ActivePurpose</c> is required by v2 and nullable: <c>TRANSPORT</c>, <c>CHARGING</c>,
/// <c>CLEARING_MAINTENANCE</c> or <c>IDLE_RETURN</c>. It is not optional on the wire -- the payload
/// object is <c>additionalProperties: false</c> with every property required, so omitting it is as
/// invalid as adding one. The envelope deserialiser runs with
/// <c>JsonUnmappedMemberHandling.Disallow</c>, so a record without this property would have thrown
/// on every snapshot the v2 server sends.
/// </remarks>
public sealed record VehicleBusinessStateSnapshotPayload(
    long VehicleBusinessStateRevision,
    string Readiness,
    string? ActivePurpose,
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

/// <summary>
/// The C_TO_O upcoming stop plan, shaped by protocol v2's
/// <c>UpcomingStopPlanSnapshot.schema.json</c>.
/// </summary>
/// <remarks>
/// <b>The top-level <c>demandId</c> is gone.</b> v2 raised <c>legs.maxItems</c> from 2 to 9, which
/// leaves one demand id per snapshot with no defined meaning, so the field moved into the leg. The
/// payload object forbids additional properties, so keeping it here would have made every snapshot
/// schema-invalid -- and, because the envelope deserialiser runs with
/// <c>JsonUnmappedMemberHandling.Disallow</c>, the v2 server's snapshots would have thrown instead.
/// </remarks>
public sealed record UpcomingStopPlanSnapshotPayload(
    long PlanRevision,
    IReadOnlyList<WireToGateMovementLeg> Legs);

/// <summary>
/// One movement leg, shaped by protocol v2's <c>UpcomingStopPlanSnapshot.schema.json</c>.
/// </summary>
/// <remarks>
/// Three properties are new in v2 and all three are required: <c>StopPurposeCategory</c>
/// (<c>BUSINESS</c>/<c>WAITING_POINT</c>/<c>CHARGER</c>, not nullable), <c>DemandId</c> (nullable,
/// moved down from the payload's top level) and <c>PublicStationFunction</c> (nullable, five
/// values). <c>LegType</c> became nullable and its enumeration is now
/// <c>TO_PICKUP</c>/<c>TO_DROPOFF</c> -- v1's <c>TO_GATE</c> is not a v2 value.
/// </remarks>
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
