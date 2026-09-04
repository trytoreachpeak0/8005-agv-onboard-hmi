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

public sealed record ExceptionRecoverySessionRequestedPayload(
    string RequestId,
    WireToGateOperatorContextPayload Administrator,
    string AdministratorRole,
    string EventId,
    string? DemandId,
    IReadOnlyList<int> Slots,
    string Reason,
    string AuthenticationProof);

public sealed record ExceptionRecoverySessionOpenedPayload(
    string RequestId,
    string ExceptionRecoverySessionId,
    DateTimeOffset OpenedAt,
    string EventId,
    string? DemandId,
    IReadOnlyList<int> Slots,
    long RecoverySessionRevision);

public sealed record RecoveryActionSubmittedPayload(
    string RecoveryActionId,
    string ExceptionRecoverySessionId,
    string Action,
    string EventId,
    string? DemandId,
    IReadOnlyList<int> Slots,
    WireToGateOperatorContextPayload Operator,
    string Reason);

public sealed record RecoveryActionAcceptedPayload(
    string RecoveryActionId,
    string ExceptionRecoverySessionId,
    string AcceptedAction,
    long RecoverySessionRevision,
    DateTimeOffset AcceptedAt);

public sealed record ManualChargingReturnToServiceRequestedPayload(
    string RequestId,
    WireToGateOperatorContextPayload Administrator,
    string AdministratorRole,
    string Reason,
    double? ObservedBatteryPercent);

public sealed record ManualChargingReturnToServiceResultPayload(
    string RequestId,
    string Outcome,
    WireToGateProblemPayload? Problem,
    long VehicleBusinessStateRevision);

public sealed record ExceptionRecoverySessionSnapshotPayload(
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
    IReadOnlyList<WireToGateBlockingFactPayload> BlockingFacts);

public sealed record WireToGateProblemPayload(
    string ReasonCode,
    string? FieldPath,
    string? DisplayMessage);
