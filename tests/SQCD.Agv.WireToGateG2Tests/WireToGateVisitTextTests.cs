using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 作业清单在界面上的那一行。它以前取 <c>Items.SingleOrDefault()</c>，两项就抛——而这条路径跑在
/// UI 线程的快照更新回调里，抛出去的结果不是报错而是**界面停在上一次的文字上**，操作员看不出这
/// 一停靠有几项，也看不出清单已经换了。
/// </summary>
public sealed class WireToGateVisitTextTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MultiItemWorklistListsEveryPendingSublot()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(Journey("SUBLOT-001", "SUBLOT-002", "SUBLOT-003"));

        Assert.Equal("ST-01 / 3 项：SUBLOT-001、SUBLOT-002、SUBLOT-003", viewModel.VisitText);
    }

    [Fact]
    public void SingleItemWorklistStaysOnTheOneLineForm()
    {
        // 单项仍然只写子批本身，不写「1 项：」——绝大多数停靠是单项，多出来的计数只是噪音。
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(Journey("SUBLOT-001"));

        Assert.Equal("ST-01 / SUBLOT-001", viewModel.VisitText);
    }

    [Fact]
    public void EmptyWorklistSaysSoInsteadOfLookingLikeAnUnsyncedJourney()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(Journey());

        Assert.Equal("ST-01 / 无待处理任务", viewModel.VisitText);
    }

    [Fact]
    public void JourneyWithoutAWorklistIsReportedAsUnsynced()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(WireToGateJourneySnapshot.Empty);

        Assert.Equal("旅程未同步", viewModel.VisitText);
    }

    [Fact]
    public void ASecondWorklistReplacesTheTextRatherThanLeavingTheStaleOne()
    {
        // 回归点：旧实现在第二份清单（两项）上抛异常，界面因此还留着第一份的文字。
        MainViewModel viewModel = CreateViewModel();

        viewModel.UpdateWireToGateJourney(Journey("SUBLOT-001"));
        viewModel.UpdateWireToGateJourney(Journey("SUBLOT-002", "SUBLOT-003"));

        Assert.Equal("ST-01 / 2 项：SUBLOT-002、SUBLOT-003", viewModel.VisitText);
    }

    private static MainViewModel CreateViewModel() => new(
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
        "AGV-8005-01");

    private static WireToGateJourneySnapshot Journey(params string[] sublots) => new(
        new WireToGateVehicleBusinessState(1, "READY", false, "SUFFICIENT", [], Now, "sha"),
        new WireToGateCurrentStopWorklist(
            "ST-01",
            1,
            "33333333-3333-3333-3333-333333333333",
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

    /// <summary>
    /// 这几条测试只走快照到文字这一段，规则网关一次也不会被碰到。
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
