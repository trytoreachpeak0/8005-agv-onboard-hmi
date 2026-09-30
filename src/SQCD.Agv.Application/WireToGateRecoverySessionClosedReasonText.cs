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
/// 入站校验按错误码注册表收，今天服务端只发上面那一个，别的码也是合法取值；猜出来的意思比没有更糟。
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
                "服务端恢复会话已关闭：恢复动作结果未闭环，需求与旅程仍阻断，请管理员重新发起恢复。"
        };

    /// <summary>收到 <c>CLOSED</c> 快照时操作员事件的整句。</summary>
    public static string Describe(string? closedReason) =>
        closedReason is null
            ? NoReasonText
            : ReasonTexts.GetValueOrDefault(closedReason, $"服务端恢复会话已关闭（{closedReason}）。");
}
