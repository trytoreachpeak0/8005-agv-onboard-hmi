using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// 非业务停靠是哪一种，决定到站那一格写哪一类话（批次9-15，<c>8005-agv-onboard-hmi#220</c>）。
/// </summary>
public enum WireToGateNonBusinessStopKind
{
    /// <summary>业务停靠，或旅程为空：到站那一格照业务停靠的写法。</summary>
    None,

    /// <summary>空闲返回去等待点（批次8-22，<c>8005-agv-onboard-hmi#217</c>）。</summary>
    IdleReturn,

    /// <summary>清桩：离开充电桩去等待点（<c>activePurpose=CLEARING_MAINTENANCE</c>，<c>REQ-0178</c>）。</summary>
    Clearing,

    /// <summary>充电停靠：去充电桩、在桩充电、充满待命、充不上。</summary>
    Charging
}

/// <summary>
/// 非业务停靠的分类，唯一的一处：<see cref="WireToGateIdleReturnText"/> 与 <see cref="WireToGateChargingText"/>
/// 都按它决定自己说不说话，所以两者不会同时说，也不会都不说。
/// </summary>
/// <remarks>
/// <para>
/// <b>是不是非业务停靠只看 <see cref="WireToGateJourneySnapshot.IsNonBusinessStop"/></b>，与四处录入入口同一个判据，
/// 这里只在它为真时再分是哪一种；为假时一律 <see cref="WireToGateNonBusinessStopKind.None"/>。所以分类永远不会让一处
/// 录入入口关着、到站那一格却照业务停靠写，反过来也不会。
/// </para>
/// <para>
/// <b>先看业务状态的 <c>activePurpose</c>，说不清时再看当前腿的类别。</b>用途说的是车在做什么，腿说的是车往哪去。
/// 批次 9 里清桩与空闲返回共用等待点集合（<c>REQ-0178</c>），同一条 <c>WAITING_POINT</c> 腿可能是空闲返回也可能是
/// 清桩，只有用途分得开；清桩刚开始时当前腿可能还是 <c>CHARGER</c>，也要写清桩而不是充电。用途为 <c>null</c> 或
/// <c>TRANSPORT</c> 时（计划先到、用途还没跟上）按腿：<c>CHARGER</c> 是充电，<c>WAITING_POINT</c> 照批次8-22 算空闲返回。
/// </para>
/// <para>
/// 都是服务端下发的值，不从站名、站点号推断；没有任何本地状态记着「到过充电桩」，下一份快照换了就换。
/// </para>
/// </remarks>
public static class WireToGateNonBusinessStop
{
    public static WireToGateNonBusinessStopKind Classify(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        if (!journey.IsNonBusinessStop)
        {
            return WireToGateNonBusinessStopKind.None;
        }

        return journey.VehicleBusinessState?.ActivePurpose switch
        {
            "CHARGING" => WireToGateNonBusinessStopKind.Charging,
            "CLEARING_MAINTENANCE" => WireToGateNonBusinessStopKind.Clearing,
            "IDLE_RETURN" => WireToGateNonBusinessStopKind.IdleReturn,
            _ => journey.CurrentLeg?.StopPurposeCategory == "CHARGER"
                ? WireToGateNonBusinessStopKind.Charging
                : WireToGateNonBusinessStopKind.IdleReturn
        };
    }
}
