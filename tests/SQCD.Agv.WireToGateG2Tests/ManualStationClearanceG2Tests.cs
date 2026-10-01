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
/// The onboard half of <c>CV-MANUAL-STATION-CLEARANCE</c> (batch 9-16, <c>trytoreachpeak0/8005-agv-onboard-hmi#221</c>):
/// while the server says the vehicle is clearing a charger, a verified maintainer confirms that the vehicle has
/// been moved and the charger is empty; the vehicle sends the request, shows the server's answer, and releases
/// nothing itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every test presses the entry on <see cref="MainViewModel"/> and reads what the operator sees there.</b>
/// The harness wires the view model with <c>StationClearanceWiring.Configure</c>, the method <c>App.xaml.cs</c>
/// calls, so each of these is also the seam test for that wiring: a delegate connected to the wrong member, or
/// not connected, fails here rather than on a vehicle.
/// </para>
/// <para>
/// <b>What is waited on is the view model's <see cref="MainViewModel.StationClearance"/></b>, one record replaced
/// whole, which is also what the assertions then read. Nothing waits on a session-layer revision and then reads
/// the view model (<c>8005-agv-onboard-hmi#226</c>). "Does not appear" and "is not sent" are held over a window
/// rather than read once.
/// </para>
/// <para>
/// <b>The two vector tests carry <c>FP-IS-13</c></b>, the slice the vector belongs to. They carried none until
/// <c>8005-agv-onboard-hmi#222</c> flipped that slice to implemented on this end.
/// </para>
/// </remarks>
public sealed class ManualStationClearanceG2Tests
{
    private const string ProofVariable = "W2G_G2_STATION_CLEARANCE_PROOF";

    private const string UnsetProofVariable = "W2G_G2_STATION_CLEARANCE_PROOF_UNSET";

    private const string RequestType = "ManualStationClearanceConfirmationRequested";

    private const string Clearing = "CLEARING_MAINTENANCE";

    private const string Charger = "CH-01";

    private const string OtherCharger = "CH-02";

    private const string WaitingPoint = "WP-01";

    /// <summary>The operator id <c>MultiDemandJourneyG2Tests</c>' static constructor configures for the harness.</summary>
    private const string Maintainer = "operator-134";

    private static readonly TimeSpan HoldWindow = TimeSpan.FromMilliseconds(500);

    static ManualStationClearanceG2Tests()
    {
        // The harness is MultiDemandJourneyG2Tests'; its credential and operator variables are set by that
        // class's static constructor, which using its nested types alone does not run.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(MultiDemandJourneyG2Tests).TypeHandle);
        Environment.SetEnvironmentVariable(ProofVariable, "g2-station-clearance-proof");
        Environment.SetEnvironmentVariable(UnsetProofVariable, null);
    }

    /// <summary>
    /// <c>CONFIRM_CLEARANCE_WITH_OPERATOR</c>: the request carries the operator, the charger and the condition,
    /// its messageId is its confirmation request id, and the server's <c>CONFIRMED</c> is shown with
    /// <c>stationReleased</c>.
    /// </summary>
    /// <remarks>
    /// <c>publicStationFunction</c> is asserted to be present and JSON <c>null</c>: the enumeration has no value
    /// for a charger, and the schema requires the property. Red before this ticket: there was no entry and no
    /// send path.
    /// </remarks>
    [Fact]
    [Trait("ProtocolVector", "CV-MANUAL-STATION-CLEARANCE")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    public async Task TheConfirmationCarriesTheOperatorTheChargerAndTheConditionAndIsShownConfirmed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.RespondToManualStationClearanceConfirmations = true);
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);

        Assert.Equal(new WireToGateStationClearancePrompt(Charger, Maintainer, null), shown.Prompt);
        Assert.Equal(WireToGateStationClearanceText.ConfirmationText(shown.Prompt!), shown.ConfirmationText);
        Assert.Contains(Charger, shown.ConfirmationText, StringComparison.Ordinal);
        Assert.False(shown.HasNotice);
        Assert.False(shown.HasStatus);

        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.True(await harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token));
        DateTimeOffset after = DateTimeOffset.UtcNow.AddSeconds(1);

        SentRequest request = Assert.Single(Requests(harness));
        Assert.Equal(JsonValueKind.Null, request.Root.GetProperty("correlationId").ValueKind);
        Assert.Equal(
            ["clearedCondition", "confirmationRequestId", "observedAt", "operator", "publicStationFunction", "stationId"],
            request.Payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.True(Guid.TryParseExact(request.ConfirmationRequestId, "D", out _));
        Assert.Equal(request.ConfirmationRequestId, request.MessageId);
        Assert.Equal(Charger, request.Payload.GetProperty("stationId").GetString());
        Assert.Equal(JsonValueKind.Null, request.Payload.GetProperty("publicStationFunction").ValueKind);
        Assert.Equal("STATION_EMPTY", request.Payload.GetProperty("clearedCondition").GetString());
        JsonElement sentOperator = request.Payload.GetProperty("operator");
        Assert.Equal(Maintainer, sentOperator.GetProperty("operatorId").GetString());
        Assert.Equal("SESSION", sentOperator.GetProperty("verificationMethod").GetString());
        Assert.InRange(sentOperator.GetProperty("verifiedAt").GetDateTimeOffset(), before, after);
        Assert.InRange(request.Payload.GetProperty("observedAt").GetDateTimeOffset(), before, after);

        StationClearanceDisplay confirmed = await WaitForStatusAsync(
            harness, WireToGateStationClearanceText.ConfirmedReleasedStatus, token);
        Assert.Contains("服务端已确认清桩", confirmed.StatusText, StringComparison.Ordinal);
        Assert.Contains("站点已释放", confirmed.StatusText, StringComparison.Ordinal);
        Assert.Contains(Charger, confirmed.StatusText, StringComparison.Ordinal);
        Assert.Single(harness.Events, item => item.Kind == "STATION_CLEARANCE_CONFIRMED");
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>NEVER_RELEASE_STATION_LOCALLY</c>: after <c>CONFIRMED</c> with <c>stationReleased=true</c> nothing on
    /// the vehicle has changed -- the journey projection is the same object, the visit cell and its raw status
    /// read the same, the entry is still offered, no sublot entry opens, no door opens, the journal's recovery
    /// state is as it was, and nothing but the request went out. The entry and the result line then go with the
    /// server's next business state, and only with it.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-MANUAL-STATION-CLEARANCE")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    public async Task AConfirmedClearanceChangesNothingOnTheVehicleUntilTheServersNextSnapshot()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.RespondToManualStationClearanceConfirmations = true);
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);
        await WaitForTheWireToGoQuietAsync(harness, token);

        WireToGateJourneySnapshot journey = harness.Session.CurrentJourney;
        // Serialized, not compared as records: the state holds lists, and a record compares those by reference.
        string recovery = JsonSerializer.Serialize(await harness.Session.Journal.ReadRecoveryStateAsync(token));
        string visit = harness.ViewModel.VisitText;
        string[] legs = harness.PlanLegStatuses();
        int sentBefore = harness.Server.ReceivedEnvelopes.Count;
        Assert.Equal(WireToGateIdleReturnText.ClearingEnRouteStatus, harness.ViewModel.IdleReturnStatus);

        Assert.True(await harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token));
        await WaitForStatusAsync(harness, WireToGateStationClearanceText.ConfirmedReleasedStatus, token);

        await AssertWhileAsync(
            () =>
            {
                Assert.Same(journey, harness.Session.CurrentJourney);
                Assert.Equal(visit, harness.ViewModel.VisitText);
                Assert.Equal(WireToGateIdleReturnText.ClearingEnRouteStatus, harness.ViewModel.IdleReturnStatus);
                Assert.Equal(legs, harness.PlanLegStatuses());
                Assert.True(harness.ViewModel.StationClearance.CanConfirm);
                Assert.Equal(Charger, harness.ViewModel.StationClearance.Prompt!.StationId);
                Assert.False(harness.ViewModel.CanSubmit);
                Assert.False(harness.Business.CanSubmitSublot);
                Assert.Null(harness.Business.CurrentOperationSnapshot);
                Assert.Equal(0, harness.Io.UnlockCount);
            },
            token);
        Assert.Equal(recovery, JsonSerializer.Serialize(await harness.Session.Journal.ReadRecoveryStateAsync(token)));
        Assert.Equal(
            [RequestType],
            harness.Server.ReceivedEnvelopes.Skip(sentBefore).Select(envelope => envelope.MessageType).ToArray());

        // The server's next business state is what ends the clearance on this vehicle.
        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", BusinessState(2, "IDLE_RETURN"));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.StationClearance == StationClearanceDisplay.Empty,
            "the entry and the result line to go with the server's next business state",
            token);
        Assert.Equal(WireToGateIdleReturnText.EnRouteStatus, harness.ViewModel.IdleReturnStatus);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The server ends the clearance in the snapshot it sends right after the result, on the same connection: the
    /// operator is still told what the server answered, in the operator record the view model shows.
    /// </summary>
    /// <remarks>
    /// The receive loop hands the result to the waiting press and goes straight on to the snapshot; the snapshot's
    /// journey handler forgets the clearance before the press has read its own result. Until
    /// <c>8005-agv-onboard-hmi#222</c>'s review (item 4) the press then found nothing to settle and told the operator
    /// nothing. Asserted on <see cref="MainViewModel.Logs"/>, the layer the operator reads; the result line itself is
    /// rightly gone with the clearance.
    /// </remarks>
    [Fact]
    public async Task AResultFollowedAtOnceByTheSnapshotThatEndsTheClearanceIsStillShownToTheOperator()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server =>
            {
                server.RespondToManualStationClearanceConfirmations = true;
                server.VehicleBusinessStateAfterManualStationClearanceResult = BusinessState(2, "IDLE_RETURN");
            });
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);

        Assert.True(await harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token));

        await harness.WaitUntilAsync(
            () => harness.ViewModel.Logs.Any(line =>
                line.Kind == OperatorRecordKind.Success
                && line.Message.Contains("服务端已确认清桩", StringComparison.Ordinal)),
            "the operator record of the confirmed clearance on the view model",
            token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.StationClearance == StationClearanceDisplay.Empty,
            "the entry to go with the snapshot that ended the clearance",
            token);
        Assert.Single(harness.Events, item => item.Kind == "STATION_CLEARANCE_CONFIRMED");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The first of the three conditions: the server has to say the vehicle is clearing. A verified maintainer
    /// and a charger leg under any other purpose offer nothing, and a press that reaches the business service
    /// anyway sends nothing.
    /// </summary>
    [Theory]
    [InlineData("CHARGING")]
    [InlineData("IDLE_RETURN")]
    [InlineData("TRANSPORT")]
    public async Task TheEntryIsNotOfferedUnlessTheServerSaysTheVehicleIsClearing(string purpose)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server => server.RespondToManualStationClearanceConfirmations = true,
            purpose: purpose);
        await WaitForThePlanOnTheViewModelAsync(harness, token);

        await AssertTheEntryStaysShutAsync(harness, token);
        Assert.Equal(StationClearanceDisplay.Empty, harness.ViewModel.StationClearance);
        Assert.False(await harness.Business.ConfirmStationClearanceAsync(new(Charger, Maintainer, null), token));
        await AssertNothingIsSentAsync(harness, 0, token);
    }

    /// <summary>
    /// The second condition: a verified maintainer -- the maintenance switch on and the administrator proof
    /// configured, the check 「充电后返回服务」 makes. Without either the entry is not offered, nothing explains
    /// it to an operator it is not for, and a press sends nothing.
    /// </summary>
    [Theory]
    [InlineData(false, ProofVariable)]
    [InlineData(true, UnsetProofVariable)]
    public async Task TheEntryIsNotOfferedWithoutAVerifiedMaintainer(bool maintenanceSwitch, string proofVariable)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server => server.RespondToManualStationClearanceConfirmations = true,
            recoveryOptions: new WireToGateRecoveryOptions(
                maintenanceSwitch, proofVariable, "MAINTENANCE_ADMINISTRATOR", "CONFIGURED_PROOF"));
        await WaitForThePlanOnTheViewModelAsync(harness, token);

        await AssertTheEntryStaysShutAsync(harness, token);
        Assert.Equal(StationClearanceDisplay.Empty, harness.ViewModel.StationClearance);
        Assert.False(await harness.Business.ConfirmStationClearanceAsync(new(Charger, Maintainer, null), token));
        await AssertNothingIsSentAsync(harness, 0, token);
    }

    /// <summary>
    /// The third condition: a charger to name. A clearance whose plan has no charger leg, or two different ones,
    /// offers no entry and says why; the station is not taken from the waiting point, from the current leg or
    /// from a name.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheEntryIsNotOfferedWithoutOneChargerInThePlanAndTheScreenSaysWhy(bool twoChargers)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        object[] legs = twoChargers
            ? [ChargerLeg(1, Charger, "COMPLETED"), ChargerLeg(2, OtherCharger, "PLANNED"), WaitingLeg(3, "ACTIVE")]
            : [WaitingLeg(1, "ACTIVE")];
        await using Harness harness = await StartAsync(
            token, server => server.RespondToManualStationClearanceConfirmations = true, legs);
        StationClearanceDisplay display = await WaitForDisplayAsync(
            harness, shown => shown.HasNotice, "the reason the entry is not offered", token);

        await AssertTheEntryStaysShutAsync(harness, token);
        Assert.Null(display.Prompt);
        Assert.Contains("充电桩", display.NoticeText, StringComparison.Ordinal);
        Assert.Contains("不可用", display.NoticeText, StringComparison.Ordinal);
        Assert.False(await harness.Business.ConfirmStationClearanceAsync(new(Charger, Maintainer, null), token));
        await AssertNothingIsSentAsync(harness, 0, token);
    }

    /// <summary>
    /// <c>REJECTED</c>: the reason code the server sent is shown as sent, the entry stays, and the next press is
    /// a new confirmation with a new id -- a rejection is an answer, and repeating its id would ask the server
    /// for the same answer again.
    /// </summary>
    [Fact]
    public async Task ARejectedClearanceShowsTheServersReasonAndTheNextPressIsANewConfirmation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server =>
            {
                server.RespondToManualStationClearanceConfirmations = true;
                server.ManualStationClearanceOutcome = "REJECTED";
                server.ManualStationClearanceStationReleased = false;
                server.ManualStationClearanceProblem = new WireToGateProblemPayload(
                    "ACTION_NOT_ALLOWED_IN_STATE", null, null);
            });
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);

        Assert.False(await harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token));

        StationClearanceDisplay rejected = await WaitForStatusAsync(
            harness, WireToGateStationClearanceText.RejectedStatus, token);
        Assert.Contains("ACTION_NOT_ALLOWED_IN_STATE", rejected.StatusText, StringComparison.Ordinal);
        Assert.True(rejected.CanConfirm);
        Assert.Null(rejected.Prompt!.ResubmittedConfirmationRequestId);
        Assert.Single(harness.Events, item => item.Kind == "STATION_CLEARANCE_REJECTED");

        Assert.False(await harness.ViewModel.ConfirmStationClearanceAsync(rejected.Prompt, token));
        SentRequest[] requests = Requests(harness);
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(requests[0].ConfirmationRequestId, requests[1].ConfirmationRequestId);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// No answer within the message timeout: the screen says the result is unknown and what to do, and nothing
    /// is sent again on its own. When the operator presses again, the request carries <b>the first request's
    /// confirmation request id</b> and the first request's payload byte for byte, under a new messageId.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The id is compared with the first one, not merely found present: a fresh id per press is exactly the
    /// defect -- one clearance recorded by the server as two confirmations.
    /// </para>
    /// <para>
    /// <b>The messageId has to be new.</b> The control server's inbox answers a repeated messageId from its first
    /// response only when the whole line is identical, and refuses it as a content conflict when so much as
    /// <c>sentAt</c> or the session generation differs (<c>WireToGateStore.CaptureFirstResponseAsync</c>). What
    /// makes the resubmission one confirmation is the confirmation request id and the unchanged payload.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnUnansweredClearanceIsShownAsUnknownAndAResubmissionCarriesTheSameConfirmationRequestId()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);

        Assert.False(await harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token));

        StationClearanceDisplay unknown = await WaitForStatusAsync(
            harness, WireToGateStationClearanceText.UnknownStatus, token);
        Assert.Contains("结果未知，请查看车辆状态后再决定是否重新提交", unknown.StatusText, StringComparison.Ordinal);
        SentRequest first = Assert.Single(Requests(harness));
        await AssertNothingIsSentAsync(harness, 1, token, TimeSpan.FromSeconds(1));
        Assert.Single(harness.Events, item => item.Kind == "STATION_CLEARANCE_UNKNOWN");

        // The entry now says what the next press is: the same confirmation again.
        StationClearanceDisplay again = await WaitForDisplayAsync(
            harness,
            display => display.Prompt?.ResubmittedConfirmationRequestId == first.ConfirmationRequestId,
            "the entry to offer the same confirmation again",
            token);
        Assert.Equal(
            new WireToGateStationClearancePrompt(Charger, Maintainer, first.ConfirmationRequestId), again.Prompt);
        Assert.Contains("重新提交", again.ConfirmationText, StringComparison.Ordinal);

        harness.Server.RespondToManualStationClearanceConfirmations = true;
        Assert.True(await harness.ViewModel.ConfirmStationClearanceAsync(again.Prompt!, token));

        SentRequest[] requests = Requests(harness);
        Assert.Equal(2, requests.Length);
        Assert.Equal(first.ConfirmationRequestId, requests[1].ConfirmationRequestId);
        Assert.Equal(first.Payload.GetRawText(), requests[1].Payload.GetRawText());
        Assert.NotEqual(first.MessageId, requests[1].MessageId);
        StationClearanceDisplay confirmed = await WaitForStatusAsync(
            harness, WireToGateStationClearanceText.ConfirmedReleasedStatus, token);
        Assert.Null(confirmed.Prompt!.ResubmittedConfirmationRequestId);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A second press while the first is still unanswered sends nothing: exactly one request is on the wire when
    /// the answer finally comes, and the entry is withdrawn for as long as the answer is awaited.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first press is known to be in flight, not assumed: the server has its request and has not answered.
    /// The second goes to the business service directly, past the withdrawn button.
    /// </para>
    /// <para>
    /// <b>It is pressed twice over, because two things could refuse it and only one of them is the gate.</b> With
    /// the prompt the operator was shown, the prompt check would refuse it as well: an unanswered request now
    /// exists, so the current prompt is a resubmission and the shown one is not. Pressed only that way, this test
    /// stayed green with the gate removed (fault injection M03). So it is also pressed with the prompt that would
    /// be current were nothing awaited -- the resubmission of the request in flight -- which only the gate can
    /// refuse, and the refusal is read for the gate's own words.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASecondPressWhileTheFirstIsUnansweredSendsNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);

        Task<bool> first = harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token);
        await harness.WaitUntilAsync(() => Requests(harness).Length == 1, "the first request to reach the server", token);
        StationClearanceDisplay awaiting = await WaitForDisplayAsync(
            harness,
            display => display is { CanConfirm: false, HasNotice: true },
            "the entry to be withdrawn while the answer is awaited",
            token);
        Assert.Contains("等待服务端应答", awaiting.NoticeText, StringComparison.Ordinal);

        SentRequest request = Assert.Single(Requests(harness));
        Assert.False(await harness.Business.ConfirmStationClearanceAsync(shown.Prompt!, token));
        Assert.False(await harness.Business.ConfirmStationClearanceAsync(
            shown.Prompt! with { ResubmittedConfirmationRequestId = request.ConfirmationRequestId }, token));
        Assert.False(first.IsCompleted);
        WireToGateOperatorEvent[] refusals =
            [.. harness.Events.Where(item => item.Kind == "STATION_CLEARANCE_BLOCKED")];
        Assert.Equal(2, refusals.Length);
        Assert.All(refusals, item => Assert.Contains("还在等待服务端应答", item.Message, StringComparison.Ordinal));

        // Answered straight away: the first press is waiting on the product's own two-second message timeout, and
        // anything held here first is time taken out of it -- a stalled process would run the wait out and turn
        // this test red for the wrong reason. The window that shows nothing more was sent comes after the answer.
        await harness.Server.SendManualStationClearanceResultAsync(request.MessageId, request.ConfirmationRequestId);
        Assert.True(await first);
        await WaitForStatusAsync(harness, WireToGateStationClearanceText.ConfirmedReleasedStatus, token);
        await AssertNothingIsSentAsync(harness, 1, token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The same result arriving twice is handled once: one record for the operator, no recovery message raised
    /// for the second copy, and the session stays up.
    /// </summary>
    [Fact]
    public async Task ARepeatedResultIsHandledOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server =>
            {
                server.RespondToManualStationClearanceConfirmations = true;
                server.ManualStationClearanceResponseCopies = 2;
            });
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);
        int raised = 0;
        harness.Session.ServerCommandReceived += (_, args) =>
        {
            if (args.Value.MessageType == "ManualStationClearanceConfirmationResult")
            {
                Interlocked.Increment(ref raised);
            }
        };

        Assert.True(await harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token));
        await WaitForStatusAsync(harness, WireToGateStationClearanceText.ConfirmedReleasedStatus, token);
        await harness.WaitUntilAsync(
            () => harness.Server.SentEnvelopes.Count(item => item.MessageType == "ManualStationClearanceConfirmationResult") == 2,
            "both copies of the result to be sent",
            token);

        await AssertWhileAsync(
            () =>
            {
                Assert.Equal(0, Volatile.Read(ref raised));
                Assert.Single(harness.Events, item => item.Kind.StartsWith("STATION_CLEARANCE_", StringComparison.Ordinal));
                Assert.True(harness.Session.Current.Connected);
            },
            token);
        await harness.Session.Client.SendHeartbeatAsync(token);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A result that arrives after the wait ran out settles the unknown, once: the line moves from unknown to
    /// what the server said, a second copy changes nothing, and a result for a confirmation this vehicle is not
    /// waiting on is ignored.
    /// </summary>
    [Fact]
    public async Task AResultThatArrivesAfterTheTimeoutSettlesTheUnknownOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.False(await harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token));
        await WaitForStatusAsync(harness, WireToGateStationClearanceText.UnknownStatus, token);
        SentRequest request = Assert.Single(Requests(harness));

        // Not this vehicle's confirmation: ignored, the line stays unknown.
        await harness.Server.SendManualStationClearanceResultAsync(
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        await AssertWhileAsync(
            () => Assert.Equal(WireToGateStationClearanceText.UnknownStatus, harness.ViewModel.StationClearance.Status),
            token);

        await harness.Server.SendManualStationClearanceResultAsync(request.MessageId, request.ConfirmationRequestId);
        StationClearanceDisplay confirmed = await WaitForStatusAsync(
            harness, WireToGateStationClearanceText.ConfirmedReleasedStatus, token);
        Assert.Null(confirmed.Prompt!.ResubmittedConfirmationRequestId);

        await harness.Server.SendManualStationClearanceResultAsync(request.MessageId, request.ConfirmationRequestId);
        await AssertWhileAsync(
            () =>
            {
                Assert.Single(harness.Events, item => item.Kind == "STATION_CLEARANCE_CONFIRMED");
                Assert.Equal(
                    WireToGateStationClearanceText.ConfirmedReleasedStatus, harness.ViewModel.StationClearance.Status);
                Assert.True(harness.Session.Current.Connected);
            },
            token);
        await AssertNothingIsSentAsync(harness, 1, token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A second, different answer under a confirmation request id already answered is the server contradicting
    /// itself: the session is failed closed with <c>BUSINESS_ID_CONTENT_CONFLICT</c>, as it is for a manual
    /// charging return, and the answer on screen is not rewritten by the second one.
    /// </summary>
    /// <remarks>
    /// Held over a window: a rewrite would come a moment after the conflicting line was read, and a single read
    /// straight after sending it would pass on the version that rewrote.
    /// </remarks>
    [Fact]
    public async Task ADifferentAnswerUnderAnAnsweredIdFailsTheSessionAndDoesNotRewriteTheResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.RespondToManualStationClearanceConfirmations = true);
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.True(await harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token));
        await WaitForStatusAsync(harness, WireToGateStationClearanceText.ConfirmedReleasedStatus, token);
        SentRequest request = Assert.Single(Requests(harness));
        List<string> diagnostics = [];
        harness.Session.Client.DiagnosticRecorded += (_, args) =>
        {
            lock (diagnostics)
            {
                diagnostics.Add(args.Value);
            }
        };

        await harness.Server.SendManualStationClearanceResultAsync(
            request.MessageId, request.ConfirmationRequestId, stationReleased: false);

        await harness.WaitUntilAsync(() => !harness.Session.Current.Connected, "the session to be failed closed", token);
        lock (diagnostics)
        {
            Assert.Contains(
                diagnostics, line => line.Contains("BUSINESS_ID_CONTENT_CONFLICT", StringComparison.Ordinal));
        }
        await AssertWhileAsync(
            () =>
            {
                Assert.Equal(
                    WireToGateStationClearanceText.ConfirmedReleasedStatus, harness.ViewModel.StationClearance.Status);
                Assert.Single(harness.Events, item => item.Kind.StartsWith("STATION_CLEARANCE_", StringComparison.Ordinal));
            },
            token);
    }

    /// <summary>
    /// The server answers the request itself with a <c>ProtocolProblem</c>: it read the request and did not take
    /// it. That is an answer, shown as such with its reason code, and it ends the confirmation request id -- every
    /// later press is a new confirmation.
    /// </summary>
    /// <remarks>
    /// Treated as an unknown, the id would be kept and resent on each press, and the refusal used here is about
    /// that very id: the review's probe had three presses send the same refused id and the screen say 「结果未知」
    /// each time (<c>8005-agv-onboard-hmi#221</c> review, S1).
    /// </remarks>
    [Fact]
    public async Task AProtocolProblemToTheRequestEndsItsIdAndEachLaterPressIsANewConfirmation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.ManualStationClearanceProtocolProblem = "BUSINESS_ID_CONTENT_CONFLICT");
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);

        Assert.False(await harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token));

        StationClearanceDisplay notAccepted = await WaitForDisplayAsync(
            harness,
            display => display.Status == WireToGateStationClearanceText.NotAcceptedStatus && display.CanConfirm,
            "the refusal shown and the entry offered again",
            token);
        Assert.Contains("服务端没有受理", notAccepted.StatusText, StringComparison.Ordinal);
        Assert.Contains("BUSINESS_ID_CONTENT_CONFLICT", notAccepted.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("结果未知", notAccepted.StatusText, StringComparison.Ordinal);
        Assert.Null(notAccepted.Prompt!.ResubmittedConfirmationRequestId);
        Assert.Single(harness.Events, item => item.Kind == "STATION_CLEARANCE_NOT_ACCEPTED");
        Assert.DoesNotContain(harness.Events, item => item.Kind == "STATION_CLEARANCE_UNKNOWN");
        Assert.True(harness.Session.Current.Connected);

        Assert.False(await harness.ViewModel.ConfirmStationClearanceAsync(notAccepted.Prompt, token));
        StationClearanceDisplay again = await WaitForEntryAsync(harness, token);
        Assert.Null(again.Prompt!.ResubmittedConfirmationRequestId);
        Assert.False(await harness.ViewModel.ConfirmStationClearanceAsync(again.Prompt, token));

        string[] ids = [.. Requests(harness).Select(request => request.ConfirmationRequestId)];
        Assert.Equal(3, ids.Length);
        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A <c>ProtocolProblem</c> about some other message does not end the id. It is not an answer to the request:
    /// it fails the session, the press ends as unknown, and after the reconnect the same confirmation is offered
    /// again under the same id.
    /// </summary>
    [Fact]
    public async Task AProtocolProblemAboutAnotherMessageLeavesTheRequestUnknownUnderTheSameId()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);

        Task<bool> press = harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token);
        await harness.WaitUntilAsync(() => Requests(harness).Length == 1, "the request to reach the server", token);
        SentRequest first = Assert.Single(Requests(harness));
        await harness.Server.SendProtocolProblemAsync(
            Guid.NewGuid().ToString("D"), "OperationProgress", "PROTOCOL_SCHEMA_INVALID");
        Assert.False(await press);

        await WaitForStatusAsync(harness, WireToGateStationClearanceText.UnknownStatus, token);
        Assert.DoesNotContain(harness.Events, item => item.Kind == "STATION_CLEARANCE_NOT_ACCEPTED");
        await harness.WaitUntilAsync(() => !harness.Session.Current.Connected, "the session to be failed", token);

        await ReconnectAndExpectTheSameConfirmationAsync(harness, first, token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A malformed result -- <c>outcome</c> a number -- is refused as <c>PROTOCOL_SCHEMA_INVALID</c> like any other
    /// malformed message: the press ends as unknown, nothing escapes it, the vehicle is not latched, and the same
    /// confirmation can be resubmitted after the reconnect.
    /// </summary>
    /// <remarks>
    /// The review's probe: this line came out of the typed read as a <c>JsonException</c>, escaped the press, and
    /// through the window's <c>async void</c> handler latched <c>UNHANDLED_UI_ERROR</c> until a restart
    /// (<c>8005-agv-onboard-hmi#221</c> review, S3). The malformed line is registered in
    /// <c>schema-known-violations.json</c> as deliberate.
    /// </remarks>
    [Fact]
    public async Task AMalformedResultLeavesTheVehicleUnlatchedAndTheConfirmationResubmittable()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);

        Task<bool> press = harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token);
        await harness.WaitUntilAsync(() => Requests(harness).Length == 1, "the request to reach the server", token);
        SentRequest first = Assert.Single(Requests(harness));
        await harness.Server.SendCommandAsync(
            "ManualStationClearanceConfirmationResult",
            Guid.NewGuid().ToString("D"),
            new
            {
                confirmationRequestId = first.ConfirmationRequestId,
                outcome = 5,
                problem = (object?)null,
                stationReleased = true
            },
            first.MessageId);

        Assert.False(await press);
        await WaitForStatusAsync(harness, WireToGateStationClearanceText.UnknownStatus, token);
        // Refused as a malformed message, by name: the catch-all below it would also have ended the press as
        // unknown, with a JsonException, and this test must not pass on that.
        Assert.Contains(
            harness.Logger.Exceptions,
            entry => entry.Exception is InvalidDataException { Message: "PROTOCOL_SCHEMA_INVALID" }
                && entry.Message.StartsWith("人工清桩确认未完成", StringComparison.Ordinal));
        Assert.False(harness.Controller.IsFatalFaultLatched);
        Assert.Empty(harness.UiErrors);

        await ReconnectAndExpectTheSameConfirmationAsync(harness, first, token);
        Assert.False(harness.Controller.IsFatalFaultLatched);
    }

    /// <summary>
    /// The session fails a waiting request with whatever its receive loop failed with, and that need not be one of
    /// the failures a request expects: here another message's malformed payload surfaces as a
    /// <c>JsonException</c>. The press still ends as unknown, and nothing escapes it to latch the vehicle.
    /// </summary>
    /// <remarks>
    /// This is the case the press's catch-all exists for. The clearance result's own malformed payloads no longer
    /// reach it -- they are refused as <c>PROTOCOL_SCHEMA_INVALID</c> before the typed read, see the test above --
    /// so it is pinned with a message this ticket does not own: a <c>ManualChargingReturnToServiceResult</c> whose
    /// <c>outcome</c> is a number. Registered in <c>schema-known-violations.json</c> as deliberate.
    /// </remarks>
    [Fact]
    public async Task ASessionThatFailsWithAnUnexpectedExceptionStillEndsThePressAsUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);

        Task<bool> press = harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token);
        await harness.WaitUntilAsync(() => Requests(harness).Length == 1, "the request to reach the server", token);
        await harness.Server.SendCommandAsync(
            "ManualChargingReturnToServiceResult",
            Guid.NewGuid().ToString("D"),
            new
            {
                requestId = Guid.NewGuid().ToString("D"),
                outcome = 5,
                problem = (object?)null,
                vehicleBusinessStateRevision = 1
            },
            Guid.NewGuid().ToString("D"));

        Assert.False(await press);
        StationClearanceDisplay unknown = await WaitForStatusAsync(
            harness, WireToGateStationClearanceText.UnknownStatus, token);
        Assert.Contains("结果未知", unknown.StatusText, StringComparison.Ordinal);
        Assert.Contains(
            harness.Logger.Exceptions,
            entry => entry.Exception is JsonException
                && entry.Message.StartsWith("人工清桩确认未完成", StringComparison.Ordinal));
        Assert.False(harness.Controller.IsFatalFaultLatched);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A result whose fields contradict each other is refused rather than shown: a rejection that says the station
    /// was released, and a confirmation that carries a problem. The press ends as unknown and the screen never
    /// states either half.
    /// </summary>
    [Theory]
    [InlineData("REJECTED", true, false)]
    [InlineData("REJECTED", true, true)]
    [InlineData("CONFIRMED", true, true)]
    [InlineData("CONFIRMED", false, true)]
    public async Task AResultThatContradictsItselfIsRefusedAndThePressEndsAsUnknown(
        string outcome,
        bool stationReleased,
        bool withProblem)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server =>
            {
                server.RespondToManualStationClearanceConfirmations = true;
                server.ManualStationClearanceOutcome = outcome;
                server.ManualStationClearanceStationReleased = stationReleased;
                server.ManualStationClearanceProblem = withProblem
                    ? new WireToGateProblemPayload("ACTION_NOT_ALLOWED_IN_STATE", null, null)
                    : null;
            });
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);

        Assert.False(await harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token));

        await WaitForStatusAsync(harness, WireToGateStationClearanceText.UnknownStatus, token);
        await AssertWhileAsync(
            () =>
            {
                Assert.Equal(WireToGateStationClearanceText.UnknownStatus, harness.ViewModel.StationClearance.Status);
                Assert.DoesNotContain(
                    harness.Events,
                    item => item.Kind is "STATION_CLEARANCE_CONFIRMED" or "STATION_CLEARANCE_REJECTED");
            },
            token);
        Assert.Contains(
            harness.Logger.Exceptions,
            entry => entry.Exception.Message == "PROTOCOL_SCHEMA_INVALID"
                && entry.Message.StartsWith("人工清桩确认未完成", StringComparison.Ordinal));
        Assert.False(harness.Controller.IsFatalFaultLatched);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The session drops while the answer is awaited: the press ends as unknown, the unknown stays on screen
    /// while offline, and after the reconnect the entry offers the same confirmation again -- the resubmission
    /// carries the first request's id. The answer the server replays into the new session for the first request
    /// then arrives after the resubmission's own and is not handled a second time.
    /// </summary>
    [Fact]
    public async Task ADroppedSessionLeavesAnUnknownThatIsResubmittedUnderTheSameIdAfterReconnecting()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);

        Task<bool> press = harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token);
        await harness.WaitUntilAsync(() => Requests(harness).Length == 1, "the request to reach the server", token);
        await harness.Session.Client.DisconnectAsync();
        Assert.False(await press);

        StationClearanceDisplay offline = await WaitForStatusAsync(
            harness, WireToGateStationClearanceText.UnknownStatus, token);
        Assert.False(offline.CanConfirm);
        SentRequest first = Assert.Single(Requests(harness));

        harness.Server.RespondToManualStationClearanceConfirmations = true;
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        // The new session is still settling when the handshake returns -- snapshots replayed, the safety state
        // reported again, readiness announced -- and the entry is withdrawn and offered again as it does. The press
        // below is made once that is over, on the reading the wait returns.
        await WaitForTheWireToGoQuietAsync(harness, token);
        await AssertNothingIsSentAsync(harness, 1, token);
        StationClearanceDisplay again = await WaitForDisplayAsync(
            harness,
            display => display.Prompt?.ResubmittedConfirmationRequestId == first.ConfirmationRequestId
                && display.Status == WireToGateStationClearanceText.UnknownStatus,
            "the entry to offer the same confirmation again after the reconnect, the unknown still shown",
            token);
        Assert.True(await harness.ViewModel.ConfirmStationClearanceAsync(again.Prompt!, token));
        SentRequest[] requests = Requests(harness);
        Assert.Equal(2, requests.Length);
        Assert.Equal(first.ConfirmationRequestId, requests[1].ConfirmationRequestId);
        Assert.Equal(first.Payload.GetRawText(), requests[1].Payload.GetRawText());
        Assert.NotEqual(first.Connection, requests[1].Connection);
        await WaitForStatusAsync(harness, WireToGateStationClearanceText.ConfirmedReleasedStatus, token);

        // The first request's answer, replayed into the new session after the resubmission was answered.
        await harness.Server.SendManualStationClearanceResultAsync(first.MessageId, first.ConfirmationRequestId);
        await AssertWhileAsync(
            () =>
            {
                Assert.Single(harness.Events, item => item.Kind == "STATION_CLEARANCE_CONFIRMED");
                Assert.True(harness.Session.Current.Connected);
            },
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The confirmation is bound to what the operator was shown. The plan names another charger while the dialog
    /// is open: the press made for the first charger is refused and sends nothing, the operator is told, and a
    /// press made for what is shown now sends the second charger.
    /// </summary>
    /// <remarks>
    /// The control the other way -- same prompt, the press goes out -- is every test above that confirms.
    /// </remarks>
    [Fact]
    public async Task APressMadeForOneChargerDoesNotConfirmAnother()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.RespondToManualStationClearanceConfirmations = true);
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.Equal(Charger, shown.Prompt!.StationId);

        await harness.Server.SendJourneySnapshotAsync(
            "UpcomingStopPlanSnapshot",
            Payloads.Plan(2, [ChargerLeg(1, OtherCharger, "COMPLETED"), WaitingLeg(2, "ACTIVE")]));
        StationClearanceDisplay current = await WaitForDisplayAsync(
            harness, display => display.Prompt?.StationId == OtherCharger, "the entry to name the other charger", token);

        Assert.False(await harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt, token));
        await AssertNothingIsSentAsync(harness, 0, token);
        Assert.Single(harness.Events, item => item.Kind == "STATION_CLEARANCE_BLOCKED");
        Assert.False(harness.ViewModel.StationClearance.HasStatus);

        Assert.Contains(OtherCharger, current.ConfirmationText, StringComparison.Ordinal);
        Assert.True(await harness.ViewModel.ConfirmStationClearanceAsync(current.Prompt!, token));
        SentRequest request = Assert.Single(Requests(harness));
        Assert.Equal(OtherCharger, request.Payload.GetProperty("stationId").GetString());
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A prompt shown as a resubmission is no longer the current one once the first request's result has
    /// arrived: the press is refused and sends nothing, instead of resubmitting a confirmation that is settled or
    /// silently turning into a new one the operator was never asked about.
    /// </summary>
    [Fact]
    public async Task APressMadeAsAResubmissionIsRefusedOnceTheResultHasArrived()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.False(await harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token));
        await WaitForStatusAsync(harness, WireToGateStationClearanceText.UnknownStatus, token);
        SentRequest request = Assert.Single(Requests(harness));
        StationClearanceDisplay resubmission = await WaitForDisplayAsync(
            harness,
            display => display.Prompt?.ResubmittedConfirmationRequestId == request.ConfirmationRequestId,
            "the entry to offer the same confirmation again",
            token);

        await harness.Server.SendManualStationClearanceResultAsync(request.MessageId, request.ConfirmationRequestId);
        await WaitForStatusAsync(harness, WireToGateStationClearanceText.ConfirmedReleasedStatus, token);

        Assert.False(await harness.ViewModel.ConfirmStationClearanceAsync(resubmission.Prompt!, token));
        await AssertNothingIsSentAsync(harness, 1, token);
        Assert.Single(harness.Events, item => item.Kind == "STATION_CLEARANCE_BLOCKED");
        Assert.Equal(
            WireToGateStationClearanceText.ConfirmedReleasedStatus, harness.ViewModel.StationClearance.Status);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A clearance that ended and one that begins later are two clearances, even at the same charger with the
    /// same maintainer. Once the server has said the vehicle is no longer clearing, what this vehicle held of an
    /// unknown confirmation is gone: the entry that comes back asks afresh, shows no old result, and its press
    /// carries a new id.
    /// </summary>
    /// <remarks>
    /// Without it the unknown's id would be offered as a resubmission in the next clearance, and a server that
    /// answers a repeated id from its first answer would report the old clearance's <c>CONFIRMED</c> for a
    /// charger nobody has confirmed this time.
    /// </remarks>
    [Fact]
    public async Task ALaterClearanceOfTheSameChargerStartsFromNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        StationClearanceDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.False(await harness.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token));
        await WaitForStatusAsync(harness, WireToGateStationClearanceText.UnknownStatus, token);
        SentRequest unanswered = Assert.Single(Requests(harness));
        await WaitForDisplayAsync(
            harness,
            display => display.Prompt?.ResubmittedConfirmationRequestId == unanswered.ConfirmationRequestId,
            "the entry to offer the same confirmation again",
            token);

        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", BusinessState(2, "IDLE_RETURN"));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.StationClearance == StationClearanceDisplay.Empty,
            "the first clearance to end on the view model",
            token);

        harness.Server.RespondToManualStationClearanceConfirmations = true;
        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", BusinessState(3, Clearing));
        StationClearanceDisplay later = await WaitForEntryAsync(harness, token);
        Assert.Equal(new WireToGateStationClearancePrompt(Charger, Maintainer, null), later.Prompt);
        Assert.False(later.HasStatus);

        Assert.True(await harness.ViewModel.ConfirmStationClearanceAsync(later.Prompt!, token));
        SentRequest[] requests = Requests(harness);
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(unanswered.ConfirmationRequestId, requests[1].ConfirmationRequestId);
        Assert.Equal(requests[1].ConfirmationRequestId, requests[1].MessageId);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A restart on the same journal, against a server that pushes nothing on the new session: whether the entry
    /// is offered comes from the restored business state and plan alone. What the last process knew of an
    /// unknown confirmation is gone with it, so nothing is sent on its own, the operator is asked afresh, and
    /// the press that follows is a new confirmation.
    /// </summary>
    [Fact]
    public async Task ARestartOffersTheEntryFromTheRestoredStateAndAsksTheOperatorAfresh()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness first = await StartAsync(token);
        StationClearanceDisplay shown = await WaitForEntryAsync(first, token);
        Assert.False(await first.ViewModel.ConfirmStationClearanceAsync(shown.Prompt!, token));
        await WaitForStatusAsync(first, WireToGateStationClearanceText.UnknownStatus, token);
        SentRequest unanswered = Assert.Single(Requests(first));

        await first.StopVehicleAsync();
        first.Server.SimulateOnboardProcessRestart();
        first.Server.VectorJourneySnapshotsAfterRecovery = [];
        first.Server.RespondToManualStationClearanceConfirmations = true;
        await using Harness second = await Harness.StartAgainstAsync(
            first.Server, token, first.JournalPath, recoveryOptions: VerifiedMaintainer());

        StationClearanceDisplay restored = await WaitForEntryAsync(second, token);
        Assert.Equal(new WireToGateStationClearancePrompt(Charger, Maintainer, null), restored.Prompt);
        Assert.False(restored.HasStatus);
        Assert.Equal(Clearing, second.Session.CurrentJourney.VehicleBusinessState!.ActivePurpose);
        await AssertNothingIsSentAsync(second, 1, token);

        Assert.True(await second.ViewModel.ConfirmStationClearanceAsync(restored.Prompt!, token));
        SentRequest[] requests = Requests(second);
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(unanswered.ConfirmationRequestId, requests[1].ConfirmationRequestId);
        Assert.Empty(second.UiErrors);
    }

    /// <summary>
    /// A fatal-fault latch does not shut this entry, on either refresh path, and a confirmation made under the
    /// latch opens no door. The recovery entries beside it shut as they always have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decision the ticket left open: this entry is not one of the nine recovery entries
    /// (<c>RecoveryEntryWriteSiteArchitectureTests</c>). Those are shut under a latch because three of them open
    /// doors through an executor the latch cannot stop. This one states a fact about a charger and shows the
    /// server's answer; it does no IO
    /// (<c>WireToGateStationClearanceTests.TheEntrysBusinessCodeNamesNoDoorNoIoAndNoRecoveryState</c>). Shut
    /// under a latch, a faulted vehicle that has been pushed off a charger could not give the charger back until
    /// its own fault was cleared, which <c>REQ-0180</c> keeps apart: a manual clearance neither restores nor
    /// blocks the vehicle.
    /// </para>
    /// <para>
    /// 「充电后返回服务」 is the positive control: offered before the latch by the same verified maintainer, shut
    /// after it. Without it, "still offered" could be a harness in which nothing is ever shut.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ALatchLeavesTheClearanceEntryOpenAndOpensNoDoor()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.RespondToManualStationClearanceConfirmations = true);
        await WaitForEntryAsync(harness, token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanRequestManualChargingReturn,
            "the recovery entry used as the control to be offered",
            token);

        // The controller's state change runs the presentation path.
        harness.Controller.EnterFatalFault("UI_COMMAND_FAILED", OnboardFatalFaultBanner.UiCommandFailed);
        await harness.WaitUntilAsync(
            () => !harness.ViewModel.CanRequestManualChargingReturn,
            "the latch to shut the recovery entries",
            token);
        Assert.True(harness.ViewModel.StationClearance.CanConfirm);

        // The other path: what a resent entry request runs.
        harness.ViewModel.RefreshWireToGateInputState();
        Assert.False(harness.ViewModel.CanRequestManualChargingReturn);
        StationClearanceDisplay latched = harness.ViewModel.StationClearance;
        Assert.True(latched.CanConfirm);

        Assert.True(await harness.ViewModel.ConfirmStationClearanceAsync(latched.Prompt!, token));
        await WaitForStatusAsync(harness, WireToGateStationClearanceText.ConfirmedReleasedStatus, token);
        Assert.Single(Requests(harness));
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.True(harness.Controller.IsFatalFaultLatched);
    }

    /// <summary>
    /// The send path checks its payload before anything is written, the way the other requests do: a value the
    /// schema does not allow is refused as <c>PROTOCOL_SCHEMA_INVALID</c> and nothing reaches the server.
    /// </summary>
    [Theory]
    [InlineData("stationId")]
    [InlineData("publicStationFunction")]
    [InlineData("clearedCondition")]
    [InlineData("operatorId")]
    [InlineData("verificationMethod")]
    [InlineData("observedAt")]
    public async Task APayloadTheSchemaDoesNotAllowIsRefusedBeforeItIsSent(string broken)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.RespondToManualStationClearanceConfirmations = true);
        await WaitForEntryAsync(harness, token);
        string id = Guid.NewGuid().ToString("D");
        ManualStationClearanceConfirmationRequestedPayload payload = new(
            id,
            broken == "stationId" ? " " : Charger,
            broken == "publicStationFunction" ? "CHARGER" : null,
            broken == "clearedCondition" ? "EMPTY" : "STATION_EMPTY",
            new WireToGateOperatorContextPayload(
                broken == "operatorId" ? string.Empty : Maintainer,
                broken == "verificationMethod" ? "PIN" : "SESSION",
                DateTimeOffset.UtcNow),
            broken == "observedAt" ? default : DateTimeOffset.UtcNow);

        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(
            () => harness.Session.ConfirmManualStationClearanceAsync(id, payload, token));

        Assert.Equal("PROTOCOL_SCHEMA_INVALID", refused.Message);
        await AssertNothingIsSentAsync(harness, 0, token);
    }

    /// <summary>
    /// Reconnects, waits for the new session to settle, and requires the entry to offer <paramref name="first"/>
    /// again -- the unknown still shown -- and the press made on it to carry the same id and payload.
    /// </summary>
    private static async Task ReconnectAndExpectTheSameConfirmationAsync(
        Harness harness,
        SentRequest first,
        CancellationToken token)
    {
        harness.Server.RespondToManualStationClearanceConfirmations = true;
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await WaitForTheWireToGoQuietAsync(harness, token);
        StationClearanceDisplay again = await WaitForDisplayAsync(
            harness,
            display => display.Prompt?.ResubmittedConfirmationRequestId == first.ConfirmationRequestId
                && display.Status == WireToGateStationClearanceText.UnknownStatus,
            "the entry to offer the same confirmation again after the reconnect, the unknown still shown",
            token);

        Assert.True(await harness.ViewModel.ConfirmStationClearanceAsync(again.Prompt!, token));
        SentRequest resubmitted = Requests(harness)[^1];
        Assert.Equal(2, Requests(harness).Length);
        Assert.Equal(first.ConfirmationRequestId, resubmitted.ConfirmationRequestId);
        Assert.Equal(first.Payload.GetRawText(), resubmitted.Payload.GetRawText());
        Assert.NotEqual(first.MessageId, resubmitted.MessageId);
        await WaitForStatusAsync(harness, WireToGateStationClearanceText.ConfirmedReleasedStatus, token);
    }

    /// <summary>What each harness's session client recorded, for the message of a wait that ran out.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Harness, List<string>> SessionDiagnostics = [];

    private static string[] DiagnosticsOf(Harness harness)
    {
        if (!SessionDiagnostics.TryGetValue(harness, out List<string>? diagnostics))
        {
            return [];
        }

        lock (diagnostics)
        {
            return [.. diagnostics];
        }
    }

    private sealed record SentRequest(string MessageId, int Connection, JsonElement Root)
    {
        public JsonElement Payload => Root.GetProperty("payload");

        public string ConfirmationRequestId => Payload.GetProperty("confirmationRequestId").GetString()!;
    }

    private static SentRequest[] Requests(Harness harness) =>
    [
        .. harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == RequestType)
            .Select(envelope =>
            {
                using JsonDocument document = JsonDocument.Parse(envelope.WireLine);
                return new SentRequest(envelope.MessageId, envelope.Connection, document.RootElement.Clone());
            })
    ];

    private static WireToGateRecoveryOptions VerifiedMaintainer() =>
        new(true, ProofVariable, "MAINTENANCE_ADMINISTRATOR", "CONFIGURED_PROOF");

    /// <summary>
    /// A vehicle the server says is clearing <see cref="Charger"/> on its way to a waiting point, with a verified
    /// maintainer at it, unless the arguments say otherwise. The plan and the business state arrive after the
    /// handshake.
    /// </summary>
    private static async Task<Harness> StartAsync(
        CancellationToken token,
        Action<FakeControlServer>? configure = null,
        IReadOnlyList<object>? legs = null,
        string purpose = Clearing,
        WireToGateRecoveryOptions? recoveryOptions = null)
    {
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.VectorJourneySnapshotsAfterRecovery = ["UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"];
                // Both snapshots are fixed objects, the business state included. The fake's own business state takes
                // observedAt afresh on every send, so a reconnect would resend revision 1 with other content and the
                // vehicle would rightly fail the new session with SNAPSHOT_REVISION_CONTENT_CONFLICT -- which is what
                // these tests' reconnects did until this was pinned. The real server resends a revision as it was.
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(
                        1, legs ?? [ChargerLeg(1, Charger, "COMPLETED"), WaitingLeg(2, "ACTIVE")]),
                    ["VehicleBusinessStateSnapshot"] = BusinessState(1, purpose)
                };
                configure?.Invoke(server);
            },
            token,
            recoveryOptions: recoveryOptions ?? VerifiedMaintainer());
        List<string> diagnostics = [];
        SessionDiagnostics.Add(harness, diagnostics);
        harness.Session.Client.DiagnosticRecorded += (_, args) =>
        {
            lock (diagnostics)
            {
                diagnostics.Add(args.Value);
            }
        };
        try
        {
            await harness.WaitUntilAsync(
                () => harness.Session.CurrentJourney is { VehicleBusinessState: not null, UpcomingStopPlan: not null },
                "the plan and the business state",
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
    /// Waits for the view model's entry to satisfy <paramref name="predicate"/> and returns <b>the reading that
    /// satisfied it</b>.
    /// </summary>
    /// <remarks>
    /// Reading the view model again after the wait is a second reading, and the entry can have moved in between
    /// -- it is withdrawn and offered again as a session reconnects, for one. The first version of these helpers
    /// did exactly that, and a whole-suite run caught it: the wait saw the entry, the read after it saw none, and
    /// the press went out with a null prompt.
    /// </remarks>
    private static async Task<StationClearanceDisplay> WaitForDisplayAsync(
        Harness harness,
        Func<StationClearanceDisplay, bool> predicate,
        string expectation,
        CancellationToken token)
    {
        StationClearanceDisplay display = StationClearanceDisplay.Empty;
        try
        {
            await harness.WaitUntilAsync(
                () =>
                {
                    display = harness.ViewModel.StationClearance;
                    return predicate(display);
                },
                expectation,
                token);
        }
        catch (Xunit.Sdk.XunitException exception)
        {
            // Both layers, so a timeout says whether the business service never offered it or the view model never
            // showed what the business service offers.
            throw new Xunit.Sdk.XunitException(
                $"{exception.Message} View model: {display}. Business service: {harness.Business.StationClearance}. "
                + $"Session: {harness.Session.Current.Readiness}, connected={harness.Session.Current.Connected}, "
                + $"reasons=[{string.Join(",", harness.Session.Current.ReasonCodes)}]. Warnings and errors logged: "
                + string.Join(
                    " | ",
                    harness.Logger.Entries
                        .Where(entry => entry.Severity is LogSeverity.Warning or LogSeverity.Error)
                        .Select(entry => $"{entry.Source}: {entry.Message}")
                        .TakeLast(8))
                + ". Session diagnostics: " + string.Join(" | ", DiagnosticsOf(harness).TakeLast(12)));
        }

        return display;
    }

    private static Task<StationClearanceDisplay> WaitForEntryAsync(Harness harness, CancellationToken token) =>
        WaitForDisplayAsync(
            harness, display => display.CanConfirm, "the clearance entry to be offered on the view model", token);

    private static Task<StationClearanceDisplay> WaitForStatusAsync(
        Harness harness,
        string status,
        CancellationToken token) =>
        WaitForDisplayAsync(
            harness, display => display.Status == status, $"the result line on the view model to read {status}", token);

    /// <summary>
    /// The plan has reached the view model: its leg rows are what <c>UpdateWireToGateJourney</c> writes from the
    /// same snapshot the entry is judged on, so an entry that is going to appear has had its chance.
    /// </summary>
    private static Task WaitForThePlanOnTheViewModelAsync(Harness harness, CancellationToken token) =>
        harness.WaitUntilAsync(
            () => harness.PlanLegStatuses().Length > 0
                && harness.Session.CurrentJourney.VehicleBusinessState is not null,
            "the plan on the view model",
            token);

    private static Task AssertTheEntryStaysShutAsync(Harness harness, CancellationToken token) =>
        AssertWhileAsync(
            () =>
            {
                Assert.False(harness.ViewModel.StationClearance.CanConfirm);
                Assert.False(harness.Business.CanConfirmStationClearance);
            },
            token);

    /// <summary>Exactly <paramref name="expected"/> clearance requests are on the wire, and stay so over the window.</summary>
    private static Task AssertNothingIsSentAsync(
        Harness harness,
        int expected,
        CancellationToken token,
        TimeSpan? window = null) =>
        AssertWhileAsync(() => Assert.Equal(expected, Requests(harness).Length), token, window);

    /// <summary>
    /// Holds <paramref name="assertion"/> over a window. A claim that something does not happen, read once,
    /// passes on the version that had not got round to it yet.
    /// </summary>
    private static async Task AssertWhileAsync(Action assertion, CancellationToken token, TimeSpan? window = null)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + (window ?? HoldWindow);
        do
        {
            assertion();
            await Task.Delay(25, token);
        }
        while (DateTimeOffset.UtcNow < until);
    }

    /// <summary>
    /// Nothing crosses the wire in either direction for 300 ms: the safety report the business service sends on
    /// start has gone and the session has stopped announcing readiness, so what is counted after the press is the
    /// press's, and the entry is not about to be withdrawn by a session that is still settling.
    /// </summary>
    private static async Task WaitForTheWireToGoQuietAsync(Harness harness, CancellationToken token)
    {
        int count = -1;
        DateTimeOffset quietSince = DateTimeOffset.UtcNow;
        await harness.WaitUntilAsync(
            () =>
            {
                int now = harness.Server.ReceivedEnvelopes.Count + harness.Server.SentEnvelopes.Count;
                if (now != count)
                {
                    count = now;
                    quietSince = DateTimeOffset.UtcNow;
                }

                return DateTimeOffset.UtcNow - quietSince > TimeSpan.FromMilliseconds(300);
            },
            "the vehicle to stop sending",
            token);
    }

    private static object BusinessState(long revision, string activePurpose) =>
        new
        {
            vehicleBusinessStateRevision = revision,
            readiness = "READY",
            activePurpose,
            manualChargingHold = false,
            batteryState = "SUFFICIENT",
            chargingCycleState = "NOT_CHARGING",
            loadingPhase = (object?)null,
            blockingFacts = Array.Empty<object>(),
            observedAt = DateTimeOffset.UtcNow
        };

    private static object ChargerLeg(int sequence, string stationId, string state) =>
        Payloads.Leg(sequence, null, "CHARGER", null, stationId, state);

    private static object WaitingLeg(int sequence, string state) =>
        Payloads.Leg(sequence, null, "WAITING_POINT", null, WaitingPoint, state);
}
