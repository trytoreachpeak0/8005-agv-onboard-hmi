namespace SQCD.Agv.Core;

public abstract record WireToGateServerCommand(
    string MessageType,
    string MessageId,
    string? CorrelationId,
    long SessionGeneration,
    DateTimeOffset SentAt);

/// <summary>
/// The server's request for an operator sublot entry.
/// </summary>
/// <remarks>
/// <see cref="ExpectedSublots"/> replaced protocol 1.0.0's single <c>ExpectedSublot</c> and its
/// <c>DemandId</c>. The entry is checked for membership in this set, and the vehicle binds no
/// demand at all.
/// </remarks>
public sealed record WireToGateSublotEntryRequest(
    string MessageId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string OperationSessionId,
    string StationId,
    long WorklistRevision,
    IReadOnlyList<string> ExpectedSublots,
    IReadOnlyList<string> EntryMethods,
    bool ExpiresOnRevisionChange)
    : WireToGateServerCommand("SublotEntryRequested", MessageId, null, SessionGeneration, SentAt);

public sealed record WireToGateSlotOperationCommand(
    string MessageId,
    string? CorrelationId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string DemandId,
    string OperationSessionId,
    string SlotOperationAttemptId,
    OperationType OperationType,
    IReadOnlyList<int> Slots,
    int ExpectedBasketCount,
    bool ExpectedOccupied,
    string CommandContentSha256)
    : WireToGateServerCommand("SlotOperationCommand", MessageId, CorrelationId, SessionGeneration, SentAt);

public sealed record WireToGateSlotOperationResumeCommand(
    string MessageId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string ExceptionRecoverySessionId,
    string RecoveryActionId,
    string DemandId,
    string SlotOperationAttemptId,
    WireToGateRecoveryCheckpoint ProvenRecoveryCheckpoint,
    IReadOnlyList<int> Slots,
    string CommandContentSha256)
    : WireToGateServerCommand("SlotOperationResumeCommand", MessageId, null, SessionGeneration, SentAt);

public sealed record WireToGatePreDepartureSafetyCheck(
    string MessageId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string PreDepartureSafetyCheckId,
    string DemandId,
    string MovementLegId,
    long ExpectedSafetyStateVersion,
    string TargetStationId)
    : WireToGateServerCommand("PreDepartureSafetyCheck", MessageId, null, SessionGeneration, SentAt);

public sealed record WireToGateRecoveryCommand(
    string MessageType,
    string MessageId,
    string? CorrelationId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string PayloadJson)
    : WireToGateServerCommand(MessageType, MessageId, CorrelationId, SessionGeneration, SentAt);

public sealed record WireToGateLoadCompensationCommand(
    string MessageId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string RecoveryActionId,
    string ExceptionRecoverySessionId,
    string DemandId,
    string SlotOperationAttemptId,
    IReadOnlyList<int> Slots,
    string ExpectedFinalPhysicalState,
    string CommandContentSha256)
    : WireToGateServerCommand(
        "LoadCompensationCommand",
        MessageId,
        null,
        SessionGeneration,
        SentAt);

public sealed record WireToGateLoadCorrectionCommand(
    string MessageId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string CorrectionId,
    string DemandId,
    string SlotOperationAttemptId,
    IReadOnlyList<int> Slots,
    IReadOnlyList<string> ExpectedSequence,
    string CommandContentSha256)
    : WireToGateServerCommand(
        "LoadCorrectionCommand",
        MessageId,
        null,
        SessionGeneration,
        SentAt);

public sealed record WireToGateFaultCargoRecoveryCommand(
    string MessageId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string ExceptionRecoverySessionId,
    string RecoveryActionId,
    string DemandId,
    IReadOnlyList<int> Slots,
    string HandoffId,
    string CommandContentSha256)
    : WireToGateServerCommand(
        "FaultCargoRecoveryCommand",
        MessageId,
        null,
        SessionGeneration,
        SentAt);

/// <summary>
/// The command half of <c>CV-FORCED-MECHANICAL-RECOVERY</c>.  Unlike the other four recovery
/// vectors it carries a <see cref="ForcedRecoveryGeneration"/>: the control server bumps that
/// number when it authorizes a forced recovery and fences everything it issued under an older one,
/// so the onboard must refuse a command that arrives carrying a generation it has already moved
/// past.
/// </summary>
public sealed record WireToGateForcedMechanicalRecoveryCommand(
    string MessageId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string ExceptionRecoverySessionId,
    string RecoveryActionId,
    string? DemandId,
    long ForcedRecoveryGeneration,
    IReadOnlyList<int> Slots,
    string CommandContentSha256)
    : WireToGateServerCommand(
        "ForcedMechanicalRecoveryCommand",
        MessageId,
        null,
        SessionGeneration,
        SentAt);

public sealed record WireToGateRecoveryBlockingFact(
    string ReasonCode,
    string SubjectType,
    string? SubjectId);

/// <summary>
/// The server's view of an open recovery session.
/// </summary>
/// <remarks>
/// <see cref="SlotOperationAttemptId"/> arrived with protocol 2.0.0 and is the server naming which
/// slot operation attempt this recovery is about. <c>null</c> means the session has none attached.
/// </remarks>
public sealed record WireToGateExceptionRecoverySessionSnapshot(
    string MessageId,
    string? CorrelationId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string ExceptionRecoverySessionId,
    long RecoverySessionRevision,
    string State,
    string AdministratorId,
    string AdministratorRole,
    string EventId,
    string? DemandId,
    string? SlotOperationAttemptId,
    IReadOnlyList<int> Slots,
    string? SelectedAction,
    IReadOnlyList<string> AllowedActions,
    IReadOnlyList<WireToGateRecoveryBlockingFact> BlockingFacts)
    : WireToGateServerCommand(
        "ExceptionRecoverySessionSnapshot",
        MessageId,
        CorrelationId,
        SessionGeneration,
        SentAt);

public sealed record WireToGateSlotExecutionResult(
    int SlotNo,
    string Outcome,
    string FinalPhysicalState,
    string LockState,
    string UnlockOutputState,
    IReadOnlyList<string> ReasonCodes);

public sealed record WireToGateOperationExecutionResult(
    string DemandId,
    string SlotOperationAttemptId,
    OperationType OperationType,
    string OverallOutcome,
    IReadOnlyList<WireToGateSlotExecutionResult> SlotResults,
    DateTimeOffset ObservedAt,
    string JournalCheckpoint,
    string ResultContentSha256);
