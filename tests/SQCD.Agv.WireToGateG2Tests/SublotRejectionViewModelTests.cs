using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 拒收原因进提示区（批次5-28，<c>trytoreachpeak0/8005-agv-onboard-hmi#77</c>）：业务服务手里的拒收经
/// <see cref="MainViewModel"/> 真实的事件路径变成提示区的一行字，UIA 按 <c>ItemStatus</c> 读原因码。
/// </summary>
/// <remarks>
/// 拒收本身的行为由 <see cref="SublotRejectedAfterEntryG2Tests"/> 证明，这里只钉界面那一步。放在本项目是因为
/// <see cref="MainViewModel"/> 在 <c>net8.0-windows</c> 的 WPF 程序集里。不带 trait：纯界面呈现。
/// </remarks>
public sealed class SublotRejectionViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARejectionIsShownInThePromptAreaWithItsReasonCode()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        WireToGateSublotRejection? rejection = null;
        bool canSubmit = true;
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => canSubmit,
            sublotRejection: () => rejection);
        viewModel.UpdateWireToGateStatus(ReadySession());
        Assert.False(viewModel.HasSublotRejection);
        Assert.Equal("可扫码", viewModel.StateText);

        rejection = Rejection("PACKAGE_CAPACITY_UNRESOLVED", kept: false);
        canSubmit = false;
        viewModel.ApplyWireToGateOperatorEvent(new(Now, "SUBLOT_REJECTED", "子批被拒收"));

        Assert.True(viewModel.HasSublotRejection);
        Assert.Equal(
            "子批 SUBLOT-001 被服务端拒收：该 PACKAGE 的花篮容量未登记或有冲突。",
            viewModel.SublotRejectionText);
        Assert.Equal("PACKAGE_CAPACITY_UNRESOLVED", viewModel.SublotRejectionReasonCode);
        Assert.Equal("子批被拒收", viewModel.StateText);
        Assert.Equal("本站录入清单已变化，等待服务端新的录入请求。", viewModel.Guidance);
        Assert.True(viewModel.HasWarning);
    }

    /// <summary>
    /// <c>SLOT_INOPERABLE</c> 经 <see cref="MainViewModel"/> 真实路径进提示区：原因一行说仓位不足，下一步说取消并联系维护人员，
    /// 录入请求保留时也不说「重新扫码」（onboard-hmi#366）。
    /// </summary>
    [Fact]
    public async Task AnInoperableSlotRejectionTellsTheOperatorToCancelEvenWhenTheEntryRequestIsKept()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        WireToGateSublotRejection? rejection = Rejection("SLOT_INOPERABLE", kept: true);
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => true,
            sublotRejection: () => rejection);

        viewModel.UpdateWireToGateStatus(ReadySession());

        Assert.Equal("子批 SUBLOT-001 被服务端拒收：可用的空仓位不足，有仓位已被停用。", viewModel.SublotRejectionText);
        Assert.Equal("SLOT_INOPERABLE", viewModel.SublotRejectionReasonCode);
        Assert.Equal("子批被拒收", viewModel.StateText);
        Assert.Equal("重扫也无法装入，请取消该需求并联系系统维护人员。", viewModel.Guidance);
    }

    /// <summary>
    /// <c>VEHICLE_NOT_READY</c>（服务端读不准车位，control-server#783）经 <see cref="MainViewModel"/> 真实路径进提示区：
    /// 原因一行说车还没就绪，下一步说等车停稳，录入请求保留时也不说「重新扫码」（onboard-hmi#377）。
    /// </summary>
    [Fact]
    public async Task AVehicleNotReadyRejectionTellsTheOperatorToWaitForTheVehicleEvenWhenTheEntryRequestIsKept()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        WireToGateSublotRejection? rejection = Rejection("VEHICLE_NOT_READY", kept: true);
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => true,
            sublotRejection: () => rejection);

        viewModel.UpdateWireToGateStatus(ReadySession());

        Assert.Equal(
            "子批 SUBLOT-001 被服务端拒收：车还没就绪（仍在移动或位置未确认），暂时不能录入。",
            viewModel.SublotRejectionText);
        Assert.Equal("VEHICLE_NOT_READY", viewModel.SublotRejectionReasonCode);
        Assert.Equal("子批被拒收", viewModel.StateText);
        Assert.Equal("请等车在本站停稳、就绪后再扫码；持续出现请联系系统维护人员。", viewModel.Guidance);
    }

    [Fact]
    public async Task AWithdrawnRejectionLeavesThePromptArea()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        WireToGateSublotRejection? rejection = Rejection("SUBLOT_NOT_IN_DISPATCH_SCOPE", kept: true);
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => true,
            sublotRejection: () => rejection);
        viewModel.UpdateWireToGateStatus(ReadySession());
        Assert.True(viewModel.HasSublotRejection);
        Assert.Equal("请核对物料后重新扫码。", viewModel.Guidance);

        rejection = null;
        viewModel.ApplyWireToGateOperatorEvent(new(Now, "SUBLOT_SUBMITTED", "子批已提交"));

        Assert.False(viewModel.HasSublotRejection);
        Assert.Equal(string.Empty, viewModel.SublotRejectionText);
        Assert.Equal(string.Empty, viewModel.SublotRejectionReasonCode);
        Assert.Equal("可扫码", viewModel.StateText);
    }

    private static WireToGateSublotRejection Rejection(string reasonCode, bool kept) => new(
        "22222222-2222-4222-8222-222222222222",
        null,
        "77777777-7777-4777-8777-777777777777",
        reasonCode,
        1,
        "SUBLOT-001",
        kept,
        Now);

    private static WireToGateSessionSnapshot ReadySession() => new(
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
