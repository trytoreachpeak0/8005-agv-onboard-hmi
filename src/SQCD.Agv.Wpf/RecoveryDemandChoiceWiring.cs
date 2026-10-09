using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The wiring of the loads-on-board choice for the fault cargo handoff and the forced mechanical recovery
/// (8005-agv-onboard-hmi#209): the list the view model shows and the two presses that carry the chosen demand.
/// </summary>
/// <remarks>
/// One method, called by <c>App.xaml.cs</c> and by the G2 harness alike, so the lines a test drives are the lines the
/// product runs -- the shape <see cref="StationClearanceWiring"/> has, for the same reason.
/// </remarks>
internal static class RecoveryDemandChoiceWiring
{
    public static void Configure(MainViewModel viewModel, WireToGateBusinessService business)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureRecoveryDemandChoices(
            () => business.RecoveryDemandChoices,
            () => business.LoadsOnBoard,
            () => business.CompensationFallbackDemandId,
            business.RecoveryTargetFor,
            (reason, demandId, cancellationToken) =>
                business.RequestFaultCargoHandoffAsync(reason, demandId, cancellationToken),
            (reason, demandId, cancellationToken) =>
                business.RequestForcedMechanicalRecoveryAsync(reason, demandId, cancellationToken));
    }
}
