using SQCD.Agv.Application;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 待送点取货停靠上清单每行的「取消」（CP-0014 修订项二，<c>REQ-0211</c>；<c>8005-agv-onboard-hmi#353</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 哪一行有按钮由屏上已有的两样决定：停靠是待送点取货停靠（「出发」入口出现，与 <see cref="LoadCancellationCaption"/> 同一个
/// 认法），这一行还没装（侧仍是「待分配」，没有任何仓位命令或日志里的装货指向它）。按不按得动由业务服务说
/// （<c>WireToGateBusinessService.CanRequestWorklistRowCancellation</c>）：会话就绪、录入请求开着、没有仓位操作在跑，而且没有
/// 别的一行的取消在等答复。
/// </para>
/// <para>
/// 行对象不可变，两样变了就整张表重建（<see cref="RebuildWorklistItemsCore"/>，选择按需求保留），不逐行改。
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    private Func<string, bool>? _worklistRowCancellationCanPress;
    private Func<string, WireToGateLoadCancellationPress>? _worklistRowCancellationBinder;
    private Func<bool>? _sharedEntryOffersCancellationBeforeAnySublot;

    /// <summary>
    /// 接上业务服务的三样（<see cref="LoadCancellationPressWiring"/>）：这一行按不按得动，按下时取下按键，以及共用入口此刻给的
    /// 是不是扫码前取消——待送点上那只剩「那一行已离开清单」的收尾，按钮照每行叫「取消」（审查 L1）。
    /// </summary>
    internal void ConfigureWorklistRowCancellation(
        Func<string, bool> canPress,
        Func<string, WireToGateLoadCancellationPress> binder,
        Func<bool> sharedEntryOffersCancellationBeforeAnySublot)
    {
        _worklistRowCancellationCanPress = canPress ?? throw new ArgumentNullException(nameof(canPress));
        _worklistRowCancellationBinder = binder ?? throw new ArgumentNullException(nameof(binder));
        _sharedEntryOffersCancellationBeforeAnySublot = sharedEntryOffersCancellationBeforeAnySublot
            ?? throw new ArgumentNullException(nameof(sharedEntryOffersCancellationBeforeAnySublot));
        RunOnUiThread(RefreshWorklistRowCancellationCore);
    }

    /// <summary>
    /// 按下某一行的「取消」时取下的一次按键：确认框复述的那一条与确认后要执行的按键出自同一次读取，确认时业务服务再核对一次，
    /// 对象变了就不发。
    /// </summary>
    public WorklistRowCancellationConfirmation BeginWorklistRowCancellation(string demandId)
    {
        ArgumentNullException.ThrowIfNull(demandId);
        if (_worklistRowCancellationBinder is not { } binder)
        {
            return new WorklistRowCancellationConfirmation(
                WireToGateLoadCancellationText.RowSubjectGone,
                _ => Task.FromResult(false));
        }

        WireToGateLoadCancellationPress press = binder(demandId);
        string detail = WorklistItems.FirstOrDefault(row => row.DemandId == (press.SubjectDemandId ?? demandId)) is { } subject
            ? WireToGateLoadCancellationText.RowSubject(subject.Sublot, subject.TaskTypeText, subject.ExpectedBasketCountText)
            : WireToGateLoadCancellationText.RowSubjectGone;
        return new WorklistRowCancellationConfirmation(detail, press.SendAsync);
    }

    /// <summary>
    /// 「本站清单」后面的条数，只在待送点取货停靠上写（「（共 6 条）」）：那里一屏露不全，操作员要知道下面还有几条。
    /// </summary>
    public string WorklistCountText =>
        CurrentStopDeparture.IsShown && WorklistItems.Count > 0 ? $"（共 {WorklistItems.Count} 条）" : string.Empty;

    /// <summary>
    /// 这一行有没有「取消」：待送点取货停靠上、还没装的那几行。侧标是「待分配」还不够：重启后更早装好的那一条侧标也回到
    /// 「待分配」，日志里的「车上待交接的需求」才记着它在车上（8005-agv-onboard-hmi#353 审查 M2）。
    /// </summary>
    private bool OffersWorklistRowCancellation(string demandId, string sideCode) =>
        _worklistRowCancellationCanPress is not null
        && CurrentStopDeparture.IsShown
        && sideCode == WorklistItemSides.Unassigned.Code
        && !(_journaledOperations is { } journaled && WireToGateBusinessService.IsLoadedOnBoard(journaled, demandId));

    private bool CanPressWorklistRowCancellation(string demandId, bool offered) =>
        offered && _worklistRowCancellationCanPress?.Invoke(demandId) == true;

    /// <summary>重算每行的按钮；与表上现有的不一样才重建，免得每次刷新都把清单整张换掉。</summary>
    private void RefreshWorklistRowCancellationCore()
    {
        foreach (WorklistItemRow row in WorklistItems)
        {
            bool offered = OffersWorklistRowCancellation(row.DemandId, row.SideCode);
            if (row.AtStagingPickupStop != CurrentStopDeparture.IsShown
                || row.OffersCancellation != offered
                || row.CanPressCancellation != CanPressWorklistRowCancellation(row.DemandId, offered))
            {
                RebuildWorklistItemsCore();
                return;
            }
        }
    }
}
