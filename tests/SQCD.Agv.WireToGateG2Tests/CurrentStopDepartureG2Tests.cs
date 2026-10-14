using System.Text;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;
using Harness = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Harness;
using Payloads = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Payloads;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The onboard half of the staging pickup stop's 「出发」 and of charging with cargo (protocol 4.0.0 candidate, CP-0012,
/// <c>8005-agv-onboard-hmi#349</c>), driven from the fake control server through the real session service, business service
/// and <see cref="MainViewModel"/>, wired as <c>App.xaml.cs</c> wires them.
/// </summary>
/// <remarks>
/// <para>
/// The six vectors the 4.0.0 candidate added each have one named test here: five on <c>FP-IS-02</c> -- accepted, the three
/// refusals, the duplicate request -- and the charging stop with cargo on <c>FP-IS-13</c>. Their slice traits are the projection
/// of their vector traits (<c>IntegrationSliceTraitArchitectureTests</c>). The tests without a vector trait are the ticket's
/// own acceptance points the vectors do not speak to: the machine pickup that must not change, the cargo-holding wait with an
/// empty worklist, the entry being processed.
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
    private const string Charger = "CH-211";
    private const string OperationSession = "77777777-7777-4777-8777-777777777777";
    private const string DemandA = "aaaaaaaa-0000-4000-8000-00000000000a";
    private const string DemandB = "bbbbbbbb-0000-4000-8000-00000000000b";
    private const string Operator = "operator-134";

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
    /// <c>CV-TRANSPORT-CHARGING-STOP-WITH-CARGO</c>: under <c>TRANSPORT</c>, a journey whose plan carries a charger leg shows the
    /// loading phase closed for charging, the charging cycle and the charger visit; the charger stop offers no entry, no
    /// cancellation and no departure, and nothing opens.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-TRANSPORT-CHARGING-STOP-WITH-CARGO")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    public async Task ChargingWithCargoIsShownAndTheChargerStopOffersNoEntry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        object[] legs =
        [
            Payloads.Leg(1, "TO_PICKUP", "BUSINESS", DemandA, Staging, "COMPLETED"),
            Payloads.Leg(2, null, "CHARGER", null, Charger, "ACTIVE"),
            Payloads.Leg(3, "TO_DROPOFF", "BUSINESS", DemandA, "WB-01", "PLANNED")
        ];
        await using Harness harness = await StartAtStagingAsync(token);
        await WaitForPressableAsync(harness, token);

        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot",
            State(
                2,
                Payloads.LoadingPhase("CLOSED", closedReason: "CHARGING_REQUIRED"),
                chargingCycleState: "EN_ROUTE",
                batteryState: "MANDATORY_CHARGE"));
        await harness.Server.SendJourneySnapshotAsync("CurrentStopWorklistSnapshot", Worklist(2, null, "CHARGING_REQUIRED"));
        await harness.Server.SendJourneySnapshotAsync("UpcomingStopPlanSnapshot", Payloads.Plan(2, legs));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.ChargingStatus == "EN_ROUTE"
                && harness.ViewModel.LoadingClosedReasonCode == "CHARGING_REQUIRED"
                && harness.Session.CurrentJourney.UpcomingStopPlan?.Revision == 2,
            "the charging stop with cargo to be shown",
            token);

        Assert.Equal(LoadingPhaseText.ChargingRequiredText, harness.ViewModel.LoadingClosedReasonText);
        Assert.Equal($"前往充电桩 {Charger}", harness.ViewModel.VisitText);
        Assert.Equal("前往充电", harness.ViewModel.ChargingStatusText);
        AssertNoEntryAt(harness);

        legs[1] = Payloads.Leg(2, null, "CHARGER", null, Charger, "ARRIVED");
        await harness.Server.SendJourneySnapshotAsync("UpcomingStopPlanSnapshot", Payloads.Plan(3, legs));
        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot",
            State(
                3,
                Payloads.LoadingPhase("CLOSED", closedReason: "CHARGING_REQUIRED"),
                chargingCycleState: "CHARGING",
                batteryState: "MANDATORY_CHARGE"));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.ChargingStatus == "CHARGING",
            "charging at the charger to be shown",
            token);

        Assert.Equal($"在充电桩 {Charger} 充电中", harness.ViewModel.VisitText);
        Assert.Equal("充电中", harness.ViewModel.ChargingStatusText);
        AssertNoEntryAt(harness);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType is "SublotSubmitted" or "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// Acceptance 4, the regression: a machine pickup -- the same stop shape with <c>WIRE_TO_GATE</c> items -- shows no
    /// 「出发」 and no staging summary, while its own entries are offered as before.
    /// </summary>
    [Fact]
    public async Task AMachinePickupShowsNoDeparture()
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
        Assert.False(harness.ScreenState().Buttons.Single(button => button.Key == "OnCurrentStopDepartureClick").Visible);

        // Its cargo-holding wait -- an empty worklist and a holding phase -- is not a staging stop either.
        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot", State(2, Payloads.LoadingPhase("CARGO_HOLDING_WAIT")));
        await harness.Server.SendJourneySnapshotAsync("CurrentStopWorklistSnapshot", Worklist(2, null, "COMPLETED"));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist?.Revision == 2,
            "the empty worklist of the holding wait",
            token);

        Assert.False(harness.Business.CurrentStopDeparture.Shown);
        Assert.Equal(CurrentStopDepartureDisplay.Empty, harness.OnUi(() => harness.ViewModel.CurrentStopDeparture));
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

    private static void AssertNoEntryAt(Harness harness)
    {
        Assert.False(harness.Business.CanSubmitSublot);
        Assert.False(harness.ViewModel.CanSubmit);
        Assert.False(harness.ViewModel.CanRequestLoadCancellation);
        Assert.False(harness.Business.CurrentStopDeparture.Shown);
        Assert.False(harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.IsShown));
    }

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
