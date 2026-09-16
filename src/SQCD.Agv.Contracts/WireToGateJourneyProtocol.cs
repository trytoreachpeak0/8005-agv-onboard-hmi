namespace SQCD.Agv.Contracts;

/// <summary>
/// The C_TO_O vehicle business state snapshot, shaped by protocol v2's
/// <c>VehicleBusinessStateSnapshot.schema.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ActivePurpose</c> is required by v2 and nullable: <c>TRANSPORT</c>, <c>CHARGING</c>,
/// <c>CLEARING_MAINTENANCE</c> or <c>IDLE_RETURN</c>. It is not optional on the wire -- the payload
/// object is <c>additionalProperties: false</c> with every property required, so omitting it is as
/// invalid as adding one. The envelope deserialiser runs with
/// <c>JsonUnmappedMemberHandling.Disallow</c>, so a record without this property would have thrown
/// on every snapshot the v2 server sends.
/// </para>
/// <para>
/// <b>2.0.0 added three things, and this end parses all of them without using any.</b>
/// <c>BatteryState</c> gained <c>MANDATORY_CHARGE</c>; <c>ChargingCycleState</c> is new with seven
/// values; <c>LoadingPhase</c> is new, required and nullable as a whole. The behaviour behind them
/// is batches 7 and 9 -- cargo holding and the charging cycle -- so nothing here reads them yet.
/// They are parsed to the full extent the schema declares anyway, because the alternative is to
/// accept only what the control server happens to send during batch 5, and
/// <c>8005-agv-onboard-hmi#38</c> is what that costs: an inbound check narrower than the contract,
/// locking a live session on a payload the protocol calls legal.
/// </para>
/// </remarks>
public sealed record VehicleBusinessStateSnapshotPayload(
    long VehicleBusinessStateRevision,
    string Readiness,
    string? ActivePurpose,
    bool ManualChargingHold,
    string BatteryState,
    string ChargingCycleState,
    WireToGateLoadingPhasePayload? LoadingPhase,
    IReadOnlyList<WireToGateBlockingFactPayload> BlockingFacts,
    DateTimeOffset ObservedAt);

/// <summary>
/// Where this stop's loading stands, new in protocol 2.0.0.
/// </summary>
/// <remarks>
/// All three properties are required. <c>ClosedReason</c> is constrained by the schema's
/// <c>if/then/else</c>: a string exactly when <c>State</c> is <c>CLOSED</c>, and <c>null</c>
/// otherwise. The whole object is nullable on the snapshot, which is how "this vehicle has no
/// loading phase right now" is said.
/// </remarks>
public sealed record WireToGateLoadingPhasePayload(
    string State,
    DateTimeOffset? CargoHoldingDeadlineAt,
    string? ClosedReason);

public sealed record WireToGateBlockingFactPayload(
    string ReasonCode,
    string SubjectType,
    string? SubjectId);

/// <summary>
/// The C_TO_O current stop worklist, shaped by the 2.0.0 candidate's
/// <c>CurrentStopWorklistSnapshot.schema.json</c>.
/// </summary>
/// <remarks>
/// <c>StationDepartureDeadlineAt</c> is new in 2.0.0 and, like every other property here, required
/// and nullable. <c>null</c> is not "absent": it says this stop carries no deadline for continuing
/// to load, which the vehicle shows as no countdown at all rather than as an expired one. The
/// payload object is <c>additionalProperties: false</c> with every property required, so a record
/// without it would throw on every worklist the 2.0.0 server sends.
/// </remarks>
public sealed record CurrentStopWorklistSnapshotPayload(
    string StationId,
    long WorklistRevision,
    string? OperationSessionId,
    DateTimeOffset? StationDepartureDeadlineAt,
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
