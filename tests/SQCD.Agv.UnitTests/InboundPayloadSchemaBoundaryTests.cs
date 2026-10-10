using System.Text.Json;
using SQCD.Agv.Contracts;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// Every boundary variant the 2.0.0 candidate's schemas declare legal for the inbound payloads this
/// ticket reshapes is accepted, driven through the same deserialiser the session client uses and
/// from real envelope bytes rather than from constructed records.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why bytes and not records.</b> The payload records are closed schemas --
/// <c>WireToGateProtocolSerializer</c> runs with <c>JsonUnmappedMemberHandling.Disallow</c> -- so a
/// record missing one property does not read a payload leniently, it throws on every message
/// carrying that property. Constructing the record in a test proves nothing about that: only a
/// round trip from the bytes the server really sends does.
/// </para>
/// <para>
/// <b>Why every variant and not one each.</b> <c>8005-agv-onboard-hmi#38</c> is the precedent: an
/// inbound check stricter than the schema locked a live session twice, on payloads the protocol
/// declared legal. The control server emits a narrow subset of these values during batch 5 --
/// <c>chargingCycleState</c> and most of <c>loadingPhase</c> are batch 7 and 9 behaviour -- and
/// accepting only that subset is precisely how the next lockup is built. The schema, not today's
/// sender, is the contract.
/// </para>
/// </remarks>
public sealed class InboundPayloadSchemaBoundaryTests
{
    private const string AgvId = "AGV-001";

    /// <summary>
    /// <c>stationDepartureDeadlineAt</c> is required and nullable: <c>null</c> means this stop has
    /// no deadline for continuing to load, and the vehicle shows no countdown.
    /// </summary>
    [Fact]
    public void AWorklistSnapshotIsAcceptedWithAndWithoutADepartureDeadline()
    {
        CurrentStopWorklistSnapshotPayload withDeadline = Inbound<CurrentStopWorklistSnapshotPayload>(
            "CurrentStopWorklistSnapshot",
            WorklistPayload("\"2026-09-16T08:30:00Z\""));

        Assert.Equal(
            new DateTimeOffset(2026, 9, 16, 8, 30, 0, TimeSpan.Zero),
            withDeadline.StationDepartureDeadlineAt);

        CurrentStopWorklistSnapshotPayload withoutDeadline =
            Inbound<CurrentStopWorklistSnapshotPayload>(
                "CurrentStopWorklistSnapshot",
                WorklistPayload("null"));

        Assert.Null(withoutDeadline.StationDepartureDeadlineAt);
        Assert.Equal("STATION-01", withoutDeadline.StationId);
        Assert.Single(withoutDeadline.Items);
    }

    /// <summary>
    /// <c>workType</c> is <c>TransportTaskType</c>, the six MES literals; every one of them passes
    /// the session client's own inbound check, not only the deserialiser.
    /// </summary>
    /// <remarks>
    /// Until batch 6 the inbound check accepted <c>WIRE_TO_GATE</c> alone, so the first
    /// <c>STAGING_TO_WIRE</c> journey the control server sends
    /// (<c>trytoreachpeak0/8005-agv-control-server#163</c>) would have been answered
    /// <c>PROTOCOL_SCHEMA_INVALID</c> -- the <c>8005-agv-onboard-hmi#38</c> lockup again. The
    /// deserialiser alone never refused them: the narrowing lived in
    /// <c>WireToGateSessionClient.ValidateCurrentStopWorklist</c>, which is why this goes through it.
    /// </remarks>
    [Theory]
    [InlineData("DIE_TO_WIRE_STAGING")]
    [InlineData("DIE_TO_OVEN")]
    [InlineData("WIRE_TO_GATE")]
    [InlineData("WIRE_TO_OPTICAL")]
    [InlineData("STAGING_TO_WIRE")]
    [InlineData("WIRE_TO_NITROGEN")]
    public void EveryWorkTypeTheSchemaDeclaresIsAccepted(string workType)
    {
        CurrentStopWorklistSnapshotPayload payload = Inbound<CurrentStopWorklistSnapshotPayload>(
            "CurrentStopWorklistSnapshot",
            WorklistPayload("null", workType));

        WireToGateSessionClient.ValidateCurrentStopWorklist(payload);

        Assert.Equal(workType, Assert.Single(payload.Items).WorkType);
    }

    /// <summary>
    /// A value outside the six is still <c>PROTOCOL_SCHEMA_INVALID</c>: that is the schema's own
    /// enum, not a business narrowing, and widening the check stops at it.
    /// </summary>
    [Theory]
    [InlineData("WIRE_TO_OVEN")]
    [InlineData("wire_to_gate")]
    [InlineData("")]
    public void AWorkTypeOutsideTheSchemaIsRefused(string workType)
    {
        CurrentStopWorklistSnapshotPayload payload = Inbound<CurrentStopWorklistSnapshotPayload>(
            "CurrentStopWorklistSnapshot",
            WorklistPayload("null", workType));

        InvalidDataException refused = Assert.Throws<InvalidDataException>(
            () => WireToGateSessionClient.ValidateCurrentStopWorklist(payload));
        Assert.Equal("PROTOCOL_SCHEMA_INVALID", refused.Message);
    }

    /// <summary>
    /// One stop's worklist that mixes the four same-direction task types batch 10 opens with
    /// <c>WIRE_TO_GATE</c> passes the session client's own inbound check, every item kept as sent
    /// (batch 10-03, <c>8005-agv-onboard-hmi#289</c>).
    /// </summary>
    /// <remarks>
    /// The single-item theory above proves each literal on its own. Batch 10 is the first time the
    /// control server can put several of them at one AREA stop, and the check runs over every item,
    /// so a narrowing on any one of them would refuse the whole snapshot -- the
    /// <c>8005-agv-onboard-hmi#38</c> lockup again.
    /// </remarks>
    [Fact]
    public void AWorklistMixingTheFourSameDirectionTypesWithWireToGateIsAccepted()
    {
        string[] workTypes = ["WIRE_TO_GATE", "DIE_TO_WIRE_STAGING", "DIE_TO_OVEN", "WIRE_TO_OPTICAL", "WIRE_TO_NITROGEN"];
        CurrentStopWorklistSnapshotPayload payload = Inbound<CurrentStopWorklistSnapshotPayload>(
            "CurrentStopWorklistSnapshot",
            WorklistPayloadWithWorkTypes(workTypes));

        WireToGateSessionClient.ValidateCurrentStopWorklist(payload);

        Assert.Equal(workTypes, payload.Items.Select(item => item.WorkType));
    }

    /// <summary>
    /// <c>items.maxItems</c> is 8: a worklist carrying eight items passes the session client's own
    /// inbound check, in the order the server sent them (batch 7-13, <c>8005-agv-onboard-hmi#134</c>).
    /// </summary>
    /// <remarks>
    /// Until batch 7 the check refused anything above one item, so the first multi-demand stop the
    /// control server sent (<c>8005-agv-control-server#211</c>) would have been answered
    /// <c>PROTOCOL_SCHEMA_INVALID</c> and the session locked -- the <c>8005-agv-onboard-hmi#38</c>
    /// lockup on a schema-legal payload.
    /// </remarks>
    [Fact]
    public void AWorklistOfEightItemsIsAccepted()
    {
        CurrentStopWorklistSnapshotPayload payload = Inbound<CurrentStopWorklistSnapshotPayload>(
            "CurrentStopWorklistSnapshot",
            WorklistPayloadWithItems(8));

        WireToGateSessionClient.ValidateCurrentStopWorklist(payload);

        Assert.Equal(
            [.. Enumerable.Range(1, 8).Select(n => $"SL-{n}")],
            payload.Items.Select(item => item.Sublot));
    }

    /// <summary>
    /// Nine items is above the schema's own <c>maxItems</c>, and stays <c>PROTOCOL_SCHEMA_INVALID</c>.
    /// </summary>
    [Fact]
    public void AWorklistOfNineItemsIsRefused()
    {
        CurrentStopWorklistSnapshotPayload payload = Inbound<CurrentStopWorklistSnapshotPayload>(
            "CurrentStopWorklistSnapshot",
            WorklistPayloadWithItems(9));

        InvalidDataException refused = Assert.Throws<InvalidDataException>(
            () => WireToGateSessionClient.ValidateCurrentStopWorklist(payload));
        Assert.Equal("PROTOCOL_SCHEMA_INVALID", refused.Message);
    }

    /// <summary>
    /// <c>legs.maxItems</c> is 9: a nine-leg plan mixing business legs, a waiting point and a charger
    /// passes the inbound check.
    /// </summary>
    [Fact]
    public void APlanOfNineLegsIsAccepted()
    {
        UpcomingStopPlanSnapshotPayload payload = Inbound<UpcomingStopPlanSnapshotPayload>(
            "UpcomingStopPlanSnapshot",
            PlanPayloadWithLegs(9));

        WireToGateSessionClient.ValidateUpcomingStopPlan(payload);

        Assert.Equal(9, payload.Legs.Count);
    }

    /// <summary>
    /// Ten legs is above the schema's own <c>maxItems</c>, and stays <c>PROTOCOL_SCHEMA_INVALID</c>.
    /// </summary>
    [Fact]
    public void APlanOfTenLegsIsRefused()
    {
        UpcomingStopPlanSnapshotPayload payload = Inbound<UpcomingStopPlanSnapshotPayload>(
            "UpcomingStopPlanSnapshot",
            PlanPayloadWithLegs(10));

        InvalidDataException refused = Assert.Throws<InvalidDataException>(
            () => WireToGateSessionClient.ValidateUpcomingStopPlan(payload));
        Assert.Equal("PROTOCOL_SCHEMA_INVALID", refused.Message);
    }

    private static string WorklistPayloadWithWorkTypes(IReadOnlyList<string> workTypes) =>
        $$"""
        {"stationId":"STATION-01","worklistRevision":7,
        "operationSessionId":"00000000-0000-4000-8000-0000000000aa",
        "stationDepartureDeadlineAt":null,
        "items":[{{string.Join(",", workTypes.Select((workType, index) =>
            $$"""
            {"demandId":"00000000-0000-4000-8000-0000000004{{index + 1:D2}}",
            "transportDemandKey":"TDK-{{index + 1}}","sublot":"SL-{{index + 1}}","workType":"{{workType}}",
            "stopRole":"PICKUP","expectedBasketCount":1}
            """))}}]}
        """;

    private static string WorklistPayloadWithItems(int count) =>
        $$"""
        {"stationId":"STATION-01","worklistRevision":7,
        "operationSessionId":"00000000-0000-4000-8000-0000000000aa",
        "stationDepartureDeadlineAt":null,
        "items":[{{string.Join(",", Enumerable.Range(1, count).Select(n =>
            $$"""
            {"demandId":"00000000-0000-4000-8000-0000000001{{n:D2}}",
            "transportDemandKey":"TDK-{{n}}","sublot":"SL-{{n}}","workType":"WIRE_TO_GATE",
            "stopRole":"PICKUP","expectedBasketCount":1}
            """))}}]}
        """;

    /// <summary>
    /// A plan of <paramref name="count"/> legs, sent in reverse <c>sequence</c> order: every third leg
    /// is a waiting point and every fifth a charger, both with no leg type and no demand.
    /// </summary>
    private static string PlanPayloadWithLegs(int count) =>
        $$"""
        {"planRevision":4,
        "legs":[{{string.Join(",", Enumerable.Range(1, count).Reverse().Select(n =>
            {
                string category = n % 5 == 0 ? "CHARGER" : n % 3 == 0 ? "WAITING_POINT" : "BUSINESS";
                string legType = category == "BUSINESS" ? (n % 2 == 0 ? "\"TO_DROPOFF\"" : "\"TO_PICKUP\"") : "null";
                string demandId = category == "BUSINESS" ? $"\"00000000-0000-4000-8000-0000000002{n:D2}\"" : "null";
                return $$"""
                    {"movementLegId":"00000000-0000-4000-8000-0000000003{{n:D2}}",
                    "legType":{{legType}},"stopPurposeCategory":"{{category}}","demandId":{{demandId}},
                    "publicStationFunction":null,"sequence":{{n}},"stationId":"ST-{{n}}",
                    "mapId":"MAP-26","state":"PLANNED"}
                    """;
            }))}}]}
        """;

    private static string WorklistPayload(string deadline, string workType = "WIRE_TO_GATE") =>
        $$"""
        {"stationId":"STATION-01","worklistRevision":7,
        "operationSessionId":"00000000-0000-4000-8000-0000000000aa",
        "stationDepartureDeadlineAt":{{deadline}},
        "items":[{"demandId":"00000000-0000-4000-8000-0000000000bb",
        "transportDemandKey":"TDK-1","sublot":"SL-1","workType":"{{workType}}",
        "stopRole":"PICKUP","expectedBasketCount":2}]}
        """;

    /// <summary>
    /// <c>expectedSublots</c> replaced <c>expectedSublot</c> and <c>demandId</c>: a set of 1 to 8
    /// unique values, and both ends of that range are legal.
    /// </summary>
    /// <remarks>
    /// One element is what a one-demand dispatch produces today, and reading the payload as if that
    /// were the only case is how the eight-element form becomes a crash later. Both are parsed here
    /// as the same shape.
    /// </remarks>
    [Fact]
    public void ASublotEntryRequestIsAcceptedWithOneExpectedSublotAndWithEight()
    {
        SublotEntryRequestedPayload single = Inbound<SublotEntryRequestedPayload>(
            "SublotEntryRequested",
            EntryRequestPayload("[\"SL-1\"]"));

        Assert.Equal(["SL-1"], single.ExpectedSublots);

        string eight = string.Join(",", Enumerable.Range(1, 8).Select(n => $"\"SL-{n}\""));
        SublotEntryRequestedPayload full = Inbound<SublotEntryRequestedPayload>(
            "SublotEntryRequested",
            EntryRequestPayload($"[{eight}]"));

        Assert.Equal([.. Enumerable.Range(1, 8).Select(n => $"SL-{n}")], full.ExpectedSublots);
        Assert.Equal("STATION-01", full.StationId);
        Assert.Equal(7, full.WorklistRevision);
    }

    private static string EntryRequestPayload(string expectedSublots) =>
        $$"""
        {"operationSessionId":"00000000-0000-4000-8000-0000000000aa",
        "stationId":"STATION-01","worklistRevision":7,
        "expectedSublots":{{expectedSublots}},
        "entryMethods":["SCANNER","KEYBOARD"],"expiresOnRevisionChange":true}
        """;

    /// <summary>
    /// <c>SublotRejected</c> gained a required <c>rejectedSublot</c>, and its <c>demandId</c>
    /// became nullable.
    /// </summary>
    /// <remarks>
    /// Both halves are the same change seen from two sides: a sublot outside the dispatch scope is
    /// rejected with <c>SUBLOT_NOT_IN_DISPATCH_SCOPE</c>, and there is no demand to name it
    /// against -- so <c>demandId</c> has to be able to say "none", and the message has to say which
    /// sublot was refused some other way.
    /// </remarks>
    [Fact]
    public void ASublotRejectionIsAcceptedWithAndWithoutADemandId()
    {
        SublotRejectedPayload named = Inbound<SublotRejectedPayload>(
            "SublotRejected",
            RejectionPayload("\"00000000-0000-4000-8000-0000000000bb\"", "SUBLOT_MISMATCH"));

        Assert.Equal("00000000-0000-4000-8000-0000000000bb", named.DemandId);
        Assert.Equal("SL-9", named.RejectedSublot);

        SublotRejectedPayload outOfScope = Inbound<SublotRejectedPayload>(
            "SublotRejected",
            RejectionPayload("null", "SUBLOT_NOT_IN_DISPATCH_SCOPE"));

        Assert.Null(outOfScope.DemandId);
        Assert.Equal("SL-9", outOfScope.RejectedSublot);
        Assert.Equal("SUBLOT_NOT_IN_DISPATCH_SCOPE", outOfScope.Problem.ReasonCode);
    }

    private static string RejectionPayload(string demandId, string reasonCode) =>
        $$"""
        {"demandId":{{demandId}},
        "operationSessionId":"00000000-0000-4000-8000-0000000000aa",
        "problem":{"reasonCode":"{{reasonCode}}","fieldPath":null,"displayMessage":null},
        "currentWorklistRevision":7,"rejectedSublot":"SL-9"}
        """;

    /// <summary>
    /// <c>batteryState</c> gained <c>MANDATORY_CHARGE</c>; all four values parse.
    /// </summary>
    [Theory]
    [InlineData("SUFFICIENT")]
    [InlineData("LOW")]
    [InlineData("UNKNOWN")]
    [InlineData("MANDATORY_CHARGE")]
    public void EveryBatteryStateTheSchemaDeclaresIsAccepted(string batteryState)
    {
        VehicleBusinessStateSnapshotPayload payload = Inbound<VehicleBusinessStateSnapshotPayload>(
            "VehicleBusinessStateSnapshot",
            BusinessStatePayload(batteryState: batteryState));

        Assert.Equal(batteryState, payload.BatteryState);
    }

    /// <summary>
    /// <c>chargingCycleState</c> is new, required, and has seven values; all seven parse.
    /// </summary>
    /// <remarks>
    /// The control server emits only a few of these during batch 5 -- the charging cycle itself is
    /// batch 9 -- and accepting only those would be the narrowing this class exists to prevent.
    /// </remarks>
    [Theory]
    [InlineData("NOT_CHARGING")]
    [InlineData("ALLOCATED")]
    [InlineData("EN_ROUTE")]
    [InlineData("CHARGING")]
    [InlineData("COMPLETE")]
    [InlineData("UNABLE_TO_CHARGE")]
    [InlineData("UNKNOWN")]
    public void EveryChargingCycleStateTheSchemaDeclaresIsAccepted(string chargingCycleState)
    {
        VehicleBusinessStateSnapshotPayload payload = Inbound<VehicleBusinessStateSnapshotPayload>(
            "VehicleBusinessStateSnapshot",
            BusinessStatePayload(chargingCycleState: chargingCycleState));

        Assert.Equal(chargingCycleState, payload.ChargingCycleState);
    }

    /// <summary>
    /// <c>loadingPhase</c> is new, required, and nullable as a whole object; the whole-object
    /// <c>null</c> parses.
    /// </summary>
    [Fact]
    public void AnAbsentLoadingPhaseIsAccepted()
    {
        VehicleBusinessStateSnapshotPayload payload = Inbound<VehicleBusinessStateSnapshotPayload>(
            "VehicleBusinessStateSnapshot",
            BusinessStatePayload(loadingPhase: "null"));

        Assert.Null(payload.LoadingPhase);
    }

    /// <summary>
    /// Every <c>loadingPhase.state</c> parses, with the <c>closedReason</c> the schema's
    /// <c>if/then/else</c> requires for it and with a deadline both present and absent.
    /// </summary>
    /// <remarks>
    /// The schema makes <c>closedReason</c> a string exactly when <c>state</c> is <c>CLOSED</c> and
    /// <c>null</c> otherwise, so the legal combinations are enumerated rather than crossed: a
    /// non-<c>CLOSED</c> phase carrying a reason is not a variant this end must accept, it is a
    /// payload the protocol forbids.
    /// </remarks>
    [Theory]
    [InlineData("LOADING", "null", "null")]
    [InlineData("LOADING", "null", "\"2026-09-16T09:00:00Z\"")]
    [InlineData("CARGO_HOLDING_WAIT", "null", "\"2026-09-16T09:00:00Z\"")]
    [InlineData("CARGO_HOLDING_WAIT", "null", "null")]
    [InlineData("VEHICLE_FULL", "null", "null")]
    [InlineData("CLOSED", "\"VEHICLE_FULL\"", "null")]
    [InlineData("CLOSED", "\"CARGO_HOLDING_TIMEOUT\"", "null")]
    [InlineData("CLOSED", "\"WAITING_STATION_YIELD\"", "null")]
    [InlineData("CLOSED", "\"PLANNED_LOADING_COMPLETE\"", "\"2026-09-16T09:00:00Z\"")]
    public void EveryLoadingPhaseTheSchemaDeclaresIsAccepted(
        string state,
        string closedReason,
        string deadline)
    {
        VehicleBusinessStateSnapshotPayload payload = Inbound<VehicleBusinessStateSnapshotPayload>(
            "VehicleBusinessStateSnapshot",
            BusinessStatePayload(loadingPhase:
                $$"""
                {"state":"{{state}}","cargoHoldingDeadlineAt":{{deadline}},
                "closedReason":{{closedReason}}}
                """));

        WireToGateLoadingPhasePayload phase = Assert.IsType<WireToGateLoadingPhasePayload>(
            payload.LoadingPhase);
        Assert.Equal(state, phase.State);
        Assert.Equal(deadline is "null" ? null : DateTimeOffset.Parse(deadline.Trim('"'),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal),
            phase.CargoHoldingDeadlineAt);
        Assert.Equal(closedReason is "null" ? null : closedReason.Trim('"'), phase.ClosedReason);
    }

    private static string BusinessStatePayload(
        string batteryState = "SUFFICIENT",
        string chargingCycleState = "NOT_CHARGING",
        string loadingPhase = "null") =>
        $$"""
        {"vehicleBusinessStateRevision":3,"readiness":"READY","activePurpose":"TRANSPORT",
        "manualChargingHold":false,"batteryState":"{{batteryState}}",
        "chargingCycleState":"{{chargingCycleState}}","loadingPhase":{{loadingPhase}},
        "blockingFacts":[],"observedAt":"2026-09-16T00:00:00Z"}
        """;

    /// <summary>
    /// All three recovery messages the server opens a session with carry a required, nullable
    /// <c>slotOperationAttemptId</c>, and both values parse on each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the protocol half of the gap <c>8005-agv-control-server#5</c> recorded on the MVP
    /// line: the server demanded an attempt id inside <c>LoadCompensationRequested</c> and never
    /// told the vehicle which one, so the vehicle could not ask for the compensation the server was
    /// waiting for.
    /// </para>
    /// <para>
    /// <c>null</c> is a real value here, not an omission -- a recovery session opened before any
    /// loading started has no slot operation attached -- and it is the one a fixed constant in a
    /// test double hides, which is why it is asserted on every one of the three.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("ExceptionRecoverySessionOpened")]
    [InlineData("ExceptionRecoverySessionSnapshot")]
    [InlineData("RecoveryActionAccepted")]
    public void EveryRecoveryMessageCarriesANullableSlotOperationAttemptId(string messageType)
    {
        const string attemptId = "00000000-0000-4000-8000-0000000000cc";

        Assert.Equal(attemptId, AttemptIdOf(messageType, $"\"{attemptId}\""));
        Assert.Null(AttemptIdOf(messageType, "null"));
    }

    private static string? AttemptIdOf(string messageType, string attemptId) => messageType switch
    {
        "ExceptionRecoverySessionOpened" => Inbound<ExceptionRecoverySessionOpenedPayload>(
            messageType, RecoverySessionOpenedPayload(attemptId)).SlotOperationAttemptId,
        "ExceptionRecoverySessionSnapshot" => Inbound<ExceptionRecoverySessionSnapshotPayload>(
            messageType, RecoverySessionSnapshotPayload(attemptId)).SlotOperationAttemptId,
        "RecoveryActionAccepted" => Inbound<RecoveryActionAcceptedPayload>(
            messageType, RecoveryActionAcceptedPayload(attemptId)).SlotOperationAttemptId,
        _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, null)
    };

    private static string RecoverySessionOpenedPayload(string attemptId) =>
        $$"""
        {"requestId":"00000000-0000-4000-8000-000000000011",
        "exceptionRecoverySessionId":"00000000-0000-4000-8000-000000000012",
        "openedAt":"2026-09-16T00:00:00Z","eventId":"00000000-0000-4000-8000-000000000013",
        "demandId":null,"slotOperationAttemptId":{{attemptId}},"slots":[1],
        "recoverySessionRevision":1}
        """;

    private static string RecoverySessionSnapshotPayload(string attemptId) =>
        $$"""
        {"exceptionRecoverySessionId":"00000000-0000-4000-8000-000000000012",
        "recoverySessionRevision":1,"state":"OPEN","administratorId":"admin-1",
        "administratorRole":"MAINTENANCE_ADMINISTRATOR",
        "eventId":"00000000-0000-4000-8000-000000000013","demandId":null,
        "slotOperationAttemptId":{{attemptId}},"slots":[1],"selectedAction":null,
        "allowedActions":["RESUME_AFTER_REPAIR"],"blockingFacts":[]}
        """;

    private static string RecoveryActionAcceptedPayload(string attemptId) =>
        $$"""
        {"recoveryActionId":"00000000-0000-4000-8000-000000000014",
        "exceptionRecoverySessionId":"00000000-0000-4000-8000-000000000012",
        "slotOperationAttemptId":{{attemptId}},"acceptedAction":"RESUME_AFTER_REPAIR",
        "recoverySessionRevision":2,"acceptedAt":"2026-09-16T00:00:00Z"}
        """;

    /// <summary>
    /// <c>stopEndedReason</c> (3.0.0, 8005-agv-onboard-hmi#214): on an empty worklist every one of the
    /// schema's seven values is accepted by the session client's own check and reaches the record.
    /// </summary>
    [Theory]
    [InlineData("COMPLETED")]
    [InlineData("STATION_DEADLINE_EXPIRED")]
    [InlineData("LOAD_CANCELLED")]
    [InlineData("LOAD_COMPENSATED")]
    [InlineData("CARGO_HANDED_OFF")]
    [InlineData("DEMAND_RELEASED")]
    [InlineData("TRIP_TERMINATED")]
    public void EveryStopEndedReasonTheSchemaDeclaresIsAcceptedOnAnEmptyWorklist(string reason)
    {
        CurrentStopWorklistSnapshotPayload payload = Inbound<CurrentStopWorklistSnapshotPayload>(
            "CurrentStopWorklistSnapshot",
            EmptyWorklistPayload($"\"{reason}\""));

        WireToGateSessionClient.ValidateCurrentStopWorklist(payload);

        Assert.Empty(payload.Items);
        Assert.Equal(reason, payload.StopEndedReason);
    }

    /// <summary>
    /// A worklist with items carries <c>stopEndedReason: null</c>, the schema's <c>then</c> branch.
    /// </summary>
    [Fact]
    public void AWorklistWithItemsIsAcceptedWithANullStopEndedReason()
    {
        CurrentStopWorklistSnapshotPayload payload = Inbound<CurrentStopWorklistSnapshotPayload>(
            "CurrentStopWorklistSnapshot",
            WorklistPayloadWithStopEndedReason("null"));

        WireToGateSessionClient.ValidateCurrentStopWorklist(payload);

        Assert.Null(payload.StopEndedReason);
        Assert.Single(payload.Items);
    }

    /// <summary>
    /// A reason beside items, or a reason outside the seven, is the schema refusing it, not a narrowing.
    /// </summary>
    [Fact]
    public void AStopEndedReasonBesideItemsOrOutsideTheSchemaIsRefused()
    {
        CurrentStopWorklistSnapshotPayload besideItems = Inbound<CurrentStopWorklistSnapshotPayload>(
            "CurrentStopWorklistSnapshot",
            WorklistPayloadWithStopEndedReason("\"COMPLETED\""));
        CurrentStopWorklistSnapshotPayload unknown = Inbound<CurrentStopWorklistSnapshotPayload>(
            "CurrentStopWorklistSnapshot",
            EmptyWorklistPayload("\"FINISHED\""));

        Assert.Equal(
            "PROTOCOL_SCHEMA_INVALID",
            Assert.Throws<InvalidDataException>(
                () => WireToGateSessionClient.ValidateCurrentStopWorklist(besideItems)).Message);
        Assert.Equal(
            "PROTOCOL_SCHEMA_INVALID",
            Assert.Throws<InvalidDataException>(
                () => WireToGateSessionClient.ValidateCurrentStopWorklist(unknown)).Message);
    }

    /// <summary>
    /// The upgrade path: an empty worklist in the 2.0.0 shape -- no <c>stopEndedReason</c> property at
    /// all, as a 2.0.0 build journaled it -- deserializes with a <c>null</c> reason and passes the same
    /// check the journal replay runs, so the first start after an upgrade does not throw on it.
    /// </summary>
    [Fact]
    public void AnEmptyWorklistInTheVersionTwoShapeStillPassesTheCheckTheReplayRuns()
    {
        CurrentStopWorklistSnapshotPayload payload = Inbound<CurrentStopWorklistSnapshotPayload>(
            "CurrentStopWorklistSnapshot",
            """
            {"stationId":"STATION-01","worklistRevision":7,"operationSessionId":null,
            "stationDepartureDeadlineAt":null,"items":[]}
            """);

        WireToGateSessionClient.ValidateCurrentStopWorklist(payload);

        Assert.Null(payload.StopEndedReason);
        Assert.Equal(7, payload.WorklistRevision);
    }

    /// <summary>
    /// <c>closedReason</c> (3.0.0) is <c>null</c> on every state, which the schema allows everywhere.
    /// </summary>
    [Theory]
    [InlineData("OPEN")]
    [InlineData("ACTION_SELECTED")]
    [InlineData("EXECUTING")]
    [InlineData("CLOSED")]
    public void ARecoverySessionSnapshotIsAcceptedWithANullClosedReasonInEveryState(string state)
    {
        ExceptionRecoverySessionSnapshotPayload payload = Inbound<ExceptionRecoverySessionSnapshotPayload>(
            "ExceptionRecoverySessionSnapshot",
            RecoverySessionSnapshotPayload(state, "null"));

        WireToGateSessionClient.ValidateExceptionRecoverySessionSnapshot(payload);

        Assert.Null(payload.ClosedReason);
    }

    /// <summary>
    /// A <c>CLOSED</c> session takes every code of the vendored registry as its <c>closedReason</c>
    /// (<c>$defs/ErrorCode</c> is the registry), not only the one the control server sends today.
    /// </summary>
    [Theory]
    [MemberData(nameof(RegisteredErrorCodes))]
    public void AClosedRecoverySessionTakesEveryRegisteredCodeAsItsClosedReason(string code)
    {
        ExceptionRecoverySessionSnapshotPayload payload = Inbound<ExceptionRecoverySessionSnapshotPayload>(
            "ExceptionRecoverySessionSnapshot",
            RecoverySessionSnapshotPayload("CLOSED", $"\"{code}\""));

        WireToGateSessionClient.ValidateExceptionRecoverySessionSnapshot(payload);

        Assert.Equal(code, payload.ClosedReason);
    }

    /// <summary>
    /// A reason on a session that is not <c>CLOSED</c>, or a code the registry does not hold, is refused.
    /// </summary>
    [Theory]
    [InlineData("OPEN", "RECOVERY_ACTION_RESULT_NOT_RECONCILED")]
    [InlineData("EXECUTING", "RECOVERY_ACTION_RESULT_NOT_RECONCILED")]
    [InlineData("CLOSED", "NOT_A_REGISTERED_CODE")]
    public void AClosedReasonOutsideTheSchemaIsRefused(string state, string code)
    {
        ExceptionRecoverySessionSnapshotPayload payload = Inbound<ExceptionRecoverySessionSnapshotPayload>(
            "ExceptionRecoverySessionSnapshot",
            RecoverySessionSnapshotPayload(state, $"\"{code}\""));

        Assert.Equal(
            "PROTOCOL_SCHEMA_INVALID",
            Assert.Throws<InvalidDataException>(
                () => WireToGateSessionClient.ValidateExceptionRecoverySessionSnapshot(payload)).Message);
    }

    /// <summary>
    /// <c>HARDWARE_REPAIR_RELEASE</c> (3.0.0) in <c>allowedActions</c> and <c>selectedAction</c> is taken:
    /// this build offers no such action yet, but a snapshot naming it is schema-legal.
    /// </summary>
    [Fact]
    public void ARecoverySessionSnapshotNamingTheHardwareRepairReleaseActionIsAccepted()
    {
        ExceptionRecoverySessionSnapshotPayload payload = Inbound<ExceptionRecoverySessionSnapshotPayload>(
            "ExceptionRecoverySessionSnapshot",
            RecoverySessionSnapshotPayload(
                "ACTION_SELECTED",
                "null",
                selectedAction: "\"HARDWARE_REPAIR_RELEASE\"",
                allowedActions: "[\"HARDWARE_REPAIR_RELEASE\"]"));

        WireToGateSessionClient.ValidateExceptionRecoverySessionSnapshot(payload);

        Assert.Equal("HARDWARE_REPAIR_RELEASE", payload.SelectedAction);
    }

    /// <summary>
    /// <c>checkPurpose</c> (3.0.0): each of the three purposes, with the ids its <c>if/then</c> clause
    /// requires, passes the session client's check. Answering the two non-departure purposes is the
    /// business service's call, not this check's.
    /// </summary>
    [Theory]
    [InlineData("DEPARTURE", Demand, Leg, "\"ST-GATE\"")]
    [InlineData("NON_BUSINESS_MOVE", "null", Leg, "\"ST-WAIT\"")]
    [InlineData("HOLD_RELEASE", "null", "null", "null")]
    public void EveryCheckPurposeWithItsOwnIdsIsAccepted(
        string purpose,
        string demandId,
        string movementLegId,
        string targetStationId)
    {
        PreDepartureSafetyCheckPayload payload = Inbound<PreDepartureSafetyCheckPayload>(
            "PreDepartureSafetyCheck",
            PreDepartureSafetyCheckPayload(purpose, demandId, movementLegId, targetStationId));

        WireToGateSessionClient.ValidatePreDepartureSafetyCheck(payload);

        Assert.Equal(purpose, payload.CheckPurpose);
    }

    /// <summary>
    /// The ids a purpose's clause pins to null or to a string, the other way round, and a purpose
    /// outside the enum, are refused.
    /// </summary>
    [Theory]
    [InlineData("DEPARTURE", "null", Leg, "\"ST-GATE\"")]
    [InlineData("DEPARTURE", Demand, Leg, "null")]
    [InlineData("NON_BUSINESS_MOVE", Demand, Leg, "\"ST-WAIT\"")]
    [InlineData("NON_BUSINESS_MOVE", "null", "null", "\"ST-WAIT\"")]
    [InlineData("HOLD_RELEASE", "null", Leg, "null")]
    [InlineData("HOLD_RELEASE", Demand, "null", "null")]
    [InlineData("HOLD_RELEASE", "null", "null", "\"ST-GATE\"")]
    [InlineData("RELEASE", Demand, Leg, "\"ST-GATE\"")]
    public void ACheckPurposeWithTheWrongIdsIsRefused(
        string purpose,
        string demandId,
        string movementLegId,
        string targetStationId)
    {
        PreDepartureSafetyCheckPayload payload = Inbound<PreDepartureSafetyCheckPayload>(
            "PreDepartureSafetyCheck",
            PreDepartureSafetyCheckPayload(purpose, demandId, movementLegId, targetStationId));

        Assert.Throws<InvalidDataException>(
            () => WireToGateSessionClient.ValidatePreDepartureSafetyCheck(payload));
    }

    private const string Demand = "\"00000000-0000-4000-8000-0000000000d1\"";

    private const string Leg = "\"00000000-0000-4000-8000-0000000000d2\"";

    public static TheoryData<string> RegisteredErrorCodes()
    {
        string path = Path.Combine(
            RepositoryRoot(), "vendor", "8005-agv-protocol", "errors", "error-codes.json");
        using JsonDocument registry = JsonDocument.Parse(File.ReadAllBytes(path));
        TheoryData<string> codes = [];
        foreach (JsonElement code in registry.RootElement.GetProperty("codes").EnumerateArray())
        {
            codes.Add(code.GetProperty("code").GetString()!);
        }

        return codes;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SQCD_8005AGV.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("SQCD_8005AGV.sln not found above the test output.");
    }

    private static string EmptyWorklistPayload(string stopEndedReason) =>
        $$"""
        {"stationId":"STATION-01","worklistRevision":7,
        "operationSessionId":"00000000-0000-4000-8000-0000000000aa",
        "stationDepartureDeadlineAt":null,"stopEndedReason":{{stopEndedReason}},"items":[]}
        """;

    private static string WorklistPayloadWithStopEndedReason(string stopEndedReason) =>
        $$"""
        {"stationId":"STATION-01","worklistRevision":7,
        "operationSessionId":"00000000-0000-4000-8000-0000000000aa",
        "stationDepartureDeadlineAt":null,"stopEndedReason":{{stopEndedReason}},
        "items":[{"demandId":"00000000-0000-4000-8000-0000000000bb",
        "transportDemandKey":"TDK-1","sublot":"SL-1","workType":"WIRE_TO_GATE",
        "stopRole":"PICKUP","expectedBasketCount":2}]}
        """;

    private static string RecoverySessionSnapshotPayload(
        string state,
        string closedReason,
        string selectedAction = "null",
        string allowedActions = "[\"RESUME_AFTER_REPAIR\"]") =>
        $$"""
        {"exceptionRecoverySessionId":"00000000-0000-4000-8000-000000000012",
        "recoverySessionRevision":1,"state":"{{state}}","administratorId":"admin-1",
        "administratorRole":"MAINTENANCE_ADMINISTRATOR",
        "eventId":"00000000-0000-4000-8000-000000000013","demandId":null,
        "slotOperationAttemptId":null,"slots":[1],"selectedAction":{{selectedAction}},
        "allowedActions":{{allowedActions}},"blockingFacts":[],"closedReason":{{closedReason}}}
        """;

    private static string PreDepartureSafetyCheckPayload(
        string purpose,
        string demandId,
        string movementLegId,
        string targetStationId) =>
        $$"""
        {"preDepartureSafetyCheckId":"00000000-0000-4000-8000-0000000000d0",
        "checkPurpose":"{{purpose}}","demandId":{{demandId}},"movementLegId":{{movementLegId}},
        "expectedSafetyStateVersion":3,"targetStationId":{{targetStationId}}}
        """;

    /// <summary>
    /// Runs the payload through the client's own envelope path: identity check, then the closed
    /// payload deserialisation.
    /// </summary>
    private static T Inbound<T>(string messageType, string payloadJson)
    {
        string line = $$"""
            {"protocolVersion":{{WireToGateRelease.ProtocolVersion}},
            "profileId":"{{WireToGateRelease.ProfileId}}",
            "protocolReleaseVersion":"{{WireToGateRelease.ReleaseVersion}}",
            "protocolReleaseManifestSha256":"{{WireToGateRelease.ManifestSha256}}",
            "messageType":"{{messageType}}","messageId":"00000000-0000-4000-8000-000000000001",
            "correlationId":null,"agvId":"{{AgvId}}","sessionGeneration":1,
            "payload":{{Compact(payloadJson)}},"sentAt":"2026-09-16T00:00:00+00:00"}
            """.ReplaceLineEndings(string.Empty);

        WireToGateEnvelope envelope =
            WireToGateProtocolSerializer.DeserializeAndValidate(line, AgvId);
        WireToGateProtocolSerializer.RequireMessage(envelope, messageType);

        return WireToGateProtocolSerializer.DeserializePayload<T>(envelope);
    }

    /// <summary>
    /// Collapses the readable multi-line payload literals above into one line, so the envelope they
    /// are embedded in stays a single NDJSON record.
    /// </summary>
    private static string Compact(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }
}
