using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 「出发」入口在界面上的全部内容，整值替换（待送点 <c>8005-agv-onboard-hmi#349</c>，机台取货 <c>8005-agv-onboard-hmi#352</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 一个记录而不是一组并排的属性，理由与 <see cref="StationClearanceDisplay"/> 相同：按钮出不出现、按不按得动、确认框写什么、
/// 旁边那几行说什么，出自同一份 <see cref="WireToGateCurrentStopDepartureView"/> 与同一次仓位读数，界面读到的永远是同一时刻的一组。
/// </para>
/// <para>
/// <see cref="Prompt"/> 就是确认框所依据的那一份；确认后原样交回业务服务，它核对仍是当前这一份才发。
/// </para>
/// </remarks>
/// <param name="IsShown">入口在不在屏上：待送点取货停靠，以及本站已装完的机台取货停靠。</param>
/// <param name="CanPress">按不按得动。</param>
/// <param name="Notice">按不动时旁边那一句；按得动时为空。</param>
/// <param name="StatusText">上一次按键现在的状况：待处理、已受理、或拒绝原因；没有时为空。</param>
/// <param name="Status">给 UIA 的原始值：<c>PENDING</c>／<c>ACCEPTED</c>／拒绝码。</param>
/// <param name="SummaryText">待送点摘要：已装篮数、前后仓空仓、待装条数；机台取货为空。</param>
/// <param name="NoticeCode">给 UIA 的按不了的原因原始值（枚举名）；按得了或入口不出现时为空（审查 L4）。</param>
/// <param name="SummaryCode">给 UIA 的摘要原始值，形如 <c>LOADED=1;FRONT_EMPTY=3;REAR_EMPTY=4;UNKNOWN=0;PENDING=2</c>（审查 L4）。</param>
public sealed record CurrentStopDepartureDisplay(
    bool IsShown,
    bool CanPress,
    WireToGateCurrentStopDeparturePrompt? Prompt,
    string ConfirmationText,
    bool HasNotice,
    string Notice,
    bool HasStatus,
    string StatusText,
    string Status,
    string SummaryText,
    string NoticeCode,
    string SummaryCode)
{
    /// <summary>入口出现在待送点取货停靠（而不是机台取货停靠）。「取消装货」按钮上的字跟它走（<c>8005-agv-onboard-hmi#348</c>）。</summary>
    public bool AtStagingPickupStop { get; init; }

    /// <summary>待送点摘要那一行在不在屏上：只在待送点出现，机台取货没有它（<c>8005-agv-onboard-hmi#352</c>）。</summary>
    public bool HasSummary => AtStagingPickupStop;

    public static CurrentStopDepartureDisplay Empty { get; } =
        new(
            false,
            false,
            null,
            string.Empty,
            false,
            string.Empty,
            false,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
}
