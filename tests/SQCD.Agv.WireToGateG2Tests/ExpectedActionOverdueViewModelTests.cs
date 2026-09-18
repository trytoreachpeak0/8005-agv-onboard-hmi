using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 期待动作超时与恢复原因在界面上的那一步（REQ-0358，CP-0005 实现票 1，
/// <c>trytoreachpeak0/8005-agv-onboard-hmi#109</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 两句文案是用户 2026-09-18 定的，逐字比对：会话在线时说「已上报」，断开时只说「与服务端断开，请联系管理员」——
/// 服务端不回执告警快照，断开时说已上报就是假话。
/// </para>
/// <para>
/// 原因输入只跟着管理员的异常处置入口（申请恢复、补偿清空、故障交接、强制机械恢复）出现；它们都要求恢复凭据，
/// 操作工的界面上没有。不带 trait：纯界面呈现，理由同 <see cref="SublotRejectionViewModelTests"/>。
/// </para>
/// </remarks>
public sealed class ExpectedActionOverdueViewModelTests
{
    private const string ReportedText = "期待的操作（关好3号仓门）很久没有完成，已上报，班组长或管理员会到现场查看";
    private const string DisconnectedText = "与服务端断开，请联系管理员";
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnOverdueSlotIsShownInItsOwnLineWhileTheSessionIsUp()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ConfiguredViewModel(controller);
        viewModel.UpdateWireToGateStatus(Session(connected: true));

        viewModel.UpdateOnboardAlarms([Overdue("关好3号仓门")]);

        Assert.True(viewModel.HasExpectedActionOverdue);
        Assert.Equal(ReportedText, viewModel.ExpectedActionOverdueText);
        // 它不再混进「告警：」那一行：那一行只剩期待的动作四个字，读不成一句话。
        Assert.False(viewModel.HasAlarms);
        Assert.DoesNotContain("关好3号仓门", viewModel.AlarmText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithTheSessionDownItNeverSaysReported()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ConfiguredViewModel(controller);
        viewModel.UpdateWireToGateStatus(Session(connected: true));
        viewModel.UpdateOnboardAlarms([Overdue("关好3号仓门")]);

        viewModel.UpdateWireToGateStatus(Session(connected: false));

        Assert.True(viewModel.HasExpectedActionOverdue);
        Assert.Equal(DisconnectedText, viewModel.ExpectedActionOverdueText);
        Assert.DoesNotContain("已上报", viewModel.ExpectedActionOverdueText, StringComparison.Ordinal);

        viewModel.UpdateWireToGateStatus(Session(connected: true));
        Assert.Equal(ReportedText, viewModel.ExpectedActionOverdueText);
    }

    [Fact]
    public async Task AWithdrawnOverdueLeavesTheLineAndOtherAlarmsStayInTheAlarmLine()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ConfiguredViewModel(controller);
        viewModel.UpdateWireToGateStatus(Session(connected: true));
        AlarmEntry ioLost = new(
            OnboardAlarmCodes.IoModuleDisconnected,
            OnboardAlarmEvaluator.Critical,
            Now,
            AlarmScope.CurrentVehicle,
            "仓门控制模块离线。");
        viewModel.UpdateOnboardAlarms([ioLost, Overdue("关好3号仓门")]);
        Assert.Equal("仓门控制模块离线。", viewModel.AlarmText);

        viewModel.UpdateOnboardAlarms([ioLost]);

        Assert.False(viewModel.HasExpectedActionOverdue);
        Assert.Equal(string.Empty, viewModel.ExpectedActionOverdueText);
        Assert.True(viewModel.HasAlarms);
    }

    [Fact]
    public async Task TheRecoveryReasonInputAppearsOnlyWithAnAdministratorsRecoveryEntry()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        bool offered = false;
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => false,
            loadCompensationRequester: (_, _) => Task.FromResult(true),
            canRequestLoadCompensation: () => offered);
        viewModel.UpdateWireToGateStatus(Session(connected: true));
        Assert.False(viewModel.HasRecoveryReasonInput);

        offered = true;
        viewModel.RefreshWireToGateInputState();

        Assert.True(viewModel.HasRecoveryReasonInput);
    }

    [Fact]
    public async Task TheEnteredReasonGoesWithTheRequestAndABlankOneLeavesTheDefault()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        List<string?> sent = [];
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => false,
            canRequestRecovery: () => true,
            recoveryRequester: (reason, _) => Record(sent, reason),
            canRequestLoadCompensation: () => true,
            loadCompensationRequester: (reason, _) => Record(sent, reason),
            canRequestFaultCargoHandoff: () => true,
            faultCargoHandoffRequester: (reason, _) => Record(sent, reason),
            canRequestForcedMechanicalRecovery: () => true,
            forcedMechanicalRecoveryRequester: (reason, _) => Record(sent, reason));

        const string Reason = "张三，锁：3号仓锁舌断，门已关但锁传感器读未锁";
        viewModel.RecoveryReason = $"  {Reason}  ";
        Assert.True(await viewModel.RequestWireToGateRecoveryAsync(TestContext.Current.CancellationToken));
        // 请求被受理后清空，下一次会话不会带着上一次的原因出去。
        Assert.Equal(string.Empty, viewModel.RecoveryReason);

        viewModel.RecoveryReason = Reason;
        Assert.True(await viewModel.RequestLoadCompensationAsync(TestContext.Current.CancellationToken));
        viewModel.RecoveryReason = Reason;
        Assert.True(await viewModel.RequestFaultCargoHandoffAsync(TestContext.Current.CancellationToken));
        viewModel.RecoveryReason = Reason;
        Assert.True(await viewModel.RequestForcedMechanicalRecoveryAsync(TestContext.Current.CancellationToken));
        viewModel.RecoveryReason = "   ";
        Assert.True(await viewModel.RequestLoadCompensationAsync(TestContext.Current.CancellationToken));

        Assert.Equal([Reason, Reason, Reason, Reason, null], sent);
    }

    private static Task<bool> Record(List<string?> sent, string? reason)
    {
        sent.Add(reason);
        return Task.FromResult(true);
    }

    private static AlarmEntry Overdue(string expectedAction) => new(
        OnboardAlarmCodes.SlotExpectedActionOverdue,
        OnboardAlarmEvaluator.Warning,
        Now,
        AlarmScope.CurrentVehicle,
        expectedAction,
        SlotOperationAttemptId: "11111111-1111-4111-8111-111111111111",
        PhysicalSlotNumber: 3);

    private static WireToGateSessionSnapshot Session(bool connected) => new(
        Connected: connected,
        SessionGeneration: connected ? 1 : null,
        Readiness: connected ? WireToGateSessionReadiness.Ready : WireToGateSessionReadiness.Recovering,
        ReasonCodes: [],
        CapabilityVersion: 1,
        SafetyStateVersion: 1,
        UpdatedAt: Now);

    private static async Task<MainViewModel> ConfiguredViewModel(OnboardController controller)
    {
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ConfigureWireToGate((_, _, _) => Task.CompletedTask, () => false);
        return viewModel;
    }

    private static async Task<MainViewModel> ViewModel(OnboardController controller)
    {
        MainViewModel viewModel = new(
            controller,
            new RecordingLogger(),
            "agv02",
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()))
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
            throw new InvalidOperationException("本测试不扫码。");

        public Task<bool> ReportOperationAsync(OperationResult result, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("本测试不上报。");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
