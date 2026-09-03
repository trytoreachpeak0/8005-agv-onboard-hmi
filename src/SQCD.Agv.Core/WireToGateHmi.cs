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
