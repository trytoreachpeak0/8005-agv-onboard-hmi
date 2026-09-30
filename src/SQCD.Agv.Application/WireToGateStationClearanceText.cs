using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// 人工清桩确认入口的文案与给 UIA 的原始状态，唯一的一处（批次9-16，<c>8005-agv-onboard-hmi#221</c>；向量
/// <c>CV-MANUAL-STATION-CLEARANCE</c>）。
/// </summary>
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

    /// <summary>确认对话框的正文：原桩、确认人，以及这一次是不是对结果未知的那一次的重新提交。</summary>
    public static string ConfirmationText(WireToGateStationClearancePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return string.Empty;
    }

    /// <summary>入口不出现时要写明的原因；不必说明时是空串。</summary>
    public static string NoticeText(WireToGateStationClearanceView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return string.Empty;
    }

    /// <summary>上一次确认的结果那一行；没有时是空串。</summary>
    public static string StatusText(WireToGateStationClearanceOutcome? outcome) => string.Empty;

    /// <summary>结果那一行给 UIA 的 ItemStatus（AutomationId <c>StationClearanceStatus</c>）；没有时是空串。</summary>
    public static string Status(WireToGateStationClearanceOutcome? outcome) => string.Empty;
}
