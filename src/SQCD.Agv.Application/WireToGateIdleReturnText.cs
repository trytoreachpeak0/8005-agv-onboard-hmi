using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// 去等待点时到站那一格的文案与原始状态，唯一的一处：空闲返回（批次8-22，<c>8005-agv-onboard-hmi#217</c>；向量
/// <c>CV-WAITING-POINT-IDLE-RETURN</c> 的 <c>TREAT_WAITING_POINT_AS_NON_BUSINESS_STOP</c>）与清桩（批次9-15，
/// <c>8005-agv-onboard-hmi#220</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 是空闲返回还是清桩只看 <see cref="WireToGateNonBusinessStop.Classify"/>：当前腿的 <c>stopPurposeCategory</c> 与业务状态的
/// <c>activePurpose</c>，都是服务端下发的值，不从站名、站点号推断。前往还是已到只看当前腿的 <c>state</c> 是不是
/// <c>ARRIVED</c>。充电停靠不归这里（<see cref="WireToGateChargingText"/>），这里对它说空串。
/// </para>
/// <para>
/// <b>清桩不写成空闲返回</b>（批次9-15）。批次8-22 把「当前腿是 <c>WAITING_POINT</c>」一律当空闲返回；批次 9 里清桩与
/// 空闲返回共用等待点集合（<c>REQ-0178</c>），同一条腿可能带 <c>activePurpose=CLEARING_MAINTENANCE</c>，那时写
/// 「清桩：……」，<see cref="Status"/> 报清桩自己的两个值，不报空闲返回的。用途为 <c>IDLE_RETURN</c> 或 <c>null</c>
/// 而腿是等待点时照批次8-22 原样。
/// </para>
/// <para>
/// 清单为 <c>null</c>（服务端到等待点不发清单）与清单为空（发一张空清单）两种做法显示同一句话：空闲返回不是
/// 「旅程未同步」，也不是「无待处理任务」那种要人留意的状态。
/// </para>
/// <para>
/// <see cref="Status"/> 是给 UIA 的原始值（到站那一格的 <c>ItemStatus</c>，AutomationId <c>IdleReturnStatus</c>），
/// 判据按它比，不必比中文全文；不是空闲返回也不是清桩时是空串。
/// </para>
/// </remarks>
public static class WireToGateIdleReturnText
{
    /// <summary>正在前往等待点。</summary>
    public const string EnRouteStatus = "EN_ROUTE_TO_WAITING_POINT";

    /// <summary>已到等待点（当前腿 <c>ARRIVED</c>），在点待命。</summary>
    public const string AtWaitingPointStatus = "AT_WAITING_POINT";

    /// <summary>清桩：正在前往等待点。</summary>
    public const string ClearingEnRouteStatus = "CLEARING_TO_WAITING_POINT";

    /// <summary>清桩：已到等待点（当前腿 <c>ARRIVED</c>）。</summary>
    public const string ClearingAtWaitingPointStatus = "CLEARING_AT_WAITING_POINT";

    /// <summary>
    /// 到站那一格的原始状态：空闲返回时 <see cref="EnRouteStatus"/>、<see cref="AtWaitingPointStatus"/>，清桩时
    /// <see cref="ClearingEnRouteStatus"/>、<see cref="ClearingAtWaitingPointStatus"/>，其余是空串。
    /// </summary>
    public static string Status(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        bool arrived = WaitingPointLeg(journey)?.State == "ARRIVED";
        return WireToGateNonBusinessStop.Classify(journey) switch
        {
            WireToGateNonBusinessStopKind.IdleReturn => arrived ? AtWaitingPointStatus : EnRouteStatus,
            WireToGateNonBusinessStopKind.Clearing => arrived ? ClearingAtWaitingPointStatus : ClearingEnRouteStatus,
            _ => string.Empty
        };
    }

    /// <summary>
    /// 到站那一格的文字：「空闲返回：前往等待点 &lt;站&gt;」或「在等待点 &lt;站&gt; 待命」；清桩时「清桩：前往等待点 &lt;站&gt;」或
    /// 「清桩：已到等待点 &lt;站&gt;」；其余是空串。
    /// </summary>
    /// <remarks>
    /// 用途已经说了、计划里当前那条腿还不是等待点（计划还没到或还是旧的，清桩时可能还是充电桩那条）时，说不出站名，
    /// 只写「……前往等待点」，不拿别的腿的站名顶上。
    /// </remarks>
    public static string VisitText(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        WireToGateNonBusinessStopKind kind = WireToGateNonBusinessStop.Classify(journey);
        if (kind is not (WireToGateNonBusinessStopKind.IdleReturn or WireToGateNonBusinessStopKind.Clearing))
        {
            return string.Empty;
        }

        WireToGateMovementLeg? leg = WaitingPointLeg(journey);
        if (kind == WireToGateNonBusinessStopKind.Clearing)
        {
            return leg is null
                ? "清桩：前往等待点"
                : leg.State == "ARRIVED"
                    ? $"清桩：已到等待点 {leg.StationId}"
                    : $"清桩：前往等待点 {leg.StationId}";
        }

        if (leg is null)
        {
            return "空闲返回：前往等待点";
        }

        return leg.State == "ARRIVED"
            ? $"在等待点 {leg.StationId} 待命"
            : $"空闲返回：前往等待点 {leg.StationId}";
    }

    private static WireToGateMovementLeg? WaitingPointLeg(WireToGateJourneySnapshot journey) =>
        journey.CurrentLeg is { StopPurposeCategory: "WAITING_POINT" } leg ? leg : null;
}
