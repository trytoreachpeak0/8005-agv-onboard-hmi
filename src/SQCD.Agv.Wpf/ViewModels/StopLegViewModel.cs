using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 行程带上的一段：去取货点的 TO_PICKUP、去交货闸口的 TO_GATE，或去充电桩的 TO_CHARGER。
/// 协议允许一趟最多 10 段，sequence 唯一但不保证连续，带子按 sequence 排。
/// </summary>
public sealed record StopLegViewModel(WireToGateMovementLeg Leg)
{
    public int Sequence => Leg.Sequence;

    public string StationId => Leg.StationId;

    public string LegTypeText => Leg.LegType switch
    {
        "TO_PICKUP" => "取货",
        "TO_GATE" => "交货",
        "TO_CHARGER" => "充电",
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
