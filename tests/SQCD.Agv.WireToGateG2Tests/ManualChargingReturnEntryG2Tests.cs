using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;
using Harness = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Harness;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 「充电后返回服务」 from the screen, while the server holds the vehicle for manual charging
/// (<c>trytoreachpeak0/8005-agv-onboard-hmi#293</c>): with the factory setting the operator reads why it cannot be
/// pressed and who to call; with a verified maintainer the button is there and a press puts the request on the wire.
/// </summary>
/// <remarks>
/// <para>
/// The field case, 2026-10-10 on agv02: the roster emptied, the server held the vehicle
/// (<c>ROSTER_EMPTY</c>), the roster came back and the server still allocated nothing
/// (<c>CHARGING_MANUAL_HOLD_ACTIVE</c>) -- by design, only this request lifts the hold. The screen said
/// 「需人工充电：服务端保持」, and with <c>recoveryResumeEnabled=false</c> the button was collapsed and nothing else
/// was shown. The vehicle was freed only by opening the recovery window on it by hand.
/// </para>
/// <para>
/// <b>Every test reads and presses on <see cref="MainViewModel"/></b>, wired by <c>ManualChargingReturnWiring</c>,
/// the method <c>App.xaml.cs</c> calls; the press is <see cref="MainViewModel.RequestManualChargingReturnAsync"/>,
/// which is what the button's click handler calls once the operator confirms the dialog. A line on the screen whose
/// button is dead is worse than none, so the shown exit is pressed, not just seen.
/// </para>
/// <para>
/// The guidance is read-only and changes no condition of the button. That matters more here than for the recovery
/// entries: this message carries no proof, and the control server checks only the role it names
/// (<c>WireToGateStore.DecideManualChargingReturnToServiceAsync</c>), so the vehicle's local checks are the whole
/// boundary. The unauthorized tests therefore also press, and hold that nothing is sent.
/// </para>
/// <para>
/// Which sentence wins when several causes hold is <c>WireToGateManualChargingReturnTextTests</c>; the view-model
/// combinations the harness cannot reach (an offline session with a hold still projected) are
/// <c>ManualChargingReturnGuidanceViewModelTests</c>.
/// </para>
/// </remarks>
public sealed class ManualChargingReturnEntryG2Tests
{
    private const string ProofVariable = "W2G_G2_MANUAL_RETURN_ENTRY_PROOF";

    private const string RequestType = "ManualChargingReturnToServiceRequested";

    private const string NotAuthorized =
        "充电完成后需维护管理员在本机开启返回服务权限，再按「充电后返回服务」。请联系系统维护人员。";

    private const string Latched = "安全故障解除前不能申请返回服务，请联系系统维护人员。";

    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 10, 12, 18, 0, TimeSpan.Zero);

    private static readonly TimeSpan HoldWindow = TimeSpan.FromMilliseconds(500);

    static ManualChargingReturnEntryG2Tests()
    {
        // The harness's credential and operator variables are set by MultiDemandJourneyG2Tests' static constructor.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(MultiDemandJourneyG2Tests).TypeHandle);
        Environment.SetEnvironmentVariable(ProofVariable, "g2-manual-return-entry-proof");
    }

    /// <summary>
    /// The field case: factory setting, hold published. No button; the line says who can return the vehicle; the
    /// business service names the cause; and a press made anyway sends nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
    public async Task WithTheFactorySettingAHeldVehicleSaysWhoCanReturnItAndSendsNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartHeldAsync(recoveryOptions: null, token);

        await WaitForGuidanceAsync(harness, NotAuthorized, token);
        Assert.True(harness.ViewModel.HasManualChargingReturnGuidance);
        Assert.False(harness.ViewModel.CanRequestManualChargingReturn);
        Assert.Equal(
            WireToGateManualChargingReturnBlocker.OperatorNotAuthorized,
            harness.Business.ManualChargingReturnBlocker);

        Assert.False(await harness.ViewModel.RequestManualChargingReturnAsync(token));
        await AssertWhileAsync(() => Assert.Empty(Requests(harness)), token);
        Assert.Equal(NotAuthorized, harness.ViewModel.ManualChargingReturnGuidance);
    }

    /// <summary>
    /// The maintenance switch on but no proof configured is the same answer: the switch alone is not a maintainer.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
    public async Task TheSwitchWithoutAProofIsStillNotAMaintainer()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartHeldAsync(
            new WireToGateRecoveryOptions(
                true, "W2G_G2_MANUAL_RETURN_ENTRY_PROOF_UNSET", "MAINTENANCE_ADMINISTRATOR", "CONFIGURED_PROOF"),
            token);

        await WaitForGuidanceAsync(harness, NotAuthorized, token);
        Assert.False(harness.ViewModel.CanRequestManualChargingReturn);
        Assert.False(await harness.ViewModel.RequestManualChargingReturnAsync(token));
        await AssertWhileAsync(() => Assert.Empty(Requests(harness)), token);
    }

    /// <summary>
    /// A verified maintainer: the button is offered, no line is shown beside it, and pressing it puts exactly one
    /// <c>ManualChargingReturnToServiceRequested</c> on the wire, naming the administrator. The server lifting the
    /// hold afterwards leaves nothing on the screen.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
    public async Task WithAVerifiedMaintainerTheButtonIsPressedAndTheRequestGoesOut()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartHeldAsync(VerifiedMaintainer(), token);

        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanRequestManualChargingReturn,
            "the button for a verified maintainer",
            token);
        Assert.Equal(string.Empty, harness.ViewModel.ManualChargingReturnGuidance);
        Assert.False(harness.ViewModel.HasManualChargingReturnGuidance);
        Assert.Equal(WireToGateManualChargingReturnBlocker.None, harness.Business.ManualChargingReturnBlocker);

        Assert.True(await harness.ViewModel.RequestManualChargingReturnAsync(token));
        JsonElement request = Assert.Single(Requests(harness));
        Assert.Equal(
            "operator-134",
            request.GetProperty("payload").GetProperty("administrator").GetProperty("operatorId").GetString());
        Assert.Equal("MAINTENANCE_ADMINISTRATOR", request.GetProperty("payload").GetProperty("administratorRole").GetString());

        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", BusinessState(2, hold: false));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.VehicleBusinessState?.ManualChargingHold == false,
            "the server to lift the hold",
            token);
        await AssertWhileAsync(
            () => Assert.Equal(string.Empty, harness.ViewModel.ManualChargingReturnGuidance),
            token);
    }

    /// <summary>
    /// The server refusing, then not answering at all: the hold stays the server's, the button stays, no line
    /// replaces it, and each press is its own request.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
    public async Task ARefusedOrUnansweredPressLeavesTheButtonAndTheHold()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartHeldAsync(
            VerifiedMaintainer(),
            token,
            server => server.ManualChargingReturnToServiceOutcome = "REJECTED");
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanRequestManualChargingReturn,
            "the button for a verified maintainer",
            token);

        Assert.False(await harness.ViewModel.RequestManualChargingReturnAsync(token));
        harness.Server.RespondToManualChargingReturnToServiceRequests = false;
        Assert.False(await harness.ViewModel.RequestManualChargingReturnAsync(token));

        JsonElement[] requests = Requests(harness);
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(
            requests[0].GetProperty("payload").GetProperty("requestId").GetString(),
            requests[1].GetProperty("payload").GetProperty("requestId").GetString());
        Assert.True(harness.Session.CurrentJourney.VehicleBusinessState!.ManualChargingHold);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanRequestManualChargingReturn,
            "the button to stay after the unanswered press",
            token);
        Assert.Equal(string.Empty, harness.ViewModel.ManualChargingReturnGuidance);
    }

    /// <summary>
    /// A fatal-fault latch shuts the button for a verified maintainer too, and the line then says the latch, not
    /// the maintainer -- on both refresh paths.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
    public async Task ALatchShutsTheButtonAndTheLineSaysTheLatch()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartHeldAsync(VerifiedMaintainer(), token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanRequestManualChargingReturn,
            "the button for a verified maintainer",
            token);

        // The controller's state change runs the presentation path.
        harness.Controller.EnterFatalFault("UI_COMMAND_FAILED", OnboardFatalFaultBanner.UiCommandFailed);
        await WaitForGuidanceAsync(harness, Latched, token);
        Assert.False(harness.ViewModel.CanRequestManualChargingReturn);

        // The other path: what a resent entry request runs.
        harness.ViewModel.RefreshWireToGateInputState();
        Assert.Equal(Latched, harness.ViewModel.ManualChargingReturnGuidance);
        Assert.False(harness.ViewModel.CanRequestManualChargingReturn);
    }

    /// <summary>
    /// The latch on a factory-set vehicle: two causes at once, and the latch is the one named.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
    public async Task ALatchOnAFactorySetVehicleIsTheCauseNamed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartHeldAsync(recoveryOptions: null, token);
        await WaitForGuidanceAsync(harness, NotAuthorized, token);

        harness.Controller.EnterFatalFault("UI_COMMAND_FAILED", OnboardFatalFaultBanner.UiCommandFailed);
        await WaitForGuidanceAsync(harness, Latched, token);
    }

    /// <summary>
    /// The vehicle process restarting while held: the new process shows the line again once the hold is back, and
    /// still sends nothing when pressed.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("IntegrationSlice", "FP-IS-13")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
    public async Task AfterARestartTheHeldVehicleSaysWhoCanReturnItAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness first = await StartHeldAsync(recoveryOptions: null, token);
        await WaitForGuidanceAsync(first, NotAuthorized, token);

        await first.StopVehicleAsync();
        first.Server.SimulateOnboardProcessRestart();
        await using Harness second = await Harness.StartAgainstAsync(first.Server, token, first.JournalPath);

        await WaitForGuidanceAsync(second, NotAuthorized, token);
        Assert.False(second.ViewModel.CanRequestManualChargingReturn);
        Assert.False(await second.ViewModel.RequestManualChargingReturnAsync(token));
        await AssertWhileAsync(() => Assert.Empty(Requests(second)), token);
    }

    private static WireToGateRecoveryOptions VerifiedMaintainer() =>
        new(true, ProofVariable, "MAINTENANCE_ADMINISTRATOR", "CONFIGURED_PROOF");

    /// <summary>
    /// A vehicle the server holds for manual charging, answered requests, and the given recovery options -- null is
    /// the factory setting. The business state is a fixed object so a reconnect resends the same revision as it was.
    /// </summary>
    private static async Task<Harness> StartHeldAsync(
        WireToGateRecoveryOptions? recoveryOptions,
        CancellationToken token,
        Action<FakeControlServer>? configure = null)
    {
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.RespondToManualChargingReturnToServiceRequests = true;
                server.VectorJourneySnapshotsAfterRecovery = ["VehicleBusinessStateSnapshot"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = BusinessState(1, hold: true)
                };
                configure?.Invoke(server);
            },
            token,
            recoveryOptions: recoveryOptions);
        try
        {
            await harness.WaitUntilAsync(
                () => harness.Session.CurrentJourney.VehicleBusinessState?.ManualChargingHold == true,
                "the hold",
                token);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    private static object BusinessState(long revision, bool hold) =>
        new
        {
            vehicleBusinessStateRevision = revision,
            readiness = "READY",
            activePurpose = (string?)null,
            manualChargingHold = hold,
            batteryState = "LOW",
            chargingCycleState = "NOT_CHARGING",
            loadingPhase = (object?)null,
            blockingFacts = Array.Empty<object>(),
            observedAt = ObservedAt
        };

    private static async Task WaitForGuidanceAsync(Harness harness, string expected, CancellationToken token)
    {
        string shown = string.Empty;
        try
        {
            await harness.WaitUntilAsync(
                () =>
                {
                    shown = harness.ViewModel.ManualChargingReturnGuidance;
                    return shown == expected;
                },
                $"the line 「{expected}」",
                token);
        }
        catch (Xunit.Sdk.XunitException exception)
        {
            throw new Xunit.Sdk.XunitException(
                $"{exception.Message} Shown: 「{shown}」; blocker: {harness.Business.ManualChargingReturnBlocker}; "
                + $"button: {harness.ViewModel.CanRequestManualChargingReturn}.");
        }
    }

    private static JsonElement[] Requests(Harness harness) =>
    [
        .. harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == RequestType)
            .Select(envelope =>
            {
                using JsonDocument document = JsonDocument.Parse(envelope.WireLine);
                return document.RootElement.Clone();
            })
    ];

    /// <summary>A claim that something does not happen, held over a window rather than read once.</summary>
    private static async Task AssertWhileAsync(Action assertion, CancellationToken token)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + HoldWindow;
        do
        {
            assertion();
            await Task.Delay(25, token);
        }
        while (DateTimeOffset.UtcNow < until);
    }
}
