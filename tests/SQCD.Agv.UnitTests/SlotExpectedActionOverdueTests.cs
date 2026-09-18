using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 期待动作超时（REQ-0358，CP-0005 实现票 1，<c>trytoreachpeak0/8005-agv-onboard-hmi#109</c>）：当前仓自本次操作第一次
/// 开锁起累计等待，达到门槛就在告警板上挂 <see cref="OnboardAlarmCodes.SlotExpectedActionOverdue"/>；该仓闭环、判
/// <c>UNKNOWN</c> 或操作结束时撤下。
/// </summary>
/// <remarks>
/// 接缝是计时器吃进的操作投影与求值器吐出的告警，两者都是业务服务与告警监视器实际走的那条路。执行器一行不改，
/// 所以这里不碰执行器。线上那一半（字段、subjectType）在 G2 测试里。不挂 <c>IntegrationSlice</c> trait，理由同
/// <see cref="OnboardAlarmEvaluatorTests"/>。
/// </remarks>
public sealed class SlotExpectedActionOverdueTests
{
    private const string Attempt = "11111111-1111-4111-8111-111111111111";
    private const string NextAttempt = "22222222-2222-4222-8222-222222222222";
    private static readonly DateTimeOffset FirstUnlock = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(6);

    [Fact]
    public void TheWaitIsCountedFromTheSlotsFirstUnlockAndAReopenDoesNotResetIt()
    {
        // 卸货时光幕卡在「有货」：关门、弹开、再关门，一直重来。每次重开都从零计就永远到不了门槛。
        SlotExpectedActionWaitTracker tracker = new();
        tracker.Observe(Operation(WireToGateHmiOperationStage.Unlocking, FirstUnlock), [3]);
        tracker.Observe(Operation(WireToGateHmiOperationStage.WaitingOperator, FirstUnlock.AddSeconds(2)), [3]);
        tracker.Observe(Operation(WireToGateHmiOperationStage.Unlocking, FirstUnlock.AddMinutes(4)), [3]);
        WireToGateHmiOperationSnapshot waiting =
            Operation(WireToGateHmiOperationStage.WaitingOperator, FirstUnlock.AddMinutes(4).AddSeconds(2));
        tracker.Observe(waiting, [3]);

        AlarmEntry alarm = Assert.Single(Overdue(tracker, waiting, FirstUnlock + Threshold));

        Assert.Equal(OnboardAlarmCodes.SlotExpectedActionOverdue, alarm.AlarmCode);
        Assert.Equal(OnboardAlarmEvaluator.Warning, alarm.Severity);
        Assert.Equal(FirstUnlock + Threshold, alarm.RaisedAt);
        Assert.Equal(3, alarm.PhysicalSlotNumber);
        Assert.Equal(Attempt, alarm.SlotOperationAttemptId);
        // subjectType=SLOT、subjectId=仓位号，是 ToWireAlarm 对「整车范围、指明一个仓」的映射。
        Assert.Equal(AlarmScope.CurrentVehicle, alarm.Scope);
    }

    [Fact]
    public void TheAlarmIsRaisedExactlyAtTheThresholdAndNotAMomentBefore()
    {
        SlotExpectedActionWaitTracker tracker = Waiting(slot: 3, since: FirstUnlock, out WireToGateHmiOperationSnapshot waiting);

        Assert.Empty(Overdue(tracker, waiting, FirstUnlock + Threshold - TimeSpan.FromMilliseconds(1)));
        Assert.Single(Overdue(tracker, waiting, FirstUnlock + Threshold));
    }

    [Fact]
    public void TheThresholdIsWhatIsConfigured()
    {
        SlotExpectedActionWaitTracker tracker = Waiting(slot: 3, since: FirstUnlock, out WireToGateHmiOperationSnapshot waiting);
        TimeSpan twoMinutes = TimeSpan.FromMinutes(2);

        AlarmEntry alarm = Assert.Single(Overdue(tracker, waiting, FirstUnlock + twoMinutes, twoMinutes));
        Assert.Equal(FirstUnlock + twoMinutes, alarm.RaisedAt);
    }

    [Fact]
    public void AWaitHeldBySafetyBeforeAReopenStillCounts()
    {
        // 读到相反态要重开，但车辆安全事实不许开锁：仍在等，仍然计时（CP-0005 新增项二说明 3 的第三种情形）。
        SlotExpectedActionWaitTracker tracker = Waiting(slot: 3, since: FirstUnlock, out _);
        WireToGateHmiOperationSnapshot held =
            Operation(WireToGateHmiOperationStage.WaitingOperator, FirstUnlock.AddMinutes(5));
        tracker.Observe(held, [3]);

        Assert.Single(Overdue(tracker, held, FirstUnlock + Threshold));
    }

    [Fact]
    public void ASlotThatClosedItsLoopIsWithdrawnAndTheNextSlotStartsItsOwnClock()
    {
        SlotExpectedActionWaitTracker tracker = Waiting(slot: 3, since: FirstUnlock, out _);
        WireToGateHmiOperationSnapshot verifying =
            Operation(WireToGateHmiOperationStage.Verifying, FirstUnlock.AddMinutes(7));
        tracker.Observe(verifying, []);

        Assert.Empty(Overdue(tracker, verifying, FirstUnlock.AddMinutes(7)));

        DateTimeOffset nextUnlock = FirstUnlock.AddMinutes(7).AddSeconds(1);
        tracker.Observe(Operation(WireToGateHmiOperationStage.Unlocking, nextUnlock), [4]);
        WireToGateHmiOperationSnapshot nextWaiting =
            Operation(WireToGateHmiOperationStage.WaitingOperator, nextUnlock.AddSeconds(2));
        tracker.Observe(nextWaiting, [4]);

        Assert.Empty(Overdue(tracker, nextWaiting, FirstUnlock.AddMinutes(12)));
        AlarmEntry alarm = Assert.Single(Overdue(tracker, nextWaiting, nextUnlock + Threshold));
        Assert.Equal(4, alarm.PhysicalSlotNumber);
    }

    [Fact]
    public void ASlotJudgedUnknownIsWithdrawn()
    {
        SlotExpectedActionWaitTracker tracker = Waiting(slot: 3, since: FirstUnlock, out _);
        WireToGateHmiOperationSnapshot recovery =
            Operation(WireToGateHmiOperationStage.RecoveryRequired, FirstUnlock.AddMinutes(8));
        tracker.Observe(recovery, null);

        Assert.DoesNotContain(
            OnboardAlarmCodes.SlotExpectedActionOverdue,
            Codes(tracker, recovery, FirstUnlock.AddMinutes(8)));
    }

    [Fact]
    public void AnEndedOperationIsWithdrawnEvenIfNothingToldTheClock()
    {
        // 结果上报、取消接管、恢复向量都会换掉当前操作投影。求值器只认「同一次尝试、仍在开锁或等操作员」，
        // 计时器漏看一条投影也不会让告警挂着。
        SlotExpectedActionWaitTracker tracker = Waiting(slot: 3, since: FirstUnlock, out _);
        DateTimeOffset later = FirstUnlock.AddMinutes(8);

        Assert.Empty(Overdue(tracker, Operation(WireToGateHmiOperationStage.Completed, later), later));
        Assert.Empty(Overdue(tracker, Operation(WireToGateHmiOperationStage.Reporting, later), later));
        Assert.Empty(Overdue(
            tracker,
            Operation(WireToGateHmiOperationStage.WaitingOperator, later) with { SlotOperationAttemptId = NextAttempt },
            later));
        Assert.Empty(Overdue(tracker, null, later));
    }

    [Fact]
    public void AProjectionWithoutAnActiveSlotStopsTheClock()
    {
        // 装货取消接管后，清空执行器发的投影也叫「等操作员」，但那是另一件事（CP-0005 只覆盖 LOAD／UNLOAD）。
        SlotExpectedActionWaitTracker tracker = Waiting(slot: 3, since: FirstUnlock, out _);
        WireToGateHmiOperationSnapshot takenOver =
            Operation(WireToGateHmiOperationStage.WaitingOperator, FirstUnlock.AddMinutes(1));
        tracker.Observe(takenOver, null);

        Assert.Null(tracker.Current);
        Assert.Empty(Overdue(tracker, takenOver, FirstUnlock.AddMinutes(9)));
    }

    [Fact]
    public void TheMessageIsTheExpectedActionReadFromTheSlot()
    {
        SlotExpectedActionWaitTracker tracker = Waiting(slot: 3, since: FirstUnlock, out WireToGateHmiOperationSnapshot load);
        DateTimeOffset at = FirstUnlock + Threshold;

        // 装货，门开着、仓里还没货。
        Assert.Equal("放入货物并关好3号仓门", Single(tracker, load, at, Io(at, slot3Locked: false, slot3HasCargo: false)).Message);
        // 装货，货已放进、门没关好——锁舌断了一直读「未锁」也是这个样子。
        Assert.Equal("关好3号仓门", Single(tracker, load, at, Io(at, slot3Locked: false, slot3HasCargo: true)).Message);

        SlotExpectedActionWaitTracker unloading = new();
        WireToGateHmiOperationSnapshot unload =
            Operation(WireToGateHmiOperationStage.WaitingOperator, FirstUnlock) with { OperationType = OperationType.Unload };
        unloading.Observe(unload, [3]);
        Assert.Equal("取出货物并关好3号仓门", Single(unloading, unload, at, Io(at, slot3Locked: false, slot3HasCargo: true)).Message);
        Assert.Equal("关好3号仓门", Single(unloading, unload, at, Io(at, slot3Locked: false, slot3HasCargo: false)).Message);

        // 读数不可信时不猜门的状态，只说这类操作要做什么。
        Assert.Equal(
            "放入货物并关好3号仓门",
            Single(tracker, load, at, Io(at - TimeSpan.FromSeconds(10), slot3Locked: false, slot3HasCargo: true)).Message);
    }

    [Fact]
    public void TheRaisedAtDoesNotMoveWhileTheSameWaitGoesOn()
    {
        // 服务端按 alarmId 判断「新出现的超时」，alarmId 由 raisedAt 等内容派生；每轮求值都拿「此刻」会让它每秒换一次。
        SlotExpectedActionWaitTracker tracker = Waiting(slot: 3, since: FirstUnlock, out WireToGateHmiOperationSnapshot waiting);

        Assert.Equal(
            Single(tracker, waiting, FirstUnlock + Threshold, null).RaisedAt,
            Single(tracker, waiting, FirstUnlock + Threshold + TimeSpan.FromMinutes(3), null).RaisedAt);
    }

    private static SlotExpectedActionWaitTracker Waiting(
        int slot,
        DateTimeOffset since,
        out WireToGateHmiOperationSnapshot waiting)
    {
        SlotExpectedActionWaitTracker tracker = new();
        tracker.Observe(Operation(WireToGateHmiOperationStage.Unlocking, since), [slot]);
        waiting = Operation(WireToGateHmiOperationStage.WaitingOperator, since.AddSeconds(2));
        tracker.Observe(waiting, [slot]);
        return tracker;
    }

    private static WireToGateHmiOperationSnapshot Operation(WireToGateHmiOperationStage stage, DateTimeOffset at) =>
        new(Attempt, OperationType.Load, [3, 4], stage, "请操作。", at);

    private static AlarmEntry[] Overdue(
        SlotExpectedActionWaitTracker tracker,
        WireToGateHmiOperationSnapshot? current,
        DateTimeOffset now,
        TimeSpan? threshold = null) =>
        [.. Evaluate(tracker, current, now, null, threshold)
            .Where(alarm => alarm.AlarmCode == OnboardAlarmCodes.SlotExpectedActionOverdue)];

    private static AlarmEntry Single(
        SlotExpectedActionWaitTracker tracker,
        WireToGateHmiOperationSnapshot current,
        DateTimeOffset now,
        IoSnapshot? io) =>
        Assert.Single(Evaluate(tracker, current, now, io, null),
            alarm => alarm.AlarmCode == OnboardAlarmCodes.SlotExpectedActionOverdue);

    private static string[] Codes(
        SlotExpectedActionWaitTracker tracker,
        WireToGateHmiOperationSnapshot current,
        DateTimeOffset now) =>
        [.. Evaluate(tracker, current, now, null, null).Select(alarm => alarm.AlarmCode)];

    private static IReadOnlyList<AlarmEntry> Evaluate(
        SlotExpectedActionWaitTracker tracker,
        WireToGateHmiOperationSnapshot? current,
        DateTimeOffset now,
        IoSnapshot? io,
        TimeSpan? threshold) =>
        OnboardAlarmEvaluator.Evaluate(new OnboardAlarmInputs(
            now,
            true,
            io ?? Io(now, slot3Locked: false, slot3HasCargo: false),
            TimeSpan.FromSeconds(3),
            null,
            Controller(now),
            new VehicleSafetySignal(VehicleMotionState.Stopped, now, "TEST"),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500),
            [],
            current)
        {
            ExpectedActionWait = tracker.Current,
            ExpectedActionOverdueThreshold = threshold ?? Threshold
        });

    private static IoSnapshot Io(DateTimeOffset at, bool slot3Locked, bool slot3HasCargo) => new(
        true,
        [.. Enumerable.Range(0, 8).Select(index => index == 2
            ? new LockerSnapshot(index, 3, false, slot3Locked, !slot3HasCargo, at)
            : new LockerSnapshot(index, index + 1, false, true, true, at))],
        at);

    private static OnboardSnapshot Controller(DateTimeOffset at) => new(
        OnboardState.WaitingArrival,
        false,
        true,
        null,
        Io(at, slot3Locked: true, slot3HasCargo: false),
        null,
        false,
        "等待到站。",
        null,
        at);
}
