using SQCD.Agv.Application;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// A held forced-recovery confirmation is what the operator reads on the main window: the business
    /// service's own events, forwarded to a real <see cref="MainViewModel"/> the way <c>App</c> forwards them,
    /// put the held line into <see cref="MainViewModel.Guidance"/> (8005-agv-onboard-hmi#214).
    /// </summary>
    /// <remarks>
    /// The business-layer assertions elsewhere read <c>CurrentOperationSnapshot</c>; a right snapshot with a
    /// wrong screen is possible, so this one reads the layer the operator sees.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AHeldForcedRecoveryConfirmationIsWhatTheMainWindowSays()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);
        viewModel.ConfigureWireToGate((_, _, _) => Task.CompletedTask, () => false);
        viewModel.UpdateWireToGateStatus(harness.Session.Current);
        harness.Business.OperatorEventPublished += (_, args) => viewModel.ApplyWireToGateOperatorEvent(args.Value);

        Assert.True(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "现场确认仓门无法电动解锁，申请强制机械恢复。", token));
        await harness.ConfirmForcedMechanicalRecoveryHeldAsync(token);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => viewModel.Guidance.Contains("不能登记货物交接", StringComparison.Ordinal),
            "the main window to say the forced recovery result is held for a cargo handoff record",
            token);
        Assert.DoesNotContain("已机械隔离", viewModel.Guidance, StringComparison.Ordinal);
    }
}
