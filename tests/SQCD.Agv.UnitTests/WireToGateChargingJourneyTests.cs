using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The charger as a non-business stop and the battery out of the sublot admission (batch 9-15,
/// <c>8005-agv-onboard-hmi#220</c>): when the vehicle counts as on a charging stop, that no sublot is admitted
/// there, that a business stop admits one whatever battery state the server projects, and the words the visit
/// cell and the vehicle cell show.
/// </summary>
/// <remarks>
/// <para>
/// Built after <see cref="WireToGateWaitingPointJourneyTests"/>, which stays as it was: the waiting-point half
/// of the non-business judgement does not change here.
/// </para>
/// <para>
/// The session-level half -- the entry button, the submit and the cancellation before any sublot the operator
/// actually sees, and the two <c>SnapshotAppliedAck</c> the vector expects -- is
/// <c>AutomaticChargingCycleG2Tests</c>.
/// </para>
/// </remarks>
public sealed class WireToGateChargingJourneyTests
{
    private const string DemandA = "11111111-1111-4111-8111-111111111111";

    /// <summary>
    /// <c>NEVER_LOAD_AT_CHARGER</c> on the leg alone, the purpose left at <c>TRANSPORT</c>: a plan whose only leg
    /// is a charger carries no demand, so <see cref="WireToGateJourneySnapshot.HasConsistentDemand"/> lets a
    /// worklist with an item through; the charger is what refuses it. Red before this ticket:
    /// <c>IsWaitingPointStop</c> did not know <c>CHARGER</c>, and nothing else did either.
    /// </summary>
    [Fact]
    public void AChargerLegAloneAdmitsNoSublotEvenWithAWorklistItem()
    {
        WireToGateJourneySnapshot journey = Journey("TRANSPORT", Worklist(DemandA), Charger(1, "ARRIVED"));

        Assert.True(journey.HasConsistentDemand);
        Assert.False(journey.IsWaitingPointStop);
        Assert.True(journey.IsChargerStop);
        Assert.True(journey.IsNonBusinessStop);
        Assert.True(journey.HasWorklistItemsAtNonBusinessStop);
        Assert.False(journey.CanAcceptSublot);
        Assert.False(journey.CanAcceptSublotAt(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// The same on the purpose alone: <c>CHARGING</c> arrived, the plan still names a business leg. The two
    /// snapshots arrive separately, and the window between them must not offer a load.
    /// </summary>
    [Fact]
    public void AChargingPurposeAloneAdmitsNoSublotBeforeTheChargerPlanArrives()
    {
        WireToGateJourneySnapshot journey = Journey("CHARGING", Worklist(DemandA), Business(1, DemandA, "ARRIVED"));

        Assert.Equal("BUSINESS", journey.CurrentLeg!.StopPurposeCategory);
        Assert.True(journey.IsChargerStop);
        Assert.False(journey.CanAcceptSublot);
    }

    /// <summary>
    /// <c>REQ-0281</c>, the point of this ticket: a transport under way crosses the mandatory-charge line and the
    /// server projects <c>MANDATORY_CHARGE</c> or <c>LOW</c>, or telemetry goes missing and it projects
    /// <c>UNKNOWN</c> (<c>REQ-0287</c>). The next pickup still admits the sublot. Red before this ticket: the
    /// admission required <c>batteryState == "SUFFICIENT"</c>. <c>SUFFICIENT</c> is the control.
    /// </summary>
    [Theory]
    [InlineData("LOW")]
    [InlineData("MANDATORY_CHARGE")]
    [InlineData("UNKNOWN")]
    [InlineData("SUFFICIENT")]
    public void ABusinessStopAdmitsASublotWhateverBatteryStateTheServerProjects(string batteryState)
    {
        WireToGateJourneySnapshot journey = Journey(
            "TRANSPORT", Worklist(DemandA), [Business(1, DemandA, "ARRIVED")], batteryState: batteryState);

        Assert.True(journey.CanAcceptSublot);
        Assert.True(journey.CanAcceptSublotAt(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// The branch this ticket keeps: a manual-charging hold still refuses the entry. The server sets it only on a
    /// vehicle with no purpose (<c>8005-agv-control-server#404</c>), so a transport under way does not meet it;
    /// this pins that removing the battery did not take the hold with it.
    /// </summary>
    [Fact]
    public void AManualChargingHoldStillAdmitsNoSublot()
    {
        WireToGateJourneySnapshot journey = Journey(
            "TRANSPORT", Worklist(DemandA), [Business(1, DemandA, "ARRIVED")], manualChargingHold: true);

        Assert.False(journey.IsNonBusinessStop);
        Assert.False(journey.CanAcceptSublot);
    }

    /// <summary>
    /// The guard against fixing too much: once the charge is over, a plan with a business leg and a
    /// <c>TRANSPORT</c> purpose admits a sublot on the snapshot that carries them, and every word goes back to
    /// the business stop's. Nothing local remembers the charger.
    /// </summary>
    [Fact]
    public void ABusinessPlanWithTransportPurposeAfterTheChargerAdmitsASublotAgain()
    {
        WireToGateJourneySnapshot journey = Journey(
            "TRANSPORT",
            Worklist(DemandA),
            [Charger(1, "COMPLETED"), Business(2, DemandA, "ARRIVED")],
            chargingCycleState: "NOT_CHARGING");

        Assert.False(journey.IsChargerStop);
        Assert.True(journey.CanAcceptSublot);
        Assert.Equal(WireToGateNonBusinessStopKind.None, WireToGateNonBusinessStop.Classify(journey));
        Assert.Equal(string.Empty, WireToGateChargingText.VisitText(journey));
        Assert.Equal("取货", WireToGateStopFacts.DirectionText(journey));
        Assert.Equal("焊线→质检关卡", WireToGateStopFacts.TaskTypeText(journey));
    }

    [Fact]
    public void DirectionAndTaskTypeAreEmptyAtAChargerEvenWithAWorklistItem()
    {
        WireToGateJourneySnapshot journey = Journey("CHARGING", Worklist(DemandA), Charger(1, "ARRIVED"));

        Assert.Equal(string.Empty, WireToGateStopFacts.DirectionText(journey));
        Assert.Equal(string.Empty, WireToGateStopFacts.TaskTypeText(journey));
    }

    /// <summary>
    /// The four charging sentences of the visit cell, one per stage of the cycle; the station is the current
    /// charger leg's. None of them reads 「旅程未同步」.
    /// </summary>
    [Theory]
    [InlineData("ALLOCATED", "ACTIVE", "前往充电桩 CH-01")]
    [InlineData("EN_ROUTE", "ACTIVE", "前往充电桩 CH-01")]
    [InlineData("CHARGING", "ARRIVED", "在充电桩 CH-01 充电中")]
    [InlineData("COMPLETE", "ARRIVED", "已充满，在充电桩 CH-01 待命")]
    [InlineData("UNABLE_TO_CHARGE", "ARRIVED", "在充电桩 CH-01 充不上电，等待处置")]
    [InlineData("UNKNOWN", "ARRIVED", "在充电桩 CH-01")]
    [InlineData("UNKNOWN", "ACTIVE", "前往充电桩 CH-01")]
    public void TheVisitCellSaysWhereTheChargeStands(string chargingCycleState, string legState, string expected)
    {
        WireToGateJourneySnapshot journey = Journey(
            "CHARGING", null, [Charger(1, legState)], chargingCycleState: chargingCycleState);

        Assert.Equal(WireToGateNonBusinessStopKind.Charging, WireToGateNonBusinessStop.Classify(journey));
        Assert.Equal(expected, WireToGateChargingText.VisitText(journey));
        Assert.Equal(string.Empty, WireToGateIdleReturnText.VisitText(journey));
        Assert.Equal(string.Empty, WireToGateIdleReturnText.Status(journey));
    }

    /// <summary>
    /// The purpose arrived and the plan still names a business leg: no station is borrowed from it.
    /// </summary>
    [Theory]
    [InlineData("EN_ROUTE", "前往充电桩")]
    [InlineData("CHARGING", "在充电桩充电中")]
    [InlineData("COMPLETE", "已充满，在充电桩待命")]
    [InlineData("UNABLE_TO_CHARGE", "在充电桩充不上电，等待处置")]
    public void AChargingPurposeWithoutAChargerLegNamesNoStation(string chargingCycleState, string expected)
    {
        WireToGateJourneySnapshot journey = Journey(
            "CHARGING", null, [Business(1, DemandA, "ARRIVED")], chargingCycleState: chargingCycleState);

        Assert.Equal(expected, WireToGateChargingText.VisitText(journey));
    }

    [Theory]
    [InlineData("SUFFICIENT", "电量充足")]
    [InlineData("LOW", "电量偏低")]
    [InlineData("MANDATORY_CHARGE", "需强制充电")]
    [InlineData("UNKNOWN", "电量未知")]
    public void EveryBatteryStateHasItsWordsAndItsRawValue(string batteryState, string expected)
    {
        WireToGateJourneySnapshot journey = Journey(
            "TRANSPORT", null, [Business(1, DemandA, "ACTIVE")], batteryState: batteryState);

        Assert.Equal(expected, WireToGateChargingText.BatteryText(journey));
        Assert.Equal(batteryState, WireToGateChargingText.BatteryStatus(journey));
        Assert.DoesNotContain("%", WireToGateChargingText.BatteryText(journey), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NOT_CHARGING", "")]
    [InlineData("ALLOCATED", "已分配充电桩")]
    [InlineData("EN_ROUTE", "前往充电")]
    [InlineData("CHARGING", "充电中")]
    [InlineData("COMPLETE", "已充满")]
    [InlineData("UNABLE_TO_CHARGE", "充不上电")]
    [InlineData("UNKNOWN", "充电状态未知")]
    public void EveryChargingCycleStateHasItsWordsAndItsRawValue(string chargingCycleState, string expected)
    {
        WireToGateJourneySnapshot journey = Journey(
            "CHARGING", null, [Charger(1, "ARRIVED")], chargingCycleState: chargingCycleState);

        Assert.Equal(expected, WireToGateChargingText.StatusText(journey));
        Assert.Equal(chargingCycleState, WireToGateChargingText.CycleStatus(journey));
    }

    [Fact]
    public void AManualChargingHoldIsShownAsTheServersHold()
    {
        WireToGateJourneySnapshot held = Journey(
            null, null, [], manualChargingHold: true, chargingCycleState: "NOT_CHARGING");
        WireToGateJourneySnapshot heldWhileUnable = Journey(
            "CHARGING", null, [Charger(1, "ARRIVED")], manualChargingHold: true, chargingCycleState: "UNABLE_TO_CHARGE");

        Assert.Equal("需人工充电：服务端保持", WireToGateChargingText.StatusText(held));
        Assert.Equal("充不上电；需人工充电：服务端保持", WireToGateChargingText.StatusText(heldWhileUnable));
    }

    [Fact]
    public void TheEmptyJourneyShowsNoChargingAtAll()
    {
        WireToGateJourneySnapshot empty = WireToGateJourneySnapshot.Empty;

        Assert.False(empty.IsChargerStop);
        Assert.False(empty.IsNonBusinessStop);
        Assert.Equal(string.Empty, WireToGateChargingText.VisitText(empty));
        Assert.Equal(string.Empty, WireToGateChargingText.BatteryText(empty));
        Assert.Equal(string.Empty, WireToGateChargingText.BatteryStatus(empty));
        Assert.Equal(string.Empty, WireToGateChargingText.StatusText(empty));
        Assert.Equal(string.Empty, WireToGateChargingText.CycleStatus(empty));
    }

    /// <summary>
    /// A charger clearance to a waiting point is not an idle return (<c>REQ-0178</c>: the two share the waiting
    /// points). The visit cell says 「清桩」 and the UIA status is the clearance's own, never the idle return's.
    /// Red before this ticket: the leg alone made it 「空闲返回」.
    /// </summary>
    [Theory]
    [InlineData("ACTIVE", "清桩：前往等待点 WP-01", WireToGateIdleReturnText.ClearingEnRouteStatus)]
    [InlineData("ARRIVED", "清桩：已到等待点 WP-01", WireToGateIdleReturnText.ClearingAtWaitingPointStatus)]
    public void AChargerClearanceToAWaitingPointIsNotAnIdleReturn(string legState, string expectedText, string expectedStatus)
    {
        WireToGateJourneySnapshot journey = Journey("CLEARING_MAINTENANCE", null, WaitingPoint(1, legState));

        Assert.Equal(WireToGateNonBusinessStopKind.Clearing, WireToGateNonBusinessStop.Classify(journey));
        Assert.Equal(expectedText, WireToGateIdleReturnText.VisitText(journey));
        Assert.Equal(expectedStatus, WireToGateIdleReturnText.Status(journey));
        Assert.DoesNotContain("空闲返回", WireToGateIdleReturnText.VisitText(journey), StringComparison.Ordinal);
        Assert.False(journey.CanAcceptSublot);
    }

    /// <summary>
    /// The clearance begins while the plan's current leg is still the charger: it reads as a clearance with no
    /// station, not as charging and not with the charger's station.
    /// </summary>
    [Fact]
    public void AClearanceStillOnTheChargerLegSaysClearingWithoutAStation()
    {
        WireToGateJourneySnapshot journey = Journey("CLEARING_MAINTENANCE", null, Charger(1, "ARRIVED"));

        Assert.Equal(WireToGateNonBusinessStopKind.Clearing, WireToGateNonBusinessStop.Classify(journey));
        Assert.Equal("清桩：前往等待点", WireToGateIdleReturnText.VisitText(journey));
        Assert.Equal(string.Empty, WireToGateChargingText.VisitText(journey));
    }

    /// <summary>
    /// The guard against fixing too much on the waiting-point side: an <c>IDLE_RETURN</c> or a <c>null</c>
    /// purpose on a waiting-point leg reads exactly as batch 8-22 wrote it.
    /// </summary>
    [Theory]
    [InlineData("IDLE_RETURN")]
    [InlineData(null)]
    public void AnIdleReturnOrNoPurposeAtAWaitingPointIsStillAnIdleReturn(string? activePurpose)
    {
        WireToGateJourneySnapshot journey = Journey(activePurpose, null, WaitingPoint(1, "ARRIVED"));

        Assert.Equal(WireToGateNonBusinessStopKind.IdleReturn, WireToGateNonBusinessStop.Classify(journey));
        Assert.Equal("在等待点 WP-01 待命", WireToGateIdleReturnText.VisitText(journey));
        Assert.Equal(WireToGateIdleReturnText.AtWaitingPointStatus, WireToGateIdleReturnText.Status(journey));
    }

    /// <summary>
    /// The classification and the entry gates never disagree: for every purpose and every current-leg category
    /// the protocol allows, a kind other than <c>None</c> is exactly a non-business stop, and exactly one of the
    /// two text classes speaks then.
    /// </summary>
    [Theory]
    [MemberData(nameof(PurposeAndLegMatrix))]
    public void TheKindIsNoneExactlyWhenTheStopIsABusinessOne(string? activePurpose, string legCategory)
    {
        WireToGateMovementLeg leg = legCategory switch
        {
            "WAITING_POINT" => WaitingPoint(1, "ARRIVED"),
            "CHARGER" => Charger(1, "ARRIVED"),
            _ => Business(1, DemandA, "ARRIVED")
        };
        WireToGateJourneySnapshot journey = Journey(activePurpose, null, leg);

        WireToGateNonBusinessStopKind kind = WireToGateNonBusinessStop.Classify(journey);
        Assert.Equal(journey.IsNonBusinessStop, kind != WireToGateNonBusinessStopKind.None);
        int speaking = new[] { WireToGateChargingText.VisitText(journey), WireToGateIdleReturnText.VisitText(journey) }
            .Count(text => text.Length > 0);
        Assert.Equal(journey.IsNonBusinessStop ? 1 : 0, speaking);
    }

    public static TheoryData<string?, string> PurposeAndLegMatrix()
    {
        TheoryData<string?, string> data = new();
        foreach (string? purpose in new[] { null, "TRANSPORT", "CHARGING", "CLEARING_MAINTENANCE", "IDLE_RETURN" })
        {
            foreach (string category in new[] { "BUSINESS", "WAITING_POINT", "CHARGER" })
            {
                data.Add(purpose, category);
            }
        }

        return data;
    }

    private static WireToGateJourneySnapshot Journey(
        string? activePurpose,
        WireToGateCurrentStopWorklist? worklist,
        params WireToGateMovementLeg[] legs) =>
        Journey(activePurpose, worklist, legs, batteryState: "SUFFICIENT");

    private static WireToGateJourneySnapshot Journey(
        string? activePurpose,
        WireToGateCurrentStopWorklist? worklist,
        WireToGateMovementLeg[] legs,
        string batteryState = "SUFFICIENT",
        string chargingCycleState = "NOT_CHARGING",
        bool manualChargingHold = false) =>
        new(
            new WireToGateVehicleBusinessState(
                1, "READY", activePurpose, manualChargingHold, batteryState, chargingCycleState, null, [],
                DateTimeOffset.UnixEpoch, new string('a', 64)),
            worklist,
            new WireToGateUpcomingStopPlan(1, legs, new string('c', 64)),
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

    private static WireToGateMovementLeg Charger(int sequence, string state) =>
        new($"66666666-6666-4666-8666-66666666666{sequence}", null, "CHARGER", null, null, sequence, "CH-01", "MAP-26", state);

    private static WireToGateMovementLeg WaitingPoint(int sequence, string state) =>
        new($"44444444-4444-4444-8444-44444444444{sequence}", null, "WAITING_POINT", null, null, sequence, "WP-01", "MAP-26", state);

    private static WireToGateMovementLeg Business(int sequence, string demandId, string state) =>
        new($"55555555-5555-4555-8555-55555555555{sequence}", "TO_PICKUP", "BUSINESS", demandId, null, sequence, "ST-01", "MAP-26", state);
}
