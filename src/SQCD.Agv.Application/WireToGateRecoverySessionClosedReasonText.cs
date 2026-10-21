namespace SQCD.Agv.Application;

/// <summary>
/// 服务端恢复会话关闭时，那条操作员事件写的话，唯一的一处（批次8-14，<c>8005-agv-onboard-hmi#216</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 协议 3.0.0 给 <c>ExceptionRecoverySessionSnapshot</c> 加了必填可空的 <c>closedReason</c>：会话不是 <c>CLOSED</c>
/// 时为 <c>null</c>；恢复动作的结果没能闭环而关闭时是 <c>RECOVERY_ACTION_RESULT_NOT_RECONCILED</c>。在此之前车上只看得到
/// 「会话关闭了」，看不到为什么关（program#115）——而这一种关闭恰恰不是处置完成：需求与旅程仍阻断，要管理员重新发起恢复。
/// </para>
/// <para>
/// <b>表里没有的值原样显示</b>，与 <c>OnboardCommandRejectionText</c>、<c>WireToGateStopEndedReasonText</c> 同一规矩：
/// 入站校验按错误码注册表收，今天服务端只发表里这两个，别的码也是合法取值；猜出来的意思比没有更糟。
/// 原因为 <c>null</c> 时照旧写「服务端恢复会话已关闭。」，不编原因。
/// </para>
/// </remarks>
public static class WireToGateRecoverySessionClosedReasonText
{
    public const string NoReasonText = "服务端恢复会话已关闭。";

    private static readonly IReadOnlyDictionary<string, string> ReasonTexts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RECOVERY_ACTION_RESULT_NOT_RECONCILED"] =
                "服务端恢复会话已关闭：恢复动作结果未闭环，需求与旅程仍阻断，请管理员重新发起恢复。",
            // 恢复授权到期、被收回或服务端重启：服务端停收新动作、已发出的动作对账完再关（CP-0022、ADR-cross-0066 决策 5，
            // 8005-agv-onboard-hmi#391）。动作做成、结果回来之后照样以这个码关（control-server#797 返工），所以这句只说
            // 「结束了」，不说没做成；做成了的，操作记录里在它前面已经有「……已完成」那一条。不写票号与「调度」，写找谁。
            ["RECOVERY_AUTHENTICATION_FAILED"] = "本次恢复已结束：恢复授权已失效。需要继续请联系系统维护人员重新开放。"
        };

    /// <summary>收到 <c>CLOSED</c> 快照时操作员事件的整句。</summary>
    public static string Describe(string? closedReason) =>
        closedReason is null
            ? NoReasonText
            : ReasonTexts.GetValueOrDefault(closedReason, $"服务端恢复会话已关闭（{closedReason}）。");
}
