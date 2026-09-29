using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The waiting-point leg as a non-business stop (batch 8-22, <c>8005-agv-onboard-hmi#217</c>): which leg is
/// current, when the vehicle counts as on an idle return, that no sublot is admitted there, and the words
/// the visit cell shows.
/// </summary>
/// <remarks>
/// <para>
/// The case this ticket closes is the first one below. Before it, a plan whose legs carry no demand -- a
/// waiting point only -- let any worklist through <see cref="WireToGateJourneySnapshot.HasConsistentDemand"/>,
/// so a worklist with an item arriving with it made <see cref="WireToGateJourneySnapshot.CanAcceptSublot"/>
/// true: <c>NEVER_LOAD_AT_WAITING_POINT</c> had no guard.
/// </para>
/// <para>
/// The session-level half -- the entry button and the cancellation before any sublot the operator actually
/// sees -- is <c>WaitingPointIdleReturnG2Tests</c>.
/// </para>
/// </remarks>
public sealed class WireToGateWaitingPointJourneyTests
{
    private const string DemandA = "11111111-1111-4111-8111-111111111111";

    [Fact]
    public void APlanWithOnlyAWaitingPointLegAdmitsNoSublotEvenWithAWorklistItem()
    {
        WireToGateJourneySnapshot journey = Journey(
            "TRANSPORT",
            Worklist(DemandA),
            WaitingPoint(1, "ARRIVED"));

        // The demand relation still lets it through; the waiting point is what refuses it.
        Assert.True(journey.HasConsistentDemand);
        Assert.True(journey.IsWaitingPointStop);
        Assert.True(journey.HasWorklistItemsAtWaitingPoint);
        Assert.False(journey.CanAcceptSublot);
        Assert.False(journey.CanAcceptSublotAt(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AnIdleReturnPurposeAloneAdmitsNoSublotBeforeTheWaitingPointPlanArrives()
    {
        WireToGateJourneySnapshot journey = Journey(
            "IDLE_RETURN",
            Worklist(DemandA),
            Business(1, DemandA, "ARRIVED"));

        Assert.True(journey.IsWaitingPointStop);
        Assert.False(journey.CanAcceptSublot);
    }

    /// <summary>
    /// <c>REQ-0293</c>: once the idle return is released, a plan with a business leg and a <c>TRANSPORT</c>
    /// purpose admits a sublot again on the snapshot that carries them. Nothing local remembers the waiting
    /// point.
    /// </summary>
    [Fact]
    public void ABusinessPlanWithTransportPurposeAfterTheWaitingPointAdmitsASublotAgain()
    {
        WireToGateJourneySnapshot journey = Journey(
            "TRANSPORT",
            Worklist(DemandA),
            WaitingPoint(1, "COMPLETED"),
            Business(2, DemandA, "ARRIVED"));

        Assert.Equal("BUSINESS", journey.CurrentLeg!.StopPurposeCategory);
        Assert.False(journey.IsWaitingPointStop);
        Assert.True(journey.CanAcceptSublot);
        Assert.Equal("取货", WireToGateStopFacts.DirectionText(journey));
        Assert.Equal(string.Empty, WireToGateIdleReturnText.Status(journey));
        Assert.Equal(string.Empty, WireToGateIdleReturnText.VisitText(journey));
    }

    /// <summary>
    /// The current leg is the first by <c>sequence</c> that is not <c>COMPLETED</c>, whatever order the legs
    /// arrive in; a waiting point planned after the business stop does not make the business stop idle.
    /// </summary>
    [Fact]
    public void TheCurrentLegIsTheFirstUncompletedBySequenceNotByArrivalOrder()
    {
        WireToGateJourneySnapshot journey = Journey(
            "TRANSPORT",
            null,
            WaitingPoint(2, "PLANNED"),
            Business(1, DemandA, "ACTIVE"));

        Assert.Equal(1, journey.CurrentLeg!.Sequence);
        Assert.False(journey.IsWaitingPointStop);
    }

    [Fact]
    public void OnTheWayToTheWaitingPointTheCellSaysIdleReturnWithTheStation()
    {
        WireToGateJourneySnapshot journey = Journey("IDLE_RETURN", null, WaitingPoint(1, "ACTIVE"));

        Assert.Equal(WireToGateIdleReturnText.EnRouteStatus, WireToGateIdleReturnText.Status(journey));
        Assert.Equal("空闲返回：前往等待点 WP-01", WireToGateIdleReturnText.VisitText(journey));
    }

    [Fact]
    public void AtTheWaitingPointTheCellSaysStandingBy()
    {
        WireToGateJourneySnapshot journey = Journey("IDLE_RETURN", Worklist(), WaitingPoint(1, "ARRIVED"));

        Assert.Equal(WireToGateIdleReturnText.AtWaitingPointStatus, WireToGateIdleReturnText.Status(journey));
        Assert.Equal("在等待点 WP-01 待命", WireToGateIdleReturnText.VisitText(journey));
    }

    /// <summary>
    /// The purpose arrived and the plan still names a business leg: no station is borrowed from it.
    /// </summary>
    [Fact]
    public void AnIdleReturnPurposeWithoutAWaitingPointLegNamesNoStation()
    {
        WireToGateJourneySnapshot journey = Journey("IDLE_RETURN", null, Business(1, DemandA, "COMPLETED"));

        Assert.Null(journey.CurrentLeg);
        Assert.Equal(WireToGateIdleReturnText.EnRouteStatus, WireToGateIdleReturnText.Status(journey));
        Assert.Equal("空闲返回：前往等待点", WireToGateIdleReturnText.VisitText(journey));
    }

    /// <summary>
    /// Direction and task type stay empty at a waiting point, including the server's contradiction of a
    /// worklist with an item there: the top line does not write a 「取货」 for a stop that is not a business one.
    /// </summary>
    [Fact]
    public void DirectionAndTaskTypeAreEmptyAtAWaitingPointEvenWithAWorklistItem()
    {
        WireToGateJourneySnapshot journey = Journey("TRANSPORT", Worklist(DemandA), WaitingPoint(1, "ARRIVED"));

        Assert.Equal(string.Empty, WireToGateStopFacts.DirectionText(journey));
        Assert.Equal(string.Empty, WireToGateStopFacts.TaskTypeText(journey));
    }

    [Fact]
    public void TheEmptyJourneyIsNotAnIdleReturn()
    {
        Assert.False(WireToGateJourneySnapshot.Empty.IsWaitingPointStop);
        Assert.Null(WireToGateJourneySnapshot.Empty.CurrentLeg);
        Assert.Equal(string.Empty, WireToGateIdleReturnText.Status(WireToGateJourneySnapshot.Empty));
    }

    private static WireToGateJourneySnapshot Journey(
        string activePurpose,
        WireToGateCurrentStopWorklist? worklist,
        params WireToGateMovementLeg[] legs) =>
        new(
            new WireToGateVehicleBusinessState(
                1, "READY", activePurpose, false, "SUFFICIENT", "NOT_CHARGING", null, [],
                DateTimeOffset.UnixEpoch, new string('a', 64)),
            worklist,
            new WireToGateUpcomingStopPlan(1, legs, new string('c', 64)),
            DateTimeOffset.UnixEpoch);

    private static WireToGateCurrentStopWorklist Worklist(params string[] demandIds) =>
        new(
            "WP-01",
            1,
            null,
            null,
            [.. demandIds.Select((demandId, index) => new WireToGateWorklistItem(
                demandId, $"TD-{index + 1:D3}", $"SUBLOT-{index + 1:D3}", "WIRE_TO_GATE", "PICKUP", 1))],
            new string('b', 64));

    private static WireToGateMovementLeg WaitingPoint(int sequence, string state) =>
        new($"44444444-4444-4444-8444-44444444444{sequence}", null, "WAITING_POINT", null, null, sequence, "WP-01", "MAP-26", state);

    private static WireToGateMovementLeg Business(int sequence, string demandId, string state) =>
        new($"55555555-5555-4555-8555-55555555555{sequence}", "TO_PICKUP", "BUSINESS", demandId, null, sequence, "ST-01", "MAP-26", state);
}
