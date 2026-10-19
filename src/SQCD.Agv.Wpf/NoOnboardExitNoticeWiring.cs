using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The wiring of the line that says the vehicle has no exit of its own while an operation's result waits for the server
/// (<c>8005-agv-onboard-hmi#370</c>): the view model's one delegate onto the business service, and the refresh on every
/// write of the business service's cached recovery state (<c>8005-agv-onboard-hmi#375</c>).
/// </summary>
/// <remarks>
/// One method, called by <c>App.xaml.cs</c> and by the G2 harness alike, so the lines a test drives are the lines the
/// product runs -- the shape of <see cref="ConflictedRecoveryWiring"/>.
/// </remarks>
internal static class NoOnboardExitNoticeWiring
{
    /// <param name="onUiThread">
    /// How the refresh reaches the UI thread. <c>null</c> in the product: the view model's refresh posts itself to the
    /// dispatcher. The G2 harness passes its stand-in UI thread, as it does for every other event it wires.
    /// </param>
    public static void Configure(
        MainViewModel viewModel,
        WireToGateBusinessService business,
        Action<Action>? onUiThread = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureNoOnboardExitNotice(() => business.NoOnboardExit);
        business.RecoveryStateCached += onUiThread is null
            ? (_, _) => viewModel.RefreshNoOnboardExitNotice()
            : (_, _) => onUiThread(viewModel.RefreshNoOnboardExitNotice);
    }
}
