using System.Diagnostics;
using System.Text.Json;
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
        string written = JsonSerializer.Serialize(screen);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, written, StringComparison.Ordinal));
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
