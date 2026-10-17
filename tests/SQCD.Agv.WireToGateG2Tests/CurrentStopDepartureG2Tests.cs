using System.Text;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;
using Harness = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Harness;
using Payloads = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Payloads;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The onboard half of the staging pickup stop's 「出发」 and of its charging-required departure (protocol 4.0.0 candidate,
/// CP-0012 and CP-0013, <c>8005-agv-onboard-hmi#349</c>), and of the machine pickup stop's 「出发」 once its loads are done
/// (CP-0014, <c>8005-agv-onboard-hmi#352</c>), driven from the fake control server through the real session service, business service
/// and <see cref="MainViewModel"/>, wired as <c>App.xaml.cs</c> wires them.
/// </summary>
/// <remarks>
/// <para>
/// The six vectors the 4.0.0 candidate added each have one named test here: five on <c>FP-IS-02</c> -- accepted, the three
/// refusals, the duplicate request -- and the charging-required departure with cargo on <c>FP-IS-13</c>. Their slice traits
/// are the projection of their vector traits (<c>IntegrationSliceTraitArchitectureTests</c>). The tests without a vector trait are the ticket's
/// own acceptance points the vectors do not speak to: the cargo-holding wait with an empty worklist, the entry being processed.
/// The three machine pickup vectors of commit <c>83c8f1f5</c> each have one named test too (#352).
/// </para>
/// <para>
/// The control server is a double here (<see cref="FakeControlServer"/>'s departure half): until
/// <c>8005-agv-control-server#713</c> moves to the same candidate the two ends refuse each other's session, so the two-end run
/// is that ticket's and the first wave's integration.
/// </para>
/// </remarks>
public sealed class CurrentStopDepartureG2Tests
{
    private const string Staging = "ST-01";
    private const string OperationSession = "77777777-7777-4777-8777-777777777777";
    private const string DemandA = "aaaaaaaa-0000-4000-8000-00000000000a";
    private const string DemandB = "bbbbbbbb-0000-4000-8000-00000000000b";
    private const string Operator = "operator-134";
    private const string NextTripOperationSession = "99999999-9999-4999-8999-999999999999";

    static CurrentStopDepartureG2Tests()
    {
        // The harness is MultiDemandJourneyG2Tests'; its credential and operator variables are set by that class's static
        // constructor, which using its nested types alone does not run.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(MultiDemandJourneyG2Tests).TypeHandle);
    }

    /// <summary>
    /// <c>CV-CURRENT-STOP-DEPARTURE-ACCEPTED</c>: at the staging pickup stop the entry is shown and can be pressed, the
    /// confirmation says what the press does, the request echoes the worklist on screen, the result is journaled and
    /// acknowledged with the hash of the line the server sent, and the departure is shown from the server's snapshots --
    /// nothing local moves, opens or closes.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-ACCEPTED")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnAcceptedDepartureIsJournaledAcknowledgedAndShownFromTheSnapshots()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new();
        io.SetCargoPresent(0, true);
        await using Harness harness = await StartAtStagingAsync(
            token,
            server =>
            {
                server.RespondToCurrentStopDepartures = true;
                server.SnapshotsAfterAcceptedDeparture =
                [
                    ("VehicleBusinessStateSnapshot", State(2, Payloads.LoadingPhase("CLOSED", closedReason: "OPERATOR_DEPARTED"))),
                    ("CurrentStopWorklistSnapshot", Worklist(2, null, "OPERATOR_DEPARTED"))
                ];
            },
            io: io);
        CurrentStopDepartureDisplay shown = await WaitForPressableAsync(harness, token);
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.SummaryText).Contains("已装 1 篮", StringComparison.Ordinal),
            "the staging summary to read the loaded slot",
            token);
        shown = harness.OnUi(() => harness.ViewModel.CurrentStopDeparture);

        Assert.Contains("已装 1 篮将出发送货；未装的 2 条退回待派", shown.ConfirmationText, StringComparison.Ordinal);
        Assert.Contains("待装 2 条", shown.SummaryText, StringComparison.Ordinal);
        // 8005-agv-onboard-hmi#352, the regression: the staging stop keeps its summary and its own wording.
        Assert.True(shown.HasSummary);
        Assert.Equal(WireToGateLoadCancellationText.StagingCaption, harness.ViewModel.LoadCancellationCaption);
        HmiButtonStateAssert.ShownAndEnabled(harness, "OnCurrentStopDepartureClick");

        Assert.True(await harness.ViewModel.RequestCurrentStopDepartureAsync(shown.Prompt!, token));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.LoadingClosedReasonCode == "OPERATOR_DEPARTED"
                && ResultAcknowledgements(harness.Server).Length == 1,
            "the result to be acknowledged and the departure to be shown",
            token);

        JsonElement request = Assert.Single(harness.Server.CurrentStopDepartureRequests);
        Assert.True(Guid.TryParse(request.GetProperty("departureRequestId").GetString(), out _));
        Assert.Equal(Staging, request.GetProperty("stationId").GetString());
        Assert.Equal(1, request.GetProperty("worklistRevision").GetInt64());
        Assert.Equal(OperationSession, request.GetProperty("operationSessionId").GetString());
        Assert.Equal(Operator, request.GetProperty("operator").GetProperty("operatorId").GetString());
        Assert.Equal("SESSION", request.GetProperty("operator").GetProperty("verificationMethod").GetString());

        // The vector's order on the vehicle's side: the request, then the acknowledgement of the result.
        Assert.Equal(
            ["CurrentStopDepartureRequested", "DurableAck"],
            harness.Server.ReceivedEnvelopes
                .Where(item => item.MessageType == "CurrentStopDepartureRequested" || IsResultAcknowledgement(item.WireLine))
                .Select(item => item.MessageType));
        AssertAcknowledgesTheSentResult(harness.Server, Assert.Single(ResultAcknowledgements(harness.Server)));

        WireToGateRecoveryState state = await harness.Session.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(request.GetProperty("departureRequestId").GetString(), state.CurrentStopDeparture?.DepartureRequestId);
        Assert.Equal(WireToGateCurrentStopDepartureOutcome.Accepted, state.CurrentStopDeparture?.Result?.Outcome);

        Assert.Equal(LoadingPhaseText.OperatorDepartedText, harness.ViewModel.LoadingClosedReasonText);
        Assert.False(harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.IsShown));
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>CV-CURRENT-STOP-DEPARTURE-STALE-REVISION</c>: the refusal is shown in Chinese, the stop is unchanged, and a new press
    /// is a new request -- the refused one is never resent unchanged.
    /// </summary>
    /// <remarks>
    /// Since protocol#23 the server refuses a stale revision only when it differs in something material, and never applies
    /// that exception at a staging pickup stop -- which is where this runs, so the refusal stands as before. Whether the change
    /// is material is decided by the server alone; the vehicle shows whichever answer comes back, so nothing here narrows.
    /// The acceptance side is <see cref="AnAcceptanceOnAStaleRevisionWithoutMaterialChangeIsAdoptedAsAnyOther"/>.
    /// </remarks>
    [Fact]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-STALE-REVISION")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public Task AStaleWorklistRefusalIsShownAndANewPressIsANewRequest() =>
        AssertRefusalIsShownAsync("WORKLIST_REVISION_STALE");

    /// <summary><c>CV-CURRENT-STOP-DEPARTURE-NOT-ALLOWED</c>: as for a stale worklist.</summary>
    [Fact]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-NOT-ALLOWED")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public Task ANotAllowedRefusalIsShownAndANewPressIsANewRequest() =>
        AssertRefusalIsShownAsync("ACTION_NOT_ALLOWED_IN_STATE");

    /// <summary>
    /// <c>CV-CURRENT-STOP-DEPARTURE-STATION-MISMATCH</c>: as for a stale worklist; the refused press is settled, so the
    /// request of the previous stop is not kept pending.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-STATION-MISMATCH")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public Task AStationMismatchRefusalIsShownAndANewPressIsANewRequest() =>
        AssertRefusalIsShownAsync("STATION_MISMATCH");

    /// <summary>
    /// <c>CV-CURRENT-STOP-DEPARTURE-DUPLICATE-REQUEST</c>: a press whose acknowledgement was lost stays pending, the button stays
    /// shut, and after the link comes back the same request goes out again -- same <c>departureRequestId</c>, same messageId,
    /// same content. The server's replayed result is acknowledged again and changes nothing.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-DUPLICATE-REQUEST")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ALostRequestIsResentUnderTheSameIdAndAReplayedResultChangesNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtStagingAsync(
            token,
            server =>
            {
                server.RespondToCurrentStopDepartures = true;
                server.CurrentStopDepartureAcksToDrop = 1;
            });
        CurrentStopDepartureDisplay shown = await WaitForPressableAsync(harness, token);

        // On file even though nothing answered: the press is pending, and it stays so.
        Assert.True(await harness.ViewModel.RequestCurrentStopDepartureAsync(shown.Prompt!, token));
        CurrentStopDepartureDisplay pending = harness.OnUi(() => harness.ViewModel.CurrentStopDeparture);
        Assert.Equal("PENDING", pending.Status);
        Assert.Equal(WireToGateCurrentStopDepartureText.PendingText, pending.StatusText);
        Assert.False(pending.CanPress);
        string messageId = Assert.Single(
            harness.Server.ReceivedEnvelopes,
            item => item.MessageType == "CurrentStopDepartureRequested").MessageId;

        harness.Server.CloseLatestConnection();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await harness.WaitUntilAsync(
            () => ResultAcknowledgements(harness.Server).Length == 1,
            "the resent request to be answered and the answer acknowledged",
            token);

        JsonElement[] requests = [.. harness.Server.CurrentStopDepartureRequests];
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0].GetRawText(), requests[1].GetRawText());
        Assert.All(
            harness.Server.ReceivedEnvelopes.Where(item => item.MessageType == "CurrentStopDepartureRequested"),
            item => Assert.Equal(messageId, item.MessageId));
        WireToGateRecoveryState settled = await harness.Session.Journal.ReadRecoveryStateAsync(token);
        WireToGateCurrentStopDepartureOutcome first = settled.CurrentStopDeparture!.Result!;

        // The server sends its stored result once more, under a new messageId: acknowledged, and the record stays as it was.
        await harness.Server.SendCurrentStopDepartureResultAsync(
            requests[0].GetProperty("departureRequestId").GetString()!,
            first.Outcome,
            first.ReasonCode,
            first.CurrentWorklistRevision);
        await harness.WaitUntilAsync(
            () => ResultAcknowledgements(harness.Server).Length == 2,
            "the replayed result to be acknowledged",
            token);

        WireToGateRecoveryState after = await harness.Session.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(first, after.CurrentStopDeparture!.Result);
        Assert.Equal(2, harness.Server.CurrentStopDepartureRequests.Count);
        foreach (string acknowledgement in ResultAcknowledgements(harness.Server))
        {
            AssertAcknowledgesTheSentResult(harness.Server, acknowledgement);
        }

        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>CV-STAGING-CHARGING-REQUIRED-DEPARTURE-WITH-CARGO</c> (protocol#15, CP-0013): below the mandatory charge line at the
    /// staging stop with cargo on board, the control server departs the vehicle itself. The vehicle shows it from the snapshots
    /// -- the loading phase closed for charging, the stop ended the same way -- acknowledges each, answers the
    /// <c>DEPARTURE</c> check, and never departs on its own: no departure request goes out, the entry is gone, nothing opens.
    /// The journey carries no charger leg, the purpose stays <c>TRANSPORT</c> and nothing is shown as charging yet.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-STAGING-CHARGING-REQUIRED-DEPARTURE-WITH-CARGO")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    public async Task AChargingRequiredDepartureWithCargoIsShownFromTheSnapshots()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        object[] legs =
        [
            Payloads.Leg(1, "TO_PICKUP", "BUSINESS", DemandA, Staging, "COMPLETED"),
            Payloads.Leg(2, "TO_DROPOFF", "BUSINESS", DemandA, "WB-01", "ACTIVE")
        ];
        await using Harness harness = await StartAtStagingAsync(token);
        await WaitForPressableAsync(harness, token);
        int acknowledgementsBefore = SnapshotAcknowledgements(harness.Server);

        // The vector's order: the business state, the worklist and the plan, each acknowledged.
        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot",
            State(
                2,
                Payloads.LoadingPhase("CLOSED", closedReason: "CHARGING_REQUIRED"),
                batteryState: "MANDATORY_CHARGE"));
        await harness.Server.SendJourneySnapshotAsync("CurrentStopWorklistSnapshot", Worklist(2, null, "CHARGING_REQUIRED"));
        await harness.Server.SendJourneySnapshotAsync("UpcomingStopPlanSnapshot", Payloads.Plan(2, legs));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.UpcomingStopPlan?.Revision == 2,
            "the plan without a charger leg to be applied",
            token);
        await harness.WaitUntilAsync(
            () => SnapshotAcknowledgements(harness.Server) >= acknowledgementsBefore + 3,
            $"the three snapshots to be acknowledged (before: {acknowledgementsBefore}, now: {SnapshotAcknowledgements(harness.Server)})",
            token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.LoadingClosedReasonCode == "CHARGING_REQUIRED",
            $"the charging-required closure to be shown (code: '{harness.ViewModel.LoadingClosedReasonCode}')",
            token);

        Assert.Equal(LoadingPhaseText.ChargingRequiredText, harness.ViewModel.LoadingClosedReasonText);
        Assert.Contains("先送", LoadingPhaseText.ChargingRequiredText, StringComparison.Ordinal);
        Assert.Contains("再充电", LoadingPhaseText.ChargingRequiredText, StringComparison.Ordinal);
        // No charging stop and no charging under way: the charge comes after the journey, from the charging allocation.
        Assert.False(harness.Session.CurrentJourney.IsChargerStop);
        Assert.Equal("NOT_CHARGING", harness.ViewModel.ChargingStatus);
        Assert.Equal("需强制充电", harness.ViewModel.BatteryStatusText);
        Assert.False(harness.Business.CurrentStopDeparture.Shown);
        Assert.False(harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.IsShown));

        // The DEPARTURE check, as the server sends it. The snapshots above can make the vehicle report its safety again, and a
        // check asking about a version older than the one the vehicle has had accepted is refused as expired
        // (PREDEPARTURE_CHECK_EXPIRED) -- the real server then asks again with the newer version, and so does this test.
        for (int attempt = 1; ; attempt++)
        {
            string checkMessageId = Guid.NewGuid().ToString("D");
            await harness.Server.SendCommandAsync(
                "PreDepartureSafetyCheck",
                checkMessageId,
                new
                {
                    preDepartureSafetyCheckId = Guid.NewGuid().ToString("D"),
                    checkPurpose = "DEPARTURE",
                    demandId = DemandA,
                    movementLegId = "22222222-2222-4222-8222-000000000002",
                    expectedSafetyStateVersion = harness.Session.Current.SafetyStateVersion,
                    targetStationId = "WB-01"
                });
            await harness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Any(item => item.MessageType == "PreDepartureSafetyCheckResult")
                    || harness.Server.ReceivedEnvelopes.Any(item =>
                        item.MessageType == "ProtocolProblem" && item.WireLine.Contains(checkMessageId, StringComparison.Ordinal)),
                "the departure check to be answered or refused as expired",
                token);
            if (harness.Server.ReceivedEnvelopes.Any(item => item.MessageType == "PreDepartureSafetyCheckResult"))
            {
                break;
            }

            Assert.Contains(
                harness.Server.ReceivedEnvelopes,
                item => item.MessageType == "ProtocolProblem"
                    && item.WireLine.Contains("PREDEPARTURE_CHECK_EXPIRED", StringComparison.Ordinal));
            Assert.True(attempt < 3, "the departure check was refused as expired three times running");
        }

        // NEVER_DEPART_LOCALLY: the server departed it; the vehicle sent no request of its own and opened nothing.
        Assert.Empty(harness.Server.CurrentStopDepartureRequests);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "SublotSubmitted");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>CV-CURRENT-STOP-DEPARTURE-MACHINE-LOADING-INCOMPLETE</c> (8005-agv-onboard-hmi#352, CP-0014): a machine pickup offers
    /// no 「出发」 while loads are still committed to it, and none when its stop ended with nothing loaded at its last item --
    /// here a cancellation before any sublot. Its own entries are offered as before, under the machine's wording.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-MACHINE-LOADING-INCOMPLETE")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AMachinePickupOffersNoDepartureBeforeItsLoadsAreDone()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtStagingAsync(token, workType: "WIRE_TO_GATE");
        await harness.WaitUntilAsync(
            () => harness.Business.CanSubmitSublot,
            "the machine pickup's entry request",
            token);

        CurrentStopDepartureDisplay display = harness.OnUi(() => harness.ViewModel.CurrentStopDeparture);
        Assert.Equal(CurrentStopDepartureDisplay.Empty, display);
        Assert.False(harness.Business.CurrentStopDeparture.Shown);
        Assert.True(harness.ViewModel.CanRequestLoadCancellation);
        Assert.Equal(WireToGateLoadCancellationText.MachineCaption, harness.ViewModel.LoadCancellationCaption);
        Assert.False(harness.ScreenState().Buttons.Single(button => button.Key == "OnCurrentStopDepartureClick").Visible);

        // The stop ends with its last item cancelled, nothing loaded: still nothing to depart from.
        harness.Server.SublotEntryExpectedSublots = null;
        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot", Worklist(2, null, "LOAD_CANCELLED"));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist?.Revision == 2,
            "the empty worklist of the cancelled stop",
            token);

        Assert.False(harness.Business.CurrentStopDeparture.Shown);
        Assert.Equal(CurrentStopDepartureDisplay.Empty, harness.OnUi(() => harness.ViewModel.CurrentStopDeparture));
        Assert.Empty(harness.Server.CurrentStopDepartureRequests);
    }

    /// <summary>
    /// <c>CV-CURRENT-STOP-DEPARTURE-MACHINE-CARGO-HOLDING</c> (8005-agv-onboard-hmi#352, CP-0014): the machine pickup's
    /// cargo-holding wait -- its loads done, at least one basket loaded -- offers 「出发」 through the departure path of #349,
    /// with the machine's confirmation and no staging summary; the request echoes the worklist on screen, and the departure is
    /// shown from the server's snapshots. Once the plan moves past the stop the entry goes.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-MACHINE-CARGO-HOLDING")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task TheMachineCargoHoldingWaitOffersDepartureAndShowsItFromTheSnapshots()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtMachineLoadsDoneAsync(
            Payloads.LoadingPhase("CARGO_HOLDING_WAIT", cargoHoldingDeadlineAt: DateTimeOffset.UtcNow.AddMinutes(5)),
            token,
            server =>
            {
                server.RespondToCurrentStopDepartures = true;
                server.SnapshotsAfterAcceptedDeparture =
                [
                    ("VehicleBusinessStateSnapshot", State(2, Payloads.LoadingPhase("CLOSED", closedReason: "OPERATOR_DEPARTED"))),
                    ("UpcomingStopPlanSnapshot", Payloads.Plan(2, MachineLegs("COMPLETED", "ACTIVE")))
                ];
            });
        CurrentStopDepartureDisplay shown = await WaitForPressableAsync(harness, token);

        Assert.Equal(WireToGateCurrentStopDepartureText.MachineConfirmationText(0), shown.ConfirmationText);
        Assert.False(shown.HasSummary);
        Assert.Equal(WireToGateLoadCancellationText.MachineCaption, harness.ViewModel.LoadCancellationCaption);
        HmiButtonStateAssert.ShownAndEnabled(harness, "OnCurrentStopDepartureClick");

        Assert.True(await harness.ViewModel.RequestCurrentStopDepartureAsync(shown.Prompt!, token));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.LoadingClosedReasonCode == "OPERATOR_DEPARTED"
                && ResultAcknowledgements(harness.Server).Length == 1
                && !harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.IsShown),
            "the departure to be shown and the entry to go once the plan moves on",
            token);

        JsonElement request = Assert.Single(harness.Server.CurrentStopDepartureRequests);
        Assert.Equal(MachineStation, request.GetProperty("stationId").GetString());
        Assert.Equal(1, request.GetProperty("worklistRevision").GetInt64());
        Assert.Equal(OperationSession, request.GetProperty("operationSessionId").GetString());
        AssertAcknowledgesTheSentResult(harness.Server, Assert.Single(ResultAcknowledgements(harness.Server)));
        Assert.Equal(LoadingPhaseText.OperatorDepartedText, harness.ViewModel.LoadingClosedReasonText);
        // NEVER_DEPART_LOCALLY: nothing opened, nothing moved by the vehicle itself.
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>CV-CURRENT-STOP-DEPARTURE-MACHINE-STATION-WAIT</c> (8005-agv-onboard-hmi#352, CP-0014): a journey that does not hold
    /// cargo -- every zone forbids en-route appends -- still reads <c>LOADING</c> once its loads are done, because the server
    /// does not announce that step; only the station departure wait remains, and 「出发」 is offered all the same. Its loading
    /// phase has in truth ended, so a press only ends the station departure wait and the vehicle goes on to the pickup after
    /// this one: the confirmation puts nothing back (review M1). Accepted, the loading phase stays as it was.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-MACHINE-STATION-WAIT")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task TheStationDepartureWaitAloneOffersDepartureWhileTheScreenStillReadsLoading()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtMachineLoadsDoneAsync(
            Payloads.LoadingPhase("LOADING"),
            token,
            server => server.RespondToCurrentStopDepartures = true,
            legs:
            [
                Payloads.Leg(1, "TO_PICKUP", "BUSINESS", DemandA, MachineStation, "ARRIVED"),
                Payloads.Leg(2, "TO_PICKUP", "BUSINESS", DemandB, "WB-08", "PLANNED"),
                Payloads.Leg(3, "TO_DROPOFF", "BUSINESS", DemandA, "GATE-01", "PLANNED"),
                Payloads.Leg(4, "TO_DROPOFF", "BUSINESS", DemandB, "GATE-02", "PLANNED")
            ]);
        CurrentStopDepartureDisplay shown = await WaitForPressableAsync(harness, token);

        Assert.Equal("LOADING", harness.Session.CurrentJourney.VehicleBusinessState?.LoadingPhase?.State);
        Assert.Equal(WireToGateCurrentStopDepartureText.MachineConfirmationText(0), shown.ConfirmationText);
        Assert.Equal(0, shown.Prompt!.PickupStopsAfter);

        Assert.True(await harness.ViewModel.RequestCurrentStopDepartureAsync(shown.Prompt!, token));
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.Status) == "ACCEPTED"
                && ResultAcknowledgements(harness.Server).Length == 1,
            "the acceptance to be shown",
            token);

        CurrentStopDepartureDisplay accepted = harness.OnUi(() => harness.ViewModel.CurrentStopDeparture);
        Assert.False(accepted.CanPress);
        Assert.Equal(WireToGateCurrentStopDepartureText.AcceptedText, accepted.StatusText);
        Assert.Equal("LOADING", harness.Session.CurrentJourney.VehicleBusinessState?.LoadingPhase?.State);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>CV-CURRENT-STOP-DEPARTURE-STALE-REVISION-ACCEPTED</c> (protocol#23, 8005-agv-onboard-hmi#362): at a machine pickup
    /// the press echoes the worklist on screen, revision 1, while the server has already moved the stop to revision 2 -- an
    /// empty worklist, nothing material changed -- and answers <c>ACCEPTED</c> naming revision 2. The vehicle adopts the
    /// acceptance as it would any other (<c>ADOPT_ACCEPTED_DEPARTURE_ON_STALE_REVISION</c>): the request is not resent under
    /// the newer revision, the departure is shown from the server's snapshots, and nothing moves locally. Whether a stale
    /// revision is material is the server's call alone; the vehicle never compares revisions to decide a departure.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-STALE-REVISION-ACCEPTED")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnAcceptanceOnAStaleRevisionWithoutMaterialChangeIsAdoptedAsAnyOther()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtMachineLoadsDoneAsync(
            Payloads.LoadingPhase("CARGO_HOLDING_WAIT", cargoHoldingDeadlineAt: DateTimeOffset.UtcNow.AddMinutes(5)),
            token,
            server =>
            {
                server.RespondToCurrentStopDepartures = true;
                server.CurrentStopDepartureCurrentWorklistRevision = 2;
                server.SnapshotsAfterAcceptedDeparture =
                [
                    ("VehicleBusinessStateSnapshot", State(2, Payloads.LoadingPhase("CLOSED", closedReason: "OPERATOR_DEPARTED"))),
                    ("UpcomingStopPlanSnapshot", Payloads.Plan(2, [Payloads.Leg(2, "TO_DROPOFF", "BUSINESS", DemandA, "GATE-01", "ACTIVE")]))
                ];
            });
        CurrentStopDepartureDisplay shown = await WaitForPressableAsync(harness, token);

        Assert.True(await harness.ViewModel.RequestCurrentStopDepartureAsync(shown.Prompt!, token));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.LoadingClosedReasonCode == "OPERATOR_DEPARTED"
                && ResultAcknowledgements(harness.Server).Length == 1
                && !harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.IsShown),
            "the departure to be shown and the entry to go once the plan moves on",
            token);

        // ECHO_DISPLAYED_WORKLIST_IN_REQUEST: the revision on screen, not the newer one the server holds.
        JsonElement request = Assert.Single(harness.Server.CurrentStopDepartureRequests);
        Assert.Equal(MachineStation, request.GetProperty("stationId").GetString());
        Assert.Equal(1, request.GetProperty("worklistRevision").GetInt64());
        Assert.Equal(OperationSession, request.GetProperty("operationSessionId").GetString());
        AssertAcknowledgesTheSentResult(harness.Server, Assert.Single(ResultAcknowledgements(harness.Server)));

        // JOURNAL_DEPARTURE_REQUEST_BEFORE_SENDING, and the acceptance on file under the server's current revision.
        WireToGateRecoveryState state = await harness.Session.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(request.GetProperty("departureRequestId").GetString(), state.CurrentStopDeparture?.DepartureRequestId);
        Assert.Equal(1, state.CurrentStopDeparture?.WorklistRevision);
        Assert.Equal(WireToGateCurrentStopDepartureOutcome.Accepted, state.CurrentStopDeparture?.Result?.Outcome);
        Assert.Equal(2, state.CurrentStopDeparture?.Result?.CurrentWorklistRevision);

        Assert.Equal(LoadingPhaseText.OperatorDepartedText, harness.ViewModel.LoadingClosedReasonText);
        // NEVER_DEPART_LOCALLY: nothing opened, nothing moved by the vehicle itself.
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// Review M2's probe: once a press at a machine pickup is accepted, the server may send a newer empty worklist before the
    /// plan moves past the stop -- a refreshed station deadline (<c>COMPLETED</c>, with the operation session) or the one
    /// ending <c>OPERATOR_DEPARTED</c>. While the pickup leg is still <c>ARRIVED</c> the acceptance holds the button shut:
    /// a second press would only draw a refusal.
    /// </summary>
    [Theory]
    [InlineData("COMPLETED", true)]
    [InlineData("OPERATOR_DEPARTED", false)]
    public async Task AnAcceptanceAtAMachinePickupHoldsTheButtonUntilThePlanMovesOn(string stopEndedReason, bool withSession)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtMachineLoadsDoneAsync(
            Payloads.LoadingPhase("CARGO_HOLDING_WAIT", cargoHoldingDeadlineAt: DateTimeOffset.UtcNow.AddMinutes(5)),
            token,
            server => server.RespondToCurrentStopDepartures = true);
        CurrentStopDepartureDisplay shown = await WaitForPressableAsync(harness, token);

        Assert.True(await harness.ViewModel.RequestCurrentStopDepartureAsync(shown.Prompt!, token));
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.Status) == "ACCEPTED",
            "the acceptance to be shown",
            token);

        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            new
            {
                stationId = MachineStation,
                worklistRevision = 2,
                operationSessionId = withSession ? OperationSession : null,
                stationDepartureDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(4),
                stopEndedReason,
                items = Array.Empty<object>()
            });
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist?.Revision == 2,
            "the newer empty worklist",
            token);

        Assert.Equal("ARRIVED", harness.Session.CurrentJourney.CurrentLeg?.State);
        Assert.False(harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.CanPress));
        Assert.Null(harness.Business.CurrentStopDeparture.Prompt);
        Assert.Single(harness.Server.CurrentStopDepartureRequests);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// Ticket item 4: the server's refusal at a machine pickup -- its state moved on under the press -- is shown as at the staging
    /// stop, and the press is offered again.
    /// </summary>
    [Fact]
    public async Task ARefusalAtAMachinePickupIsShownAndThePressOfferedAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtMachineLoadsDoneAsync(
            Payloads.LoadingPhase("CARGO_HOLDING_WAIT"),
            token,
            server =>
            {
                server.RespondToCurrentStopDepartures = true;
                server.RejectCurrentStopDeparturesWith = "ACTION_NOT_ALLOWED_IN_STATE";
            });
        CurrentStopDepartureDisplay shown = await WaitForPressableAsync(harness, token);

        Assert.True(await harness.ViewModel.RequestCurrentStopDepartureAsync(shown.Prompt!, token));
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.Status) == "ACTION_NOT_ALLOWED_IN_STATE",
            "the refusal to be shown",
            token);

        CurrentStopDepartureDisplay refused = harness.OnUi(() => harness.ViewModel.CurrentStopDeparture);
        Assert.Equal(WireToGateCurrentStopDepartureText.RejectionText("ACTION_NOT_ALLOWED_IN_STATE"), refused.StatusText);
        Assert.True(refused.CanPress);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>CV-STAGING-CARGO-HOLDING-DEADLINE-MOVED</c> (CP-0014; 8005-agv-onboard-hmi#352, C11's follow-up): at the staging
    /// pickup stop with a basket on board and an appended demand not yet scanned, the server sends <c>LOADING</c> with the
    /// cargo-holding deadline running. The countdown is shown from it, and when the next
    /// snapshot moves the deadline -- a demand appended -- the countdown takes the new one; nothing is computed locally.
    /// The vector's end state since protocol#19 (<c>3fb214e5</c>) is
    /// <c>STAGING_LOADING_WITH_CARGO_HOLDING_DEADLINE_MOVED_TO_APPEND</c>: the phase stays <c>LOADING</c> throughout.
    /// <c>DISPLAY_CARGO_HOLDING_COUNTDOWN_FROM_DEADLINE_NOT_FROM_STATE</c>: the same <c>LOADING</c> with no deadline -- nothing
    /// loaded yet -- shows no countdown; the deadline, not the phase, puts it on screen.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-STAGING-CARGO-HOLDING-DEADLINE-MOVED")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task TheStagingCountdownShowsWhileLoadingAndTakesTheMovedDeadline()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtStagingAsync(token);
        await WaitForPressableAsync(harness, token);

        // LOADING with no deadline: nothing loaded yet, no countdown -- the phase alone does not put one on screen.
        Assert.Equal("LOADING", harness.Session.CurrentJourney.VehicleBusinessState?.LoadingPhase?.State);
        Assert.Null(harness.Session.CurrentJourney.VehicleBusinessState?.LoadingPhase?.CargoHoldingDeadlineAt);
        Assert.False(harness.OnUi(() => harness.ViewModel.HasCargoHoldingCountdown));
        Assert.Equal(string.Empty, harness.OnUi(() => harness.ViewModel.CargoHoldingCountdownText));

        // The first basket closes safely: the same LOADING, now with the deadline.
        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot",
            State(2, Payloads.LoadingPhase("LOADING", cargoHoldingDeadlineAt: DateTimeOffset.UtcNow.AddMinutes(5))));
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CargoHoldingCountdownStatus) == "ACTIVE",
            "the countdown while loading",
            token);
        // The timer that runs it on the vehicle's clock is MultiDemandViewModelTests' to prove: this harness has no running
        // dispatcher, so no countdown ticks here.
        string loading = harness.OnUi(() => harness.ViewModel.CargoHoldingCountdownText);
        Assert.StartsWith("还有任务待扫码，最迟 ", loading, StringComparison.Ordinal);
        Assert.Matches("带已装的货离站（剩 0[45]:", loading);

        // A demand appended: the plan grows and the next snapshot moves the deadline.
        await harness.Server.SendJourneySnapshotAsync("UpcomingStopPlanSnapshot", Payloads.Plan(2, StagingLegs));
        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot",
            State(3, Payloads.LoadingPhase("LOADING", cargoHoldingDeadlineAt: DateTimeOffset.UtcNow.AddMinutes(9).AddSeconds(30))));
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CargoHoldingCountdownText).Contains("剩 09:", StringComparison.Ordinal),
            "the countdown to take the moved deadline",
            token);

        Assert.True(harness.OnUi(() => harness.ViewModel.HasCargoHoldingCountdown));
        Assert.Equal("LOADING", harness.Session.CurrentJourney.VehicleBusinessState?.LoadingPhase?.State);
        Assert.Empty(harness.Server.CurrentStopDepartureRequests);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>CV-STAGING-CARGO-HOLDING-TIMEOUT</c> (CP-0014; 8005-agv-onboard-hmi#352): the staging stop's deadline expires and the
    /// server closes the phase with <c>CARGO_HOLDING_TIMEOUT</c>, releasing what was not loaded. The vehicle shows the
    /// timeout from the snapshots -- no countdown left on screen though the deadline is still given -- offers no 「出发」 any
    /// more, and departs nothing on its own.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-STAGING-CARGO-HOLDING-TIMEOUT")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task TheStagingTimeoutIsShownFromTheSnapshots()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtStagingAsync(token);
        await WaitForPressableAsync(harness, token);

        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot",
            State(2, Payloads.LoadingPhase(
                "CLOSED",
                cargoHoldingDeadlineAt: DateTimeOffset.UtcNow.AddSeconds(-1),
                closedReason: "CARGO_HOLDING_TIMEOUT")));
        await harness.Server.SendJourneySnapshotAsync("CurrentStopWorklistSnapshot", Worklist(2, null, "DEMAND_RELEASED"));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.LoadingClosedReasonCode == "CARGO_HOLDING_TIMEOUT"
                && harness.Session.CurrentJourney.CurrentStopWorklist?.Revision == 2,
            "the timeout to be shown",
            token);

        Assert.Equal("等单已到期，本车结束等单，前往卸货", harness.ViewModel.LoadingClosedReasonText);
        Assert.False(harness.OnUi(() => harness.ViewModel.HasCargoHoldingCountdown));
        Assert.False(harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.IsShown));
        // NEVER_DEPART_LOCALLY.
        Assert.Empty(harness.Server.CurrentStopDepartureRequests);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The cargo-holding wait at the staging station -- an empty worklist, the time an operator most often departs -- still
    /// offers 「出发」, and the request carries a <c>null</c> <c>operationSessionId</c>. A press pending when the process goes away
    /// is resent after the restart under the same id, and only once the session is up -- never inside the handshake. The wait
    /// still offers 「出发」 after the restart, because the staging station is journaled.
    /// </summary>
    [Fact]
    public async Task TheHoldingWaitAtTheStagingStationOffersDepartureAcrossARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journal = Harness.NewJournalPath();
        await using Harness harness = await StartAtStagingAsync(token, journalPath: journal);
        await WaitForPressableAsync(harness, token);
        await harness.WaitUntilAsync(
            () => harness.Session.Journal.ReadRecoveryStateAsync(token).GetAwaiter().GetResult()
                .StagingPickupStationIds?.Contains(Staging) == true,
            "the staging station to be journaled",
            token);

        // The same instances are what the next handshake replays: a revision sent again with another observedAt is a
        // content conflict, not a replay.
        object holdingState = State(2, Payloads.LoadingPhase("CARGO_HOLDING_WAIT"));
        object holdingWorklist = Worklist(2, null, "COMPLETED");
        harness.Server.JourneySnapshotPayloads = new Dictionary<string, object>
        {
            ["VehicleBusinessStateSnapshot"] = holdingState,
            ["CurrentStopWorklistSnapshot"] = holdingWorklist,
            ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, StagingLegs)
        };
        // Every load is done: no entry request any more.
        harness.Server.SublotEntryExpectedSublots = null;
        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", holdingState);
        await harness.Server.SendJourneySnapshotAsync("CurrentStopWorklistSnapshot", holdingWorklist);
        CurrentStopDepartureDisplay holding = await WaitForPressableAsync(harness, token, revision: 2);
        Assert.Null(holding.Prompt!.OperationSessionId);
        Assert.Contains("待装 0 条", holding.SummaryText, StringComparison.Ordinal);

        // Pressed, and the request lost before the process goes away: it is journaled and in the outbox, unacknowledged.
        harness.Server.CurrentStopDepartureAcksToDrop = 1;
        Assert.True(await harness.ViewModel.RequestCurrentStopDepartureAsync(holding.Prompt!, token));
        string pendingId = Assert.Single(harness.Server.CurrentStopDepartureRequests)
            .GetProperty("departureRequestId").GetString()!;
        int lastConnectionBefore = harness.Server.ReceivedEnvelopes.Max(item => item.Connection);

        await harness.StopVehicleAsync();
        harness.Server.SimulateOnboardProcessRestart();
        harness.Server.RespondToCurrentStopDepartures = true;
        // Refused, so the stop stays as it was and the entry has to come back on its own after the restart.
        harness.Server.RejectCurrentStopDeparturesWith = "WORKLIST_REVISION_STALE";
        await using Harness restarted = await Harness.StartAgainstAsync(harness.Server, token, journal);
        await restarted.WaitUntilAsync(
            () => ResultAcknowledgements(restarted.Server).Length == 1,
            "the pending departure to be resent after the restart and its answer acknowledged",
            token);

        // The direct evidence that the handshake does not replay it (8005-agv-onboard-hmi#349): on the new connection
        // nothing about the departure -- neither the request nor the vehicle's DurableAck of its result -- goes out before
        // the RecoveryStateReport, the handshake's last line before the server's SessionReadiness.
        string[] afterRestart =
        [
            .. restarted.Server.ReceivedEnvelopes
                .Where(item => item.Connection > lastConnectionBefore)
                .Select(item => item.MessageType)
        ];
        int report = Array.IndexOf(afterRestart, "RecoveryStateReport");
        Assert.True(report >= 0, "the restarted vehicle sent no RecoveryStateReport: " + string.Join(", ", afterRestart));
        Assert.DoesNotContain("CurrentStopDepartureRequested", afterRestart[..report]);
        Assert.DoesNotContain("DurableAck", afterRestart[..report]);
        Assert.Contains("CurrentStopDepartureRequested", afterRestart[(report + 1)..]);

        // The same press: same id, same content.
        JsonElement[] requests = [.. restarted.Server.CurrentStopDepartureRequests];
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0].GetRawText(), requests[1].GetRawText());
        Assert.Equal(pendingId, requests[1].GetProperty("departureRequestId").GetString());
        Assert.Equal(JsonValueKind.Null, requests[1].GetProperty("operationSessionId").ValueKind);
        Assert.Equal(2, requests[1].GetProperty("worklistRevision").GetInt64());

        // And the holding wait still offers 「出发」 after the restart: the staging station was journaled.
        CurrentStopDepartureDisplay afterRefusal = await WaitForPressableAsync(restarted, token, revision: 2);
        Assert.Null(afterRefusal.Prompt!.OperationSessionId);
        Assert.Equal("WORKLIST_REVISION_STALE", afterRefusal.Status);
        Assert.Empty(restarted.UiErrors);
    }

    /// <summary>
    /// The server's word as the source: the vehicle starts straight into the cargo-holding wait -- an empty worklist, no staging
    /// worklist ever seen, nothing remembered -- and the plan's <c>publicStationFunction</c> <c>WIRE_STAGING</c> at that station
    /// is what offers 「出发」. The same stop without the function offers nothing, since there is nothing to infer it from.
    /// </summary>
    [Theory]
    [InlineData("WIRE_STAGING", true)]
    [InlineData(null, false)]
    public async Task TheServersStationFunctionAloneOffersDepartureInTheHoldingWait(string? function, bool offered)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        object[] legs =
        [
            FunctionLeg(1, "TO_PICKUP", DemandA, Staging, "COMPLETED", function),
            FunctionLeg(2, "TO_DROPOFF", DemandA, "WB-01", "PLANNED", null)
        ];
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.ReplayJourneySnapshotsWithStableIdentity = true;
                server.SublotEntryExpectedSublots = null;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = State(1, Payloads.LoadingPhase("CARGO_HOLDING_WAIT")),
                    ["CurrentStopWorklistSnapshot"] = Worklist(1, null, "COMPLETED"),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, legs)
                };
            },
            token);
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.UpcomingStopPlan is not null
                && harness.Session.CurrentJourney.CurrentStopWorklist is not null
                && harness.Session.CurrentJourney.VehicleBusinessState is not null,
            "the holding wait's three snapshots",
            token);

        if (offered)
        {
            CurrentStopDepartureDisplay shown = await WaitForPressableAsync(harness, token);
            Assert.Null(shown.Prompt!.OperationSessionId);
        }
        else
        {
            Assert.False(harness.Business.CurrentStopDeparture.Shown);
            Assert.Equal(CurrentStopDepartureDisplay.Empty, harness.OnUi(() => harness.ViewModel.CurrentStopDeparture));
        }

        WireToGateRecoveryState state = await harness.Session.Journal.ReadRecoveryStateAsync(token);
        Assert.Null(state.StagingPickupStationIds);
    }

    /// <summary>
    /// Acceptance 1: while a submitted sublot waits for the server's answer, 「出发」 stays on screen and cannot be pressed, and
    /// says why; the answer -- here a rejection -- gives the press back.
    /// </summary>
    [Fact]
    public async Task ASubmittedEntryHoldsDepartureUntilTheServerAnswersIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtStagingAsync(token);
        await WaitForPressableAsync(harness, token);
        await harness.WaitUntilAsync(() => harness.Business.CanSubmitSublot, "the entry request", token);

        await harness.Business.SubmitSublotAsync("SUBLOT-A", "SCANNER", token);
        await harness.WaitForSubmissionAsync(token);

        CurrentStopDepartureDisplay held = harness.OnUi(() => harness.ViewModel.CurrentStopDeparture);
        Assert.True(held.IsShown);
        Assert.False(held.CanPress);
        Assert.Equal(
            WireToGateCurrentStopDepartureUnavailability.EntryInProgress,
            harness.Business.CurrentStopDeparture.Unavailability);
        Assert.Equal(
            WireToGateCurrentStopDepartureText.UnavailableText(WireToGateCurrentStopDepartureUnavailability.EntryInProgress),
            held.Notice);
        Assert.False(await harness.Business.RequestCurrentStopDepartureAsync(
            new WireToGateCurrentStopDeparturePrompt(Staging, 1, OperationSession, 2, Operator), token));
        Assert.Empty(harness.Server.CurrentStopDepartureRequests);

        string submission = harness.Server.ReceivedEnvelopes
            .Last(item => item.MessageType == "SublotSubmitted").MessageId;
        await harness.Server.SendCommandAsync(
            "SublotRejected",
            Guid.NewGuid().ToString("D"),
            new
            {
                demandId = (string?)null,
                operationSessionId = OperationSession,
                problem = new WireToGateProblemPayload("SUBLOT_MISMATCH", null, null),
                currentWorklistRevision = 1,
                rejectedSublot = "SUBLOT-A"
            },
            submission);
        await WaitForPressableAsync(harness, token);
    }

    /// <summary>
    /// Review M1, the first variant (P1): the acknowledgement of the request is lost and the link stays up. The press is
    /// resent in the same session under the same messageId until it is acknowledged and answered -- not left pending until
    /// the next disconnect, with the button shut and no way out.
    /// </summary>
    [Fact]
    public async Task ALostAcknowledgementWithTheLinkUpIsResentInTheSameSession()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtStagingAsync(
            token,
            server =>
            {
                server.RespondToCurrentStopDepartures = true;
                server.CurrentStopDepartureAcksToDrop = 1;
            });
        CurrentStopDepartureDisplay shown = await WaitForPressableAsync(harness, token);
        int connection = harness.Server.ReceivedEnvelopes.Max(item => item.Connection);

        Assert.True(await harness.ViewModel.RequestCurrentStopDepartureAsync(shown.Prompt!, token));
        // The press as journaled, before anything resends it.
        WireToGateCurrentStopDeparture pressed = (await harness.Session.Journal.ReadRecoveryStateAsync(token))
            .CurrentStopDeparture!;
        await harness.WaitUntilAsync(
            () => ResultAcknowledgements(harness.Server).Length == 1,
            "the lost request to be resent in the same session and answered",
            token);

        (int Connection, string MessageType, string MessageId, string WireLine)[] sends =
        [
            .. harness.Server.ReceivedEnvelopes.Where(item => item.MessageType == "CurrentStopDepartureRequested")
        ];
        Assert.Equal(2, sends.Length);
        Assert.All(sends, item => Assert.Equal(connection, item.Connection));
        Assert.Equal(sends[0].MessageId, sends[1].MessageId);
        // The very line, byte for byte: the server deduplicates by messageId and compares the whole line, so a resend that
        // changed anything -- a new sentAt, a new verifiedAt -- would be a content conflict, not a repeat.
        Assert.Equal(sends[0].WireLine, sends[1].WireLine);
        JsonElement[] requests = [.. harness.Server.CurrentStopDepartureRequests];
        Assert.Equal(requests[0].GetRawText(), requests[1].GetRawText());
        // The resend is not a press (the lesson of 8005-agv-onboard-hmi#236 and #239): the journaled press keeps its id, its
        // messageId and its operator's verification time, and no second id is ever minted.
        WireToGateCurrentStopDeparture afterResend = (await harness.Session.Journal.ReadRecoveryStateAsync(token))
            .CurrentStopDeparture!;
        Assert.Equal(pressed.DepartureRequestId, afterResend.DepartureRequestId);
        Assert.Equal(pressed.MessageId, afterResend.MessageId);
        Assert.Equal(pressed.VerifiedAt, afterResend.VerifiedAt);
        Assert.Equal(pressed with { Result = afterResend.Result }, afterResend);
        Assert.Single(
            requests.Select(item => item.GetProperty("departureRequestId").GetString()).Distinct(StringComparer.Ordinal));
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.Status) == "ACCEPTED",
            "the acceptance to be shown",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// Review M1, the second variant (P1): the server answers the request itself with a <c>ProtocolProblem</c> that is not
    /// given up for good. The press is settled as refused with the code shown as it came, and the operator can press again --
    /// it is not left pending.
    /// </summary>
    [Fact]
    public async Task AProtocolProblemAnsweringTheRequestSettlesItAsRefused()
    {
        const string ReasonCode = "PROTOCOL_SCHEMA_INVALID";
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtStagingAsync(
            token,
            server => server.NextCurrentStopDepartureProtocolProblem = ReasonCode);
        CurrentStopDepartureDisplay shown = await WaitForPressableAsync(harness, token);

        await harness.ViewModel.RequestCurrentStopDepartureAsync(shown.Prompt!, token);
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.Status) == ReasonCode,
            "the refusal to be shown with its code",
            token);

        CurrentStopDepartureDisplay refused = harness.OnUi(() => harness.ViewModel.CurrentStopDeparture);
        Assert.Equal(WireToGateCurrentStopDepartureText.RejectionText(ReasonCode), refused.StatusText);
        Assert.Contains(ReasonCode, refused.StatusText, StringComparison.Ordinal);
        await WaitForPressableAsync(harness, token);
        WireToGateRecoveryState state = await harness.Session.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(WireToGateCurrentStopDepartureOutcome.Rejected, state.CurrentStopDeparture?.Result?.Outcome);
        Assert.Equal(ReasonCode, state.CurrentStopDeparture?.Result?.ReasonCode);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// Review M2 (P2): a settled result belongs to the stop it was for. The next trip back to the same staging station -- a
    /// new operation session, a newer worklist -- shows no 「服务端已受理出发请求」 left over from the last one.
    /// </summary>
    [Fact]
    public async Task ASettledResultIsNotShownOnTheNextTripToTheSameStation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtStagingAsync(
            token,
            server =>
            {
                server.RespondToCurrentStopDepartures = true;
                server.SnapshotsAfterAcceptedDeparture =
                [
                    ("VehicleBusinessStateSnapshot", State(2, Payloads.LoadingPhase("CLOSED", closedReason: "OPERATOR_DEPARTED"))),
                    ("CurrentStopWorklistSnapshot", Worklist(2, null, "OPERATOR_DEPARTED"))
                ];
            });
        CurrentStopDepartureDisplay shown = await WaitForPressableAsync(harness, token);
        Assert.True(await harness.ViewModel.RequestCurrentStopDepartureAsync(shown.Prompt!, token));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.LoadingClosedReasonCode == "OPERATOR_DEPARTED",
            "the departure to be shown",
            token);

        // The next trip: back at the staging station, loading again under a new operation session.
        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", State(3, Payloads.LoadingPhase("LOADING")));
        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            Worklist(
                3,
                NextTripOperationSession,
                null,
                Payloads.Item(DemandA, "TD-A2", "SUBLOT-A2", WireToGateStagingPickupStop.StagingToWire, "PICKUP", 1)));
        CurrentStopDepartureDisplay nextTrip = await WaitForPressableAsync(harness, token, revision: 3);

        Assert.False(nextTrip.HasStatus);
        Assert.Equal(string.Empty, nextTrip.StatusText);
        Assert.Equal(string.Empty, nextTrip.Status);
    }

    /// <summary>
    /// Review L1: once the server has accepted, the button stays shut while the stop has not moved on -- a second press in that
    /// window would draw a refusal under the acceptance. It comes back once the worklist moves past the accepted revision.
    /// </summary>
    [Fact]
    public async Task TheButtonStaysShutAfterAnAcceptanceUntilTheStopMovesOn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtStagingAsync(
            token,
            server => server.RespondToCurrentStopDepartures = true);
        CurrentStopDepartureDisplay shown = await WaitForPressableAsync(harness, token);

        Assert.True(await harness.ViewModel.RequestCurrentStopDepartureAsync(shown.Prompt!, token));
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.Status) == "ACCEPTED",
            "the acceptance to be shown",
            token);

        CurrentStopDepartureDisplay accepted = harness.OnUi(() => harness.ViewModel.CurrentStopDeparture);
        Assert.True(accepted.IsShown);
        Assert.False(accepted.CanPress);
        Assert.Null(accepted.Prompt);
        Assert.False(await harness.Business.RequestCurrentStopDepartureAsync(shown.Prompt!, token));
        Assert.Single(harness.Server.CurrentStopDepartureRequests);

        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            Worklist(
                2,
                OperationSession,
                null,
                Payloads.Item(DemandB, "TD-B", "SUBLOT-B", WireToGateStagingPickupStop.StagingToWire, "PICKUP", 1)));
        await WaitForPressableAsync(harness, token, revision: 2);
    }

    private static async Task AssertRefusalIsShownAsync(string reasonCode)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAtStagingAsync(
            token,
            server =>
            {
                server.RespondToCurrentStopDepartures = true;
                server.RejectCurrentStopDeparturesWith = reasonCode;
            });
        CurrentStopDepartureDisplay shown = await WaitForPressableAsync(harness, token);

        Assert.True(await harness.ViewModel.RequestCurrentStopDepartureAsync(shown.Prompt!, token));
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.Status) == reasonCode
                && ResultAcknowledgements(harness.Server).Length == 1,
            $"the {reasonCode} refusal to be shown and acknowledged",
            token);

        CurrentStopDepartureDisplay refused = harness.OnUi(() => harness.ViewModel.CurrentStopDeparture);
        Assert.Equal(WireToGateCurrentStopDepartureText.RejectionText(reasonCode), refused.StatusText);
        Assert.DoesNotContain("调度", refused.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("#", refused.StatusText, StringComparison.Ordinal);
        Assert.Contains("出发请求未被受理", refused.StatusText, StringComparison.Ordinal);
        AssertAcknowledgesTheSentResult(harness.Server, Assert.Single(ResultAcknowledgements(harness.Server)));
        // Nothing changed: still loading at the staging stop, and the press is offered again.
        Assert.True(refused.CanPress);
        Assert.Equal("LOADING", harness.Session.CurrentJourney.VehicleBusinessState?.LoadingPhase?.State);

        Assert.True(await harness.ViewModel.RequestCurrentStopDepartureAsync(refused.Prompt!, token));
        await harness.WaitUntilAsync(
            () => ResultAcknowledgements(harness.Server).Length == 2,
            "the second press to be answered",
            token);
        string[] ids =
        [
            .. harness.Server.CurrentStopDepartureRequests.Select(item => item.GetProperty("departureRequestId").GetString()!)
        ];
        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    private static readonly object[] StagingLegs =
    [
        Payloads.Leg(1, "TO_PICKUP", "BUSINESS", DemandA, Staging, "ARRIVED"),
        Payloads.Leg(2, "TO_DROPOFF", "BUSINESS", DemandA, "WB-01", "PLANNED"),
        Payloads.Leg(3, "TO_DROPOFF", "BUSINESS", DemandB, "WB-02", "PLANNED")
    ];

    private static Task<Harness> StartAtStagingAsync(
        CancellationToken token,
        Action<FakeControlServer>? configure = null,
        string workType = WireToGateStagingPickupStop.StagingToWire,
        FakeIoModuleClient? io = null,
        string? journalPath = null) =>
        Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.ReplayJourneySnapshotsWithStableIdentity = true;
                server.SublotEntryExpectedSublots = ["SUBLOT-A", "SUBLOT-B"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = State(1, Payloads.LoadingPhase("LOADING")),
                    ["CurrentStopWorklistSnapshot"] = Worklist(
                        1,
                        OperationSession,
                        null,
                        Payloads.Item(DemandA, "TD-A", "SUBLOT-A", workType, "PICKUP", 1),
                        Payloads.Item(DemandB, "TD-B", "SUBLOT-B", workType, "PICKUP", 1)),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, StagingLegs)
                };
                configure?.Invoke(server);
            },
            token,
            journalPath: journalPath,
            io: io);

    private const string MachineStation = "WB-07";

    private static object[] MachineLegs(string pickupState, string dropoffState) =>
    [
        Payloads.Leg(1, "TO_PICKUP", "BUSINESS", DemandA, MachineStation, pickupState),
        Payloads.Leg(2, "TO_DROPOFF", "BUSINESS", DemandA, "GATE-01", dropoffState)
    ];

    /// <summary>
    /// A machine pickup whose committed loads are done: the empty worklist ending <c>COMPLETED</c> the control server sends once
    /// the last load settles (<c>RefreshDeadlineOfALoadedStopAsync</c>), with its operation session and station departure
    /// deadline, at the pickup leg the vehicle stands at.
    /// </summary>
    private static Task<Harness> StartAtMachineLoadsDoneAsync(
        object loadingPhase,
        CancellationToken token,
        Action<FakeControlServer>? configure = null,
        object[]? legs = null) =>
        Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.ReplayJourneySnapshotsWithStableIdentity = true;
                server.SublotEntryExpectedSublots = null;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = State(1, loadingPhase),
                    ["CurrentStopWorklistSnapshot"] = new
                    {
                        stationId = MachineStation,
                        worklistRevision = 1,
                        operationSessionId = OperationSession,
                        stationDepartureDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(5),
                        stopEndedReason = "COMPLETED",
                        items = Array.Empty<object>()
                    },
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, legs ?? MachineLegs("ARRIVED", "PLANNED"))
                };
                configure?.Invoke(server);
            },
            token);

    private static async Task<CurrentStopDepartureDisplay> WaitForPressableAsync(
        Harness harness,
        CancellationToken token,
        long revision = 1)
    {
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CurrentStopDeparture) is { CanPress: true, Prompt: { } prompt }
                && prompt.WorklistRevision == revision,
            $"「出发」 to be pressable at worklist revision {revision}",
            token);
        return harness.OnUi(() => harness.ViewModel.CurrentStopDeparture);
    }

    private static object FunctionLeg(
        int sequence,
        string legType,
        string demandId,
        string stationId,
        string state,
        string? publicStationFunction) =>
        new
        {
            movementLegId = $"22222222-2222-4222-8222-{sequence:D12}",
            legType,
            stopPurposeCategory = "BUSINESS",
            demandId,
            publicStationFunction,
            sequence,
            stationId,
            mapId = "MAP-26",
            state
        };

    private static object State(
        long revision,
        object? loadingPhase,
        string chargingCycleState = "NOT_CHARGING",
        string batteryState = "SUFFICIENT") =>
        new
        {
            vehicleBusinessStateRevision = revision,
            readiness = "READY",
            activePurpose = "TRANSPORT",
            manualChargingHold = false,
            batteryState,
            chargingCycleState,
            loadingPhase,
            blockingFacts = Array.Empty<object>(),
            observedAt = DateTimeOffset.UtcNow
        };

    private static object Worklist(long revision, string? operationSessionId, string? stopEndedReason, params object[] items) =>
        new
        {
            stationId = Staging,
            worklistRevision = revision,
            operationSessionId,
            stationDepartureDeadlineAt = (DateTimeOffset?)null,
            stopEndedReason,
            items
        };

    private static int SnapshotAcknowledgements(FakeControlServer server) =>
        server.ReceivedEnvelopes.Count(item => item.MessageType == "SnapshotAppliedAck");

    private static bool IsResultAcknowledgement(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        JsonElement root = document.RootElement;
        return root.GetProperty("messageType").GetString() == "DurableAck"
            && root.GetProperty("payload").GetProperty("acceptedMessageType").GetString() == "CurrentStopDepartureResult";
    }

    /// <summary>The vehicle's acknowledgements of departure results, as wire lines, oldest first.</summary>
    private static string[] ResultAcknowledgements(FakeControlServer server) =>
    [
        .. server.ReceivedEnvelopes
            .Where(item => item.MessageType == "DurableAck" && IsResultAcknowledgement(item.WireLine))
            .Select(item => item.WireLine)
    ];

    /// <summary>
    /// The acknowledgement names a result the server sent, by messageId, and carries the SHA-256 of that very line: the hash
    /// the control server compares with the line it stored (<c>WireToGateStore.AcknowledgeOutboundEnvelopeAsync</c>).
    /// </summary>
    private static void AssertAcknowledgesTheSentResult(FakeControlServer server, string acknowledgement)
    {
        using JsonDocument document = JsonDocument.Parse(acknowledgement);
        JsonElement root = document.RootElement;
        JsonElement payload = root.GetProperty("payload");
        string acceptedMessageId = payload.GetProperty("acceptedMessageId").GetString()!;
        Assert.Equal(acceptedMessageId, root.GetProperty("correlationId").GetString());
        var sent = Assert.Single(
            server.SentEnvelopes,
            item => item.MessageType == "CurrentStopDepartureResult" && item.MessageId == acceptedMessageId);
        Assert.Equal(
            WireToGateProtocolSerializer.ComputeSha256(Encoding.UTF8.GetBytes(sent.WireLine.TrimEnd('\r', '\n'))),
            payload.GetProperty("acceptedContentSha256").GetString());
    }

    private static class HmiButtonStateAssert
    {
        public static void ShownAndEnabled(Harness harness, string key)
        {
            var button = harness.ScreenState().Buttons.Single(item => item.Key == key);
            Assert.True(button.Visible);
            Assert.True(button.Enabled);
        }
    }
}
