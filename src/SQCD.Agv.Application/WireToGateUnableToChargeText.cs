using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// 现场确认充不上入口的文案与给 UIA 的原始状态，唯一的一处（批次9-17，<c>8005-agv-onboard-hmi#222</c>；向量
/// <c>CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>对话框正文把 <see cref="WireToGateUnableToChargePrompt"/> 的每一项都写出来</b>：充电桩、确认人、观察到的情况，以及
/// 这一次是不是重新提交。按下确认时交回去核对的就是那一份记录，正文少写一项，操作员就确认了一样他没读到的东西
/// （<c>8005-agv-onboard-hmi#216</c> 审查的那一类）。<c>WireToGateUnableToChargeTests</c> 逐项改、逐项要求正文跟着变。
/// </para>
/// <para>
/// <b>服务端的决定只翻译、不解释成本地动作。</b>结果一行写出 <c>chargingPolicyDecision</c> 的中文，车辆接下来去哪仍以服务端
/// 下发的为准（<c>NEVER_DECIDE_CHARGING_POLICY_LOCALLY</c>）。拒绝原因照服务端下发的码原样显示，不翻译、不猜。
/// </para>
/// </remarks>
public static class WireToGateUnableToChargeText
{
    /// <summary>服务端已确认（记录了现场确认）。</summary>
    public const string ConfirmedStatus = "CONFIRMED";

    /// <summary>服务端拒绝。</summary>
    public const string RejectedStatus = "REJECTED";

    /// <summary>等应答超时或断线，结果未知。</summary>
    public const string UnknownStatus = "UNKNOWN";

    /// <summary>服务端以 <c>ProtocolProblem</c> 答这条请求：读到了，没有受理。</summary>
    public const string NotAcceptedStatus = "NOT_ACCEPTED";

    /// <summary>按钮上的短句：操作员在现场看到的情况，每个取值一句。</summary>
    public static string ConditionLabel(string observedCondition) => observedCondition switch
    {
        "CHARGER_UNREACHABLE" => "车到不了充电桩",
        "CHARGER_OCCUPIED" => "充电桩被占用",
        "CONNECTION_FAILED" => "接不上充电",
        "CHARGER_FAULT" => "充电桩故障",
        _ => observedCondition
    };

    /// <summary>对话框里对这个取值的一句说明。</summary>
    public static string ConditionDescription(string observedCondition) => observedCondition switch
    {
        "CHARGER_UNREACHABLE" => "车辆到不了充电桩（例如通道被挡、到不了停靠位置）。",
        "CHARGER_OCCUPIED" => "充电桩被其他车辆或物品占着，车辆停不上去。",
        "CONNECTION_FAILED" => "车辆已在充电桩上，试过开始充电，但充电连接没有建立。",
        "CHARGER_FAULT" => "车辆已在充电桩上，充电桩故障，充电没有开始。",
        _ => observedCondition
    };

    /// <summary>服务端决定的中文；<c>null</c> 时是空串（不显示决定）。</summary>
    public static string DecisionText(string? chargingPolicyDecision) => chargingPolicyDecision switch
    {
        null => string.Empty,
        "RETRY_LATER" => "稍后重试",
        "MANUAL_CHARGING_HOLD" => "转人工充电",
        "REASSIGN_CHARGER" => "改派其它充电桩",
        _ => chargingPolicyDecision
    };

    /// <summary>
    /// 确认对话框的正文：充电桩、确认人、观察到的情况，以及这一次是不是对结果未知的那一次的重新提交。
    /// </summary>
    public static string ConfirmationText(WireToGateUnableToChargePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        string resubmission = prompt.ResubmittedConfirmationRequestId is null
            ? string.Empty
            : $"\n\n上一次提交的结果未知。本次以同一确认号、同样的内容重新提交（确认号 {prompt.ResubmittedConfirmationRequestId}）。";
        return $"请由授权维护人员确认：本车在充电桩 {prompt.ChargerStationId} 没有充上电。\n\n"
            + $"现场情况：{ConditionLabel(prompt.ObservedCondition)}（{prompt.ObservedCondition}）——"
            + ConditionDescription(prompt.ObservedCondition) + "\n\n"
            + $"确认人：{prompt.OperatorId}\n\n"
            + "系统只把现场情况提交给服务端，不在本地改变充电、装货或车辆去向；受不受理、接下来怎么充电由服务端决定。"
            + resubmission
            + "\n\n是否确认？";
    }

    /// <summary>入口不出现时要写明的原因；不必说明时是空串。</summary>
    /// <remarks>
    /// 只有两种情形说明：充电中却说不出当前是哪个充电桩，和已经提交、正在等应答。不在充电用途上、或者面前不是已验证的
    /// 维护人员时什么都不写——这个入口本来就不是给他的。
    /// </remarks>
    public static string NoticeText(WireToGateUnableToChargeView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Unavailability switch
        {
            WireToGateUnableToChargeUnavailability.StationUnknown =>
                "车辆在充电用途上，但服务端下发的计划里当前腿不是充电桩，现场确认充不上入口不可用。",
            WireToGateUnableToChargeUnavailability.AwaitingAnswer => "现场确认充不上已提交，正在等待服务端应答。",
            _ => string.Empty
        };
    }

    /// <summary>上一次确认的结果那一行；没有时是空串。</summary>
    public static string StatusText(WireToGateUnableToChargeOutcome? outcome)
    {
        if (outcome is null)
        {
            return string.Empty;
        }

        string decision = DecisionText(outcome.ChargingPolicyDecision);
        string decisionSentence = decision.Length == 0 ? string.Empty : $"服务端决定：{decision}。";
        string subject = $"充电桩 {outcome.ChargerStationId}，{ConditionLabel(outcome.ObservedCondition)}";
        return outcome.Kind switch
        {
            WireToGateUnableToChargeOutcomeKind.Confirmed =>
                $"服务端已记录现场确认（{subject}）。{decisionSentence}车辆接下来怎么走以服务端下发的为准。",
            WireToGateUnableToChargeOutcomeKind.Rejected =>
                $"服务端未确认充不上（{subject}）：{outcome.ReasonCode ?? "未给出原因"}。{decisionSentence}"
                + "车辆接下来怎么走以服务端下发的为准。",
            WireToGateUnableToChargeOutcomeKind.NotAccepted =>
                $"服务端没有受理现场确认（{subject}）：{outcome.ReasonCode ?? "未给出原因"}。再次提交会是一次新的确认。",
            _ => $"现场确认（{subject}）结果未知，请查看车辆状态后再决定是否重新提交。"
                + (outcome.ReasonCode is null ? string.Empty : $"原因：{outcome.ReasonCode}。")
        };
    }

    /// <summary>结果那一行给 UIA 的 ItemStatus（AutomationId <c>UnableToChargeStatus</c>）；没有时是空串。</summary>
    public static string Status(WireToGateUnableToChargeOutcome? outcome) => outcome?.Kind switch
    {
        null => string.Empty,
        WireToGateUnableToChargeOutcomeKind.Confirmed => ConfirmedStatus,
        WireToGateUnableToChargeOutcomeKind.Rejected => RejectedStatus,
        WireToGateUnableToChargeOutcomeKind.NotAccepted => NotAcceptedStatus,
        _ => UnknownStatus
    };
}
