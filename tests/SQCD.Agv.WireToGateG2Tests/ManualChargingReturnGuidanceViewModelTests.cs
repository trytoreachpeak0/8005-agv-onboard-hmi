using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 人工充电保持时「充电后返回服务」按不了，屏上那一行说明在视图模型这一层的三件事（8005-agv-onboard-hmi#293）：
/// 跟着保持出现和消失、以属性名通知到窗口、夹具到不了的原因组合（会话离线而保持投影还在、锁存同时离线）各说哪一句。
/// </summary>
/// <remarks>
/// 业务服务给出哪个原因、按钮按下去真的发出请求，由 <see cref="ManualChargingReturnEntryG2Tests"/> 证明；哪一句压过哪一句
/// 由 <c>WireToGateManualChargingReturnTextTests</c> 逐组合钉住。这里的原因由测试直接给，只钉视图模型的接线与两条刷新路径。
/// 不带 trait：纯界面呈现，理由同 <see cref="RecoveryEntryNotificationViewModelTests"/>。
/// </remarks>
public sealed class ManualChargingReturnGuidanceViewModelTests
{
    private const string NotAuthorized =
        "充电完成后需维护管理员在本机开启返回服务权限，再按「充电后返回服务」。请联系系统维护人员。";

    private const string SessionNotReady = "与服务端恢复连接后才能申请返回服务。";

    private const string Latched = "安全故障解除前不能申请返回服务，请联系系统维护人员。";

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 20, 18, 0, TimeSpan.Zero);

    /// <summary>
    /// 这一行只跟着保持：没有保持不显示，保持来了显示，服务端解除了消失；每次变化都以那两个属性名通知窗口——
    /// 属性值对、通知名不对，按钮与文字就停在启动时的样子（hmi#109／#112 的形状）。
    /// </summary>
    [Fact]
    public async Task TheLineFollowsTheHoldAndIsAnnouncedToTheWindow()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        Configure(viewModel, () => WireToGateManualChargingReturnBlocker.OperatorNotAuthorized);
        viewModel.UpdateWireToGateStatus(Session(connected: true));
        viewModel.UpdateWireToGateJourney(Journey(hold: false));
        Assert.Equal(string.Empty, viewModel.ManualChargingReturnGuidance);
        Assert.False(viewModel.HasManualChargingReturnGuidance);

        List<string?> announced = [];
        viewModel.PropertyChanged += (_, args) => announced.Add(args.PropertyName);
        viewModel.UpdateWireToGateJourney(Journey(hold: true));

        Assert.Equal(NotAuthorized, viewModel.ManualChargingReturnGuidance);
        Assert.True(viewModel.HasManualChargingReturnGuidance);
        Assert.Contains(nameof(MainViewModel.ManualChargingReturnGuidance), announced);
        Assert.Contains(nameof(MainViewModel.HasManualChargingReturnGuidance), announced);

        viewModel.UpdateWireToGateJourney(Journey(hold: false, revision: 2));
        Assert.Equal(string.Empty, viewModel.ManualChargingReturnGuidance);
        Assert.False(viewModel.HasManualChargingReturnGuidance);
    }

    /// <summary>会话离线、保持投影还在：说连接，不说权限——配好权限也按不了。</summary>
    [Fact]
    public async Task AnOfflineSessionIsTheCauseNamedBeforeTheMaintainer()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        Configure(viewModel, () => WireToGateManualChargingReturnBlocker.SessionNotReady);
        viewModel.UpdateWireToGateJourney(Journey(hold: true));
        viewModel.UpdateWireToGateStatus(Session(connected: false));

        Assert.Equal(SessionNotReady, viewModel.ManualChargingReturnGuidance);
        viewModel.RefreshWireToGateInputState();
        Assert.Equal(SessionNotReady, viewModel.ManualChargingReturnGuidance);
    }

    /// <summary>
    /// 锁存同时离线：说锁存，两条刷新路径同一个答案。会话快照走展示路径（锁存守卫 early return 的那一条），
    /// 重发录入请求走输入刷新那一条；只在一条上算，另一条就会把锁存那一刻的旧文字留在屏上。
    /// </summary>
    [Fact]
    public async Task ALatchWhileOfflineIsTheCauseNamedOnBothRefreshPaths()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        Configure(viewModel, () => WireToGateManualChargingReturnBlocker.SessionNotReady);
        viewModel.UpdateWireToGateJourney(Journey(hold: true));
        viewModel.UpdateWireToGateStatus(Session(connected: false));
        Assert.Equal(SessionNotReady, viewModel.ManualChargingReturnGuidance);

        controller.EnterFatalFault("UI_COMMAND_FAILED", OnboardFatalFaultBanner.UiCommandFailed);
        Assert.Equal(Latched, viewModel.ManualChargingReturnGuidance);

        viewModel.UpdateWireToGateStatus(Session(connected: false, safetyStateVersion: 2));
        Assert.Equal(Latched, viewModel.ManualChargingReturnGuidance);
        viewModel.RefreshWireToGateInputState();
        Assert.Equal(Latched, viewModel.ManualChargingReturnGuidance);
    }

    /// <summary>业务服务说可以按：按钮在，旁边不再挂一行说明。</summary>
    [Fact]
    public async Task NoLineBesideAButtonThatCanBePressed()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        Configure(viewModel, () => WireToGateManualChargingReturnBlocker.None, offered: true);
        viewModel.UpdateWireToGateStatus(Session(connected: true));
        viewModel.UpdateWireToGateJourney(Journey(hold: true));

        Assert.True(viewModel.CanRequestManualChargingReturn);
        Assert.Equal(string.Empty, viewModel.ManualChargingReturnGuidance);
        Assert.False(viewModel.HasManualChargingReturnGuidance);
    }

    private static void Configure(
        MainViewModel viewModel,
        Func<WireToGateManualChargingReturnBlocker> blocker,
        bool offered = false)
    {
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => false,
            canRequestManualChargingReturn: () => offered,
            manualChargingReturnRequester: _ => Task.FromResult(true));
        viewModel.ConfigureManualChargingReturnGuidance(blocker);
    }

    private static WireToGateSessionSnapshot Session(bool connected, long safetyStateVersion = 1) => new(
        Connected: connected,
        SessionGeneration: 1,
        Readiness: connected ? WireToGateSessionReadiness.Ready : WireToGateSessionReadiness.Disconnected,
        ReasonCodes: [],
        CapabilityVersion: 1,
        SafetyStateVersion: safetyStateVersion,
        UpdatedAt: Now);

    private static WireToGateJourneySnapshot Journey(bool hold, long revision = 1) => new(
        new WireToGateVehicleBusinessState(
            revision, "READY", null, hold, "LOW", "NOT_CHARGING", null, [], Now, new string('a', 64)),
        null,
        null,
        Now);

    private static async Task<MainViewModel> ViewModel(OnboardController controller)
    {
        MainViewModel viewModel = new(
            controller,
            new RecordingLogger(),
            "agv02",
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()))
        {
            StationDepartureCountdownDispatcher = null,
            Clock = new SystemClock()
        };
        await viewModel.InitializeAsync();
        return viewModel;
    }

    private static OnboardController Controller() => new(
        new FakeIoModuleClient(),
        new DisabledRuleGateway(),
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
}
