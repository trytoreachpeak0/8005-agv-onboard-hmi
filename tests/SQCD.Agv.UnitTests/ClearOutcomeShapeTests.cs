using SQCD.Agv.Contracts;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The outbound check on a load cancellation or compensation result's <c>overallOutcome</c>
/// (<c>WireToGateSessionClient.ValidateClearOutcome</c>, 8005-agv-onboard-hmi#219): the 3.0.0 value
/// <c>ALL_EMPTY_DOOR_UNPROVEN</c> is let out only in the shape the schema's <c>if/then</c> gives it -- at least one slot
/// result, and every one of them <c>EMPTY</c> -- and the older values pass as they always did (PR #248 review: the shape
/// half of the check was not pinned).
/// </summary>
public sealed class ClearOutcomeShapeTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-EMPTY-DOOR-UNPROVEN")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    public void ADoorUnprovenResultWithEverySlotEmptyGoesOut()
    {
        WireToGateSessionClient.ValidateClearOutcome(
            "ALL_EMPTY_DOOR_UNPROVEN",
            [Slot(1, "EMPTY", "UNKNOWN"), Slot(2, "EMPTY", "LOCKED")]);
    }

    [Theory]
    [InlineData("no-slot")]
    [InlineData("occupied")]
    [InlineData("unknown")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-EMPTY-DOOR-UNPROVEN")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    public void ADoorUnprovenResultOutsideTheSchemaShapeIsRefused(string shape)
    {
        IReadOnlyList<WireToGateSlotResultPayload> slots = shape switch
        {
            "no-slot" => [],
            "occupied" => [Slot(1, "EMPTY", "UNLOCKED"), Slot(2, "OCCUPIED", "LOCKED")],
            _ => [Slot(1, "UNKNOWN", "UNKNOWN")]
        };

        InvalidDataException refused = Assert.Throws<InvalidDataException>(
            () => WireToGateSessionClient.ValidateClearOutcome("ALL_EMPTY_DOOR_UNPROVEN", slots));
        Assert.Equal("PROTOCOL_SCHEMA_INVALID", refused.Message);
    }

    [Theory]
    [InlineData("ALL_EMPTY")]
    [InlineData("FAILED")]
    [InlineData("UNKNOWN")]
    public void TheOlderOutcomesStillGoOutAndAnyOtherValueIsRefused(string outcome)
    {
        WireToGateSessionClient.ValidateClearOutcome(outcome, [Slot(1, "OCCUPIED", "LOCKED")]);
        Assert.Throws<InvalidDataException>(
            () => WireToGateSessionClient.ValidateClearOutcome("COMPLETED", [Slot(1, "EMPTY", "LOCKED")]));
    }

    private static WireToGateSlotResultPayload Slot(int slotNo, string physical, string lockState) =>
        new(slotNo, "FAILED", physical, lockState, "RESET", []);
}
