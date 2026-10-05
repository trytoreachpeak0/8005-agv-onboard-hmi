using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The wiring of the entry that closes a recovery whose result the server refused for good, after a maintainer checked
/// the cargo on site (<c>8005-agv-onboard-hmi#254</c>): the view model's three delegates onto the business service.
/// </summary>
/// <remarks>
/// One method, called by <c>App.xaml.cs</c> and by the G2 harness alike, so the lines a test drives are the lines the
/// product runs -- the shape of <see cref="StationClearanceWiring"/>.
/// </remarks>
internal static class ConflictedRecoveryWiring
{
    public static void Configure(MainViewModel viewModel, WireToGateBusinessService business)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureConflictedRecovery(
            () => business.ConflictedRecoveryView,
            () => business.CanCloseConflictedRecoveryAfterReview,
            cancellationToken => business.CloseConflictedRecoveryAfterReviewAsync(cancellationToken));
    }
}
