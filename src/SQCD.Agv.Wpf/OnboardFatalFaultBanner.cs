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
/// <b>那两句曾经准确，是因为那个缺陷还在；到期条件写在这里，#191 按它改了。</b>
/// <c>onboard-hmi#191</c>（一并解决 <c>#84</c>）之后，两个执行器的每一次开锁紧前面都问锁存，锁存期间一扇新门都不开，
/// 「仓门仍可能自动打开」就成了过度警告。现在：
/// </para>
/// <list type="bullet">
/// <item><c>UI_COMMAND_FAILED</c> 说「本机不再自动开门」。这一次是真的：这个进程是完好的（它自己把故障归了类），
/// 锁存经注入的那个问题送到每一次开锁前面；行为判据在 <c>MultiDemandJourneyG2Tests.FatalFaultLatch</c> 与
/// <c>RecoveryVectorG2Tests.FatalFaultLatch</c>。</item>
/// <item><c>UNHANDLED_UI_ERROR</c> 不这么说。那是一个没人接住的异常，出处不明，这个进程还守不守锁存本身就是未知
/// （<c>OnboardFailureClassification</c> 因此规定它只能重启解除），所以它不作这个保证。</item>
/// <item>两句共用的那一句改成讲已经开着的门：锁存不关门，锁存之前开着的门仍然开着，靠近前照样要看。</item>
/// </list>
/// <para>
/// <b>为什么不写「服务端」。</b>站在车前的人要在两秒内得到的，是一个关于这台机器会不会动的
/// 判断，不是一条需要他先知道有个服务端、再自己推出「所以门可能开」的陈述。所以那句话在横幅上
/// 是独立的、不需要推理的一句（<see cref="OpenDoorsStayOpen"/>）；实现层面的说法留给日志。
/// </para>
/// </remarks>
public static class OnboardFatalFaultBanner
{
    /// <summary>
    /// 横幅里那一句独立的、不需要推理的话。两处锁存共用它，所以两句不会各说各的。
    /// </summary>
    public const string OpenDoorsStayOpen = "锁存之前已经打开的仓门仍然开着——靠近前请先确认仓门状态，并联系维护人员。";

    /// <summary>界面命令失败导致的锁存（<c>UI_COMMAND_FAILED</c>）。</summary>
    public const string UiCommandFailed = "操作界面出现异常，本界面已禁止扫码开门，本机不再自动开门。" + OpenDoorsStayOpen;

    /// <summary>未处理异常导致的锁存（<c>UNHANDLED_UI_ERROR</c>）。</summary>
    public const string UnhandledUiError = "软件运行异常，本界面已禁止继续操作。" + OpenDoorsStayOpen;

    /// <summary>未处理异常同时弹出的对话框正文，与横幅同一套说法。</summary>
    public const string UnhandledUiErrorDialog = "软件运行异常，本界面已禁止继续操作。\n" + OpenDoorsStayOpen;
}
