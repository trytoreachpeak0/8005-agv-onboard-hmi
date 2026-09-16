using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 故障态下还应该留给操作员的那一条退出路径：「取消装货」。
///
/// 原实现在控制器进入 <see cref="OnboardState.Faulted"/> 时把五个恢复入口一视同仁地关掉，
/// 而界面把按钮的可见性和可用性绑在同一个属性上，所以按钮不是变灰而是整个消失。现场的结果是
/// 操作员只能干等 5 分钟站点超时，而超时会把那张需求单永久抑制——比操作员主动取消更重的
/// 后果。也就是说出故障时系统强制走了后果更重的那条路（onboard-hmi#85）。
///
/// 放行的只是「本站还没开始装」这一条：它不开仓门、不动货，只发一条「这一站不装了」。
/// 要写恢复向量或驱动仓门的另外三个入口，故障态下仍然关闭。
/// </summary>
public sealed class MainViewModelFaultedRecoveryEntryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 16, 10, 3, 50, TimeSpan.FromHours(8));

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task FaultedHmiStillOffersCancellationForAStopWithNothingLoaded()
    {
        await using Rig rig = await Rig.CreateAsync(canCancelBeforeLoad: true);

        rig.Controller.EnterFatalFault("UI_COMMAND_FAILED", "操作界面出现异常。");

        // 先确认故障真的落到了界面上，否则下一条断言可能只是因为界面还没进故障态才绿。
        Assert.True(rig.ViewModel.HasError);
        Assert.True(rig.ViewModel.CanRequestLoadCancellation);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task FaultedHmiKeepsTheRecoveryEntriesThatTouchCargoClosed()
    {
        // 这三个要么先写入恢复向量、要么要驱动仓门，故障态下放行等于让一个自称状态不可信的
        // 界面去开锁。判据本身在替身里全给 true，关掉它们的只能是故障态这一层。
        await using Rig rig = await Rig.CreateAsync(canCancelBeforeLoad: true);

        rig.Controller.EnterFatalFault("UI_COMMAND_FAILED", "操作界面出现异常。");

        Assert.False(rig.ViewModel.CanRequestWireToGateRecovery);
        Assert.False(rig.ViewModel.CanRequestLoadCompensation);
        Assert.False(rig.ViewModel.CanRequestLoadCorrection);
        Assert.False(rig.ViewModel.CanRequestFaultCargoHandoff);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task FaultedHmiWithdrawsTheCancellationOnceTheStopHasSomethingCommanded()
    {
        // 已经对仓位下过命令之后，取消走的是要清空仓位的长路径，那条路仍然属于恢复，故障态下
        // 不放行。业务侧的 CanRequestLoadCancellationBeforeLoad 此时返回 false，界面照它走。
        await using Rig rig = await Rig.CreateAsync(canCancelBeforeLoad: false);

        rig.Controller.EnterFatalFault("UI_COMMAND_FAILED", "操作界面出现异常。");

        Assert.False(rig.ViewModel.CanRequestLoadCancellation);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task AServerSublotEntryRefreshDoesNotReopenTheClosedRecoveryEntries()
    {
        // 回归点：服务端请求录入子批时走的是另一个刷新入口，它原来根本不判故障态，于是故障态下
        // 五个按钮会随「哪一次刷新排在最后」忽隐忽现。现在两个入口共用同一份故障态判定。
        await using Rig rig = await Rig.CreateAsync(canCancelBeforeLoad: true);
        rig.Controller.EnterFatalFault("UI_COMMAND_FAILED", "操作界面出现异常。");

        rig.ViewModel.RefreshWireToGateInputState();

        Assert.True(rig.ViewModel.CanRequestLoadCancellation);
        Assert.False(rig.ViewModel.CanRequestLoadCompensation);
        Assert.False(rig.ViewModel.CanRequestLoadCorrection);
        Assert.False(rig.ViewModel.CanRequestFaultCargoHandoff);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task AHealthyHmiStillTakesEveryEntryFromTheBusinessPredicates()
    {
        // 正常态一个字没改：这四个入口仍然完全由业务侧判据决定。
        await using Rig rig = await Rig.CreateAsync(canCancelBeforeLoad: true);

        rig.ViewModel.RefreshWireToGateInputState();

        Assert.True(rig.ViewModel.CanRequestLoadCancellation);
        Assert.True(rig.ViewModel.CanRequestLoadCompensation);
        Assert.True(rig.ViewModel.CanRequestLoadCorrection);
        Assert.True(rig.ViewModel.CanRequestFaultCargoHandoff);
    }

    private sealed class Rig(OnboardController controller, MainViewModel viewModel) : IAsyncDisposable
    {
        internal OnboardController Controller { get; } = controller;

        internal MainViewModel ViewModel { get; } = viewModel;

        internal static async Task<Rig> CreateAsync(bool canCancelBeforeLoad)
        {
            OnboardController controller = new(
                new FakeIoModuleClient(),
                new InertRuleGateway(),
                new SilentLogger(),
                new SystemClock(),
                new OnboardWorkflowOptions(
                    TimeSpan.FromMilliseconds(50),
                    TimeSpan.FromMilliseconds(50),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    128,
                    2));
            MainViewModel viewModel = new(
                controller,
                new SilentLogger(),
                new SystemClock(),
                "AGV-8005-01");
            viewModel.UpdateWireToGateStatus(new WireToGateSessionSnapshot(
                true,
                1,
                WireToGateSessionReadiness.Ready,
                [],
                1,
                1,
                Now));
            viewModel.ConfigureWireToGate(
                (_, _, _) => Task.CompletedTask,
                () => true,
                canRequestRecovery: () => true,
                canRequestLoadCancellation: () => true,
                canRequestLoadCompensation: () => true,
                canRequestLoadCorrection: () => true,
                canRequestFaultCargoHandoff: () => true,
                canRequestLoadCancellationBeforeLoad: () => canCancelBeforeLoad);
            // 界面是在 InitializeAsync 里才订阅控制器快照的，不跑它就看不到故障态。
            await viewModel.InitializeAsync();
            return new Rig(controller, viewModel);
        }

        public async ValueTask DisposeAsync() => await Controller.DisposeAsync();
    }
}
