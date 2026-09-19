using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The paths around a multi-demand stop that the ticket names as risks: the loading phase arriving and
/// leaving, a reconnect and a restart, a worklist revision racing an entry, two slot commands at one
/// stop, the pre-departure check, failed and unknown results, and an illegal loading phase.
/// </summary>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// Cargo holding, then the vehicle full, then the stop closed because another vehicle needs it,
    /// then no loading phase: each line appears as a positive fact while its snapshot is the latest --
    /// the countdown's time left comes from the snapshot's deadline, the closed reason's
    /// <c>ItemStatus</c> is <c>WAITING_STATION_YIELD</c> -- and the next snapshot takes it away.
    /// </summary>
    [Fact]
    public async Task TheLoadingPhaseLinesFollowTheServersBusinessStateSnapshots()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        DateTimeOffset holdingDeadline = DateTimeOffset.UtcNow.AddMinutes(10);
        DateTimeOffset stationDeadline = DateTimeOffset.UtcNow.AddMinutes(20);
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(
                        1,
                        Payloads.LoadingPhase("CARGO_HOLDING_WAIT", holdingDeadline)),
                    ["CurrentStopWorklistSnapshot"] = Payloads.WorklistAt(1, stationDeadline, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
            },
            token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.HasCargoHoldingCountdown && harness.ViewModel.HasStationDepartureCountdown,
            "the cargo holding line and the station countdown",
            token);

        string latest = TimeZoneInfo.ConvertTime(holdingDeadline, TimeZoneInfo.Local).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        string holding = harness.ViewModel.CargoHoldingCountdownText;
        Assert.StartsWith($"等待更多任务，最迟 {latest} 离站（剩 ", holding, StringComparison.Ordinal);
        TimeSpan left = TimeSpan.ParseExact(holding[^6..^1], @"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(left, TimeSpan.FromMinutes(9.5), TimeSpan.FromMinutes(10));
        Assert.Equal("ACTIVE", harness.ViewModel.CargoHoldingCountdownStatus);
        // Two deadlines side by side, not one: the station's is its own line, counting to its own deadline.
        TimeSpan stationLeft = TimeSpan.ParseExact(
            harness.ViewModel.StationDepartureCountdownText,
            @"mm\:ss",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(stationLeft, TimeSpan.FromMinutes(19.5), TimeSpan.FromMinutes(20));

        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot",
            Payloads.BusinessState(2, Payloads.LoadingPhase("VEHICLE_FULL")));
        await harness.WaitUntilAsync(() => harness.ViewModel.HasVehicleFullNotice, "the vehicle-full line", token);
        Assert.False(harness.ViewModel.HasCargoHoldingCountdown);
        Assert.Equal("已装满，装完已承诺的任务后离站", harness.ViewModel.VehicleFullNoticeText);

        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot",
            Payloads.BusinessState(3, Payloads.LoadingPhase("CLOSED", closedReason: "WAITING_STATION_YIELD")));
        await harness.WaitUntilAsync(() => harness.ViewModel.HasLoadingClosedReason, "the closed-reason line", token);
        Assert.Equal("WAITING_STATION_YIELD", harness.ViewModel.LoadingClosedReasonCode);
        Assert.Equal("另一辆车需要本站，本车结束等单，前往卸货", harness.ViewModel.LoadingClosedReasonText);
        Assert.False(harness.ViewModel.HasVehicleFullNotice);

        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot",
            Payloads.BusinessState(4, loadingPhase: null));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.VehicleBusinessState?.Revision == 4,
            "the fourth business state",
            token);

        Assert.False(harness.ViewModel.HasLoadingClosedReason);
        Assert.Equal(string.Empty, harness.ViewModel.LoadingClosedReasonCode);
        Assert.False(harness.ViewModel.HasCargoHoldingCountdown);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The connection drops: the session client clears its projection, and the plan list, the
    /// worklist and the cargo holding countdown go with it -- no countdown left running on a stop the
    /// vehicle no longer has a session for.
    /// </summary>
    [Fact]
    public async Task ADroppedConnectionClearsThePlanTheWorklistAndTheCargoHoldingCountdown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(
                        1,
                        Payloads.LoadingPhase("CARGO_HOLDING_WAIT", DateTimeOffset.UtcNow.AddMinutes(10))),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, NineLegsOutOfOrder)
                };
            },
            token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.HasCargoHoldingCountdown
                && harness.WorklistRows().Length == 2
                && harness.PlanLegStatuses().Length == 9,
            "the stop to be shown",
            token);

        await harness.StopServerAsync();
        await harness.WaitUntilAsync(() => !harness.Session.Current.Connected, "the session to drop", token);

        Assert.Empty(harness.PlanLegStatuses());
        Assert.Empty(harness.WorklistRows());
        Assert.False(harness.ViewModel.HasCargoHoldingCountdown);
        Assert.Equal(string.Empty, harness.ViewModel.CargoHoldingCountdownText);
        Assert.False(harness.ViewModel.HasLoadingClosedReason);
        Assert.Equal("旅程未同步", harness.ViewModel.VisitText);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A restart on the same journal: the two items, the nine-leg plan and the loading phase are
    /// restored from the journal's applied snapshots (<c>RestorePersistedJourneyProjectionAsync</c>) with
    /// the revisions they had, before the new session pushes anything; the item whose slot command was
    /// armed before the restart keeps its side from the journal's operation context.
    /// </summary>
    [Fact]
    public async Task ARestartRestoresTheItemsTheNineLegPlanTheLoadingPhaseAndTheSide()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness first = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(
                        3,
                        Payloads.LoadingPhase("CLOSED", closedReason: "PLANNED_LOADING_COMPLETE")),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(5, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(7, NineLegsOutOfOrder)
                };
            },
            token,
            io: io);
        await first.WaitUntilAsync(
            () => first.PlanLegStatuses().Length == 9 && first.WorklistRows().Length == 2,
            "the stop to be shown",
            token);
        await first.Server.SendCommandAsync("SlotOperationCommand", Guid.NewGuid().ToString("D"), SlotCommand(DemandA, AttemptA, [2]), Guid.NewGuid().ToString("D"));
        await first.WaitUntilAsync(
            () => first.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator
                && first.WorklistRows() is [(_, "FRONT"), ..],
            "the load for A to wait on the operator",
            token);
        string[] legsBefore = first.PlanLegStatuses();

        await first.StopVehicleAsync();
        first.Server.SimulateOnboardProcessRestart();
        await using Harness second = await Harness.StartAgainstAsync(first.Server, token, first.JournalPath, io);
        await second.WaitUntilAsync(
            () => second.PlanLegStatuses().Length == 9
                && second.WorklistRows() is [(_, "FRONT"), (_, _)],
            "the restored stop and A's side",
            token);

        WireToGateJourneySnapshot restored = second.Session.CurrentJourney;
        Assert.Equal(5, restored.CurrentStopWorklist!.Revision);
        Assert.Equal(7, restored.UpcomingStopPlan!.Revision);
        Assert.Equal(3, restored.VehicleBusinessState!.Revision);
        Assert.Equal(legsBefore, second.PlanLegStatuses());
        Assert.Equal([("SUBLOT-A", "FRONT"), ("SUBLOT-B", "UNASSIGNED")], second.WorklistRows());
        Assert.True(second.ViewModel.HasLoadingClosedReason);
        Assert.Equal("PLANNED_LOADING_COMPLETE", second.ViewModel.LoadingClosedReasonCode);
        Assert.Empty(second.UiErrors);
    }

    /// <summary>
    /// The operator has the entry request in hand; before the sublot is submitted another demand
    /// finishes and a worklist of a later revision arrives. The entry is refused locally as
    /// <c>SUBLOT_NOT_IN_WORKLIST</c> and nothing is sent: the request no longer belongs to the
    /// worklist in front of the vehicle.
    /// </summary>
    [Fact]
    public async Task AWorklistRevisionArrivingBeforeTheEntryIsSubmittedRefusesItLocally()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.SublotEntryExpectedSublots = ["SUBLOT-A", "SUBLOT-B"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
            },
            token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanSubmitSublot && harness.Session.CurrentJourney.CurrentStopWorklist is not null,
            "the entry request and its worklist",
            token);
        Assert.Equal(["SUBLOT-A", "SUBLOT-B"], harness.Business.ExpectedSublots);

        // A finished elsewhere at this stop: the worklist moves on to revision 2 with B alone.
        await harness.Server.SendJourneySnapshotAsync("CurrentStopWorklistSnapshot", Payloads.Worklist(2, Payloads.ItemB));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist?.Revision == 2,
            "worklist revision 2",
            token);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Business.SubmitSublotAsync("SUBLOT-B", "SCANNER", token));

        Assert.Equal("SUBLOT_NOT_IN_WORKLIST", refused.Message);
        await Task.Delay(200, token);
        Assert.Empty(harness.Submissions);
        Assert.Equal(["SUBLOT-B"], harness.WorklistRows().Select(row => row.Sublot));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// Two demands at one stop, two slot commands back to back: the second waits behind the first on
    /// the single executor and is not refused, and its expected-action clock starts at its own first
    /// unlock -- the time it spent queued is not counted, so with a small threshold no
    /// <c>SLOT_EXPECTED_ACTION_OVERDUE</c> is raised for its slot while it waits or as it starts.
    /// </summary>
    [Fact]
    public async Task ASecondSlotCommandQueuesBehindTheFirstAndItsOverdueClockStartsAtItsOwnUnlock()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TimeSpan threshold = TimeSpan.FromMilliseconds(800);
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
            },
            token,
            io: io);
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null,
            "the worklist",
            token);

        await harness.Server.SendCommandAsync("SlotOperationCommand", Guid.NewGuid().ToString("D"), SlotCommand(DemandA, AttemptA, [1]), Guid.NewGuid().ToString("D"));
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentExpectedActionWait?.SlotOperationAttemptId == AttemptA
                && harness.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator
                && io.UnlockCount == 1,
            "A's slot to be unlocked and waiting on the operator",
            token);
        await harness.Server.SendCommandAsync("SlotOperationCommand", Guid.NewGuid().ToString("D"), SlotCommand(DemandB, AttemptB, [5]), Guid.NewGuid().ToString("D"));

        // B waits in the queue for longer than the threshold.
        DateTimeOffset queuedUntil = DateTimeOffset.UtcNow + threshold * 2;
        while (DateTimeOffset.UtcNow < queuedUntil)
        {
            Assert.Null(OverdueFor(harness, 5, threshold));
            Assert.Equal(1, io.UnlockCount);
            await Task.Delay(50, token);
        }

        Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, item => item.MessageType == "SlotOperationCommandRejected");

        DateTimeOffset aSettledAt = DateTimeOffset.UtcNow;
        io.CloseDoor(0, cargo: true);
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentExpectedActionWait?.SlotOperationAttemptId == AttemptB && io.UnlockCount == 2,
            "B's slot to be unlocked",
            token);

        SlotExpectedActionWait bWait = harness.Business.CurrentExpectedActionWait!;
        Assert.Equal(5, bWait.PhysicalSlotNumber);
        Assert.True(bWait.FirstUnlockAt >= aSettledAt, $"B's clock started at {bWait.FirstUnlockAt:O}, before A settled at {aSettledAt:O}.");
        Assert.Null(OverdueFor(harness, 5, threshold));
        Assert.Equal(2, io.UnlockCount);
        Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, item => item.MessageType == "SlotOperationCommandRejected");
        await harness.WaitUntilAsync(
            () => harness.WorklistRows() is [("SUBLOT-A", "FRONT"), ("SUBLOT-B", "REAR")],
            "both rows to take their side from their own command",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The pre-departure check of a two-item stop names the first accepted demand (spec section 22.2,
    /// item 7). The vehicle does not compare that demand with the worklist: it answers from the IO as
    /// it would for one item -- <c>SAFE</c>, <c>UNSAFE</c> with a door open, <c>UNKNOWN</c> with a slot
    /// unreadable -- and a check about a superseded safety state is refused as
    /// <c>PREDEPARTURE_CHECK_EXPIRED</c>.
    /// </summary>
    [Theory]
    [InlineData("SAFE")]
    [InlineData("UNSAFE")]
    [InlineData("UNKNOWN")]
    [InlineData("PREDEPARTURE_CHECK_EXPIRED")]
    public async Task APreDepartureCheckNamingTheFirstDemandIsAnsweredAsForOneItem(string expected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
            },
            token,
            io: io);
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null && harness.Session.Current.SafetyStateVersion > 0,
            "the worklist and an accepted safety state",
            token);

        if (expected == "UNSAFE")
        {
            await harness.Server.SendCommandAsync("SlotOperationCommand", Guid.NewGuid().ToString("D"), SlotCommand(DemandA, AttemptA, [1]), Guid.NewGuid().ToString("D"));
            await harness.WaitUntilAsync(
                () => harness.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator,
                "A's door to stand open",
                token);
        }
        else if (expected == "UNKNOWN")
        {
            io.SetUnreadable(3);
        }

        await Task.Delay(300, token);
        long version = harness.Session.Current.SafetyStateVersion;
        string checkId = Guid.NewGuid().ToString("D");
        await harness.Server.SendCommandAsync(
            "PreDepartureSafetyCheck",
            Guid.NewGuid().ToString("D"),
            new
            {
                preDepartureSafetyCheckId = checkId,
                // The first demand the stop accepted -- A, not B -- as the control server fills it.
                demandId = DemandA,
                movementLegId = "22222222-2222-4222-8222-000000000002",
                expectedSafetyStateVersion = expected == "PREDEPARTURE_CHECK_EXPIRED" ? version - 1 : version,
                targetStationId = "ST-GATE"
            });

        if (expected == "PREDEPARTURE_CHECK_EXPIRED")
        {
            JsonElement problem = await WaitForPayloadAsync(harness, "ProtocolProblem", token);
            Assert.Equal("PreDepartureSafetyCheck", problem.GetProperty("rejectedMessageType").GetString());
            Assert.Equal("PREDEPARTURE_CHECK_EXPIRED", problem.GetProperty("problem").GetProperty("reasonCode").GetString());
            Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "PreDepartureSafetyCheckResult");
        }
        else
        {
            JsonElement result = await WaitForPayloadAsync(harness, "PreDepartureSafetyCheckResult", token);
            Assert.Equal(checkId, result.GetProperty("preDepartureSafetyCheckId").GetString());
            Assert.Equal(expected, result.GetProperty("outcome").GetString());
            Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        }

        Assert.True(harness.Session.Current.Connected);
        Assert.Equal(2, harness.WorklistRows().Length);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The first demand's load ends <c>FAILED</c> (refused before any unlock) or <c>UNKNOWN</c> (the
    /// door never reached a state the executor could prove) while a second demand waits on the same
    /// stop: the worklist and its side marks stay whole, nothing faults the UI, and the side of the
    /// failed load does not leak onto the other demand's row.
    /// </summary>
    [Theory]
    [InlineData("FAILED")]
    [InlineData("UNKNOWN")]
    public async Task AFailedOrUnknownResultForTheFirstDemandLeavesTheListIntactAndItsSideOnItsOwnRow(string outcome)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = outcome == "UNKNOWN" ? new() { LockerWaitTimesOut = true } : new();
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
            },
            token,
            io: io);
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null,
            "the worklist",
            token);
        if (outcome == "FAILED")
        {
            // Unreadable before the first pulse: the executor's own IO precheck refuses the load.
            io.SetUnreadable(5);
        }

        await harness.Server.SendCommandAsync("SlotOperationCommand", Guid.NewGuid().ToString("D"), SlotCommand(DemandA, AttemptA, [6]), Guid.NewGuid().ToString("D"));
        JsonElement result = await WaitForPayloadAsync(harness, "OperationResult", token);

        Assert.Equal(AttemptA, result.GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal(outcome, result.GetProperty("overallOutcome").GetString());
        await Task.Delay(200, token);
        Assert.Equal([("SUBLOT-A", "REAR"), ("SUBLOT-B", "UNASSIGNED")], harness.WorklistRows());
        Assert.Empty(harness.UiErrors);
        Assert.NotEqual("UNHANDLED_UI_ERROR", harness.Controller.Current.ErrorCode);
    }

    /// <summary>
    /// <c>loadingPhase</c> <c>CLOSED</c> without a <c>closedReason</c> is illegal under the schema's
    /// <c>if/then/else</c>. Widening the counts left that check alone: the snapshot is still refused as
    /// <c>PROTOCOL_SCHEMA_INVALID</c>, and nothing of it reaches the display -- no closed-reason line,
    /// no half-applied state.
    /// </summary>
    [Fact]
    public async Task AClosedLoadingPhaseWithoutAReasonIsStillRefusedAndLeavesNoHalfState()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, Payloads.LoadingPhase("LOADING")),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
            },
            token);
        await harness.WaitUntilAsync(
            () => harness.WorklistRows().Length == 2 && harness.Session.CurrentJourney.UpcomingStopPlan is not null,
            "the stop to be shown",
            token);

        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot",
            Payloads.BusinessState(2, Payloads.LoadingPhase("CLOSED", closedReason: null)));
        JsonElement problem = await WaitForPayloadAsync(harness, "ProtocolProblem", token);

        Assert.Equal("VehicleBusinessStateSnapshot", problem.GetProperty("rejectedMessageType").GetString());
        Assert.Equal("PROTOCOL_SCHEMA_INVALID", problem.GetProperty("problem").GetProperty("reasonCode").GetString());
        await harness.WaitUntilAsync(() => !harness.Session.Current.Connected, "the session to drop", token);
        Assert.False(harness.ViewModel.HasLoadingClosedReason);
        Assert.Equal(string.Empty, harness.ViewModel.LoadingClosedReasonCode);
        Assert.Empty(harness.WorklistRows());
        Assert.Empty(harness.PlanLegStatuses());
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// With two items and the entry request open, the cancellation before any sublot is not offered by
    /// the business service -- choosing a demand is not the vehicle's -- and the view model says so in
    /// its place instead of letting the button vanish. With one item the button is offered as before.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public async Task TheCancellationBeforeSublotIsShownDisabledWithAHintOnlyWhenTheStopHasSeveralItems(int items)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.SublotEntryExpectedSublots = items == 2 ? ["SUBLOT-A", "SUBLOT-B"] : ["SUBLOT-A"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = items == 2
                        ? Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB)
                        : Payloads.Worklist(1, Payloads.ItemA),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
            },
            token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanSubmitSublot && harness.WorklistRows().Length == items,
            "the entry request and the worklist",
            token);
        harness.ViewModel.RefreshWireToGateInputState();

        if (items == 2)
        {
            Assert.False(harness.ViewModel.CanRequestLoadCancellation);
            Assert.True(harness.ViewModel.HasLoadCancellationUnavailableHint);
            Assert.Equal("本站有多条任务，扫码前取消暂不可用", harness.ViewModel.LoadCancellationUnavailableHintText);
        }
        else
        {
            Assert.True(harness.ViewModel.CanRequestLoadCancellation);
            Assert.False(harness.ViewModel.HasLoadCancellationUnavailableHint);
        }
    }

    private const string AttemptA = "44444444-0000-4444-8444-00000000000a";
    private const string AttemptB = "44444444-0000-4444-8444-00000000000b";

    private static object SlotCommand(string demandId, string attemptId, int[] slots) => new
    {
        demandId,
        operationSessionId = OperationSessionId,
        slotOperationAttemptId = attemptId,
        operationType = "LOAD",
        slots,
        expectedBasketCount = slots.Length,
        expectedFinalPhysicalState = "OCCUPIED",
        commandContentSha256 = new string('0', 64)
    };

    /// <summary>The overdue alarm for this slot, as the onboard alarm evaluator would raise it now.</summary>
    private static AlarmEntry? OverdueFor(Harness harness, int slot, TimeSpan threshold)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IoSnapshot io = harness.Io.CurrentSnapshot with { ObservedAt = now };
        IReadOnlyList<AlarmEntry> alarms = OnboardAlarmEvaluator.Evaluate(new OnboardAlarmInputs(
            now,
            true,
            io,
            TimeSpan.FromSeconds(30),
            null,
            new OnboardSnapshot(OnboardState.WaitingArrival, false, true, null, io, null, false, string.Empty, null, now),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500),
            harness.Session.Current.ReasonCodes,
            harness.Business.CurrentOperationSnapshot)
        {
            ExpectedActionWait = harness.Business.CurrentExpectedActionWait,
            ExpectedActionOverdueThreshold = threshold
        });
        return alarms.FirstOrDefault(alarm => alarm.AlarmCode == OnboardAlarmCodes.SlotExpectedActionOverdue
            && alarm.Message.Contains($"{slot}号仓门", StringComparison.Ordinal));
    }

    private static async Task<JsonElement> WaitForPayloadAsync(Harness harness, string messageType, CancellationToken token)
    {
        await harness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(item => item.MessageType == messageType),
            $"the control server to receive {messageType}",
            token);
        using JsonDocument document = JsonDocument.Parse(
            harness.Server.ReceivedEnvelopes.First(item => item.MessageType == messageType).WireLine);
        return document.RootElement.GetProperty("payload").Clone();
    }
}
