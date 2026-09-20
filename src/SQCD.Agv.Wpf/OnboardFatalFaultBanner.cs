namespace SQCD.Agv.Wpf;

/// <summary>
/// 严重安全故障锁存之后，操作员在横幅上读到的话。两处锁存共用这里的句子。
/// </summary>
/// <remarks>
/// <para>
/// <b>原来的两句都是不实的。</b><c>UI_COMMAND_FAILED</c> 说「已停止开门」，
/// <c>UNHANDLED_UI_ERROR</c> 说「已禁止继续操作」——而锁存只挡得住本界面发起的开门。
/// 服务端下发的仓位操作由 <c>WireToGateSlotOperationExecutor</c> 执行，它直接持有
/// <c>IIoModuleClient</c>，根本不经过 <c>OnboardController</c>，所以 <c>_fatalFault</c>
/// 在结构上就管不到它（不是「今天恰好没有引用」，是这条路压根不从那里走）。
/// 2026-09-16 现场实测：锁存之后仍有 5 次开锁脉冲（onboard-hmi#82）。
/// </para>
/// <para>
/// <b>这两句话现在是准确的，而它们准确的原因是那个缺陷还在。</b>
/// <c>onboard-hmi#84</c> 要让锁存真的挡住服务端下发的开锁；那一天落地之后，
/// 「仓门仍可能自动打开」就从「准确」变成「过度警告」——**届时这两句必须跟着改**。
/// 这是一句因为缺陷存在才成立的观测，它的到期条件就写在这里。
/// </para>
/// <para>
/// <b>为什么不写「服务端」。</b>站在车前的人要在两秒内得到的，是一个关于这台机器会不会动的
/// 判断，不是一条需要他先知道有个服务端、再自己推出「所以门可能开」的陈述。所以那句话在横幅上
/// 是独立的、不需要推理的一句（<see cref="DoorsMayStillOpen"/>）；实现层面的说法留给日志。
/// </para>
/// </remarks>
public static class OnboardFatalFaultBanner
{
    /// <summary>
    /// 横幅里那一句独立的、不需要推理的话。两处锁存共用它，所以两句不会各说各的。
    /// </summary>
    public const string DoorsMayStillOpen = "仓门仍可能自动打开——靠近前请先确认仓门状态，并联系维护人员。";

    /// <summary>界面命令失败导致的锁存（<c>UI_COMMAND_FAILED</c>）。</summary>
    public const string UiCommandFailed = "操作界面出现异常，本界面已禁止扫码开门。" + DoorsMayStillOpen;

    /// <summary>未处理异常导致的锁存（<c>UNHANDLED_UI_ERROR</c>）。</summary>
    public const string UnhandledUiError = "软件运行异常，本界面已禁止继续操作。" + DoorsMayStillOpen;

    /// <summary>未处理异常同时弹出的对话框正文，与横幅同一套说法。</summary>
    public const string UnhandledUiErrorDialog = "软件运行异常，本界面已禁止继续操作。\n" + DoorsMayStillOpen;
}
