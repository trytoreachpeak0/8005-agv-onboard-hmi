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
        // 这里原本还有一条 Assert.True(viewModel.CanSubmit)，删掉了：本用例的 ConfigureWireToGate
        // 第二个参数传的是常量 () => true，所以那条断言在任何实现下都成立——它看起来在守「一次业务
        // 拒绝之后扫码入口还在」，实际什么都没守（审查，判据路条目 3）。真判据在
        // MultiDemandJourneyG2Tests.AnEntryRefusedLocallyIsAMessageToTheOperatorAndNotALatchedVehicle，
        // 那里接的是真业务服务（harness.Business），并且在拒绝前后各断一次 CanSubmit。
        // 引名字不引行号：行号会漂移，而我核这条时拿到的行号已经指向了别的测试。
        // 加强它就要接真服务，那会和 Paths.cs 重复；一条删掉的假断言比一条半真的干净。
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
    /// 锁存期间恢复入口关闭，**两条刷新路径给同一个答案**（审查，产品路发现 2 ＋ 判据路条目 2）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// onboard-hmi#174 之后九个里有一个例外：扫码之前的取消装货锁存期间开着，因为它不碰 IO。这里没接那一半
    /// （<c>canRequestLoadCancellationBeforeAnySublot</c> 不传，视图模型按 false 处理），所以九个照旧全关；例外本身由
    /// <see cref="ALatchKeepsTheInFlightCancellationShutAndOpensOnlyTheOneBeforeAnySublot"/> 钉住。
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <para>
    /// 这几个入口里有三个（补偿清空、修正装货、强制机械取出）会经
    /// <c>WireToGateRecoveryVectorExecutor</c> 真的开门，而那个执行器不经过 <c>OnboardController</c>，
    /// 所以锁存在执行那一层拦不住——**挡住它的就是界面这一层**。
    /// </para>
    /// <para>
    /// 而第一版只在 <c>ApplyWireToGatePresentationCore</c> 那一条路径上挡，还在注释里称它
    /// 「今天唯一挡住它的就是这几行」。<c>RefreshWireToGateInputStateCore</c> 重写同样这些属性、
    /// 完全不看控制器状态，**服务端重发一次录入请求（`App.xaml.cs` 挂在 `SublotEntryRequested` 上）
    /// 就会把入口放回来**。所以这条测试专门走那条平行路径，而不是走快照路径。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task BothRefreshPathsKeepTheRecoveryEntriesClosedWhileALatchStands()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => true,
            canRequestRecovery: () => true,
            canRequestLoadCancellation: () => true,
            canRequestLoadCompensation: () => true,
            canRequestLoadCorrection: () => true,
            canRequestFaultCargoHandoff: () => true,
            canRequestForcedMechanicalRecovery: () => true,
            canRequestManualChargingReturn: () => true);
        // 会话必须先建立：ApplyWireToGatePresentationCore 开头就 `_wireToGateSession is null` 直接
        // return，没有会话时那条路径根本不跑——两条路径要比，就得让两条都真的跑。
        viewModel.UpdateWireToGateStatus(new WireToGateSessionSnapshot(
            Connected: true,
            SessionGeneration: 1,
            Readiness: WireToGateSessionReadiness.RecoveryRequired,
            ReasonCodes: [],
            CapabilityVersion: 1,
            SafetyStateVersion: 1,
            UpdatedAt: Now));
        viewModel.RefreshWireToGateInputState();
        Assert.True(viewModel.CanRequestLoadCompensation);
        Assert.True(viewModel.CanRequestForcedMechanicalRecovery);

        // 一、快照那条路径关掉它们。
        controller.EnterFatalFault("UI_COMMAND_FAILED", OnboardFatalFaultBanner.UiCommandFailed);
        Assert.False(viewModel.CanRequestLoadCompensation);

        // 二、**这一步是判据的核心**：服务端重发录入请求走的正是这条平行路径，业务侧仍然说「可以」，
        // 而锁存必须让它们保持关闭。第一版在这里会把九个入口全部放回来。
        viewModel.RefreshWireToGateInputState();

        Assert.False(viewModel.CanRequestWireToGateRecovery);
        Assert.False(viewModel.CanRequestLoadCancellation);
        Assert.False(viewModel.CanRequestLoadCompensation);
        Assert.False(viewModel.CanRequestLoadCorrection);
        Assert.False(viewModel.CanRequestFaultCargoHandoff);
        Assert.False(viewModel.CanRequestForcedMechanicalRecovery);
        Assert.False(viewModel.CanRequestManualChargingReturn);
        Assert.False(viewModel.CanConfirmForcedMechanicalRecovery);
        Assert.False(viewModel.CanSubmitHardwareRecoveryRecord);
    }

    /// <summary>
    /// 锁存期间取消装货只剩扫码之前那一半（onboard-hmi#174），两条刷新路径同一个答案。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 在途那一半按下去会经恢复向量执行器开门清空，锁存在执行层拦不住它，所以业务侧说「在途可以取消」时入口仍然关着；
    /// 扫码之前那一半授权时仓位集为空、不开门，锁存期间开着——否则操作员只能干等站点超时，需求被永久抑制。
    /// </para>
    /// <para>
    /// <b>两个方向都断</b>：只断「扫码前开着」，一个把在途那一半也放行的实现（<c>AllowLoadCancellationEntry</c> 里少了
    /// <c>AllowRecoveryEntry</c>）也能通过；结构守卫只认这个闸门的名字、看不见它的方法体，这一半只有这里接得住。
    /// 其余八个在扫码前那一半开着的时候也要照旧关着。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ALatchKeepsTheInFlightCancellationShutAndOpensOnlyTheOneBeforeAnySublot()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        bool beforeAnySublot = false;
        List<string> announced = [];
        viewModel.PropertyChanged += (_, args) => announced.Add(args.PropertyName ?? string.Empty);
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => true,
            canRequestRecovery: () => true,
            canRequestLoadCancellation: () => true,
            canRequestLoadCompensation: () => true,
            canRequestLoadCorrection: () => true,
            canRequestFaultCargoHandoff: () => true,
            canRequestForcedMechanicalRecovery: () => true,
            canRequestManualChargingReturn: () => true,
            canRequestLoadCancellationBeforeAnySublot: () => beforeAnySublot);
        viewModel.UpdateWireToGateStatus(new WireToGateSessionSnapshot(
            Connected: true,
            SessionGeneration: 1,
            Readiness: WireToGateSessionReadiness.Ready,
            ReasonCodes: [],
            CapabilityVersion: 1,
            SafetyStateVersion: 1,
            UpdatedAt: Now));
        viewModel.RefreshWireToGateInputState();
        Assert.True(viewModel.CanRequestLoadCancellation);

        // 一、只有在途那一半：锁存之后两条路径都关。
        controller.EnterFatalFault("UI_COMMAND_FAILED", OnboardFatalFaultBanner.UiCommandFailed);
        Assert.False(viewModel.CanRequestLoadCancellation);
        viewModel.RefreshWireToGateInputState();
        Assert.False(viewModel.CanRequestLoadCancellation);

        // 二、扫码之前那一半到了：两条路径都开，按钮真的出现在窗口上（属性值 ＋ 以属性名发出的通知）。
        beforeAnySublot = true;
        announced.Clear();
        viewModel.RefreshWireToGateInputState();
        Assert.True(viewModel.CanRequestLoadCancellation);
        Assert.Contains(nameof(MainViewModel.CanRequestLoadCancellation), announced);
        // 会话状态更新先走输入刷新、最后走展示路径，所以这一步读到的是展示路径（守卫之前那一处写）的答案。
        viewModel.UpdateWireToGateStatus(new WireToGateSessionSnapshot(
            Connected: true,
            SessionGeneration: 1,
            Readiness: WireToGateSessionReadiness.Ready,
            ReasonCodes: [],
            CapabilityVersion: 1,
            SafetyStateVersion: 2,
            UpdatedAt: Now));
        Assert.True(viewModel.CanRequestLoadCancellation);

        // 其余八个照旧关着。
        Assert.False(viewModel.CanRequestWireToGateRecovery);
        Assert.False(viewModel.CanRequestLoadCompensation);
        Assert.False(viewModel.CanRequestLoadCorrection);
        Assert.False(viewModel.CanRequestFaultCargoHandoff);
        Assert.False(viewModel.CanRequestForcedMechanicalRecovery);
        Assert.False(viewModel.CanRequestManualChargingReturn);
        Assert.False(viewModel.CanConfirmForcedMechanicalRecovery);
        Assert.False(viewModel.CanSubmitHardwareRecoveryRecord);

        // 三、扫码之前那一半走了（例如一条仓位命令到了）：又关上。
        beforeAnySublot = false;
        viewModel.RefreshWireToGateInputState();
        Assert.False(viewModel.CanRequestLoadCancellation);
    }

    /// <summary>
    /// 提示会过期。**这条守的是一条新立的红线**（<c>OperatorNoticeHold</c> = 8 秒），
    /// 而新立的红线要有自己的护栏（审查，判据路条目 5）。
    /// </summary>
    /// <remarks>
    /// 没有这一条，把 <c>OperatorNoticeHold</c> 改成一天全绿——后果是提示行一旦写上一条业务拒绝，
    /// 整个班次里除非进阻断态否则再也不会被常态横幅换掉，操作员读到的是几小时前的拒绝，而车早就
    /// 换了站。用注入的时钟推进，不靠真的等 8 秒。
    /// </remarks>
    [Fact]
    public async Task ARefusalStopsHoldingTheBannerOnceItHasExpired()
    {
        MultiDemandViewModelTests.ManualClock clock = new(new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero));
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, clock: clock);
        viewModel.ConfigureWireToGate(
            (_, _, _) => throw new InvalidOperationException("SUBLOT_NOT_IN_WORKLIST"),
            () => true);
        viewModel.ScanText = "SUBLOT-X";

        viewModel.ScannerSubmitCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Logs.Count > 0);
        Assert.Contains("不属于服务端下发的站点任务", viewModel.Guidance, StringComparison.Ordinal);

        // 还没到期：仍然压住。
        clock.Advance(TimeSpan.FromSeconds(7));
        controller.RefreshExternalSafetyState();
        Assert.Contains("不属于服务端下发的站点任务", viewModel.Guidance, StringComparison.Ordinal);

        // 过期之后：常态横幅拿回那一行。
        clock.Advance(TimeSpan.FromSeconds(2));
        controller.RefreshExternalSafetyState();
        Assert.DoesNotContain("不属于服务端下发的站点任务", viewModel.Guidance, StringComparison.Ordinal);
        Assert.Equal(controller.Current.Guidance, viewModel.Guidance);
    }

    /// <summary>
    /// 门正在开的时候，提示必须让位给「正在打开N号仓」（审查，产品路发现 1 ＋ 判据路条目 6）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PublishGuidance</c> 的 <c>preempting</c> 参数第一版取自 <c>banner.HasError</c>，而
    /// <c>WireToGateHmiPresentation.Create</c> 的**每一个分支都写死 <c>HasError: false</c>**
    /// （全文 10 处 false、0 处 true）——**一个恒为 false 的开关，和不存在是一样的。**
    /// </para>
    /// <para>
    /// 所以判据里加了「有在途仓位操作」：那一刻门正在开，而那句话比一条已经读过的拒绝回执要紧
    /// 得多。这条测试把提示造在仓位操作已经在途之后，因为 <c>ApplyWireToGateOperatorEvent</c>
    /// 自己会清提示——要测的是 <c>preempting</c>，不是那条清除。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnInFlightSlotOperationTakesTheBannerBackFromARefusal()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ConfigureWireToGate(
            (_, _, _) => throw new InvalidOperationException("SUBLOT_NOT_IN_WORKLIST"),
            () => true);
        viewModel.UpdateWireToGateStatus(new WireToGateSessionSnapshot(
            Connected: true,
            SessionGeneration: 1,
            Readiness: WireToGateSessionReadiness.Ready,
            ReasonCodes: [],
            CapabilityVersion: 1,
            SafetyStateVersion: 1,
            UpdatedAt: Now));
        // 仓位操作在途：门正在开。
        viewModel.ApplyWireToGateOperatorEvent(new WireToGateOperatorEvent(
            Now,
            "OPERATION_PROGRESS",
            "正在打开3号仓。",
            new WireToGateHmiOperationSnapshot(
                "a5d6ad42-16e6-045c-90ea-2e29b7aaec5d",
                OperationType.Load,
                [3],
                WireToGateHmiOperationStage.Unlocking,
                "正在打开3号仓。",
                Now)));

        viewModel.ScanText = "SUBLOT-X";
        viewModel.ScannerSubmitCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Logs.Any(
            line => line.Message.Contains("不属于服务端下发的站点任务", StringComparison.Ordinal)));

        // 常态横幅又发了一遍。门在开，所以这一行不能还在说他刚才扫错了。
        controller.RefreshExternalSafetyState();

        Assert.DoesNotContain("不属于服务端下发的站点任务", viewModel.Guidance, StringComparison.Ordinal);
    }

    /// <summary>
    /// 又有事发生了，上一条拒绝回执就到此为止——不等那 8 秒走完（审查，产品路发现 1）。
    /// </summary>
    /// <remarks>
    /// 审查给的时间线：t=0 扫错子批 → t≈1.5s 扫对 → t≈3s 执行器真的开门、横幅要说「正在打开3号仓」。
    /// 修之前那两句一句都不会显示，直到 t=8s——**界面在说他刚才扫错了，而门正在他面前打开**。
    /// 这正是本票要消灭的形状，只是换了一句话。
    /// </remarks>
    [Fact]
    public async Task ANewOperatorEventEndsTheRefusalHoldImmediately()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ConfigureWireToGate(
            (_, _, _) => throw new InvalidOperationException("SUBLOT_NOT_IN_WORKLIST"),
            () => true);
        viewModel.ScanText = "SUBLOT-X";

        viewModel.ScannerSubmitCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Logs.Count > 0);
        Assert.Contains("不属于服务端下发的站点任务", viewModel.Guidance, StringComparison.Ordinal);

        viewModel.ApplyWireToGateOperatorEvent(new WireToGateOperatorEvent(
            Now,
            "SUBLOT_SUBMITTED",
            "子批 SUBLOT-A 已提交，等待服务端下发仓位操作。",
            null));
        controller.RefreshExternalSafetyState();

        Assert.DoesNotContain("不属于服务端下发的站点任务", viewModel.Guidance, StringComparison.Ordinal);
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

    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    private static async Task<MainViewModel> ViewModel(
        OnboardController controller,
        Func<string?>? operatorIdProvider = null,
        IClock? clock = null)
    {
        MainViewModel viewModel = new(
            controller,
            new RecordingLogger(),
            "agv02",
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()),
            operatorIdProvider)
        {
            StationDepartureCountdownDispatcher = null,
            Clock = clock ?? new SystemClock()
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
