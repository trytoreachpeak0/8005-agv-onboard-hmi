using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The wiring of the line that explains a hidden 「充电后返回服务」 (<c>8005-agv-onboard-hmi#293</c>): the view
/// model's one delegate onto the business service.
/// </summary>
/// <remarks>
/// One method, called by <c>App.xaml.cs</c> and by the G2 harness alike, so the lines a test drives are the lines the
/// product runs -- the shape <see cref="UnableToChargeWiring"/> has, for the same reason.
/// </remarks>
internal static class ManualChargingReturnWiring
{
    public static void Configure(MainViewModel viewModel, WireToGateBusinessService business)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureManualChargingReturnGuidance(() => business.ManualChargingReturnBlocker);
    }
}
