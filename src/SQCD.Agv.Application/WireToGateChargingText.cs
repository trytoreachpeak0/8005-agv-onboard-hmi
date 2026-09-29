using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// 充电的文案，唯一的一处（批次9-15，<c>8005-agv-onboard-hmi#220</c>；向量 <c>CV-AUTOMATIC-CHARGING-CYCLE</c> 的
/// <c>DISPLAY_CHARGING_PURPOSE</c>）：充电停靠时到站那一格的话，以及车辆那一格的电量与充电状态。
/// </summary>
/// <remarks>
/// <para>
/// <b>只显示服务端下发的四个值，车载端不判任何事。</b><c>batteryState</c> 四值、<c>chargingCycleState</c> 七值、
/// <c>manualChargingHold</c> 与 <c>activePurpose</c>；线上没有电量百分比，这里也不显示百分比。何时去充、充到多少、
/// 能不能接活都是服务端的策略（<c>NEVER_DECIDE_POLICY_LOCALLY</c>）。
/// </para>
/// <para>
/// 文案表里没有的值原样显示，不猜它的意思——与 <c>OnboardCommandRejectionText</c> 同一个取舍：猜错比不说更糟，原始值
/// 至少能念给电话那头的人听。入站校验已经只放行协议声明的值，所以这一支今天走不到。
/// </para>
/// <para>
/// 充电停靠时到站那一格写哪句由 <see cref="WireToGateNonBusinessStop.Classify"/> 定，与录入入口同一个判据；
/// 站名只取当前那条 <c>CHARGER</c> 腿的，说不出站名时不拿别的腿顶上（照 <see cref="WireToGateIdleReturnText"/> 的写法）。
/// </para>
/// </remarks>
public static class WireToGateChargingText
{
    /// <summary>
    /// <c>manualChargingHold=true</c> 时的那一句：车在等人工充电，由服务端保持，车载端不能自己解除
    /// （<c>CV-MANUAL-CHARGING-RETURN</c> 的 <c>NEVER_CLEAR_HOLD_LOCALLY</c>）。
    /// </summary>
    public const string ManualChargingHoldText = "需人工充电：服务端保持";

    private static readonly IReadOnlyDictionary<string, string> BatteryTexts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SUFFICIENT"] = "电量充足",
            ["LOW"] = "电量偏低",
            ["MANDATORY_CHARGE"] = "需强制充电",
            ["UNKNOWN"] = "电量未知"
        };

    /// <summary><c>NOT_CHARGING</c> 不显示：不在充电周期里没什么要说的。</summary>
    private static readonly IReadOnlyDictionary<string, string> CycleTexts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NOT_CHARGING"] = string.Empty,
            ["ALLOCATED"] = "已分配充电桩",
            ["EN_ROUTE"] = "前往充电",
            ["CHARGING"] = "充电中",
            ["COMPLETE"] = "已充满",
            ["UNABLE_TO_CHARGE"] = "充不上电",
            ["UNKNOWN"] = "充电状态未知"
        };

    /// <summary>电量那一格的文字；还没有业务状态时是空串。</summary>
    public static string BatteryText(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        return journey.VehicleBusinessState is { } state
            ? BatteryTexts.GetValueOrDefault(state.BatteryState, state.BatteryState)
            : string.Empty;
    }

    /// <summary>
    /// 充电状态那一格的文字：充电周期状态（<c>NOT_CHARGING</c> 不写）与人工充电保持，都有时用「；」连起来；
    /// 两样都没有或还没有业务状态时是空串。
    /// </summary>
    public static string StatusText(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        if (journey.VehicleBusinessState is not { } state)
        {
            return string.Empty;
        }

        string[] parts =
        [
            CycleTexts.GetValueOrDefault(state.ChargingCycleState, state.ChargingCycleState),
            state.ManualChargingHold ? ManualChargingHoldText : string.Empty
        ];
        return string.Join("；", parts.Where(part => part.Length > 0));
    }

    /// <summary>给 UIA 的电量原始值（AutomationId <c>BatteryStatus</c> 的 <c>ItemStatus</c>）；还没有业务状态时是空串。</summary>
    public static string BatteryStatus(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        return journey.VehicleBusinessState?.BatteryState ?? string.Empty;
    }

    /// <summary>
    /// 给 UIA 的充电周期原始值（AutomationId <c>ChargingStatus</c> 的 <c>ItemStatus</c>），<c>NOT_CHARGING</c> 也照报；
    /// 还没有业务状态时是空串。
    /// </summary>
    public static string CycleStatus(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        return journey.VehicleBusinessState?.ChargingCycleState ?? string.Empty;
    }

    /// <summary>
    /// 充电停靠时到站那一格的文字：「前往充电桩 &lt;站&gt;」「在充电桩 &lt;站&gt; 充电中」「已充满，在充电桩 &lt;站&gt; 待命」
    /// 「在充电桩 &lt;站&gt; 充不上电，等待处置」；不是充电停靠时是空串。
    /// </summary>
    /// <remarks>
    /// 充电中、已充满、充不上三种按 <c>chargingCycleState</c> 写；其余（已分配、前往、未知、还没有业务状态）按当前腿的
    /// <c>state</c>：<c>ARRIVED</c> 写「在充电桩 &lt;站&gt;」，否则「前往充电桩 &lt;站&gt;」。
    /// </remarks>
    public static string VisitText(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        if (WireToGateNonBusinessStop.Classify(journey) != WireToGateNonBusinessStopKind.Charging)
        {
            return string.Empty;
        }

        WireToGateMovementLeg? leg = journey.CurrentLeg is { StopPurposeCategory: "CHARGER" } charger ? charger : null;
        // 有站名时站名两边留空格（「在充电桩 ST-09 充电中」），没有时不留（「在充电桩充电中」）。
        string at = leg is null ? "充电桩" : $"充电桩 {leg.StationId} ";
        return journey.VehicleBusinessState?.ChargingCycleState switch
        {
            "CHARGING" => $"在{at}充电中",
            "COMPLETE" => $"已充满，在{at}待命",
            "UNABLE_TO_CHARGE" => $"在{at}充不上电，等待处置",
            _ => leg?.State == "ARRIVED" ? $"在{at.TrimEnd()}" : $"前往{at.TrimEnd()}"
        };
    }
}
