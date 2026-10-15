using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The wiring of the staging pickup stop's 「出发」 entry (8005-agv-onboard-hmi#349): the view model reads the business
/// service's entry view whole, and hands back the prompt the operator confirmed.
/// </summary>
/// <remarks>
/// One method, called by <c>App.xaml.cs</c> and by the G2 harness alike, so the lines a test drives are the lines the
/// product runs -- the shape <see cref="StationClearanceWiring"/> has, for the same reason.
/// </remarks>
internal static class CurrentStopDepartureWiring
{
    public static void Configure(MainViewModel viewModel, WireToGateBusinessService business)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureCurrentStopDeparture(
            () => business.CurrentStopDeparture,
            (shown, cancellationToken) => business.RequestCurrentStopDepartureAsync(shown, cancellationToken));
    }
}
