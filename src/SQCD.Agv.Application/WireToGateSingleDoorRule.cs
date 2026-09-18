using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// One door at a time across the whole vehicle (REQ-0357, ADR-cross-0061): the check both executors
/// make immediately before an unlock pulse.
/// </summary>
/// <remarks>
/// The two executors each hold their own operation gate and cannot see one another, so each executor
/// keeping its own slots in order is not enough: a door the other one opened -- the case of
/// control-server#128, an executor opening a slot after a cancellation already took the attempt over
/// -- would be invisible to it. The IO is the one thing both see. Maintenance door mode (REQ-0226) is
/// the exception the rule names, and it does not pulse through these executors.
/// </remarks>
internal static class WireToGateSingleDoorRule
{
    /// <summary>
    /// Why <paramref name="physicalSlot"/> may not be unlocked now, or null when every other slot of the
    /// vehicle reads locked with its unlock output reset. A slot that cannot be read -- or a snapshot
    /// that cannot be trusted -- counts as not shut, since a door not proven shut may be open.
    /// </summary>
    public static string? OtherDoorNotShut(IoSnapshot snapshot, bool fresh, int physicalSlot)
    {
        if (!fresh)
        {
            return "SLOT_STATE_UNKNOWN";
        }

        for (int other = 1; other <= 8; other++)
        {
            if (other == physicalSlot)
            {
                continue;
            }

            LockerSnapshot? locker = snapshot.Lockers.FirstOrDefault(item => item.PhysicalNumber == other);
            if (locker is null || !locker.IsKnown)
            {
                return "SLOT_STATE_UNKNOWN";
            }

            if (locker.UnlockOutputRaw is not false)
            {
                return "UNLOCK_OUTPUT_NOT_RESET";
            }

            if (!locker.IsLocked)
            {
                return "LOCK_NOT_CLOSED";
            }
        }

        return null;
    }
}
