using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 开锁后门没关的横幅在车载屏上的样子（<c>trytoreachpeak0/8005-agv-onboard-hmi#354</c>）：把真的 <c>MainWindow.xaml</c> 按车载屏的
/// 客户区（1024×768 最大化，减去标题栏与任务栏）摆出来量。
/// </summary>
/// <remarks>
/// <para>
/// 钉三件事：门锁着时横幅不占地方、平时布局一个像素不变；门开着时它在顶栏正下方、扫码框之上，大字一行放得下最长的两仓文案；
/// 它出现后底部操作记录仍是固定的 110，仓位区仍有地方。加载方式照 <c>WorklistScrollingLayoutTests</c>（onboard-hmi#353）：
/// 不 new <c>MainWindow</c>，按原文加载 XAML 并把 <c>App.xaml</c> 的资源原文放进来，理由在那里写着。
/// </para>
/// <para>不带 trait：纯界面呈现，不证明任何协议向量。</para>
/// </remarks>
public sealed class DoorOpenPromptLayoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithEveryDoorLockedTheBannerTakesNoRoom()
    {
        FakeIoModuleClient io = new();
        OnboardController controller = Controller(io);
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ApplyWireToGateOperatorEvent(Operation(3, 4));
        await controller.DisposeAsync();

        double height = OnStaThread(() => Banner(Lay(viewModel)).ActualHeight);

        Assert.Equal(0, height);
    }

    [Fact]
    public async Task AnOpenDoorPutsTheBannerUnderTheStatusBarAboveTheScanBoxOnOneLine()
    {
        FakeIoModuleClient io = new();
        OnboardController controller = Controller(io);
        MainViewModel viewModel = await ViewModel(controller);
        io.OpenDoor(2);
        io.OpenDoor(3);
        io.PublishSnapshot();
        viewModel.ApplyWireToGateOperatorEvent(Operation(3, 4));
        Assert.Equal("3、4号仓门开着：请取出篮子，然后关好仓门", viewModel.DoorOpenPromptHeadline);
        // The controller's own IO poll would otherwise append to the log while the STA thread has it bound.
        await controller.DisposeAsync();
        Assert.True(viewModel.HasDoorOpenPrompt);

        (double bannerTop, double bannerHeight, double scanTop, double headlineHeight, double headlineLine, double lockersHeight, double logHeight) =
            OnStaThread(() =>
            {
                FrameworkElement root = Lay(viewModel);
                Border banner = Banner(root);
                TextBox scan = Find<TextBox>(root, box => box.Name == "ScanTextBox")!;
                TextBlock headline = Find<TextBlock>(banner, block => block.Text == viewModel.DoorOpenPromptHeadline)!;
                Grid outer = (Grid)root;
                return (
                    banner.TranslatePoint(new Point(0, 0), root).Y,
                    banner.ActualHeight,
                    scan.TranslatePoint(new Point(0, 0), root).Y,
                    headline.ActualHeight,
                    headline.FontSize * headline.FontFamily.LineSpacing,
                    outer.RowDefinitions[4].ActualHeight,
                    outer.RowDefinitions[5].ActualHeight);
            });

        Assert.True(bannerHeight > 0);
        Assert.True(bannerTop + bannerHeight <= scanTop, $"banner {bannerTop}+{bannerHeight} should end above the scan box at {scanTop}");
        Assert.True(headlineHeight < 1.5 * headlineLine, $"the headline wrapped: {headlineHeight} for a line of {headlineLine}");
        Assert.Equal(110, logHeight);
        Assert.True(lockersHeight >= 180, $"the slot area shrank to {lockersHeight}");
    }

    /// <summary>
    /// The crowded case (review M3): the banner red, and the prompt area carrying the load cancellation entry, the
    /// expected-action-overdue line and the station departure countdown at once. At this window's client size and at the
    /// one <c>WorklistScrollingLayoutTests</c> assumes (review L8), the slot area keeps some height, nothing in the prompt
    /// area is cut off below its border, and the operator record keeps its 110.
    /// </summary>
    [Theory]
    [InlineData(1004, 660)]
    [InlineData(989, 640)]
    public async Task InTheCrowdedCaseNothingInThePromptAreaIsCutOff(int width, int height)
    {
        FakeIoModuleClient io = new();
        ManualClock clock = new(Now);
        OnboardController controller = Controller(io);
        MainViewModel viewModel = await ViewModel(controller, clock);
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => false,
            canRequestLoadCancellation: () => true,
            loadCancellationRequester: (_, _) => Task.FromResult(true));
        viewModel.UpdateWireToGateStatus(new WireToGateSessionSnapshot(
            Connected: true,
            SessionGeneration: 1,
            Readiness: WireToGateSessionReadiness.Ready,
            ReasonCodes: [],
            CapabilityVersion: 1,
            SafetyStateVersion: 1,
            UpdatedAt: Now));
        viewModel.UpdateWireToGateJourney(new WireToGateJourneySnapshot(
            null,
            new WireToGateCurrentStopWorklist(
                "ST-01",
                1,
                null,
                Now + TimeSpan.FromMinutes(5),
                [new WireToGateWorklistItem("11111111-1111-1111-1111-111111111111", "TD-001", "SUBLOT-001", "WIRE_TO_GATE", "DROP", 1)],
                new string('a', 64)),
            null,
            Now));
        viewModel.UpdateOnboardAlarms([new AlarmEntry(
            OnboardAlarmCodes.SlotExpectedActionOverdue,
            OnboardAlarmEvaluator.Warning,
            Now,
            AlarmScope.CurrentVehicle,
            "取出货物并关好3号仓门",
            SlotOperationAttemptId: "ATTEMPT-1",
            PhysicalSlotNumber: 3)]);
        io.OpenDoor(2);
        io.OpenDoor(3);
        io.PublishSnapshot();
        viewModel.ApplyWireToGateOperatorEvent(Operation(3, 4));
        clock.Advance(TimeSpan.FromSeconds(134));
        viewModel.RefreshDoorOpenPrompt();
        await controller.DisposeAsync();
        Assert.Equal(DoorOpenPromptTier.Overdue, viewModel.DoorOpenPromptTier);
        Assert.True(viewModel.CanRequestLoadCancellation);
        Assert.True(viewModel.HasExpectedActionOverdue);
        Assert.True(viewModel.HasStationDepartureCountdown);

        (double slotArea, double log, string[] cut) = OnStaThread(() =>
        {
            FrameworkElement root = Lay(viewModel, width, height);
            Grid outer = (Grid)root;
            Border prompt = Find<Border>(root, border => Grid.GetRow(border) == 3 && ReferenceEquals(VisualTreeHelper.GetParent(border), outer))!;
            double promptBottom = prompt.TranslatePoint(new Point(0, 0), root).Y + prompt.ActualHeight;
            FrameworkElement[] mustShow =
            [
                Find<Button>(root, button => button.Content as string == viewModel.LoadCancellationCaption)!,
                Find<TextBlock>(root, block => AutomationProperties.GetAutomationId(block) == "ExpectedActionOverdue")!,
                Find<TextBlock>(root, block => AutomationProperties.GetAutomationId(block) == "StationDepartureCountdown")!,
            ];
            string[] cutOff = [.. mustShow
                .Where(element => element.ActualHeight <= 0
                    || element.TranslatePoint(new Point(0, 0), root).Y + element.ActualHeight > promptBottom + 0.5)
                .Select(element => $"{element.GetType().Name} {AutomationProperties.GetAutomationId(element)} h={element.ActualHeight:0}")];
            return (outer.RowDefinitions[4].ActualHeight, outer.RowDefinitions[5].ActualHeight, cutOff);
        });

        Assert.Empty(cut);
        Assert.True(slotArea > 0, $"the slot area is gone at {width}x{height}");
        Assert.Equal(110, log);
    }

    private static Border Banner(FrameworkElement root) =>
        Find<Border>(root, border => AutomationProperties.GetAutomationId(border) == "DoorOpenPrompt")!;

    private static WireToGateOperatorEvent Operation(params int[] slots) =>
        new(Now, "OPERATION_PROGRESS", "进度", new WireToGateHmiOperationSnapshot(
            "ATTEMPT-1", OperationType.Unload, slots, WireToGateHmiOperationStage.WaitingOperator, "进度", Now));

    /// <summary>The window's content laid out at the onboard screen's client size (1024×768, maximized, title bar and taskbar).</summary>
    private static FrameworkElement Lay(MainViewModel viewModel) => Lay(viewModel, 1004, 660);

    private static FrameworkElement Lay(MainViewModel viewModel, double width, double height)
    {
        Window window = (Window)XamlReader.Parse(LoadableMainWindowXaml());
        FrameworkElement root = (FrameworkElement)window.Content;
        window.Content = null;
        root.Resources.MergedDictionaries.Add(window.Resources);
        root.DataContext = viewModel;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        return root;
    }

    private static string LoadableMainWindowXaml()
    {
        string app = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "SQCD.Agv.Wpf", "App.xaml"));
        string resources = Regex.Match(app, @"<Application\.Resources>(?<r>[\s\S]*)</Application\.Resources>").Groups["r"].Value;
        string xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "SQCD.Agv.Wpf", "MainWindow.xaml"));
        xaml = xaml
            .Replace("x:Class=\"SQCD.Agv.Wpf.MainWindow\"", string.Empty, StringComparison.Ordinal)
            .Replace("clr-namespace:SQCD.Agv.Wpf.ViewModels\"", "clr-namespace:SQCD.Agv.Wpf.ViewModels;assembly=SQCD.Agv.Wpf\"", StringComparison.Ordinal)
            .Replace("Background=\"{StaticResource PageBackgroundBrush}\"", string.Empty, StringComparison.Ordinal);
        xaml = Regex.Replace(xaml, @"\sClick=""[A-Za-z]+""", string.Empty);
        int body = xaml.IndexOf('>', xaml.IndexOf("<Window", StringComparison.Ordinal)) + 1;
        return xaml[..body] + "<Window.Resources>" + resources + "</Window.Resources>" + xaml[body..];
    }

    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("STA work failed", failure);
        }

        return result;
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool> match)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && match(typed))
            {
                return typed;
            }

            if (Find(child, match) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SQCD_8005AGV.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("No SQCD_8005AGV.sln above " + AppContext.BaseDirectory);
    }

    private static async Task<MainViewModel> ViewModel(OnboardController controller) =>
        await ViewModel(controller, new SystemClock());

    private static async Task<MainViewModel> ViewModel(OnboardController controller, IClock clock)
    {
        MainViewModel viewModel = new(
            controller,
            new RecordingLogger(),
            "agv02",
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()))
        {
            Clock = clock,
            StationDepartureCountdownDispatcher = null
        };
        await viewModel.InitializeAsync();
        return viewModel;
    }

    private static OnboardController Controller(FakeIoModuleClient io) => new(
        io,
        new NoRuleGateway(),
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

        public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class NoRuleGateway : IRuleGateway
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
