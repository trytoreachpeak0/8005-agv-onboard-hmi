using System.ComponentModel;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 严重安全故障在界面这一层的三件事（<c>trytoreachpeak0/8005-agv-onboard-hmi#171</c>）：真故障仍然锁存、
/// 锁存之后复位入口真的出现在窗口上、横幅不再声称一件不成立的安全事实。
/// </summary>
/// <remarks>
/// <para>
/// 入口那条断到「属性值 ＋ 以该属性名发出的变更通知」两件事，理由与
/// <see cref="RecoveryEntryNotificationViewModelTests"/> 同一条：WPF 只在收到以绑定属性名命名的
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> 时重读绑定，hmi#109／#112 两次都是属性值对、
/// 通知名不对，于是按钮停在启动时的 <c>Collapsed</c>，CI 与单测全绿、只有真装置看得到。
/// </para>
/// <para>不带 trait：纯界面呈现，与 <see cref="SublotRejectionViewModelTests"/> 同一理由。</para>
/// </remarks>
public sealed class FatalFaultLatchViewModelTests
{
    /// <summary>
    /// 反向对照：分级之后真故障必须照样锁存。少了这一条，「业务拒绝不锁存」可以被一个
    /// 「什么都不锁存」的实现通过。
    /// </summary>
    [Fact]
    public async Task ATechnicalFailureStillLatchesTheVehicle()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ConfigureWireToGate(
            (_, _, _) => throw new IOException("仓门控制设备连接中断。"),
            () => true);
        viewModel.ScanText = "SUBLOT-A";

        viewModel.ScannerSubmitCommand.Execute(null);
        await WaitUntilAsync(() => controller.Current.State == OnboardState.Faulted);

        Assert.Equal("UI_COMMAND_FAILED", controller.Current.ErrorCode);
        Assert.True(viewModel.HasError);
        Assert.Equal(OnboardFatalFaultBanner.UiCommandFailed, viewModel.Guidance);
    }

    /// <summary>
    /// 业务拒绝不锁存，而且操作员读到的是为什么被拒，不是一个裸码。
    /// </summary>
    [Fact]
    public async Task ABusinessRefusalIsShownAndTheVehicleKeepsRunning()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ConfigureWireToGate(
            (_, _, _) => throw new InvalidOperationException("SUBLOT_NOT_IN_WORKLIST"),
            () => true);
        viewModel.ScanText = "SUBLOT-X";

        viewModel.ScannerSubmitCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Logs.Count > 0);

        Assert.NotEqual(OnboardState.Faulted, controller.Current.State);
        Assert.False(viewModel.HasError);
        Assert.True(viewModel.CanSubmit);
        Assert.Contains(
            viewModel.Logs,
            line => line.Kind == OperatorRecordKind.Warning
                && line.Message == OnboardCommandRejectionText.DescribeWithCode("SUBLOT_NOT_IN_WORKLIST"));
        // 读到的是一句中文，不是一个裸码。码留在句末的括号里是有意的：操作员把它报给维护人员，
        // G2 也靠它认出是哪一条守卫拒的（见 OnboardCommandRejectionText.DescribeWithCode）。
        Assert.Contains("不属于服务端下发的站点任务", viewModel.Guidance, StringComparison.Ordinal);
        Assert.StartsWith("当前条码", viewModel.Guidance, StringComparison.Ordinal);
    }

    /// <summary>
    /// 被拒的那句话要活过接下来的常态发布（8005-agv-onboard-hmi#171 审查 M1）。
    /// </summary>
    /// <remarks>
    /// IO 快照默认每 100 毫秒到一次，每次都重发一遍常态横幅。所以「把提示写进 Guidance 就完了」
    /// 在这里不成立——那句话活不过 100 毫秒，操作员根本来不及读。这条用一次真实的重新发布
    /// （<c>RefreshExternalSafetyState</c> 走的正是 <c>ReevaluateIdleState</c> → <c>PublishCore</c>
    /// 那条路）来钉它，不靠 sleep。
    ///
    /// 同一个 PR 里，复位复核被拒那条路先修掉了同型的问题（原因写进锁存，否则 PublishCore 会
    /// 改写回原横幅），而这条路当时重犯了一次。
    /// </remarks>
    [Fact]
    public async Task ARefusalSurvivesTheNextRoutineSnapshot()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ConfigureWireToGate(
            (_, _, _) => throw new InvalidOperationException("SUBLOT_NOT_IN_WORKLIST"),
            () => true);
        viewModel.ScanText = "SUBLOT-X";

        viewModel.ScannerSubmitCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Logs.Count > 0);
        string refusal = viewModel.Guidance;
        Assert.Contains("不属于服务端下发的站点任务", refusal, StringComparison.Ordinal);

        // 常态横幅又发了一遍 —— 提示还在。
        controller.RefreshExternalSafetyState();
        Assert.Equal(refusal, viewModel.Guidance);
        Assert.True(viewModel.HasWarning);

        controller.RefreshExternalSafetyState();
        Assert.Equal(refusal, viewModel.Guidance);
    }

    /// <summary>
    /// 锁存之后复位入口出现并被通知到窗口；复位之后它自己消失。正常运行里它不出现，由
    /// <c>OnboardControllerTests.ANormalRunNeverOffersTheFatalFaultClearance</c> 钉住。
    /// </summary>
    [Fact]
    public async Task TheClearanceEntryAppearsWhenAClearableFaultLatchesAndGoesAwayAfterItIsLifted()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, () => "OP-7");
        Assert.False(viewModel.CanClearFatalFault);
        List<string?> announced = [];
        viewModel.PropertyChanged += (_, args) => announced.Add(args.PropertyName);

        controller.EnterFatalFault("UI_COMMAND_FAILED", OnboardFatalFaultBanner.UiCommandFailed);

        Assert.True(viewModel.CanClearFatalFault);
        Assert.Contains(nameof(MainViewModel.CanClearFatalFault), announced);
        // 启动安全复核那个入口不受这次锁存影响：两个按钮，两条判据。
        Assert.False(viewModel.CanSafetyReview);

        announced.Clear();
        Assert.True(await viewModel.ClearFatalFaultAsync(TestContext.Current.CancellationToken));

        Assert.False(viewModel.CanClearFatalFault);
        Assert.Contains(nameof(MainViewModel.CanClearFatalFault), announced);
        Assert.NotEqual(OnboardState.Faulted, controller.Current.State);
    }

    /// <summary>
    /// 说不出是谁在复位，就不给这个入口——复位要留痕，而留不下痕的复位不值得有。
    /// </summary>
    [Fact]
    public async Task WithoutAnOperatorIdentityTheClearanceEntryIsNotOffered()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, () => "   ");

        controller.EnterFatalFault("UI_COMMAND_FAILED", OnboardFatalFaultBanner.UiCommandFailed);

        // 控制器这一层是愿意受理的，关掉入口的只有「没有身份」这一条。
        Assert.True(controller.CanClearFatalFault);
        Assert.False(viewModel.CanClearFatalFault);
        Assert.False(await viewModel.ClearFatalFaultAsync(TestContext.Current.CancellationToken));
        Assert.Equal(OnboardState.Faulted, controller.Current.State);
    }

    /// <summary>
    /// 横幅的**措辞**：两句共用同一段结尾，所以它们不会各说各的。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这条只管措辞，管不到那句话是不是真的</b>——第一版就只有这一条，于是「本界面已禁止扫码
    /// 开门」被换上去之后没有任何东西发现它在 v2 上同样是假的（审查 S1）。真判据在
    /// <c>MultiDemandJourneyG2Tests.WhileAFatalFaultIsLatchedTheScanEntryIsClosedAndTheSubmitPathRefuses</c>：
    /// 锁存之后按钮点不下去，**并且**直接调提交路径也被拒。两条都要，删哪一条都会留下一个洞。
    /// </para>
    /// <para>
    /// 「仓门仍可能自动打开」那半句跟着 <c>onboard-hmi#84</c> 走：那张票让锁存真的挡住服务端下发
    /// 的开锁之后，它就不再准确，届时这条测试与两句文案一起改。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(OnboardFatalFaultBanner.UiCommandFailed)]
    [InlineData(OnboardFatalFaultBanner.UnhandledUiError)]
    [InlineData(OnboardFatalFaultBanner.UnhandledUiErrorDialog)]
    public void NoLatchBannerClaimsTheDoorsAreStopped(string banner)
    {
        Assert.Contains("仓门仍可能自动打开", banner, StringComparison.Ordinal);
        Assert.DoesNotContain("已停止开门", banner, StringComparison.Ordinal);
        // 实现层面的说法留给日志：站在车前的人不需要知道有个服务端才能读懂这句话。
        Assert.DoesNotContain("服务端", banner, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "界面命令没有在 5 秒内走完错误处理。");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<MainViewModel> ViewModel(
        OnboardController controller,
        Func<string?>? operatorIdProvider = null)
    {
        MainViewModel viewModel = new(
            controller,
            new RecordingLogger(),
            "agv02",
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()),
            operatorIdProvider)
        {
            StationDepartureCountdownDispatcher = null
        };
        await viewModel.InitializeAsync();
        return viewModel;
    }

    private static OnboardController Controller() => new(
        new FakeIoModuleClient(),
        new IdleRuleGateway(),
        new RecordingLogger(),
        new SystemClock(),
        new OnboardWorkflowOptions(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromSeconds(1),
            128,
            2));

    private sealed class IdleRuleGateway : IRuleGateway
    {
        public bool IsConnected => false;

        public VisitContext? CurrentVisit => null;

        public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

        public event EventHandler<ValueChangedEventArgs<VisitContext?>>? VisitChanged;

        public Task StartAsync(CancellationToken applicationStopping) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ScanAuthorization> VerifyScanAsync(
            ScanVerificationRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("本测试不走规则模块。");

        public Task<bool> ReportOperationAsync(OperationResult result, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("本测试不上报。");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
