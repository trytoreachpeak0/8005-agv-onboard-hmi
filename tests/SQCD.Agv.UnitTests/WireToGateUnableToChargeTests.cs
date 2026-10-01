using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The parts of the unable-to-charge field confirmation entry that need no session (batch 9-17,
/// <c>8005-agv-onboard-hmi#222</c>): which charger a confirmation is about, the words the operator reads, and three
/// structural facts about the entry's code.
/// </summary>
/// <remarks>
/// The session-level half -- what goes on the wire, the results, the resubmission and the wiring up to
/// <c>MainViewModel</c> -- is <c>UnableToChargeFieldConfirmationG2Tests</c>.
/// </remarks>
public sealed class WireToGateUnableToChargeTests
{
    private const string Charger = "CH-01";

    private const string RequestId = "99999999-9999-4999-8999-999999999999";

    /// <summary>
    /// The charger is the current leg's station when that leg is a charger, whatever the leg's state: a vehicle that
    /// cannot reach the charger is still on its way to it.
    /// </summary>
    [Theory]
    [InlineData("PLANNED")]
    [InlineData("ACTIVE")]
    [InlineData("ARRIVED")]
    public void TheChargerIsTheCurrentLegsStationWhenThatLegIsACharger(string chargerState)
    {
        WireToGateJourneySnapshot journey = Journey(
            Leg(1, "CHARGER", Charger, chargerState),
            Leg(2, "WAITING_POINT", "WP-01", "PLANNED"));

        Assert.Equal(Charger, WireToGateUnableToCharge.ResolveCharger(journey));
    }

    /// <summary>
    /// A current leg that is not a charger names no station, and none is taken from a completed charger leg or a
    /// later one; a station id is never read for what kind of station it is.
    /// </summary>
    [Fact]
    public void WithoutACurrentChargerLegNoStationIsNamed()
    {
        Assert.Null(WireToGateUnableToCharge.ResolveCharger(WireToGateJourneySnapshot.Empty));
        Assert.Null(WireToGateUnableToCharge.ResolveCharger(Journey()));
        Assert.Null(WireToGateUnableToCharge.ResolveCharger(
            Journey(Leg(1, "CHARGER", Charger, "COMPLETED"), Leg(2, "WAITING_POINT", "WP-01", "ACTIVE"))));
        Assert.Null(WireToGateUnableToCharge.ResolveCharger(
            Journey(Leg(1, "WAITING_POINT", Charger, "ACTIVE"), Leg(2, "CHARGER", "CH-02", "PLANNED"))));
        Assert.Null(WireToGateUnableToCharge.ResolveCharger(Journey(Leg(1, "CHARGER", Charger, "COMPLETED"))));
    }

    /// <summary>The purpose alone says whether the vehicle is on a charging claim; a charger leg does not.</summary>
    [Fact]
    public void OnlyTheChargingPurposeCounts()
    {
        Assert.True(WireToGateUnableToCharge.IsCharging(Journey(Leg(1, "CHARGER", Charger, "ARRIVED"))));
        Assert.False(WireToGateUnableToCharge.IsCharging(
            Journey("CLEARING_MAINTENANCE", Leg(1, "CHARGER", Charger, "ARRIVED"))));
        Assert.False(WireToGateUnableToCharge.IsCharging(Journey("TRANSPORT", Leg(1, "CHARGER", Charger, "ARRIVED"))));
        Assert.False(WireToGateUnableToCharge.IsCharging(WireToGateJourneySnapshot.Empty));
    }

    /// <summary>
    /// All four of the schema's observed conditions are offered, in its order, each with its own label and
    /// description. None is left out: which are enough to confirm is the server's to say.
    /// </summary>
    [Fact]
    public void EveryObservedConditionHasItsOwnWords()
    {
        Assert.Equal(
            ["CHARGER_UNREACHABLE", "CHARGER_OCCUPIED", "CONNECTION_FAILED", "CHARGER_FAULT"],
            WireToGateUnableToCharge.ObservedConditions);
        string[] labels = [.. WireToGateUnableToCharge.ObservedConditions.Select(WireToGateUnableToChargeText.ConditionLabel)];
        string[] descriptions =
            [.. WireToGateUnableToCharge.ObservedConditions.Select(WireToGateUnableToChargeText.ConditionDescription)];

        Assert.Equal(4, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, descriptions.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(labels, label => WireToGateUnableToCharge.ObservedConditions.Contains(label));
        Assert.DoesNotContain(
            descriptions, description => WireToGateUnableToCharge.ObservedConditions.Contains(description));
    }

    /// <summary>
    /// The dialog states everything the confirmation is bound to: the charger, the condition, who confirms, and --
    /// only when it is one -- that this press resubmits a confirmation whose result is unknown.
    /// </summary>
    /// <remarks>
    /// Each field of the prompt is varied alone and the text has to follow. A field the text did not show would be
    /// one the operator confirmed without having read it.
    /// </remarks>
    [Fact]
    public void TheDialogShowsEveryFieldTheConfirmationIsBoundTo()
    {
        WireToGateUnableToChargePrompt first = new(Charger, "maintainer-7", "CONNECTION_FAILED", null);
        string text = WireToGateUnableToChargeText.ConfirmationText(first);

        Assert.Contains(Charger, text, StringComparison.Ordinal);
        Assert.Contains("maintainer-7", text, StringComparison.Ordinal);
        Assert.Contains("接不上充电", text, StringComparison.Ordinal);
        Assert.Contains("CONNECTION_FAILED", text, StringComparison.Ordinal);
        Assert.Contains("不在本地改变充电", text, StringComparison.Ordinal);
        Assert.DoesNotContain("重新提交", text, StringComparison.Ordinal);

        Assert.NotEqual(text, WireToGateUnableToChargeText.ConfirmationText(first with { ChargerStationId = "CH-02" }));
        Assert.NotEqual(text, WireToGateUnableToChargeText.ConfirmationText(first with { OperatorId = "maintainer-8" }));
        Assert.NotEqual(
            text, WireToGateUnableToChargeText.ConfirmationText(first with { ObservedCondition = "CHARGER_FAULT" }));
        string resubmission = WireToGateUnableToChargeText.ConfirmationText(
            first with { ResubmittedConfirmationRequestId = RequestId });
        Assert.NotEqual(text, resubmission);
        Assert.Contains("重新提交", resubmission, StringComparison.Ordinal);
        Assert.Contains("结果未知", resubmission, StringComparison.Ordinal);
        Assert.Contains(RequestId, resubmission, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two absences explain themselves: a charging claim with no current charger to name, and an answer being
    /// awaited. An operator who is not a verified maintainer is not told about an entry that is not theirs.
    /// </summary>
    [Fact]
    public void OnlyAMissingChargerAndAnAwaitedAnswerAreExplained()
    {
        string missing = WireToGateUnableToChargeText.NoticeText(
            new([], WireToGateUnableToChargeUnavailability.StationUnknown, null));
        string awaiting = WireToGateUnableToChargeText.NoticeText(
            new([], WireToGateUnableToChargeUnavailability.AwaitingAnswer, null));

        Assert.Contains("当前腿不是充电桩", missing, StringComparison.Ordinal);
        Assert.Contains("不可用", missing, StringComparison.Ordinal);
        Assert.Contains("等待服务端应答", awaiting, StringComparison.Ordinal);
        Assert.Equal(string.Empty, WireToGateUnableToChargeText.NoticeText(WireToGateUnableToChargeView.NotCharging));
        Assert.Equal(
            string.Empty,
            WireToGateUnableToChargeText.NoticeText(new([], WireToGateUnableToChargeUnavailability.NoVerifiedMaintainer, null)));
        Assert.Equal(
            string.Empty,
            WireToGateUnableToChargeText.NoticeText(
                new([new(Charger, "maintainer-7", "CHARGER_FAULT", null)], WireToGateUnableToChargeUnavailability.None, null)));
    }

    /// <summary>
    /// The result line: confirmed with the server's decision in words or none, rejected with the reason code as sent
    /// and the decision beside it, unknown with the sentence the ticket fixes, and not accepted as its own line.
    /// </summary>
    [Theory]
    [InlineData("RETRY_LATER", "稍后重试")]
    [InlineData("MANUAL_CHARGING_HOLD", "转人工充电")]
    [InlineData("REASSIGN_CHARGER", "改派其它充电桩")]
    public void TheResultLineSaysWhatTheServerSaidAndDecided(string decision, string decisionText)
    {
        WireToGateUnableToChargeOutcome confirmed = Outcome(WireToGateUnableToChargeOutcomeKind.Confirmed, decision, null);
        WireToGateUnableToChargeOutcome rejected = Outcome(
            WireToGateUnableToChargeOutcomeKind.Rejected, decision, "ACTION_NOT_ALLOWED_IN_STATE");

        Assert.Equal(WireToGateUnableToChargeText.ConfirmedStatus, WireToGateUnableToChargeText.Status(confirmed));
        Assert.Equal(WireToGateUnableToChargeText.RejectedStatus, WireToGateUnableToChargeText.Status(rejected));
        Assert.Equal(decisionText, WireToGateUnableToChargeText.DecisionText(decision));
        Assert.Contains("服务端已记录现场确认", WireToGateUnableToChargeText.StatusText(confirmed), StringComparison.Ordinal);
        Assert.Contains($"服务端决定：{decisionText}", WireToGateUnableToChargeText.StatusText(confirmed), StringComparison.Ordinal);
        Assert.Contains(Charger, WireToGateUnableToChargeText.StatusText(confirmed), StringComparison.Ordinal);
        Assert.Contains("ACTION_NOT_ALLOWED_IN_STATE", WireToGateUnableToChargeText.StatusText(rejected), StringComparison.Ordinal);
        Assert.Contains($"服务端决定：{decisionText}", WireToGateUnableToChargeText.StatusText(rejected), StringComparison.Ordinal);
    }

    [Fact]
    public void TheResultLineShowsNoDecisionWhenThereIsNoneAndKeepsUnknownAndNotAcceptedApart()
    {
        WireToGateUnableToChargeOutcome confirmed = Outcome(WireToGateUnableToChargeOutcomeKind.Confirmed, null, null);
        WireToGateUnableToChargeOutcome unknown = Outcome(WireToGateUnableToChargeOutcomeKind.Unknown, null, null);
        WireToGateUnableToChargeOutcome notAccepted = Outcome(
            WireToGateUnableToChargeOutcomeKind.NotAccepted, null, "BUSINESS_ID_CONTENT_CONFLICT");

        Assert.Equal(string.Empty, WireToGateUnableToChargeText.DecisionText(null));
        Assert.DoesNotContain("服务端决定", WireToGateUnableToChargeText.StatusText(confirmed), StringComparison.Ordinal);
        Assert.Equal(WireToGateUnableToChargeText.UnknownStatus, WireToGateUnableToChargeText.Status(unknown));
        Assert.Equal(WireToGateUnableToChargeText.NotAcceptedStatus, WireToGateUnableToChargeText.Status(notAccepted));
        Assert.Equal(string.Empty, WireToGateUnableToChargeText.Status(null));
        Assert.Equal(string.Empty, WireToGateUnableToChargeText.StatusText(null));
        Assert.Contains(
            "结果未知，请查看车辆状态后再决定是否重新提交", WireToGateUnableToChargeText.StatusText(unknown), StringComparison.Ordinal);
        Assert.Contains("服务端没有受理", WireToGateUnableToChargeText.StatusText(notAccepted), StringComparison.Ordinal);
        Assert.Contains("BUSINESS_ID_CONTENT_CONFLICT", WireToGateUnableToChargeText.StatusText(notAccepted), StringComparison.Ordinal);
        Assert.DoesNotContain("结果未知", WireToGateUnableToChargeText.StatusText(notAccepted), StringComparison.Ordinal);
    }

    /// <summary>
    /// The entry's business code reaches no door, no IO and no recovery state, and none of the vehicle's own charging
    /// or loading state -- by construction, not by the fatal-fault latch.
    /// </summary>
    /// <remarks>
    /// This is what lets the entry stay outside the nine recovery entries <see cref="RecoveryEntryWriteSiteArchitectureTests"/>
    /// guards and open while a latch stands, as the station clearance does; and it is the structural half of
    /// <c>NEVER_DECIDE_CHARGING_POLICY_LOCALLY</c>. A name scan, so it sees a member named here and not one reached
    /// through a helper elsewhere; the behavioural half is
    /// <c>UnableToChargeFieldConfirmationG2Tests.TheServersDecisionChangesNothingOnTheVehicleUntilItsNextBusinessState</c>.
    /// </remarks>
    [Fact]
    public void TheEntrysBusinessCodeNamesNoDoorNoIoAndNoRecoveryState()
    {
        string source = File.ReadAllText(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(),
            "src",
            "SQCD.Agv.Wpf",
            "WireToGateBusinessService.UnableToCharge.cs"));

        Assert.Contains("ConfirmUnableToChargeAsync", source, StringComparison.Ordinal);
        foreach (string forbidden in new[]
        {
            "_ioModule",
            "_executor",
            "_vectorExecutor",
            "PulseUnlock",
            "UpdateRecoveryStateAsync",
            "_lastRecoveryState",
            "_currentEntryRequest",
            "ApplyJourney",
            "ManualChargingHold"
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <c>App.xaml.cs</c> wires the entry through <c>UnableToChargeWiring.Configure</c>, the method the G2 harness
    /// calls -- so the seam test drives the product's own lines, not a copy of them.
    /// </summary>
    [Fact]
    public void TheAppWiresTheEntryThroughTheMethodTheSeamTestDrives()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string app = File.ReadAllText(Path.Combine(root, "src", "SQCD.Agv.Wpf", "App.xaml.cs"));
        string harness = File.ReadAllText(Path.Combine(
            root, "tests", "SQCD.Agv.WireToGateG2Tests", "MultiDemandJourneyG2Tests.cs"));

        Assert.Contains("UnableToChargeWiring.Configure(viewModel, _wireToGateBusiness);", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureUnableToCharge(", app, StringComparison.Ordinal);
        Assert.Contains("UnableToChargeWiring.Configure(viewModel, business);", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureUnableToCharge(", harness, StringComparison.Ordinal);
    }

    /// <summary>
    /// The window's click handler builds the dialog from the pressed button's own option and hands back that option's
    /// prompt; it never reads the view model's entry.
    /// </summary>
    /// <remarks>
    /// The dialog is a modal <c>MessageBox</c>, which no test can drive, and the view model goes on refreshing while it
    /// is open. A handler that read the entry again would show one charger or condition and confirm another with every
    /// G2 test still green, because those tests start at the view model.
    /// </remarks>
    [Fact]
    public void TheClickHandlerConfirmsThePromptOfThePressedButton()
    {
        string window = File.ReadAllText(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(), "src", "SQCD.Agv.Wpf", "MainWindow.xaml.cs"));
        int start = window.IndexOf("void OnConfirmUnableToChargeClick(", StringComparison.Ordinal);
        Assert.True(start >= 0, "The click handler is gone or renamed.");
        int end = window.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string handler = window[start..end];

        Assert.DoesNotContain(".UnableToCharge", handler, StringComparison.Ordinal);
        Assert.Contains(
            "(sender as FrameworkElement)?.DataContext is not UnableToChargeOption { Prompt: var prompt, ConfirmationText: var confirmationText }",
            handler,
            StringComparison.Ordinal);
        Assert.Contains("MessageBox.Show(\n                confirmationText,", handler, StringComparison.Ordinal);
        Assert.Contains("_viewModel.ConfirmUnableToChargeAsync(prompt)", handler, StringComparison.Ordinal);
    }

    private static WireToGateUnableToChargeOutcome Outcome(
        WireToGateUnableToChargeOutcomeKind kind,
        string? decision,
        string? reasonCode) =>
        new(kind, RequestId, Charger, "CONNECTION_FAILED", decision, reasonCode);

    private static WireToGateJourneySnapshot Journey(params WireToGateMovementLeg[] legs) => Journey("CHARGING", legs);

    private static WireToGateJourneySnapshot Journey(string purpose, params WireToGateMovementLeg[] legs) =>
        new(
            new WireToGateVehicleBusinessState(
                1,
                "READY",
                purpose,
                false,
                "MANDATORY_CHARGE",
                "CHARGING",
                null,
                [],
                DateTimeOffset.UnixEpoch,
                new string('a', 64)),
            null,
            new WireToGateUpcomingStopPlan(1, legs, new string('b', 64)),
            DateTimeOffset.UnixEpoch);

    private static WireToGateMovementLeg Leg(int sequence, string category, string stationId, string state) =>
        new(
            $"22222222-2222-4222-8222-{sequence:D12}",
            null,
            category,
            null,
            null,
            sequence,
            stationId,
            "MAP-26",
            state);
}
