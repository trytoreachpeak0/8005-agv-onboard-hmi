namespace SQCD.Agv.Core;

/// <summary>
/// 用 C# 类型准确表达“车载端现在处于什么状态、8 个仓位是什么情况”。
/// </summary>
///

// 整个车载端软件的业务状态，不是某一扇仓门的状态。
public enum OnboardState
{
    // 软件刚启动，尚未完成初始化
    Starting,
    // 正在等待 IO 模块或规则模块连接
    Connecting,
    // 两端已连通，但规则模块尚未通知到站
    WaitingArrival,
    // 已到站，可以扫码
    ReadyToScan,
    // 已扫码，等待规则模块返回校验结果
    Verifying,
    // 已获准开仓，正在开门、等待人工操作和关门反馈
    Operating,
    // 仓门和货物状态已确认，正在向规则模块上报结果
    Reporting,
    // 安全故障状态，禁止继续操作
    Faulted
}

// 操作类型
public enum OperationType
{
    // 装料
    Load,
    // 卸料
    Unload
}

// 操作阶段
public enum OperationStage
{
    None,
    Precheck,
    // 正在向对应 DO 写开锁脉冲
    WritingUnlock,
    // 等待锁反馈 DI 从 1 变为 0
    WaitingUnlockFeedback,
    // 锁反馈已经确认未锁，等待硬件脉冲对应的开锁DO自动恢复为0
    WaitingUnlockOutputReset,
    // 等待人工操作、关门、锁反馈为 1、光幕状态正确
    WaitingCargoAndRelock,
    // 仓门已经关闭，但货物状态尚未达到任务要求，等待操作员选择重新开门或取消
    WaitingOperatorRecovery,
    // 正在向规则模块发送成功或失败结果
    ReportingResult,
    Completed,
    // 此次操作失败，需人工处理
    Failed
}

// 输入方式
public enum ScanInputMethod
{
    // 扫码枪输入
    Scanner,
    // 手动输入后提交
    Manual
}

// 日志等级
public enum LogSeverity
{
    Debug,
    Information,
    Warning,
    Error
}

// 某一个仓位在某一个时刻的 IO 事实快照。例如 1 号仓刚完成装料并关门：
public sealed record LockerSnapshot(
    // 软件索引，范围 0～7
    int SlotIndex,
    // 给人看的仓位号，范围 1～8
    int PhysicalNumber,
    // 对应 DO 的原始状态
    bool? UnlockOutputRaw,
    // 锁反馈 DI 原始状态
    bool? LockFeedbackRaw,
    // 光幕 DI 原始状态
    bool? LightCurtainRaw,
    // 这次 IO 数据被读取到的时间
    DateTimeOffset ObservedAt)
{
    //DO、锁反馈 DI、光幕 DI 都不为 null，才认为该仓位状态可靠。
    public bool IsKnown => UnlockOutputRaw.HasValue && LockFeedbackRaw.HasValue && LightCurtainRaw.HasValue;

    //锁反馈极性
    public bool IsLocked => LockFeedbackRaw is true;

    // 光幕极性
    public bool HasCargo => LightCurtainRaw is false;

    // 未知仓位
    public static LockerSnapshot Unknown(int slotIndex, DateTimeOffset observedAt) =>
        new(slotIndex, slotIndex + 1, null, null, null, observedAt);
}

// 8 个仓位整体快照 IoSnapshot
///IO 是否已连接 + 8 个仓位的当前快照+ 本次快照的读取时间
public sealed record IoSnapshot(
    bool IsConnected,
    IReadOnlyList<LockerSnapshot> Lockers,
    DateTimeOffset ObservedAt)
{
    // 按索引取仓位
    public LockerSnapshot GetLocker(int slotIndex)
    {
        if (slotIndex is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex), "仓位索引必须在0到7之间。");
        }

        return Lockers.First(locker => locker.SlotIndex == slotIndex);
    }

    // 系统启动时的 IO 初始状态
    public static IoSnapshot Unknown(DateTimeOffset observedAt) =>
        new(false, Enumerable.Range(0, 8).Select(index => LockerSnapshot.Unknown(index, observedAt)).ToArray(), observedAt);
}

// 车辆当前是否处在一个有效、允许操作的站点
public sealed record VisitContext(
    // 本次到站的唯一编号，例如 VISIT-MOCK-001,临时业务编号
    string VisitId,
    // 站点系统编号，例如 ST-MOCK-01
    string StationId,
    string StationName,
    bool AllowOperation,
    DateTimeOffset ExpiresAt)
{
    public bool IsActive(DateTimeOffset now) => AllowOperation && ExpiresAt > now;
}

// 车载端内部的扫码请求
public sealed record ScanVerificationRequest(string Sublot, ScanInputMethod InputMethod, string VisitId);

// 规则模块的授权结果,它是车载端拿到规则模块响应后，转换得到的“业务授权结果”。
public sealed record ScanAuthorization(
    bool Accepted,
    string? OperationId,
    string? TaskId,
    string Sublot,
    int? SlotIndex,
    OperationType? OperationType,
    bool? ExpectedCargoAfter,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ScanAuthorization Rejected(string sublot, string errorCode, string errorMessage) =>
        new(false, null, null, sublot, null, null, null, errorCode, errorMessage);
}

// 一次操作的最终结果
public sealed record OperationResult(
    string MessageId,
    string VisitId,
    string OperationId,
    string TaskId,
    string Sublot,
    int SlotIndex,
    OperationType OperationType,
    bool Success,
    string? FailureCode,
    OperationStage FailureStage,
    LockerSnapshot FinalLocker,
    bool DeparturePermitted,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

// 进行中的操作
public sealed record ActiveOperation(
    string VisitId,
    string OperationId,
    string TaskId,
    string Sublot,
    int SlotIndex,
    OperationType OperationType,
    OperationStage Stage,
    DateTimeOffset StartedAt)
{
    // 当前任务已经执行过的重新开门次数，不包含第一次正常开门。
    public int ReopenAttempts { get; init; }
}

// 界面唯一应读取的总状态
public sealed record OnboardSnapshot(
    OnboardState State,
    bool RuleConnected,
    bool IoConnected,
    VisitContext? Visit,
    IoSnapshot Io,
    ActiveOperation? ActiveOperation,
    bool DeparturePermitted,
    string Guidance,
    string? ErrorCode,
    DateTimeOffset UpdatedAt);

// 日志:时间、等级、来源类名、文本内容
public sealed record LogEntry(DateTimeOffset Timestamp, LogSeverity Severity, string Source, string Message);

public sealed class ValueChangedEventArgs<T>(T value) : EventArgs
{
    public T Value { get; } = value;
}

public sealed class LogEntryEventArgs(LogEntry entry) : EventArgs
{
    public LogEntry Entry { get; } = entry;
}
