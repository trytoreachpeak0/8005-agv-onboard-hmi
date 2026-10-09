using SQCD.Agv.Contracts;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The session client's outbound check of <c>ForcedMechanicalRecoveryResult</c> holds the 3.0.0 schema's
/// <c>demandId</c>／<c>cargoHandoff</c> rule in both directions (8005-agv-onboard-hmi#214): an isolation on a
/// demand carries a handoff record, and nothing else does.
/// </summary>
/// <remarks>
/// The business service never builds an isolation on a demand today -- it holds that confirmation
/// unreported -- so the G2 schema gate sees only the "no handoff" half. This pins the other half at the
/// last point before the wire, for when 8005-agv-onboard-hmi#216 adds the send path.
/// </remarks>
public sealed class ForcedMechanicalRecoveryResultShapeTests
{
    private const string Demand = "11111111-1111-4111-8111-111111111111";

    private static readonly WireToGateCargoHandoffPayload Handoff =
        new("SUBLOT-001", "王师傅", new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("MECHANICALLY_ISOLATED", null, false)]
    [InlineData("MECHANICALLY_ISOLATED", Demand, true)]
    [InlineData("FAILED", Demand, false)]
    [InlineData("FAILED", null, false)]
    [InlineData("UNKNOWN", Demand, false)]
    public void EveryShapeTheSchemaAllowsPassesTheOutboundCheck(string outcome, string? demandId, bool withHandoff)
    {
        WireToGateSessionClient.ValidateForcedMechanicalRecoveryResult(Result(outcome, demandId, withHandoff ? Handoff : null));
    }

    /// <summary>
    /// An isolation on a demand without a record, a record on anything else, a demand that is not a UUID,
    /// and a record with an empty field are all refused before the wire.
    /// </summary>
    [Theory]
    [InlineData("MECHANICALLY_ISOLATED", Demand, false, false)]
    [InlineData("MECHANICALLY_ISOLATED", null, true, false)]
    [InlineData("FAILED", Demand, true, false)]
    [InlineData("UNKNOWN", null, true, false)]
    [InlineData("FAILED", "not-a-uuid", false, false)]
    [InlineData("MECHANICALLY_ISOLATED", Demand, true, true)]
    public void EveryShapeTheSchemaRefusesIsRefusedBeforeTheWire(
        string outcome,
        string? demandId,
        bool withHandoff,
        bool blankReceiver)
    {
        WireToGateCargoHandoffPayload? handoff = withHandoff
            ? blankReceiver ? Handoff with { ReceiverName = " " } : Handoff
            : null;

        InvalidDataException refused = Assert.Throws<InvalidDataException>(
            () => WireToGateSessionClient.ValidateForcedMechanicalRecoveryResult(Result(outcome, demandId, handoff)));
        Assert.Equal("PROTOCOL_SCHEMA_INVALID", refused.Message);
    }

    private static ForcedMechanicalRecoveryResultPayload Result(
        string outcome,
        string? demandId,
        WireToGateCargoHandoffPayload? handoff) =>
        new(
            "77777777-7777-4777-8777-777777777777",
            "2af5c209-93df-485b-9cf0-5722e37ed751",
            1,
            outcome,
            [1, 2],
            new WireToGateOperatorContextPayload("maintenance-001", "SESSION", DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow,
            ElectronicEmptyProven: false,
            VehicleReadyProven: false,
            DemandId: demandId,
            CargoHandoff: handoff);
}
