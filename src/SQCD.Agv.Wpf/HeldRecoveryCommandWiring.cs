using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The wiring of the held recovery command entry (<c>8005-agv-onboard-hmi#239</c>): the view model's three delegates
/// onto the business service.
/// </summary>
/// <remarks>
/// One method, called by <c>App.xaml.cs</c> and by the G2 harness alike, so the lines a test drives are the lines the
/// product runs -- the shape of <see cref="StationClearanceWiring"/>. Both decisions take back the one
/// <see cref="WireToGateHeldRecoveryCommandPrompt"/> the operator read, so there is nothing to pass the wrong way round.
/// </remarks>
internal static class HeldRecoveryCommandWiring
{
    public static void Configure(MainViewModel viewModel, WireToGateBusinessService business)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureHeldRecoveryCommand(
            () => business.HeldRecoveryCommandView,
            (shown, cancellationToken) => business.ConfirmHeldRecoveryCommandAsync(shown, cancellationToken),
            (shown, cancellationToken) => business.DeclineHeldRecoveryCommandAsync(shown, cancellationToken));
    }
}
