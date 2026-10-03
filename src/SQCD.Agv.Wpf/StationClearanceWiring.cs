using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The wiring of the manual station clearance entry (batch 9-16, <c>8005-agv-onboard-hmi#221</c>): the view
/// model's two delegates onto the business service.
/// </summary>
/// <remarks>
/// One method, called by <c>App.xaml.cs</c> and by the G2 harness alike, so the lines a test drives are the
/// lines the product runs (the shape <c>8005-agv-onboard-hmi#216</c>'s review asked of its own wiring).
/// Neither delegate takes two values of one type: what the operator confirms travels as a single
/// <see cref="Core.WireToGateStationClearancePrompt"/>, so there is no pair of strings to pass the wrong way round.
/// </remarks>
internal static class StationClearanceWiring
{
    public static void Configure(MainViewModel viewModel, WireToGateBusinessService business)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureStationClearance(
            () => business.StationClearance,
            (shown, cancellationToken) => business.ConfirmStationClearanceAsync(shown, cancellationToken));
    }
}
