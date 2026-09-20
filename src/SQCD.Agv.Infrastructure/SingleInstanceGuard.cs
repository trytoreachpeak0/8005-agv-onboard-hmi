using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// 单例守卫抢名字的结果。
/// </summary>
public enum SingleInstanceOutcome
{
    /// <summary>抢到了：本进程是这辆车上唯一的车载端，可以照常启动。</summary>
    Acquired,

    /// <summary>没抢到：同一辆车上已经有一个车载端在跑，本次不该启动。</summary>
    AlreadyRunning,

    /// <summary>守卫自己没建起来。放行启动，但这一次没有单例保护。</summary>
    Unavailable,
}

/// <summary>
/// 车载端的单例守卫：一辆车上同时只允许一个 <c>SQCD.Agv.Wpf</c> 在跑。
///
/// 为什么要有它：两个进程用同一个 agvId / onboardInstanceId 连同一个服务端时，会抢同一条会话，
/// 会话代次被来回顶掉，两个客户端都收不全消息。2026-09-18 22:39 生产服务端自停 20 分钟，触发点
/// 就是现场把车载端连开了两次（onboard-hmi#157、control-server#148）。服务端那一侧已经加固过，
/// 但制造这个局面的入口还开着——桌面上有图标，双击是最自然的动作。
///
/// 用的是机器级命名互斥体，名字带车辆身份。工作区里已有同名做法
/// （<c>Global\W2G-InteractiveDesktop</c>，唯一定义处是 mes-ingest 的 <c>Invoke-WithDesktopLock.ps1</c>），
/// 那边的经验是**名字才是契约，代码可以各自一份**——所以这里是本仓自己的一份实现，不跨仓引包。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>
    /// 机器级名字的前缀。名字是契约的一半，另一半是 agvId：换掉任何一半，两个进程就抢不到一起去，
    /// 守卫等于没装。改动这里必须同时改文档与运维脚本里引用它的地方。
    /// </summary>
    public const string GlobalNamePrefix = @"Global\SQCD.Agv.Wpf-";

    /// <summary>会话级名字的前缀，只在机器级名字建不起来时降级使用。</summary>
    public const string SessionNamePrefix = @"Local\SQCD.Agv.Wpf-";

    private readonly Mutex? _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex? mutex, SingleInstanceOutcome outcome, string name)
    {
        _mutex = mutex;
        Outcome = outcome;
        Name = name;
    }

    /// <summary>抢名字的结果。</summary>
    public SingleInstanceOutcome Outcome { get; }

    /// <summary>实际用的互斥体名字，写进日志用来核对两个进程抢的是不是同一个名字。</summary>
    public string Name { get; }

    /// <summary>
    /// 本次是否应该继续启动。只有明确抢不到（已有实例在跑）才是否；守卫自己出问题时放行。
    /// </summary>
    public bool ShouldStart => Outcome != SingleInstanceOutcome.AlreadyRunning;

    /// <summary>
    /// 按车辆身份抢名字。先抢机器级的名字，建不起来时降级到会话级。
    /// </summary>
    public static SingleInstanceGuard Acquire(string agvId, IAppLogger? logger = null)
    {
        SingleInstanceGuard guard = AcquireByName(BuildName(GlobalNamePrefix, agvId), logger);
        if (guard.Outcome != SingleInstanceOutcome.Unavailable)
        {
            return guard;
        }

        guard.Dispose();
        // 建 Global\ 名字要 SeCreateGlobalPrivilege，交互登录的账户默认有，运维脚本用的
        // Interactive 计划任务也有。真碰上没有的机器时，会话级的名字仍然挡得住这张票要挡的
        // 那件事：现场双击图标起出的第二个进程，与第一个同在一个交互桌面会话里。降级挡不住的
        // 只有跨会话重复启动，而车上只有一个会话。
        logger?.Write(
            LogSeverity.Warning,
            nameof(SingleInstanceGuard),
            "机器级单例名字建不起来，本次降级为会话级名字。");
        return AcquireByName(BuildName(SessionNamePrefix, agvId), logger);
    }

    /// <summary>
    /// 拼出互斥体名字。
    /// </summary>
    public static string BuildName(string prefix, string agvId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);

        // 反斜杠是内核对象名里唯一非法的字符——它分隔命名空间前缀和名字本身。其余字符内核照单
        // 全收，所以不做多余的清洗：清洗得越多，两个不同的 agvId 撞成同一个名字的机会越大，而
        // 那种撞法的表现是「另一辆车的客户端起不来」，比清洗本来要防的问题更难查。
        return prefix + agvId.Trim().Replace('\\', '_');
    }

    /// <summary>
    /// 按完整名字抢。不抛异常：守卫自己出问题时返回 <see cref="SingleInstanceOutcome.Unavailable"/>。
    /// </summary>
    public static SingleInstanceGuard AcquireByName(string name, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者被 Kill 或崩溃时留下的状态。WaitOne 抛这个异常的同时所有权已经
                // 交给了本线程，所以这是「抢到了」而不是「失败了」——名字不会因为一次异常退出
                // 就变成谁都抢不到。
                acquired = true;
            }

            return new SingleInstanceGuard(
                mutex,
                acquired ? SingleInstanceOutcome.Acquired : SingleInstanceOutcome.AlreadyRunning,
                name);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or WaitHandleCannotBeOpenedException
                or ArgumentException
                or NotSupportedException)
        {
            // 守卫是防误操作的，不是安全边界。它自己出问题时放行启动：一辆正在跑单的车因为抢
            // 不到一个名字而起不来，代价远大于偶发的双开——后者服务端已经能扛住了。
            mutex?.Dispose();
            logger?.Write(
                LogSeverity.Warning,
                nameof(SingleInstanceGuard),
                $"单例名字 {name} 无法使用，本次不做单例保护。",
                exception);
            return new SingleInstanceGuard(null, SingleInstanceOutcome.Unavailable, name);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_mutex is null)
        {
            return;
        }

        if (Outcome == SingleInstanceOutcome.Acquired)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 互斥体的所有权是线程级的：释放的线程必须是当初抢到的那个线程。不是时就交给
                // 进程退出——内核在最后一个句柄关闭时照样放开名字，下一次启动仍抢得到。
            }
        }

        _mutex.Dispose();
    }
}
