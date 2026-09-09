namespace SQCD.Agv.Contracts;

/// <summary>
/// 服务端请求操作员录入子批。<see cref="ExpectedSublots"/> 从 protocol 0.2.0 起是集合而不是单个
/// 字符串：FR-001 AC-3 与 BR-001 把可录入范围定义为「本次派车关联的任务集合」，判据是集合归属而
/// 不是与当前站点的距离，而一趟车可以带着几个站点各自的任务。
/// </summary>
public sealed record SublotEntryRequestedPayload(
    string DemandId,
    string OperationSessionId,
    string StationId,
    long WorklistRevision,
    IReadOnlyList<string> ExpectedSublots,
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
    string? SlotOperationAttemptId,
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
    string? SlotOperationAttemptId,
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
    string? SlotOperationAttemptId,
    IReadOnlyList<int> Slots,
    string? SelectedAction,
    IReadOnlyList<string> AllowedActions,
    IReadOnlyList<WireToGateBlockingFactPayload> BlockingFacts);

public sealed record WireToGateProblemPayload(
    string ReasonCode,
    string? FieldPath,
    string? DisplayMessage);

public sealed record LoadCancellationStartRequestedPayload(
    string CancellationId,
    string DemandId,
    string? SlotOperationAttemptId,
    WireToGateOperatorContextPayload Operator,
    string Reason);

public sealed record LoadCancellationAuthorizationPayload(
    string CancellationId,
    string Decision,
    string DemandId,
    string? SlotOperationAttemptId,
    IReadOnlyList<int> Slots,
    WireToGateProblemPayload? Problem);

public sealed record LoadCancellationResultPayload(
    string CancellationId,
    string DemandId,
    string? SlotOperationAttemptId,
    string OverallOutcome,
    IReadOnlyList<WireToGateSlotResultPayload> SlotResults,
    DateTimeOffset ObservedAt);

public sealed record LoadCompensationRequestedPayload(
    string RecoveryActionId,
    string ExceptionRecoverySessionId,
    string DemandId,
    string SlotOperationAttemptId,
    WireToGateOperatorContextPayload Operator);

public sealed record LoadCompensationCommandPayload(
    string RecoveryActionId,
    string ExceptionRecoverySessionId,
    string DemandId,
    string SlotOperationAttemptId,
    IReadOnlyList<int> Slots,
    string ExpectedFinalPhysicalState,
    string CommandContentSha256);

public sealed record LoadCompensationResultPayload(
    string RecoveryActionId,
    string DemandId,
    string SlotOperationAttemptId,
    string OverallOutcome,
    IReadOnlyList<WireToGateSlotResultPayload> SlotResults,
    DateTimeOffset ObservedAt);

public sealed record LoadCorrectionRequestedPayload(
    string CorrectionId,
    string DemandId,
    string SlotOperationAttemptId,
    IReadOnlyList<int> Slots,
    WireToGateOperatorContextPayload Operator,
    string Reason);

public sealed record LoadCorrectionCommandPayload(
    string CorrectionId,
    string DemandId,
    string SlotOperationAttemptId,
    IReadOnlyList<int> Slots,
    IReadOnlyList<string> ExpectedSequence,
    string CommandContentSha256);

public sealed record LoadCorrectionResultPayload(
    string CorrectionId,
    string DemandId,
    string SlotOperationAttemptId,
    string OverallOutcome,
    IReadOnlyList<WireToGateSlotResultPayload> SlotResults,
    DateTimeOffset ObservedAt);

public sealed record FaultCargoRecoveryCommandPayload(
    string ExceptionRecoverySessionId,
    string RecoveryActionId,
    string DemandId,
    IReadOnlyList<int> Slots,
    string HandoffId,
    string CommandContentSha256);

public sealed record FaultCargoRecoveryResultPayload(
    string ExceptionRecoverySessionId,
    string RecoveryActionId,
    string DemandId,
    string HandoffId,
    string OverallOutcome,
    IReadOnlyList<WireToGateSlotResultPayload> SlotResults,
    WireToGateOperatorContextPayload Operator,
    DateTimeOffset ObservedAt);
