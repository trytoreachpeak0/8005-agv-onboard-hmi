using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// 空闲返回时到站那一格的文案与原始状态，唯一的一处（批次8-22，<c>8005-agv-onboard-hmi#217</c>；向量
/// <c>CV-WAITING-POINT-IDLE-RETURN</c> 的 <c>TREAT_WAITING_POINT_AS_NON_BUSINESS_STOP</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 是不是空闲返回只看 <see cref="WireToGateJourneySnapshot.IsWaitingPointStop"/>：当前腿的
/// <c>stopPurposeCategory</c> 或业务状态的 <c>activePurpose</c>，都是服务端下发的值，不从站名、站点号推断。
/// 前往还是已到只看当前腿的 <c>state</c> 是不是 <c>ARRIVED</c>。
/// </para>
/// <para>
/// 清单为 <c>null</c>（服务端到等待点不发清单）与清单为空（发一张空清单）两种做法显示同一句话：空闲返回不是
/// 「旅程未同步」，也不是「无待处理任务」那种要人留意的状态。
/// </para>
/// <para>
/// <see cref="Status"/> 是给 UIA 的原始值（到站那一格的 <c>ItemStatus</c>，AutomationId <c>IdleReturnStatus</c>），
/// 判据按它比，不必比中文全文；不是空闲返回时是空串。
/// </para>
/// </remarks>
public static class WireToGateIdleReturnText
{
    /// <summary>正在前往等待点。</summary>
    public const string EnRouteStatus = "EN_ROUTE_TO_WAITING_POINT";

    /// <summary>已到等待点（当前腿 <c>ARRIVED</c>），在点待命。</summary>
    public const string AtWaitingPointStatus = "AT_WAITING_POINT";

    /// <summary>
    /// 空闲返回的原始状态：<see cref="EnRouteStatus"/>、<see cref="AtWaitingPointStatus"/>，不是空闲返回时是空串。
    /// </summary>
    public static string Status(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        if (!journey.IsWaitingPointStop)
        {
            return string.Empty;
        }

        return WaitingPointLeg(journey)?.State == "ARRIVED" ? AtWaitingPointStatus : EnRouteStatus;
    }

    /// <summary>
    /// 到站那一格的文字：「空闲返回：前往等待点 &lt;站&gt;」或「在等待点 &lt;站&gt; 待命」；不是空闲返回时是空串。
    /// </summary>
    /// <remarks>
    /// 只有业务状态说了 <c>IDLE_RETURN</c>、计划里当前那条腿还不是等待点（计划还没到或还是旧的）时，说不出站名，
    /// 只写「空闲返回：前往等待点」，不拿别的腿的站名顶上。
    /// </remarks>
    public static string VisitText(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        if (!journey.IsWaitingPointStop)
        {
            return string.Empty;
        }

        WireToGateMovementLeg? leg = WaitingPointLeg(journey);
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
