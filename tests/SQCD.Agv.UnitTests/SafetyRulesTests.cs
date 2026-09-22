using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

public sealed class SafetyRulesTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public void ResumeAuthorizationRequiresExactPersistedAttemptAndCheckpoint()
    {
        WireToGateRecoveryState state = new(
            "11111111-1111-4111-8111-111111111111",
            WireToGateRecoveryCheckpoint.SafeFinishReached,
            [],
            0,
            []);

        Assert.True(WireToGateRecoverySafetyPolicy.MatchesPersistedResumeState(
            state,
            "11111111-1111-4111-8111-111111111111",
            WireToGateRecoveryCheckpoint.SafeFinishReached));
        Assert.False(WireToGateRecoverySafetyPolicy.MatchesPersistedResumeState(
            state,
            "22222222-2222-4222-8222-222222222222",
            WireToGateRecoveryCheckpoint.SafeFinishReached));
        Assert.False(WireToGateRecoverySafetyPolicy.MatchesPersistedResumeState(
            state,
            "11111111-1111-4111-8111-111111111111",
            WireToGateRecoveryCheckpoint.Prepared));
        Assert.False(WireToGateRecoverySafetyPolicy.MatchesPersistedResumeState(
            state with { ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ResultRecorded },
            "11111111-1111-4111-8111-111111111111",
            WireToGateRecoveryCheckpoint.ResultRecorded));
    }

    [Fact]
    public void RecordedVehicleSafetyProviderDistinguishesStoppedMovingAndUnknown()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RecordedVehicleSafetySignalProvider provider = new();

        provider.Set(VehicleMotionState.Stopped, now.AddMilliseconds(-100));
        VehicleSafetySignal stopped = provider.Read();
        Assert.True(stopped.IsStoppedAndFresh(now, TimeSpan.FromSeconds(1)));

        provider.Set(VehicleMotionState.Moving, now);
        VehicleSafetySignal moving = provider.Read();
        Assert.False(moving.IsStoppedAndFresh(now, TimeSpan.FromSeconds(1)));

        provider.Set(VehicleMotionState.Unknown, now.AddSeconds(-2));
        VehicleSafetySignal expired = provider.Read();
        Assert.False(expired.IsFresh(now, TimeSpan.FromSeconds(1)));
        Assert.Equal(VehicleMotionState.Unknown, expired.MotionState);
    }

    [Fact]
    public void UnavailableVehicleSafetyProviderFailsClosed()
    {
        VehicleSafetySignal signal = new UnavailableVehicleSafetySignalProvider().Read();

        Assert.Equal(VehicleMotionState.Unknown, signal.MotionState);
        Assert.False(signal.IsStoppedAndFresh(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(100, 500, true)]
    [InlineData(500, 500, true)]
    [InlineData(501, 500, false)]
    [InlineData(-5_000, 500, true)]
    [InlineData(-5_001, 500, false)]
    public void VehicleSafetyFreshnessAllowsOnlyBoundedClockSkew(
        int observedAtOffsetMs,
        int clockSkewToleranceMs,
        bool expected)
    {
        DateTimeOffset now = new(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);
        VehicleSafetySignal signal = new(
            VehicleMotionState.Stopped,
            now.AddMilliseconds(observedAtOffsetMs),
            "CONTROL_SERVER");

        Assert.Equal(
            expected,
            signal.IsFresh(
                now,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(clockSkewToleranceMs)));
    }

    [Fact]
    public void VehicleSafetyFreshnessRejectsInvalidPolicyInputs()
    {
        DateTimeOffset now = new(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);

        Assert.False(VehicleSafetyFreshness.IsFresh(
            now,
            now,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(500)));
        Assert.False(VehicleSafetyFreshness.IsFresh(
            now,
            now,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(-1)));
        Assert.False(VehicleSafetyFreshness.IsFresh(
            default,
            now,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500)));
    }

    [Theory]
    [InlineData(VehicleMotionState.Unknown, true, "VEHICLE_NOT_READY")]
    [InlineData(VehicleMotionState.Moving, false, "ACTION_NOT_ALLOWED_IN_STATE")]
    public void WireToGateSafetySummaryPreservesVehicleTriState(
        VehicleMotionState motionState,
        bool expectedUnknown,
        string expectedReason)
    {
        DateTimeOffset now = new(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);
        IoSnapshot snapshot = CreateSnapshot(targetHasCargo: false, targetLocked: true) with
        {
            ObservedAt = now,
            Lockers = CreateSnapshot(false, true).Lockers
                .Select(locker => locker with { ObservedAt = now })
                .ToArray()
        };

        var summary = WireToGateSafetyEvaluator.Evaluate(
            snapshot,
            new VehicleSafetySignal(motionState, now.AddMilliseconds(100), "CONTROL_SERVER"),
            now,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500),
            fatalFaultLatched: false);

        Assert.False(summary.DepartureSafe);
        Assert.False(summary.VehicleStopped);
        Assert.Equal(expectedUnknown, summary.UnknownPresent);
        Assert.Contains(expectedReason, summary.ReasonCodes);
        Assert.DoesNotContain(
            expectedUnknown ? "ACTION_NOT_ALLOWED_IN_STATE" : "VEHICLE_NOT_READY",
            summary.ReasonCodes);
    }

    /// <summary>
    /// A latched fatal safety fault makes departure unsafe on its own, with every physical reading in order
    /// (8005-agv-onboard-hmi#197). Before this the evaluator never saw the latch, and a latched vehicle told the
    /// server it could leave.
    /// </summary>
    /// <remarks>
    /// Only <c>departureSafe</c> and the reason change. The other four fields describe physical readings, and the
    /// latch does not change any of them -- in particular it is a known state, not an unknown one, so it must not
    /// set <c>unknownPresent</c> (the server would read that as missing evidence and answer UNKNOWN, not UNSAFE).
    /// </remarks>
    [Fact]
    public void AFatalFaultLatchAloneMakesDepartureUnsafeAndSaysSo()
    {
        DateTimeOffset now = DateTimeOffset.Now;

        var summary = EvaluateAllInOrder(now, VehicleMotionState.Stopped, fatalFaultLatched: true);

        Assert.False(summary.DepartureSafe);
        Assert.Equal([WireToGateSafetyEvaluator.FatalFaultLatchedReason], summary.ReasonCodes);
        Assert.True(summary.VehicleStopped);
        Assert.True(summary.AllTargetSlotsLocked);
        Assert.True(summary.AllUnlockOutputsReset);
        Assert.False(summary.UnknownPresent);
    }

    /// <summary>
    /// The unlatched evaluation is what it was before #197: safe, no reason.
    /// </summary>
    [Fact]
    public void WithoutALatchTheSafetySummaryIsUnchanged()
    {
        DateTimeOffset now = DateTimeOffset.Now;

        var summary = EvaluateAllInOrder(now, VehicleMotionState.Stopped, fatalFaultLatched: false);

        Assert.True(summary.DepartureSafe);
        Assert.Empty(summary.ReasonCodes);
        Assert.False(summary.UnknownPresent);
    }

    /// <summary>
    /// The latch reason is added beside the others, never in place of them, and it is a code outside both of the
    /// control server's allow-lists that relax departure safety: <c>OwnMovementOrderExplanation.VehicleOnlyReasons</c>
    /// (VEHICLE_NOT_READY, ACTION_NOT_ALLOWED_IN_STATE -- control-server#314 lets the pickup dispatch plan through
    /// when every reason is in it) and <c>WireToGateStore.OperationInducedUnsafety</c> (LOCK_NOT_CLOSED,
    /// UNLOCK_OUTPUT_NOT_RESET -- readiness stays Ready mid-load when every reason is in it).
    /// </summary>
    /// <remarks>
    /// The two cases are the two in which the server would otherwise excuse the unsafety: a vehicle driving on this
    /// server's own order (motion unknown, <c>VEHICLE_NOT_READY</c>) and a vehicle whose door stands open for a load.
    /// Had the latch borrowed <c>VEHICLE_NOT_READY</c>, the first would read exactly as it reads unlatched. The two
    /// allow-lists are copied here from control-server <c>fp/v2-impl</c> 80a12868; if either gains
    /// <c>DEPARTURE_UNSAFE</c>, this reason has to move.
    /// </remarks>
    [Theory]
    [InlineData(VehicleMotionState.Unknown, false, "VEHICLE_NOT_READY")]
    [InlineData(VehicleMotionState.Stopped, true, "LOCK_NOT_CLOSED")]
    public void TheLatchReasonStaysBesideTheOthersAndOutsideEveryServerAllowList(
        VehicleMotionState motionState,
        bool doorOpen,
        string otherReason)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string[] vehicleOnlyReasons = ["VEHICLE_NOT_READY", "ACTION_NOT_ALLOWED_IN_STATE"];
        string[] operationInducedUnsafety = ["LOCK_NOT_CLOSED", "UNLOCK_OUTPUT_NOT_RESET"];

        var latched = EvaluateAllInOrder(now, motionState, fatalFaultLatched: true, doorOpen: doorOpen);
        var unlatched = EvaluateAllInOrder(now, motionState, fatalFaultLatched: false, doorOpen: doorOpen);

        Assert.Contains(otherReason, unlatched.ReasonCodes);
        Assert.Equal(
            [.. unlatched.ReasonCodes, WireToGateSafetyEvaluator.FatalFaultLatchedReason],
            latched.ReasonCodes);
        Assert.Equal(unlatched.UnknownPresent, latched.UnknownPresent);
        Assert.False(latched.DepartureSafe);
        Assert.DoesNotContain(WireToGateSafetyEvaluator.FatalFaultLatchedReason, vehicleOnlyReasons);
        Assert.DoesNotContain(WireToGateSafetyEvaluator.FatalFaultLatchedReason, operationInducedUnsafety);
    }

    private static WireToGateSafetySummaryPayload EvaluateAllInOrder(
        DateTimeOffset now,
        VehicleMotionState motionState,
        bool fatalFaultLatched,
        bool doorOpen = false)
    {
        IoSnapshot snapshot = CreateSnapshot(targetHasCargo: false, targetLocked: !doorOpen) with
        {
            ObservedAt = now,
            Lockers = CreateSnapshot(false, !doorOpen).Lockers
                .Select(locker => locker with { ObservedAt = now })
                .ToArray()
        };
        return WireToGateSafetyEvaluator.Evaluate(
            snapshot,
            new VehicleSafetySignal(motionState, now.AddMilliseconds(100), "CONTROL_SERVER"),
            now,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500),
            fatalFaultLatched);
    }

    [Fact]
    public void RecoverySafetyPolicyRequiresAuthorizationPersistenceAndFreshPhysicalFacts()
    {
        WireToGateRecoverySafetyFacts facts = WireToGateRecoverySafetyFacts.Unknown;

        Assert.Equal(
            "RECOVERY_AUTHENTICATION_FAILED",
            WireToGateRecoverySafetyPolicy.Evaluate(facts).ReasonCode);

        facts = facts with { RecoverySessionAuthorized = true };
        Assert.Equal(
            "RECOVERY_SESSION_NOT_OPEN",
            WireToGateRecoverySafetyPolicy.Evaluate(facts).ReasonCode);

        facts = facts with { RecoveryStatePersisted = true };
        Assert.Equal(
            "VEHICLE_NOT_READY",
            WireToGateRecoverySafetyPolicy.Evaluate(facts).ReasonCode);

        facts = facts with { VehicleSignalFresh = true, VehicleStopped = true };
        Assert.Equal(
            "SLOT_STATE_UNKNOWN",
            WireToGateRecoverySafetyPolicy.Evaluate(facts).ReasonCode);

        facts = facts with { AllTargetSlotsKnown = true };
        Assert.Equal(
            "LOCK_NOT_CLOSED",
            WireToGateRecoverySafetyPolicy.Evaluate(facts).ReasonCode);

        facts = facts with { AllTargetSlotsLocked = true };
        Assert.Equal(
            "UNLOCK_OUTPUT_NOT_RESET",
            WireToGateRecoverySafetyPolicy.Evaluate(facts).ReasonCode);

        facts = facts with { AllUnlockOutputsReset = true };
        Assert.Equal(
            WireToGateRecoverySafetyDecision.Allow(),
            WireToGateRecoverySafetyPolicy.Evaluate(facts));
    }

    [Fact]
    public void LoadIntoEmptyLockedSlotIsAllowed()
    {
        ScanAuthorization authorization = CreateAuthorization(OperationType.Load, slotIndex: 0);
        IoSnapshot snapshot = CreateSnapshot(targetHasCargo: false, targetLocked: true);

        string? error = Validate(authorization, snapshot);

        Assert.Null(error);
    }

    [Fact]
    public void LoadIntoOccupiedSlotIsRejected()
    {
        ScanAuthorization authorization = CreateAuthorization(OperationType.Load, slotIndex: 0);
        IoSnapshot snapshot = CreateSnapshot(targetHasCargo: true, targetLocked: true);

        string? error = Validate(authorization, snapshot);

        Assert.Equal("SLOT_NOT_EMPTY", error);
    }

    [Fact]
    public void UnloadFromEmptySlotIsRejected()
    {
        ScanAuthorization authorization = CreateAuthorization(OperationType.Unload, slotIndex: 0);
        IoSnapshot snapshot = CreateSnapshot(targetHasCargo: false, targetLocked: true);

        string? error = Validate(authorization, snapshot);

        Assert.Equal("SLOT_HAS_NO_CARGO", error);
    }

    [Fact]
    public void DepartureIsRejectedWhenAnyDoorIsUnlocked()
    {
        IoSnapshot snapshot = CreateSnapshot(targetHasCargo: false, targetLocked: false);

        bool permitted = IsDeparturePermitted(snapshot);

        Assert.False(permitted);
    }

    [Fact]
    public void DepartureIsRejectedWhenAnyUnlockOutputIsActive()
    {
        IoSnapshot snapshot = ReplaceLocker(CreateSnapshot(false, true), 0, unlockOutput: true, isLocked: true);

        Assert.False(IsDeparturePermitted(snapshot));
    }

    [Fact]
    public void DepartureIsRejectedByBlockingFault()
    {
        IoSnapshot snapshot = CreateSnapshot(false, true);

        bool permitted = SafetyRules.IsDeparturePermitted(
            snapshot,
            null,
            hasBlockingFault: true,
            DateTimeOffset.Now,
            TimeSpan.FromSeconds(1));

        Assert.False(permitted);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public void StaleSnapshotIsRejectedBeforeUnlockAndDeparture()
    {
        ScanAuthorization authorization = CreateAuthorization(OperationType.Load, slotIndex: 0);
        IoSnapshot current = CreateSnapshot(false, true);
        IoSnapshot stale = current with
        {
            ObservedAt = DateTimeOffset.Now.AddSeconds(-5),
            Lockers = current.Lockers.Select(locker => locker with { ObservedAt = DateTimeOffset.Now.AddSeconds(-5) }).ToArray()
        };

        Assert.Equal("IO_SNAPSHOT_STALE", Validate(authorization, stale));
        Assert.False(IsDeparturePermitted(stale));
    }

    [Fact]
    public void ActiveUnlockOutputIsRejectedBeforeUnlock()
    {
        ScanAuthorization authorization = CreateAuthorization(OperationType.Load, slotIndex: 0);
        IoSnapshot snapshot = ReplaceLocker(CreateSnapshot(false, true), 0, unlockOutput: true, isLocked: true);

        Assert.Equal("UNLOCK_OUTPUT_ACTIVE", Validate(authorization, snapshot));
    }

    [Fact]
    public void AnotherUnlockedDoorIsRejectedBeforeUnlock()
    {
        ScanAuthorization authorization = CreateAuthorization(OperationType.Load, slotIndex: 0);
        IoSnapshot snapshot = ReplaceLocker(CreateSnapshot(false, true), 1, unlockOutput: false, isLocked: false);

        Assert.Equal("SLOT_NOT_LOCKED", Validate(authorization, snapshot));
    }

    private static string? Validate(ScanAuthorization authorization, IoSnapshot snapshot)
    {
        return SafetyRules.ValidateBeforeUnlock(
            authorization,
            snapshot,
            new HashSet<string>(),
            DateTimeOffset.Now,
            TimeSpan.FromSeconds(1));
    }

    private static bool IsDeparturePermitted(IoSnapshot snapshot)
    {
        return SafetyRules.IsDeparturePermitted(
            snapshot,
            null,
            hasBlockingFault: false,
            DateTimeOffset.Now,
            TimeSpan.FromSeconds(1));
    }

    private static ScanAuthorization CreateAuthorization(OperationType type, int slotIndex)
    {
        return new ScanAuthorization(
            true,
            "OP-001",
            "TASK-001",
            "SUBLOT-001",
            slotIndex,
            type,
            type == OperationType.Load,
            null,
            null);
    }

    private static IoSnapshot CreateSnapshot(bool targetHasCargo, bool targetLocked)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        LockerSnapshot[] lockers = Enumerable.Range(0, 8)
            .Select(index => new LockerSnapshot(
                index,
                index + 1,
                false,
                index == 0 ? targetLocked : true,
                index == 0 ? !targetHasCargo : true,
                now))
            .ToArray();
        return new IoSnapshot(true, lockers, now);
    }

    private static IoSnapshot ReplaceLocker(
        IoSnapshot snapshot,
        int slotIndex,
        bool unlockOutput,
        bool isLocked)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        LockerSnapshot[] lockers = snapshot.Lockers.Select(locker => locker.SlotIndex == slotIndex
            ? locker with { UnlockOutputRaw = unlockOutput, LockFeedbackRaw = isLocked, ObservedAt = now }
            : locker with { ObservedAt = now }).ToArray();
        return snapshot with { Lockers = lockers, ObservedAt = now };
    }
}
