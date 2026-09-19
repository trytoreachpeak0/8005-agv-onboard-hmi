using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// 旅程事实一行里的两段文字：本站取货还是卸货，以及本站清单项的任务类型（批次6-03，
/// <c>8005-agv-onboard-hmi#115</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>两段都只读服务端下发的字段，不推断。</b>方向只看清单项的 <c>stopRole</c> 与计划腿的
/// <c>legType</c>，不看任务类型：<c>STAGING_TO_WIRE</c> 在派工待送点取货、在 AREA 机台卸货，和
/// <c>WIRE_TO_GATE</c> 恰好相反，按任务类型推方向就会把反向旅程显示反（向量
/// <c>CV-REVERSED-DIRECTION-JOURNEY</c> 的 <c>DISPLAY_DIRECTION_AS_PLANNED</c>）。任务类型只看清单项的
/// <c>workType</c>：没有清单项就不显示，不从计划、站名、站点功能或 <c>blockingFacts</c> 推（向量
/// <c>CV-TASK-TYPE-ADMISSION-FAIL-CLOSED</c> 的 <c>NEVER_INFER_UNBOUND_TASK_TYPE</c>）。
/// </para>
/// <para>
/// <c>PublicStationFunction</c> 不上界面：产品不维护它，v2 服务端下发时保持为空（规格第 5.3 节）。
/// 准入阻断原因也不上界面：它只在服务端与看板，不经 <c>blockingFacts</c> 下发（同一节）。
/// </para>
/// </remarks>
public static class WireToGateStopFacts
{
    /// <summary>文案表里没有的 <c>workType</c> 显示这个，不映射成任何已知类型。</summary>
    public const string UnknownTaskTypeText = "未知";

    /// <summary>
    /// 六类 MES 运输任务的中文文案，唯一的一处。文案取自 MES 统一查询六个分支的路线注释。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> TaskTypeTexts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DIE_TO_WIRE_STAGING"] = "装片→焊线待送",
            ["DIE_TO_OVEN"] = "装片→烘箱",
            ["WIRE_TO_GATE"] = "焊线→质检关卡",
            ["WIRE_TO_OPTICAL"] = "焊线→三光",
            ["STAGING_TO_WIRE"] = "待送→焊线机台",
            ["WIRE_TO_NITROGEN"] = "焊线→氮气柜"
        };

    /// <summary>
    /// 本站清单项的任务类型文案；没有清单项时是空串。
    /// </summary>
    public static string TaskTypeText(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        return CurrentItem(journey) is { } item
            ? TaskTypeTexts.GetValueOrDefault(item.WorkType, UnknownTaskTypeText)
            : string.Empty;
    }

    /// <summary>
    /// 本站取货还是卸货：有清单项时取它的 <c>stopRole</c>，否则取计划里当前那条腿的 <c>legType</c>；
    /// 两者都说不出方向时是空串。
    /// </summary>
    /// <remarks>
    /// 到站后清单项是本站的权威，所以它优先；还在路上、清单没下发时，按序号第一条未完成的腿就是正在去的那一站。
    /// <c>legType</c> 为 <c>null</c> 的腿（等待点、充电桩）不带方向。
    /// </remarks>
    public static string DirectionText(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        if (CurrentItem(journey) is { } item)
        {
            return item.StopRole switch
            {
                "PICKUP" => PickupText,
                "DROPOFF" => DropoffText,
                _ => string.Empty
            };
        }

        WireToGateMovementLeg? currentLeg = journey.UpcomingStopPlan?.Legs
            .OrderBy(leg => leg.Sequence)
            .FirstOrDefault(leg => leg.State != "COMPLETED");
        return currentLeg?.LegType switch
        {
            "TO_PICKUP" => PickupText,
            "TO_DROPOFF" => DropoffText,
            _ => string.Empty
        };
    }

    private const string PickupText = "取货";

    private const string DropoffText = "卸货";

    /// <summary>
    /// 本站唯一的清单项。多条清单项是批次 7 的事，入站校验今天只收一条；这里遇到多条也不挑一条来显示。
    /// </summary>
    private static WireToGateWorklistItem? CurrentItem(WireToGateJourneySnapshot journey) =>
        journey.CurrentStopWorklist?.Items is [var only] ? only : null;
}
