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
/// The onboard half of <c>CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION</c> (batch 9-17,
/// <c>trytoreachpeak0/8005-agv-onboard-hmi#222</c>): while the server says the vehicle is charging at a charger, a
/// verified maintainer reports what they observed when it did not charge; the vehicle sends the request, shows the
/// server's answer and its charging decision, and decides nothing itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every test presses the entry on <see cref="MainViewModel"/> and reads what the operator sees there</b>, wired
/// with <c>UnableToChargeWiring.Configure</c>, the method <c>App.xaml.cs</c> calls -- so each of these is also the
/// seam test for that wiring.
/// </para>
/// <para>
/// <b>What is waited on is the view model's <see cref="MainViewModel.UnableToCharge"/></b>, one record replaced
/// whole, which is also what the assertions then read; the wait returns the reading that satisfied it. "Does not
/// appear" and "is not sent" are held over a window rather than read once.
/// </para>
/// <para>
/// <b>The two vector tests carry <c>FP-IS-13</c></b>: this ticket flips that slice to implemented on this end, and
/// the slice trait has to equal the projection of the test's own vectors
/// (<c>IntegrationSliceTraitArchitectureTests</c>). The other tests here claim no vector and so carry no slice.
/// </para>
/// </remarks>
public sealed class UnableToChargeFieldConfirmationG2Tests
{
    private const string ProofVariable = "W2G_G2_UNABLE_TO_CHARGE_PROOF";

    private const string UnsetProofVariable = "W2G_G2_UNABLE_TO_CHARGE_PROOF_UNSET";

    private const string RequestType = "UnableToChargeFieldConfirmationRequested";

    private const string ResultType = "UnableToChargeFieldConfirmationResult";

    private const string Charging = "CHARGING";

    private const string Charger = "CH-01";

    private const string OtherCharger = "CH-02";

    private const string WaitingPoint = "WP-01";

    /// <summary>The operator id <c>MultiDemandJourneyG2Tests</c>' static constructor configures for the harness.</summary>
    private const string Maintainer = "operator-134";

    private static readonly TimeSpan HoldWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>The schema's three <c>chargingPolicyDecision</c> values.</summary>
    private static readonly string[] Decisions = ["RETRY_LATER", "MANUAL_CHARGING_HOLD", "REASSIGN_CHARGER"];

    static UnableToChargeFieldConfirmationG2Tests()
    {
        // The harness is MultiDemandJourneyG2Tests'; its credential and operator variables are set by that class's
        // static constructor, which using its nested types alone does not run.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(MultiDemandJourneyG2Tests).TypeHandle);
        Environment.SetEnvironmentVariable(ProofVariable, "g2-unable-to-charge-proof");
        Environment.SetEnvironmentVariable(UnsetProofVariable, null);
    }

    /// <summary>
    /// <c>REPORT_OBSERVED_CONDITION_WITH_OPERATOR</c>, and the vector in order: the request carries the operator,
    /// the charger and the condition the operator chose, its messageId is its confirmation request id; the server's
    /// result is followed by a business state snapshot, which the vehicle acknowledges with
    /// <c>SnapshotAppliedAck</c>, and the operator is told what the server recorded and decided.
    /// </summary>
    /// <remarks>
    /// Red before this ticket: there was no entry and no send path (<c>evidence/hmi-222/red/</c>).
    /// </remarks>
    [Fact]
    [Trait("ProtocolVector", "CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    public async Task TheReportCarriesTheOperatorTheChargerAndTheConditionAndTheVectorRunsInOrder()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server =>
            {
                server.RespondToUnableToChargeConfirmations = true;
                server.VehicleBusinessStateAfterUnableToChargeResult = BusinessState(2, "CLEARING_MAINTENANCE", "UNABLE_TO_CHARGE");
            });
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        await WaitForTheWireToGoQuietAsync(harness, token);
        int sentBefore = harness.Server.ReceivedEnvelopes.Count;
        int receivedBefore = harness.Server.SentEnvelopes.Count;

        UnableToChargeOption option = Option(shown, "CONNECTION_FAILED");
        Assert.Equal(new WireToGateUnableToChargePrompt(Charger, Maintainer, "CONNECTION_FAILED", null), option.Prompt);
        Assert.Equal(WireToGateUnableToChargeText.ConfirmationText(option.Prompt), option.ConfirmationText);
        Assert.Contains(Charger, option.ConfirmationText, StringComparison.Ordinal);
        Assert.Contains("接不上充电", option.ConfirmationText, StringComparison.Ordinal);
        Assert.Contains(Maintainer, option.ConfirmationText, StringComparison.Ordinal);

        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(option.Prompt, token));
        DateTimeOffset after = DateTimeOffset.UtcNow.AddSeconds(1);

        SentRequest request = Assert.Single(Requests(harness));
        Assert.Equal(JsonValueKind.Null, request.Root.GetProperty("correlationId").ValueKind);
        Assert.Equal(
            ["chargerStationId", "confirmationRequestId", "observedAt", "observedCondition", "operator"],
            request.Payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.True(Guid.TryParseExact(request.ConfirmationRequestId, "D", out _));
        Assert.Equal(request.ConfirmationRequestId, request.MessageId);
        Assert.Equal(Charger, request.Payload.GetProperty("chargerStationId").GetString());
        Assert.Equal("CONNECTION_FAILED", request.Payload.GetProperty("observedCondition").GetString());
        JsonElement sentOperator = request.Payload.GetProperty("operator");
        Assert.Equal(Maintainer, sentOperator.GetProperty("operatorId").GetString());
        Assert.Equal("SESSION", sentOperator.GetProperty("verificationMethod").GetString());
        Assert.InRange(sentOperator.GetProperty("verifiedAt").GetDateTimeOffset(), before, after);
        Assert.InRange(request.Payload.GetProperty("observedAt").GetDateTimeOffset(), before, after);

        // The business state that follows the result takes the vehicle into the clearing, and is acknowledged. That
        // closes the entry and leaves the result line (8005-agv-onboard-hmi#242).
        await harness.WaitUntilAsync(
            () => !harness.ViewModel.UnableToCharge.CanConfirm
                && harness.ViewModel.UnableToCharge.Status == WireToGateUnableToChargeText.ConfirmedStatus
                && harness.Session.CurrentJourney.VehicleBusinessState?.ActivePurpose == "CLEARING_MAINTENANCE"
                && AcknowledgedKinds(harness.Server, sentBefore).Length == 1,
            "the business state after the result to be applied, acknowledged and to close the entry",
            token);
        Assert.False(harness.ViewModel.UnableToCharge.HasNotice);
        Assert.Equal(string.Empty, harness.ViewModel.UnableToCharge.NoticeText);
        Assert.Equal(
            [RequestType, "SnapshotAppliedAck"],
            harness.Server.ReceivedEnvelopes.Skip(sentBefore).Select(envelope => envelope.MessageType).ToArray());
        Assert.Equal(["VEHICLE_BUSINESS_STATE"], AcknowledgedKinds(harness.Server, sentBefore));
        Assert.Equal(
            [ResultType, "VehicleBusinessStateSnapshot"],
            harness.Server.SentEnvelopes.Skip(receivedBefore).Select(envelope => envelope.MessageType).ToArray());
        Assert.Equal("CLEARING_MAINTENANCE", harness.Session.CurrentJourney.VehicleBusinessState!.ActivePurpose);

        // What the server recorded and decided reaches the operator, at the layer the operator reads: the result line,
        // kept through the clearing, and the operator record. Until the review's item 2 the press could lose the race
        // to the snapshot right behind the result and tell nobody.
        Assert.Contains(
            WireToGateUnableToChargeText.DecisionText("REASSIGN_CHARGER"),
            harness.ViewModel.UnableToCharge.StatusText,
            StringComparison.Ordinal);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.Logs.Any(line =>
                line.Kind == OperatorRecordKind.Success
                && line.Message.Contains("服务端已记录现场确认", StringComparison.Ordinal)
                && line.Message.Contains(WireToGateUnableToChargeText.DecisionText("REASSIGN_CHARGER"), StringComparison.Ordinal)),
            "the operator record of what the server recorded and decided on the view model",
            token);
        Assert.Single(harness.Events, item => item.Kind == "UNABLE_TO_CHARGE_CONFIRMED");
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>NEVER_DECIDE_CHARGING_POLICY_LOCALLY</c>: for each of the server's three decisions, the result changes
    /// nothing on the vehicle -- the journey projection is the same object, the visit, charging and battery cells
    /// read the same, no sublot entry opens, no door opens, the journal's recovery state is as it was, the entry is
    /// still offered with the same four conditions, and nothing but the request went out. Only the result line says
    /// what the server decided. The server's next business state is what changes the vehicle, and the vehicle
    /// acknowledges it with <c>SnapshotAppliedAck</c>.
    /// </summary>
    [Theory]
    [Trait("ProtocolVector", "CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    [InlineData("RETRY_LATER", "清桩后回充电队列等待")]
    [InlineData("MANUAL_CHARGING_HOLD", "转人工充电等待，人工充电后由「充电后返回服务」解除")]
    [InlineData("REASSIGN_CHARGER", "清桩后回充电队列，由服务端重新分配充电桩")]
    public async Task TheServersDecisionChangesNothingOnTheVehicleUntilItsNextBusinessState(
        string decision,
        string decisionText)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server =>
            {
                server.RespondToUnableToChargeConfirmations = true;
                server.UnableToChargePolicyDecision = decision;
            });
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        await WaitForTheWireToGoQuietAsync(harness, token);

        WireToGateJourneySnapshot journey = harness.Session.CurrentJourney;
        // Serialized, not compared as records: the state holds lists, and a record compares those by reference.
        string recovery = JsonSerializer.Serialize(await harness.Session.Journal.ReadRecoveryStateAsync(token));
        string visit = harness.ViewModel.VisitText;
        string charging = harness.ViewModel.ChargingStatus;
        string battery = harness.ViewModel.BatteryStatus;
        string[] legs = harness.PlanLegStatuses();
        int sentBefore = harness.Server.ReceivedEnvelopes.Count;

        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CHARGER_FAULT").Prompt, token));
        UnableToChargeDisplay confirmed = await WaitForStatusAsync(harness, WireToGateUnableToChargeText.ConfirmedStatus, token);
        Assert.Contains($"服务端决定：{decisionText}", confirmed.StatusText, StringComparison.Ordinal);

        await AssertWhileAsync(
            () =>
            {
                Assert.Same(journey, harness.Session.CurrentJourney);
                Assert.Equal(visit, harness.ViewModel.VisitText);
                Assert.Equal(charging, harness.ViewModel.ChargingStatus);
                Assert.Equal(battery, harness.ViewModel.BatteryStatus);
                Assert.Equal(legs, harness.PlanLegStatuses());
                Assert.Equal(shown.Options.Select(item => item.Prompt), harness.ViewModel.UnableToCharge.Options.Select(item => item.Prompt));
                Assert.Equal(WireToGateUnableToChargeText.ConfirmedStatus, harness.ViewModel.UnableToCharge.Status);
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

        // The server's next business state is what moves the vehicle on, and it is acknowledged.
        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot", BusinessState(2, "CLEARING_MAINTENANCE", "UNABLE_TO_CHARGE"));
        // The clearing closes the entry; the result line and the decision stay (8005-agv-onboard-hmi#242).
        await harness.WaitUntilAsync(
            () => !harness.ViewModel.UnableToCharge.CanConfirm
                && harness.ViewModel.UnableToCharge.Status == WireToGateUnableToChargeText.ConfirmedStatus
                && harness.ViewModel.UnableToCharge.StatusText.Contains($"服务端决定：{decisionText}", StringComparison.Ordinal)
                && harness.ViewModel.ChargingStatus == "UNABLE_TO_CHARGE",
            "the entry to go, the result line to stay, and the charging cell to change, with the server's next business state",
            token);
        await harness.WaitUntilAsync(
            () => AcknowledgedKinds(harness.Server, sentBefore).Length == 1,
            "the next business state to be acknowledged",
            token);
        Assert.Equal(["VEHICLE_BUSINESS_STATE"], AcknowledgedKinds(harness.Server, sentBefore));
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>CONFIRMED</c> with <c>chargingPolicyDecision: null</c>: the line says the server recorded it and shows no
    /// decision -- nothing is made up for the missing one.
    /// </summary>
    [Fact]
    public async Task AConfirmationWithoutADecisionShowsNoDecision()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server =>
            {
                server.RespondToUnableToChargeConfirmations = true;
                server.UnableToChargePolicyDecision = null;
            });
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);

        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token));

        UnableToChargeDisplay confirmed = await WaitForStatusAsync(harness, WireToGateUnableToChargeText.ConfirmedStatus, token);
        Assert.Contains("服务端已记录现场确认", confirmed.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("服务端决定", confirmed.StatusText, StringComparison.Ordinal);
        foreach (string decision in Decisions)
        {
            Assert.DoesNotContain(
                WireToGateUnableToChargeText.DecisionText(decision), confirmed.StatusText, StringComparison.Ordinal);
        }

        Assert.Single(harness.Events, item => item.Kind == "UNABLE_TO_CHARGE_CONFIRMED");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The four observed conditions are all offered, in the schema's order, each with its own words, and each goes
    /// out as chosen. The line each one puts on the wire is checked against the frozen schema by
    /// <see cref="OutboundSchemaConformance"/> at the end of the run.
    /// </summary>
    /// <remarks>
    /// None is filtered out here: <c>CHARGER_UNREACHABLE</c> and <c>CHARGER_OCCUPIED</c> are not enough to confirm a
    /// failure to charge (<c>REQ-0175</c>), and it is the server that says so in its answer.
    /// </remarks>
    [Theory]
    [InlineData("CHARGER_UNREACHABLE", "车到不了充电桩")]
    [InlineData("CHARGER_OCCUPIED", "充电桩被占用")]
    [InlineData("CONNECTION_FAILED", "接不上充电")]
    [InlineData("CHARGER_FAULT", "充电桩故障")]
    public async Task EveryObservedConditionIsOfferedAndGoesOutAsChosen(string condition, string label)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.RespondToUnableToChargeConfirmations = true);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);

        Assert.Equal(
            ["CHARGER_UNREACHABLE", "CHARGER_OCCUPIED", "CONNECTION_FAILED", "CHARGER_FAULT"],
            shown.Options.Select(item => item.Prompt.ObservedCondition).ToArray());
        Assert.Equal(4, shown.Options.Select(item => item.Label).Distinct(StringComparer.Ordinal).Count());
        UnableToChargeOption option = Option(shown, condition);
        Assert.Equal(label, option.Label);
        Assert.Equal("ConfirmUnableToCharge_" + condition, option.AutomationId);
        Assert.Contains(label, option.ConfirmationText, StringComparison.Ordinal);

        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(option.Prompt, token));

        SentRequest request = Assert.Single(Requests(harness));
        Assert.Equal(condition, request.Payload.GetProperty("observedCondition").GetString());
        Assert.Equal(Charger, request.Payload.GetProperty("chargerStationId").GetString());
        await WaitForStatusAsync(harness, WireToGateUnableToChargeText.ConfirmedStatus, token);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The entry is offered whatever state the charger leg and the charging cycle are in: on the way, arrived,
    /// charging, full, failed or unknown. Which of those a confirmation fits is the server's to judge
    /// (<c>8005-agv-control-server#410</c> rejects, for instance, a cycle already <c>COMPLETE</c>).
    /// </summary>
    [Theory]
    [InlineData("ACTIVE", "EN_ROUTE")]
    [InlineData("ARRIVED", "CHARGING")]
    [InlineData("ARRIVED", "COMPLETE")]
    [InlineData("ARRIVED", "UNABLE_TO_CHARGE")]
    [InlineData("ARRIVED", "UNKNOWN")]
    public async Task TheEntryIsOfferedWhateverTheLegAndTheCycleSay(string legState, string cycleState)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            legs: [ChargerLeg(1, Charger, legState)],
            cycleState: cycleState);

        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);

        Assert.Equal(4, shown.Options.Count);
        Assert.All(shown.Options, item => Assert.Equal(Charger, item.Prompt.ChargerStationId));
        Assert.False(shown.HasNotice);
    }

    /// <summary>
    /// The first condition: the server has to say the vehicle is on a charging claim. A verified maintainer and a
    /// current charger leg under any other purpose offer nothing, and a press that reaches the business service
    /// anyway sends nothing.
    /// </summary>
    [Theory]
    [InlineData("CLEARING_MAINTENANCE")]
    [InlineData("IDLE_RETURN")]
    [InlineData("TRANSPORT")]
    public async Task TheEntryIsNotOfferedUnlessTheServerSaysTheVehicleIsCharging(string purpose)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server => server.RespondToUnableToChargeConfirmations = true,
            purpose: purpose);
        await WaitForThePlanOnTheViewModelAsync(harness, token);

        await AssertTheEntryStaysShutAsync(harness, token);
        Assert.Equal(UnableToChargeDisplay.Empty, harness.ViewModel.UnableToCharge);
        Assert.False(await harness.Business.ConfirmUnableToChargeAsync(Prompt("CONNECTION_FAILED"), token));
        await AssertNothingIsSentAsync(harness, 0, token);
    }

    /// <summary>
    /// <c>wireToGate.unableToChargeEntryEnabled</c> off -- the factory setting: with everything else in place the entry
    /// is not offered, nothing explains it, and a press that reaches the business service is refused and sends nothing.
    /// </summary>
    /// <remarks>
    /// Off by default because a control server without <c>8005-agv-control-server#410</c> ends the session on this
    /// message (<see cref="TodaysServerEndsTheSessionAndThePressIsShownAsUnknownWithoutAReplay"/>), and nothing on the
    /// wire says which kind of server the vehicle is talking to (8005-agv-onboard-hmi#222 review, item 1).
    /// </remarks>
    [Fact]
    public async Task TheEntryIsNotOfferedWhileTheSwitchIsOff()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server => server.RespondToUnableToChargeConfirmations = true,
            recoveryOptions: VerifiedMaintainer() with { UnableToChargeEntryEnabled = false });
        await WaitForThePlanOnTheViewModelAsync(harness, token);

        await AssertTheEntryStaysShutAsync(harness, token);
        Assert.Equal(UnableToChargeDisplay.Empty, harness.ViewModel.UnableToCharge);
        Assert.False(await harness.Business.ConfirmUnableToChargeAsync(Prompt("CONNECTION_FAILED"), token));
        await AssertNothingIsSentAsync(harness, 0, token);
        WireToGateOperatorEvent refused = Assert.Single(harness.Events, item => item.Kind == "UNABLE_TO_CHARGE_BLOCKED");
        Assert.Contains("unableToChargeEntryEnabled", refused.Message, StringComparison.Ordinal);
        // The control: the same maintainer is offered 「充电后返回服务」, so the switch is what shut this entry.
        Assert.True(harness.ViewModel.CanRequestManualChargingReturn);
    }

    /// <summary>
    /// What today's control server does with the message, as the review's probe against a real listener measured
    /// it: no case for it, the connection is closed, nothing comes back. The press ends as unknown and says so to the
    /// operator, and after the reconnect nothing is replayed -- the request was never written to the journal.
    /// </summary>
    [Fact]
    public async Task TodaysServerEndsTheSessionAndThePressIsShownAsUnknownWithoutAReplay()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.CloseConnectionOnUnableToChargeConfirmation = true);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        int connection = harness.Server.ReceivedEnvelopes[^1].Connection;

        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token));

        UnableToChargeDisplay unknown = await WaitForStatusAsync(harness, WireToGateUnableToChargeText.UnknownStatus, token);
        Assert.Contains("结果未知", unknown.StatusText, StringComparison.Ordinal);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.Logs.Any(line =>
                line.Kind == OperatorRecordKind.Warning && line.Message.Contains("结果未知", StringComparison.Ordinal)),
            "the operator record that the result is unknown on the view model",
            token);
        SentRequest request = Assert.Single(Requests(harness));
        Assert.Equal(connection, request.Connection);
        Assert.DoesNotContain(harness.Server.SentEnvelopes, item => item.MessageType is ResultType or "ProtocolProblem");

        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await WaitForTheWireToGoQuietAsync(harness, token);
        Assert.Contains(harness.Server.ReceivedEnvelopes, item => item.Connection > connection);
        await AssertNothingIsSentAsync(harness, 1, token);
        Assert.False(harness.Controller.IsFatalFaultLatched);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The second condition: a verified maintainer -- the maintenance switch on and the administrator proof
    /// configured, the check 「充电后返回服务」 makes. Without either the entry is not offered, nothing explains it to
    /// an operator it is not for, and a press sends nothing.
    /// </summary>
    [Theory]
    [InlineData(false, ProofVariable)]
    [InlineData(true, UnsetProofVariable)]
    public async Task TheEntryIsNotOfferedWithoutAVerifiedMaintainer(bool maintenanceSwitch, string proofVariable)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server => server.RespondToUnableToChargeConfirmations = true,
            recoveryOptions: new WireToGateRecoveryOptions(
                maintenanceSwitch,
                proofVariable,
                "MAINTENANCE_ADMINISTRATOR",
                "CONFIGURED_PROOF",
                UnableToChargeEntryEnabled: true));
        await WaitForThePlanOnTheViewModelAsync(harness, token);

        await AssertTheEntryStaysShutAsync(harness, token);
        Assert.Equal(UnableToChargeDisplay.Empty, harness.ViewModel.UnableToCharge);
        Assert.False(await harness.Business.ConfirmUnableToChargeAsync(Prompt("CONNECTION_FAILED"), token));
        await AssertNothingIsSentAsync(harness, 0, token);
    }

    /// <summary>
    /// The third condition: the plan's current leg is a charger. A charging purpose whose plan has moved on to a
    /// waiting point, or has no leg left, offers no entry and says why; the charger is not taken from a completed
    /// leg.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheEntryIsNotOfferedUnlessTheCurrentLegIsAChargerAndTheScreenSaysWhy(bool allCompleted)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        object[] legs = allCompleted
            ? [ChargerLeg(1, Charger, "COMPLETED")]
            : [ChargerLeg(1, Charger, "COMPLETED"), WaitingLeg(2, "ACTIVE")];
        await using Harness harness = await StartAsync(
            token, server => server.RespondToUnableToChargeConfirmations = true, legs);
        UnableToChargeDisplay display = await WaitForDisplayAsync(
            harness, shown => shown.HasNotice, "the reason the entry is not offered", token);

        await AssertTheEntryStaysShutAsync(harness, token);
        Assert.Empty(display.Options);
        Assert.Contains("当前腿不是充电桩", display.NoticeText, StringComparison.Ordinal);
        Assert.Contains("不可用", display.NoticeText, StringComparison.Ordinal);
        Assert.False(await harness.Business.ConfirmUnableToChargeAsync(Prompt("CONNECTION_FAILED"), token));
        await AssertNothingIsSentAsync(harness, 0, token);
    }

    /// <summary>
    /// <c>REJECTED</c>: the reason code the server sent is shown as sent, no decision is made up, the entry stays,
    /// and the next press is a new confirmation with a new id.
    /// </summary>
    /// <remarks>
    /// The shape <c>8005-agv-control-server#410</c> gives an observation that is not enough to confirm:
    /// <c>REJECTED</c> with <c>chargingPolicyDecision: null</c> -- a rejection changed nothing on the server, so it
    /// claims no decision (8005-agv-onboard-hmi#242 review, N5). The vehicle shows the reason and acts on nothing.
    /// </remarks>
    [Fact]
    public async Task ARejectedConfirmationShowsTheServersReasonAndDecisionAndTheNextPressIsANewConfirmation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server =>
            {
                server.RespondToUnableToChargeConfirmations = true;
                server.UnableToChargeOutcome = "REJECTED";
                server.UnableToChargePolicyDecision = null;
                server.UnableToChargeProblem = new WireToGateProblemPayload("ACTION_NOT_ALLOWED_IN_STATE", null, null);
            });
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);

        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CHARGER_OCCUPIED").Prompt, token));

        UnableToChargeDisplay rejected = await WaitForStatusAsync(harness, WireToGateUnableToChargeText.RejectedStatus, token);
        Assert.Contains("ACTION_NOT_ALLOWED_IN_STATE", rejected.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("服务端决定", rejected.StatusText, StringComparison.Ordinal);
        Assert.Equal(4, rejected.Options.Count);
        Assert.All(rejected.Options, item => Assert.Null(item.Prompt.ResubmittedConfirmationRequestId));
        Assert.Single(harness.Events, item => item.Kind == "UNABLE_TO_CHARGE_REJECTED");

        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(rejected, "CHARGER_OCCUPIED").Prompt, token));
        SentRequest[] requests = Requests(harness);
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(requests[0].ConfirmationRequestId, requests[1].ConfirmationRequestId);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// No answer within the message timeout: the screen says the result is unknown and what to do, and nothing is
    /// sent again on its own. The entry then offers only that one confirmation again -- the same condition -- and the
    /// press carries <b>the first request's confirmation request id</b> and payload byte for byte, under a new
    /// messageId.
    /// </summary>
    [Fact]
    public async Task AnUnansweredConfirmationIsShownAsUnknownAndAResubmissionCarriesTheSameIdAndCondition()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);

        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CHARGER_FAULT").Prompt, token));

        UnableToChargeDisplay unknown = await WaitForStatusAsync(harness, WireToGateUnableToChargeText.UnknownStatus, token);
        Assert.Contains("结果未知，请查看车辆状态后再决定是否重新提交", unknown.StatusText, StringComparison.Ordinal);
        SentRequest first = Assert.Single(Requests(harness));
        await AssertNothingIsSentAsync(harness, 1, token, TimeSpan.FromSeconds(1));
        Assert.Single(harness.Events, item => item.Kind == "UNABLE_TO_CHARGE_UNKNOWN");

        UnableToChargeDisplay again = await WaitForDisplayAsync(
            harness,
            display => display.Options.Count == 1
                && display.Options[0].Prompt.ResubmittedConfirmationRequestId == first.ConfirmationRequestId,
            "the entry to offer only the same confirmation again",
            token);
        UnableToChargeOption resubmission = Assert.Single(again.Options);
        Assert.Equal(
            new WireToGateUnableToChargePrompt(Charger, Maintainer, "CHARGER_FAULT", first.ConfirmationRequestId),
            resubmission.Prompt);
        Assert.Contains("重新提交", resubmission.ConfirmationText, StringComparison.Ordinal);

        harness.Server.RespondToUnableToChargeConfirmations = true;
        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(resubmission.Prompt, token));

        SentRequest[] requests = Requests(harness);
        Assert.Equal(2, requests.Length);
        Assert.Equal(first.ConfirmationRequestId, requests[1].ConfirmationRequestId);
        Assert.Equal(first.Payload.GetRawText(), requests[1].Payload.GetRawText());
        Assert.NotEqual(first.MessageId, requests[1].MessageId);
        UnableToChargeDisplay confirmed = await WaitForStatusAsync(harness, WireToGateUnableToChargeText.ConfirmedStatus, token);
        Assert.Equal(4, confirmed.Options.Count);
        Assert.All(confirmed.Options, item => Assert.Null(item.Prompt.ResubmittedConfirmationRequestId));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A resubmission is offered only to the maintainer who made the unknown confirmation. Another maintainer at the
    /// vehicle is asked afresh -- four conditions, no resubmission -- their press is a new confirmation under their own
    /// name, and the resubmission prompt the first maintainer was shown is refused.
    /// </summary>
    /// <remarks>
    /// The other half of what a resubmission is bound to, beside the charger (hmi#216 M1): without it the second
    /// maintainer would confirm the first one's observation, sent under the first one's operator id. The review's
    /// mutation X2 dropped that comparison and every test stayed green (8005-agv-onboard-hmi#222 review, item 3).
    /// The operator id is read from a variable only this test uses, so changing who is at the vehicle touches no
    /// other test running beside it.
    /// </remarks>
    [Fact]
    public async Task AnotherMaintainerIsAskedAfreshInsteadOfResubmittingSomeoneElsesConfirmation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string OperatorVariable = "W2G_G2_UNABLE_TO_CHARGE_OPERATOR";
        Environment.SetEnvironmentVariable(OperatorVariable, "maintainer-a");
        await using Harness harness = await StartAsync(token, operatorIdEnvironmentVariable: OperatorVariable);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CHARGER_FAULT").Prompt, token));
        await WaitForStatusAsync(harness, WireToGateUnableToChargeText.UnknownStatus, token);
        SentRequest first = Assert.Single(Requests(harness));
        UnableToChargeDisplay resubmission = await WaitForDisplayAsync(
            harness,
            display => display.Options.Count == 1
                && display.Options[0].Prompt.ResubmittedConfirmationRequestId == first.ConfirmationRequestId,
            "the entry to offer maintainer A the same confirmation again",
            token);

        Environment.SetEnvironmentVariable(OperatorVariable, "maintainer-b");
        harness.ViewModel.RefreshWireToGateInputState();
        UnableToChargeDisplay afresh = await WaitForDisplayAsync(
            harness,
            display => display.Options.Count == 4,
            "the entry to ask maintainer B afresh",
            token);
        Assert.All(afresh.Options, item =>
        {
            Assert.Equal("maintainer-b", item.Prompt.OperatorId);
            Assert.Null(item.Prompt.ResubmittedConfirmationRequestId);
        });

        harness.Server.RespondToUnableToChargeConfirmations = true;
        Assert.False(await harness.Business.ConfirmUnableToChargeAsync(resubmission.Options[0].Prompt, token));
        await AssertNothingIsSentAsync(harness, 1, token);
        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(afresh, "CHARGER_FAULT").Prompt, token));

        SentRequest[] requests = Requests(harness);
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(first.ConfirmationRequestId, requests[1].ConfirmationRequestId);
        Assert.Equal("maintainer-a", first.Payload.GetProperty("operator").GetProperty("operatorId").GetString());
        Assert.Equal("maintainer-b", requests[1].Payload.GetProperty("operator").GetProperty("operatorId").GetString());
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// Another condition chosen while a confirmation is unknown is not offered, and a press for it that reaches the
    /// business service is refused: it would be a second confirmation of the same attempt while the first may have
    /// been taken.
    /// </summary>
    [Fact]
    public async Task AnotherConditionIsRefusedWhileAConfirmationIsUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CHARGER_FAULT").Prompt, token));
        await WaitForStatusAsync(harness, WireToGateUnableToChargeText.UnknownStatus, token);

        Assert.False(await harness.Business.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token));

        await AssertNothingIsSentAsync(harness, 1, token);
        Assert.Single(harness.Events, item => item.Kind == "UNABLE_TO_CHARGE_BLOCKED");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A second press while the first is still unanswered sends nothing: exactly one request is on the wire when the
    /// answer finally comes, and the entry is withdrawn, with a line saying why, for as long as it is awaited.
    /// </summary>
    /// <remarks>
    /// Pressed twice over, for the reason <c>ManualStationClearanceG2Tests.ASecondPressWhileTheFirstIsUnansweredSendsNothing</c>
    /// gives: with the prompt the operator was shown, the prompt check would refuse it too, so it is also pressed with
    /// the resubmission of the request in flight, which only the gate can refuse.
    /// </remarks>
    [Fact]
    public async Task ASecondPressWhileTheFirstIsUnansweredSendsNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        WireToGateUnableToChargePrompt prompt = Option(shown, "CONNECTION_FAILED").Prompt;

        Task<bool> first = harness.ViewModel.ConfirmUnableToChargeAsync(prompt, token);
        await harness.WaitUntilAsync(() => Requests(harness).Length == 1, "the first request to reach the server", token);
        UnableToChargeDisplay awaiting = await WaitForDisplayAsync(
            harness,
            display => display is { CanConfirm: false, HasNotice: true },
            "the entry to be withdrawn while the answer is awaited",
            token);
        Assert.Contains("等待服务端应答", awaiting.NoticeText, StringComparison.Ordinal);

        SentRequest request = Assert.Single(Requests(harness));
        Assert.False(await harness.Business.ConfirmUnableToChargeAsync(prompt, token));
        Assert.False(await harness.Business.ConfirmUnableToChargeAsync(
            prompt with { ResubmittedConfirmationRequestId = request.ConfirmationRequestId }, token));
        Assert.False(first.IsCompleted);
        WireToGateOperatorEvent[] refusals = [.. harness.Events.Where(item => item.Kind == "UNABLE_TO_CHARGE_BLOCKED")];
        Assert.Equal(2, refusals.Length);
        Assert.All(refusals, item => Assert.Contains("还在等待服务端应答", item.Message, StringComparison.Ordinal));

        // Answered straight away: the first press is waiting on the product's own message timeout.
        await harness.Server.SendUnableToChargeResultAsync(request.MessageId, request.ConfirmationRequestId);
        Assert.True(await first);
        await WaitForStatusAsync(harness, WireToGateUnableToChargeText.ConfirmedStatus, token);
        await AssertNothingIsSentAsync(harness, 1, token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The same result arriving twice is handled once: one record for the operator, nothing raised for the second
    /// copy, and the session stays up.
    /// </summary>
    [Fact]
    public async Task ARepeatedResultIsHandledOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server =>
            {
                server.RespondToUnableToChargeConfirmations = true;
                server.UnableToChargeResponseCopies = 2;
            });
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        int raised = 0;
        harness.Session.ServerCommandReceived += (_, args) =>
        {
            if (args.Value.MessageType == ResultType)
            {
                Interlocked.Increment(ref raised);
            }
        };

        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token));
        await WaitForStatusAsync(harness, WireToGateUnableToChargeText.ConfirmedStatus, token);
        await harness.WaitUntilAsync(
            () => harness.Server.SentEnvelopes.Count(item => item.MessageType == ResultType) == 2,
            "both copies of the result to be sent",
            token);

        await AssertWhileAsync(
            () =>
            {
                Assert.Equal(0, Volatile.Read(ref raised));
                Assert.Single(harness.Events, item => item.Kind.StartsWith("UNABLE_TO_CHARGE_", StringComparison.Ordinal));
                Assert.True(harness.Session.Current.Connected);
            },
            token);
        await harness.Session.Client.SendHeartbeatAsync(token);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A result that arrives after the wait ran out settles the unknown, once: the line moves from unknown to what
    /// the server said, a second copy changes nothing, and a result for a confirmation this vehicle is not waiting on
    /// is ignored.
    /// </summary>
    [Fact]
    public async Task AResultThatArrivesAfterTheTimeoutSettlesTheUnknownOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token));
        await WaitForStatusAsync(harness, WireToGateUnableToChargeText.UnknownStatus, token);
        SentRequest request = Assert.Single(Requests(harness));

        // Not this vehicle's confirmation: ignored, the line stays unknown.
        await harness.Server.SendUnableToChargeResultAsync(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        await AssertWhileAsync(
            () => Assert.Equal(WireToGateUnableToChargeText.UnknownStatus, harness.ViewModel.UnableToCharge.Status),
            token);

        await harness.Server.SendUnableToChargeResultAsync(
            request.MessageId, request.ConfirmationRequestId, chargingPolicyDecision: "RETRY_LATER");
        UnableToChargeDisplay confirmed = await WaitForStatusAsync(harness, WireToGateUnableToChargeText.ConfirmedStatus, token);
        Assert.Contains($"服务端决定：{WireToGateUnableToChargeText.DecisionText("RETRY_LATER")}", confirmed.StatusText, StringComparison.Ordinal);
        Assert.All(confirmed.Options, item => Assert.Null(item.Prompt.ResubmittedConfirmationRequestId));

        await harness.Server.SendUnableToChargeResultAsync(
            request.MessageId, request.ConfirmationRequestId, chargingPolicyDecision: "RETRY_LATER");
        await AssertWhileAsync(
            () =>
            {
                Assert.Single(harness.Events, item => item.Kind == "UNABLE_TO_CHARGE_CONFIRMED");
                Assert.Equal(WireToGateUnableToChargeText.ConfirmedStatus, harness.ViewModel.UnableToCharge.Status);
                Assert.True(harness.Session.Current.Connected);
            },
            token);
        await AssertNothingIsSentAsync(harness, 1, token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The server ends the charging claim while the press waits, and no answer comes: the press ends as unknown, and
    /// the operator record says the claim is over and the entry closed -- not to resubmit, which the gone entry no
    /// longer allows (8005-agv-onboard-hmi#222 incremental review, item 1).
    /// </summary>
    [Fact]
    public async Task AnUnknownPressWhoseChargingTheServerEndedDoesNotAskForAResubmission()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);

        Task<bool> press = harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token);
        await harness.WaitUntilAsync(() => Requests(harness).Length == 1, "the request to reach the server", token);
        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", BusinessState(2, "TRANSPORT", "NOT_CHARGING"));
        Assert.False(await press);

        LogLineViewModel record = await WaitForLogAsync(
            harness,
            line => line.Kind == OperatorRecordKind.Warning && line.Message.Contains("结果未知", StringComparison.Ordinal),
            "the operator record that the press's result is unknown",
            token);
        Assert.Contains(WireToGateUnableToChargeText.ChargingEndedSentence[..^1], record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("重新提交", record.Message, StringComparison.Ordinal);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.UnableToCharge == UnableToChargeDisplay.Empty,
            "the entry and the result line to be gone with the charging claim",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// An answer correlated to the request's messageId but naming another confirmation request id is refused as
    /// <c>CORRELATION_INVALID</c>: the press ends as unknown, and nothing the answer says is shown as this press's
    /// result (8005-agv-onboard-hmi#222 incremental review, Y6).
    /// </summary>
    [Fact]
    public async Task AnAnswerNamingAnotherConfirmationIsNotTakenAsThisPresssResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);

        Task<bool> press = harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token);
        await harness.WaitUntilAsync(() => Requests(harness).Length == 1, "the request to reach the server", token);
        SentRequest request = Assert.Single(Requests(harness));
        await harness.Server.SendUnableToChargeResultAsync(request.MessageId, Guid.NewGuid().ToString("D"));
        Assert.False(await press);

        // Unknown, and the same confirmation offered again: the answer did not settle it.
        await WaitForDisplayAsync(
            harness,
            display => display.Status == WireToGateUnableToChargeText.UnknownStatus
                && display.Options.Count == 1
                && display.Options[0].Prompt.ResubmittedConfirmationRequestId == request.ConfirmationRequestId,
            "the press to end as unknown with the same confirmation offered again",
            token);
        await AssertWhileAsync(
            () => Assert.DoesNotContain(
                harness.Events, item => item.Kind is "UNABLE_TO_CHARGE_CONFIRMED" or "UNABLE_TO_CHARGE_REJECTED"),
            token);
        Assert.Contains(
            harness.Logger.Exceptions,
            entry => entry.Exception is InvalidDataException { Message: "CORRELATION_INVALID" }
                && entry.Message.StartsWith("现场确认充不上未完成", StringComparison.Ordinal));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A second, different answer under a confirmation request id already answered is the server contradicting
    /// itself: the session is failed closed with <c>BUSINESS_ID_CONTENT_CONFLICT</c>, and the answer on screen is not
    /// rewritten by the second one.
    /// </summary>
    [Fact]
    public async Task ADifferentAnswerUnderAnAnsweredIdFailsTheSessionAndDoesNotRewriteTheResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.RespondToUnableToChargeConfirmations = true);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token));
        UnableToChargeDisplay confirmed = await WaitForStatusAsync(harness, WireToGateUnableToChargeText.ConfirmedStatus, token);
        SentRequest request = Assert.Single(Requests(harness));
        List<string> diagnostics = [];
        harness.Session.Client.DiagnosticRecorded += (_, args) =>
        {
            lock (diagnostics)
            {
                diagnostics.Add(args.Value);
            }
        };

        await harness.Server.SendUnableToChargeResultAsync(
            request.MessageId, request.ConfirmationRequestId, chargingPolicyDecision: "MANUAL_CHARGING_HOLD");

        await harness.WaitUntilAsync(() => !harness.Session.Current.Connected, "the session to be failed closed", token);
        lock (diagnostics)
        {
            Assert.Contains(diagnostics, line => line.Contains("BUSINESS_ID_CONTENT_CONFLICT", StringComparison.Ordinal));
        }

        await AssertWhileAsync(
            () =>
            {
                Assert.Equal(confirmed.StatusText, harness.ViewModel.UnableToCharge.StatusText);
                Assert.DoesNotContain(WireToGateUnableToChargeText.DecisionText("MANUAL_CHARGING_HOLD"), harness.ViewModel.UnableToCharge.StatusText, StringComparison.Ordinal);
                Assert.Single(harness.Events, item => item.Kind.StartsWith("UNABLE_TO_CHARGE_", StringComparison.Ordinal));
            },
            token);
    }

    /// <summary>
    /// The server answers the request itself with a <c>ProtocolProblem</c> -- what a server without the
    /// <c>8005-agv-control-server#410</c> handling may do with a message it does not take. That is an answer, shown
    /// as such with its reason code and never as unknown, and it ends the confirmation request id: the next press is a
    /// new confirmation.
    /// </summary>
    [Fact]
    public async Task AProtocolProblemToTheRequestIsShownAsNotAcceptedAndEndsItsId()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.UnableToChargeProtocolProblem = "PROTOCOL_SCHEMA_INVALID");
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);

        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CHARGER_FAULT").Prompt, token));

        UnableToChargeDisplay notAccepted = await WaitForDisplayAsync(
            harness,
            display => display.Status == WireToGateUnableToChargeText.NotAcceptedStatus && display.CanConfirm,
            "the refusal shown and the entry offered again",
            token);
        Assert.Contains("服务端没有受理", notAccepted.StatusText, StringComparison.Ordinal);
        Assert.Contains("PROTOCOL_SCHEMA_INVALID", notAccepted.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("结果未知", notAccepted.StatusText, StringComparison.Ordinal);
        Assert.Equal(4, notAccepted.Options.Count);
        Assert.Single(harness.Events, item => item.Kind == "UNABLE_TO_CHARGE_NOT_ACCEPTED");
        Assert.DoesNotContain(harness.Events, item => item.Kind == "UNABLE_TO_CHARGE_UNKNOWN");
        Assert.True(harness.Session.Current.Connected);

        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(notAccepted, "CHARGER_FAULT").Prompt, token));
        string[] ids = [.. Requests(harness).Select(request => request.ConfirmationRequestId)];
        Assert.Equal(2, ids.Length);
        Assert.NotEqual(ids[0], ids[1]);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A result whose fields contradict each other or fall outside the schema is refused rather than shown: a
    /// confirmation that carries a problem, and a decision the schema does not have. The press ends as unknown and the
    /// screen states neither half.
    /// </summary>
    /// <remarks>
    /// A rejection that carries a decision is <b>not</b> such a result -- it is the shape the server gives an
    /// observation that is not enough to confirm, see
    /// <see cref="ARejectedConfirmationShowsTheServersReasonAndDecisionAndTheNextPressIsANewConfirmation"/>. The
    /// out-of-schema decision is registered in <c>schema-known-violations.json</c> as deliberate.
    /// </remarks>
    [Theory]
    [InlineData("CONFIRMED", true, "REASSIGN_CHARGER")]
    [InlineData("CONFIRMED", false, "CHARGE_ELSEWHERE")]
    public async Task AResultThatContradictsItselfOrTheSchemaIsRefusedAndThePressEndsAsUnknown(
        string outcome,
        bool withProblem,
        string decision)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server =>
            {
                server.RespondToUnableToChargeConfirmations = true;
                server.UnableToChargeOutcome = outcome;
                server.UnableToChargePolicyDecision = decision;
                server.UnableToChargeProblem = withProblem
                    ? new WireToGateProblemPayload("ACTION_NOT_ALLOWED_IN_STATE", null, null)
                    : null;
            });
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);

        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token));

        await WaitForStatusAsync(harness, WireToGateUnableToChargeText.UnknownStatus, token);
        await AssertWhileAsync(
            () =>
            {
                Assert.Equal(WireToGateUnableToChargeText.UnknownStatus, harness.ViewModel.UnableToCharge.Status);
                Assert.DoesNotContain(
                    harness.Events,
                    item => item.Kind is "UNABLE_TO_CHARGE_CONFIRMED" or "UNABLE_TO_CHARGE_REJECTED");
            },
            token);
        Assert.Contains(
            harness.Logger.Exceptions,
            entry => entry.Exception.Message == "PROTOCOL_SCHEMA_INVALID"
                && entry.Message.StartsWith("现场确认充不上未完成", StringComparison.Ordinal));
        Assert.False(harness.Controller.IsFatalFaultLatched);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The session drops while the answer is awaited: the press ends as unknown, the unknown stays on screen while
    /// offline, and after the reconnect the entry offers the same confirmation again -- same id, same payload. The
    /// answer the server replays into the new session for the first request then arrives after the resubmission's own
    /// and is not handled a second time.
    /// </summary>
    [Fact]
    public async Task ADroppedSessionLeavesAnUnknownThatIsResubmittedUnderTheSameIdAfterReconnecting()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);

        Task<bool> press = harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token);
        await harness.WaitUntilAsync(() => Requests(harness).Length == 1, "the request to reach the server", token);
        await harness.Session.Client.DisconnectAsync();
        Assert.False(await press);

        UnableToChargeDisplay offline = await WaitForStatusAsync(harness, WireToGateUnableToChargeText.UnknownStatus, token);
        Assert.False(offline.CanConfirm);
        SentRequest first = Assert.Single(Requests(harness));

        harness.Server.RespondToUnableToChargeConfirmations = true;
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await WaitForTheWireToGoQuietAsync(harness, token);
        await AssertNothingIsSentAsync(harness, 1, token);
        UnableToChargeDisplay again = await WaitForDisplayAsync(
            harness,
            display => display.Options.Count == 1
                && display.Options[0].Prompt.ResubmittedConfirmationRequestId == first.ConfirmationRequestId
                && display.Status == WireToGateUnableToChargeText.UnknownStatus,
            "the entry to offer the same confirmation again after the reconnect, the unknown still shown",
            token);
        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(again.Options[0].Prompt, token));
        SentRequest[] requests = Requests(harness);
        Assert.Equal(2, requests.Length);
        Assert.Equal(first.ConfirmationRequestId, requests[1].ConfirmationRequestId);
        Assert.Equal(first.Payload.GetRawText(), requests[1].Payload.GetRawText());
        Assert.NotEqual(first.Connection, requests[1].Connection);
        await WaitForStatusAsync(harness, WireToGateUnableToChargeText.ConfirmedStatus, token);

        // The first request's answer, replayed into the new session after the resubmission was answered.
        await harness.Server.SendUnableToChargeResultAsync(first.MessageId, first.ConfirmationRequestId);
        await AssertWhileAsync(
            () =>
            {
                Assert.Single(harness.Events, item => item.Kind == "UNABLE_TO_CHARGE_CONFIRMED");
                Assert.True(harness.Session.Current.Connected);
            },
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The confirmation is bound to what the operator was shown. The plan moves the vehicle to another charger while
    /// the dialog is open: the press made for the first charger is refused and sends nothing, the operator is told,
    /// and a press made for what is shown now sends the second charger.
    /// </summary>
    [Fact]
    public async Task APressMadeForOneChargerDoesNotConfirmAnother()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.RespondToUnableToChargeConfirmations = true);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        WireToGateUnableToChargePrompt stale = Option(shown, "CHARGER_FAULT").Prompt;
        Assert.Equal(Charger, stale.ChargerStationId);

        await harness.Server.SendJourneySnapshotAsync(
            "UpcomingStopPlanSnapshot",
            Payloads.Plan(2, [ChargerLeg(1, Charger, "COMPLETED"), ChargerLeg(2, OtherCharger, "ACTIVE")]));
        UnableToChargeDisplay current = await WaitForDisplayAsync(
            harness,
            display => display.Options.Count > 0 && display.Options[0].Prompt.ChargerStationId == OtherCharger,
            "the entry to name the other charger",
            token);

        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(stale, token));
        await AssertNothingIsSentAsync(harness, 0, token);
        Assert.Single(harness.Events, item => item.Kind == "UNABLE_TO_CHARGE_BLOCKED");
        Assert.False(harness.ViewModel.UnableToCharge.HasStatus);

        UnableToChargeOption now = Option(current, "CHARGER_FAULT");
        Assert.Contains(OtherCharger, now.ConfirmationText, StringComparison.Ordinal);
        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(now.Prompt, token));
        SentRequest request = Assert.Single(Requests(harness));
        Assert.Equal(OtherCharger, request.Payload.GetProperty("chargerStationId").GetString());
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A charging claim that ended and one that begins later are two attempts, even at the same charger with the same
    /// maintainer. Once the server has said the vehicle is no longer charging, what this vehicle held of an unknown
    /// confirmation is gone: the entry that comes back asks afresh, shows no old result, and its press carries a new
    /// id.
    /// </summary>
    [Fact]
    public async Task ALaterChargingAttemptStartsFromNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token));
        await WaitForStatusAsync(harness, WireToGateUnableToChargeText.UnknownStatus, token);
        SentRequest unanswered = Assert.Single(Requests(harness));

        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", BusinessState(2, "TRANSPORT", "NOT_CHARGING"));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.UnableToCharge == UnableToChargeDisplay.Empty,
            "the first attempt to end on the view model",
            token);

        harness.Server.RespondToUnableToChargeConfirmations = true;
        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", BusinessState(3, Charging, "CHARGING"));
        UnableToChargeDisplay later = await WaitForEntryAsync(harness, token);
        Assert.Equal(4, later.Options.Count);
        Assert.All(later.Options, item => Assert.Null(item.Prompt.ResubmittedConfirmationRequestId));
        Assert.False(later.HasStatus);

        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(later, "CONNECTION_FAILED").Prompt, token));
        SentRequest[] requests = Requests(harness);
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(unanswered.ConfirmationRequestId, requests[1].ConfirmationRequestId);
        Assert.Equal(requests[1].ConfirmationRequestId, requests[1].MessageId);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A confirmation the server takes moves the vehicle from <c>CHARGING</c> to <c>CLEARING_MAINTENANCE</c>
    /// (<c>chargingCycleState=UNABLE_TO_CHARGE</c>), the business state arriving right behind the result, as
    /// <c>8005-agv-control-server#410</c> does. That is the same charging claim going on into its clearing, not its
    /// end: the result line -- that the server recorded it, and its decision -- stays on screen through it, while the
    /// entry itself closes with the charging purpose.
    /// </summary>
    /// <remarks>
    /// Red before <c>8005-agv-onboard-hmi#242</c>: any purpose other than <c>CHARGING</c> counted as the claim being
    /// over, and the line went within a second of the result (cs#410 G3, <c>G3-13-23</c>/<c>G3-13-27</c>).
    /// </remarks>
    [Fact]
    public async Task AConfirmedResultStaysOnTheLineThroughTheClearingItLeadsTo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server =>
            {
                server.RespondToUnableToChargeConfirmations = true;
                server.UnableToChargePolicyDecision = "MANUAL_CHARGING_HOLD";
                server.VehicleBusinessStateAfterUnableToChargeResult =
                    BusinessState(2, "CLEARING_MAINTENANCE", "UNABLE_TO_CHARGE");
            });
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        await WaitForTheWireToGoQuietAsync(harness, token);
        int sentBefore = harness.Server.ReceivedEnvelopes.Count;

        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.VehicleBusinessState?.ActivePurpose == "CLEARING_MAINTENANCE"
                && AcknowledgedKinds(harness.Server, sentBefore).Length == 1
                && harness.ViewModel.ChargingStatus == "UNABLE_TO_CHARGE",
            "the clearing business state behind the result to be applied and acknowledged",
            token);

        await AssertWhileAsync(
            () =>
            {
                UnableToChargeDisplay display = harness.ViewModel.UnableToCharge;
                Assert.False(display.CanConfirm);
                Assert.True(display.HasStatus);
                Assert.Equal(WireToGateUnableToChargeText.ConfirmedStatus, display.Status);
                Assert.Contains("服务端已记录现场确认", display.StatusText, StringComparison.Ordinal);
                Assert.Contains(
                    $"服务端决定：{WireToGateUnableToChargeText.DecisionText("MANUAL_CHARGING_HOLD")}",
                    display.StatusText,
                    StringComparison.Ordinal);
                Assert.Equal(WireToGateUnableToChargeText.ConfirmedStatus, WireToGateUnableToChargeText.Status(harness.Business.UnableToCharge.LastOutcome));
            },
            token,
            TimeSpan.FromSeconds(1.5));
        Assert.Single(harness.Events, item => item.Kind == "UNABLE_TO_CHARGE_CONFIRMED");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// What ends the line kept through the clearing: a purpose that is neither <c>CHARGING</c> nor
    /// <c>CLEARING_MAINTENANCE</c>, a clearing whose charging cycle is no longer <c>UNABLE_TO_CHARGE</c> (the cycle
    /// closed), or a <c>CHARGING</c> purpose after the clearing -- a new attempt, which starts from nothing, as
    /// <see cref="ALaterChargingAttemptStartsFromNothing"/> says.
    /// </summary>
    [Theory]
    [InlineData(null, "NOT_CHARGING")]
    [InlineData("IDLE_RETURN", "NOT_CHARGING")]
    [InlineData("TRANSPORT", "NOT_CHARGING")]
    [InlineData("CLEARING_MAINTENANCE", "NOT_CHARGING")]
    [InlineData("CLEARING_MAINTENANCE", "COMPLETE")]
    [InlineData(Charging, "ALLOCATED")]
    public async Task TheLineKeptThroughTheClearingGoesWhenTheClaimOrItsCycleEnds(string? purpose, string cycleState)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server =>
            {
                server.RespondToUnableToChargeConfirmations = true;
                server.UnableToChargePolicyDecision = "REASSIGN_CHARGER";
                server.VehicleBusinessStateAfterUnableToChargeResult =
                    BusinessState(2, "CLEARING_MAINTENANCE", "UNABLE_TO_CHARGE");
            });
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CHARGER_FAULT").Prompt, token));
        await WaitForDisplayAsync(
            harness,
            display => !display.CanConfirm
                && display.Status == WireToGateUnableToChargeText.ConfirmedStatus
                && harness.Session.CurrentJourney.VehicleBusinessState?.ActivePurpose == "CLEARING_MAINTENANCE",
            "the confirmed line to stand in the clearing",
            token);

        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", BusinessState(3, purpose, cycleState));
        UnableToChargeDisplay after = await WaitForDisplayAsync(
            harness,
            display => !display.HasStatus
                && harness.Session.CurrentJourney.VehicleBusinessState?.Revision == 3,
            "the result line to go with the claim or its cycle",
            token);
        Assert.Equal(string.Empty, after.Status);
        if (purpose == Charging)
        {
            // A new attempt: asked afresh, with all four conditions and no resubmission.
            UnableToChargeDisplay fresh = await WaitForEntryAsync(harness, token);
            Assert.Equal(4, fresh.Options.Count);
            Assert.All(fresh.Options, item => Assert.Null(item.Prompt.ResubmittedConfirmationRequestId));
            Assert.False(fresh.HasStatus);
        }
        else
        {
            Assert.Equal(UnableToChargeDisplay.Empty, after);
        }

        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A rejection is not carried into a clearing the server then enters on its own confirmation: the maintainer's
    /// report was refused, the system confirmed the failure itself, and a line saying 「服务端未确认充不上」 beside a
    /// charging cell that reads <c>UNABLE_TO_CHARGE</c> would contradict it (8005-agv-onboard-hmi#242 review, S2).
    /// </summary>
    [Fact]
    public async Task ARejectionIsNotCarriedIntoTheClearingTheServerEntersOnItsOwn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token,
            server =>
            {
                server.RespondToUnableToChargeConfirmations = true;
                server.UnableToChargeOutcome = "REJECTED";
                server.UnableToChargePolicyDecision = null;
                server.UnableToChargeProblem = new WireToGateProblemPayload("ACTION_NOT_ALLOWED_IN_STATE", null, null);
            });
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CHARGER_OCCUPIED").Prompt, token));
        await WaitForStatusAsync(harness, WireToGateUnableToChargeText.RejectedStatus, token);

        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot", BusinessState(2, "CLEARING_MAINTENANCE", "UNABLE_TO_CHARGE"));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.ChargingStatus == "UNABLE_TO_CHARGE",
            "the clearing to reach the view model",
            token);
        await AssertWhileAsync(
            () =>
            {
                Assert.Equal(UnableToChargeDisplay.Empty, harness.ViewModel.UnableToCharge);
                Assert.Null(harness.Business.UnableToCharge.LastOutcome);
            },
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// An unknown stays into the clearing, for the server may yet answer it, but its line no longer asks for a
    /// resubmission the closed entry does not allow (8005-agv-onboard-hmi#242 review, S3); the late result then
    /// settles it there.
    /// </summary>
    [Fact]
    public async Task AnUnknownInTheClearingDoesNotAskForAResubmissionAndALateResultSettlesIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(token);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.False(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token));
        await WaitForStatusAsync(harness, WireToGateUnableToChargeText.UnknownStatus, token);
        SentRequest request = Assert.Single(Requests(harness));

        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot", BusinessState(2, "CLEARING_MAINTENANCE", "UNABLE_TO_CHARGE"));
        UnableToChargeDisplay inClearing = await WaitForDisplayAsync(
            harness,
            display => !display.CanConfirm
                && display.StatusText.Contains(WireToGateUnableToChargeText.InClearingSentence, StringComparison.Ordinal),
            "the unknown line to say the entry closed with the clearing",
            token);
        Assert.Equal(WireToGateUnableToChargeText.UnknownStatus, inClearing.Status);
        Assert.DoesNotContain("重新提交", inClearing.StatusText, StringComparison.Ordinal);

        await harness.Server.SendUnableToChargeResultAsync(
            request.MessageId, request.ConfirmationRequestId, chargingPolicyDecision: "MANUAL_CHARGING_HOLD");
        UnableToChargeDisplay settled = await WaitForStatusAsync(harness, WireToGateUnableToChargeText.ConfirmedStatus, token);
        Assert.False(settled.CanConfirm);
        Assert.Contains(
            $"服务端决定：{WireToGateUnableToChargeText.DecisionText("MANUAL_CHARGING_HOLD")}",
            settled.StatusText,
            StringComparison.Ordinal);
        Assert.Single(harness.Events, item => item.Kind == "UNABLE_TO_CHARGE_CONFIRMED");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A result is about one charger. A plan that puts the vehicle at another charger, still charging and with no
    /// clearing in between, takes the old line off the screen and asks afresh there (8005-agv-onboard-hmi#242
    /// review, N1).
    /// </summary>
    [Fact]
    public async Task AResultAboutOneChargerIsNotShownAtAnother()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.RespondToUnableToChargeConfirmations = true);
        UnableToChargeDisplay shown = await WaitForEntryAsync(harness, token);
        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CHARGER_FAULT").Prompt, token));
        await WaitForStatusAsync(harness, WireToGateUnableToChargeText.ConfirmedStatus, token);

        await harness.Server.SendJourneySnapshotAsync(
            "UpcomingStopPlanSnapshot",
            Payloads.Plan(2, [ChargerLeg(1, Charger, "COMPLETED"), ChargerLeg(2, OtherCharger, "ARRIVED")]));
        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", BusinessState(2, Charging, "CHARGING"));
        UnableToChargeDisplay there = await WaitForDisplayAsync(
            harness,
            display => display.Options.Count == 4 && display.Options[0].Prompt.ChargerStationId == OtherCharger,
            "the entry to ask afresh at the other charger",
            token);
        Assert.False(there.HasStatus);
        Assert.Null(harness.Business.UnableToCharge.LastOutcome);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A restart on the same journal, against a server that pushes nothing on the new session: whether the entry is
    /// offered comes from the restored business state and plan alone. The request was never written to disk
    /// (<c>durableBeforeSend: false</c>), so nothing is sent on its own, the operator is asked afresh, and the press
    /// that follows is a new confirmation.
    /// </summary>
    [Fact]
    public async Task ARestartOffersTheEntryFromTheRestoredStateAndAsksTheOperatorAfresh()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness first = await StartAsync(token);
        UnableToChargeDisplay shown = await WaitForEntryAsync(first, token);
        Assert.False(await first.ViewModel.ConfirmUnableToChargeAsync(Option(shown, "CONNECTION_FAILED").Prompt, token));
        await WaitForStatusAsync(first, WireToGateUnableToChargeText.UnknownStatus, token);
        SentRequest unanswered = Assert.Single(Requests(first));

        await first.StopVehicleAsync();
        first.Server.SimulateOnboardProcessRestart();
        first.Server.VectorJourneySnapshotsAfterRecovery = [];
        first.Server.RespondToUnableToChargeConfirmations = true;
        await using Harness second = await Harness.StartAgainstAsync(
            first.Server, token, first.JournalPath, recoveryOptions: VerifiedMaintainer());

        UnableToChargeDisplay restored = await WaitForEntryAsync(second, token);
        Assert.Equal(4, restored.Options.Count);
        Assert.All(restored.Options, item => Assert.Null(item.Prompt.ResubmittedConfirmationRequestId));
        Assert.False(restored.HasStatus);
        Assert.Equal(Charging, second.Session.CurrentJourney.VehicleBusinessState!.ActivePurpose);
        await AssertNothingIsSentAsync(second, 1, token);

        Assert.True(await second.ViewModel.ConfirmUnableToChargeAsync(Option(restored, "CONNECTION_FAILED").Prompt, token));
        SentRequest[] requests = Requests(second);
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(unanswered.ConfirmationRequestId, requests[1].ConfirmationRequestId);
        Assert.Empty(second.UiErrors);
    }

    /// <summary>
    /// A fatal-fault latch does not shut this entry, on either refresh path, and a confirmation made under the latch
    /// opens no door. 「充电后返回服务」 beside it, offered by the same verified maintainer, shuts as it always has --
    /// the control that shows the harness can shut an entry at all.
    /// </summary>
    /// <remarks>
    /// The decision <c>8005-agv-onboard-hmi#221</c> made for the station clearance, made the same way here: this entry
    /// is not one of the nine recovery entries (<c>RecoveryEntryWriteSiteArchitectureTests</c>), because it does no IO
    /// (<c>WireToGateUnableToChargeTests.TheEntrysBusinessCodeNamesNoDoorNoIoAndNoRecoveryState</c>). A faulted vehicle
    /// that did not charge should still be able to say so.
    /// </remarks>
    [Fact]
    public async Task ALatchLeavesTheEntryOpenAndOpensNoDoor()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.RespondToUnableToChargeConfirmations = true);
        await WaitForEntryAsync(harness, token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanRequestManualChargingReturn,
            "the recovery entry used as the control to be offered",
            token);

        harness.Controller.EnterFatalFault("UI_COMMAND_FAILED", OnboardFatalFaultBanner.UiCommandFailed);
        await harness.WaitUntilAsync(
            () => !harness.ViewModel.CanRequestManualChargingReturn,
            "the latch to shut the recovery entries",
            token);
        Assert.True(harness.ViewModel.UnableToCharge.CanConfirm);

        // The other path: what a resent entry request runs.
        harness.ViewModel.RefreshWireToGateInputState();
        Assert.False(harness.ViewModel.CanRequestManualChargingReturn);
        UnableToChargeDisplay latched = harness.ViewModel.UnableToCharge;
        Assert.True(latched.CanConfirm);

        Assert.True(await harness.ViewModel.ConfirmUnableToChargeAsync(Option(latched, "CHARGER_FAULT").Prompt, token));
        await WaitForStatusAsync(harness, WireToGateUnableToChargeText.ConfirmedStatus, token);
        Assert.Single(Requests(harness));
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.True(harness.Controller.IsFatalFaultLatched);
    }

    /// <summary>
    /// The send path checks its payload before anything is written, the way the other requests do: a value the
    /// schema does not allow is refused as <c>PROTOCOL_SCHEMA_INVALID</c> and nothing reaches the server.
    /// </summary>
    [Theory]
    [InlineData("chargerStationId")]
    [InlineData("observedCondition")]
    [InlineData("operatorId")]
    [InlineData("verificationMethod")]
    [InlineData("observedAt")]
    public async Task APayloadTheSchemaDoesNotAllowIsRefusedBeforeItIsSent(string broken)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartAsync(
            token, server => server.RespondToUnableToChargeConfirmations = true);
        await WaitForEntryAsync(harness, token);
        string id = Guid.NewGuid().ToString("D");
        UnableToChargeFieldConfirmationRequestedPayload payload = new(
            id,
            broken == "chargerStationId" ? " " : Charger,
            broken == "observedCondition" ? "BATTERY_FULL" : "CONNECTION_FAILED",
            new WireToGateOperatorContextPayload(
                broken == "operatorId" ? string.Empty : Maintainer,
                broken == "verificationMethod" ? "PIN" : "SESSION",
                DateTimeOffset.UtcNow),
            broken == "observedAt" ? default : DateTimeOffset.UtcNow);

        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(
            () => harness.Session.ConfirmUnableToChargeAsync(id, payload, token));

        Assert.Equal("PROTOCOL_SCHEMA_INVALID", refused.Message);
        await AssertNothingIsSentAsync(harness, 0, token);
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

    /// <summary>The snapshot kinds the vehicle acknowledged after the first <paramref name="skip"/> received lines.</summary>
    private static string[] AcknowledgedKinds(FakeControlServer server, int skip) =>
    [
        .. server.ReceivedEnvelopes
            .Skip(skip)
            .Where(item => item.MessageType == "SnapshotAppliedAck")
            .Select(item =>
            {
                using JsonDocument document = JsonDocument.Parse(item.WireLine);
                return document.RootElement.GetProperty("payload").GetProperty("snapshotKind").GetString()!;
            })
    ];

    private static UnableToChargeOption Option(UnableToChargeDisplay display, string condition) =>
        Assert.Single(display.Options, item => item.Prompt.ObservedCondition == condition);

    private static WireToGateUnableToChargePrompt Prompt(string condition) => new(Charger, Maintainer, condition, null);

    /// <summary>A verified maintainer, with <c>wireToGate.unableToChargeEntryEnabled</c> on.</summary>
    private static WireToGateRecoveryOptions VerifiedMaintainer() =>
        new(true, ProofVariable, "MAINTENANCE_ADMINISTRATOR", "CONFIGURED_PROOF", UnableToChargeEntryEnabled: true);

    /// <summary>
    /// A vehicle the server says is charging at <see cref="Charger"/>, arrived, with a verified maintainer at it,
    /// unless the arguments say otherwise. The plan and the business state arrive after the handshake, both as fixed
    /// objects so a reconnect resends each revision as it was (the lesson of <c>8005-agv-onboard-hmi#221</c>).
    /// </summary>
    private static async Task<Harness> StartAsync(
        CancellationToken token,
        Action<FakeControlServer>? configure = null,
        IReadOnlyList<object>? legs = null,
        string purpose = Charging,
        string cycleState = "CHARGING",
        WireToGateRecoveryOptions? recoveryOptions = null,
        string? operatorIdEnvironmentVariable = null)
    {
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.VectorJourneySnapshotsAfterRecovery = ["UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, legs ?? [ChargerLeg(1, Charger, "ARRIVED")]),
                    ["VehicleBusinessStateSnapshot"] = BusinessState(1, purpose, cycleState)
                };
                configure?.Invoke(server);
            },
            token,
            recoveryOptions: recoveryOptions ?? VerifiedMaintainer(),
            operatorIdEnvironmentVariable: operatorIdEnvironmentVariable);
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
    /// satisfied it</b>; a timeout names both layers, the session and the logs.
    /// </summary>
    private static async Task<UnableToChargeDisplay> WaitForDisplayAsync(
        Harness harness,
        Func<UnableToChargeDisplay, bool> predicate,
        string expectation,
        CancellationToken token)
    {
        UnableToChargeDisplay display = UnableToChargeDisplay.Empty;
        try
        {
            await harness.WaitUntilAsync(
                () =>
                {
                    display = harness.ViewModel.UnableToCharge;
                    return predicate(display);
                },
                expectation,
                token);
        }
        catch (Xunit.Sdk.XunitException exception)
        {
            throw new Xunit.Sdk.XunitException(
                $"{exception.Message} View model: {Describe(display)}. Business service: {harness.Business.UnableToCharge}. "
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

    private static string Describe(UnableToChargeDisplay display) =>
        $"options=[{string.Join(",", display.Options.Select(item => item.Prompt))}], notice={display.NoticeText}, "
        + $"status={display.Status}, statusText={display.StatusText}";

    /// <summary>Waits for an operator record on the view model and returns the one that satisfied it.</summary>
    private static async Task<LogLineViewModel> WaitForLogAsync(
        Harness harness,
        Func<LogLineViewModel, bool> predicate,
        string expectation,
        CancellationToken token)
    {
        LogLineViewModel? found = null;
        await harness.WaitUntilAsync(
            () => (found = harness.ViewModel.Logs.LastOrDefault(predicate)) is not null,
            expectation,
            token);
        return found!;
    }

    private static Task<UnableToChargeDisplay> WaitForEntryAsync(Harness harness, CancellationToken token) =>
        WaitForDisplayAsync(
            harness, display => display.CanConfirm, "the unable-to-charge entry to be offered on the view model", token);

    private static Task<UnableToChargeDisplay> WaitForStatusAsync(Harness harness, string status, CancellationToken token) =>
        WaitForDisplayAsync(
            harness, display => display.Status == status, $"the result line on the view model to read {status}", token);

    /// <summary>
    /// The plan has reached the view model: its leg rows are written from the same snapshot the entry is judged on,
    /// so an entry that is going to appear has had its chance.
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
                Assert.False(harness.ViewModel.UnableToCharge.CanConfirm);
                Assert.False(harness.Business.CanConfirmUnableToCharge);
            },
            token);

    /// <summary>Exactly <paramref name="expected"/> requests are on the wire, and stay so over the window.</summary>
    private static Task AssertNothingIsSentAsync(
        Harness harness,
        int expected,
        CancellationToken token,
        TimeSpan? window = null) =>
        AssertWhileAsync(() => Assert.Equal(expected, Requests(harness).Length), token, window);

    /// <summary>
    /// Holds <paramref name="assertion"/> over a window. A claim that something does not happen, read once, passes on
    /// the version that had not got round to it yet.
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
    /// Nothing crosses the wire in either direction for 300 ms: what is counted after the press is the press's, and
    /// the entry is not about to be withdrawn by a session that is still settling.
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

    private static object BusinessState(long revision, string? activePurpose, string chargingCycleState) =>
        new
        {
            vehicleBusinessStateRevision = revision,
            readiness = "READY",
            activePurpose,
            manualChargingHold = false,
            batteryState = "MANDATORY_CHARGE",
            chargingCycleState,
            loadingPhase = (object?)null,
            blockingFacts = Array.Empty<object>(),
            observedAt = DateTimeOffset.UtcNow
        };

    private static object ChargerLeg(int sequence, string stationId, string state) =>
        Payloads.Leg(sequence, null, "CHARGER", null, stationId, state);

    private static object WaitingLeg(int sequence, string state) =>
        Payloads.Leg(sequence, null, "WAITING_POINT", null, WaitingPoint, state);
}
