namespace SQCD.Agv.Core;

/// <summary>
/// 一次告警求值要读的全部事实，由调用方在同一时刻取好。
/// </summary>
/// <remarks>
/// 求值器自己不读任何端口。把「读」留在外面，判定才是纯函数：能逐条单测，一轮里也不会因为读的先后
/// 看到两个时刻。
/// </remarks>
/// <param name="Now">求值时刻。</param>
/// <param name="IoConnected">仓门控制模块是否在线。</param>
/// <param name="Io">仓门控制模块当前快照。</param>
/// <param name="IoSnapshotMaxAge">仓门快照允许的最大年龄，与作业前检查同一个值。</param>
/// <param name="LegacyRuleGatewayConnected">
/// 旧任务系统网关是否在线；<c>null</c> 表示不适用（WIRE_TO_GATE 模式，网关是空实现）。
/// </param>
/// <param name="Controller">车载端控制器当前状态。</param>
/// <param name="VehicleSafety">出发安全信号；<c>null</c> 表示不适用（旧模式不轮询它）。</param>
/// <param name="VehicleSafetyMaxAge">出发安全信号允许的最大年龄。</param>
/// <param name="VehicleSafetyClockSkewTolerance">出发安全信号允许的时钟偏差。</param>
/// <param name="SessionReasonCodes">上层会话当前的原因码；没有会话时为空。</param>
/// <param name="CurrentOperation">WIRE_TO_GATE 当前仓位操作的投影；旧模式或还没有操作时为 <c>null</c>。</param>
public sealed record OnboardAlarmInputs(
    DateTimeOffset Now,
    bool IoConnected,
    IoSnapshot Io,
    TimeSpan IoSnapshotMaxAge,
    bool? LegacyRuleGatewayConnected,
    OnboardSnapshot Controller,
    VehicleSafetySignal? VehicleSafety,
    TimeSpan VehicleSafetyMaxAge,
    TimeSpan VehicleSafetyClockSkewTolerance,
    IReadOnlyList<string> SessionReasonCodes,
    WireToGateHmiOperationSnapshot? CurrentOperation);

/// <summary>
/// 车载端告警的判定规则：从一份当下的事实算出当下的全部告警。
/// </summary>
/// <remarks>
/// <para>
/// <b>每一轮都算全集，不做 Raise／Clear 的增量。</b>一条告警什么时候消失，只取决于它的条件此刻还成不成立，
/// 不取决于某个「恢复」事件有没有被恰好收到——漏一个事件的增量模型会让告警永远挂着，正是 REQ-0269 要避免
/// 的那种不确定新旧的旧值。
/// </para>
/// <para>
/// <b>互斥的条件不重复报。</b>IO 离线时不再说快照陈旧或锁反馈读不到；快照陈旧时不拿它去判锁反馈与行驶中
/// 的仓门——一份不可信的读数上判出来的告警同样不可信。
/// </para>
/// <para>
/// 光幕被挡（<see cref="OnboardAlarmCodes.SlotLightCurtainBlocked"/>）不在这里判，理由写在那个常量上。
/// </para>
/// </remarks>
public static class OnboardAlarmEvaluator
{
    public const string Warning = "WARNING";
    public const string Critical = "CRITICAL";

    private static readonly HashSet<string> LegacyOperationTimeoutCodes = new(StringComparer.Ordinal)
    {
        "UNLOCK_FEEDBACK_TIMEOUT",
        "UNLOCK_OUTPUT_RESET_TIMEOUT",
        "RELOCK_OR_CARGO_TIMEOUT"
    };

    public static IReadOnlyList<AlarmEntry> Evaluate(OnboardAlarmInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        DateTimeOffset now = inputs.Now;
        List<AlarmEntry> alarms = [];

        bool ioTrusted = false;
        if (!inputs.IoConnected)
        {
            alarms.Add(Vehicle(
                OnboardAlarmCodes.IoModuleDisconnected,
                Critical,
                now,
                "仓门控制模块离线，读不到仓门状态，禁止开仓。"));
        }
        else if (!SafetyRules.IsSnapshotFresh(inputs.Io, now, inputs.IoSnapshotMaxAge))
        {
            alarms.Add(Vehicle(
                OnboardAlarmCodes.SlotStateStale,
                Warning,
                now,
                $"仓门状态超过{inputs.IoSnapshotMaxAge.TotalMilliseconds:0}毫秒没有刷新，当前读数不可信。"));
        }
        else
        {
            ioTrusted = true;
            int[] unreadable = SlotsWhere(inputs.Io, locker => locker.LockFeedbackRaw is null);
            if (unreadable.Length > 0)
            {
                alarms.Add(Slots(
                    OnboardAlarmCodes.SlotLockFeedbackLost,
                    Critical,
                    now,
                    unreadable,
                    $"{FormatSlots(unreadable)}锁反馈读不到，无法确认仓门是否锁好。"));
            }
        }

        if (inputs.LegacyRuleGatewayConnected is false)
        {
            alarms.Add(Vehicle(
                OnboardAlarmCodes.RuleGatewayDisconnected,
                Warning,
                now,
                "任务系统离线，收不到到站与校验结果。"));
        }

        AddOperationAlarm(alarms, inputs);

        if (inputs.Controller.State == OnboardState.Faulted)
        {
            alarms.Add(Vehicle(
                OnboardAlarmCodes.SafetyFaultLatched,
                Critical,
                now,
                $"车载端已进入故障锁定（{inputs.Controller.ErrorCode ?? "未给出原因码"}），禁止继续操作，请联系维护人员。"));
        }

        VehicleSafetySignal? signal = inputs.VehicleSafety;
        bool signalFresh = signal is not null
            && signal.IsFresh(now, inputs.VehicleSafetyMaxAge, inputs.VehicleSafetyClockSkewTolerance);
        if (signal is not null && (signal.MotionState == VehicleMotionState.Unknown || !signalFresh))
        {
            string reasons = signal.EffectiveReasonCodes.Count == 0
                ? string.Empty
                : $"（{string.Join("、", signal.EffectiveReasonCodes)}）";
            alarms.Add(Vehicle(
                OnboardAlarmCodes.DepartureSafetySignalUnavailable,
                Warning,
                now,
                signalFresh
                    ? $"车辆是否停稳未知{reasons}，禁止开仓。"
                    : $"车辆停稳信号已过期{reasons}，禁止开仓。"));
        }

        if (ioTrusted && signalFresh && signal!.MotionState == VehicleMotionState.Moving)
        {
            int[] unsecured = SlotsWhere(
                inputs.Io,
                locker => locker.IsKnown && (!locker.IsLocked || locker.UnlockOutputRaw is true));
            if (unsecured.Length > 0)
            {
                alarms.Add(Slots(
                    OnboardAlarmCodes.SlotUnsecuredWhileMoving,
                    Critical,
                    now,
                    unsecured,
                    $"车辆行驶中{FormatSlots(unsecured)}未锁好或开锁输出未复位。"));
            }
        }

        if (inputs.SessionReasonCodes.Contains(
            SlotConfigurationActivationCoordinator.FingerprintMismatchReasonCode,
            StringComparer.Ordinal))
        {
            alarms.Add(Vehicle(
                OnboardAlarmCodes.SlotConfigurationMismatch,
                Critical,
                now,
                "本车生效的仓位配置与服务端批准的不一致，暂停接收作业。"));
        }

        return alarms;
    }

    private static void AddOperationAlarm(List<AlarmEntry> alarms, OnboardAlarmInputs inputs)
    {
        if (inputs.CurrentOperation is { Stage: WireToGateHmiOperationStage.RecoveryRequired } operation)
        {
            int[] slots = [.. operation.Slots.Order()];
            alarms.Add(new AlarmEntry(
                OnboardAlarmCodes.SlotOperationUnfinished,
                Critical,
                inputs.Now,
                AlarmScope.CurrentOperation,
                $"{FormatSlots(slots)}{(operation.OperationType == OperationType.Load ? "装货" : "卸货")}未完成，等待恢复处理。",
                SlotOperationAttemptId: operation.SlotOperationAttemptId,
                PhysicalSlotNumber: slots.Length == 1 ? slots[0] : null));
        }

        if (inputs.Controller.ErrorCode is not { } errorCode || !LegacyOperationTimeoutCodes.Contains(errorCode))
        {
            return;
        }

        ActiveOperation? active = inputs.Controller.ActiveOperation;
        alarms.Add(active is null
            ? Vehicle(OnboardAlarmCodes.StationOperationOverdue, Warning, inputs.Now, $"作业超时（{errorCode}）。")
            : new AlarmEntry(
                OnboardAlarmCodes.StationOperationOverdue,
                Warning,
                inputs.Now,
                AlarmScope.CurrentOperation,
                $"{active.SlotIndex + 1}号仓作业超时（{errorCode}）。",
                SlotOperationAttemptId: active.OperationId,
                PhysicalSlotNumber: active.SlotIndex + 1));
    }

    private static AlarmEntry Vehicle(string code, string severity, DateTimeOffset now, string message) =>
        new(code, severity, now, AlarmScope.CurrentVehicle, message);

    private static AlarmEntry Slots(string code, string severity, DateTimeOffset now, int[] slots, string message) =>
        new(code, severity, now, AlarmScope.CurrentVehicle, message, PhysicalSlotNumber: slots.Length == 1 ? slots[0] : null);

    private static int[] SlotsWhere(IoSnapshot io, Func<LockerSnapshot, bool> predicate) =>
        [.. io.Lockers.Where(predicate).Select(locker => locker.PhysicalNumber).Order()];

    private static string FormatSlots(IReadOnlyList<int> slots) =>
        string.Join("、", slots.Select(slot => $"{slot}号仓"));
}
