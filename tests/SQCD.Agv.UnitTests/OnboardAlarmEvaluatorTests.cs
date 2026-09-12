using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 车载端告警的判定规则与告警板的整份替换（<c>FP-IS-15</c> 车载半边，REQ-0269／REQ-0270）。
/// </summary>
/// <remarks>
/// 不挂 <c>IntegrationSlice</c> trait，理由同 <see cref="OnboardAlarmSnapshotTests"/>。线上那一半——会话中途报快照、
/// 监视器按比对决定发不发——在 G2 测试里。
/// </remarks>
public sealed class OnboardAlarmEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AStoppedVehicleWithEverySlotSecuredRaisesNothing()
    {
        Assert.Empty(OnboardAlarmEvaluator.Evaluate(WireToGateInputs()));
    }

    [Fact]
    public void ALostIoModuleIsOneAlarmAndClaimsNothingAboutTheSlots()
    {
        OnboardAlarmInputs inputs = WireToGateInputs(WithSlot(Io(), 2, NoLockFeedback)) with { IoConnected = false };

        AlarmEntry alarm = Assert.Single(OnboardAlarmEvaluator.Evaluate(inputs));
        Assert.Equal(OnboardAlarmCodes.IoModuleDisconnected, alarm.AlarmCode);
        Assert.Equal(OnboardAlarmEvaluator.Critical, alarm.Severity);
        Assert.Equal(AlarmScope.CurrentVehicle, alarm.Scope);
    }

    [Fact]
    public void AStaleSlotReadingIsReportedAsStaleAndIsNotReadForLockFeedback()
    {
        // 一份不可信的读数上判出来的「锁反馈读不到」同样不可信。
        IoSnapshot stale = WithSlot(Io(), 2, NoLockFeedback) with { ObservedAt = Now - TimeSpan.FromSeconds(10) };

        Assert.Equal([OnboardAlarmCodes.SlotStateStale], Codes(WireToGateInputs(stale)));
    }

    [Fact]
    public void UnreadableLockFeedbackIsOneAlarmNamingEverySlotAndPointsAtTheSlotWhenThereIsOnlyOne()
    {
        IoSnapshot two = WithSlot(WithSlot(Io(), 5, NoLockFeedback), 2, NoLockFeedback);
        AlarmEntry both = Assert.Single(OnboardAlarmEvaluator.Evaluate(WireToGateInputs(two)));
        Assert.Equal(OnboardAlarmCodes.SlotLockFeedbackLost, both.AlarmCode);
        Assert.Null(both.PhysicalSlotNumber);
        Assert.Contains("2号仓、5号仓", both.Message, StringComparison.Ordinal);

        AlarmEntry one = Assert.Single(OnboardAlarmEvaluator.Evaluate(WireToGateInputs(WithSlot(Io(), 7, NoLockFeedback))));
        Assert.Equal(7, one.PhysicalSlotNumber);
    }

    [Fact]
    public void ABlockedLightCurtainIsNotAnAlarmBecauseItOnlyMeansTheSlotHoldsCargo()
    {
        // 光幕被挡就是仓里有货。车载着货停着、走着都是常态，照「光幕被挡」报会一直报。
        IoSnapshot loaded = WithSlot(Io(), 1, slot => slot with { LightCurtainRaw = false });

        Assert.Empty(OnboardAlarmEvaluator.Evaluate(WireToGateInputs(loaded)));
        Assert.Empty(OnboardAlarmEvaluator.Evaluate(
            WireToGateInputs(loaded) with { VehicleSafety = Signal(VehicleMotionState.Moving) }));
    }

    [Fact]
    public void TheLegacyRuleGatewayIsJudgedOnlyWhereItIsInUse()
    {
        // WIRE_TO_GATE 模式下网关是空实现，连接状态恒为断开；照原条件判会一直报。
        Assert.Empty(OnboardAlarmEvaluator.Evaluate(WireToGateInputs()));
        Assert.Empty(OnboardAlarmEvaluator.Evaluate(LegacyInputs()));
        Assert.Equal(
            [OnboardAlarmCodes.RuleGatewayDisconnected],
            Codes(LegacyInputs() with { LegacyRuleGatewayConnected = false }));
    }

    [Fact]
    public void AnOperationLeftWaitingForRecoveryIsAnAlarmAboutThatOperationAndShowsWhileItIsCurrent()
    {
        WireToGateHmiOperationSnapshot unfinished = new(
            "ATT-9",
            OperationType.Load,
            [3],
            WireToGateHmiOperationStage.RecoveryRequired,
            "操作需要管理员恢复。",
            Now);

        AlarmEntry alarm = Assert.Single(OnboardAlarmEvaluator.Evaluate(
            WireToGateInputs() with { CurrentOperation = unfinished }));
        Assert.Equal(OnboardAlarmCodes.SlotOperationUnfinished, alarm.AlarmCode);
        Assert.Equal(OnboardAlarmEvaluator.Critical, alarm.Severity);
        Assert.Equal(AlarmScope.CurrentOperation, alarm.Scope);
        Assert.Equal("ATT-9", alarm.SlotOperationAttemptId);
        Assert.Equal(3, alarm.PhysicalSlotNumber);

        // 它归当前操作：那次操作还是当前的，本机界面就显示它。
        OnboardAlarmSnapshot snapshot = new("AGV-01", 1, Now, [alarm]);
        Assert.Single(OnboardAlarmVisibility.ForLocalDisplay(snapshot, new OnboardAlarmContext("AGV-01", null, "ATT-9")));

        foreach (WireToGateHmiOperationStage stage in Enum.GetValues<WireToGateHmiOperationStage>()
            .Where(stage => stage != WireToGateHmiOperationStage.RecoveryRequired))
        {
            Assert.Empty(OnboardAlarmEvaluator.Evaluate(
                WireToGateInputs() with { CurrentOperation = unfinished with { Stage = stage } }));
        }
    }

    [Fact]
    public void ALegacyOperationTimeoutIsReportedAgainstTheOperationThatTimedOut()
    {
        ActiveOperation active = new(
            "VISIT-1",
            "OP-1",
            "TASK-1",
            "SUBLOT-1",
            1,
            OperationType.Load,
            OperationStage.WaitingCargoAndRelock,
            Now);
        OnboardAlarmInputs inputs = LegacyInputs() with
        {
            Controller = Controller() with { ErrorCode = "RELOCK_OR_CARGO_TIMEOUT", ActiveOperation = active }
        };

        AlarmEntry alarm = Assert.Single(OnboardAlarmEvaluator.Evaluate(inputs));
        Assert.Equal(OnboardAlarmCodes.StationOperationOverdue, alarm.AlarmCode);
        Assert.Equal(AlarmScope.CurrentOperation, alarm.Scope);
        Assert.Equal("OP-1", alarm.SlotOperationAttemptId);
        Assert.Equal(2, alarm.PhysicalSlotNumber);
    }

    [Fact]
    public void ALatchedSafetyFaultIsCriticalAndCarriesItsReasonCode()
    {
        OnboardAlarmInputs inputs = WireToGateInputs() with
        {
            Controller = Controller() with { State = OnboardState.Faulted, ErrorCode = "STARTUP_STATE_UNSAFE" }
        };

        AlarmEntry alarm = Assert.Single(OnboardAlarmEvaluator.Evaluate(inputs));
        Assert.Equal(OnboardAlarmCodes.SafetyFaultLatched, alarm.AlarmCode);
        Assert.Equal(OnboardAlarmEvaluator.Critical, alarm.Severity);
        Assert.Contains("STARTUP_STATE_UNSAFE", alarm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADepartureSafetySignalThatIsUnknownOrOutOfDateIsAnAlarmWhereTheSignalIsInUse()
    {
        AlarmEntry unknown = Assert.Single(OnboardAlarmEvaluator.Evaluate(WireToGateInputs() with
        {
            VehicleSafety = Signal(VehicleMotionState.Unknown) with { ReasonCodes = ["REQUEST_TIMEOUT"] }
        }));
        Assert.Equal(OnboardAlarmCodes.DepartureSafetySignalUnavailable, unknown.AlarmCode);
        Assert.Contains("REQUEST_TIMEOUT", unknown.Message, StringComparison.Ordinal);

        Assert.Equal(
            [OnboardAlarmCodes.DepartureSafetySignalUnavailable],
            Codes(WireToGateInputs() with
            {
                VehicleSafety = Signal(VehicleMotionState.Stopped) with { ObservedAt = Now - TimeSpan.FromMinutes(1) }
            }));

        // 旧模式不轮询这个信号，不适用就不判。
        Assert.Empty(OnboardAlarmEvaluator.Evaluate(LegacyInputs()));
    }

    [Fact]
    public void ASlotThatIsNotSecuredWhileTheVehicleMovesIsCritical()
    {
        OnboardAlarmInputs moving = WireToGateInputs() with { VehicleSafety = Signal(VehicleMotionState.Moving) };
        Assert.Empty(OnboardAlarmEvaluator.Evaluate(moving));

        IoSnapshot unsecured = WithSlot(
            WithSlot(Io(), 3, slot => slot with { LockFeedbackRaw = false }),
            6,
            slot => slot with { UnlockOutputRaw = true });
        AlarmEntry alarm = Assert.Single(OnboardAlarmEvaluator.Evaluate(moving with { Io = unsecured }));
        Assert.Equal(OnboardAlarmCodes.SlotUnsecuredWhileMoving, alarm.AlarmCode);
        Assert.Equal(OnboardAlarmEvaluator.Critical, alarm.Severity);
        Assert.Contains("3号仓、6号仓", alarm.Message, StringComparison.Ordinal);

        // 停着开仓是作业本身，不是告警——v2 线上装卸不经过控制器，按「非作业期间」判会误报。
        Assert.Empty(OnboardAlarmEvaluator.Evaluate(WireToGateInputs(unsecured)));

        // 行驶状态本身过期时不拿它判仓门；过期是另一条告警。
        Assert.Equal(
            [OnboardAlarmCodes.DepartureSafetySignalUnavailable],
            Codes(moving with
            {
                Io = unsecured,
                VehicleSafety = Signal(VehicleMotionState.Moving) with { ObservedAt = Now - TimeSpan.FromMinutes(1) }
            }));
    }

    [Fact]
    public void AFingerprintMismatchReportedByTheSessionIsAnAlarm()
    {
        OnboardAlarmInputs inputs = WireToGateInputs() with
        {
            SessionReasonCodes = [SlotConfigurationActivationCoordinator.FingerprintMismatchReasonCode]
        };

        AlarmEntry alarm = Assert.Single(OnboardAlarmEvaluator.Evaluate(inputs));
        Assert.Equal(OnboardAlarmCodes.SlotConfigurationMismatch, alarm.AlarmCode);
        Assert.Equal(OnboardAlarmEvaluator.Critical, alarm.Severity);
    }

    [Fact]
    public void EveryAlarmTheEvaluatorRaisesIsDeclaredSoTheRegistryDisjointnessCheckCoversIt()
    {
        IoSnapshot unsecured = WithSlot(Io(), 4, slot => slot with { LockFeedbackRaw = false });
        OnboardAlarmInputs[] worstCases =
        [
            WireToGateInputs() with { IoConnected = false },
            WireToGateInputs(Io() with { ObservedAt = Now - TimeSpan.FromSeconds(10) }),
            WireToGateInputs(WithSlot(Io(), 1, NoLockFeedback)),
            LegacyInputs() with
            {
                LegacyRuleGatewayConnected = false,
                Controller = Controller() with { ErrorCode = "UNLOCK_FEEDBACK_TIMEOUT" }
            },
            WireToGateInputs() with
            {
                Controller = Controller() with { State = OnboardState.Faulted, ErrorCode = "UNHANDLED_UI_ERROR" },
                VehicleSafety = Signal(VehicleMotionState.Unknown),
                SessionReasonCodes = [SlotConfigurationActivationCoordinator.FingerprintMismatchReasonCode],
                CurrentOperation = new WireToGateHmiOperationSnapshot(
                    "ATT-1", OperationType.Load, [1], WireToGateHmiOperationStage.RecoveryRequired, "需要恢复。", Now)
            },
            WireToGateInputs(unsecured) with { VehicleSafety = Signal(VehicleMotionState.Moving) }
        ];

        string[] raised = [.. worstCases.SelectMany(Codes).Distinct().Order(StringComparer.Ordinal)];

        Assert.Equal(
            OnboardAlarmCodes.All
                .Where(code => code != OnboardAlarmCodes.SlotLightCurtainBlocked)
                .Order(StringComparer.Ordinal),
            raised);
    }

    [Fact]
    public void ReplacingTheAlarmSetKeepsWhenAStillStandingAlarmWasFirstRaised()
    {
        OnboardAlarmBoard board = new("AGV-01", new FixedClock(Now));
        AlarmEntry first = new(
            OnboardAlarmCodes.IoModuleDisconnected, OnboardAlarmEvaluator.Critical, Now, AlarmScope.CurrentVehicle, "离线。");

        Assert.True(board.ReplaceAll([first]));

        // 下一轮求值给的抬起时间是「此刻」，内容没变：不算变化，抬起时间留在第一次。
        Assert.False(board.ReplaceAll([first with { RaisedAt = Now.AddSeconds(1) }]));
        Assert.Equal(Now, Assert.Single(board.Peek().Alarms).RaisedAt);

        // 内容变了就是另一条。
        Assert.True(board.ReplaceAll([first with { RaisedAt = Now.AddSeconds(2), Message = "仍然离线。" }]));
        Assert.Equal(Now.AddSeconds(2), Assert.Single(board.Peek().Alarms).RaisedAt);

        Assert.True(board.ReplaceAll([]));
        Assert.Empty(board.Peek().Alarms);
    }

    [Fact]
    public void PeekingReadsTheCurrentSetWithoutSpendingASequenceNumber()
    {
        OnboardAlarmBoard board = new("AGV-01", new FixedClock(Now));

        Assert.Equal(0, board.Peek().SnapshotSequence);
        Assert.Equal(1, board.Capture().SnapshotSequence);
        Assert.Equal(1, board.Peek().SnapshotSequence);
        Assert.Equal(1, board.Peek().SnapshotSequence);
        Assert.Equal(2, board.Capture().SnapshotSequence);
    }

    private static OnboardAlarmInputs WireToGateInputs(IoSnapshot? io = null) => new(
        Now,
        true,
        io ?? Io(),
        TimeSpan.FromSeconds(3),
        null,
        Controller(),
        Signal(VehicleMotionState.Stopped),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMilliseconds(500),
        [],
        null);

    private static OnboardAlarmInputs LegacyInputs() =>
        WireToGateInputs() with { LegacyRuleGatewayConnected = true, VehicleSafety = null };

    private static OnboardSnapshot Controller() => new(
        OnboardState.WaitingArrival,
        false,
        true,
        null,
        Io(),
        null,
        false,
        "等待到站。",
        null,
        Now);

    private static VehicleSafetySignal Signal(VehicleMotionState state) => new(state, Now, "TEST");

    private static IoSnapshot Io() => new(
        true,
        [.. Enumerable.Range(0, 8).Select(index => new LockerSnapshot(index, index + 1, false, true, true, Now))],
        Now);

    private static IoSnapshot WithSlot(IoSnapshot io, int physicalSlot, Func<LockerSnapshot, LockerSnapshot> change) =>
        io with
        {
            Lockers = [.. io.Lockers.Select(locker => locker.PhysicalNumber == physicalSlot ? change(locker) : locker)]
        };

    private static LockerSnapshot NoLockFeedback(LockerSnapshot slot) => slot with { LockFeedbackRaw = null };

    private static string[] Codes(OnboardAlarmInputs inputs) =>
        [.. OnboardAlarmEvaluator.Evaluate(inputs).Select(alarm => alarm.AlarmCode)];

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
