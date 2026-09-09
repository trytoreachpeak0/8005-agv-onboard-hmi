using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The journey projection's demand agreement, which protocol v2 moved the ground under.
/// </summary>
/// <remarks>
/// v2 took <c>demandId</c> off the plan snapshot's top level and put it on each leg, because
/// <c>legs.maxItems</c> went from 2 to 9. That turns a question the projection used to answer from
/// one value ("the plan's demand") into a question about a set, and it introduces a case that could
/// not previously exist: <b>legs that name different demands</b>. Deriving a single nullable
/// <c>DemandId</c> from that set and feeding it to the old comparison reads the contradiction as
/// "no demand", which the comparison treats as agreement -- a plan that contradicts itself would
/// pass the guard <see cref="WireToGateJourneySnapshot.CanAcceptSublot"/> stands behind.
/// </remarks>
public sealed class WireToGateJourneySnapshotTests
{
    private const string DemandA = "11111111-1111-4111-8111-111111111111";
    private const string DemandB = "22222222-2222-4222-8222-222222222222";

    [Fact]
    public void APlanWhoseLegsNameDifferentDemandsIsNeverConsistent()
    {
        WireToGateJourneySnapshot journey = Journey(Worklist(DemandA), Plan(DemandA, DemandB));

        Assert.Equal([DemandA, DemandB], journey.UpcomingStopPlan!.DemandIds);
        Assert.Null(journey.UpcomingStopPlan.DemandId);
        Assert.False(journey.HasConsistentDemand);
        Assert.False(journey.CanAcceptSublot);
    }

    /// <summary>
    /// The behaviour v1 had, unchanged: one demand on both sides agrees.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public void APlanWhoseLegsAllNameTheWorklistDemandIsConsistent()
    {
        WireToGateJourneySnapshot journey = Journey(Worklist(DemandA), Plan(DemandA, DemandA));

        Assert.Equal([DemandA], journey.UpcomingStopPlan!.DemandIds);
        Assert.Equal(DemandA, journey.UpcomingStopPlan.DemandId);
        Assert.True(journey.HasConsistentDemand);
    }

    /// <summary>
    /// A plan of legs that carry no demand at all still agrees with the worklist.
    /// </summary>
    /// <remarks>
    /// v2's <c>demandId</c> is nullable on the leg, and the legs that carry no demand are the ones
    /// batches 5 and 8 add -- waiting points and chargers. Those must not make an otherwise usable
    /// worklist unusable, which is why "none" and "more than one" have to stay distinguishable.
    /// </remarks>
    [Fact]
    public void APlanWhoseLegsCarryNoDemandIsConsistent()
    {
        WireToGateJourneySnapshot journey = Journey(Worklist(DemandA), Plan(null, null));

        Assert.Empty(journey.UpcomingStopPlan!.DemandIds);
        Assert.Null(journey.UpcomingStopPlan.DemandId);
        Assert.True(journey.HasConsistentDemand);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public void APlanNamingADifferentDemandThanTheWorklistIsNotConsistent()
    {
        WireToGateJourneySnapshot journey = Journey(Worklist(DemandA), Plan(DemandB, DemandB));

        Assert.False(journey.HasConsistentDemand);
    }

    private static WireToGateJourneySnapshot Journey(
        WireToGateCurrentStopWorklist worklist,
        WireToGateUpcomingStopPlan plan) =>
        new(
            new WireToGateVehicleBusinessState(
                1, "READY", "TRANSPORT", false, "SUFFICIENT", [],
                DateTimeOffset.UnixEpoch, new string('a', 64)),
            worklist,
            plan,
            DateTimeOffset.UnixEpoch);

    private static WireToGateCurrentStopWorklist Worklist(string demandId) =>
        new(
            "ST-01",
            1,
            null,
            [new WireToGateWorklistItem(demandId, "TD-001", "SUBLOT-001", "WIRE_TO_GATE", "PICKUP", 1)],
            new string('b', 64));

    private static WireToGateUpcomingStopPlan Plan(string? first, string? second) =>
        new(
            1,
            [Leg(1, first), Leg(2, second)],
            new string('c', 64));

    private static WireToGateMovementLeg Leg(int sequence, string? demandId) =>
        new(
            $"33333333-3333-4333-8333-33333333333{sequence}",
            "TO_PICKUP",
            "BUSINESS",
            demandId,
            null,
            sequence,
            "ST-01",
            "MAP-01",
            "PLANNED");
}
