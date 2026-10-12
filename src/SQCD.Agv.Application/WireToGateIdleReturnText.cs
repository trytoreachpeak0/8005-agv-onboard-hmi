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
/// <b>清桩中、计划里还没有等待点腿时，只陈述这件事，不预言车动不动</b>（<c>8005-agv-onboard-hmi#330</c>）。服务端只在清桩开往等待点的单被
/// RIoT 确认之后才发「原桩腿 <c>COMPLETED</c> + 等待点腿」那份计划（control-server
/// <c>JourneyRuntimeEngine.ClearanceMove</c>，<c>PublishClearanceMoveSnapshotsAsync</c>）；开关
/// <c>ClearanceToWaitingPointEnabled</c> 关着、开着但还没建单或单还没确认、移动撤回之后，计划里都只有充电桩那条腿。开关本身
/// 不下发，这几种情形车载端分不开。所以那时写「清桩：尚未下发开往等待点的任务」，<see cref="Status"/> 报
/// <see cref="ClearingNoWaitingPointTaskStatus"/>。不写「前往等待点」：开关关着时车不会自己走，现场会白等。也不写「暂留原位」
/// 一类的话：开关开着时，单已发出而服务端对账还没确认（<c>ResultUnknown</c>）的那段里车可能已经在开，这段多长没有上限，
/// 说车不动正是危险的那一侧。也不写「等待人工确认」：开关开着的过渡期里会诱人提前去按确认。
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

    /// <summary>清桩：计划里还没有等待点腿，服务端尚未下发开往等待点的任务（<c>8005-agv-onboard-hmi#330</c>）。不说车动不动。</summary>
    public const string ClearingNoWaitingPointTaskStatus = "CLEARING_NO_WAITING_POINT_TASK";

    /// <summary>
    /// 到站那一格的原始状态：空闲返回时 <see cref="EnRouteStatus"/>、<see cref="AtWaitingPointStatus"/>，清桩时
    /// <see cref="ClearingNoWaitingPointTaskStatus"/>、<see cref="ClearingEnRouteStatus"/>、<see cref="ClearingAtWaitingPointStatus"/>，
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
                ? ClearingNoWaitingPointTaskStatus
                : arrived ? ClearingAtWaitingPointStatus : ClearingEnRouteStatus,
            _ => string.Empty
        };
    }

    /// <summary>
    /// 到站那一格的文字：「空闲返回：前往等待点 &lt;站&gt;」或「在等待点 &lt;站&gt; 待命」；清桩时「清桩：尚未下发开往等待点的任务」、
    /// 「清桩：前往等待点 &lt;站&gt;」或「清桩：已到等待点 &lt;站&gt;」；其余是空串。
    /// </summary>
    /// <remarks>
    /// 空闲返回的用途已经说了、计划里当前那条腿还不是等待点（计划还没到或还是旧的）时，说不出站名，只写
    /// 「空闲返回：前往等待点」，不拿别的腿的站名顶上。清桩不这样写：没有等待点腿时只说服务端还没下发去等待点的任务
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
                ? "清桩：尚未下发开往等待点的任务"
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
