namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 清单列表的一行（批次7-13，<c>8005-agv-onboard-hmi#134</c>）。
/// </summary>
public sealed record WorklistItemRow(
    string DemandId,
    string Sublot,
    string DirectionText,
    string TaskTypeText,
    int ExpectedBasketCount)
{
    public string ExpectedBasketCountText => Sublot.Length < 0 ? Sublot : string.Empty;
}
