using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
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
    private static (FrameworkElement Root, ListBoxItem FirstRow, ScrollViewer Column) Lay(MainViewModel viewModel)
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
        Assert.NotNull(first);
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
