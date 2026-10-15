using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;
using WireToGateMovementLeg = SQCD.Agv.Core.WireToGateMovementLeg;
using WireToGateWorklistItem = SQCD.Agv.Core.WireToGateWorklistItem;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The pieces of the staging pickup stop's 「出发」 that need no session (8005-agv-onboard-hmi#349): which stop is a staging
/// pickup stop, the staging stations the journal remembers, the screen's sentences, and the inbound check of
/// <c>CurrentStopDepartureResult</c>.
/// </summary>
/// <remarks>
/// The judgement of the stop is the one that, broken, makes no noise anywhere else: a machine pickup that showed 「出发」 would
/// pass every G2 test that never looks at a machine pickup, and a cargo-holding wait that lost it would leave the operator at
/// the staging station with no way to leave. So every branch is here.
/// </remarks>
public sealed class CurrentStopDepartureTests
{
    private const string Staging = "ST-STAGING";
    private const string Machine = "ST-WB-01";

    [Fact]
    public void AWorklistOfStagingPickupsIsAStagingPickupStop()
    {
        Assert.True(WireToGateStagingPickupStop.Is(Journey(Worklist(Staging, Item("STAGING_TO_WIRE"), Item("STAGING_TO_WIRE"))), []));
    }

    [Theory]
    [InlineData("WIRE_TO_GATE", "PICKUP")]
    [InlineData("DIE_TO_OVEN", "PICKUP")]
    [InlineData("WIRE_TO_OPTICAL", "PICKUP")]
    [InlineData("STAGING_TO_WIRE", "DROPOFF")]
    public void AMachinePickupOrADropoffIsNot(string workType, string stopRole)
    {
        Assert.False(WireToGateStagingPickupStop.Is(Journey(Worklist(Machine, Item(workType, stopRole))), [Staging]));
    }

    [Fact]
    public void AWorklistMixingAStagingPickupWithAnotherTaskTypeIsNot()
    {
        Assert.False(WireToGateStagingPickupStop.Is(
            Journey(Worklist(Staging, Item("STAGING_TO_WIRE"), Item("WIRE_TO_GATE"))),
            [Staging]));
    }

    [Theory]
    [InlineData("LOADING", true)]
    [InlineData("CARGO_HOLDING_WAIT", true)]
    [InlineData("VEHICLE_FULL", false)]
    [InlineData("CLOSED", false)]
    public void AnEmptyWorklistAtAKnownStagingStationCountsOnlyWhileLoadingIsOpen(string state, bool expected)
    {
        WireToGateLoadingPhase phase = new(state, null, state == "CLOSED" ? "OPERATOR_DEPARTED" : null);

        Assert.Equal(expected, WireToGateStagingPickupStop.Is(Journey(Worklist(Staging), phase), [Staging]));
    }

    [Fact]
    public void AnEmptyWorklistAtAStationNeverSeenWithStagingPickupsIsNot()
    {
        WireToGateLoadingPhase holding = new("CARGO_HOLDING_WAIT", null, null);

        Assert.False(WireToGateStagingPickupStop.Is(Journey(Worklist(Machine), holding), [Staging]));
        Assert.False(WireToGateStagingPickupStop.Is(Journey(Worklist(Staging), holding), []));
        Assert.False(WireToGateStagingPickupStop.Is(Journey(Worklist(Staging), loadingPhase: null), [Staging]));
    }

    /// <summary>
    /// Source one, the server's word: a plan leg at the worklist's station naming <c>WIRE_STAGING</c> makes it the staging
    /// station with no remembered station and no staging items -- including the cargo-holding wait's empty worklist.
    /// </summary>
    [Fact]
    public void TheServersStationFunctionMakesTheStagingStationWithoutAnyInference()
    {
        WireToGateLoadingPhase holding = new("CARGO_HOLDING_WAIT", null, null);

        Assert.True(WireToGateStagingPickupStop.Is(WithFunction(Journey(Worklist(Machine), holding), Machine, "WIRE_STAGING"), []));
        Assert.True(WireToGateStagingPickupStop.Is(
            WithFunction(Journey(Worklist(Machine, Item("WIRE_TO_GATE"))), Machine, "WIRE_STAGING"),
            []));
        // Still only a pickup stop with its loading open.
        Assert.False(WireToGateStagingPickupStop.Is(
            WithFunction(Journey(Worklist(Machine), new WireToGateLoadingPhase("CLOSED", null, "VEHICLE_FULL")), Machine, "WIRE_STAGING"),
            []));
        Assert.False(WireToGateStagingPickupStop.Is(
            WithFunction(Journey(Worklist(Machine, Item("DIE_TO_WIRE_STAGING", "DROPOFF"))), Machine, "WIRE_STAGING"),
            []));
    }

    /// <summary>
    /// The server's word wins over the inference both ways: another function at the station is not the staging station, even
    /// with staging items and a remembered station.
    /// </summary>
    [Fact]
    public void AnotherStationFunctionOverridesTheInference()
    {
        WireToGateJourneySnapshot journey = WithFunction(Journey(Worklist(Staging, Item("STAGING_TO_WIRE"))), Staging, "GATE");

        Assert.False(WireToGateStagingPickupStop.Is(journey, [Staging]));
    }

    /// <summary>
    /// Source two, the fallback: a function named only at another station says nothing about this one, and the inference
    /// decides as before.
    /// </summary>
    [Fact]
    public void AFunctionAtAnotherStationLeavesTheInferenceToDecide()
    {
        WireToGateJourneySnapshot journey = WithFunction(Journey(Worklist(Staging, Item("STAGING_TO_WIRE"))), "WB-01", "GATE");

        Assert.True(WireToGateStagingPickupStop.Is(journey, []));
        Assert.Null(WireToGateStagingPickupStop.StationFunctionOf(journey, Staging));
    }

    [Fact]
    public void AChargerStopIsNeverOneEvenWithAStagingWorklist()
    {
        WireToGateJourneySnapshot journey = Journey(Worklist(Staging, Item("STAGING_TO_WIRE"))) with
        {
            UpcomingStopPlan = new WireToGateUpcomingStopPlan(
                1,
                [new WireToGateMovementLeg("leg-1", null, "CHARGER", null, null, 1, "CH-211", "MAP-26", "ACTIVE")],
                new string('0', 64))
        };

        Assert.False(WireToGateStagingPickupStop.Is(journey, [Staging]));
        Assert.Same(WireToGateRecoveryState.Empty, WireToGateRecoveryState.Empty.WithStagingPickupStationOf(journey));
    }

    /// <summary>
    /// 8005-agv-onboard-hmi#348: the staging pickup stop offers no new cancellation before any sublot, whichever way it was
    /// recognized.
    /// </summary>
    [Fact]
    public void TheStagingPickupStopOffersNoCancellationBeforeAnySublot()
    {
        Assert.False(WireToGateStagingPickupStop.OffersLoadCancellationBeforeAnySublot(
            Journey(Worklist(Staging, Item("STAGING_TO_WIRE"), Item("STAGING_TO_WIRE"))), []));
        Assert.False(WireToGateStagingPickupStop.OffersLoadCancellationBeforeAnySublot(
            WithFunction(Journey(Worklist(Staging, Item("WIRE_TO_GATE"))), Staging, "WIRE_STAGING"), []));
    }

    /// <summary>8005-agv-onboard-hmi#348, the regression: every machine pickup still offers it.</summary>
    [Theory]
    [InlineData("WIRE_TO_GATE")]
    [InlineData("WIRE_TO_OPTICAL")]
    [InlineData("WIRE_TO_NITROGEN")]
    [InlineData("DIE_TO_WIRE_STAGING")]
    [InlineData("DIE_TO_OVEN")]
    public void AMachinePickupStillOffersTheCancellationBeforeAnySublot(string workType)
    {
        Assert.True(WireToGateStagingPickupStop.OffersLoadCancellationBeforeAnySublot(
            Journey(Worklist(Machine, Item(workType))), [Staging]));
    }

    /// <summary>
    /// 8005-agv-onboard-hmi#348: the server's station function decides here too -- a staging worklist at a station the server
    /// names as something else still offers it, and a station mixing a staging pickup with another task type is no staging stop.
    /// </summary>
    [Fact]
    public void WhatIsNotAStagingPickupStopStillOffersTheCancellationBeforeAnySublot()
    {
        Assert.True(WireToGateStagingPickupStop.OffersLoadCancellationBeforeAnySublot(
            WithFunction(Journey(Worklist(Staging, Item("STAGING_TO_WIRE"))), Staging, "GATE"), [Staging]));
        Assert.True(WireToGateStagingPickupStop.OffersLoadCancellationBeforeAnySublot(
            Journey(Worklist(Staging, Item("STAGING_TO_WIRE"), Item("WIRE_TO_GATE"))), [Staging]));
    }

    [Fact]
    public void AStagingStationIsRememberedOnceAndAMachineStationNever()
    {
        WireToGateRecoveryState once = WireToGateRecoveryState.Empty.WithStagingPickupStationOf(
            Journey(Worklist(Staging, Item("STAGING_TO_WIRE"))));
        WireToGateRecoveryState twice = once.WithStagingPickupStationOf(Journey(Worklist(Staging, Item("STAGING_TO_WIRE"))));
        WireToGateRecoveryState machine = once.WithStagingPickupStationOf(Journey(Worklist(Machine, Item("WIRE_TO_GATE"))));

        Assert.Equal([Staging], once.StagingPickupStationIds);
        Assert.Same(once, twice);
        Assert.Same(once, machine);
    }

    // ---- 8005-agv-onboard-hmi#352 (CP-0014): 「出发」 at a machine pickup stop once its loads are done ----

    /// <summary>
    /// The ticket's two waits: the cargo-holding wait, and the station departure wait alone -- including a journey that does
    /// not hold cargo, whose screen still reads <c>LOADING</c> because the server does not announce that step. The loading
    /// phase decides nothing: an empty worklist ending <c>COMPLETED</c> at the pickup leg the vehicle stands at does.
    /// </summary>
    [Theory]
    [InlineData("CARGO_HOLDING_WAIT", null)]
    [InlineData("LOADING", null)]
    [InlineData("VEHICLE_FULL", null)]
    [InlineData("CLOSED", "CARGO_HOLDING_TIMEOUT")]
    [InlineData("CLOSED", "PLANNED_LOADING_COMPLETE")]
    public void AMachinePickupWhoseLoadsAreDoneOffersDepartureWhateverItsLoadingPhase(string state, string? closedReason)
    {
        Assert.True(WireToGateMachinePickupStop.OffersDeparture(
            MachineAt(LoadsDone(), new WireToGateLoadingPhase(state, null, closedReason)), [Staging], basketLoadedHere: false));
    }

    /// <summary>Loads still committed to the stop: nothing is offered before every one of them is done.</summary>
    [Fact]
    public void AMachinePickupWithLoadsStillCommittedOffersNoDeparture()
    {
        Assert.False(WireToGateMachinePickupStop.OffersDeparture(
            MachineAt(Worklist(Machine, Item("WIRE_TO_GATE")), new WireToGateLoadingPhase("CARGO_HOLDING_WAIT", null, null)),
            [Staging],
            basketLoadedHere: false));
    }

    /// <summary>
    /// A stop that ended for any other reason loaded nothing at its last item -- cancelled, compensated, released, expired --
    /// and a stop with not one basket loaded is no stop "loaded and done" (CP-0014 section 5, item 7).
    /// </summary>
    [Theory]
    [InlineData("LOAD_CANCELLED")]
    [InlineData("LOAD_COMPENSATED")]
    [InlineData("DEMAND_RELEASED")]
    [InlineData("STATION_DEADLINE_EXPIRED")]
    [InlineData("OPERATOR_DEPARTED")]
    [InlineData("TRIP_TERMINATED")]
    public void AMachinePickupThatEndedWithoutItsLastLoadOffersNoDeparture(string stopEndedReason)
    {
        Assert.False(WireToGateMachinePickupStop.OffersDeparture(
            MachineAt(LoadsDone() with { StopEndedReason = stopEndedReason }), [Staging], basketLoadedHere: false));
    }

    /// <summary>
    /// 8005-agv-onboard-hmi#352, C11's follow-up: a basket loaded at this stop and still on board offers 「出发」 even when the
    /// stop's last item ended otherwise -- A loaded, B cancelled before any sublot. Without one, the same stop offers nothing.
    /// </summary>
    [Theory]
    [InlineData("LOAD_CANCELLED")]
    [InlineData("LOAD_COMPENSATED")]
    public void ABasketLoadedHereOffersDepartureWhateverEndedTheLastItem(string stopEndedReason)
    {
        WireToGateJourneySnapshot journey = MachineAt(LoadsDone() with { StopEndedReason = stopEndedReason, OperationSessionId = null });

        Assert.True(WireToGateMachinePickupStop.OffersDeparture(journey, [Staging], basketLoadedHere: true));
        Assert.False(WireToGateMachinePickupStop.OffersDeparture(journey, [Staging], basketLoadedHere: false));
    }

    /// <summary>A basket loaded here does not offer it while loads are still committed to the stop.</summary>
    [Fact]
    public void ABasketLoadedHereDoesNotOfferItBeforeTheStopsLoadsAreDone()
    {
        Assert.False(WireToGateMachinePickupStop.OffersDeparture(
            MachineAt(Worklist(Machine, Item("WIRE_TO_GATE"))), [Staging], basketLoadedHere: true));
    }

    /// <summary>
    /// Only while the vehicle stands at that pickup: once the plan has moved past it the vehicle has left, and a worklist at
    /// another station than the leg is not this stop's.
    /// </summary>
    [Theory]
    [InlineData("TO_PICKUP", "COMPLETED", Machine)]
    [InlineData("TO_PICKUP", "ACTIVE", Machine)]
    [InlineData("TO_DROPOFF", "ARRIVED", Machine)]
    [InlineData("TO_PICKUP", "ARRIVED", "ST-WB-02")]
    public void OnlyThePickupLegTheVehicleStandsAtOffersIt(string legType, string state, string legStation)
    {
        Assert.False(WireToGateMachinePickupStop.OffersDeparture(
            MachineAt(LoadsDone(), legs: [Leg(1, legType, legStation, state), Leg(2, "TO_DROPOFF", "WB-09", "PLANNED")]),
            [Staging],
            basketLoadedHere: true));
    }

    [Fact]
    public void WithNoPlanThereIsNoStopToDepartFrom()
    {
        Assert.False(WireToGateMachinePickupStop.OffersDeparture(Journey(LoadsDone()), [Staging], basketLoadedHere: false));
    }

    /// <summary>The staging station keeps its own rule (CP-0012), whether the server names it or the vehicle remembers it.</summary>
    [Fact]
    public void TheStagingStationIsNeverAMachinePickup()
    {
        WireToGateJourneySnapshot atStaging = MachineAt(
            LoadsDone() with { StationId = Staging },
            legs: [Leg(1, "TO_PICKUP", Staging, "ARRIVED"), Leg(2, "TO_DROPOFF", "WB-09", "PLANNED")]);

        Assert.False(WireToGateMachinePickupStop.OffersDeparture(atStaging, [Staging], basketLoadedHere: false));
        Assert.False(WireToGateMachinePickupStop.OffersDeparture(WithFunctionAtCurrentLeg(atStaging, "WIRE_STAGING"), [], basketLoadedHere: false));
        Assert.True(WireToGateMachinePickupStop.OffersDeparture(WithFunctionAtCurrentLeg(atStaging, "WIRE_BONDING"), [Staging], basketLoadedHere: false));
    }

    [Fact]
    public void ANonBusinessStopIsNeverOne()
    {
        WireToGateJourneySnapshot journey = MachineAt(LoadsDone());
        WireToGateJourneySnapshot idle = journey with
        {
            VehicleBusinessState = journey.VehicleBusinessState! with { ActivePurpose = "IDLE_RETURN" }
        };

        Assert.False(WireToGateMachinePickupStop.OffersDeparture(idle, [Staging], basketLoadedHere: false));
    }

    /// <summary>
    /// The appended pickups a press puts back to be dispatched (CP-0014: the confirmation names how many): the pickup legs
    /// after the one the vehicle stands at that are not done.
    /// </summary>
    [Fact]
    public void ThePickupStopsAfterThisOneAreCounted()
    {
        WireToGateJourneySnapshot journey = MachineAt(
            LoadsDone(),
            new WireToGateLoadingPhase("LOADING", DateTimeOffset.UnixEpoch.AddMinutes(5), null),
            legs:
            [
                Leg(1, "TO_PICKUP", Machine, "ARRIVED"),
                Leg(2, "TO_PICKUP", "ST-WB-02", "PLANNED"),
                Leg(3, "TO_PICKUP", "ST-WB-03", "PLANNED"),
                Leg(4, "TO_DROPOFF", "WB-09", "PLANNED")
            ]);

        Assert.Equal(2, WireToGateMachinePickupStop.PickupStopsAfterCurrent(journey));
        Assert.Equal(0, WireToGateMachinePickupStop.PickupStopsAfterCurrent(MachineAt(
            LoadsDone(), new WireToGateLoadingPhase("CARGO_HOLDING_WAIT", DateTimeOffset.UnixEpoch.AddMinutes(5), null))));
    }

    /// <summary>
    /// Review M1: a press puts the stops after this one back only while the loading phase is still open with the cargo-holding
    /// deadline running (CP-0014 note 3: it counts as that deadline expiring now). Once the phase has ended -- full, closed,
    /// or a journey that does not hold cargo and so has no deadline -- a press only ends the station departure wait, and the
    /// vehicle goes on to those stops: nothing is put back, and the confirmation names none.
    /// </summary>
    [Theory]
    [InlineData("LOADING", true, null, 1)]
    [InlineData("CARGO_HOLDING_WAIT", true, null, 1)]
    [InlineData("LOADING", false, null, 0)]
    [InlineData("CARGO_HOLDING_WAIT", false, null, 0)]
    [InlineData("VEHICLE_FULL", true, null, 0)]
    [InlineData("CLOSED", true, "CARGO_HOLDING_TIMEOUT", 0)]
    public void OnlyARunningCargoHoldingDeadlinePutsTheStopsAfterThisOneBack(
        string state,
        bool deadline,
        string? closedReason,
        int expected)
    {
        WireToGateJourneySnapshot journey = MachineAt(
            LoadsDone(),
            new WireToGateLoadingPhase(state, deadline ? DateTimeOffset.UnixEpoch.AddMinutes(5) : null, closedReason),
            legs:
            [
                Leg(1, "TO_PICKUP", Machine, "ARRIVED"),
                Leg(2, "TO_PICKUP", "ST-WB-02", "PLANNED"),
                Leg(3, "TO_DROPOFF", "WB-09", "PLANNED")
            ]);

        Assert.Equal(expected, WireToGateMachinePickupStop.PickupStopsAfterCurrent(journey));
    }

    // ---- review M3: every branch of the entry's judgement, one fact at a time from "everything clear" ----

    [Fact]
    public void WithEverythingClearThePressIsOffered()
    {
        Assert.Equal(
            WireToGateCurrentStopDepartureUnavailability.None,
            WireToGateCurrentStopDepartureGate.Judge(WireToGateCurrentStopDepartureFacts.Clear));
    }

    [Fact]
    public void ASlotOperationRunningUnsettledOrAwaitingAcknowledgementHoldsThePress()
    {
        WireToGateCurrentStopDepartureFacts clear = WireToGateCurrentStopDepartureFacts.Clear;
        WireToGateCurrentStopDepartureFacts[] held =
        [
            clear with { SlotOperationRunning = true },
            clear with { SlotOperationUnsettled = true },
            clear with { ResultsAwaitingAcknowledgement = true }
        ];

        Assert.All(held, facts => Assert.Equal(
            WireToGateCurrentStopDepartureUnavailability.SlotOperationInProgress,
            WireToGateCurrentStopDepartureGate.Judge(facts)));
    }

    [Fact]
    public void AnOpenCorrectionOrCancellationHoldsThePress()
    {
        WireToGateCurrentStopDepartureFacts clear = WireToGateCurrentStopDepartureFacts.Clear;

        Assert.Equal(
            WireToGateCurrentStopDepartureUnavailability.CorrectionOrCancellationOpen,
            WireToGateCurrentStopDepartureGate.Judge(clear with { RecoveryVectorOpen = true }));
        Assert.Equal(
            WireToGateCurrentStopDepartureUnavailability.CorrectionOrCancellationOpen,
            WireToGateCurrentStopDepartureGate.Judge(clear with { LoadCancellationOpen = true }));
    }

    /// <summary>
    /// A session that is not Ready -- RecoveryRequired, or no session -- holds the press as a session problem; a latched severe
    /// safety fault holds it as what it is, with its own sentence (review L3), even when the session is not Ready either.
    /// </summary>
    [Fact]
    public void ASessionNotReadyAndALatchedSafetyFaultHoldThePressEachAsWhatItIs()
    {
        WireToGateCurrentStopDepartureFacts clear = WireToGateCurrentStopDepartureFacts.Clear;

        Assert.Equal(
            WireToGateCurrentStopDepartureUnavailability.SessionNotReady,
            WireToGateCurrentStopDepartureGate.Judge(clear with { SessionReady = false }));
        Assert.Equal(
            WireToGateCurrentStopDepartureUnavailability.SafetyFaultLatched,
            WireToGateCurrentStopDepartureGate.Judge(clear with { SafetyFaultLatched = true }));
        Assert.Equal(
            WireToGateCurrentStopDepartureUnavailability.SafetyFaultLatched,
            WireToGateCurrentStopDepartureGate.Judge(clear with { SafetyFaultLatched = true, SessionReady = false }));
        Assert.Equal(
            "严重安全故障锁存中，不能出发",
            WireToGateCurrentStopDepartureText.UnavailableText(WireToGateCurrentStopDepartureUnavailability.SafetyFaultLatched));
        Assert.DoesNotContain(
            "连接",
            WireToGateCurrentStopDepartureText.UnavailableText(WireToGateCurrentStopDepartureUnavailability.SafetyFaultLatched),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoOperatorHoldsThePress()
    {
        Assert.Equal(
            WireToGateCurrentStopDepartureUnavailability.NoOperator,
            WireToGateCurrentStopDepartureGate.Judge(WireToGateCurrentStopDepartureFacts.Clear with { OperatorSignedIn = false }));
    }

    [Fact]
    public void APendingPressAnAcceptanceAtThisStopOrAnEntryInProgressHoldThePress()
    {
        WireToGateCurrentStopDepartureFacts clear = WireToGateCurrentStopDepartureFacts.Clear;

        Assert.Equal(
            WireToGateCurrentStopDepartureUnavailability.AwaitingResult,
            WireToGateCurrentStopDepartureGate.Judge(clear with { DepartureAwaitingResult = true }));
        Assert.Equal(
            WireToGateCurrentStopDepartureUnavailability.AwaitingResult,
            WireToGateCurrentStopDepartureGate.Judge(clear with { AcceptedAtThisStop = true }));
        Assert.Equal(
            WireToGateCurrentStopDepartureUnavailability.EntryInProgress,
            WireToGateCurrentStopDepartureGate.Judge(clear with { EntryInProgress = true }));
    }

    /// <summary>Review M2/L1: a settled press belongs to the station it was for, up to the revision its result named.</summary>
    [Theory]
    [InlineData(Staging, 3, true)]
    [InlineData(Staging, 2, true)]
    [InlineData(Staging, 4, false)]
    [InlineData(Machine, 3, false)]
    public void ASettledPressBelongsToItsStopUntilTheWorklistMovesPastIt(string stationId, long revision, bool expected)
    {
        WireToGateCurrentStopDeparture settled = new WireToGateCurrentStopDeparture(
            "00000000-0000-4000-8000-000000000001",
            "00000000-0000-4000-8000-000000000002",
            Staging,
            2,
            null,
            "operator-1",
            "SESSION",
            DateTimeOffset.UnixEpoch) with
        {
            Result = new("00000000-0000-4000-8000-000000000003", new string('a', 64), "ACCEPTED", null, 3, DateTimeOffset.UnixEpoch)
        };
        WireToGateCurrentStopWorklist worklist = Worklist(stationId) with { Revision = revision };

        Assert.Equal(expected, WireToGateCurrentStopDepartureGate.BelongsToStop(settled, worklist, currentLeg: null));
        Assert.False(WireToGateCurrentStopDepartureGate.BelongsToStop(settled with { Result = null }, worklist, currentLeg: null));
    }

    /// <summary>
    /// Review M2: a press at a machine pickup stop belongs to the stop's pickup leg for as long as the vehicle stands at it
    /// (<c>ARRIVED</c>), whatever worklist revisions come after -- the server may send another empty worklist before the
    /// plan moves (a refreshed deadline, or one ending <c>OPERATOR_DEPARTED</c>). Once the leg is left, or another leg is the
    /// current one, it no longer does.
    /// </summary>
    [Theory]
    [InlineData("leg-1", "ARRIVED", true)]
    [InlineData("leg-1", "ACTIVE", false)]
    [InlineData("leg-1", "COMPLETED", false)]
    [InlineData("leg-2", "ARRIVED", false)]
    public void AMachinePressBelongsToItsPickupLegWhileTheVehicleStandsThere(string legId, string state, bool expected)
    {
        WireToGateCurrentStopDeparture settled = new WireToGateCurrentStopDeparture(
            "00000000-0000-4000-8000-000000000001",
            "00000000-0000-4000-8000-000000000002",
            Machine,
            2,
            null,
            "operator-1",
            "SESSION",
            DateTimeOffset.UnixEpoch) with
        {
            MovementLegId = "leg-1",
            Result = new("00000000-0000-4000-8000-000000000003", new string('a', 64), "ACCEPTED", null, 2, DateTimeOffset.UnixEpoch)
        };
        WireToGateCurrentStopWorklist newer = LoadsDone() with { Revision = 5 };

        Assert.Equal(expected, WireToGateCurrentStopDepartureGate.BelongsToStop(settled, newer, Leg(1, "TO_PICKUP", Machine, state) with
        {
            MovementLegId = legId
        }));
    }

    /// <summary>
    /// Review L2 of 8005-agv-onboard-hmi#349, moved by #352: a machine pickup is no longer refused for not being the staging
    /// stop, so the not-allowed refusal names what does refuse it there -- loads not yet done -- and no longer the staging stop.
    /// </summary>
    [Fact]
    public void TheNotAllowedRefusalNamesALoadNotYetDone()
    {
        string text = WireToGateCurrentStopDepartureText.RejectionText("ACTION_NOT_ALLOWED_IN_STATE");

        Assert.Contains("本站还没装完", text, StringComparison.Ordinal);
        Assert.DoesNotContain("待送点", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// CP-0014 (UC-002): the machine confirmation says the vehicle leaves at once, that no correction is possible afterwards,
    /// and how many appended pickups go back to be dispatched -- not the staging stop's baskets and items.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public void TheMachineConfirmationSaysItLeavesAtOnceWithNoCorrectionAfterwards(int pickupStopsAfter, bool namesThem)
    {
        string text = WireToGateCurrentStopDepartureText.MachineConfirmationText(pickupStopsAfter);

        Assert.StartsWith("本站已装完", text, StringComparison.Ordinal);
        Assert.Contains("立即离站", text, StringComparison.Ordinal);
        // 用户 10-15 定：持货时屏上倒计时的标签是「本站作业剩余」，不点名哪一个标签。
        Assert.Contains("不再等本站倒计时走完", text, StringComparison.Ordinal);
        Assert.DoesNotContain("离站剩余", text, StringComparison.Ordinal);
        Assert.Contains("不能再在本站纠错", text, StringComparison.Ordinal);
        Assert.Equal(namesThem, text.Contains($"后面还没装货的 {pickupStopsAfter} 个取货站点退回待派", StringComparison.Ordinal));
        Assert.DoesNotContain("待送点", text, StringComparison.Ordinal);
        Assert.DoesNotContain("调度", text, StringComparison.Ordinal);
        Assert.DoesNotContain("#", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSummaryCodeCarriesTheFiveNumbersWithoutText()
    {
        Assert.Equal(
            "LOADED=1;FRONT_EMPTY=3;REAR_EMPTY=4;UNKNOWN=0;PENDING=2",
            WireToGateCurrentStopDepartureText.SummaryCode(1, 3, 4, 0, 2));
    }

    [Theory]
    [InlineData(2, 3, "已装 2 篮将出发送货；未装的 3 条退回待派。")]
    [InlineData(2, 0, "已装 2 篮将出发送货。")]
    [InlineData(0, 3, "本站一篮未装，车将留在待送点；未装的 3 条退回待派。")]
    public void TheConfirmationSaysWhatThePressDoes(int loaded, int unloaded, string expectedStart)
    {
        Assert.StartsWith(
            expectedStart,
            WireToGateCurrentStopDepartureText.ConfirmationText(loaded, unloaded),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("STATION_MISMATCH")]
    [InlineData("WORKLIST_REVISION_STALE")]
    [InlineData("ACTION_NOT_ALLOWED_IN_STATE")]
    [InlineData("SOME_FUTURE_REASON")]
    public void ARefusalIsShownInChineseWithNoTicketAndNoCoordinator(string reasonCode)
    {
        string text = WireToGateCurrentStopDepartureText.RejectionText(reasonCode);

        Assert.StartsWith("出发请求未被受理", text, StringComparison.Ordinal);
        Assert.DoesNotContain("调度", text, StringComparison.Ordinal);
        Assert.DoesNotContain("#", text, StringComparison.Ordinal);
        if (reasonCode == "SOME_FUTURE_REASON")
        {
            // Not mapped onto a known reason: the raw code, for the person on the phone.
            Assert.Contains(reasonCode, text, StringComparison.Ordinal);
            Assert.Contains("联系系统维护人员", text, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(reasonCode, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void APressIsPendingUntilItsResultIsOnFile()
    {
        WireToGateCurrentStopDeparture pending = new(
            "00000000-0000-4000-8000-000000000001",
            "00000000-0000-4000-8000-000000000002",
            Staging,
            3,
            null,
            "operator-1",
            "SESSION",
            DateTimeOffset.UnixEpoch);
        WireToGateCurrentStopDeparture accepted = pending with
        {
            Result = new("00000000-0000-4000-8000-000000000003", new string('a', 64), "ACCEPTED", null, 3, DateTimeOffset.UnixEpoch)
        };

        Assert.Equal(WireToGateCurrentStopDepartureText.PendingText, WireToGateCurrentStopDepartureText.StatusText(pending));
        Assert.Equal("PENDING", WireToGateCurrentStopDepartureText.Status(pending));
        Assert.Equal(WireToGateCurrentStopDepartureText.AcceptedText, WireToGateCurrentStopDepartureText.StatusText(accepted));
        Assert.Equal("ACCEPTED", WireToGateCurrentStopDepartureText.Status(accepted));
        Assert.Equal(string.Empty, WireToGateCurrentStopDepartureText.StatusText(null));
    }

    [Theory]
    [InlineData("OPERATOR_DEPARTED", "已按出发，未装的任务退回待派")]
    [InlineData("CHARGING_REQUIRED", "电量低，先送车上的货再充电，未装的任务退回待派")]
    public void TheTwoNewStopEndedReasonsHaveTheirOwnSentence(string reason, string expected)
    {
        Assert.Equal(expected, WireToGateStopEndedReasonText.Describe(reason));
    }

    [Theory]
    [InlineData("ACCEPTED", null)]
    [InlineData("REJECTED", "WORKLIST_REVISION_STALE")]
    public void AResultInTheSchemasShapeIsTaken(string outcome, string? reasonCode)
    {
        WireToGateCurrentStopDepartureResult result = WireToGateSessionClient.CreateCurrentStopDepartureResult(
            Envelope(outcome, reasonCode is null ? null : new WireToGateProblemPayload(reasonCode, null, null)));

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reasonCode, result.ReasonCode);
        Assert.Equal(7, result.CurrentWorklistRevision);
        Assert.Matches("^[0-9a-f]{64}$", result.ContentSha256);
    }

    [Fact]
    public void AResultOutsideTheSchemasShapeIsRefused()
    {
        WireToGateProblemPayload problem = new("STATION_MISMATCH", null, null);
        WireToGateEnvelope[] refused =
        [
            Envelope("ACCEPTED", problem),
            Envelope("REJECTED", null),
            Envelope("MAYBE", null),
            Envelope("REJECTED", new WireToGateProblemPayload(string.Empty, null, null)),
            Envelope("ACCEPTED", null, currentWorklistRevision: -1)
        ];

        foreach (WireToGateEnvelope envelope in refused)
        {
            Assert.Equal(
                "PROTOCOL_SCHEMA_INVALID",
                Assert.Throws<InvalidDataException>(() => WireToGateSessionClient.CreateCurrentStopDepartureResult(envelope))
                    .Message);
        }

        Assert.Equal(
            "CORRELATION_INVALID",
            Assert.Throws<InvalidDataException>(() => WireToGateSessionClient.CreateCurrentStopDepartureResult(
                Envelope("ACCEPTED", null) with { CorrelationId = "00000000-0000-4000-8000-000000000009" })).Message);
    }

    private static WireToGateEnvelope Envelope(
        string outcome,
        WireToGateProblemPayload? problem,
        long currentWorklistRevision = 7) =>
        WireToGateProtocolSerializer.Create(
            "CurrentStopDepartureResult",
            "00000000-0000-4000-8000-000000000010",
            null,
            "AGV-8005-01",
            1,
            DateTimeOffset.UnixEpoch,
            new CurrentStopDepartureResultPayload(
                "00000000-0000-4000-8000-000000000011",
                outcome,
                problem,
                currentWorklistRevision));

    private static WireToGateJourneySnapshot WithFunction(
        WireToGateJourneySnapshot journey,
        string stationId,
        string function) =>
        journey with
        {
            UpcomingStopPlan = new WireToGateUpcomingStopPlan(
                1,
                [
                    new WireToGateMovementLeg("leg-1", "TO_PICKUP", "BUSINESS", "d-1", function, 1, stationId, "MAP-26", "COMPLETED"),
                    new WireToGateMovementLeg("leg-2", "TO_DROPOFF", "BUSINESS", "d-1", null, 2, "WB-09", "MAP-26", "PLANNED")
                ],
                new string('0', 64))
        };

    private static WireToGateCurrentStopWorklist LoadsDone() =>
        new(Machine, 3, "00000000-0000-4000-8000-000000000021", null, [], new string('0', 64), "COMPLETED");

    private static WireToGateMovementLeg Leg(int sequence, string legType, string stationId, string state) =>
        new($"leg-{sequence}", legType, "BUSINESS", "d-1", null, sequence, stationId, "MAP-26", state);

    /// <summary>A machine pickup the vehicle stands at: its pickup leg <c>ARRIVED</c>, the dropoff ahead.</summary>
    private static WireToGateJourneySnapshot MachineAt(
        WireToGateCurrentStopWorklist worklist,
        WireToGateLoadingPhase? loadingPhase = null,
        WireToGateMovementLeg[]? legs = null) =>
        Journey(worklist, loadingPhase ?? new WireToGateLoadingPhase("CARGO_HOLDING_WAIT", null, null)) with
        {
            UpcomingStopPlan = new WireToGateUpcomingStopPlan(
                1,
                legs ?? [Leg(1, "TO_PICKUP", worklist.StationId, "ARRIVED"), Leg(2, "TO_DROPOFF", "WB-09", "PLANNED")],
                new string('0', 64))
        };

    private static WireToGateJourneySnapshot WithFunctionAtCurrentLeg(WireToGateJourneySnapshot journey, string function) =>
        journey with
        {
            UpcomingStopPlan = journey.UpcomingStopPlan! with
            {
                Legs = [.. journey.UpcomingStopPlan.Legs.Select(leg => leg.Sequence == 1 ? leg with { PublicStationFunction = function } : leg)]
            }
        };

    private static WireToGateWorklistItem Item(string workType, string stopRole = "PICKUP") =>
        new(Guid.NewGuid().ToString("D"), "TD-1", "SUBLOT-1", workType, stopRole, 1);

    private static WireToGateCurrentStopWorklist Worklist(string stationId, params WireToGateWorklistItem[] items) =>
        new(stationId, 1, items.Length == 0 ? null : "00000000-0000-4000-8000-000000000020", null, items, new string('0', 64),
            items.Length == 0 ? "COMPLETED" : null);

    private static WireToGateJourneySnapshot Journey(
        WireToGateCurrentStopWorklist worklist,
        WireToGateLoadingPhase? loadingPhase = null) =>
        new(
            new WireToGateVehicleBusinessState(
                1,
                "READY",
                "TRANSPORT",
                false,
                "SUFFICIENT",
                "NOT_CHARGING",
                loadingPhase,
                [],
                DateTimeOffset.UnixEpoch,
                new string('0', 64)),
            worklist,
            null,
            DateTimeOffset.UnixEpoch);
}
