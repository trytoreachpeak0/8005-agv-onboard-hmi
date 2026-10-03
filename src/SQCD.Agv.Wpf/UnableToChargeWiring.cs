using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The wiring of the unable-to-charge field confirmation entry (batch 9-17, <c>8005-agv-onboard-hmi#222</c>): the
/// view model's two delegates onto the business service.
/// </summary>
/// <remarks>
/// One method, called by <c>App.xaml.cs</c> and by the G2 harness alike, so the lines a test drives are the lines the
/// product runs -- the shape <see cref="StationClearanceWiring"/> has, for the same reason.
/// </remarks>
internal static class UnableToChargeWiring
{
    public static void Configure(MainViewModel viewModel, WireToGateBusinessService business)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureUnableToCharge(
            () => business.UnableToCharge,
            (shown, cancellationToken) => business.ConfirmUnableToChargeAsync(shown, cancellationToken));
    }
}
