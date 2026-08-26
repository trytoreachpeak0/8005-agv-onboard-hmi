namespace SQCD.Agv.Contracts;

public sealed record SublotEntryRequestedPayload(
    string DemandId,
    string OperationSessionId,
    string StationId,
    long WorklistRevision,
    string ExpectedSublot,
    IReadOnlyList<string> EntryMethods,
    bool ExpiresOnRevisionChange);

public sealed record SublotSubmittedPayload(
    string DemandId,
    string OperationSessionId,
    string StationId,
    long WorklistRevision,
    string Sublot,
    string EntryMethod,
    WireToGateOperatorContextPayload Operator);

public sealed record WireToGateOperatorContextPayload(
    string OperatorId,
    string VerificationMethod,
    DateTimeOffset VerifiedAt);

public sealed record SublotRejectedPayload(
    string DemandId,
    string OperationSessionId,
    WireToGateProblemPayload Problem,
    long CurrentWorklistRevision);

public sealed record SlotOperationCommandPayload(
    string DemandId,
    string OperationSessionId,
    string SlotOperationAttemptId,
    string OperationType,
    IReadOnlyList<int> Slots,
    int ExpectedBasketCount,
    string ExpectedFinalPhysicalState,
    string CommandContentSha256);

public sealed record SlotOperationCommandRejectedPayload(
    string SlotOperationAttemptId,
    WireToGateProblemPayload Problem,
    long ObservedCapabilityVersion,
    string? ConflictingContentSha256);

public sealed record SlotOperationResumeCommandPayload(
    string ExceptionRecoverySessionId,
    string RecoveryActionId,
    string DemandId,
    string SlotOperationAttemptId,
    string ProvenRecoveryCheckpoint,
    IReadOnlyList<int> Slots,
    string CommandContentSha256);

public sealed record OperationProgressPayload(
    string SlotOperationAttemptId,
    string Phase,
    IReadOnlyList<int> ActiveUnlockSlots,
    IReadOnlyList<int> CompletedSlots,
    DateTimeOffset ObservedAt);

public sealed record WireToGateSlotResultPayload(
    int SlotNo,
    string Outcome,
    string FinalPhysicalState,
    string LockState,
    string UnlockOutputState,
    IReadOnlyList<string> ReasonCodes);

public sealed record WireToGateOperationResultPayload(
    string DemandId,
    string SlotOperationAttemptId,
    string OperationType,
    string OverallOutcome,
    IReadOnlyList<WireToGateSlotResultPayload> SlotResults,
    DateTimeOffset ObservedAt,
    string JournalCheckpoint,
    string ResultContentSha256);

public sealed record PreDepartureSafetyCheckPayload(
    string PreDepartureSafetyCheckId,
    string DemandId,
    string MovementLegId,
    long ExpectedSafetyStateVersion,
    string TargetStationId);

public sealed record WireToGateSafetySummaryPayload(
    bool DepartureSafe,
    bool VehicleStopped,
    bool AllTargetSlotsLocked,
    bool AllUnlockOutputsReset,
    bool UnknownPresent,
    IReadOnlyList<string> ReasonCodes);

public sealed record PreDepartureSafetyCheckResultPayload(
    string PreDepartureSafetyCheckId,
    string Outcome,
    DateTimeOffset ObservedAt,
    long SafetyStateVersion,
    DateTimeOffset ValidUntil,
    WireToGateSafetySummaryPayload Safety);

public sealed record SafetyStateChangedPayload(
    long SafetyStateVersion,
    DateTimeOffset ObservedAt,
    WireToGateSafetySummaryPayload Safety,
    IReadOnlyList<int> AffectedSlots);

public sealed record WireToGateProblemPayload(
    string ReasonCode,
    string? FieldPath,
    string? DisplayMessage);
