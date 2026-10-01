using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;
using Harness = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Harness;
using Payloads = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Payloads;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The onboard half of <c>CV-AUTOMATIC-CHARGING-CYCLE</c> (batch 9-15, <c>trytoreachpeak0/8005-agv-onboard-hmi#220</c>):
/// a charger leg is a non-business stop, the charge is shown, and a transport under way is not refused for its
/// battery. Driven from the fake control server through the real session service, business service and
/// <see cref="SQCD.Agv.Wpf.ViewModels.MainViewModel"/>, wired as <c>App.xaml.cs</c> wires them.
/// </summary>
/// <remarks>
/// <para>
/// The vector carries two onboard assertions and binds no payload (spec section 6.6):
/// <c>DISPLAY_CHARGING_PURPOSE</c> and <c>NEVER_LOAD_AT_CHARGER</c>, over the sequence
/// <c>UpcomingStopPlanSnapshot → SnapshotAppliedAck → VehicleBusinessStateSnapshot → SnapshotAppliedAck</c>. Each
/// named test below proves one of them on what the operator sees -- the visit cell, the vehicle cell's battery and
/// charging status with their UIA values, the entry and cancellation buttons -- and on the two
/// <c>SnapshotAppliedAck</c>, not on the payload's shape.
/// </para>
/// <para>
/// <b>The vector tests carry <c>FP-IS-13</c></b>, the projection of their vector onto the slices this line
/// implements (<c>IntegrationSliceTraitArchitectureTests</c>). They carried no slice until the last of the three
/// onboard batch-9 tickets (<c>8005-agv-onboard-hmi#222</c>) flipped <c>FP-IS-13</c> to implemented, once its
/// field-confirmation and station-clearance vectors had their tests too.
/// </para>
/// <para>
/// The tests without a vector trait are the ones the vector does not speak to -- the battery at a business stop,
/// the return to transport, the charger clearance -- and the controls that keep the fix from reaching too far.
/// </para>
/// </remarks>
public sealed class AutomaticChargingCycleG2Tests
{
    private const string Charger = "CH-01";

    private const string WaitingPoint = "WP-01";

    private const string ItemADemand = "aaaaaaaa-0000-4000-8000-00000000000a";

    static AutomaticChargingCycleG2Tests()
    {
        // The harness is MultiDemandJourneyG2Tests'; its credential and operator variables are set by that
        // class's static constructor, which using its nested types alone does not run.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(MultiDemandJourneyG2Tests).TypeHandle);
    }

    /// <summary>
    /// <c>DISPLAY_CHARGING_PURPOSE</c>: the plan and the business state arrive in the vector's order and both are
    /// acknowledged; the visit cell says the vehicle is on its way to the charger, then charging at it, then full
    /// and standing by, and the vehicle cell carries the battery and the cycle with their raw values. Direction
    /// and task type stay empty and no entry is offered at any stage.
    /// </summary>
    /// <remarks>
    /// Red before this ticket: with no worklist the visit cell read 「旅程未同步」, and nothing showed the charge.
    /// </remarks>
    [Fact]
    [Trait("ProtocolVector", "CV-AUTOMATIC-CHARGING-CYCLE")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    public async Task AChargeIsShownOnTheWayAtTheChargerAndWhenFull()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.VectorJourneySnapshotsAfterRecovery = ["UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"];
                server.VectorPlanLegs = [(null, "ACTIVE", Charger)];
                server.VectorLegStopPurposeCategories = ["CHARGER"];
                server.JourneyActivePurpose = "CHARGING";
                server.JourneyChargingCycleState = "EN_ROUTE";
                server.JourneyBatteryState = "MANDATORY_CHARGE";
            },
            token);

        await harness.WaitUntilAsync(
            () => AcknowledgedKinds(harness.Server).Length == 2
                && harness.ViewModel.ChargingStatus == "EN_ROUTE",
            "the plan and the business state to be applied and shown",
            token);

        Assert.Equal(["UPCOMING_STOP_PLAN", "VEHICLE_BUSINESS_STATE"], AcknowledgedKinds(harness.Server));
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        WireToGateMovementLeg leg = Assert.Single(harness.Session.CurrentJourney.UpcomingStopPlan!.Legs);
        Assert.Null(leg.LegType);
        Assert.Null(leg.DemandId);
        Assert.Null(harness.Session.CurrentJourney.CurrentStopWorklist);

        Assert.Equal($"前往充电桩 {Charger}", harness.ViewModel.VisitText);
        Assert.Equal("前往充电", harness.ViewModel.ChargingStatusText);
        Assert.Equal("需强制充电", harness.ViewModel.BatteryStatusText);
        Assert.Equal("MANDATORY_CHARGE", harness.ViewModel.BatteryStatus);
        Assert.Equal(string.Empty, harness.ViewModel.IdleReturnStatus);
        AssertShownAsNonBusinessStop(harness);
        Assert.Equal(["充电桩"], harness.ViewModel.JourneyPlanLegs.Select(row => row.StopPurposeText));

        await harness.Server.SendJourneySnapshotAsync("UpcomingStopPlanSnapshot", Payloads.Plan(2, [ChargerLeg(1, "ARRIVED")]));
        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot", BusinessState(2, "CHARGING", "CHARGING", "MANDATORY_CHARGE"));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.ChargingStatus == "CHARGING"
                && harness.Session.CurrentJourney.UpcomingStopPlan?.Revision == 2,
            "the vehicle charging at the charger",
            token);

        Assert.Equal($"在充电桩 {Charger} 充电中", harness.ViewModel.VisitText);
        Assert.Equal("充电中", harness.ViewModel.ChargingStatusText);
        AssertShownAsNonBusinessStop(harness);

        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot", BusinessState(3, "CHARGING", "COMPLETE", "SUFFICIENT"));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.ChargingStatus == "COMPLETE",
            "the full charge to be shown",
            token);

        Assert.Equal($"已充满，在充电桩 {Charger} 待命", harness.ViewModel.VisitText);
        Assert.Equal("已充满", harness.ViewModel.ChargingStatusText);
        Assert.Equal("电量充足", harness.ViewModel.BatteryStatusText);
        AssertShownAsNonBusinessStop(harness);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>NEVER_LOAD_AT_CHARGER</c>, the entry button: the plan has only a charger leg (no demand), and the server
    /// nevertheless sends a worklist with an item and an entry request. The entry is not offered, the stop reads
    /// as the charger it is, and the contradiction is logged once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The purpose is left at <c>TRANSPORT</c> on purpose: the leg's category alone has to be enough.
    /// </para>
    /// <para>
    /// One of three gates, pinned alone as batch 8-22 pinned the waiting point's: the one in
    /// <c>CanSubmitSublot</c>. The submit that bypasses the button is
    /// <see cref="ADirectSubmitAtAChargerIsRefusedAndSendsNothing"/>, the cancellation before any sublot is
    /// <see cref="TheCancellationBeforeAnySublotIsNotOfferedAtACharger"/>. Red before this ticket: none of the
    /// three knew a charger.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("ProtocolVector", "CV-AUTOMATIC-CHARGING-CYCLE")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    public async Task NoEntryIsOfferedAtAChargerEvenWithAWorklistItemAndAnEntryRequest()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartWithEntryRequestAsync("TRANSPORT", ChargerLeg(1, "ARRIVED"), token);

        AssertEachSnapshotAcknowledgedOnce(harness);

        await AssertEntryStaysClosedAsync(harness, token);
        Assert.Equal($"在充电桩 {Charger}", harness.ViewModel.VisitText);
        Assert.Equal(string.Empty, harness.ViewModel.StopDirectionText);
        Assert.Equal(string.Empty, harness.ViewModel.TaskTypeText);
        Assert.Single(
            harness.Logger.Entries,
            entry => entry.Severity == LogSeverity.Warning && entry.Message.StartsWith("充电桩停靠收到带项的清单", StringComparison.Ordinal));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The second gate of <c>NEVER_LOAD_AT_CHARGER</c>: a submit that does not ask the button first is refused at
    /// the charger, no <c>SublotSubmitted</c> goes out and no door opens.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-AUTOMATIC-CHARGING-CYCLE")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    public async Task ADirectSubmitAtAChargerIsRefusedAndSendsNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartWithEntryRequestAsync("TRANSPORT", ChargerLeg(1, "ARRIVED"), token);
        AssertEachSnapshotAcknowledgedOnce(harness);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Business.SubmitSublotAsync("SUBLOT-A", "SCANNER", token));

        Assert.Equal("WIRE_TO_GATE_JOURNEY_NOT_READY", refused.Message);
        await Task.Delay(300, token);
        Assert.Empty(harness.Submissions);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The third gate: the cancellation before any sublot is not offered at a charger -- there is nothing to give
    /// up before loading -- and a press that reaches the business service anyway sends nothing.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-AUTOMATIC-CHARGING-CYCLE")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    public async Task TheCancellationBeforeAnySublotIsNotOfferedAtACharger()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartWithEntryRequestAsync("TRANSPORT", ChargerLeg(1, "ARRIVED"), token);
        AssertEachSnapshotAcknowledgedOnce(harness);

        await AssertCancellationStaysClosedAsync(harness, token);
        try
        {
            await harness.Business.RequestLoadCancellationAsync(
                WireToGateBusinessService.LoadCancellationDefaultReason, token);
        }
        catch (InvalidOperationException)
        {
            // A press on an entry that was never offered has always been refused this way; what counts is below.
        }

        await Task.Delay(300, token);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "LoadCancellationStartRequested");
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// The two facts disagree: the business state says <c>CHARGING</c>, the plan's current leg is still a
    /// <c>BUSINESS</c> one. Either fact is enough, so every gate stays shut and the cell says the vehicle is on its
    /// way to a charger without borrowing the business leg's station.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-AUTOMATIC-CHARGING-CYCLE")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    public async Task AChargingPurposeOverABusinessLegStillOffersNoEntry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartWithEntryRequestAsync(
            "CHARGING", BusinessLeg(1, "ARRIVED"), token, server => server.JourneyChargingCycleState = "EN_ROUTE");

        AssertEachSnapshotAcknowledgedOnce(harness);
        Assert.Equal("BUSINESS", harness.Session.CurrentJourney.CurrentLeg!.StopPurposeCategory);
        await AssertEntryStaysClosedAsync(harness, token);
        Assert.Equal("前往充电桩", harness.ViewModel.VisitText);
        Assert.Equal(string.Empty, harness.ViewModel.StopDirectionText);

        // Every gate reads the purpose too, not the leg alone: with the leg a business one, a gate that looked only
        // at the leg would offer the cancellation or send the entry here.
        await AssertCancellationStaysClosedAsync(harness, token);
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Business.SubmitSublotAsync("SUBLOT-A", "SCANNER", token));
        Assert.Equal("WIRE_TO_GATE_JOURNEY_NOT_READY", refused.Message);
        await Task.Delay(300, token);
        Assert.Empty(harness.Submissions);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>REQ-0281</c> end to end: a transport under way whose battery the server projects as <c>LOW</c>,
    /// <c>MANDATORY_CHARGE</c> or <c>UNKNOWN</c> is offered the entry at its business stop and the entry goes out.
    /// The vehicle cell shows the battery; nothing about it closes anything. The controller's half is
    /// <c>OnboardControllerTests.ATransportStopLoadsWhateverBatteryStateTheServerProjects</c>.
    /// </summary>
    [Theory]
    [InlineData("LOW", "电量偏低")]
    [InlineData("MANDATORY_CHARGE", "需强制充电")]
    [InlineData("UNKNOWN", "电量未知")]
    public async Task ATransportUnderWayIsOfferedTheEntryWhateverItsBattery(string batteryState, string batteryText)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartWithEntryRequestAsync(
            "TRANSPORT", BusinessLeg(1, "ARRIVED"), token, server => server.JourneyBatteryState = batteryState);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanSubmit && harness.ViewModel.CanRequestLoadCancellation,
            "the entry and the cancellation before any sublot to be offered",
            token);

        Assert.True(harness.Session.CurrentJourney.CanAcceptSublot);
        Assert.True(harness.Business.CanSubmitSublot);
        Assert.Equal(batteryText, harness.ViewModel.BatteryStatusText);
        Assert.Equal(batteryState, harness.ViewModel.BatteryStatus);
        Assert.Equal("ST-01", harness.ViewModel.VisitText);
        Assert.Equal("取货", harness.ViewModel.StopDirectionText);

        await harness.Business.SubmitSublotAsync("SUBLOT-A", "SCANNER", token);
        JsonElement submitted = await harness.WaitForSubmissionAsync(token);
        Assert.Equal("SUBLOT-A", submitted.GetProperty("sublot").GetString());
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The guard against fixing too much: the worklist and the entry request stay exactly as they were, and only
    /// the charging facts go -- a <c>TRANSPORT</c> purpose and a plan whose charger leg is completed and whose
    /// current leg is a business one. The entry, the direction and the station come back on those snapshots, with
    /// nothing local left over from the charger to clear first.
    /// </summary>
    [Fact]
    public async Task LeavingTheChargerForATransportBringsTheEntryBackAtOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneyActivePurpose = "CHARGING";
                server.JourneyChargingCycleState = "COMPLETE";
                server.SublotEntryExpectedSublots = ["SUBLOT-A"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, [ChargerLeg(1, "ARRIVED")])
                };
            },
            token);
        await harness.WaitUntilAsync(
            () => harness.Business.ExpectedSublots is not null && harness.ViewModel.ChargingStatus == "COMPLETE",
            "the vehicle full at the charger with an entry request in hand",
            token);
        await AssertEntryStaysClosedAsync(harness, token);

        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot", BusinessState(2, "TRANSPORT", "NOT_CHARGING", "SUFFICIENT"));
        await harness.Server.SendJourneySnapshotAsync(
            "UpcomingStopPlanSnapshot",
            Payloads.Plan(2, [ChargerLeg(1, "COMPLETED"), Payloads.Leg(2, "TO_PICKUP", "BUSINESS", ItemADemand, "ST-01", "ARRIVED")]));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.UpcomingStopPlan?.Revision == 2
                && harness.Session.CurrentJourney.VehicleBusinessState?.Revision == 2
                && harness.ViewModel.CanSubmit,
            "the transport purpose and the business leg to reopen the entry",
            token);

        Assert.True(harness.Business.CanSubmitSublot);
        Assert.Equal("ST-01", harness.ViewModel.VisitText);
        Assert.Equal("取货", harness.ViewModel.StopDirectionText);
        Assert.Equal("焊线→质检关卡", harness.ViewModel.TaskTypeText);
        Assert.Equal(string.Empty, harness.ViewModel.ChargingStatusText);
        Assert.Equal("NOT_CHARGING", harness.ViewModel.ChargingStatus);

        await harness.Business.SubmitSublotAsync("SUBLOT-A", "SCANNER", token);
        JsonElement submitted = await harness.WaitForSubmissionAsync(token);
        Assert.Equal("SUBLOT-A", submitted.GetProperty("sublot").GetString());
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A charger clearance to a waiting point is not an idle return (<c>REQ-0178</c>): the visit cell says
    /// 「清桩」 and <c>IdleReturnStatus</c> carries the clearance's own raw value, never the idle return's. The
    /// entry stays shut, as at any waiting point.
    /// </summary>
    [Fact]
    public async Task AChargerClearanceToAWaitingPointIsNotShownAsAnIdleReturn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.VectorJourneySnapshotsAfterRecovery = ["UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"];
                server.VectorPlanLegs = [(null, "ACTIVE", WaitingPoint)];
                server.VectorLegStopPurposeCategories = ["WAITING_POINT"];
                server.JourneyActivePurpose = "CLEARING_MAINTENANCE";
            },
            token);

        await harness.WaitUntilAsync(
            () => AcknowledgedKinds(harness.Server).Length == 2
                && harness.ViewModel.IdleReturnStatus == WireToGateIdleReturnText.ClearingEnRouteStatus,
            "the clearance to be applied and shown",
            token);

        Assert.Equal($"清桩：前往等待点 {WaitingPoint}", harness.ViewModel.VisitText);
        Assert.DoesNotContain("空闲返回", harness.ViewModel.VisitText, StringComparison.Ordinal);
        AssertShownAsNonBusinessStop(harness);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The clearance purpose alone, over an old business leg with a worklist item and an entry request: every gate
    /// stays shut and the cell says a clearance without the business leg's station -- symmetric with
    /// <see cref="AChargingPurposeOverABusinessLegStillOffersNoEntry"/>.
    /// </summary>
    [Fact]
    public async Task AClearancePurposeOverABusinessLegOffersNoEntry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartWithEntryRequestAsync("CLEARING_MAINTENANCE", BusinessLeg(1, "ARRIVED"), token);

        AssertEachSnapshotAcknowledgedOnce(harness);
        await AssertEntryStaysClosedAsync(harness, token);
        await AssertCancellationStaysClosedAsync(harness, token);
        Assert.Equal("清桩：前往等待点", harness.ViewModel.VisitText);
        Assert.Equal(WireToGateIdleReturnText.ClearingEnRouteStatus, harness.ViewModel.IdleReturnStatus);
        Assert.Equal(string.Empty, harness.ViewModel.StopDirectionText);
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Business.SubmitSublotAsync("SUBLOT-A", "SCANNER", token));
        Assert.Equal("WIRE_TO_GATE_JOURNEY_NOT_READY", refused.Message);
        await Task.Delay(300, token);
        Assert.Empty(harness.Submissions);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A dropped session clears the projection: the charging words, the battery and their UIA values go with it.
    /// </summary>
    [Fact]
    public async Task ADisconnectClearsTheChargingCells()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartChargingAsync(token);

        await harness.StopServerAsync();
        await harness.WaitUntilAsync(() => !harness.Session.Current.Connected, "the session to drop", token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.ChargingStatus.Length == 0,
            "the charging cells to clear",
            token);

        Assert.Equal("旅程未同步", harness.ViewModel.VisitText);
        Assert.Equal(string.Empty, harness.ViewModel.ChargingStatusText);
        Assert.Equal(string.Empty, harness.ViewModel.BatteryStatusText);
        Assert.Equal(string.Empty, harness.ViewModel.BatteryStatus);
        Assert.Empty(harness.ViewModel.JourneyPlanLegs);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A restart on the same journal, against a server that pushes nothing on the new session: the charger plan
    /// and the <c>CHARGING</c> state come back from the journal (<c>RestorePersistedJourneyProjectionAsync</c>)
    /// with their revisions, and read the same words.
    /// </summary>
    [Fact]
    public async Task ARestartRestoresTheChargeFromTheJournalWithTheSameWords()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness first = await StartChargingAsync(token, planRevision: 7);
        string visitBefore = first.ViewModel.VisitText;
        string chargingBefore = first.ViewModel.ChargingStatusText;
        string batteryBefore = first.ViewModel.BatteryStatusText;

        await first.StopVehicleAsync();
        first.Server.SimulateOnboardProcessRestart();
        // Nothing is pushed on the next session, so what is shown can only have come from the journal.
        first.Server.VectorJourneySnapshotsAfterRecovery = [];
        await using Harness second = await Harness.StartAgainstAsync(first.Server, token, first.JournalPath);
        await second.WaitUntilAsync(
            () => second.ViewModel.ChargingStatus == "CHARGING",
            "the restored charge",
            token);

        WireToGateJourneySnapshot restored = second.Session.CurrentJourney;
        Assert.Equal(7, restored.UpcomingStopPlan!.Revision);
        Assert.Equal("CHARGING", restored.VehicleBusinessState!.ActivePurpose);
        Assert.Equal($"在充电桩 {Charger} 充电中", visitBefore);
        Assert.Equal(visitBefore, second.ViewModel.VisitText);
        Assert.Equal(chargingBefore, second.ViewModel.ChargingStatusText);
        Assert.Equal(batteryBefore, second.ViewModel.BatteryStatusText);
        Assert.False(second.ViewModel.CanSubmit);
        Assert.Empty(second.UiErrors);
    }

    private static async Task<Harness> StartChargingAsync(CancellationToken token, long planRevision = 1)
    {
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.VectorJourneySnapshotsAfterRecovery = ["UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"];
                server.JourneyActivePurpose = "CHARGING";
                server.JourneyChargingCycleState = "CHARGING";
                server.JourneyBatteryState = "LOW";
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(planRevision, [ChargerLeg(1, "ARRIVED")])
                };
            },
            token);
        try
        {
            await harness.WaitUntilAsync(
                () => AcknowledgedKinds(harness.Server).Length == 2
                    && harness.ViewModel.ChargingStatus == "CHARGING",
                "the vehicle charging at the charger",
                token);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The entry and the cancellation before any sublot stay shut for half a second, on the view model and on the
    /// business service alike -- held over a window rather than read once, for the reason
    /// <c>WaitingPointIdleReturnG2Tests</c> gives.
    /// </summary>
    private static Task AssertEntryStaysClosedAsync(Harness harness, CancellationToken token) =>
        HoldsForHalfASecondAsync(
            () =>
            {
                Assert.False(harness.ViewModel.CanSubmit);
                Assert.False(harness.Business.CanSubmitSublot);
            },
            token);

    private static Task AssertCancellationStaysClosedAsync(Harness harness, CancellationToken token) =>
        HoldsForHalfASecondAsync(
            () =>
            {
                Assert.False(harness.ViewModel.CanRequestLoadCancellation);
                Assert.False(harness.Business.CanRequestLoadCancellation);
            },
            token);

    private static async Task HoldsForHalfASecondAsync(Action assertion, CancellationToken token)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow.AddMilliseconds(500);
        do
        {
            assertion();
            await Task.Delay(20, token);
        }
        while (DateTimeOffset.UtcNow < until);
    }

    /// <summary>
    /// A stop at <c>ST-01</c> with one worklist item (<c>SUBLOT-A</c>) and an entry request for it, under the given
    /// purpose and plan leg, and whatever else <paramref name="configure"/> sets on the business state.
    /// </summary>
    private static async Task<Harness> StartWithEntryRequestAsync(
        string activePurpose,
        object leg,
        CancellationToken token,
        Action<FakeControlServer>? configure = null)
    {
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneyActivePurpose = activePurpose;
                server.SublotEntryExpectedSublots = ["SUBLOT-A"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, [leg])
                };
                configure?.Invoke(server);
            },
            token);
        try
        {
            await harness.WaitUntilAsync(
                () => harness.Business.ExpectedSublots is not null
                    && harness.Session.CurrentJourney is { CurrentStopWorklist: not null, UpcomingStopPlan: not null }
                    && AcknowledgedKinds(harness.Server).Length == 3,
                "the business state, the worklist, the plan and the entry request",
                token);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    private static object BusinessState(long revision, string activePurpose, string chargingCycleState, string batteryState) =>
        new
        {
            vehicleBusinessStateRevision = revision,
            readiness = "READY",
            activePurpose,
            manualChargingHold = false,
            batteryState,
            chargingCycleState,
            loadingPhase = (object?)null,
            blockingFacts = Array.Empty<object>(),
            observedAt = DateTimeOffset.UtcNow
        };

    private static object BusinessLeg(int sequence, string state) =>
        Payloads.Leg(sequence, "TO_PICKUP", "BUSINESS", ItemADemand, "ST-01", state);

    private static object ChargerLeg(int sequence, string state) =>
        Payloads.Leg(sequence, null, "CHARGER", null, Charger, state);

    private static void AssertShownAsNonBusinessStop(Harness harness)
    {
        Assert.DoesNotContain("旅程未同步", harness.ViewModel.VisitText, StringComparison.Ordinal);
        Assert.DoesNotContain("无待处理任务", harness.ViewModel.VisitText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, harness.ViewModel.StopDirectionText);
        Assert.Equal(string.Empty, harness.ViewModel.TaskTypeText);
        Assert.False(harness.ViewModel.CanSubmit);
        Assert.False(harness.ViewModel.CanRequestLoadCancellation);
    }

    /// <summary>
    /// The vector's two <c>SnapshotAppliedAck</c> -- the plan's and the business state's -- each exactly once, with the
    /// worklist's that this path also sends, in the order the fake sends them; and no <c>ProtocolProblem</c>. Exact,
    /// not a containment: a second ack of either kind or a missing one is a change in what the peer sees.
    /// </summary>
    private static void AssertEachSnapshotAcknowledgedOnce(Harness harness)
    {
        Assert.Equal(
            ["VEHICLE_BUSINESS_STATE", "CURRENT_STOP_WORKLIST", "UPCOMING_STOP_PLAN"],
            AcknowledgedKinds(harness.Server));
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
    }

    private static string[] AcknowledgedKinds(FakeControlServer server) =>
    [
        .. server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SnapshotAppliedAck")
            .Select(item =>
            {
                using JsonDocument document = JsonDocument.Parse(item.WireLine);
                return document.RootElement.GetProperty("payload").GetProperty("snapshotKind").GetString()!;
            })
    ];
}
