namespace SQCD.Agv.Wpf;

/// <summary>
/// 启动失败时给现场看的提示框：有人看得见才弹，而且最多挡住退出 <see cref="Timeout"/> 这么久（onboard-hmi#14）。
/// </summary>
/// <remarks>
/// <para>
/// 修复之前这里是一个阻塞的模态框，挡在 <c>Shutdown(-1)</c> 前面。车上是无人值守的自动登录桌面，没人点确定，
/// 进程就一直活着：没有窗口、CPU 静止、端口从不监听，从外面看像是卡死。
/// </para>
/// <para>
/// 取舍：提示框放在一条后台 STA 线程上，调用方只等它 <see cref="Timeout"/>，到点就照常退出；后台线程不拖住进程，
/// 进程一退，框跟着消失。不在交互式桌面上（CI 的 session 0 服务、sshd 起的进程）就根本不弹——那里弹出来也没人看得见，
/// 只会平白耽误退出。原因已经先写进日志，提示框只是给恰好站在车前的人一句话，不是唯一的出处。
/// </para>
/// </remarks>
internal static class StartupFailureNotice
{
    /// <summary>
    /// 提示框最多挡住退出多久：够站在车前的人读完一句话，又不至于让拉起它的脚本以为进程卡住。
    /// </summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 在后台 STA 线程上执行 <paramref name="show"/>，它返回或 <paramref name="timeout"/> 到点，二者先到者为准。
    /// </summary>
    /// <returns>提示框是否在到点前被关掉。</returns>
    internal static async Task<bool> ShowAsync(Action show, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(show);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                show();
            }
#pragma warning disable CA1031 // 后台线程上的未处理异常会直接结束进程，跳过调用方的日志与退出码；提示框失败不值得那样。
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            finally
            {
                closed.TrySetResult();
            }
        })
        {
            // 判据的一部分：前台线程会在 Shutdown 之后继续把进程留着，那就是换了个地方的同一个缺陷。
            IsBackground = true,
            Name = "startup-failure-notice"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Task finished = await Task.WhenAny(closed.Task, Task.Delay(timeout)).ConfigureAwait(true);
        return finished == closed.Task;
    }
}
