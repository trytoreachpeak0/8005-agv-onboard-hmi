using System.Text.Json;
using SQCD.Agv.Contracts;

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

    private static string WorklistPayload(string deadline) =>
        $$"""
        {"stationId":"STATION-01","worklistRevision":7,
        "operationSessionId":"00000000-0000-4000-8000-0000000000aa",
        "stationDepartureDeadlineAt":{{deadline}},
        "items":[{"demandId":"00000000-0000-4000-8000-0000000000bb",
        "transportDemandKey":"TDK-1","sublot":"SL-1","workType":"WIRE_TO_GATE",
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
