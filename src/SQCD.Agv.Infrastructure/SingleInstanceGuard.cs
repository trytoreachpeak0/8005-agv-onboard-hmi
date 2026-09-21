using System.Runtime.InteropServices;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// 单例守卫判定的结果。只有 <see cref="Acquired"/> 可以启动。
/// </summary>
public enum SingleInstanceOutcome
{
    /// <summary>抢到了：本机没有别的车载端在跑，可以照常启动。</summary>
    Acquired,

    /// <summary>本机已有车载端在跑（v2 的整机名字被占着，或现场线的名字存在）。本次不启动。</summary>
    AlreadyRunning,

    /// <summary>判断不了本机有没有别的车载端。按已有实例处理，本次不启动。</summary>
    Undeterminable,
}

/// <summary>
/// 车载端的单例守卫：一台车载电脑上同时只允许一个车载端在跑（onboard-hmi#173、#165）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有它。</b>两个车载端进程都会无条件构造 Modbus 客户端，同时对着同一个真实 IO 模块写开锁 DO。
/// 服务端的 <c>sessionGeneration</c> 只裁决会话，管不住两个进程同时写 DO——那是硬件事实。2026-09-20 在生产车
/// agv01 上发生过：现场线装着守卫，却因为「拿不到互斥体」而放行，35 秒后第二个实例真的起来了，两个同时连着
/// 同一个 IO 模块。
/// </para>
/// <para>
/// <b>失败方向是拒绝启动，不是放行。</b>现场线（03027de）的守卫把自己出问题归为「不可用」并放行；
/// onboard-hmi#165 与 2026-09-21 的分叉调研报告都保留了「守卫自己建不起来时放行」这一支，理由是
/// 「一辆车因为抢不到一个名字而起不来，代价大于偶发的双开」，前提是建 <c>Global\</c> 名字要
/// <c>SeCreateGlobalPrivilege</c>、没有它的账户会误伤。**那个前提不成立**：微软文档
/// 《Kernel object namespaces》写明需要该特权的只有在全局命名空间里<b>创建</b> file-mapping 与
/// symbolic link 对象，互斥体不在其列。于是「自己建不起来、其实没有别的实例」这一支对互斥体基本不存在，
/// 剩下的「判断不了」一律按已有实例处理。车起不来是看得见、能处理的；两个进程同时写 DO 是看不见、会出事的。
/// </para>
/// <para>
/// <b>「拒绝访问」恰恰是有实例的信号</b>（#165 第 1 条）。另一个账户（例如 ssh 给的 session 0）建的互斥体，
/// 默认 DACL 不让别的账户打开；打开已存在的名字时得到 <see cref="UnauthorizedAccessException"/>，说明名字
/// 在、有人持有。所以先 <see cref="Mutex.TryOpenExisting(string, out Mutex)"/>：存在但打不开就是已有实例；
/// 不存在才去建。<b>不降级到 <c>Local\</c></b>：会话级名字跨会话互相看不见，agv01 那次正是降级之后放行的。
/// </para>
/// <para>
/// <b>名字是整机一个，不带车辆身份。</b>车载电脑上本来就只该有一个车载端；三台车载电脑是同一镜像克隆，
/// 配置里的 agvId 也可能写错，整机名字最不依赖配置正确。按 IO 端点取名挡不住别名（同一个模块一份配置写 IP、
/// 一份写主机名）。
/// </para>
/// <para>
/// <b>跨版本：再只打开、不创建现场线的名字。</b>切换前后车上可能同时装着现场线车载端，它的守卫名按 agvId 取
/// （<see cref="FieldLineGlobalPrefix"/> + agvId，建不起来时 <see cref="FieldLineSessionPrefix"/> + agvId，
/// 规则见 <see cref="BuildFieldLineName"/>），与 v2 的整机名字不同，两者互相看不见。v2 启动时去打开它：存在即判
/// 已有实例。反方向——现场线看不见 v2——不在本守卫能管的范围里。
/// </para>
/// <para>
/// <b><see cref="AbandonedMutexException"/> 是「抢到了」。</b>上一个持有者被 Kill 或崩溃时没人释放，内核把
/// 所有权连同这个异常交给下一个等待者。当成失败的话，名字就成了谁都抢不到的，车再也起不来。这一支只在还有
/// <b>别的句柄</b>让对象活着时才走得到（诊断工具、正在等它的进程）：被杀的进程若握着唯一的句柄，对象随它一起
/// 销毁，下一次启动是新建名字——L2 与 G3 先强杀再起车载端，走的是这一种。
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>
    /// v2 车载端的整机名字。名字就是契约：改动它，新旧两个版本的车载端会互相看不见，守卫等于没装。
    /// </summary>
    public const string MachineName = @"Global\SQCD.Agv.Wpf.Onboard";

    /// <summary>现场线（<c>w2g/field-agv01-*</c>，03027de）守卫的机器级名字前缀，后接 agvId。</summary>
    public const string FieldLineGlobalPrefix = @"Global\SQCD.Agv.Wpf-";

    /// <summary>现场线守卫建不起机器级名字时降级用的会话级名字前缀，后接 agvId。</summary>
    public const string FieldLineSessionPrefix = @"Local\SQCD.Agv.Wpf-";

    private readonly Mutex? _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex? mutex, SingleInstanceOutcome outcome, string name, string detail)
    {
        _mutex = mutex;
        Outcome = outcome;
        Name = name;
        Detail = detail;
    }

    /// <summary>判定结果。</summary>
    public SingleInstanceOutcome Outcome { get; }

    /// <summary>作出这个判定的那个名字，写进日志用来核对。</summary>
    public string Name { get; }

    /// <summary>判定的依据，一句话，写进日志。</summary>
    public string Detail { get; }

    /// <summary>本次能否启动。只有抢到了才能：判断不了与已有实例一样，都不启动。</summary>
    public bool ShouldStart => Outcome == SingleInstanceOutcome.Acquired;

    /// <summary>
    /// 先只打开现场线的两个名字，再抢 v2 的整机名字。任一步不是「可以启动」就停在那里。
    /// </summary>
    public static SingleInstanceGuard Acquire(string agvId, IAppLogger? logger = null) =>
        Acquire(agvId, MachineName, logger);

    /// <summary>同 <see cref="Acquire(string, IAppLogger?)"/>，整机名字可换，只给测试用：测试不能去碰真名字。</summary>
    internal static SingleInstanceGuard Acquire(string agvId, string machineName, IAppLogger? logger)
    {
        foreach (string fieldLineName in FieldLineNames(agvId))
        {
            SingleInstanceGuard probe = ProbeExisting(fieldLineName);
            if (!probe.ShouldStart)
            {
                Log(logger, probe);
                return probe;
            }
        }

        SingleInstanceGuard guard = AcquireByName(machineName);
        Log(logger, guard);
        return guard;
    }

    /// <summary>
    /// 现场线守卫会用的名字（按 03027de 的 <c>BuildName</c>：前缀 + agvId 去首尾空白、反斜杠换成下划线）。
    /// agvId 为空时现场线建不出名字，这里也不产出。
    /// </summary>
    public static string BuildFieldLineName(string prefix, string agvId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        return prefix + agvId.Trim().Replace('\\', '_');
    }

    internal static string[] FieldLineNames(string? agvId) =>
        string.IsNullOrWhiteSpace(agvId)
            ? []
            : [BuildFieldLineName(FieldLineGlobalPrefix, agvId), BuildFieldLineName(FieldLineSessionPrefix, agvId)];

    /// <summary>
    /// 只打开、不创建：名字不存在 → 可以启动（不持有任何东西）；存在（打开得了或被拒绝访问）→ 已有实例；
    /// 名字被别的类型的对象占着、或任何别的错误 → 判断不了。
    /// </summary>
    /// <remarks>
    /// 直接调 <c>OpenMutexW</c> 读错误码，不用 <see cref="Mutex.TryOpenExisting(string, out Mutex)"/>：.NET 8 的后者遇到
    /// 名字属于别的类型的对象（<c>ERROR_INVALID_HANDLE</c>）时不抛、返回 false，与「名字不存在」分不开，于是一个事件对象
    /// 占住现场线的名字就能让这里放行（审查 M-1，复现用例 <c>AFieldLineNameTakenByAnotherKindOfObjectStopsTheStart</c>）。
    /// 只有 <c>ERROR_FILE_NOT_FOUND</c> 才算不存在；其余错误码一律判断不了——失败方向是拒绝启动。
    /// </remarks>
    internal static SingleInstanceGuard ProbeExisting(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        IntPtr handle = OpenMutexW(Synchronize, inheritHandle: false, name);
        if (handle != IntPtr.Zero)
        {
            _ = CloseHandle(handle);
            return new SingleInstanceGuard(null, SingleInstanceOutcome.AlreadyRunning, name, "现场线车载端的单例名字存在。");
        }

        int error = Marshal.GetLastWin32Error();
        return error switch
        {
            ErrorFileNotFound => new SingleInstanceGuard(null, SingleInstanceOutcome.Acquired, name, "现场线车载端的单例名字不存在。"),
            ErrorAccessDenied => new SingleInstanceGuard(
                null,
                SingleInstanceOutcome.AlreadyRunning,
                name,
                "现场线车载端的单例名字存在，但本账户无权打开（另一个账户的实例持有它）。"),
            ErrorInvalidHandle => new SingleInstanceGuard(
                null,
                SingleInstanceOutcome.Undeterminable,
                name,
                "现场线车载端的单例名字被别的类型的对象占着，判断不了，按已有实例处理。"),
            _ => new SingleInstanceGuard(
                null,
                SingleInstanceOutcome.Undeterminable,
                name,
                $"打开现场线车载端的单例名字失败（Win32 错误 {error}），判断不了，按已有实例处理。"),
        };
    }

    private const uint Synchronize = 0x0010_0000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidHandle = 6;

    // 用 DllImport 而不是 LibraryImport：后者生成的封送代码要求项目打开 AllowUnsafeBlocks（SYSLIB1062）。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenMutexW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// 抢一个名字。先打开已存在的：打开得了就去等，被拒绝访问就是已有实例；不存在才建。
    /// 建的那一刻若别人刚建好，构造函数会直接打开它，结果同样由等待决定。
    /// </summary>
    internal static SingleInstanceGuard AcquireByName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Mutex? mutex = null;
        try
        {
            if (!Mutex.TryOpenExisting(name, out mutex))
            {
                mutex = new Mutex(initiallyOwned: false, name);
            }

            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // 等待者拿到所有权的同时收到这个异常：这是「抢到了」。
                acquired = true;
            }

            if (acquired)
            {
                return new SingleInstanceGuard(mutex, SingleInstanceOutcome.Acquired, name, "抢到了整机单例名字。");
            }

            mutex.Dispose();
            return new SingleInstanceGuard(null, SingleInstanceOutcome.AlreadyRunning, name, "整机单例名字被另一个车载端持有。");
        }
        catch (UnauthorizedAccessException)
        {
            // 打开已存在的名字被拒绝，或建的那一刻撞上别人刚建好又打不开：两种都说明名字在、有人持有（#165）。
            mutex?.Dispose();
            return new SingleInstanceGuard(
                null,
                SingleInstanceOutcome.AlreadyRunning,
                name,
                "整机单例名字存在，但本账户无权打开（另一个账户的实例持有它）。");
        }
        catch (Exception exception) when (exception is WaitHandleCannotBeOpenedException or IOException or ArgumentException)
        {
            mutex?.Dispose();
            return Undeterminable(name, exception);
        }
    }

    private static SingleInstanceGuard Undeterminable(string name, Exception exception) =>
        new(
            null,
            SingleInstanceOutcome.Undeterminable,
            name,
            $"判断不了本机有没有别的车载端（{exception.GetType().Name}：{exception.Message}），按已有实例处理。");

    private static void Log(IAppLogger? logger, SingleInstanceGuard guard)
    {
        if (logger is null)
        {
            return;
        }

        if (guard.ShouldStart)
        {
            logger.Write(LogSeverity.Information, nameof(SingleInstanceGuard), $"{guard.Detail}名字={guard.Name}。");
        }
        else
        {
            logger.Write(LogSeverity.Warning, nameof(SingleInstanceGuard), $"{guard.Detail}本次不启动。名字={guard.Name}。");
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

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 所有权是线程级的，释放的线程不是抢到的那个时释放不了；交给进程退出。没有别的句柄时对象随之销毁，
            // 下一次启动新建名字；有别的句柄时下一次拿到 AbandonedMutexException，同样算抢到。
        }

        _mutex.Dispose();
    }
}
