namespace SQCD.Agv.Core;

public static class SafetyRules
{
    // 是否允许发车
    public static bool IsDeparturePermitted(
        IoSnapshot snapshot,
        ActiveOperation? activeOperation,
        bool hasBlockingFault,
        DateTimeOffset now,
        TimeSpan snapshotMaxAge)
    {
        return snapshot.IsConnected
            && activeOperation is null
            && !hasBlockingFault
            && IsSnapshotFresh(snapshot, now, snapshotMaxAge)
            && snapshot.Lockers.Count == 8
            && snapshot.Lockers.All(locker => locker.IsKnown
                && locker.UnlockOutputRaw is false
                && locker.IsLocked);
    }

    // 开锁前检查
    public static string? ValidateBeforeUnlock(
        ScanAuthorization authorization,
        IoSnapshot snapshot,
        IReadOnlySet<string> startedOperationIds,
        DateTimeOffset now,
        TimeSpan snapshotMaxAge)
    {
        if (!authorization.Accepted)
        {
            return authorization.ErrorCode ?? "SCAN_REJECTED";
        }

        if (string.IsNullOrWhiteSpace(authorization.OperationId)
            || string.IsNullOrWhiteSpace(authorization.TaskId)
            || authorization.SlotIndex is not (>= 0 and <= 7)
            || authorization.OperationType is null)
        {
            return "INVALID_RULE_RESPONSE";
        }

        if (startedOperationIds.Contains(authorization.OperationId))
        {
            return "DUPLICATE_OPERATION";
        }

        string? doorSafetyError = ValidateAllDoorsSafe(snapshot, now, snapshotMaxAge);
        if (doorSafetyError is not null)
        {
            return doorSafetyError;
        }

        LockerSnapshot target = snapshot.GetLocker(authorization.SlotIndex.Value);

        if (authorization.OperationType == OperationType.Load && target.HasCargo)
        {
            return "SLOT_NOT_EMPTY";
        }

        if (authorization.OperationType == OperationType.Unload && !target.HasCargo)
        {
            return "SLOT_HAS_NO_CARGO";
        }

        return null;
    }

    // 需要开锁、完成操作或允许发车前都可复用的全车仓门安全检查。
    public static string? ValidateAllDoorsSafe(
        IoSnapshot snapshot,
        DateTimeOffset now,
        TimeSpan snapshotMaxAge)
    {
        if (!snapshot.IsConnected)
        {
            return "IO_OFFLINE";
        }

        if (!IsSnapshotFresh(snapshot, now, snapshotMaxAge))
        {
            return "IO_SNAPSHOT_STALE";
        }

        if (snapshot.Lockers.Count != 8 || snapshot.Lockers.Any(locker => !locker.IsKnown))
        {
            return "IO_STATE_UNKNOWN";
        }

        if (snapshot.Lockers.Any(locker => locker.UnlockOutputRaw is not false))
        {
            return "UNLOCK_OUTPUT_ACTIVE";
        }

        if (snapshot.Lockers.Any(locker => !locker.IsLocked))
        {
            return "SLOT_NOT_LOCKED";
        }

        return null;
    }

    public static bool IsSnapshotFresh(IoSnapshot snapshot, DateTimeOffset now, TimeSpan snapshotMaxAge)
    {
        if (snapshotMaxAge <= TimeSpan.Zero || snapshot.ObservedAt > now)
        {
            return false;
        }

        return now - snapshot.ObservedAt <= snapshotMaxAge;
    }
}
