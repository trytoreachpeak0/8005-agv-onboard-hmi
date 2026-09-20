using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// 把已经在跑的那个车载端窗口拉到前台。
///
/// 这是单例守卫（<see cref="SQCD.Agv.Infrastructure.SingleInstanceGuard"/>）抢不到名字时唯一做的事。
/// 之所以要做，是因为现场的人双击桌面图标，往往正是因为屏幕上看不到界面——如果第二个进程只是
/// 悄悄退出，他看到的是「点了没反应」，于是接着点；弹个报错框同样帮不上忙，他要的是界面。
/// 把已有窗口调出来，恰好就是他本来想要的结果。
/// </summary>
internal static class RunningInstanceWindow
{
    private const int SwRestore = 9;

    /// <summary>
    /// 找到同一会话里已经在跑的那个车载端，把它的主窗口调到前台。
    /// 返回那个进程的 pid，找不到时返回 <c>null</c>。
    /// </summary>
    public static int? BringToFront(IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        Process[] candidates = [];
        try
        {
            using Process current = Process.GetCurrentProcess();
            candidates = Process.GetProcessesByName(current.ProcessName);

            // 同一个交互会话里、比自己早起来的那个，才是持有名字的那一个。跨会话的进程即便同名
            // 也不该去碰：它的窗口在另一个桌面上，置前只会失败，而 pid 写进日志会误导排查。
            Process? existing = candidates
                .Where(candidate => candidate.Id != current.Id && IsSameSession(candidate, current))
                .OrderBy(GetStartTimeOrMaxValue)
                .FirstOrDefault();
            if (existing is null)
            {
                return null;
            }

            IntPtr handle = existing.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                // 进程在、主窗口句柄还没有：它多半正处在自己的启动过程里。pid 照样报出去，
                // 运维据此就能认出是谁占着名字。
                logger.Write(
                    LogSeverity.Warning,
                    nameof(RunningInstanceWindow),
                    $"已有实例 pid={existing.Id} 尚无主窗口，无法置前。");
                return existing.Id;
            }

            // 只有最小化时才还原。对一个最大化或全屏的窗口调 SW_RESTORE 会把它变回普通大小，
            // 那是在替操作员改他没要求改的东西。
            if (IsIconic(handle))
            {
                ShowWindow(handle, SwRestore);
            }

            if (!SetForegroundWindow(handle))
            {
                // 前台窗口的归属由系统管，调用不一定成功（例如别的程序刚抢走了输入焦点）。
                // 失败不影响本次退出的结论，只是操作员这一下没看到界面弹出来。
                logger.Write(
                    LogSeverity.Warning,
                    nameof(RunningInstanceWindow),
                    $"已有实例 pid={existing.Id} 的窗口置前未成功。");
            }

            return existing.Id;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            logger.Write(
                LogSeverity.Warning,
                nameof(RunningInstanceWindow),
                "查找已有实例窗口失败。",
                exception);
            return null;
        }
        finally
        {
            foreach (Process candidate in candidates)
            {
                candidate.Dispose();
            }
        }
    }

    private static bool IsSameSession(Process candidate, Process current)
    {
        try
        {
            return candidate.SessionId == current.SessionId;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // 进程在这一瞬间退出了，或者读不到它的信息。当成不是同一个会话，宁可少报一个。
            return false;
        }
    }

    private static DateTime GetStartTimeOrMaxValue(Process candidate)
    {
        try
        {
            return candidate.StartTime;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // 读不到启动时间的排到最后，让读得到的那些先按真实先后排序。
            return DateTime.MaxValue;
        }
    }

    // 用 DllImport 而不是 LibraryImport：后者生成的封送代码要求整个项目打开
    // AllowUnsafeBlocks（SYSLIB1062），为三个无参数封送的窗口 API 给 WPF 项目开 unsafe 不划算。
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
