namespace SQCD.Agv.Core;

/// <summary>
/// 一条告警与当前这台车正在做的事的关系。
/// </summary>
/// <remarks>
/// REQ-0270 的收敛在本期不是按人分权——本期没有人员认证——而是按「哪个界面看得到什么」分：与当前
/// AGV／当前停靠／当前操作直接相关的显示在本机界面，其余进看板。所以这个枚举描述的是关系，不是
/// 权限，求值时不需要也不接受任何身份输入。
/// </remarks>
public enum AlarmScope
{
    /// <summary>与这台车本身直接相关（IO 掉线、锁反馈异常等）。</summary>
    CurrentVehicle,

    /// <summary>与车当前停靠的那个站点直接相关。</summary>
    CurrentStop,

    /// <summary>与车当前正在执行的那次操作直接相关。</summary>
    CurrentOperation,

    /// <summary>与以上三者都不直接相关。本机界面不显示，它归看板。</summary>
    Fleet
}

/// <summary>
/// 一条告警。
/// </summary>
/// <remarks>
/// <see cref="AlarmCode"/> **是字符串，不是枚举**。告警是开放集合，`ErrorCode` 是封闭 enum，把
/// 开放集合塞进封闭 enum 等于每加一个故障码就发一次 breaking change。有架构测试断言这个属性是
/// <c>string</c>，并且车载端实际产出的告警码与协议错误码注册表无交集。
/// </remarks>
public sealed record AlarmEntry(
    string AlarmCode,
    string Severity,
    DateTimeOffset RaisedAt,
    AlarmScope Scope,
    string Message,
    string? DemandId = null,
    string? StationId = null,
    string? SlotOperationAttemptId = null,
    int? PhysicalSlotNumber = null);

/// <summary>
/// 车载端当前全量告警的一份快照。
/// </summary>
/// <remarks>
/// **快照而不是事件流。**事件流断线重连那段正是 REQ-0269 禁止的东西：重连后你不知道断线期间漏了
/// 什么，只能显示一个不确定新旧的旧值。快照没有这个问题——每一份都是当下的全部事实，后一份整体
/// 取代前一份，不做增量合并。
/// </remarks>
public sealed record OnboardAlarmSnapshot(
    string AgvId,
    long SnapshotSequence,
    DateTimeOffset CapturedAt,
    IReadOnlyList<AlarmEntry> Alarms)
{
    /// <summary>交付类别。它是 SNAPSHOT，不是 RELIABLE，也不是事件。</summary>
    public const string DeliveryClass = "SNAPSHOT";
}

/// <summary>车此刻在做什么。告警是否显示在本机界面，只由它和告警本身决定。</summary>
public sealed record OnboardAlarmContext(
    string AgvId,
    string? CurrentStationId,
    string? CurrentSlotOperationAttemptId);

/// <summary>
/// 车载端的告警板：持有当前全量告警，产出快照，并挑出该显示在本机界面的那些。
/// </summary>
public sealed class OnboardAlarmBoard(string agvId, TimeProvider clock)
{
    private readonly string _agvId = !string.IsNullOrWhiteSpace(agvId)
        ? agvId
        : throw new ArgumentException("agvId 不能为空。", nameof(agvId));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly Dictionary<string, AlarmEntry> _active = new(StringComparer.Ordinal);

    private long _sequence;

    /// <summary>抬起或更新一条告警。同一个 code 只有一条，后一次覆盖前一次。</summary>
    public void Raise(AlarmEntry alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        ArgumentException.ThrowIfNullOrWhiteSpace(alarm.AlarmCode);

        _active[alarm.AlarmCode] = alarm;
    }

    /// <summary>清掉一条告警。</summary>
    public void Clear(string alarmCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alarmCode);

        _active.Remove(alarmCode);
    }

    /// <summary>
    /// 产出当前全量快照。每次调用序号加一，内容是此刻的全部告警——不是自上次以来的增量。
    /// </summary>
    public OnboardAlarmSnapshot Capture() => new(
        _agvId,
        ++_sequence,
        _clock.GetUtcNow(),
        [.. _active.Values.OrderBy(alarm => alarm.AlarmCode, StringComparer.Ordinal)]);
}

/// <summary>
/// 哪些告警显示在本机界面。
/// </summary>
/// <remarks>
/// 求值路径上没有任何身份或密钥输入：参数只有一份快照和车此刻在做什么。把它实现成需要身份才能求值
/// 的权限判断，会让它落在一个全场共用环境变量密钥的地基上。
/// </remarks>
public static class OnboardAlarmVisibility
{
    public static IReadOnlyList<AlarmEntry> ForLocalDisplay(
        OnboardAlarmSnapshot snapshot,
        OnboardAlarmContext context)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);

        return [.. snapshot.Alarms.Where(alarm => IsDirectlyRelated(alarm, context))];
    }

    private static bool IsDirectlyRelated(AlarmEntry alarm, OnboardAlarmContext context) => alarm.Scope switch
    {
        AlarmScope.CurrentVehicle => true,
        AlarmScope.CurrentStop =>
            context.CurrentStationId is not null
            && string.Equals(alarm.StationId, context.CurrentStationId, StringComparison.Ordinal),
        AlarmScope.CurrentOperation =>
            context.CurrentSlotOperationAttemptId is not null
            && string.Equals(
                alarm.SlotOperationAttemptId,
                context.CurrentSlotOperationAttemptId,
                StringComparison.Ordinal),
        _ => false,
    };
}

/// <summary>
/// 车载端目前会抬起的告警码。
/// </summary>
/// <remarks>
/// 这是一个**开放集合**的当前内容，不是它的定义。加一个码就是往这里加一行，不需要动协议、不需要
/// 发版——这正是它不能是 enum 的理由。
/// </remarks>
public static class OnboardAlarmCodes
{
    public const string IoModuleDisconnected = "ONBOARD_IO_MODULE_DISCONNECTED";
    public const string SlotLockFeedbackLost = "ONBOARD_SLOT_LOCK_FEEDBACK_LOST";
    public const string SlotLightCurtainBlocked = "ONBOARD_SLOT_LIGHT_CURTAIN_BLOCKED";
    public const string RuleGatewayDisconnected = "ONBOARD_RULE_GATEWAY_DISCONNECTED";
    public const string StationOperationOverdue = "ONBOARD_STATION_OPERATION_OVERDUE";

    public static IReadOnlyList<string> All { get; } =
    [
        IoModuleDisconnected,
        SlotLockFeedbackLost,
        SlotLightCurtainBlocked,
        RuleGatewayDisconnected,
        StationOperationOverdue
    ];
}
