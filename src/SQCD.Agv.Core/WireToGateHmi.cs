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
public sealed record WireToGateHmiOperationSnapshot(
    string SlotOperationAttemptId,
    OperationType OperationType,
    IReadOnlyList<int> Slots,
    WireToGateHmiOperationStage Stage,
    string Guidance,
    DateTimeOffset ObservedAt);

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
