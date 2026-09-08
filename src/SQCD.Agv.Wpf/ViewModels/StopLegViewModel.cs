using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 行程带上的一段。协议把一趟限制在最多两段——去取货点的 TO_PICKUP 与去交货闸口的 TO_GATE——
/// 且 sequence 从 1 起连续，所以这条带子最宽也就两格。
/// </summary>
public sealed record StopLegViewModel(WireToGateMovementLeg Leg)
{
    public int Sequence => Leg.Sequence;

    public string StationId => Leg.StationId;

    public string LegTypeText => Leg.LegType switch
    {
        "TO_PICKUP" => "取货",
        "TO_GATE" => "交货",
        _ => Leg.LegType
    };

    public string StateText => Leg.State switch
    {
        "PLANNED" => "待走",
        "ACTIVE" => "行进中",
        "ARRIVED" => "已到达",
        "COMPLETED" => "已完成",
        "BLOCKED" => "受阻",
        _ => Leg.State
    };

    /// <summary>
    /// 车此刻所在的那一段：正在走，或已经到了但这一段还没结清。操作员要一眼找到的就是它。
    /// </summary>
    public bool IsCurrent => Leg.State is "ACTIVE" or "ARRIVED";

    public bool IsBlocked => Leg.State == "BLOCKED";

    public bool IsDone => Leg.State == "COMPLETED";
}
