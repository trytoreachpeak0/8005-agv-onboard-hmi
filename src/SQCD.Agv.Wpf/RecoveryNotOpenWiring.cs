using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

internal static class RecoveryNotOpenWiring
{
    public static void Configure(MainViewModel viewModel, WireToGateBusinessService business)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureRecoveryNotOpen(() => business.IsRecoveryNotOpen);
    }
}
