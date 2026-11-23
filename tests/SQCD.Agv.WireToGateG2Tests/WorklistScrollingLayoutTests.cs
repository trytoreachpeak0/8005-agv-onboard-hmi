using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 左栏清单滚得动（<c>8005-agv-onboard-hmi#353</c>）：待送点清单「显示不全」的两个原因之一，是清单自带的滚动区吞掉了滚轮与手指
/// 拖动，而它自己从来不用滚，操作员只能去拖外层那根细滚动条。这里把真的 <c>MainWindow.xaml</c> 按真的视图模型摆出来量。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不 new 一个 <c>MainWindow</c>。</b>它的资源在 <c>App.xaml</c> 里，要有一个 <c>Application</c> 才找得到；而一个进程只能有一个
/// <c>Application</c>，视图模型的 <c>RunOnUiThread</c> 又认 <c>Application.Current</c>，建了它，这个程序集里别的用例全都换了线程走法。
/// 所以按原文加载 <c>MainWindow.xaml</c>：去掉 <c>x:Class</c> 与 <c>Click</c> 两样只有代码后置才认得的东西，把 <c>App.xaml</c> 的资源
/// 原文放进窗口自己的资源里。两份文件都从仓库读，布局与样式就是产品的。
/// </para>
/// <para>
/// <b>手指拖动测的是什么。</b>触摸平移由 WPF 的 manipulation 交给离手指最近的、开了 <c>PanningMode</c> 的 <c>ScrollViewer</c>，
/// 合成真的触摸事件这里做不到。所以断言的是那个前提：从清单一行往上找到的第一个 <c>ScrollViewer</c> 就是左栏那个、它允许竖向平移；
/// 中间多一个清单自带的滚动区，拖动就落在那个不会滚的滚动区上——那正是改之前的样子。
/// </para>
/// </remarks>
public sealed class WorklistScrollingLayoutTests
{
    [Fact]
    public async Task TheWheelOverAWorklistRowScrollsTheLeftColumn()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await EightRowViewModelAsync(controller);

        double offset = OnStaThread(() =>
        {
            (FrameworkElement root, ListBoxItem row, ScrollViewer column) = Lay(viewModel);
            Assert.True(column.ScrollableHeight > 0, $"eight rows should not fit: extent {column.ExtentHeight}, viewport {column.ViewportHeight}");
            row.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = UIElement.MouseWheelEvent
            });
            root.UpdateLayout();
            return column.VerticalOffset;
        });

        Assert.True(offset > 0, "the wheel over a row did not move the left column");
    }

    [Fact]
    public async Task AFingerDraggedOnAWorklistRowIsTakenByTheLeftColumnWhichPansVertically()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await EightRowViewModelAsync(controller);

        (string? nearestScroller, PanningMode panning) = OnStaThread(() =>
        {
            (_, ListBoxItem row, ScrollViewer column) = Lay(viewModel);
            ScrollViewer nearest = Ancestor<ScrollViewer>(row)!;
            return (ReferenceEquals(nearest, column) ? "left column" : nearest.TemplatedParent?.GetType().Name, nearest.PanningMode);
        });

        Assert.Equal("left column", nearestScroller);
        Assert.Equal(PanningMode.VerticalOnly, panning);
    }

    /// <summary>
    /// 清单行的字整串显示（<c>8005-agv-onboard-hmi#426</c>，改写原 #417 审查 L1 那条）：现场最长的子批整串占一行，与它并排放不下的
    /// 机台号整块挪到下面一行，也不拆开；子批比整行还长时在行内换行、整串都在；机台号再长也整串显示、不出这一行。
    /// 量的是车载屏客户区上真排出来的三行。
    /// </summary>
    [Fact]
    public async Task EverySublotAndMachineNumberIsShownWholeAndTheMachineNumberMovesBelowWhenTheyDoNotFit()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);
        string longSublot = "LONG-SUBLOT-" + new string('9', 48);
        viewModel.UpdateWireToGateJourney(MultiDemandViewModelTests.Journey(MultiDemandViewModelTests.Worklist(
            1,
            MultiDemandViewModelTests.Item("aaaaaaaa-0000-4000-8000-000000000001", FieldLongestSublot, "WIRE_TO_FULL_INSPECTION", "PICKUP", 2) with { Eqp = FieldLongestEqp },
            MultiDemandViewModelTests.Item("aaaaaaaa-0000-4000-8000-000000000002", longSublot, "WIRE_TO_GATE", "PICKUP", 1) with { Eqp = "2ZPB101" },
            MultiDemandViewModelTests.Item("aaaaaaaa-0000-4000-8000-000000000003", "Q26096568-10", "WIRE_TO_GATE", "PICKUP", 1) with { Eqp = "MACHINE-NUMBER-FAR-LONGER-THAN-ANY-MES-SENDS-0123456789-ABCDEFGHIJ" })));

        (RowLayout[] rows, string[] cut) = OnStaThread(() =>
        {
            (FrameworkElement root, _, _) = Lay(viewModel);
            ListBox list = Find<ListBox>(root, element => AutomationProperties.GetAutomationId(element) == "WorklistItems")!;
            RowLayout[] laid =
            [
                .. Enumerable.Range(0, 3).Select(index =>
                {
                    ListBoxItem item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
                    TextBlock sublot = Find<TextBlock>(item, _ => true)!;
                    TextBlock eqp = Find<TextBlock>(item, block => AutomationProperties.GetAutomationId(block) == "WorklistItemEqp")!;
                    return new RowLayout(
                        sublot.Text, Box(sublot, root), LineHeight(sublot), eqp.Text, Box(eqp, root), LineHeight(eqp), Box(item, root));
                })
            ];
            return (laid, Cut(list, root));
        });

        Assert.Empty(cut);

        // The field's longest pair does not fit side by side on a staging pickup row (count, side and the cancel button take the
        // rest), so the machine number goes below; the sublot keeps a line of its own, unbroken, and so does the machine number.
        RowLayout field = rows[0];
        Assert.Equal(FieldLongestSublot, field.Sublot);
        Assert.True(field.SublotBox.Height < 1.5 * field.SublotLine, $"the field's longest sublot wrapped: {field}");
        Assert.True(field.EqpBox.Height < 1.5 * field.EqpLine, $"the field's longest machine number wrapped: {field}");

        RowLayout wrapped = rows[1];
        Assert.Equal(longSublot, wrapped.Sublot);
        Assert.True(wrapped.SublotBox.Height > 1.5 * wrapped.SublotLine, $"the long sublot should wrap inside the row: {wrapped}");
        Assert.True(wrapped.EqpBox.Top >= wrapped.SublotBox.Bottom - 0.5, $"the machine number should sit below a sublot that fills the row: {wrapped}");

        RowLayout longEqp = rows[2];
        Assert.True(longEqp.EqpBox.Top >= longEqp.SublotBox.Bottom - 0.5, $"an over-long machine number should move below the sublot: {longEqp}");
        Assert.All(rows, row => Assert.True(
            row.EqpBox.Right <= row.ItemBox.Right + 0.5 && row.SublotBox.Right <= row.ItemBox.Right + 0.5,
            $"text runs off the row: {row}"));
    }

    /// <summary>
    /// 现场最长的真实值摆在车载屏上，每一段看得见的字都整串显示（<c>8005-agv-onboard-hmi#426</c>）。值取自 v2 现场库发件箱的全量历史
    /// （<c>evidence/hmi-426/field-longest-values.txt</c>）：车名、子批、机台号、站点名、七种任务类型都在。设了环境变量
    /// <c>HMI_LAYOUT_SCREENSHOT_DIR</c> 时，把这一屏存成 PNG，证据截图就是这样出的。
    /// </summary>
    [Fact]
    public async Task TheFieldsLongestValuesAreShownWholeOnTheOnboardScreen()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await FieldVehicleViewModelAsync(controller);
        string[] workTypes =
            ["WIRE_TO_FULL_INSPECTION", "DIE_TO_WIRE_STAGING", "WIRE_TO_NITROGEN", "STAGING_TO_WIRE", "WIRE_TO_GATE", "WIRE_TO_OPTICAL", "DIE_TO_OVEN"];
        string[] sublots = [FieldLongestSublot, "Q26101619-10-2", "Q26101652-10-2", "Q26101619-11-1", "Q26101654-10-1", "Q26101654-11-1", "Q26101654-10-2"];
        string[] eqps = [FieldLongestEqp, "3QHS6022", "3QHS3751", "3QHS6016", "3QHS6015", "2ZPB105", "2ZPB101"];
        WireToGateWorklistItem[] items =
        [
            .. workTypes.Select((workType, index) => MultiDemandViewModelTests.Item(
                $"aaaaaaaa-0000-4000-8000-{index + 1:D12}", sublots[index], workType, "PICKUP", 2) with { Eqp = eqps[index] })
        ];
        viewModel.UpdateWireToGateJourney(MultiDemandViewModelTests.Journey(
            new WireToGateCurrentStopWorklist(FieldLongestStation, 1, null, DateTimeOffset.UtcNow.AddMinutes(30), items, new string('b', 64)),
            MultiDemandViewModelTests.Plan(
                1,
                MultiDemandViewModelTests.Leg(1, "TO_PICKUP", "BUSINESS", items[0].DemandId, FieldLongestStation, "ARRIVED"),
                MultiDemandViewModelTests.Leg(2, "TO_PICKUP", "BUSINESS", items[1].DemandId, "N17-8_N18-7_N18-8", "PLANNED"),
                MultiDemandViewModelTests.Leg(3, "TO_DROPOFF", "BUSINESS", items[0].DemandId, "N19-8_N20-7_N20-8", "PLANNED"),
                MultiDemandViewModelTests.Leg(4, "TO_DROPOFF", "BUSINESS", items[1].DemandId, "T12-1_T12-2_T13-1", "PLANNED"))));

        Assert.Empty(LayAndCut(viewModel, "field-longest-values-1004x660.png"));
    }

    /// <summary>
    /// 顶栏最长的状态文案，配现场最长的车名与站点名（<c>8005-agv-onboard-hmi#426</c>）：充电那一格「充电状态未知；需人工充电：服务端保持」，
    /// 到站那一格清单空了时的「站名 / 电量低，先送车上的货再充电，未装的任务退回待派」。都由真的业务快照推出来，不是往格子里硬塞字。
    /// </summary>
    [Fact]
    public async Task TheLongestStatusTextsAreShownWholeOnTheOnboardScreen()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await FieldVehicleViewModelAsync(controller);
        viewModel.UpdateWireToGateJourney(new WireToGateJourneySnapshot(
            new WireToGateVehicleBusinessState(
                1, "READY", "TRANSPORT", true, "SUFFICIENT", "UNKNOWN", null, [], DateTimeOffset.UtcNow, new string('a', 64)),
            new WireToGateCurrentStopWorklist(FieldLongestStation, 1, null, null, [], new string('b', 64), "CHARGING_REQUIRED"),
            MultiDemandViewModelTests.Plan(
                1,
                MultiDemandViewModelTests.Leg(1, "TO_PICKUP", "BUSINESS", null, FieldLongestStation, "ARRIVED")),
            DateTimeOffset.UtcNow));
        Assert.Equal("充电状态未知；需人工充电：服务端保持", viewModel.ChargingStatusText);
        Assert.Equal($"{FieldLongestStation} / 电量低，先送车上的货再充电，未装的任务退回待派", viewModel.VisitText);

        Assert.Empty(LayAndCut(viewModel, "longest-status-texts-1004x660.png"));
    }

    private static async Task<MainViewModel> FieldVehicleViewModelAsync(OnboardController controller)
    {
        MainViewModel viewModel = new(
            controller,
            new RecordingLogger(),
            FieldLongestAgvId,
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()))
        {
            Clock = new SystemClock(),
            DisplayTimeZone = TimeZoneInfo.CreateCustomTimeZone("China", TimeSpan.FromHours(8), "China", "China")
        };
        await viewModel.InitializeAsync();
        return viewModel;
    }

    /// <summary>Lays the screen out, saves it as <paramref name="screenshot"/> when asked to, and returns what is cut.</summary>
    private static string[] LayAndCut(MainViewModel viewModel, string screenshot) => OnStaThread(() =>
    {
        (FrameworkElement root, _, _) = Lay(viewModel, hasRows: viewModel.WorklistItems.Count > 0);
        if (Environment.GetEnvironmentVariable("HMI_LAYOUT_SCREENSHOT_DIR") is { Length: > 0 } directory)
        {
            Screenshot(root, Path.Combine(directory, screenshot));
        }

        return Cut(root, root);
    });

    /// <summary>现场（v2 库发件箱 11-23 全量历史）最长的子批、机台号、站点名与车名。</summary>
    private const string FieldLongestSublot = "Q26101654-11-2";

    private const string FieldLongestEqp = "3QHS6012";

    private const string FieldLongestStation = "N15-8_N16-7_N16-8";

    private const string FieldLongestAgvId = "老厂前线新多仓位1";

    private sealed record RowLayout(
        string Sublot, Rect SublotBox, double SublotLine, string Eqp, Rect EqpBox, double EqpLine, Rect ItemBox);

    /// <summary>
    /// Every visible <see cref="TextBlock"/> under <paramref name="scope"/> whose text is not all on screen: one that trims, one
    /// narrower than its unwrapped text, or one that its own slot or an ancestor short of a scroll viewer clips.
    /// </summary>
    private static string[] Cut(DependencyObject scope, FrameworkElement root)
    {
        List<string> cut = [];
        foreach (TextBlock block in Descendants<TextBlock>(scope))
        {
            if (!Shown(block) || string.IsNullOrEmpty(block.Text))
            {
                continue;
            }

            string name = $"{AutomationProperties.GetAutomationId(block)} \"{block.Text}\"";
            if (block.TextTrimming != TextTrimming.None)
            {
                cut.Add($"{name} trims");
            }

            if (block.TextWrapping == TextWrapping.NoWrap && NaturalWidth(block) > block.ActualWidth + 1)
            {
                cut.Add($"{name} is {block.ActualWidth:0.#} wide for {NaturalWidth(block):0.#} of text");
            }

            Rect box = new(0, 0, block.ActualWidth, block.ActualHeight);
            for (FrameworkElement? element = block;
                element is not null and not ScrollContentPresenter;
                element = VisualTreeHelper.GetParent(element) as FrameworkElement)
            {
                Rect local = block.TransformToAncestor(element).TransformBounds(box);
                if (LayoutInformation.GetLayoutClip(element)?.Bounds is { IsEmpty: false } clip && !Contains(clip, local))
                {
                    cut.Add($"{name} is clipped by {element.GetType().Name} {element.Name}: {local} outside {clip}");
                    break;
                }

                if (ReferenceEquals(element, root))
                {
                    if (!Contains(new Rect(0, 0, root.ActualWidth, root.ActualHeight), local))
                    {
                        cut.Add($"{name} runs off the window: {local}");
                    }

                    break;
                }
            }
        }

        return [.. cut];
    }

    /// <summary>
    /// Whether the block and every ancestor are <see cref="Visibility.Visible"/>. Not <see cref="UIElement.IsVisible"/>: a tree
    /// laid out without a window has no presentation source, and there it is false for everything.
    /// </summary>
    private static bool Shown(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(Rect outer, Rect inner) =>
        inner.Left >= outer.Left - 0.5 && inner.Top >= outer.Top - 0.5
        && inner.Right <= outer.Right + 0.5 && inner.Bottom <= outer.Bottom + 0.5;

    private static Rect Box(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static double LineHeight(TextBlock block)
    {
        TextBlock probe = new() { Text = "Q", FontFamily = block.FontFamily, FontSize = block.FontSize, FontWeight = block.FontWeight };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Height;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (T nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static void Screenshot(FrameworkElement root, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DrawingVisual page = new();
        using (DrawingContext context = page.RenderOpen())
        {
            Vector margin = VisualTreeHelper.GetOffset(root);
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, root.ActualWidth + 2 * margin.X, root.ActualHeight + 2 * margin.Y));
        }

        // The root sits at its margin's offset; the bitmap is the whole client area around it.
        Vector offset = VisualTreeHelper.GetOffset(root);
        RenderTargetBitmap bitmap = new(
            (int)Math.Ceiling(root.ActualWidth + 2 * offset.X), (int)Math.Ceiling(root.ActualHeight + 2 * offset.Y), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        bitmap.Render(root);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>How wide the block's text would be with no limit: the same text, font and size, measured unconstrained.</summary>
    private static double NaturalWidth(TextBlock block)
    {
        TextBlock probe = new()
        {
            Text = block.Text,
            FontFamily = block.FontFamily,
            FontSize = block.FontSize,
            FontWeight = block.FontWeight,
            FontStyle = block.FontStyle
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private static async Task<MainViewModel> EightRowViewModelAsync(OnboardController controller)
    {
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);
        viewModel.UpdateWireToGateJourney(MultiDemandViewModelTests.Journey(MultiDemandViewModelTests.Worklist(
            1,
            [.. Enumerable.Range(1, 8).Select(i => MultiDemandViewModelTests.Item(
                $"aaaaaaaa-0000-4000-8000-{i:D12}",
                $"SUBLOT-{i}",
                WireToGateStagingPickupStop.StagingToWire,
                "PICKUP",
                2))])));
        Assert.Equal(8, viewModel.WorklistItems.Count);
        return viewModel;
    }

    /// <summary>The window's content laid out at the onboard screen's client size (1024×768, maximized, title bar and taskbar).</summary>
    private static (FrameworkElement Root, ListBoxItem FirstRow, ScrollViewer Column) Lay(MainViewModel viewModel, bool hasRows = true)
    {
        Window window = (Window)XamlReader.Parse(LoadableMainWindowXaml());
        FrameworkElement root = (FrameworkElement)window.Content;
        window.Content = null;
        root.Resources.MergedDictionaries.Add(window.Resources);
        root.DataContext = viewModel;
        root.Measure(new Size(1004, 660));
        root.Arrange(new Rect(0, 0, 1004, 660));
        root.UpdateLayout();

        ListBox list = Find<ListBox>(root, element => AutomationProperties.GetAutomationId(element) == "WorklistItems")!;
        ListBoxItem first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        Assert.True(!hasRows || first is not null, "the worklist has no first row");
        ScrollViewer column = Ancestor<ScrollViewer>(list)!;
        while (Ancestor<ScrollViewer>(column) is { } outer)
        {
            column = outer;
        }

        return (root, first, column);
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

    private static T? Ancestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        DependencyObject? current = VisualTreeHelper.GetParent(element);
        while (current is not null and not T)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        return current as T;
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
}
