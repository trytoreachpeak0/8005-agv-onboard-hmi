using System.ComponentModel;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The cargo handoff fields of the forced mechanical recovery confirmation on the main window
/// (8005-agv-onboard-hmi#216): both are required on a demand, the confirm button stays disabled and says
/// what is missing until they are filled, and a record already on file replaces the fields.
/// </summary>
/// <remarks>
/// <para>
/// Enabled state and text are asserted together with the change notification under the bound property's
/// name, for the reason <see cref="FatalFaultLatchViewModelTests"/> gives: WPF rereads a binding only on a
/// notification with that name, and hmi#109／#112 were both a right value with a wrong notification.
/// </para>
/// <para>No trait: screen presentation, as with <see cref="FatalFaultLatchViewModelTests"/>.</para>
/// </remarks>
public sealed class ForcedCargoHandoffViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheConfirmButtonStaysDisabledAndSaysWhatIsMissingUntilBothFieldsAreFilled()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await ConfiguredAsync(controller, needsHandoff: true, onFile: null);
        List<string?> changed = [];
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.True(viewModel.CanConfirmForcedMechanicalRecovery);
        Assert.True(viewModel.NeedsForcedCargoHandoff);
        Assert.False(viewModel.CanSubmitForcedMechanicalRecoveryConfirmation);
        Assert.Equal("请填写取出货物的子批号和接收人。", viewModel.ForcedCargoHandoffMissingText);

        viewModel.ForcedHandoffSublot = "SUBLOT-001";
        Assert.False(viewModel.CanSubmitForcedMechanicalRecoveryConfirmation);
        Assert.Equal("请填写接收人。", viewModel.ForcedCargoHandoffMissingText);

        viewModel.ForcedHandoffSublot = " ";
        viewModel.ForcedHandoffReceiverName = "收货员李二";
        Assert.False(viewModel.CanSubmitForcedMechanicalRecoveryConfirmation);
        Assert.Equal("请填写取出货物的子批号。", viewModel.ForcedCargoHandoffMissingText);

        viewModel.ForcedHandoffSublot = "SUBLOT-001";
        Assert.True(viewModel.CanSubmitForcedMechanicalRecoveryConfirmation);
        Assert.Equal(string.Empty, viewModel.ForcedCargoHandoffMissingText);
        Assert.Contains(nameof(MainViewModel.CanSubmitForcedMechanicalRecoveryConfirmation), changed);
        Assert.Contains(nameof(MainViewModel.ForcedCargoHandoffMissingText), changed);
    }

    [Fact]
    public async Task AForcedRecoveryWithNoDemandNeedsNoHandoffRecord()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await ConfiguredAsync(controller, needsHandoff: false, onFile: null);

        Assert.False(viewModel.NeedsForcedCargoHandoff);
        Assert.True(viewModel.CanSubmitForcedMechanicalRecoveryConfirmation);
        Assert.Equal(string.Empty, viewModel.ForcedCargoHandoffMissingText);
    }

    [Fact]
    public async Task ARecordOnFileReplacesTheFieldsAndIsWhatThePressSends()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        (string? Sublot, string? Receiver)? pressed = null;
        MainViewModel viewModel = await ConfiguredAsync(
            controller,
            needsHandoff: false,
            onFile: new WireToGateForcedCargoHandoff("SUBLOT-001", "收货员李二", Now),
            confirmer: (sublot, receiver, _) =>
            {
                pressed = (sublot, receiver);
                return Task.FromResult(false);
            });

        Assert.False(viewModel.NeedsForcedCargoHandoff);
        Assert.True(viewModel.HasForcedCargoHandoffOnFile);
        Assert.Contains("子批号 SUBLOT-001，接收人 收货员李二", viewModel.ForcedCargoHandoffOnFileText, StringComparison.Ordinal);
        Assert.True(viewModel.CanSubmitForcedMechanicalRecoveryConfirmation);

        Assert.False(await viewModel.ConfirmForcedMechanicalRecoveryAsync(TestContext.Current.CancellationToken));
        // The screen sends what it holds (empty); the business service ignores it for the record on file.
        Assert.Equal((string.Empty, string.Empty), pressed);
    }

    [Fact]
    public async Task TheTypedFieldsAreKeptAfterARefusedPressAndClearedAfterAReportedOne()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        bool report = false;
        (string? Sublot, string? Receiver)? pressed = null;
        MainViewModel viewModel = await ConfiguredAsync(
            controller,
            needsHandoff: true,
            onFile: null,
            confirmer: (sublot, receiver, _) =>
            {
                pressed = (sublot, receiver);
                return Task.FromResult(report);
            });
        viewModel.ForcedHandoffSublot = "SUBLOT-001";
        viewModel.ForcedHandoffReceiverName = "收货员李二";
        CancellationToken token = TestContext.Current.CancellationToken;

        Assert.False(await viewModel.ConfirmForcedMechanicalRecoveryAsync(token));
        Assert.Equal(("SUBLOT-001", "收货员李二"), pressed);
        Assert.Equal("SUBLOT-001", viewModel.ForcedHandoffSublot);
        Assert.Equal("收货员李二", viewModel.ForcedHandoffReceiverName);

        report = true;
        Assert.True(await viewModel.ConfirmForcedMechanicalRecoveryAsync(token));
        Assert.Equal(string.Empty, viewModel.ForcedHandoffSublot);
        Assert.Equal(string.Empty, viewModel.ForcedHandoffReceiverName);
    }

    /// <summary>
    /// A latch closes the confirmation, and the handoff fields and the button's enabled state go with it --
    /// through either refresh path, because both read the confirmation's own entry.
    /// </summary>
    [Fact]
    public async Task ALatchClosesTheHandoffFieldsWithTheConfirmation()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await ConfiguredAsync(controller, needsHandoff: true, onFile: null);
        viewModel.ForcedHandoffSublot = "SUBLOT-001";
        viewModel.ForcedHandoffReceiverName = "收货员李二";
        Assert.True(viewModel.CanSubmitForcedMechanicalRecoveryConfirmation);

        controller.EnterFatalFault("UI_COMMAND_FAILED", OnboardFatalFaultBanner.UiCommandFailed);
        viewModel.RefreshWireToGateInputState();

        Assert.False(viewModel.CanConfirmForcedMechanicalRecovery);
        Assert.False(viewModel.NeedsForcedCargoHandoff);
        Assert.False(viewModel.CanSubmitForcedMechanicalRecoveryConfirmation);
        Assert.Equal(string.Empty, viewModel.ForcedCargoHandoffMissingText);
    }

    private static async Task<MainViewModel> ConfiguredAsync(
        OnboardController controller,
        bool needsHandoff,
        WireToGateForcedCargoHandoff? onFile,
        Func<string?, string?, CancellationToken, Task<bool>>? confirmer = null)
    {
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);
        viewModel.ConfigureWireToGate((_, _, _) => Task.CompletedTask, () => true);
        viewModel.UpdateWireToGateStatus(new WireToGateSessionSnapshot(
            Connected: true,
            SessionGeneration: 1,
            Readiness: WireToGateSessionReadiness.RecoveryRequired,
            ReasonCodes: [],
            CapabilityVersion: 1,
            SafetyStateVersion: 1,
            UpdatedAt: Now));
        viewModel.ConfigureForcedIsolation(
            () => true,
            confirmer ?? ((_, _, _) => Task.FromResult(false)),
            () => [],
            () => false,
            (_, _) => Task.FromResult(false),
            needsCargoHandoff: () => needsHandoff,
            cargoHandoffOnFile: () => onFile);
        viewModel.RefreshWireToGateInputState();
        return viewModel;
    }
}
