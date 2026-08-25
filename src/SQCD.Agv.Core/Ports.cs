namespace SQCD.Agv.Core;
/// <summary>
/// 车载端核心流程需要外部世界提供哪些能力
/// </summary>
public interface IIoModuleClient : IAsyncDisposable
{
    // IO 是否在线
    public bool IsConnected { get; }

    // IO 当前快照
    public IoSnapshot CurrentSnapshot { get; }

    // 两个事件
    public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

    public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged;

    // 启动和停止 IO 客户端
    public Task StartAsync(CancellationToken applicationStopping);

    public Task StopAsync(CancellationToken cancellationToken = default);

    // 脉冲开锁
    public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken);

    // 等待某个仓位满足条件
    public Task<LockerSnapshot> WaitForLockerAsync(
        int slotIndex,
        Func<LockerSnapshot, bool> predicate,
        TimeSpan timeout,
        TimeSpan stableWindow,
        CancellationToken cancellationToken);
}

//规则模块通信能力
public interface IRuleGateway : IAsyncDisposable
{
    //与规则模块的 TCP 是否在线
    public bool IsConnected { get; }

    //当前是否有有效到站信息
    public VisitContext? CurrentVisit { get; }

    //规则模块连接或掉线时通知
    public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

    //收到 visit.started 或 visit.ended 时通知
    public event EventHandler<ValueChangedEventArgs<VisitContext?>>? VisitChanged;

    // 启动、停止规则模块通信
    public Task StartAsync(CancellationToken applicationStopping);

    public Task StopAsync(CancellationToken cancellationToken = default);

    // 请求扫码核验
    public Task<ScanAuthorization> VerifyScanAsync(
        ScanVerificationRequest request,
        CancellationToken cancellationToken);

    // 上报操作结果
    public Task<bool> ReportOperationAsync(OperationResult result, CancellationToken cancellationToken);
}

// 日志接口
public interface IAppLogger
{
    public event EventHandler<LogEntryEventArgs>? EntryWritten;

    public void Write(LogSeverity severity, string source, string message, Exception? exception = null);
}

// 时钟接口
public interface IClock
{
    public DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
