using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 现场确认充不上入口在界面上的全部内容，整值替换（批次9-17，<c>8005-agv-onboard-hmi#222</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 一个记录而不是一组并排的属性，理由与 <see cref="StationClearanceDisplay"/> 相同：按钮、对话框正文、说明与结果出自同一份
/// <see cref="WireToGateUnableToChargeView"/>，界面读到的永远是同一时刻的一组，判据也只需等这一个属性。
/// </para>
/// <para>
/// <see cref="Options"/> 每项是一个按钮：操作员在现场看到的一种情况。按钮上的 <see cref="UnableToChargeOption.Prompt"/>
/// 就是对话框依据、按下确认后原样交回去核对的那一份；重新提交时只有一项，即上一次结果未知的那一种情况。
/// </para>
/// <para>
/// 相等按内容比（选项逐项比），所以刷新得到同样内容时不触发属性变更，按钮不会在操作员手下被重建。
/// </para>
/// </remarks>
public sealed record UnableToChargeDisplay(
    IReadOnlyList<UnableToChargeOption> Options,
    bool HasNotice,
    string NoticeText,
    bool HasStatus,
    string StatusText,
    string Status)
{
    public static UnableToChargeDisplay Empty { get; } =
        new([], false, string.Empty, false, string.Empty, string.Empty);

    public bool CanConfirm => Options.Count > 0;

    public bool Equals(UnableToChargeDisplay? other) =>
        other is not null
        && Options.SequenceEqual(other.Options)
        && HasNotice == other.HasNotice
        && NoticeText == other.NoticeText
        && HasStatus == other.HasStatus
        && StatusText == other.StatusText
        && Status == other.Status;

    public override int GetHashCode() =>
        HashCode.Combine(Options.Count, NoticeText, StatusText, Status);
}

/// <summary>
/// 一种现场情况的按钮：给操作员看的短句、对话框正文，以及按下确认时交回去的那一份提示。
/// </summary>
public sealed record UnableToChargeOption(
    WireToGateUnableToChargePrompt Prompt,
    string Label,
    string ConfirmationText,
    string AutomationId);
