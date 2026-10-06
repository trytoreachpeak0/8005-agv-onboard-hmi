using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// A new <c>SlotOperationCommand</c> the executor refused before its first journal write and its first unlock pulse,
/// because the journal still names a door a fresh reading does not prove shut (8005-agv-onboard-hmi#267). Nothing
/// physical happened and the journal was not touched.
/// </summary>
/// <remarks>
/// The caller answers it with <c>SlotOperationCommandRejected</c>. The control server only acknowledges such a refusal
/// and sends the same command again each round while the session is ready, so the door being shut is the whole way
/// out: the next copy finds it proven shut and runs.
/// </remarks>
public sealed class WireToGateDoorNotProvenShutException : Exception
{
    public WireToGateDoorNotProvenShutException(
        IReadOnlyList<int> doors,
        bool readingFresh,
        string? recordedSlotOperationAttemptId)
        : base($"DOOR_NOT_PROVEN_SHUT:{string.Join(',', doors)}")
    {
        Doors = doors;
        ReadingFresh = readingFresh;
        RecordedSlotOperationAttemptId = recordedSlotOperationAttemptId;
    }

    /// <summary>The doors of the journal's active unlock set the reading does not prove shut, ascending.</summary>
    public IReadOnlyList<int> Doors { get; }

    /// <summary>Whether the reading was fresh; when it was not, no door could be proven shut.</summary>
    public bool ReadingFresh { get; }

    /// <summary>The unsettled attempt the journal names beside those doors, if any.</summary>
    public string? RecordedSlotOperationAttemptId { get; }
}

/// <summary>
/// A new <c>SlotOperationCommand</c> the executor refused before its first journal write and its first unlock pulse,
/// because a recovery vector is on file that has no result of its own in the outbox yet -- prepared and not run, or
/// running -- or that is a forced mechanical recovery, whose isolation only its acknowledged result records
/// (8005-agv-onboard-hmi#267). Nothing physical happened and the journal was not touched.
/// </summary>
/// <remarks>
/// Answered as <see cref="WireToGateDoorNotProvenShutException"/> is. The vector leaves the journal by its own result's
/// acknowledgement, a maintainer's close-out or the recovery session's CLOSED fallback, and the next copy of the
/// command then runs.
/// </remarks>
public sealed class WireToGateRecoveryVectorUnsettledException : Exception
{
    public WireToGateRecoveryVectorUnsettledException(WireToGateRecoveryVectorContext vector)
        : base($"RECOVERY_VECTOR_UNSETTLED:{vector.VectorType}:{vector.PrimaryId}")
    {
        Vector = vector;
    }

    /// <summary>The vector on file.</summary>
    public WireToGateRecoveryVectorContext Vector { get; }
}

/// <summary>
/// What a new slot operation replaced in the journal when it started (8005-agv-onboard-hmi#267): the earlier attempt and
/// active unlock set, and the recovery vector it settled first -- one whose result was already in the outbox -- told to
/// the caller so the overwrite leaves a trace in the log.
/// </summary>
/// <param name="ClearedRecoveryVector">The vector taken off the journal with its recovery session, or <c>null</c>.</param>
/// <param name="ClearedRecoveryVectorResultKey">The outbox key of that vector's result.</param>
public sealed record WireToGateJournalOverwrite(
    string NewSlotOperationAttemptId,
    string? PreviousSlotOperationAttemptId,
    IReadOnlyList<int> ClearedActiveUnlockSlots,
    WireToGateRecoveryVectorContext? ClearedRecoveryVector,
    string? ClearedRecoveryVectorResultKey);
