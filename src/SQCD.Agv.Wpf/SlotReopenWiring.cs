using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The wiring of 「重新开门」 over a door shut on the opposite occupancy (<c>8005-agv-onboard-hmi#385</c>): the view model's
/// three delegates onto the business service.
/// </summary>
/// <remarks>
/// One method, called by <c>App.xaml.cs</c> and by the G2 harness alike, so the lines a test drives are the lines the
/// product runs -- the shape of <see cref="ConflictedRecoveryWiring"/>.
/// </remarks>
internal static class SlotReopenWiring
{
    public static void Configure(MainViewModel viewModel, WireToGateBusinessService business)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureSlotReopen(
            () => business.CanReopenWaitingSlot,
            () => business.SlotReopenStatus,
            cancellationToken => business.RequestSlotReopenAsync(cancellationToken));
        // 「不装了」 is offered in the same wait, over a resumed load whose slots read empty (8005-agv-onboard-hmi#410).
        viewModel.ConfigureResumeAbandon(
            () => business.CanAbandonResume,
            cancellationToken => business.AbandonResumeAsync(cancellationToken));
    }
}
