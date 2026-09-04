namespace SQCD.Agv.Core;

public abstract record WireToGateServerCommand(
    string MessageType,
    string MessageId,
    string? CorrelationId,
    long SessionGeneration,
    DateTimeOffset SentAt);

public sealed record WireToGateSublotEntryRequest(
    string MessageId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string DemandId,
    string OperationSessionId,
    string StationId,
    long WorklistRevision,
    string ExpectedSublot,
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

public sealed record WireToGateRecoveryBlockingFact(
    string ReasonCode,
    string SubjectType,
    string? SubjectId);

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
