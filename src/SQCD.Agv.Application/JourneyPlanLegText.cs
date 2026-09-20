namespace SQCD.Agv.Application;

/// <summary>
/// 计划腿列表的文案，唯一的一处（批次7-13，<c>8005-agv-onboard-hmi#134</c>）。
/// </summary>
/// <remarks>
/// 表里没有的值原样显示，不映射成最接近的已知值：协议升级带来新值时，操作员看到原始码比看到一个错的中文强。
/// <c>legType</c> 为 <c>null</c> 的腿（等待点、充电桩）不带方向，显示空串。
/// </remarks>
public static class JourneyPlanLegText
{
    private static readonly IReadOnlyDictionary<string, string> StopPurposeTexts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BUSINESS"] = "业务站",
            ["WAITING_POINT"] = "等待点",
            ["CHARGER"] = "充电桩"
        };

    private static readonly IReadOnlyDictionary<string, string> StateTexts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PLANNED"] = "待前往",
            ["ACTIVE"] = "前往中",
            ["ARRIVED"] = "已到达",
            ["COMPLETED"] = "已完成",
            ["BLOCKED"] = "受阻"
        };

    public static string StopPurpose(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return StopPurposeTexts.GetValueOrDefault(category, category);
    }

    public static string LegType(string? legType) => legType switch
    {
        "TO_PICKUP" => "取货",
        "TO_DROPOFF" => "卸货",
        null => string.Empty,
        _ => legType
    };

    public static string State(string state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return StateTexts.GetValueOrDefault(state, state);
    }
}
