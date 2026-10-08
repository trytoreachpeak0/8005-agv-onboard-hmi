namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 「车上待交接的需求」列表的一行：车上一条已装需求的货（8005-agv-onboard-hmi#209）。
/// </summary>
/// <remarks>
/// <see cref="Text"/> 是屏上那一行，也是 UIA 的 <c>Name</c>；<see cref="DemandId"/> 是 UIA 的 <c>ItemStatus</c>，真装置场景按它认行。
/// 子批号取自本次运行见过的清单，说不出时只写仓号——仓号就是操作员要去开的那几扇门。
/// </remarks>
public sealed record RecoveryDemandChoiceRow(string DemandId, string Text);
