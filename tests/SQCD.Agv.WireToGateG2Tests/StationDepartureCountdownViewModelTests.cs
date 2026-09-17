using System.Windows.Threading;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 提示区的离站期限倒计时（批次5-26，<c>trytoreachpeak0/8005-agv-onboard-hmi#75</c>）：服务端快照里的
/// <c>stationDepartureDeadlineAt</c> 经 <see cref="MainViewModel"/> 真实的更新路径变成界面上的一行字。
/// </summary>
/// <remarks>
/// 五种显示状态本身由单元测试 <c>StationDepartureCountdownFormatterTests</c> 覆盖；这里钉的是界面那一步——
/// 期限只取最新快照、车载端不自己延长或作废，以及留给 <c>#78</c> 的覆盖入口。放在本项目是因为
/// <see cref="MainViewModel"/> 在 <c>net8.0-windows</c> 的 WPF 程序集里。不带 <c>IntegrationSlice</c>／
/// <c>ProtocolVector</c> trait：纯界面呈现，不证明任何协议向量。
/// </remarks>
public sealed class StationDepartureCountdownViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NoWorklistYetHidesTheCountdown()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, new ManualClock(Now));

        Assert.False(viewModel.HasStationDepartureCountdown);

        viewModel.UpdateWireToGateJourney(WireToGateJourneySnapshot.Empty);

        Assert.False(viewModel.HasStationDepartureCountdown);
    }

    [Fact]
    public async Task TheServerDeadlineIsCountedDownAgainstTheOnboardClock()
    {
        ManualClock clock = new(Now);
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, clock);

        viewModel.UpdateWireToGateJourney(Journey(Now + TimeSpan.FromMinutes(2)));

        Assert.True(viewModel.HasStationDepartureCountdown);
        AssertCountdown(viewModel, "02:00", StationDepartureCountdownTier.Normal);

        clock.Advance(TimeSpan.FromSeconds(75));
        viewModel.RefreshStationDepartureCountdown();
        AssertCountdown(viewModel, "00:45", StationDepartureCountdownTier.Warning);

        clock.Advance(TimeSpan.FromSeconds(40));
        viewModel.RefreshStationDepartureCountdown();
        AssertCountdown(viewModel, "00:05", StationDepartureCountdownTier.Critical);

        clock.Advance(TimeSpan.FromSeconds(30));
        viewModel.RefreshStationDepartureCountdown();
        AssertCountdown(viewModel, "已到期，等待本站结束", StationDepartureCountdownTier.Expired);
        Assert.False(viewModel.StationDepartureCountdownDimmed);
    }

    [Fact]
    public async Task ARefilledDeadlineReplacesTheOldOneAfterALoadSubmission()
    {
        // 装货提交后服务端重新计满，随下一份快照下发。车载端不能把第一次收到的期限当成本站定值。
        ManualClock clock = new(Now);
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, clock);
        viewModel.UpdateWireToGateJourney(Journey(Now + TimeSpan.FromSeconds(5)));
        AssertCountdown(viewModel, "00:05", StationDepartureCountdownTier.Critical);

        viewModel.UpdateWireToGateJourney(Journey(Now + TimeSpan.FromMinutes(5), revision: 2));

        AssertCountdown(viewModel, "05:00", StationDepartureCountdownTier.Normal);
        Assert.False(viewModel.StationDepartureCountdownDimmed);
    }

    [Fact]
    public async Task AnExpiredStopFollowsTheNewDeadlineAfterReconnection()
    {
        // 断联作废、恢复后重新计满：到期那一档不是终态，新快照一到就回到倒计时。
        ManualClock clock = new(Now);
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, clock);
        viewModel.UpdateWireToGateJourney(Journey(Now - TimeSpan.FromSeconds(30)));
        AssertCountdown(viewModel, "已到期，等待本站结束", StationDepartureCountdownTier.Expired);

        viewModel.UpdateWireToGateJourney(Journey(null, revision: 2));
        AssertCountdown(viewModel, "无倒计时", StationDepartureCountdownTier.Absent);

        viewModel.UpdateWireToGateJourney(Journey(Now + TimeSpan.FromMinutes(3), revision: 3));
        AssertCountdown(viewModel, "03:00", StationDepartureCountdownTier.Normal);
    }

    [Fact]
    public async Task ADeadlineThatBecomesNullShowsNoCountdownAndStaysThatWayAsTimePasses()
    {
        // 期限变为空时车载端不沿用旧值继续倒数，也不自己推算一个新期限。
        ManualClock clock = new(Now);
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, clock);
        viewModel.UpdateWireToGateJourney(Journey(Now + TimeSpan.FromSeconds(30)));

        viewModel.UpdateWireToGateJourney(Journey(null, revision: 2));
        clock.Advance(TimeSpan.FromMinutes(10));
        viewModel.RefreshStationDepartureCountdown();

        Assert.True(viewModel.HasStationDepartureCountdown);
        AssertCountdown(viewModel, "无倒计时", StationDepartureCountdownTier.Absent);
    }

    [Fact]
    public async Task TheLastTenSecondsFlashAsTheClockTicks()
    {
        ManualClock clock = new(Now);
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, clock);
        viewModel.UpdateWireToGateJourney(Journey(Now + TimeSpan.FromSeconds(9)));
        Assert.False(viewModel.StationDepartureCountdownDimmed);

        clock.Advance(TimeSpan.FromSeconds(1));
        viewModel.RefreshStationDepartureCountdown();
        Assert.True(viewModel.StationDepartureCountdownDimmed);

        clock.Advance(TimeSpan.FromSeconds(1));
        viewModel.RefreshStationDepartureCountdown();
        Assert.False(viewModel.StationDepartureCountdownDimmed);
    }

    [Fact]
    public async Task WithoutAnOverrideTheGenericTextIsShown()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, new ManualClock(Now));

        viewModel.UpdateWireToGateJourney(Journey(Now - TimeSpan.FromSeconds(1)));

        Assert.Null(viewModel.StationDepartureCountdownTextOverride);
        AssertCountdown(viewModel, "已到期，等待本站结束", StationDepartureCountdownTier.Expired);
    }

    [Fact]
    public async Task AnOverrideReplacesOnlyTheTextAndSeesTheServerDeadline()
    {
        // #78 的入口：它拿到服务端期限与当前时刻（要显示已过期多久），只换文字，档位仍由期限决定。
        ManualClock clock = new(Now);
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, clock);
        viewModel.UpdateWireToGateJourney(Journey(Now - TimeSpan.FromSeconds(42)));
        List<StationDepartureCountdownContext> seen = [];

        viewModel.StationDepartureCountdownTextOverride = context =>
        {
            seen.Add(context);
            return context.Generic.Tier == StationDepartureCountdownTier.Expired
                ? $"覆盖文案 {(context.Now - context.DeadlineAt!.Value).TotalSeconds:0}"
                : null;
        };

        AssertCountdown(viewModel, "覆盖文案 42", StationDepartureCountdownTier.Expired);
        StationDepartureCountdownContext first = Assert.Single(seen);
        Assert.Equal(Now - TimeSpan.FromSeconds(42), first.DeadlineAt);
        Assert.Equal(Now, first.Now);
        Assert.Equal("已到期，等待本站结束", first.Generic.Text);

        // 覆盖方返回 null 时回到通用文案。
        viewModel.UpdateWireToGateJourney(Journey(Now + TimeSpan.FromMinutes(1), revision: 2));
        AssertCountdown(viewModel, "01:00", StationDepartureCountdownTier.Warning);

        // 撤掉覆盖后也回到通用文案。
        viewModel.UpdateWireToGateJourney(Journey(Now - TimeSpan.FromSeconds(1), revision: 3));
        viewModel.StationDepartureCountdownTextOverride = null;
        AssertCountdown(viewModel, "已到期，等待本站结束", StationDepartureCountdownTier.Expired);
    }

    [Fact]
    public async Task TheTimerRunsOnlyWhileThereIsADeadline()
    {
        using DispatcherThread dispatcher = new();
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, new ManualClock(Now), dispatcher.Dispatcher);

        viewModel.UpdateWireToGateJourney(Journey(null));
        Assert.False(viewModel.IsStationDepartureCountdownTicking);

        viewModel.UpdateWireToGateJourney(Journey(Now + TimeSpan.FromMinutes(2), revision: 2));
        Assert.True(viewModel.IsStationDepartureCountdownTicking);

        // 到期之后仍有期限，定时器继续跑（#78 的覆盖文案要显示已过期多久）。
        viewModel.UpdateWireToGateJourney(Journey(Now - TimeSpan.FromSeconds(5), revision: 3));
        Assert.True(viewModel.IsStationDepartureCountdownTicking);

        viewModel.UpdateWireToGateJourney(Journey(null, revision: 4));
        Assert.False(viewModel.IsStationDepartureCountdownTicking);

        viewModel.UpdateWireToGateJourney(WireToGateJourneySnapshot.Empty);
        Assert.False(viewModel.IsStationDepartureCountdownTicking);
    }

    [Fact]
    public async Task TheRunningTimerRefreshesTheCountdownFromTheClock()
    {
        using DispatcherThread dispatcher = new();
        ManualClock clock = new(Now);
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, clock, dispatcher.Dispatcher);
        viewModel.UpdateWireToGateJourney(Journey(Now + TimeSpan.FromMinutes(2)));
        Assert.Equal("02:00", viewModel.StationDepartureCountdownText);

        // 不调 RefreshStationDepartureCountdown：只有定时器真的在跑，文字才会变。
        clock.Advance(TimeSpan.FromSeconds(75));

        Assert.True(
            SpinWait.SpinUntil(() => viewModel.StationDepartureCountdownText == "00:45", TimeSpan.FromSeconds(5)),
            $"定时器没有刷新，仍显示 {viewModel.StationDepartureCountdownText}");
    }

    [Fact]
    public async Task StoppingForAClosedWindowStopsTheTimerForGood()
    {
        using DispatcherThread dispatcher = new();
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, new ManualClock(Now), dispatcher.Dispatcher);
        viewModel.UpdateWireToGateJourney(Journey(Now + TimeSpan.FromMinutes(2)));
        Assert.True(viewModel.IsStationDepartureCountdownTicking);

        viewModel.StopStationDepartureCountdown();
        Assert.False(viewModel.IsStationDepartureCountdownTicking);

        // 窗口关了之后到达的快照不再把它启动起来，但显示照常更新。
        viewModel.UpdateWireToGateJourney(Journey(Now + TimeSpan.FromMinutes(5), revision: 2));
        Assert.False(viewModel.IsStationDepartureCountdownTicking);
        Assert.Equal("05:00", viewModel.StationDepartureCountdownText);
    }

    [Fact]
    public async Task WithoutADispatcherNoTimerIsCreated()
    {
        // 单元测试与没有 WPF 应用的宿主：不起定时器，靠 RefreshStationDepartureCountdown 手动重算。
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, new ManualClock(Now));

        viewModel.UpdateWireToGateJourney(Journey(Now + TimeSpan.FromMinutes(2)));

        Assert.False(viewModel.IsStationDepartureCountdownTicking);
    }

    private static void AssertCountdown(MainViewModel viewModel, string text, StationDepartureCountdownTier tier)
    {
        Assert.Equal(text, viewModel.StationDepartureCountdownText);
        Assert.Equal(tier, viewModel.StationDepartureCountdownTier);
    }

    private static WireToGateJourneySnapshot Journey(DateTimeOffset? deadlineAt, long revision = 1) => new(
        null,
        new WireToGateCurrentStopWorklist(
            "ST-01",
            revision,
            null,
            deadlineAt,
            [new WireToGateWorklistItem(
                "11111111-1111-1111-1111-111111111111",
                "TD-001",
                "SUBLOT-001",
                "WIRE_TO_GATE",
                "PICKUP",
                1)],
            new string('a', 64)),
        null,
        Now);

    private static async Task<MainViewModel> ViewModel(
        OnboardController controller,
        IClock clock,
        Dispatcher? dispatcher = null)
    {
        MainViewModel viewModel = new(
            controller,
            new RecordingLogger(),
            "agv02",
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()))
        {
            Clock = clock,
            StationDepartureCountdownDispatcher = dispatcher
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

    private sealed class ManualClock(DateTimeOffset now) : IClock
    {
        private long _ticks = now.UtcTicks;

        // 定时器在另一条线程上读，推进与读取都走原子操作。
        public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>
    /// 一条跑着消息循环的线程，给定时器一个真的会触发的 Dispatcher。测试进程里没有 WPF 应用，不能借 UI 线程。
    /// </summary>
    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;

        public DispatcherThread()
        {
            using ManualResetEventSlim ready = new();
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            Dispatcher = dispatcher!;
        }

        public Dispatcher Dispatcher { get; }

        public void Dispose()
        {
            Dispatcher.InvokeShutdown();
            _thread.Join();
        }
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
            throw new InvalidOperationException("本测试不扫码。");

        public Task<bool> ReportOperationAsync(OperationResult result, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("本测试不上报。");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
