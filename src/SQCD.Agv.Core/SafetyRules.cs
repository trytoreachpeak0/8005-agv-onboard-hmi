using System.Diagnostics;

namespace SQCD.Agv.Core;

public static class SafetyRules
{
    // 是否允许发车
    public static bool IsDeparturePermitted(
        IoSnapshot snapshot,
        ActiveOperation? activeOperation,
        bool hasBlockingFault,
        IClock clock,
        TimeSpan snapshotMaxAge)
    {
        return snapshot.IsConnected
            && activeOperation is null
            && !hasBlockingFault
            && IsSnapshotFresh(snapshot, clock, snapshotMaxAge)
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
        IClock clock,
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

        string? doorSafetyError = ValidateAllDoorsSafe(snapshot, clock, snapshotMaxAge);
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
        IClock clock,
        TimeSpan snapshotMaxAge)
    {
        if (!snapshot.IsConnected)
        {
            return "IO_OFFLINE";
        }

        if (!IsSnapshotFresh(snapshot, clock, snapshotMaxAge))
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

    public static bool IsSnapshotFresh(IoSnapshot snapshot, IClock clock, TimeSpan snapshotMaxAge)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return IsSnapshotFresh(snapshot, clock.Now, clock.GetTimestamp(), snapshotMaxAge);
    }

    /// <summary>
    /// Whether <paramref name="snapshot"/> is no older than <paramref name="snapshotMaxAge"/>, measured on the monotonic
    /// clock (8005-agv-onboard-hmi#404).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reading the IO module took carries its <see cref="IoSnapshot.ObservedTimestamp"/>, and its age is
    /// <paramref name="nowTimestamp"/> less that: the wall clock stepped by workspace#55's hourly <c>Set-Date -Adjust</c>
    /// neither ages it past the maximum nor makes it come "from the future", which on the wall clock left a precheck
    /// refused as <c>SLOT_STATE_UNKNOWN</c> and a safe finish recorded as an active unlock set. A timestamp after
    /// <paramref name="nowTimestamp"/> is a reading published after the caller read its clock, the freshest there is.
    /// </para>
    /// <para>
    /// A snapshot without one -- the unknown snapshot before the first reading, or a test double's -- is measured on the
    /// wall clock as before: <paramref name="now"/> less its <see cref="IoSnapshot.ObservedAt"/>, and stale when that is
    /// in the future.
    /// </para>
    /// </remarks>
    public static bool IsSnapshotFresh(
        IoSnapshot snapshot,
        DateTimeOffset now,
        long nowTimestamp,
        TimeSpan snapshotMaxAge)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshotMaxAge <= TimeSpan.Zero)
        {
            return false;
        }

        if (snapshot.ObservedTimestamp is { } observed)
        {
            return nowTimestamp <= observed || Stopwatch.GetElapsedTime(observed, nowTimestamp) <= snapshotMaxAge;
        }

        if (snapshot.ObservedAt > now)
        {
            return false;
        }

        return now - snapshot.ObservedAt <= snapshotMaxAge;
    }
}
