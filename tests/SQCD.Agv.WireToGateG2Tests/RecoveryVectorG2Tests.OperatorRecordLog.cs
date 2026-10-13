using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.OperatorRecordOutput;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The content boundary of the operator record log (onboard-hmi#317, the coordinator's condition 1 as set on review
/// M1): what the operator types as a recovery reason, a receiver name or hardware observations, and the session
/// credential, never appear in a technical-log line -- measured on the business paths those values really travel.
/// </summary>
/// <remarks>
/// <para>
/// Each case first proves the typed value went out (it is in the line the fake server received), so a value that
/// never left the view model cannot make the absence check pass. The two SUBLOT boxes are exempt by that decision:
/// the business service names the submitted or handed-off SUBLOT in operator messages by design.
/// </para>
/// <para>
/// No <c>IntegrationSlice</c>/<c>ProtocolVector</c> trait: these prove the record log's boundary, not a frozen vector,
/// and <c>IntegrationSliceTraitArchitectureTests</c> keeps slice traits equal to the vectors a test claims.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    private const string T4Reason = "W2G_T4_RECOVERY_REASON_原因";
    private const string T4Observations = "W2G_T4_OBSERVATIONS_观察";
    private const string T4Receiver = "W2G_T4_RECEIVER_收货员";

    [Fact]
    public async Task TheRepairReasonAndObservationsThatWentOutAreNotInTheOperatorRecordLog()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            loadAlreadySettled: true);
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        (MainViewModel viewModel, RecordingLogger log, OperatorRecordLog recordLog) = await WireRecordLogAsync(harness, controller);
        using OperatorRecordLog _ = recordLog;
        await HoldBothSlotsAsync(harness, token);
        viewModel.UpdateWireToGateStatus(harness.Session.Current);
        viewModel.RefreshWireToGateInputState();

        viewModel.RecoveryReason = T4Reason;
        Assert.True(await viewModel.RequestHardwareRepairReleaseAsync(token));
        Assert.True(CarriesString(harness.ResultsOfType("ExceptionRecoverySessionRequested")[^1], T4Reason));

        viewModel.HardwareRecoveryObservations = T4Observations;
        Assert.True(await viewModel.SubmitHardwareRecoveryRecordAsync(token));
        Assert.True(CarriesString(harness.ResultsOfType("HardwareRecoveryRecordSubmitted")[^1], T4Observations));

        AssertTheRecordLogLeavesOut(log, viewModel, T4Reason, T4Observations);
    }

    [Fact]
    public async Task TheReceiverNameThatWentOutIsNotInTheOperatorRecordLog()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        (MainViewModel viewModel, RecordingLogger log, OperatorRecordLog recordLog) = await WireRecordLogAsync(harness, controller);
        using OperatorRecordLog _ = recordLog;
        await AuthorizeForcedRecoveryAsync(harness, token);
        await harness.NameTheDemandOnTheWorklistAsync(DemandId, "SUBLOT-T4-1", token);
        viewModel.UpdateWireToGateStatus(harness.Session.Current);
        viewModel.RefreshWireToGateInputState();

        viewModel.ForcedHandoffSublot = "SUBLOT-T4-1";
        viewModel.ForcedHandoffReceiverName = T4Receiver;
        Assert.True(await viewModel.ConfirmForcedMechanicalRecoveryAsync(token));
        JsonElement result = await harness.WaitForResultAsync("ForcedMechanicalRecoveryResult", token);
        Assert.Equal(T4Receiver, result.GetProperty("cargoHandoff").GetProperty("receiverName").GetString());

        AssertTheRecordLogLeavesOut(log, viewModel, T4Receiver);
    }

    /// <summary>
    /// The view model wired to the harness's business service as <c>App</c> wires it for these paths -- operator
    /// events into <c>ApplyWireToGateOperatorEvent</c>, <see cref="ForcedIsolationWiring.Configure"/> -- with the record
    /// log on its <c>Logs</c>.
    /// </summary>
    private static async Task<(MainViewModel ViewModel, RecordingLogger Log, OperatorRecordLog RecordLog)> WireRecordLogAsync(
        RecoveryVectorHarness harness,
        OnboardController controller)
    {
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);
        viewModel.ConfigureWireToGate((_, _, _) => Task.CompletedTask, () => false);
        ForcedIsolationWiring.Configure(viewModel, harness.Business);
        RecordingLogger log = new();
        OperatorRecordLog? recordLog = OperatorRecordLog.TryStart(new LogSettings { OperatorRecords = true }, viewModel.Logs, log); // logs-reference: the record log subscribes to its CollectionChanged
        Assert.NotNull(recordLog);
        harness.Business.OperatorEventPublished += (_, args) => viewModel.ApplyWireToGateOperatorEvent(args.Value);
        return (viewModel, log, recordLog);
    }

    /// <summary>Whether any string anywhere in the JSON line equals or contains <paramref name="value"/>, read unescaped.</summary>
    private static bool CarriesString(string line, string value)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return Carries(document.RootElement);

        bool Carries(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!.Contains(value, StringComparison.Ordinal),
            JsonValueKind.Object => element.EnumerateObject().Any(property => Carries(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Any(Carries),
            _ => false,
        };
    }

    private static void AssertTheRecordLogLeavesOut(RecordingLogger log, MainViewModel viewModel, params string[] typed)
    {
        string[] lines = [.. log.Entries
            .Where(entry => entry.Source == nameof(OperatorRecordLog))
            .Select(entry => entry.Message)];
        // Something was written: the paths published operator events, so an empty log would pass vacuously.
        Assert.NotEmpty(lines);
        Assert.Contains(OperatorLogSnapshot.Of(viewModel), line => line.Source == OperatorRecordSource.OperatorEvent);
        string[] excluded = [.. typed, "g2-vector-credential", "vector-test-proof"];
        Assert.All(lines, line => Assert.DoesNotContain(excluded, value => line.Contains(value, StringComparison.Ordinal)));
    }
}
