using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

public sealed class SafetyRulesTests
{
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
