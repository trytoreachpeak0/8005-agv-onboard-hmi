namespace SQCD.Agv.Contracts;

/// <summary>
/// The C_TO_O sublot entry request, shaped by the 2.0.0 candidate's
/// <c>SublotEntryRequested.schema.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>demandId</c> and <c>expectedSublot</c> are gone; <c>expectedSublots</c> replaces both.</b>
/// The vehicle never binds a demand -- <c>FP-IS-01</c> gives this end
/// <c>NEVER_DISCOVER_SELECT_OR_BIND_DEMAND</c> -- so a demand id here was a value it could only
/// echo. What it checks instead is membership: the operator's entry has to be one of the 1 to 8
/// unique sublots the server says this stop expects. A sublot outside that set is the control
/// server's verdict to give, as <c>SUBLOT_NOT_IN_DISPATCH_SCOPE</c>, not this end's to guess.
/// </para>
/// <para>
/// A one-demand dispatch produces a single-element set, but nothing here may assume that: parsing
/// and checking are written over the set.
/// </para>
/// </remarks>
public sealed record SublotEntryRequestedPayload(
    string OperationSessionId,
    string StationId,
    long WorklistRevision,
    IReadOnlyList<string> ExpectedSublots,
    IReadOnlyList<string> EntryMethods,
    bool ExpiresOnRevisionChange);

/// <summary>
/// The O_TO_C sublot submission, shaped by the 2.0.0 candidate's
/// <c>SublotSubmitted.schema.json</c>.
/// </summary>
/// <remarks>
/// <b>No <c>demandId</c>.</b> The control server resolves the demand from the entered sublot within
/// the current dispatch scope (<c>8005-agv-control-server#82</c>). The payload object is
/// <c>additionalProperties: false</c>, so carrying one would make every submission schema-invalid.
/// </remarks>
public sealed record SublotSubmittedPayload(
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

/// <summary>
/// The C_TO_O rejection of a sublot submission, shaped by the 2.0.0 candidate's
/// <c>SublotRejected.schema.json</c>.
/// </summary>
/// <remarks>
/// <b><c>DemandId</c> became nullable and <c>RejectedSublot</c> is new; both are required.</b> The
/// two go together: a sublot outside the current dispatch scope is refused with
/// <c>SUBLOT_NOT_IN_DISPATCH_SCOPE</c>, and there is by definition no demand to name it against, so
/// the message identifies what was refused by the sublot itself. <c>correlationId</c> stays
/// required on the envelope -- a rejection nothing can be matched to is refused on arrival.
/// </remarks>
public sealed record SublotRejectedPayload(
    string? DemandId,
    string OperationSessionId,
    WireToGateProblemPayload Problem,
    long CurrentWorklistRevision,
    string RejectedSublot);

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

/// <summary>
/// The v3 check: <see cref="CheckPurpose"/> is <c>DEPARTURE</c>, <c>NON_BUSINESS_MOVE</c> or
/// <c>HOLD_RELEASE</c>, and the schema's three <c>if/then</c> clauses pin which of the other three ids
/// are null for each purpose (8005-agv-onboard-hmi#214).
/// </summary>
public sealed record PreDepartureSafetyCheckPayload(
    string PreDepartureSafetyCheckId,
    string CheckPurpose,
    string? DemandId,
    string? MovementLegId,
    long ExpectedSafetyStateVersion,
    string? TargetStationId);

public sealed record WireToGateSafetySummaryPayload(
    bool DepartureSafe,
    bool VehicleStopped,
    bool AllTargetSlotsLocked,
    bool AllUnlockOutputsReset,
    bool UnknownPresent,
    IReadOnlyList<string> ReasonCodes);

public sealed record PreDepartureSafetyCheckResultPayload(
    string PreDepartureSafetyCheckId,
    string CheckPurpose,
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

/// <summary>
/// The C_TO_O acknowledgement that a recovery session is open, shaped by the 2.0.0 candidate's
/// <c>ExceptionRecoverySessionOpened.schema.json</c>.
/// </summary>
/// <remarks>
/// <b><c>SlotOperationAttemptId</c> is new in 2.0.0: required, and nullable.</b> It is the server
/// naming which slot operation attempt this recovery is about. <c>null</c> says the session has no
/// slot operation attached -- a recovery opened before any loading began -- and is a value this end
/// has to distinguish from a name, because a name is authoritative and an absence is not. See
/// <c>WireToGateBusinessService.RecoveryVectors</c> for what the vehicle does with each.
/// </remarks>
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

/// <summary>
/// The C_TO_O acceptance of a recovery action, shaped by the 2.0.0 candidate's
/// <c>RecoveryActionAccepted.schema.json</c>.
/// </summary>
/// <remarks>
/// <b><c>SlotOperationAttemptId</c> is new in 2.0.0: required, and nullable.</b> The three messages
/// of one recovery session are expected to name the same attempt; a disagreement between them is a
/// defect rather than a scope this end should follow.
/// </remarks>
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

/// <summary>
/// The O_TO_C hardware recovery record, shaped by 2.0.0's
/// <c>HardwareRecoveryRecordSubmitted.schema.json</c>: the device half of a forced mechanical recovery
/// (ADR-cross-0036, REQ-0242). The slot set is the forced recovery's whole set; the server records it
/// only when it equals the session's.
/// </summary>
public sealed record HardwareRecoveryRecordSubmittedPayload(
    string RecordId,
    string ExceptionRecoverySessionId,
    string RecoveryActionId,
    WireToGateOperatorContextPayload Operator,
    string AdministratorRole,
    IReadOnlyList<int> Slots,
    IReadOnlyList<string> ChecksPerformed,
    IReadOnlyList<string> ActionsPerformed,
    IReadOnlyList<string> Observations,
    DateTimeOffset ObservedAt);

/// <summary>
/// The C_TO_O answer to <see cref="HardwareRecoveryRecordSubmittedPayload"/>, a RESPONSE correlated to
/// the request's messageId.
/// </summary>
public sealed record HardwareRecoveryRecordResultPayload(
    string RecordId,
    string Outcome,
    WireToGateProblemPayload? Problem,
    long RecoverySessionRevision);

public sealed record ManualChargingReturnToServiceResultPayload(
    string RequestId,
    string Outcome,
    WireToGateProblemPayload? Problem,
    long VehicleBusinessStateRevision);

/// <summary>
/// The O_TO_C manual station clearance confirmation, shaped by 2.0.0's
/// <c>ManualStationClearanceConfirmationRequested.schema.json</c> (batch 9-16,
/// <c>8005-agv-onboard-hmi#221</c>; <c>REQ-0179</c>).
/// </summary>
/// <remarks>
/// <b><c>PublicStationFunction</c> is required and nullable.</b> Its enumeration has no value for a
/// charger, so a charger clearance sends <c>null</c> -- on the wire as <c>null</c>, never left out.
/// There is no role field: who may confirm is the control server's to decide from
/// <see cref="WireToGateOperatorContextPayload.OperatorId"/>.
/// </remarks>
public sealed record ManualStationClearanceConfirmationRequestedPayload(
    string ConfirmationRequestId,
    string StationId,
    string? PublicStationFunction,
    string ClearedCondition,
    WireToGateOperatorContextPayload Operator,
    DateTimeOffset ObservedAt);

/// <summary>
/// The C_TO_O answer to <see cref="ManualStationClearanceConfirmationRequestedPayload"/>, a RESPONSE
/// correlated to the request's messageId. <c>StationReleased</c> is the server's fact; nothing on the
/// vehicle is released for it (<c>NEVER_RELEASE_STATION_LOCALLY</c>).
/// </summary>
public sealed record ManualStationClearanceConfirmationResultPayload(
    string ConfirmationRequestId,
    string Outcome,
    WireToGateProblemPayload? Problem,
    bool StationReleased);

/// <summary>
/// The O_TO_C field confirmation that a vehicle could not charge, shaped by 2.0.0's
/// <c>UnableToChargeFieldConfirmationRequested.schema.json</c> (batch 9-17, <c>8005-agv-onboard-hmi#222</c>;
/// <c>REQ-0176</c>).
/// </summary>
/// <remarks>
/// <c>ObservedCondition</c> is what the operator saw, sent as chosen from the schema's four values; whether it
/// is enough to confirm that charging failed is the control server's to judge (<c>REQ-0175</c>). There is no
/// role field: whether this is a confirmation or a report is decided by the server from
/// <see cref="WireToGateOperatorContextPayload.OperatorId"/>.
/// </remarks>
public sealed record UnableToChargeFieldConfirmationRequestedPayload(
    string ConfirmationRequestId,
    string ChargerStationId,
    string ObservedCondition,
    WireToGateOperatorContextPayload Operator,
    DateTimeOffset ObservedAt);

/// <summary>
/// The C_TO_O answer to <see cref="UnableToChargeFieldConfirmationRequestedPayload"/>, a RESPONSE correlated to
/// the request's messageId. <c>ChargingPolicyDecision</c> is the server's decision, shown and never acted on here
/// (<c>NEVER_DECIDE_CHARGING_POLICY_LOCALLY</c>).
/// </summary>
public sealed record UnableToChargeFieldConfirmationResultPayload(
    string ConfirmationRequestId,
    string Outcome,
    WireToGateProblemPayload? Problem,
    string? ChargingPolicyDecision);

/// <summary>
/// The C_TO_O recovery session snapshot, shaped by the 2.0.0 candidate's
/// <c>ExceptionRecoverySessionSnapshot.schema.json</c>.
/// </summary>
/// <remarks>
/// <b><c>SlotOperationAttemptId</c> is new in 2.0.0: required, and nullable.</b> Same field, same
/// meaning, as on the other two recovery messages.
/// <para>
/// <c>ClosedReason</c> is new in 3.0.0: required and nullable, an error code only when
/// <c>state</c> is <c>CLOSED</c> (8005-agv-onboard-hmi#214). It is received and kept on the record;
/// showing it is 8005-agv-onboard-hmi#216.
/// </para>
/// </remarks>
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
    IReadOnlyList<WireToGateBlockingFactPayload> BlockingFacts,
    string? ClosedReason = null);

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

public sealed record ForcedMechanicalRecoveryCommandPayload(
    string ExceptionRecoverySessionId,
    string RecoveryActionId,
    string? DemandId,
    long ForcedRecoveryGeneration,
    IReadOnlyList<int> Slots,
    string CommandContentSha256);

/// <summary>
/// The result half of <c>CV-FORCED-MECHANICAL-RECOVERY</c>.  Three fields differ from every other
/// recovery result and none of the differences is incidental.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DemandId"/> and <see cref="CargoHandoff"/> are new in 3.0.0, both required and
/// nullable: the result copies the command's demand, and an isolation on a demand names the cargo
/// handoff (<c>CV-FORCED-MECHANICAL-RECOVERY</c>, 8005-agv-onboard-hmi#214). There is no per-slot
/// result array -- a forced mechanical recovery is a human
/// prying a locker open, and the vehicle has no trustworthy electronic reading of what happened
/// inside it, so the message reports only which slots were in scope.
/// </para>
/// <para>
/// <see cref="ElectronicEmptyProven"/> and <see cref="VehicleReadyProven"/> are
/// <c>{"const": false}</c> in the schema.  The protocol refuses to let this message claim either
/// proof, which is what keeps <c>ready-before-reconciliation</c> and <c>unknown-as-success</c> off
/// the table; they are written as constants at the send site rather than computed from state,
/// because a state that could ever compute <c>true</c> here would be a bug the schema would then
/// have to catch on the wire.
/// </para>
/// </remarks>
public sealed record ForcedMechanicalRecoveryResultPayload(
    string ExceptionRecoverySessionId,
    string RecoveryActionId,
    long ForcedRecoveryGeneration,
    string Outcome,
    IReadOnlyList<int> Slots,
    WireToGateOperatorContextPayload Operator,
    DateTimeOffset ObservedAt,
    bool ElectronicEmptyProven,
    bool VehicleReadyProven,
    string? DemandId,
    WireToGateCargoHandoffPayload? CargoHandoff);

/// <summary>
/// The cargo handoff an isolation on a demand records: which sublot went to whom, and when.
/// </summary>
public sealed record WireToGateCargoHandoffPayload(
    string Sublot,
    string ReceiverName,
    DateTimeOffset HandedOverAt);

/// <summary>
/// The C_TO_O administrator's slot fault declaration, shaped by the 3.0.0 candidate's
/// <c>SlotFaultDeclarationCommand.schema.json</c> (REQ-0359, 8005-agv-onboard-hmi#215).
/// </summary>
public sealed record SlotFaultDeclarationCommandPayload(
    string DeclarationId,
    string DemandId,
    string SlotOperationAttemptId,
    int SlotNo,
    WireToGateOperatorContextPayload Administrator,
    string AdministratorRole,
    string FaultCategory,
    string Note,
    DateTimeOffset DeclaredAt);

/// <summary>
/// The O_TO_C answer to a slot fault declaration. <see cref="Problem"/> is required and nullable: it says why
/// a <c>NOT_APPLICABLE</c> declaration was refused, and is <c>null</c> for <c>APPLIED</c>.
/// </summary>
public sealed record SlotFaultDeclarationResultPayload(
    string DeclarationId,
    string SlotOperationAttemptId,
    string Outcome,
    WireToGateProblemPayload? Problem);
