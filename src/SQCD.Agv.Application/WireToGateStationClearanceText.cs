using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// 人工清桩确认入口的文案与给 UIA 的原始状态，唯一的一处（批次9-16，<c>8005-agv-onboard-hmi#221</c>；向量
/// <c>CV-MANUAL-STATION-CLEARANCE</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>对话框正文把 <see cref="WireToGateStationClearancePrompt"/> 的每一项都写出来。</b>按下确认时交回去核对的就是那一份
/// 记录：原桩、确认人、这一次是不是重新提交。正文少写一项，操作员就确认了一样他没读到的东西
/// （<c>8005-agv-onboard-hmi#216</c> 审查的那一类）。<c>WireToGateStationClearanceTests</c> 逐项改、逐项要求正文跟着变。
/// </para>
/// <para>
/// <see cref="Status"/> 是结果那一行给 UIA 的原始值（AutomationId <c>StationClearanceStatus</c> 的
/// <c>ItemStatus</c>），判据按它比，不必比中文全文。拒绝原因照服务端下发的码原样显示，不翻译、不猜。
/// </para>
/// </remarks>
public static class WireToGateStationClearanceText
{
    /// <summary>服务端已确认，且站点已释放。</summary>
    public const string ConfirmedReleasedStatus = "CONFIRMED_STATION_RELEASED";

    /// <summary>服务端已确认，站点尚未释放。</summary>
    public const string ConfirmedNotReleasedStatus = "CONFIRMED_STATION_NOT_RELEASED";

    /// <summary>服务端拒绝。</summary>
    public const string RejectedStatus = "REJECTED";

    /// <summary>等应答超时或断线，结果未知。</summary>
    public const string UnknownStatus = "UNKNOWN";

    /// <summary>服务端以 <c>ProtocolProblem</c> 答这条请求：读到了，没有受理。</summary>
    public const string NotAcceptedStatus = "NOT_ACCEPTED";

    /// <summary>确认对话框的正文：原桩、确认人，以及这一次是不是对结果未知的那一次的重新提交。</summary>
    public static string ConfirmationText(WireToGateStationClearancePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        string resubmission = prompt.ResubmittedConfirmationRequestId is null
            ? string.Empty
            : $"\n\n上一次提交的结果未知。本次以同一确认号重新提交（确认号 {prompt.ResubmittedConfirmationRequestId}）。";
        return $"请由授权维护人员确认：车辆已移至安全位置，原充电桩 {prompt.StationId} 已腾空。\n\n"
            + $"确认人：{prompt.OperatorId}\n\n"
            + "系统只向服务端提交清桩确认，不在本地释放站点，不开仓门；充电桩是否释放以服务端的决定为准。"
            + resubmission
            + "\n\n是否确认？";
    }

    /// <summary>入口不出现时要写明的原因；不必说明时是空串。</summary>
    /// <remarks>
    /// 只有两种情形说明：清桩中却说不出是哪个桩，和已经提交、正在等应答。不在清桩中、或者面前不是已验证的维护人员时
    /// 什么都不写——这个入口本来就不是给他的。
    /// </remarks>
    public static string NoticeText(WireToGateStationClearanceView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Unavailability switch
        {
            WireToGateStationClearanceUnavailability.StationUnknown =>
                "车辆在清桩中，但服务端下发的计划里没有可确定的原充电桩（需要恰好一个充电桩停靠），人工清桩确认入口不可用。",
            WireToGateStationClearanceUnavailability.AwaitingAnswer => "清桩确认已提交，正在等待服务端应答。",
            _ => string.Empty
        };
    }

    /// <summary>上一次确认的结果那一行；没有时是空串。</summary>
    public static string StatusText(WireToGateStationClearanceOutcome? outcome) => outcome switch
    {
        null => string.Empty,
        { Kind: WireToGateStationClearanceOutcomeKind.Confirmed, StationReleased: true } =>
            $"服务端已确认清桩（充电桩 {outcome.StationId}），站点已释放。车辆与站点状态以服务端下发的为准。",
        { Kind: WireToGateStationClearanceOutcomeKind.Confirmed } =>
            $"服务端已确认清桩（充电桩 {outcome.StationId}），站点尚未释放。车辆与站点状态以服务端下发的为准。",
        { Kind: WireToGateStationClearanceOutcomeKind.Rejected } =>
            $"服务端拒绝清桩确认（充电桩 {outcome.StationId}）：{outcome.ReasonCode ?? "未给出原因"}。站点保持原状态。",
        { Kind: WireToGateStationClearanceOutcomeKind.NotAccepted, ClearanceEnded: true } =>
            $"服务端没有受理清桩确认（充电桩 {outcome.StationId}）：{outcome.ReasonCode ?? "未给出原因"}。{ClearanceEndedSentence}",
        { Kind: WireToGateStationClearanceOutcomeKind.NotAccepted } =>
            $"服务端没有受理清桩确认（充电桩 {outcome.StationId}）：{outcome.ReasonCode ?? "未给出原因"}。站点保持原状态，再次提交会是一次新的确认。",
        { ClearanceEnded: true } =>
            $"服务端已结束这次清桩，入口已关闭，这次清桩确认（充电桩 {outcome.StationId}）的结果未知。"
            + (outcome.ReasonCode is null ? string.Empty : $"原因：{outcome.ReasonCode}。"),
        _ => $"清桩确认（充电桩 {outcome.StationId}）结果未知，请查看车辆状态后再决定是否重新提交。"
            + (outcome.ReasonCode is null ? string.Empty : $"原因：{outcome.ReasonCode}。")
    };

    /// <summary>
    /// 等应答期间服务端已结束这次清桩时的那一句：入口已关闭，没法重新提交，所以不叫操作员去重新提交。
    /// </summary>
    public const string ClearanceEndedSentence = "服务端已结束这次清桩，入口已关闭。";

    /// <summary>结果那一行给 UIA 的 ItemStatus（AutomationId <c>StationClearanceStatus</c>）；没有时是空串。</summary>
    public static string Status(WireToGateStationClearanceOutcome? outcome) => outcome switch
    {
        null => string.Empty,
        { Kind: WireToGateStationClearanceOutcomeKind.Confirmed, StationReleased: true } => ConfirmedReleasedStatus,
        { Kind: WireToGateStationClearanceOutcomeKind.Confirmed } => ConfirmedNotReleasedStatus,
        { Kind: WireToGateStationClearanceOutcomeKind.Rejected } => RejectedStatus,
        { Kind: WireToGateStationClearanceOutcomeKind.NotAccepted } => NotAcceptedStatus,
        _ => UnknownStatus
    };
}
