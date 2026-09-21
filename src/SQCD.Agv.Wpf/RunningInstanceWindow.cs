using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf;

/// <summary>
/// 单例守卫挡下第二个车载端时，让已经在跑的那个窗口回到操作员面前（onboard-hmi#165 第 2 条），
/// 并给这一次没启动的进程一个看得见的交代。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么由已有实例自己置前。</b>现场线让新进程直接对已有窗口调 <c>SetForegroundWindow</c>，agv01 上实测
/// 「置前未成功」：Windows 只允许前台进程、或刚响应过用户输入的进程改变前台窗口，一个后台进程去调会被静默拒绝。
/// 这里反过来：新进程（操作员刚双击起来的，它有前台权）先 <c>AllowSetForegroundWindow</c> 把权让出去，再广播一条
/// 注册过的窗口消息；已有实例在自己的窗口过程里收到它，自己还原、自己激活。
/// </para>
/// <para>
/// 跨会话（例如运维从 ssh 的 session 0 起的第二个进程）时广播到不了另一个桌面，已有实例收不到，也不该收到——
/// 那里没有操作员在看。本进程照样拒绝启动，日志里有那一行。
/// </para>
/// </remarks>
internal static class RunningInstanceWindow
{
    /// <summary>拒绝提示窗口自己关掉之前停留的时间。无人值守时（隐藏窗口启动、CI）进程必须能自己退出。</summary>
    internal static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(10);

    private const int SwRestore = 9;
    private const uint AsfwAny = unchecked((uint)-1);
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    /// <summary>「把你的窗口调出来」这条消息。名字是两个进程之间的契约。</summary>
    internal static readonly uint ShowRequestMessage = RegisterWindowMessageW("SQCD.Agv.Wpf.Onboard.ShowExistingWindow");

    /// <summary>已有实例这一侧：在主窗口上接住那条消息。窗口句柄在 <see cref="Window.Show"/> 之后才有。</summary>
    public static void Listen(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        IntPtr handle = new WindowInteropHelper(window).Handle;
        HwndSource? source = HwndSource.FromHwnd(handle);
        if (source is null || ShowRequestMessage == 0)
        {
            return;
        }

        source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if ((uint)message != ShowRequestMessage)
            {
                return IntPtr.Zero;
            }

            // 只有最小化时才还原：SW_RESTORE 会把最大化前被最小化的窗口还原回最大化，而对一个本来就显示着的
            // 最大化窗口调它会把它变回普通大小——那是在替操作员改他没要求改的东西。
            if (IsIconic(hwnd))
            {
                _ = ShowWindow(hwnd, SwRestore);
            }

            _ = window.Activate();
            handled = true;
            return IntPtr.Zero;
        });
    }

    /// <summary>
    /// 被挡下的新进程这一侧：请已有实例把窗口调出来，再给操作员一个会自己关掉的提示，然后由调用方退出。
    /// </summary>
    public static void ReportRefusal(SingleInstanceGuard guard, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(logger);

        if (guard.Outcome == SingleInstanceOutcome.AlreadyRunning)
        {
            _ = AllowSetForegroundWindow(AsfwAny);
            if (ShowRequestMessage == 0 || !PostMessageW(HwndBroadcast, ShowRequestMessage, IntPtr.Zero, IntPtr.Zero))
            {
                logger.Write(
                    LogSeverity.Warning,
                    nameof(RunningInstanceWindow),
                    $"没能通知已有实例把窗口调到前台，Win32 错误={Marshal.GetLastWin32Error()}。");
            }
        }

        ShowNotice(NoticeText(guard.Outcome));
    }

    internal static string NoticeText(SingleInstanceOutcome outcome) => outcome switch
    {
        SingleInstanceOutcome.AlreadyRunning =>
            "本机已经有一个车载端在运行，这一次不再启动第二个。\n\n如果屏幕上没看到它的界面，请在任务栏里找到它；仍然找不到时请联系维护人员。",
        _ =>
            "无法确认本机是否已有车载端在运行。为避免两个程序同时控制仓门，这一次不启动。\n\n请联系维护人员。",
    };

    private static void ShowNotice(string text)
    {
        Window notice = new()
        {
            Title = "车载端没有启动",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = true,
        };
        Button close = new() { Content = "确定", MinWidth = 96, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        close.Click += (_, _) => notice.Close();
        notice.Content = new StackPanel
        {
            Margin = new Thickness(24),
            MaxWidth = 480,
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 16 },
                close,
            },
        };

        DispatcherTimer timer = new() { Interval = NoticeLifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            notice.Close();
        };
        // 在 ShowDialog 之前启动：计时器挂在当前 Dispatcher 上，模态循环里照样会触发，不依赖窗口的 Loaded 事件。
        timer.Start();
        _ = notice.ShowDialog();
        timer.Stop();
    }

    // 用 DllImport 而不是 LibraryImport：后者生成的封送代码要求项目打开 AllowUnsafeBlocks（SYSLIB1062）。
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
