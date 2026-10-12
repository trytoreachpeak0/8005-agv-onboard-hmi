using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf.ScreenState;

/// <summary>
/// What the banner and the entries were computed from (T1b of <c>docs/W2G_HMI_OBSERVABILITY_RESEARCH.md</c>,
/// onboard-hmi#314): the controller's state, the inputs of <c>WireToGateHmiPresentation.Create</c>, each recovery entry's
/// business value beside its final value, and the safety state the server accepted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same build, same values.</b> Everything but <see cref="AcceptedSafety"/> is written on the dispatcher thread in
/// the same work items that write the values it explains, and the builder reads it there too, so the business value and
/// the final value of an entry come from one refresh. <see cref="AcceptedSafety"/> lives in the session client and is
/// written on its threads: it is read as one record, so its version and summary always belong together, but it is not
/// of the same instant as the rest; its <see cref="HmiAcceptedSafetyState.SessionGeneration"/> says which session.
/// </para>
/// <para>
/// <b>Content boundary.</b> No guidance line: the controller's names the sublot being verified
/// (<c>OnboardController</c>, <c>正在确认物料条码 {sublot}</c>), and the slot operation's can too (research section 4.1,
/// <c>HmiScreenStateContentBoundaryArchitectureTests</c>); only whether one is there. The sublot rejection is only
/// whether there is one, as <c>Create</c> takes it. No timestamp that moves with every IO reading, so the content
/// revision does not either.
/// </para>
/// </remarks>
/// <param name="RecoveryEntriesLatched">The latch gate's own criterion (the controller is <c>Faulted</c>).</param>
public sealed record HmiScreenBasisState(
    HmiControllerState? Controller,
    bool RecoveryEntriesLatched,
    HmiPresentationInputsState? Presentation,
    IReadOnlyList<HmiRecoveryEntryState> RecoveryEntries,
    HmiAcceptedSafetyState? AcceptedSafety);

/// <summary>The controller snapshot the banner and the latch gate last read; null before the first.</summary>
public sealed record HmiControllerState(
    OnboardState State,
    string? ErrorCode,
    bool HasControllerGuidance,
    bool DeparturePermitted,
    bool RuleConnected,
    bool IoConnected);

/// <summary>The inputs <c>WireToGateHmiPresentation.Create</c> was last called with; null before the first call.</summary>
public sealed record HmiPresentationInputsState(
    bool SessionConnected,
    long? SessionGeneration,
    WireToGateSessionReadiness Readiness,
    IReadOnlyList<string> ReasonCodes,
    HmiSlotOperationState? Operation,
    bool CanSubmit,
    bool LoadCancellationPending,
    bool SublotRejected,
    bool EntryPausedUntilStopped);

/// <summary>The slot operation in progress, as <c>Create</c> read it.</summary>
public sealed record HmiSlotOperationState(
    string SlotOperationAttemptId,
    OperationType OperationType,
    IReadOnlyList<int> Slots,
    WireToGateHmiOperationStage Stage,
    bool HasOperationGuidance);

/// <summary>
/// One recovery entry: what the business offered, read in the refresh that wrote <see cref="Final"/>, and the final
/// value. Where the two differ the latch gate closed it.
/// </summary>
/// <param name="Entry">The view-model property holding <see cref="Final"/>, as XAML and the button table name it.</param>
/// <param name="OfferedByBusiness">Null before the entry's first refresh.</param>
/// <param name="OfferPath">The refresh path that read <see cref="OfferedByBusiness"/>.</param>
/// <param name="OfferedBeforeAnySublot">Load cancellation only: its "before any sublot" half, which the latch leaves open.</param>
public sealed record HmiRecoveryEntryState(
    string Entry,
    bool? OfferedByBusiness,
    string? OfferPath,
    bool? OfferedBeforeAnySublot,
    bool Final);

/// <summary>
/// The safety state the control server accepted (<c>WireToGateSessionClient.AcceptedSafety</c>): the version, and the
/// summary sent under it when this client knows it (<see cref="SafetyKnown"/>), never another version's.
/// </summary>
public sealed record HmiAcceptedSafetyState(
    long SafetyStateVersion,
    long? SessionGeneration,
    WireToGateAcceptedSafetySource Source,
    bool SafetyKnown,
    bool? DepartureSafe,
    bool? VehicleStopped,
    bool? AllTargetSlotsLocked,
    bool? AllUnlockOutputsReset,
    bool? UnknownPresent,
    IReadOnlyList<string>? ReasonCodes);
