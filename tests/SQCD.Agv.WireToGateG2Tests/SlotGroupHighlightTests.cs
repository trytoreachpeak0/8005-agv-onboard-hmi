using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 仓位区组标题高亮（批次4（第二版）-02，<c>trytoreachpeak0/8005-agv-onboard-hmi#66</c>）：从卡片的
/// <c>IsTarget</c> 算出哪几组高亮、顶部标哪一侧，走 <see cref="MainViewModel"/> 真实的刷新路径。
/// </summary>
/// <remarks>
/// 分组与文案本身由单元测试 <c>SlotGroupPresentationTests</c> 覆盖；这里钉的是界面那一步——卡片的目标态有没有
/// 真的传到组标题上。放在本项目是因为 <see cref="MainViewModel"/> 在 <c>net8.0-windows</c> 的 WPF 程序集里。
/// 不带 <c>IntegrationSlice</c>／<c>ProtocolVector</c> trait：纯界面呈现，不证明任何协议向量。
/// </remarks>
public sealed class SlotGroupHighlightTests
{
    [Fact]
    public async Task OnlyTheRearGroupIsHighlightedWhenEveryTargetSlotIsAtTheRear()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);

        viewModel.ApplyWireToGateOperatorEvent(Operation(6, 8));

        Assert.Equal([false, true], viewModel.SlotGroups.Select(group => group.IsTarget));
        Assert.Equal("本次开门：后侧", viewModel.OpeningSideText);
        Assert.True(viewModel.HasOpeningSide);
    }

    [Fact]
    public async Task BothGroupsAreHighlightedWhenTheTargetsSpanFrontAndRear()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);

        viewModel.ApplyWireToGateOperatorEvent(Operation(3, 6));

        Assert.Equal([true, true], viewModel.SlotGroups.Select(group => group.IsTarget));
        Assert.Equal("本次开门：前后两侧", viewModel.OpeningSideText);
    }

    [Fact]
    public async Task TheHighlightClearsWhenTheOperationCompletes()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ApplyWireToGateOperatorEvent(Operation(2));

        viewModel.ApplyWireToGateOperatorEvent(Operation(WireToGateHmiOperationStage.Completed, 2));

        Assert.Equal([false, false], viewModel.SlotGroups.Select(group => group.IsTarget));
        Assert.Equal(string.Empty, viewModel.OpeningSideText);
        Assert.False(viewModel.HasOpeningSide);
    }

    private static async Task<MainViewModel> ViewModel(OnboardController controller)
    {
        MainViewModel viewModel = new(
            controller,
            new RecordingLogger(),
            "agv02",
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()));
        await viewModel.InitializeAsync();
        Assert.Equal(["前侧仓门（1～4 号）", "后侧仓门（5～8 号）"], viewModel.SlotGroups.Select(group => group.Title));
        Assert.All(viewModel.SlotGroups, group => Assert.False(group.IsTarget));
        return viewModel;
    }

    private static WireToGateOperatorEvent Operation(params int[] slots) =>
        Operation(WireToGateHmiOperationStage.Unlocking, slots);

    private static WireToGateOperatorEvent Operation(WireToGateHmiOperationStage stage, params int[] slots)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new WireToGateOperatorEvent(
            now,
            "OPERATION_PROGRESS",
            "正在打开仓门",
            new WireToGateHmiOperationSnapshot("ATTEMPT-1", OperationType.Load, slots, stage, "正在打开仓门", now));
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
