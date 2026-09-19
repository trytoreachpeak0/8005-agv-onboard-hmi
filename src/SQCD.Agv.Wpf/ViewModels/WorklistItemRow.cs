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
    string SideCode)
{
    public string ExpectedBasketCountText => $"{ExpectedBasketCount} 篮";
}
