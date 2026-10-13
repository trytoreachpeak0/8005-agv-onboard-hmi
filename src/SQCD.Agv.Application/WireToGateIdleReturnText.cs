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
/// <b>清桩中、计划里还没有等待点腿时，只陈述车载端自己知道的事，不预言车动不动</b>（<c>8005-agv-onboard-hmi#330</c>）。服务端只在清桩开往等待点的单被
/// RIoT 确认之后才发「原桩腿 <c>COMPLETED</c> + 等待点腿」那份计划（control-server
/// <c>JourneyRuntimeEngine.ClearanceMove</c>，<c>PublishClearanceMoveSnapshotsAsync</c>）；开关
/// <c>ClearanceToWaitingPointEnabled</c> 关着、开着但还没建单或单还没确认、移动撤回之后，计划里都只有充电桩那条腿。开关本身
/// 不下发，这几种情形车载端分不开。所以那时写「清桩：未收到开往等待点的计划」，<see cref="Status"/> 报
/// <see cref="ClearingNoWaitingPointPlanStatus"/>：说的是这台车收到了什么，而不是服务端做了什么、车会不会动。
/// 不写「前往等待点」：开关关着时车不会自己走，现场会白等。也不写「暂留原位」，也不写「尚未下发……任务」（审查 M1、M1'）：
/// 开关开着时，单可能已发给 RIoT 而服务端对账还没确认（<c>ResultUnknown</c>），也可能 RIoT 已确认而计划还没到车上，或者
/// 车载端与服务端断了线而 RIoT 仍在驱动车辆——这些时候车可能已经在开，而「暂留」「尚未下发」在现场都会被读成「车不会走」，
/// 错在危险的一侧。也不写「等待人工确认」：开关开着的过渡期里会诱人提前去按确认。
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

    /// <summary>清桩：车载端收到的计划里没有等待点腿（<c>8005-agv-onboard-hmi#330</c>）。只说收到了什么，不说车动不动。</summary>
    public const string ClearingNoWaitingPointPlanStatus = "CLEARING_NO_WAITING_POINT_PLAN";

    /// <summary>
    /// 到站那一格的原始状态：空闲返回时 <see cref="EnRouteStatus"/>、<see cref="AtWaitingPointStatus"/>，清桩时
    /// <see cref="ClearingNoWaitingPointPlanStatus"/>、<see cref="ClearingEnRouteStatus"/>、<see cref="ClearingAtWaitingPointStatus"/>，
    /// 其余是空串。
    /// </summary>
    public static string Status(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        WireToGateMovementLeg? leg = WaitingPointLeg(journey);
        bool arrived = leg?.State == "ARRIVED";
        return WireToGateNonBusinessStop.Classify(journey) switch
        {
            WireToGateNonBusinessStopKind.IdleReturn => arrived ? AtWaitingPointStatus : EnRouteStatus,
            WireToGateNonBusinessStopKind.Clearing => leg is null
                ? ClearingNoWaitingPointPlanStatus
                : arrived ? ClearingAtWaitingPointStatus : ClearingEnRouteStatus,
            _ => string.Empty
        };
    }

    /// <summary>
    /// 到站那一格的文字：「空闲返回：前往等待点 &lt;站&gt;」或「在等待点 &lt;站&gt; 待命」；清桩时「清桩：未收到开往等待点的计划」、
    /// 「清桩：前往等待点 &lt;站&gt;」或「清桩：已到等待点 &lt;站&gt;」；其余是空串。
    /// </summary>
    /// <remarks>
    /// 空闲返回的用途已经说了、计划里当前那条腿还不是等待点（计划还没到或还是旧的）时，说不出站名，只写
    /// 「空闲返回：前往等待点」，不拿别的腿的站名顶上。清桩不这样写：没有等待点腿时只说没收到开往等待点的计划
    /// （见类注释）。
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
                ? "清桩：未收到开往等待点的计划"
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
