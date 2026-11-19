using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The wiring of the 「取消装货」 press (<c>8005-agv-onboard-hmi#291</c>): the view model takes the press when the entry
/// is pressed and sends it once the operator confirms, and its confirmation names what the press acts on.
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
        // 装货开始后只有一篮未装、门都锁好、输出复位时可取消；不能时入口按不动并说原因（8005-agv-onboard-hmi#392）。
        viewModel.ConfigureLoadCancellationBlockedReason(() => business.LoadCancellationBlockedReason);
        // 取货停靠清单每行的「取消」（待送点 8005-agv-onboard-hmi#353，机台 8005-agv-onboard-hmi#363）：同一个请求，主体由操作员按哪一行给出。
        viewModel.ConfigureWorklistRowCancellation(
            () => business.OffersWorklistRowCancellationAtThisStop,
            business.CanRequestWorklistRowCancellation,
            demandId => business.BindWorklistRowCancellationPress(
                WireToGateBusinessService.LoadCancellationDefaultReason,
                demandId),
            () => business.CanRequestLoadCancellationBeforeAnySublot);
    }
}
