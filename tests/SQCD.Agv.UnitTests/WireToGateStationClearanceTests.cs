using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The parts of the manual station clearance entry that need no session (batch 9-16,
/// <c>8005-agv-onboard-hmi#221</c>): which charger a clearance is about, the words the operator reads, and two
/// structural facts about the entry's code.
/// </summary>
/// <remarks>
/// The session-level half -- what goes on the wire, the three results, the resubmission and the wiring up to
/// <c>MainViewModel</c> -- is <c>ManualStationClearanceG2Tests</c>.
/// </remarks>
public sealed class WireToGateStationClearanceTests
{
    private const string Charger = "CH-01";

    private const string RequestId = "99999999-9999-4999-8999-999999999999";

    /// <summary>
    /// The charger is the plan's <c>CHARGER</c> leg, whatever its state: a clearance starts with the vehicle
    /// still on it and goes on after the leg is <c>COMPLETED</c> and a waiting-point leg has taken over.
    /// </summary>
    [Theory]
    [InlineData("ARRIVED")]
    [InlineData("ACTIVE")]
    [InlineData("COMPLETED")]
    public void TheChargerIsThePlansChargerLegWhateverItsState(string chargerState)
    {
        WireToGateJourneySnapshot journey = Journey(
            Leg(1, "CHARGER", Charger, chargerState),
            Leg(2, "WAITING_POINT", "WP-01", "PLANNED"));

        Assert.Equal(Charger, WireToGateStationClearanceStation.Resolve(journey));
    }

    /// <summary>
    /// No charger leg, no plan at all, or two chargers: there is no one station to name, and none is guessed.
    /// </summary>
    /// <remarks>
    /// The waiting point in the second case is named like a charger on purpose. A station id is never read for
    /// what kind of station it is; only the leg's <c>stopPurposeCategory</c> says so.
    /// </remarks>
    [Fact]
    public void WithoutExactlyOneChargerInThePlanNoStationIsNamed()
    {
        Assert.Null(WireToGateStationClearanceStation.Resolve(WireToGateJourneySnapshot.Empty));
        Assert.Null(WireToGateStationClearanceStation.Resolve(Journey()));
        Assert.Null(WireToGateStationClearanceStation.Resolve(
            Journey(Leg(1, "WAITING_POINT", Charger, "ACTIVE"), Leg(2, "BUSINESS", "ST-01", "PLANNED"))));
        Assert.Null(WireToGateStationClearanceStation.Resolve(
            Journey(Leg(1, "CHARGER", Charger, "COMPLETED"), Leg(2, "CHARGER", "CH-02", "PLANNED"))));
    }

    /// <summary>Two charger legs at the same station are one charger.</summary>
    [Fact]
    public void TwoChargerLegsAtOneStationNameThatStation()
    {
        WireToGateJourneySnapshot journey = Journey(
            Leg(1, "CHARGER", Charger, "COMPLETED"),
            Leg(2, "CHARGER", Charger, "PLANNED"));

        Assert.Equal(Charger, WireToGateStationClearanceStation.Resolve(journey));
    }

    /// <summary>
    /// The dialog states everything the confirmation is bound to: the charger, who confirms, and -- only when it
    /// is one -- that this press resubmits a confirmation whose result is unknown.
    /// </summary>
    /// <remarks>
    /// Each field of the prompt is varied alone and the text has to follow. A field the text did not show would
    /// be one the operator confirmed without having read it.
    /// </remarks>
    [Fact]
    public void TheDialogShowsEveryFieldTheConfirmationIsBoundTo()
    {
        WireToGateStationClearancePrompt first = new(Charger, "maintainer-7", null);
        string text = WireToGateStationClearanceText.ConfirmationText(first);

        Assert.Contains(Charger, text, StringComparison.Ordinal);
        Assert.Contains("maintainer-7", text, StringComparison.Ordinal);
        Assert.Contains("已腾空", text, StringComparison.Ordinal);
        Assert.Contains("安全位置", text, StringComparison.Ordinal);
        Assert.DoesNotContain("重新提交", text, StringComparison.Ordinal);

        Assert.NotEqual(text, WireToGateStationClearanceText.ConfirmationText(first with { StationId = "CH-02" }));
        Assert.NotEqual(text, WireToGateStationClearanceText.ConfirmationText(first with { OperatorId = "maintainer-8" }));
        string resubmission = WireToGateStationClearanceText.ConfirmationText(
            first with { ResubmittedConfirmationRequestId = RequestId });
        Assert.NotEqual(text, resubmission);
        Assert.Contains("重新提交", resubmission, StringComparison.Ordinal);
        Assert.Contains("结果未知", resubmission, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two absences explain themselves: a clearance with no charger to name, and an answer being awaited. An
    /// operator who is not a verified maintainer is not told about an entry that is not theirs.
    /// </summary>
    [Fact]
    public void OnlyAMissingChargerAndAnAwaitedAnswerAreExplained()
    {
        string missing = WireToGateStationClearanceText.NoticeText(
            new(null, WireToGateStationClearanceUnavailability.StationUnknown, null));
        string awaiting = WireToGateStationClearanceText.NoticeText(
            new(null, WireToGateStationClearanceUnavailability.AwaitingAnswer, null));

        Assert.Contains("清桩", missing, StringComparison.Ordinal);
        Assert.Contains("充电桩", missing, StringComparison.Ordinal);
        Assert.Contains("不可用", missing, StringComparison.Ordinal);
        Assert.Contains("等待服务端应答", awaiting, StringComparison.Ordinal);
        Assert.Equal(string.Empty, WireToGateStationClearanceText.NoticeText(WireToGateStationClearanceView.NotClearing));
        Assert.Equal(
            string.Empty,
            WireToGateStationClearanceText.NoticeText(
                new(null, WireToGateStationClearanceUnavailability.NoVerifiedMaintainer, null)));
        Assert.Equal(
            string.Empty,
            WireToGateStationClearanceText.NoticeText(
                new(new(Charger, "maintainer-7", null), WireToGateStationClearanceUnavailability.None, null)));
    }

    /// <summary>
    /// The result line: confirmed with the server's <c>stationReleased</c> either way, rejected with the reason
    /// code as sent, unknown with the sentence the ticket fixes, and not accepted -- the server's
    /// <c>ProtocolProblem</c> to the request -- as its own line, never as unknown.
    /// </summary>
    [Fact]
    public void TheResultLineSaysWhatTheServerSaid()
    {
        WireToGateStationClearanceOutcome released = Outcome(WireToGateStationClearanceOutcomeKind.Confirmed, true, null);
        WireToGateStationClearanceOutcome held = Outcome(WireToGateStationClearanceOutcomeKind.Confirmed, false, null);
        WireToGateStationClearanceOutcome rejected = Outcome(
            WireToGateStationClearanceOutcomeKind.Rejected, false, "ACTION_NOT_ALLOWED_IN_STATE");
        WireToGateStationClearanceOutcome unknown = Outcome(WireToGateStationClearanceOutcomeKind.Unknown, false, null);
        WireToGateStationClearanceOutcome notAccepted = Outcome(
            WireToGateStationClearanceOutcomeKind.NotAccepted, false, "BUSINESS_ID_CONTENT_CONFLICT");

        Assert.Equal(WireToGateStationClearanceText.ConfirmedReleasedStatus, WireToGateStationClearanceText.Status(released));
        Assert.Equal(WireToGateStationClearanceText.ConfirmedNotReleasedStatus, WireToGateStationClearanceText.Status(held));
        Assert.Equal(WireToGateStationClearanceText.RejectedStatus, WireToGateStationClearanceText.Status(rejected));
        Assert.Equal(WireToGateStationClearanceText.UnknownStatus, WireToGateStationClearanceText.Status(unknown));
        Assert.Equal(WireToGateStationClearanceText.NotAcceptedStatus, WireToGateStationClearanceText.Status(notAccepted));
        Assert.Equal(string.Empty, WireToGateStationClearanceText.Status(null));
        Assert.Equal(string.Empty, WireToGateStationClearanceText.StatusText(null));

        Assert.Contains("服务端已确认清桩", WireToGateStationClearanceText.StatusText(released), StringComparison.Ordinal);
        Assert.Contains("站点已释放", WireToGateStationClearanceText.StatusText(released), StringComparison.Ordinal);
        Assert.Contains("服务端已确认清桩", WireToGateStationClearanceText.StatusText(held), StringComparison.Ordinal);
        Assert.Contains("站点尚未释放", WireToGateStationClearanceText.StatusText(held), StringComparison.Ordinal);
        Assert.Contains(Charger, WireToGateStationClearanceText.StatusText(released), StringComparison.Ordinal);
        Assert.Contains("ACTION_NOT_ALLOWED_IN_STATE", WireToGateStationClearanceText.StatusText(rejected), StringComparison.Ordinal);
        Assert.Contains("服务端没有受理", WireToGateStationClearanceText.StatusText(notAccepted), StringComparison.Ordinal);
        Assert.Contains("BUSINESS_ID_CONTENT_CONFLICT", WireToGateStationClearanceText.StatusText(notAccepted), StringComparison.Ordinal);
        Assert.DoesNotContain("结果未知", WireToGateStationClearanceText.StatusText(notAccepted), StringComparison.Ordinal);
        Assert.Contains(
            "结果未知，请查看车辆状态后再决定是否重新提交",
            WireToGateStationClearanceText.StatusText(unknown),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A press whose clearance the server ended while it waited cannot be resubmitted -- the entry is gone -- so its
    /// unknown and not-accepted lines say the clearance is over and never ask for a resubmission
    /// (8005-agv-onboard-hmi#222 incremental review, item 1).
    /// </summary>
    [Fact]
    public void AnEndedClearanceIsSaidAndNoResubmissionIsAskedFor()
    {
        string unknown = WireToGateStationClearanceText.StatusText(
            Outcome(WireToGateStationClearanceOutcomeKind.Unknown, false, null) with { ClearanceEnded = true });
        string notAccepted = WireToGateStationClearanceText.StatusText(
            Outcome(WireToGateStationClearanceOutcomeKind.NotAccepted, false, "BUSINESS_ID_CONTENT_CONFLICT")
                with { ClearanceEnded = true });

        Assert.Contains("服务端已结束这次清桩，入口已关闭", unknown, StringComparison.Ordinal);
        Assert.Contains("结果未知", unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("重新提交", unknown, StringComparison.Ordinal);
        Assert.Contains(WireToGateStationClearanceText.ClearanceEndedSentence, notAccepted, StringComparison.Ordinal);
        Assert.Contains("BUSINESS_ID_CONTENT_CONFLICT", notAccepted, StringComparison.Ordinal);
        Assert.DoesNotContain("再次提交", notAccepted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The entry's business code reaches no door, no IO and no recovery state -- by construction, not by the
    /// fatal-fault latch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what lets the entry stay outside the nine recovery entries
    /// <see cref="RecoveryEntryWriteSiteArchitectureTests"/> guards, and stay open while a latch stands: those
    /// nine are shut by the view model because three of them open doors through an executor the latch cannot
    /// stop. This one only sends a statement about a charger and shows the answer
    /// (<c>NEVER_RELEASE_STATION_LOCALLY</c>). The day this file names the IO module or an executor, that
    /// reasoning is gone and the entry belongs behind <c>AllowRecoveryEntry</c>.
    /// </para>
    /// <para>
    /// A name scan, so it sees a member named here and not one reached through a helper elsewhere; the
    /// behavioural half is <c>ManualStationClearanceG2Tests.ALatchLeavesTheClearanceEntryOpenAndOpensNoDoor</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheEntrysBusinessCodeNamesNoDoorNoIoAndNoRecoveryState()
    {
        string source = File.ReadAllText(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(),
            "src",
            "SQCD.Agv.Wpf",
            "WireToGateBusinessService.StationClearance.cs"));

        Assert.Contains("ConfirmManualStationClearanceAsync", source, StringComparison.Ordinal);
        foreach (string forbidden in new[]
        {
            "_ioModule",
            "_executor",
            "_vectorExecutor",
            "PulseUnlock",
            "UpdateRecoveryStateAsync",
            "_lastRecoveryState"
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <c>App.xaml.cs</c> wires the entry through <c>StationClearanceWiring.Configure</c>, the method the G2
    /// harness calls -- so the seam test drives the product's own lines, not a copy of them.
    /// </summary>
    [Fact]
    public void TheAppWiresTheEntryThroughTheMethodTheSeamTestDrives()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string app = File.ReadAllText(Path.Combine(root, "src", "SQCD.Agv.Wpf", "App.xaml.cs"));
        string harness = File.ReadAllText(Path.Combine(
            root, "tests", "SQCD.Agv.WireToGateG2Tests", "MultiDemandJourneyG2Tests.cs"));

        Assert.Contains("StationClearanceWiring.Configure(viewModel, _wireToGateBusiness);", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureStationClearance(", app, StringComparison.Ordinal);
        Assert.Contains("StationClearanceWiring.Configure(viewModel, business);", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureStationClearance(", harness, StringComparison.Ordinal);
    }

    /// <summary>
    /// The window's click handler reads the view model's entry once, before the dialog, and hands back the prompt
    /// from that one reading.
    /// </summary>
    /// <remarks>
    /// The dialog is a modal <c>MessageBox</c>, which no test can drive, and the view model goes on refreshing
    /// while it is open. A handler that read the entry again after the dialog would show one charger and confirm
    /// another with every G2 test still green, because those tests start at the view model. So the shape is held
    /// here: one reading, and the prompt passed on is the one taken from it.
    /// </remarks>
    [Fact]
    public void TheClickHandlerConfirmsThePromptItBuiltTheDialogFrom()
    {
        string window = File.ReadAllText(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(), "src", "SQCD.Agv.Wpf", "MainWindow.xaml.cs"));
        int start = window.IndexOf("void OnConfirmStationClearanceClick(", StringComparison.Ordinal);
        Assert.True(start >= 0, "The click handler is gone or renamed.");
        int end = window.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string handler = window[start..end];

        Assert.Equal(1, CountOf(handler, ".StationClearance"));
        Assert.Contains(
            "_viewModel?.StationClearance is not { Prompt: { } prompt, ConfirmationText: var confirmationText }",
            handler,
            StringComparison.Ordinal);
        Assert.Contains("MessageBox.Show(\n                confirmationText,", handler, StringComparison.Ordinal);
        Assert.Contains("_viewModel.ConfirmStationClearanceAsync(prompt)", handler, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static WireToGateStationClearanceOutcome Outcome(
        WireToGateStationClearanceOutcomeKind kind,
        bool stationReleased,
        string? reasonCode) =>
        new(kind, RequestId, Charger, stationReleased, reasonCode);

    private static WireToGateJourneySnapshot Journey(params WireToGateMovementLeg[] legs) =>
        new(
            new WireToGateVehicleBusinessState(
                1,
                "READY",
                "CLEARING_MAINTENANCE",
                false,
                "SUFFICIENT",
                "NOT_CHARGING",
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
