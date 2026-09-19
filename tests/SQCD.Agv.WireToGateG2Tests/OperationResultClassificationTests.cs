using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// HMI 怎样读一份 FAILED，要与服务端怎样结算它同一个口径（8005-agv-control-server#170、
/// 8005-agv-onboard-hmi#116）：只有装货、而且没有一个仓位读到有货，才是「没人交货」的确定失败，
/// 操作员什么都不用做；有货留在车上，就要停下来等管理员。
/// </summary>
public sealed class OperationResultClassificationTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public void AnEmptyLockedLoadFailureIsDeterminate()
    {
        WireToGateOperationExecutionResult result = Result(
            OperationType.Load,
            Slot(3, "FAILED", "EMPTY", "OPERATOR_TIMEOUT"),
            Slot(4, "NOT_STARTED", "EMPTY"));

        Assert.True(WireToGateBusinessService.IsDeterminateFailure(result));
        Assert.Empty(WireToGateBusinessService.ConflictSlots(result));
    }

    /// <summary>2026-09-19 09:23 agv01 那一单的形状：3、4 号仓装上了，5 号仓空关到期限。</summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public void APartlyLoadedFailureIsNotDeterminate()
    {
        WireToGateOperationExecutionResult result = Result(
            OperationType.Load,
            Slot(3, "COMPLETED", "OCCUPIED"),
            Slot(4, "COMPLETED", "OCCUPIED"),
            Slot(5, "FAILED", "EMPTY", "OPERATOR_TIMEOUT"));

        Assert.False(WireToGateBusinessService.IsDeterminateFailure(result));
        Assert.Empty(WireToGateBusinessService.ConflictSlots(result));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public void ALoadRefusedOverAnOccupiedSlotIsNotDeterminateAndNamesTheSlot()
    {
        WireToGateOperationExecutionResult result = Result(
            OperationType.Load,
            Slot(3, "NOT_STARTED", "OCCUPIED", "SLOT_OPERATION_CONFLICT"),
            Slot(4, "NOT_STARTED", "EMPTY"));

        Assert.False(WireToGateBusinessService.IsDeterminateFailure(result));
        Assert.Equal([3], WireToGateBusinessService.ConflictSlots(result));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-04")]
    public void AnUnloadFailureIsNeverDeterminate()
    {
        WireToGateOperationExecutionResult result = Result(
            OperationType.Unload,
            Slot(1, "NOT_STARTED", "EMPTY", "SLOT_OPERATION_CONFLICT"));

        Assert.False(WireToGateBusinessService.IsDeterminateFailure(result));
        Assert.Equal([1], WireToGateBusinessService.ConflictSlots(result));
    }

    private static WireToGateSlotExecutionResult Slot(
        int slotNo,
        string outcome,
        string physical,
        params string[] reasons) =>
        new(slotNo, outcome, physical, "LOCKED", "RESET", reasons);

    private static WireToGateOperationExecutionResult Result(
        OperationType operationType,
        params WireToGateSlotExecutionResult[] slots) =>
        new(
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            operationType,
            "FAILED",
            slots,
            DateTimeOffset.UtcNow,
            "SAFE_FINISH_REACHED",
            string.Empty);
}
