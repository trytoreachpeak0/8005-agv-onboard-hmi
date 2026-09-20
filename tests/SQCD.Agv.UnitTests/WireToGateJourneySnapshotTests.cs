using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The journey projection's demand agreement: the worklist's demands are a subset of the plan's.
/// </summary>
/// <remarks>
/// <para>
/// v2 took <c>demandId</c> off the plan snapshot's top level and put it on each leg, because
/// <c>legs.maxItems</c> went from 2 to 9, and <c>items.maxItems</c> went to 8. Until batch 7 the
/// projection still read both as single values -- a plan whose legs named two demands was never
/// consistent, and <see cref="WireToGateJourneySnapshot.CanAcceptSublot"/> wanted exactly one item --
/// so the first multi-demand stop could never take a sublot.
/// </para>
/// <para>
/// Batch 7-13 (<c>8005-agv-onboard-hmi#134</c>) redefined it as a set relation: every demand the
/// worklist names is one the plan names. The plan lists the legs already completed and the one the
/// vehicle is at (control server <c>JourneyPlanBuilder</c>: <c>COMPLETED</c> and <c>ARRIVED</c> legs
/// stay in the snapshot), so a demand being worked here is always among them; a plan naming no
/// demand at all -- waiting points and chargers only -- still lets the worklist through.
/// </para>
/// </remarks>
public sealed class WireToGateJourneySnapshotTests
{
    private const string DemandA = "11111111-1111-4111-8111-111111111111";
    private const string DemandB = "22222222-2222-4222-8222-222222222222";
    private const string DemandC = "33333333-0000-4333-8333-000000000003";

    /// <summary>
    /// Two demands at one stop, both on the plan: consistent, and a sublot can be taken.
    /// </summary>
    [Fact]
    public void AWorklistOfTwoDemandsBothOnThePlanIsConsistent()
    {
        WireToGateJourneySnapshot journey = Journey(Worklist(DemandA, DemandB), Plan(DemandA, DemandB));

        Assert.Equal([DemandA, DemandB], journey.UpcomingStopPlan!.DemandIds);
        Assert.True(journey.HasConsistentDemand);
        Assert.True(journey.CanAcceptSublot);
    }

    /// <summary>
    /// A plan naming more demands than this stop's worklist -- the others are picked up or dropped
    /// elsewhere -- is consistent. Before batch 7 a plan of two demands was never consistent.
    /// </summary>
    [Fact]
    public void APlanNamingMoreDemandsThanTheWorklistIsConsistent()
    {
        WireToGateJourneySnapshot journey = Journey(Worklist(DemandA), Plan(DemandA, DemandB));

        Assert.True(journey.HasConsistentDemand);
        Assert.True(journey.CanAcceptSublot);
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
        Assert.True(journey.HasConsistentDemand);
    }

    /// <summary>
    /// A plan of legs that carry no demand at all still agrees with the worklist, whatever its size.
    /// </summary>
    /// <remarks>
    /// v2's <c>demandId</c> is nullable on the leg, and the legs that carry no demand are the ones
    /// later batches add -- waiting points and chargers. Those must not make an otherwise usable
    /// worklist unusable.
    /// </remarks>
    [Fact]
    public void APlanWhoseLegsCarryNoDemandIsConsistent()
    {
        WireToGateJourneySnapshot journey = Journey(Worklist(DemandA, DemandB), Plan(null, null));

        Assert.Empty(journey.UpcomingStopPlan!.DemandIds);
        Assert.True(journey.HasConsistentDemand);
        Assert.True(journey.CanAcceptSublot);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public void APlanNamingADifferentDemandThanTheWorklistIsNotConsistent()
    {
        WireToGateJourneySnapshot journey = Journey(Worklist(DemandA), Plan(DemandB, DemandB));

        Assert.False(journey.HasConsistentDemand);
    }

    /// <summary>
    /// One worklist demand missing from the plan is enough to make the pair inconsistent, even when
    /// the other is on it: the subset is checked item by item, not by any single item.
    /// </summary>
    [Fact]
    public void AWorklistDemandMissingFromThePlanIsNotConsistent()
    {
        WireToGateJourneySnapshot journey = Journey(Worklist(DemandA, DemandC), Plan(DemandA, DemandB));

        Assert.False(journey.HasConsistentDemand);
        Assert.False(journey.CanAcceptSublot);
    }

    /// <summary>
    /// A worklist with no item takes no sublot -- there is nothing to enter one for.
    /// </summary>
    [Fact]
    public void AnEmptyWorklistCannotAcceptASublot()
    {
        WireToGateJourneySnapshot journey = Journey(Worklist(), Plan(DemandA, DemandB));

        Assert.True(journey.HasConsistentDemand);
        Assert.False(journey.CanAcceptSublot);
    }

    /// <summary>
    /// Eight items -- the schema's maximum -- against a plan naming all eight: neither property throws.
    /// </summary>
    [Fact]
    public void EightWorklistItemsAreReadWithoutThrowing()
    {
        string[] demands = [.. Enumerable.Range(1, 8).Select(n => $"44444444-0000-4444-8444-0000000000{n:D2}")];
        WireToGateJourneySnapshot journey = Journey(
            Worklist(demands),
            new WireToGateUpcomingStopPlan(
                1,
                [.. demands.Select((demand, index) => Leg(index + 1, demand))],
                new string('c', 64)));

        Assert.True(journey.HasConsistentDemand);
        Assert.True(journey.CanAcceptSublotAt(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1)));
    }

    private static WireToGateJourneySnapshot Journey(
        WireToGateCurrentStopWorklist worklist,
        WireToGateUpcomingStopPlan plan) =>
        new(
            new WireToGateVehicleBusinessState(
                1, "READY", "TRANSPORT", false, "SUFFICIENT", "NOT_CHARGING", null, [],
                DateTimeOffset.UnixEpoch, new string('a', 64)),
            worklist,
            plan,
            DateTimeOffset.UnixEpoch);

    private static WireToGateCurrentStopWorklist Worklist(params string[] demandIds) =>
        new(
            "ST-01",
            1,
            null,
            null,
            [.. demandIds.Select((demandId, index) => new WireToGateWorklistItem(
                demandId, $"TD-{index + 1:D3}", $"SUBLOT-{index + 1:D3}", "WIRE_TO_GATE", "PICKUP", 1))],
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
