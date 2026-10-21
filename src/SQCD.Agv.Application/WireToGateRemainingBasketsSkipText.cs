namespace SQCD.Agv.Application;

/// <summary>
/// The operator's words for 「剩下的不装」 on a multi-basket load (8005-agv-onboard-hmi#388, CP-0021 REQ-0367), kept in one
/// place so the button, its confirmation and every answer read the same.
/// </summary>
/// <remarks>
/// The confirmation is the user's sentence of 2026-10-21: what goes, what stays, and that the system lets go of the rest. No
/// ticket number and no "调度" on the screen; a reason code is shown in brackets after the sentence, for whoever is on the
/// phone, as the other refusals do.
/// </remarks>
public static class WireToGateRemainingBasketsSkipText
{
    /// <summary>The button: 「剩下的不装（已装 k/N 篮）」.</summary>
    public static string Caption(int loaded, int expected) => $"剩下的不装（已装 {loaded}/{expected} 篮）";

    /// <summary>The confirmation dialog's body.</summary>
    public static string Confirmation(int loaded, int expected) =>
        $"已装的 {loaded} 篮照常送走，剩下的 {expected - loaded} 篮留在机台，系统不再管。\n\n确认剩下的不装？";

    public const string ConfirmationTitle = "剩下的不装";

    /// <summary>Shown while the press waits for the server.</summary>
    public const string PendingText = "已申请剩下的不装，正在等服务端确认；确认前不开仓门。";

    /// <summary>The press went nowhere because what it was read from changed under the dialog.</summary>
    public const string ChangedText = "确认期间装货状态变了，这次「剩下的不装」没有发出。请看屏上当前提示，仍要少装时重新按。";

    /// <summary>Shown when the server agreed: the load ends with what is in.</summary>
    public static string AuthorizedText(int loaded) => $"服务端已同意剩下的不装：已装的 {loaded} 篮照常送走，其余仓门不再打开。";

    /// <summary>The server refused: why, and that loading goes on.</summary>
    public static string RejectionText(string reasonCode) =>
        $"服务端没有同意剩下的不装：{Reason(reasonCode)}（{reasonCode}）。装货照常继续，需要时可再按「剩下的不装」。";

    /// <summary>The request ended without effect before an answer came.</summary>
    public static string DroppedText(string reasonCode) =>
        $"这次「剩下的不装」没有生效（{reasonCode}），仓门状态未变。";

    /// <summary>The refusal codes of the wire notes in the operator's words; any other code is shown as itself.</summary>
    public static string Reason(string reasonCode) => reasonCode switch
    {
        "LOCK_NOT_CLOSED" => "有仓门没有确认锁好",
        "UNLOCK_OUTPUT_NOT_RESET" => "有仓门的开锁信号还没复位",
        "SLOT_STATE_UNKNOWN" => "有仓位状态读不到",
        "REMAINING_BASKETS_SKIP_NOTHING_LOADED" => "服务端看到这条任务还没有装进花篮",
        "SLOT_SET_INVALID" => "已装的仓位和服务端记录的不一致",
        "ACTION_NOT_ALLOWED_IN_STATE" => "这次装货现在不能少装",
        "DEMAND_NOT_CURRENT" => "这条任务已经不是本站当前的任务",
        "OPERATION_SESSION_MISMATCH" => "本站作业已经变了",
        _ => "未识别的原因"
    };
}
