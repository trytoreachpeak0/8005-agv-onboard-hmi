namespace SQCD.Agv.Core;

public enum WireToGateHmiOperationStage
{
    Preparing,
    Unlocking,
    WaitingOperator,
    Verifying,
    Reporting,
    Completed,
    RecoveryRequired
}

public static class WireToGateHmiOperationStageExtensions
{
    /// <summary>
    /// Maps the HMI presentation stage to the formal OperationProgress phase
    /// vocabulary. Completed is intentionally not a wire progress phase.
    /// </summary>
    public static string? ToProtocolPhase(this WireToGateHmiOperationStage stage) => stage switch
    {
        WireToGateHmiOperationStage.Preparing => "PREPARING",
        WireToGateHmiOperationStage.Unlocking => "UNLOCKING",
        WireToGateHmiOperationStage.WaitingOperator => "WAITING_OPERATOR",
        WireToGateHmiOperationStage.Verifying => "VERIFYING",
        WireToGateHmiOperationStage.Reporting => "SAFE_FINISH",
        WireToGateHmiOperationStage.RecoveryRequired => "PAUSED",
        WireToGateHmiOperationStage.Completed => null,
        _ => null
    };
}

/// <summary>
/// Read-only projection of a formal WIRE_TO_GATE slot operation for the HMI.
/// It never grants authority to perform IO; the server command and the physical
/// executor remain the only sources of business and hardware authority.
/// </summary>
/// <param name="IsRecoveryVector">
/// The doors are a recovery vector's (compensation, correction, cancellation, fault cargo handoff, forced mechanical
/// recovery), not an ordinary load or unload's. Its <see cref="OperationType"/> is only a stand-in for those -- a
/// correction reads as Load although it first waits for the slot to be emptied -- so a reader that words the operator's
/// next step from the type must not take this one at its word (onboard-hmi#354 review M1).
/// </param>
public sealed record WireToGateHmiOperationSnapshot(
    string SlotOperationAttemptId,
    OperationType OperationType,
    IReadOnlyList<int> Slots,
    WireToGateHmiOperationStage Stage,
    string Guidance,
    DateTimeOffset ObservedAt,
    bool IsRecoveryVector = false);

public sealed record WireToGateOperatorEvent(
    DateTimeOffset Timestamp,
    string Kind,
    string Message,
    WireToGateHmiOperationSnapshot? Operation = null);

/// <summary>
/// The server's latest refusal of an entered sublot, held for the prompt area until the operator
/// enters again or the stop moves on (8005-agv-onboard-hmi#77).
/// </summary>
/// <remarks>
/// <see cref="DemandId"/> is <c>null</c> for a sublot outside the dispatch scope: there is no demand
/// to name it against, and that is exactly the rejection the operator most needs to see.
/// <see cref="EntryRequestKept"/> records whether the vehicle kept its entry request -- it does when
/// the rejection names the worklist revision that request was made at.
/// </remarks>
public sealed record WireToGateSublotRejection(
    string MessageId,
    string? DemandId,
    string OperationSessionId,
    string ReasonCode,
    long CurrentWorklistRevision,
    string RejectedSublot,
    bool EntryRequestKept,
    DateTimeOffset ReceivedAt);
