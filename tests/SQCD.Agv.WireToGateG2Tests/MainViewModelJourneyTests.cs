using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 旅程快照落到界面上的两处：作业清单那一行文字，和状态条下方的行程带。
///
/// 清单那一行以前取 <c>Items.SingleOrDefault()</c>，两项就抛——而这条路径跑在 UI 线程的快照更新
/// 回调里，抛出去的结果不是报错而是**界面停在上一次的文字上**，操作员看不出这一停靠有几项，也
/// 看不出清单已经换了。行程带则是新的：<c>UpcomingStopPlan</c> 在 WPF 层此前一处引用都没有。
/// </summary>
public sealed class MainViewModelJourneyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void MultiItemWorklistListsEveryPendingSublot()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(Journey("SUBLOT-001", "SUBLOT-002", "SUBLOT-003"));

        Assert.Equal("ST-01 / 3 项：SUBLOT-001、SUBLOT-002、SUBLOT-003", viewModel.VisitText);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void SingleItemWorklistStaysOnTheOneLineForm()
    {
        // 单项仍然只写子批本身，不写「1 项：」——绝大多数停靠是单项，多出来的计数只是噪音。
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(Journey("SUBLOT-001"));

        Assert.Equal("ST-01 / SUBLOT-001", viewModel.VisitText);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void EmptyWorklistSaysSoInsteadOfLookingLikeAnUnsyncedJourney()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(Journey());

        Assert.Equal("ST-01 / 无待处理任务", viewModel.VisitText);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void JourneyWithoutAWorklistIsReportedAsUnsynced()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(WireToGateJourneySnapshot.Empty);

        Assert.Equal("旅程未同步", viewModel.VisitText);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void ASecondWorklistReplacesTheTextRatherThanLeavingTheStaleOne()
    {
        // 回归点：旧实现在第二份清单（两项）上抛异常，界面因此还留着第一份的文字。
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(Journey("SUBLOT-001"));
        viewModel.UpdateWireToGateJourney(Journey("SUBLOT-002", "SUBLOT-003"));

        Assert.Equal("ST-01 / 2 项：SUBLOT-002、SUBLOT-003", viewModel.VisitText);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void UpcomingPlanRendersEveryLegInSequenceOrder()
    {
        // 协议保证 sequence 从 1 起连续，但不保证数组本身有序——这里故意倒着发。
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(JourneyWithLegs(
            Leg(2, "TO_GATE", "ST-09", "PLANNED"),
            Leg(1, "TO_PICKUP", "ST-01", "ACTIVE")));

        Assert.True(viewModel.HasUpcomingPlan);
        Assert.Equal([1, 2], viewModel.UpcomingLegs.Select(leg => leg.Sequence));
        Assert.Equal(["取货", "交货"], viewModel.UpcomingLegs.Select(leg => leg.LegTypeText));
        Assert.Equal(["ST-01", "ST-09"], viewModel.UpcomingLegs.Select(leg => leg.StationId));
        Assert.Equal(["行进中", "待走"], viewModel.UpcomingLegs.Select(leg => leg.StateText));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void OnlyTheLegTheVehicleIsOnIsMarkedCurrent()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(JourneyWithLegs(
            Leg(1, "TO_PICKUP", "ST-01", "COMPLETED"),
            Leg(2, "TO_GATE", "ST-09", "ARRIVED")));

        Assert.Equal([false, true], viewModel.UpcomingLegs.Select(leg => leg.IsCurrent));
        Assert.Equal([true, false], viewModel.UpcomingLegs.Select(leg => leg.IsDone));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void BlockedLegIsCalledOutSeparatelyFromTheCurrentOne()
    {
        // 受阻要与「正在走」区分开：两者都不是完成，但操作员对它们要做的事不一样。
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(JourneyWithLegs(Leg(1, "TO_PICKUP", "ST-01", "BLOCKED")));

        StopLegViewModel leg = Assert.Single(viewModel.UpcomingLegs);
        Assert.True(leg.IsBlocked);
        Assert.False(leg.IsCurrent);
        Assert.Equal("受阻", leg.StateText);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void JourneyWithoutAPlanCollapsesTheBand()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(WireToGateJourneySnapshot.Empty);

        Assert.False(viewModel.HasUpcomingPlan);
        Assert.Empty(viewModel.UpcomingLegs);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void ASecondPlanReplacesTheBandRatherThanAppendingToIt()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(JourneyWithLegs(
            Leg(1, "TO_PICKUP", "ST-01", "ACTIVE"),
            Leg(2, "TO_GATE", "ST-09", "PLANNED")));
        viewModel.UpdateWireToGateJourney(JourneyWithLegs(Leg(1, "TO_GATE", "ST-09", "ACTIVE")));

        StopLegViewModel leg = Assert.Single(viewModel.UpcomingLegs);
        Assert.Equal("ST-09", leg.StationId);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void ServerDeadlineBecomesACountdownOnTheStopBanner()
    {
        // 决策 3：站点能停多久由服务端掌握，车载端只显示。这里锁的是「显示的是服务端那个时刻」。
        MainViewModel viewModel = CreateViewModel(new FixedClock(Now));

        viewModel.UpdateWireToGateJourney(JourneyWithDeadline(Now + TimeSpan.FromMinutes(3)));

        Assert.True(viewModel.HasStationDepartureCountdown);
        Assert.Equal("03:00", viewModel.StationDepartureCountdownText);
        Assert.Equal("Normal", viewModel.StationDepartureCountdownTier);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void AWorklistWithoutADeadlineStillShowsTheBlockSayingThereIsNone()
    {
        // 纯卸货站与关卡站没有期限。整块藏起来会让「服务端没给期限」与「清单还没到」看着一样。
        MainViewModel viewModel = CreateViewModel(new FixedClock(Now));

        viewModel.UpdateWireToGateJourney(JourneyWithDeadline(null));

        Assert.True(viewModel.HasStationDepartureCountdown);
        Assert.Equal("无倒计时", viewModel.StationDepartureCountdownText);
        Assert.Equal("Absent", viewModel.StationDepartureCountdownTier);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void JourneyWithoutAWorklistHidesTheCountdownEntirely()
    {
        MainViewModel viewModel = CreateViewModel(new FixedClock(Now));

        viewModel.UpdateWireToGateJourney(WireToGateJourneySnapshot.Empty);

        Assert.False(viewModel.HasStationDepartureCountdown);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void AnExpiredDeadlineKeepsTheVehicleWaitingInsteadOfShowingANegativeNumber()
    {
        // ADR-cross-0058 决策 4 之后期限到期不必然结束本站：仓门未闭时服务端只发告警并继续等。
        MainViewModel viewModel = CreateViewModel(new FixedClock(Now));

        viewModel.UpdateWireToGateJourney(JourneyWithDeadline(Now - TimeSpan.FromSeconds(90)));

        Assert.Equal("已到期，等待服务端结算", viewModel.StationDepartureCountdownText);
        Assert.Equal("Expired", viewModel.StationDepartureCountdownTier);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void ASecondWorklistThatPushesTheDeadlineOutIsFollowed()
    {
        // 每批 LoadBatch 闭环后服务端重置计时并随下一份清单重发，断联也会先清空再重填。
        // 界面不能把第一次收到的期限当成本次停靠的定值。
        MainViewModel viewModel = CreateViewModel(new FixedClock(Now));

        viewModel.UpdateWireToGateJourney(JourneyWithDeadline(Now + TimeSpan.FromSeconds(5)));
        Assert.Equal("Critical", viewModel.StationDepartureCountdownTier);

        viewModel.UpdateWireToGateJourney(JourneyWithDeadline(Now + TimeSpan.FromMinutes(5)));

        Assert.Equal("05:00", viewModel.StationDepartureCountdownText);
        Assert.Equal("Normal", viewModel.StationDepartureCountdownTier);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void TheCountdownIsRecomputedFromTheClockRatherThanCachedAtArrival()
    {
        // 定时器每 250 ms 调的就是这个方法。这里用一个会走的时钟替身代替定时器，锁「重算」这件事。
        MovingClock clock = new(Now);
        MainViewModel viewModel = CreateViewModel(clock);

        viewModel.UpdateWireToGateJourney(JourneyWithDeadline(Now + TimeSpan.FromMinutes(2)));
        Assert.Equal("02:00", viewModel.StationDepartureCountdownText);

        clock.Advance(TimeSpan.FromSeconds(115));
        viewModel.RefreshStationDepartureCountdown();

        Assert.Equal("00:05", viewModel.StationDepartureCountdownText);
        Assert.Equal("Critical", viewModel.StationDepartureCountdownTier);
    }

    private static MainViewModel CreateViewModel(IClock? clock = null) => new(
        new OnboardController(
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
                2)),
        new SilentLogger(),
        clock ?? new SystemClock(),
        "AGV-8005-01");

    private static WireToGateJourneySnapshot Journey(params string[] sublots) => new(
        new WireToGateVehicleBusinessState(1, "READY", false, "SUFFICIENT", [], Now, "sha"),
        new WireToGateCurrentStopWorklist(
            "ST-01",
            1,
            "33333333-3333-3333-3333-333333333333",
            null,
            sublots
                .Select(sublot => new WireToGateWorklistItem(
                    "D-1",
                    $"TD-{sublot}",
                    sublot,
                    "LOAD",
                    "PICKUP",
                    1))
                .ToArray(),
            "sha"),
        new WireToGateUpcomingStopPlan(
            1,
            "D-1",
            [new WireToGateMovementLeg("L-1", "TO_PICKUP", 1, "ST-01", "MAP-01", "ACTIVE")],
            "sha"),
        Now);

    private static WireToGateJourneySnapshot JourneyWithDeadline(DateTimeOffset? deadlineAt)
    {
        WireToGateJourneySnapshot journey = Journey("SUBLOT-001");
        return journey with
        {
            CurrentStopWorklist = journey.CurrentStopWorklist! with
            {
                StationDepartureDeadlineAt = deadlineAt
            }
        };
    }

    private static WireToGateJourneySnapshot JourneyWithLegs(params WireToGateMovementLeg[] legs) =>
        Journey("SUBLOT-001") with
        {
            UpcomingStopPlan = new WireToGateUpcomingStopPlan(1, "D-1", legs, "sha")
        };

    private static WireToGateMovementLeg Leg(
        int sequence,
        string legType,
        string stationId,
        string state) =>
        new($"leg-{sequence}", legType, sequence, stationId, "MAP-01", state);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now => now;
    }

    private sealed class MovingClock(DateTimeOffset start) : IClock
    {
        private DateTimeOffset _now = start;

        public DateTimeOffset Now => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>
    /// 这几条测试只走快照到界面状态这一段，规则网关一次也不会被碰到。
    /// </summary>
    private sealed class InertRuleGateway : IRuleGateway
    {
        public bool IsConnected => false;

        public VisitContext? CurrentVisit => null;

        public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

        public event EventHandler<ValueChangedEventArgs<VisitContext?>>? VisitChanged;

        public Task StartAsync(CancellationToken applicationStopping)
        {
            ConnectionChanged?.Invoke(this, new ValueChangedEventArgs<bool>(false));
            VisitChanged?.Invoke(this, new ValueChangedEventArgs<VisitContext?>(null));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ScanAuthorization> VerifyScanAsync(
            ScanVerificationRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ReportOperationAsync(
            OperationResult result,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SilentLogger : IAppLogger
    {
        public event EventHandler<LogEntryEventArgs>? EntryWritten;

        public void Write(
            LogSeverity severity,
            string source,
            string message,
            Exception? exception = null)
        {
            EntryWritten?.Invoke(
                this,
                new LogEntryEventArgs(new LogEntry(DateTimeOffset.Now, severity, source, message)));
        }
    }
}
