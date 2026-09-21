using System.ComponentModel;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 车辆停稳信号一变，界面上的扫码按钮与横幅跟着变，不等会话或旅程变化（8005-agv-onboard-hmi#177）。
/// </summary>
/// <remarks>
/// <para>
/// 扫码入口看停稳，而停稳一变，会话、旅程、录入请求都没变，原有的几条刷新路径一条也不会跑。第一版把订阅写在
/// <c>App.xaml.cs</c>，那里没有测试碰得到——审查把它删掉，一条测试都没红（M1）；而少了它，按钮要等服务端把会话降级
/// 才关，界面会同时挂着「暂停扫码」和一个可按的按钮。所以订阅挪进 <see cref="MainViewModel"/>，由这里守。
/// </para>
/// <para>
/// 断到「属性值 ＋ 以该属性名发出的变更通知」两件事，理由与 <see cref="RecoveryEntryNotificationViewModelTests"/> 同一条：
/// WPF 只在收到以绑定属性名命名的 <see cref="INotifyPropertyChanged.PropertyChanged"/> 时重读绑定。
/// </para>
/// <para>不带 trait：纯界面呈现，与 <see cref="SublotRejectionViewModelTests"/> 同一理由。</para>
/// </remarks>
public sealed class VehicleMotionEntryViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheScanButtonAndBannerFollowTheStopSignalWithoutAnyOtherRefresh()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        bool stopped = true;
        SignalSource signal = new();
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => stopped,
            sublotEntryPausedUntilStopped: () => !stopped,
            vehicleSafetySignal: signal);
        viewModel.UpdateWireToGateStatus(ReadySession());
        Assert.True(viewModel.CanSubmit);
        Assert.Equal("可扫码", viewModel.StateText);

        List<string?> changed = [];
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        stopped = false;
        signal.Raise();

        Assert.False(viewModel.CanSubmit);
        Assert.Contains(nameof(MainViewModel.CanSubmit), changed);
        Assert.Equal("暂停扫码", viewModel.StateText);

        changed.Clear();
        stopped = true;
        signal.Raise();

        Assert.True(viewModel.CanSubmit);
        Assert.Contains(nameof(MainViewModel.CanSubmit), changed);
        Assert.Equal("可扫码", viewModel.StateText);
    }

    /// <summary>
    /// 重新配置换了信号源，旧的那个不再驱动界面：订阅不会叠在一起，一次变化也不会被处理两次。
    /// </summary>
    [Fact]
    public async Task ReconfiguringMovesTheSubscriptionToTheNewSource()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        bool stopped = true;
        SignalSource first = new();
        SignalSource second = new();
        viewModel.ConfigureWireToGate((_, _, _) => Task.CompletedTask, () => stopped, vehicleSafetySignal: first);
        viewModel.ConfigureWireToGate((_, _, _) => Task.CompletedTask, () => stopped, vehicleSafetySignal: second);
        viewModel.UpdateWireToGateStatus(ReadySession());
        Assert.True(viewModel.CanSubmit);

        stopped = false;
        first.Raise();
        Assert.True(viewModel.CanSubmit);

        second.Raise();
        Assert.False(viewModel.CanSubmit);
        Assert.Equal(0, first.SubscriberCount);
        Assert.Equal(1, second.SubscriberCount);
    }

    private static WireToGateSessionSnapshot ReadySession() =>
        new(
            Connected: true,
            SessionGeneration: 1,
            Readiness: WireToGateSessionReadiness.Ready,
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
            StationDepartureCountdownDispatcher = null,
            Clock = new SystemClock()
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

    /// <summary>只会报「变了」；读数由各用例自己的 <c>stopped</c> 决定，因为入口读的是那个委托。</summary>
    private sealed class SignalSource : IObservableVehicleSafetySignalProvider
    {
        private EventHandler<ValueChangedEventArgs<VehicleSafetySignal>>? _handlers;

        public event EventHandler<ValueChangedEventArgs<VehicleSafetySignal>>? SignalChanged
        {
            add => _handlers += value;
            remove => _handlers -= value;
        }

        public int SubscriberCount => _handlers?.GetInvocationList().Length ?? 0;

        public VehicleSafetySignal Read() =>
            new(VehicleMotionState.Unknown, DateTimeOffset.UtcNow, "VIEW_MODEL_TEST");

        public void Raise() =>
            _handlers?.Invoke(this, new ValueChangedEventArgs<VehicleSafetySignal>(Read()));
    }

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
