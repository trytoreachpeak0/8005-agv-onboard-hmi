namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 清单列表的一行：本站的一条需求（批次7-13，<c>8005-agv-onboard-hmi#134</c>）。
/// </summary>
/// <remarks>
/// 每个字段都取自这一条清单项自己，不从别的行、站名或计划推。侧取自带这条需求的仓位命令
/// （<c>WorklistItemSides</c>），<see cref="SideCode"/> 是给 UIA 的 <c>ItemStatus</c> 的原始值。整行不可变：
/// 清单换修订号、侧有了新来源时整张表重建，不逐行合并。
/// </remarks>
public sealed record WorklistItemRow(
    string DemandId,
    string Sublot,
    string DirectionText,
    string TaskTypeText,
    int ExpectedBasketCount,
    string SideText,
    string SideCode,
    bool OffersCancellation = false,
    bool CanPressCancellation = false)
{
    public string ExpectedBasketCountText => $"{ExpectedBasketCount} 篮";

    /// <summary>
    /// 待送点取货停靠上每行压成一行（8005-agv-onboard-hmi#353）：方向与任务类型整张清单都一样（待送→焊线机台的取货），
    /// 只留篮数，单独一格放在子批后面。机台取货照旧两行。
    /// </summary>
    public bool AtStagingPickupStop { get; init; }

    public bool ShowsDetailLine => !AtStagingPickupStop;

    /// <summary>
    /// 侧标显示的字。有「取消」的行侧一定是「待分配」，字不显示，把宽度让给子批；<see cref="SideCode"/> 照旧给 UIA。
    /// </summary>
    public string ShownSideText => OffersCancellation ? string.Empty : SideText;
}
