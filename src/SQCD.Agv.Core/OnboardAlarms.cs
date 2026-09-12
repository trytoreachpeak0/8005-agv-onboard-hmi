namespace SQCD.Agv.Core;

/// <summary>
/// 一条告警与当前这台车正在做的事的关系。
/// </summary>
/// <remarks>
/// REQ-0270 的收敛在本期不是按人分权——本期没有人员认证——而是按「哪个界面看得到什么」分：与当前
/// AGV／当前停靠／当前操作直接相关的显示在本机界面；服务端看板集中显示全部，不按这个枚举过滤。所以
/// 这个枚举描述的是关系，不是权限，求值时不需要也不接受任何身份输入。
/// </remarks>
public enum AlarmScope
{
    /// <summary>与这台车本身直接相关（IO 掉线、锁反馈异常等）。</summary>
    CurrentVehicle,

    /// <summary>与车当前停靠的那个站点直接相关。</summary>
    CurrentStop,

    /// <summary>与车当前正在执行的那次操作直接相关。</summary>
    CurrentOperation,

    /// <summary>与以上三者都不直接相关。本机界面不显示，只在看板上显示。</summary>
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
/// <remarks>
/// 线程安全。三方同时碰它：告警监视器在后台整份替换，会话客户端在握手里和会话中途各自抓快照，界面
/// 读当前内容。
/// </remarks>
public sealed class OnboardAlarmBoard(string agvId, TimeProvider clock)
{
    private readonly string _agvId = !string.IsNullOrWhiteSpace(agvId)
        ? agvId
        : throw new ArgumentException("agvId 不能为空。", nameof(agvId));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly Dictionary<string, AlarmEntry> _active = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private long _sequence;

    /// <summary>抬起或更新一条告警。同一个 code 只有一条，后一次覆盖前一次。</summary>
    public void Raise(AlarmEntry alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        ArgumentException.ThrowIfNullOrWhiteSpace(alarm.AlarmCode);

        lock (_gate)
        {
            _active[alarm.AlarmCode] = alarm;
        }
    }

    /// <summary>清掉一条告警。</summary>
    public void Clear(string alarmCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alarmCode);

        lock (_gate)
        {
            _active.Remove(alarmCode);
        }
    }

    /// <summary>
    /// 用一次求值的结果整份替换当前告警，返回内容是否变了。
    /// </summary>
    /// <remarks>
    /// 一条告警持续存在、别的都没变时，保留它**第一次**抬起的 <see cref="AlarmEntry.RaisedAt"/>。求值器每一轮
    /// 都拿「此刻」当抬起时间，照单全收会让一条挂了十分钟的告警在每一份快照里都像刚刚发生——线上的告警身份
    /// 由内容派生，也会跟着每轮换一次。
    /// </remarks>
    public bool ReplaceAll(IEnumerable<AlarmEntry> alarms)
    {
        ArgumentNullException.ThrowIfNull(alarms);

        lock (_gate)
        {
            Dictionary<string, AlarmEntry> next = new(StringComparer.Ordinal);
            foreach (AlarmEntry alarm in alarms)
            {
                ArgumentNullException.ThrowIfNull(alarm);
                ArgumentException.ThrowIfNullOrWhiteSpace(alarm.AlarmCode);

                next[alarm.AlarmCode] =
                    _active.TryGetValue(alarm.AlarmCode, out AlarmEntry? existing)
                    && existing with { RaisedAt = alarm.RaisedAt } == alarm
                        ? existing
                        : alarm;
            }

            bool changed = next.Count != _active.Count
                || next.Any(pair => !_active.TryGetValue(pair.Key, out AlarmEntry? existing) || existing != pair.Value);
            _active.Clear();
            foreach (KeyValuePair<string, AlarmEntry> pair in next)
            {
                _active[pair.Key] = pair.Value;
            }

            return changed;
        }
    }

    /// <summary>
    /// 产出当前全量快照。每次调用序号加一，内容是此刻的全部告警——不是自上次以来的增量。
    /// </summary>
    public OnboardAlarmSnapshot Capture()
    {
        lock (_gate)
        {
            return new(_agvId, ++_sequence, _clock.GetUtcNow(), OrderedAlarms());
        }
    }

    /// <summary>
    /// 读当前全量告警，不推进序号。
    /// </summary>
    /// <remarks>
    /// 序号是最近一次 <see cref="Capture"/> 用掉的那个，从没抓过时是 0。它只给本机界面和「服务端手上是不是
    /// 已经是这一份」的比对用，本身不上线——上线的每一份都由 <see cref="Capture"/> 产出。
    /// </remarks>
    public OnboardAlarmSnapshot Peek()
    {
        lock (_gate)
        {
            return new(_agvId, _sequence, _clock.GetUtcNow(), OrderedAlarms());
        }
    }

    private AlarmEntry[] OrderedAlarms() =>
        [.. _active.Values.OrderBy(alarm => alarm.AlarmCode, StringComparer.Ordinal)];
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
/// 车载端定义的告警码。
/// </summary>
/// <remarks>
/// 这是一个**开放集合**的当前内容，不是它的定义。加一个码就是往这里加一行，不需要动协议、不需要
/// 发版——这正是它不能是 enum 的理由。什么条件下抬起哪一条，见 <see cref="OnboardAlarmEvaluator"/>。
/// </remarks>
public static class OnboardAlarmCodes
{
    /// <summary>仓门控制模块（IO）离线。</summary>
    public const string IoModuleDisconnected = "ONBOARD_IO_MODULE_DISCONNECTED";

    /// <summary>IO 在线且读数新鲜，但有仓位的锁反馈读不到。</summary>
    public const string SlotLockFeedbackLost = "ONBOARD_SLOT_LOCK_FEEDBACK_LOST";

    /// <summary>
    /// 光幕被挡。**定义了，不抬起。**光幕被挡就是仓里有货，车载着货走是常态，照这个条件会一直报；要报的是
    /// 「与作业期望不符」，那需要知道每个仓此刻该不该有货，本端的求值输入里没有这一项。
    /// </summary>
    public const string SlotLightCurtainBlocked = "ONBOARD_SLOT_LIGHT_CURTAIN_BLOCKED";

    /// <summary>
    /// 旧任务系统网关断开。**只在不走 WIRE_TO_GATE 的旧模式下判**：v2 线上网关是空实现，连接状态恒为断开。
    /// </summary>
    public const string RuleGatewayDisconnected = "ONBOARD_RULE_GATEWAY_DISCONNECTED";

    /// <summary>旧模式下作业超时：控制器报了开锁反馈、开锁输出复位或关门装卸超时。</summary>
    public const string StationOperationOverdue = "ONBOARD_STATION_OPERATION_OVERDUE";

    /// <summary>
    /// WIRE_TO_GATE 模式下一次仓位操作没有完成、等待恢复处理。v2 线上超时与 IO 失败在执行器里走同一条路，
    /// 结果都是这个状态，所以这一条不叫「超时」。
    /// </summary>
    public const string SlotOperationUnfinished = "ONBOARD_SLOT_OPERATION_UNFINISHED";

    /// <summary>车载端控制器进入故障锁定，禁止继续操作。</summary>
    public const string SafetyFaultLatched = "ONBOARD_SAFETY_FAULT_LATCHED";

    /// <summary>出发安全信号（车辆是否停稳）读不到或已经过期。</summary>
    public const string DepartureSafetySignalUnavailable = "ONBOARD_DEPARTURE_SAFETY_SIGNAL_UNAVAILABLE";

    /// <summary>IO 在线，但仓门状态超过允许的时长没有刷新。</summary>
    public const string SlotStateStale = "ONBOARD_SLOT_STATE_STALE";

    /// <summary>车辆行驶中，有仓门未锁或开锁输出未复位。</summary>
    public const string SlotUnsecuredWhileMoving = "ONBOARD_SLOT_UNSECURED_WHILE_MOVING";

    /// <summary>本车生效的仓位配置指纹与服务端批准的不一致，会话因此不就绪。</summary>
    public const string SlotConfigurationMismatch = "ONBOARD_SLOT_CONFIGURATION_MISMATCH";

    public static IReadOnlyList<string> All { get; } =
    [
        IoModuleDisconnected,
        SlotLockFeedbackLost,
        SlotLightCurtainBlocked,
        RuleGatewayDisconnected,
        StationOperationOverdue,
        SlotOperationUnfinished,
        SafetyFaultLatched,
        DepartureSafetySignalUnavailable,
        SlotStateStale,
        SlotUnsecuredWhileMoving,
        SlotConfigurationMismatch
    ];
}
