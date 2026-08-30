using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

public sealed class SafetyRulesTests
{
    [Fact]
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

    [Fact]
    public void RecoverySafetyPolicyRequiresAuthorizationPersistenceAndFreshPhysicalFacts()
    {
        WireToGateRecoverySafetyFacts facts = WireToGateRecoverySafetyFacts.Unknown;

        Assert.Equal(
            "RECOVERY_AUTHORIZATION_REQUIRED",
            WireToGateRecoverySafetyPolicy.Evaluate(facts).ReasonCode);

        facts = facts with { RecoverySessionAuthorized = true };
        Assert.Equal(
            "RECOVERY_STATE_NOT_PERSISTED",
            WireToGateRecoverySafetyPolicy.Evaluate(facts).ReasonCode);

        facts = facts with { RecoveryStatePersisted = true };
        Assert.Equal(
            "VEHICLE_STATE_UNKNOWN",
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
