using System.Diagnostics;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// 把界面调度线程以外的两种未处理失败记进技术日志的「本地异常」（REQ-0272，onboard-hmi#312）。
/// </summary>
/// <remarks>
/// <para>
/// 调度线程上的未处理异常由 <c>App.OnDispatcherUnhandledException</c> 接，它会锁存致命故障；这里补的是另外两条路：
/// 后台线程上的未处理异常（<see cref="AppDomain.UnhandledException"/>，运行时随后终止进程）和没人观察的后台任务异常
/// （<see cref="TaskScheduler.UnobservedTaskException"/>，运行时默认不终止进程）。此前两条都不留痕迹。
/// </para>
/// <para>
/// <b>只记日志，不改变进程的生死。</b>不调 <see cref="UnobservedTaskExceptionEventArgs.SetObserved"/>（那是替运行时
/// 决定吞掉它），不拦终止，处理器自己也不往外抛（终结器线程上抛出会把进程带走）。
/// </para>
/// </remarks>
public sealed class ProcessFailureLogging : IDisposable
{
    private readonly IAppLogger _logger;
    private bool _attached;

    public ProcessFailureLogging(IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>挂到当前进程的两个事件上；<see cref="Dispose"/> 摘下。</summary>
    public static ProcessFailureLogging Attach(IAppLogger logger)
    {
        ProcessFailureLogging logging = new(logger);
        AppDomain.CurrentDomain.UnhandledException += logging.OnUnhandledException;
        TaskScheduler.UnobservedTaskException += logging.OnUnobservedTaskException;
        logging._attached = true;
        return logging;
    }

    public void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        TryWrite(
            e.IsTerminating ? "后台线程发生未处理异常，进程即将退出。" : "后台线程发生未处理异常。",
            e.ExceptionObject as Exception);
    }

    public void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        TryWrite("后台任务的异常没有被观察到。", e.Exception);
    }

    public void Dispose()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void TryWrite(string message, Exception? exception)
    {
#pragma warning disable CA1031 // 见类注释：处理器自己不往外抛。
        try
        {
            _logger.Write(
                LogSeverity.Error,
                OnboardTechnicalLogCategory.LocalFault,
                nameof(ProcessFailureLogging),
                message,
                exception);
        }
        catch (Exception failure)
        {
            Debug.WriteLine(failure);
        }
#pragma warning restore CA1031
    }
}
