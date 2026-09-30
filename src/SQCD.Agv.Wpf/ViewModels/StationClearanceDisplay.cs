using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 人工清桩确认入口在界面上的全部内容，整值替换（批次9-16，<c>8005-agv-onboard-hmi#221</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 一个记录而不是六个并排的属性：按钮亮不亮、对话框写什么、旁边那两行说什么，出自同一份
/// <see cref="WireToGateStationClearanceView"/>，界面读到的永远是同一时刻的一组，不会出现按钮已经换了原桩、
/// 对话框还写着上一个的中间态。读它的判据也因此只需等这一个属性。
/// </para>
/// <para>
/// <see cref="Prompt"/> 就是操作员在对话框里读到的那一份；按下确认时原样交回去，业务服务核对它仍是当前这一份才发。
/// </para>
/// </remarks>
public sealed record StationClearanceDisplay(
    bool CanConfirm,
    WireToGateStationClearancePrompt? Prompt,
    string ConfirmationText,
    bool HasNotice,
    string NoticeText,
    bool HasStatus,
    string StatusText,
    string Status)
{
    public static StationClearanceDisplay Empty { get; } =
        new(false, null, string.Empty, false, string.Empty, false, string.Empty, string.Empty);
}
