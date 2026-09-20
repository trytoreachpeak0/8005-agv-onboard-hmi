using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// 旅程事实一行里的两段文字：本站取货还是卸货，以及本站清单项的任务类型（批次6-03，
/// <c>8005-agv-onboard-hmi#115</c>）；以及清单列表里每一行自己的这两段（批次7-13，
/// <c>8005-agv-onboard-hmi#134</c>）。
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
/// <para>
/// <b>一站多条清单项时，顶栏只显示全部清单项一致的那一段</b>，不一致时是空串，不挑一条来代表本站。
/// 方向与任务类型各自判断：同是取货、任务类型不同时，顶栏仍显示「取货」。每条清单项自己的方向与任务类型
/// 在清单列表的那一行上。只有一条清单项时，两段与批次 6 逐字相同（G3 场景按 <c>StopDirection</c>、
/// <c>TaskType</c> 两个 AutomationId 读它们）。
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
    /// 本站清单项的任务类型文案：全部清单项的任务类型相同时是它，没有清单项或各项不同时是空串。
    /// </summary>
    public static string TaskTypeText(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        return Shared(journey.CurrentStopWorklist?.Items ?? [], ItemTaskTypeText);
    }

    /// <summary>
    /// 本站取货还是卸货：清单已下发时取全部清单项共同的 <c>stopRole</c>，否则取计划里当前那条腿的
    /// <c>legType</c>；说不出一个方向（含清单项之间不一致）时是空串。
    /// </summary>
    /// <remarks>
    /// 到站后清单是本站的权威，所以它优先；还在路上、清单没下发时，按序号第一条未完成的腿就是正在去的那一站。
    /// 清单已下发但没有清单项（同一行显示「无待处理任务」）时方向为空，不退回去读计划腿，否则会自相矛盾。
    /// <c>legType</c> 为 <c>null</c> 的腿（等待点、充电桩）不带方向。
    /// </remarks>
    public static string DirectionText(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        if (journey.CurrentStopWorklist is not null)
        {
            return Shared(journey.CurrentStopWorklist.Items, ItemDirectionText);
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

    /// <summary>清单一行的方向：只看这一条的 <c>stopRole</c>。</summary>
    public static string ItemDirectionText(WireToGateWorklistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.StopRole switch
        {
            "PICKUP" => PickupText,
            "DROPOFF" => DropoffText,
            _ => string.Empty
        };
    }

    /// <summary>清单一行的任务类型：只看这一条的 <c>workType</c>，文案表里没有的显示「未知」。</summary>
    public static string ItemTaskTypeText(WireToGateWorklistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return TaskTypeTexts.GetValueOrDefault(item.WorkType, UnknownTaskTypeText);
    }

    private const string PickupText = "取货";

    private const string DropoffText = "卸货";

    /// <summary>
    /// 全部清单项在这一段上的共同文字；没有清单项或有任何一条不同时是空串。
    /// </summary>
    private static string Shared(
        IReadOnlyList<WireToGateWorklistItem> items,
        Func<WireToGateWorklistItem, string> text)
    {
        string[] distinct = [.. items.Select(text).Distinct(StringComparer.Ordinal)];
        return distinct is [var only] ? only : string.Empty;
    }
}
