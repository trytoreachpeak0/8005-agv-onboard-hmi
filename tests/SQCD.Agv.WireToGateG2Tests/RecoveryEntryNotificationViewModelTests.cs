using System.ComponentModel;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 车载端重启后中断操作结算为 <c>UNKNOWN</c>、会话 <c>RecoveryRequired</c>、管理员凭据齐全时，四个管理员恢复入口
/// （申请恢复、补偿清空、故障交接、强制机械恢复）要真的出现在界面上（<c>trytoreachpeak0/8005-agv-onboard-hmi#112</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 四个按钮的 <c>IsEnabled</c> 与 <c>Visibility</c> 都绑在各自的 <c>CanRequest*</c> 上，WPF 只在收到以该属性名命名的
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> 时重读绑定。所以「入口出现」在视图模型这一层的判据是两件事：
/// 属性值为 <c>true</c>，并且以该属性名发出了变更通知。只看属性值不够——onboard-hmi#109 之后属性值一直是对的，
/// 通知却以别的名字发出，按钮停在启动时的 <c>Collapsed</c>（G3 journey 与真装置 L2 看到的正是这个）。
/// </para>
/// <para>
/// 状态走真实的事件路径：会话快照进 <see cref="MainViewModel.UpdateWireToGateStatus"/>，恢复投影经
/// <see cref="MainViewModel.ApplyWireToGateOperatorEvent"/> 到达，与 <c>App</c> 里业务服务发布
/// <c>OPERATION_RECOVERY_REQUIRED</c> 的那一步相同。业务服务何时认为入口可用由 <see cref="RecoveryVectorG2Tests"/> 证明，
/// 这里只钉界面那一步。不带 trait：纯界面呈现，理由同 <see cref="SublotRejectionViewModelTests"/>。
/// </para>
/// </remarks>
public sealed class RecoveryEntryNotificationViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AfterARestartSettledAsUnknownTheFourAdministratorEntriesAreAnnouncedToTheWindow()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        bool offered = false;
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => false,
            canRequestRecovery: () => offered,
            canRequestLoadCompensation: () => offered,
            canRequestFaultCargoHandoff: () => offered,
            canRequestForcedMechanicalRecovery: () => offered);
        viewModel.UpdateWireToGateStatus(RecoveryRequiredSession());
        Assert.False(viewModel.CanRequestLoadCompensation);

        List<string?> announced = [];
        viewModel.PropertyChanged += (_, args) => announced.Add(args.PropertyName);
        // 重启结算完、恢复状态读回之后，业务服务发布的那条「需要管理员恢复」投影。
        offered = true;
        viewModel.ApplyWireToGateOperatorEvent(new(
            Now,
            "OPERATION_RECOVERY_REQUIRED",
            "上次装货操作未完成：1号仓，需要管理员恢复。",
            new WireToGateHmiOperationSnapshot(
                "a5d6ad42-16e6-045c-90ea-2e29b7aaec5d",
                OperationType.Load,
                [1],
                WireToGateHmiOperationStage.RecoveryRequired,
                "上次装货操作未完成：1号仓，需要管理员恢复。",
                Now)));

        Assert.True(viewModel.CanRequestWireToGateRecovery);
        Assert.True(viewModel.CanRequestLoadCompensation);
        Assert.True(viewModel.CanRequestFaultCargoHandoff);
        Assert.True(viewModel.CanRequestForcedMechanicalRecovery);
        Assert.Contains(nameof(MainViewModel.CanRequestWireToGateRecovery), announced);
        Assert.Contains(nameof(MainViewModel.CanRequestLoadCompensation), announced);
        Assert.Contains(nameof(MainViewModel.CanRequestFaultCargoHandoff), announced);
        Assert.Contains(nameof(MainViewModel.CanRequestForcedMechanicalRecovery), announced);
    }

    private static WireToGateSessionSnapshot RecoveryRequiredSession() => new(
        Connected: true,
        SessionGeneration: 2,
        Readiness: WireToGateSessionReadiness.RecoveryRequired,
        ReasonCodes: [],
        CapabilityVersion: 1,
        SafetyStateVersion: 1,
        UpdatedAt: Now);

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
