using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The one wiring of the forced mechanical recovery's confirmation and hardware recovery record from the
/// main window to the business service, used by <c>App</c> and by the seam test that drives it.
/// </summary>
/// <remarks>
/// A method of its own rather than lambdas inline in <c>App.OnStartup</c> because the confirmer takes two
/// <c>string?</c> arguments: the SUBLOT and the receiver swapped would compile, and no test of either side
/// alone would notice. <c>ForcedCargoHandoffViewModelTests</c> runs this very method against a real business
/// service (independent review of 8005-agv-onboard-hmi#216).
/// </remarks>
internal static class ForcedIsolationWiring
{
    internal static void Configure(MainViewModel viewModel, WireToGateBusinessService business)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(business);
        viewModel.ConfigureForcedIsolation(
            () => business.CanConfirmForcedMechanicalRecovery,
            (sublot, receiverName, cancellationToken) => business.ConfirmForcedMechanicalRecoveryAsync(
                sublot,
                receiverName,
                cancellationToken),
            () => business.PhysicallyUnknownSlots,
            () => business.CanSubmitHardwareRecoveryRecord,
            (observations, cancellationToken) => business.SubmitHardwareRecoveryRecordAsync(
                observations,
                cancellationToken),
            // 货物交接记录（8005-agv-onboard-hmi#216）：要不要填、已登记的是哪一份。
            needsCargoHandoff: () => business.ForcedConfirmationNeedsCargoHandoff,
            cargoHandoffOnFile: () => business.ForcedCargoHandoffOnFile);
        // 维修放行（8005-agv-onboard-hmi#219）：同一张硬件恢复记录表单，记录挂在放行动作上。入口按理由发，与其他
        // 管理员入口同一个写法。
        viewModel.ConfigureRepairRelease(
            () => business.CanRequestHardwareRepairRelease,
            (reason, cancellationToken) => business.RequestHardwareRepairReleaseAsync(reason, cancellationToken),
            () => business.PendingHardwareRecoveryRecordObservations);
    }
}
