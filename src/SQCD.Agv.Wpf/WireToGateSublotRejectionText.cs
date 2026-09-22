using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The operator-facing wording of a <c>SublotRejected</c>, kept in one place (8005-agv-onboard-hmi#77).
/// </summary>
/// <remarks>
/// <para>
/// ADR-cross-0057 asks for the <b>real</b> reason in front of the operator, not a generic "not in the
/// list". The three codes the 2.0.0 candidate added for this message and the reused
/// <c>EXPECTED_BASKET_COUNT_MISMATCH</c> each have their own sentence.
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
        _ => $"未识别的拒收原因（{reasonCode}）"
    };

    /// <summary>The reason line: which entry was refused, and why.</summary>
    public static string Describe(WireToGateSublotRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        return $"子批 {rejection.RejectedSublot} 被服务端拒收：{Reason(rejection.ReasonCode)}。";
    }

    /// <summary>The line for a <c>LoadCancellationAuthorization</c> the server refused.</summary>
    public static string LoadCancellationRefusal(string reasonCode) =>
        $"服务端拒绝装货取消：{reasonCode}。";

    /// <summary>What the operator can do next, which turns on whether the entry request was kept.</summary>
    public static string NextStep(bool canEnterAgain) =>
        canEnterAgain
            ? "请核对物料后重新扫码。"
            : "本站录入清单已变化，等待服务端新的录入请求。";
}
