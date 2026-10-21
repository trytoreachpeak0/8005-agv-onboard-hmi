using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The wiring of the 「恢复尚未开放」 line beside the recovery entries (<c>8005-agv-onboard-hmi#391</c>): the view model's one
/// delegate onto the business service.
/// </summary>
/// <remarks>
/// One method, called by <c>App.xaml.cs</c> and by the G2 harness alike, so the lines a test drives are the lines the
/// product runs -- the shape of <see cref="SlotReopenWiring"/>.
/// </remarks>
internal static class RecoveryNotOpenWiring
{
    public static void Configure(MainViewModel viewModel, WireToGateBusinessService business)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureRecoveryNotOpen(() => business.IsRecoveryNotOpen, () => business.LastRecoveryRefusalText);
    }
}
