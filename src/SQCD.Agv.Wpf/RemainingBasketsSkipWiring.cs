using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The wiring of 「剩下的不装」 (<c>8005-agv-onboard-hmi#388</c>): the view model's three delegates onto the business service.
/// </summary>
/// <remarks>
/// One method, called by <c>App.xaml.cs</c> and by the G2 harness alike, so the lines a test drives are the lines the
/// product runs -- the shape of <see cref="SlotReopenWiring"/>.
/// </remarks>
internal static class RemainingBasketsSkipWiring
{
    public static void Configure(MainViewModel viewModel, WireToGateBusinessService business)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureRemainingBasketsSkip(
            () => business.RemainingBasketsSkipOffer,
            () => business.RemainingBasketsSkipStatus,
            (shown, cancellationToken) => business.RequestRemainingBasketsSkipAsync(shown, cancellationToken));
    }
}
