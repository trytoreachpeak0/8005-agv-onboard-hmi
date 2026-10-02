namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 扣住等待现场确认的服务端恢复命令在界面上的全部内容，整值替换（<c>8005-agv-onboard-hmi#239</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 一个记录而不是几个并排的属性：两个按钮亮不亮、说明写什么，出自同一份
/// <see cref="WireToGateHeldRecoveryCommandView"/>，界面不会出现按钮已经换了命令、说明还写着上一条的中间态。
/// </para>
/// <para>
/// <see cref="Prompt"/> 就是操作员读到的那一份；按下时原样交回去，业务服务核对它仍是扣住的那一条才执行或回复。
/// 「确认执行」会开门，严重安全故障锁存期间关着；「不执行」不碰 IO，锁存期间照常开着。
/// </para>
/// <para>
/// <see cref="DeclineArmed"/>：装货修正的「不执行」要按两次，第一次只把后果写进说明；为真时下一次按下才生效。
/// </para>
/// </remarks>
public sealed record HeldRecoveryCommandDisplay(
    bool CanConfirm,
    bool CanDecline,
    WireToGateHeldRecoveryCommandPrompt? Prompt,
    bool HasNotice,
    string NoticeText,
    bool DeclineArmed)
{
    public static HeldRecoveryCommandDisplay Empty { get; } = new(false, false, null, false, string.Empty, false);
}
