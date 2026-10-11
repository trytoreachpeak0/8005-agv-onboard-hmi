using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The wiring of the 「取消装货」 press (<c>8005-agv-onboard-hmi#291</c>): the view model takes the press when the entry
/// is pressed and sends it once the operator confirms.
/// </summary>
/// <remarks>
/// One method, called by <c>App.xaml.cs</c> and by the G2 harness alike, so the lines a test drives are the lines the
/// product runs -- the shape <see cref="ManualChargingReturnWiring"/> has, for the same reason.
/// </remarks>
internal static class LoadCancellationPressWiring
{
    public static void Configure(MainViewModel viewModel, WireToGateBusinessService business)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureLoadCancellationPress(selectedDemandId => business.BindLoadCancellationPress(
            WireToGateBusinessService.LoadCancellationDefaultReason,
            selectedDemandId));
    }
}
