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

        Assert.Equal(expected, WireToGateCurrentStopDepartureGate.BelongsToStop(settled, worklist));
        Assert.False(WireToGateCurrentStopDepartureGate.BelongsToStop(settled with { Result = null }, worklist));
    }

    /// <summary>Review L2: the not-allowed refusal names the not-a-staging-stop case too.</summary>
    [Fact]
    public void TheNotAllowedRefusalNamesTheNotAStagingStopCase()
    {
        Assert.Contains(
            "本站不是待送点",
            WireToGateCurrentStopDepartureText.RejectionText("ACTION_NOT_ALLOWED_IN_STATE"),
            StringComparison.Ordinal);
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
