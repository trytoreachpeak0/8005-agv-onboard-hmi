using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The operator-facing wording of a <c>SublotRejected</c>, kept in one place (8005-agv-onboard-hmi#77).
/// </summary>
/// <remarks>
/// <para>
/// ADR-cross-0057 asks for the <b>real</b> reason in front of the operator, not a generic "not in the
/// list". The three codes the 2.0.0 candidate added for this message and the reused
/// <c>EXPECTED_BASKET_COUNT_MISMATCH</c>, <c>WORKLIST_REVISION_STALE</c> and <c>SLOT_INOPERABLE</c> each have their
/// own sentence.
/// </para>
/// <para>
/// <b>Any other code is shown as itself.</b> The registry lets the server grow the set, and a guessed
/// meaning is worse than none: the fallback says the reason is unrecognised and puts the raw code in
/// front of the operator, who can read it to whoever is on the other end of the phone.
/// </para>
/// </remarks>
public static class WireToGateSublotRejectionText
{
    public static string Reason(string reasonCode) => reasonCode switch
    {
        "SUBLOT_NOT_IN_DISPATCH_SCOPE" => "子批号不在本次派车范围内",
        "SUBLOT_BOX_COUNT_UNAVAILABLE" => "查不到该子批的箱数",
        "PACKAGE_CAPACITY_UNRESOLVED" => "该 PACKAGE 的花篮容量未登记或有冲突",
        "EXPECTED_BASKET_COUNT_MISMATCH" => "花篮数量与已预留仓位数不符",
        // An entry that reached the server after the stop had ended (8005-agv-onboard-hmi#199).
        WorklistRevisionStale => "本站作业已结束，不再接收扫码",
        // A slot was disabled after acceptance and this side no longer has enough free slots (control-server#754).
        SlotInoperable => "可用的空仓位不足，有仓位已被停用",
        _ => $"未识别的拒收原因（{reasonCode}）"
    };

    private const string WorklistRevisionStale = "WORKLIST_REVISION_STALE";

    private const string SlotInoperable = "SLOT_INOPERABLE";

    /// <summary>The reason line: which entry was refused, and why.</summary>
    public static string Describe(WireToGateSublotRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        return $"子批 {rejection.RejectedSublot} 被服务端拒收：{Reason(rejection.ReasonCode)}。";
    }

    /// <summary>The line for a <c>LoadCancellationAuthorization</c> the server refused.</summary>
    /// <remarks>
    /// <c>WORKLIST_REVISION_STALE</c> is a cancellation that arrived after the stop had ended
    /// (control-server#324): nothing is left to cancel, and the operator is told so, with the code kept for
    /// whoever is on the phone. <c>LOAD_CANCELLATION_BASKET_LOADED</c> (protocol 6.0.0 candidate, CP-0021 REQ-0211) is a load
    /// with a basket in: it is never cancelled, and the way to load fewer is 「剩下的不装」 (8005-agv-onboard-hmi#388). Every
    /// other code is shown as itself, as before.
    /// </remarks>
    public static string LoadCancellationRefusal(string reasonCode) => reasonCode switch
    {
        WorklistRevisionStale => $"服务端拒绝装货取消：本站作业已结束，无需再取消（{WorklistRevisionStale}）。",
        BasketLoaded => $"已经装进花篮，不能取消；要少装请按「剩下的不装」（{BasketLoaded}）。",
        _ => $"服务端拒绝装货取消：{reasonCode}。"
    };

    private const string BasketLoaded = "LOAD_CANCELLATION_BASKET_LOADED";

    /// <summary>What the operator can do next, which turns on whether the entry request was kept.</summary>
    /// <remarks>
    /// <c>SLOT_INOPERABLE</c> is the exception and turns on the code alone (onboard-hmi#366): scanning again is
    /// refused again for as long as the slot stays disabled, so the operator is told to cancel the demand and get
    /// maintenance, whether or not the entry request was kept.
    /// </remarks>
    public static string NextStep(string reasonCode, bool canEnterAgain) =>
        reasonCode == SlotInoperable
            ? "重扫也无法装入，请取消该需求并联系系统维护人员。"
            : canEnterAgain
                ? "请核对物料后重新扫码。"
                : "本站录入清单已变化，等待服务端新的录入请求。";
}
