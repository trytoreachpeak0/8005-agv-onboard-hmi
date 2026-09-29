namespace SQCD.Agv.Application;

/// <summary>
/// 清单为空时到站那一格写的「本站结束原因」，唯一的一处（批次8-12，<c>8005-agv-onboard-hmi#214</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 协议 3.0.0 给 <c>CurrentStopWorklistSnapshot</c> 加了必填的 <c>stopEndedReason</c>：清单项非空时为 <c>null</c>，
/// 清单项为空时是七个值之一。在此之前清单变空只能写一句笼统的「无待处理任务」，操作员分不清是做完了、超时了还是被取消了。
/// </para>
/// <para>
/// <b>表里没有的值原样显示</b>，与 <c>OnboardCommandRejectionText</c> 同一规矩：猜出来的意思比没有更糟。入站校验按
/// schema 的七个值收，所以今天表外的值到不了这里；这一条是给下一次枚举扩展留的，届时文案表没跟上也不会把新原因说成别的。
/// </para>
/// <para>
/// 原因为空时（车载端从 2.0.0 升上来，日志里重放的旧清单没有这个字段）照旧写「无待处理任务」，不编原因。
/// </para>
/// </remarks>
public static class WireToGateStopEndedReasonText
{
    public const string NoReasonText = "无待处理任务";

    private static readonly IReadOnlyDictionary<string, string> ReasonTexts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["COMPLETED"] = "本站作业已完成",
            ["STATION_DEADLINE_EXPIRED"] = "本站因超时结束",
            ["LOAD_CANCELLED"] = "本站因装货取消结束",
            ["LOAD_COMPENSATED"] = "本站因补偿清空结束",
            ["CARGO_HANDED_OFF"] = "本站因货物交接结束",
            ["DEMAND_RELEASED"] = "本站因任务释放结束",
            ["TRIP_TERMINATED"] = "本站因行程终止结束"
        };

    /// <summary>清单为空时站名后面那半句。</summary>
    public static string Describe(string? stopEndedReason) =>
        stopEndedReason is null
            ? NoReasonText
            : ReasonTexts.GetValueOrDefault(stopEndedReason, stopEndedReason);
}
