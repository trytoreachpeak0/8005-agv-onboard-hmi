namespace SQCD.Agv.Core;

public enum WireToGateHmiOperationStage
{
    Preparing,
    Unlocking,
    WaitingOperator,
    Verifying,
    Reporting,
    Completed,
    RecoveryRequired,

    /// <summary>
    /// 本站期限过了，货物始终没有交接。ADR-cross-0058 决策 5 的确定失败：现场没有任何
    /// 一件事是不确定的，所以它不是 <see cref="RecoveryRequired"/>——不需要管理员，
    /// 需要的是操作员在 HMI 上取消本次装货（ADR-cross-0015、ADR-cross-0046）。
    /// </summary>
    StationDeadlineExpired
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
        // 门已闭、开锁输出已复位，车辆这一侧安全地结束了——与达成目标态时同一个 phase。
        // 失败是业务结论，由 OperationResult 承载，不是进度里的一个阶段。
        WireToGateHmiOperationStage.StationDeadlineExpired => "SAFE_FINISH",
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
