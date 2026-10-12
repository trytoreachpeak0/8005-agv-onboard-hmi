using System.Diagnostics;
using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ScreenState;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The screen-state snapshot read in existing scenarios (onboard-hmi#313, T1a): loading in progress
/// (<see cref="TwoWorklistItemsPassTheViewModelTheJourneyGateAndEntryWithoutAnUnhandledUiError"/> and the first half of
/// <see cref="TheLoadCancellationEntryClosesOnceTheLoadsNonCompletedResultIsOnItsWay"/>), a session that requires
/// recovery (its second half), and a latched fatal fault
/// (<see cref="ALatchedVehicleOpensNoDoorForASlotCommandAndReportsItFailedNotStarted"/>). The helpers live here; the
/// assertions sit in those tests, at the moment each one already waits for.
/// </summary>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// The ten recovery entries (MainViewModel's own count): nine gated by <c>AllowRecoveryEntry</c>, the load
    /// cancellation by <c>AllowLoadCancellationEntry</c>.
    /// </summary>
    private static readonly string[] RecoveryEntryButtons =
    [
        "OnWireToGateRecoveryClick",
        "OnLoadCancellationClick",
        "OnLoadCompensationClick",
        "OnLoadCorrectionClick",
        "OnFaultCargoHandoffClick",
        "OnForcedMechanicalRecoveryClick",
        "OnConfirmForcedMechanicalRecoveryClick",
        "OnSubmitHardwareRecoveryRecordClick",
        "OnHardwareRepairReleaseClick",
        "OnManualChargingReturnClick",
    ];

    private static HmiButtonState ButtonOf(HmiScreenState screen, string key) =>
        Assert.Single(screen.Buttons, button => button.Key == key);

    private static HmiInputState InputOf(HmiScreenState screen, string key) =>
        Assert.Single(screen.Inputs, input => input.Key == key);

    /// <summary>
    /// The content boundary of research section 4.1: none of these strings -- sublots the scenario used, text it typed --
    /// appears anywhere in the snapshot as it would be written out.
    /// </summary>
    private static void AssertCarriesNone(HmiScreenState screen, params string[] secrets)
    {
        string written = JsonSerializer.Serialize(screen, ReadableJson);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, written, StringComparison.Ordinal));
    }

    /// <summary>
    /// The departure from research section 4.1 the coordinator decided on 2026-10-12: the banner's guidance line and the
    /// operator record's messages are not in the snapshot, because both can carry a sublot or text the operator typed.
    /// Checked against the text the screen shows at that moment, not a fixture: the guidance line (unless it is the
    /// operator notice, which is recorded on purpose, or the same words as the state line) and every record message.
    /// </summary>
    private static void AssertCarriesNoGuidanceOrRecordText(Harness harness, HmiScreenState screen)
    {
        string written = JsonSerializer.Serialize(screen, ReadableJson);
        string guidance = harness.OnUi(() => harness.ViewModel.Guidance);
        string[] shown = [.. harness.LogsSnapshot().Select(line => line.Message).Append(guidance)
            .Where(text => text.Length > 0
                && text != screen.OperatorNotice?.Text
                && text != screen.Banner.StateText)];

        Assert.NotEmpty(shown);
        Assert.All(shown, text => Assert.DoesNotContain(text, written, StringComparison.Ordinal));
    }

    /// <summary>Unescaped, so a search for Chinese text finds it if it is there.</summary>
    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The guidance line at a moment it really names a sublot -- the one the operator typed into the forced handoff box
    /// -- stays out of the snapshot (onboard-hmi#313, review M1). The event has the shape the business service publishes
    /// for that warning (<c>WireToGateBusinessService.RecoveryVectors.cs</c>: <c>RECOVERY_BLOCKED</c>, the warning both as the
    /// record's message and as the operation's guidance), delivered through the same view-model entry <c>App</c> wires.
    /// </summary>
    [Fact]
    public async Task AGuidanceLineNamingTheTypedSublotStaysOutOfTheScreenState()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
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
            token);
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null,
            "the worklist",
            token);

        const string Typed = "SUBLOT-TYPED-7";
        string warning = $"子批号 {Typed} 与需求的子批号 SUBLOT-A 不一致。请核对实物；确认无误后再按一次「已隔离并完成机械取出」上报，或改正子批号后再确认。";
        DateTimeOffset now = DateTimeOffset.Now;
        harness.OnUi(() => harness.ViewModel.ApplyWireToGateOperatorEvent(new WireToGateOperatorEvent(
            now,
            "RECOVERY_BLOCKED",
            warning,
            new WireToGateHmiOperationSnapshot(
                AttemptA, OperationType.Load, [1], WireToGateHmiOperationStage.RecoveryRequired, warning, now))));

        // The precondition the review found missing: the screen does show the typed sublot right now.
        Assert.Contains(Typed, harness.OnUi(() => harness.ViewModel.Guidance), StringComparison.Ordinal);
        HmiScreenState screen = harness.ScreenState();
        AssertCarriesNone(screen, Typed, "SUBLOT-A", "SUBLOT-B");
        AssertCarriesNoGuidanceOrRecordText(harness, screen);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// Research section 5, T1a: one measurement of the build in the G2 harness. The number is reported, not judged --
    /// it goes into the PR. The harness has no dispatcher, so this is the cost of the reads alone, under the stand-in UI
    /// lock, which is the work T2 will queue on the dispatcher at <c>Background</c> priority.
    /// </summary>
    [Fact]
    public async Task BuildingTheScreenStateIsMeasured()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
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
            token);
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null
                && harness.Session.CurrentJourney.UpcomingStopPlan is not null,
            "the worklist and the plan",
            token);

        for (int warmup = 0; warmup < 20; warmup++)
        {
            _ = harness.ScreenState();
        }

        const int Rounds = 200;
        double[] micros = new double[Rounds];
        for (int round = 0; round < Rounds; round++)
        {
            long start = Stopwatch.GetTimestamp();
            _ = harness.ScreenState();
            micros[round] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }

        Array.Sort(micros);
        HmiScreenState screen = harness.ScreenState();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"HmiScreenState build over {Rounds} rounds: median {micros[Rounds / 2]:F1} us, "
                + $"p95 {micros[(int)(Rounds * 0.95)]:F1} us, max {micros[^1]:F1} us; "
                + $"{screen.Buttons.Count} buttons, {screen.Inputs.Count} inputs, {screen.Lockers.Count} lockers, "
                + $"{screen.WorklistRows.Count} worklist rows, {screen.JourneyPlanLegs.Count} plan legs, "
                + $"{JsonSerializer.Serialize(screen).Length} JSON chars");
        Assert.Equal(2, screen.WorklistRows.Count);
        Assert.Empty(harness.UiErrors);
    }
}
