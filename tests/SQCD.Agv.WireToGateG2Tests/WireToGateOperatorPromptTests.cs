using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 钉住重开循环对操作员的可见性。ADR-cross-0058 决策 1 要求重开不设上限，
/// 而 PublishOperatorEvent 是按 detailKey 去重的：轮次不进键，第二轮之后
/// 操作员一次提示都收不到，现场表现就是一个默默转圈的死循环。
/// </summary>
public sealed class WireToGateOperatorPromptTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public void EveryPromptRoundProducesItsOwnDeduplicationKey()
    {
        string first = WireToGateBusinessService.OperationDetailKey("WAITING_OPERATOR", [1], [], 0);
        string second = WireToGateBusinessService.OperationDetailKey("WAITING_OPERATOR", [1], [], 1);
        string third = WireToGateBusinessService.OperationDetailKey("WAITING_OPERATOR", [1], [], 2);

        Assert.Equal(3, new HashSet<string>([first, second, third], StringComparer.Ordinal).Count);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public void SamePromptRoundKeepsDeduplicatingUnrelatedRepeats()
    {
        Assert.Equal(
            WireToGateBusinessService.OperationDetailKey("PREPARING", [], [], 0),
            WireToGateBusinessService.OperationDetailKey("PREPARING", [], [], 0));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public void ReopenPromptTellsTheOperatorWhatWentWrongAndCountsTheRounds()
    {
        WireToGateSlotOperationCommand command = Command(OperationType.Load, [1, 2]);

        Assert.Equal(
            "正在打开1号仓。",
            WireToGateBusinessService.OperationGuidance(command, "UNLOCKING", [1], [], 0));
        Assert.Equal(
            "1号仓的货物状态与预期不符，正在重新打开。",
            WireToGateBusinessService.OperationGuidance(command, "UNLOCKING", [1], [], 1));
        Assert.Equal(
            "请向1号仓放入货物并关门。",
            WireToGateBusinessService.OperationGuidance(command, "WAITING_OPERATOR", [1], [], 0));
        Assert.Equal(
            "请向1号仓放入货物并关门。（第3次提示）",
            WireToGateBusinessService.OperationGuidance(command, "WAITING_OPERATOR", [1], [], 2));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-04")]
    public void UnloadPromptKeepsItsOwnWordingAcrossRounds()
    {
        WireToGateSlotOperationCommand command = Command(OperationType.Unload, [3]);

        Assert.Equal(
            "请从3号仓取出货物并关门。（第2次提示）",
            WireToGateBusinessService.OperationGuidance(command, "WAITING_OPERATOR", [3], [], 1));
    }

    private static WireToGateSlotOperationCommand Command(
        OperationType operationType,
        IReadOnlyList<int> slots) =>
        new(
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            1,
            new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero),
            "33333333-3333-4333-8333-333333333333",
            "44444444-4444-4444-8444-444444444444",
            "55555555-5555-4555-8555-555555555555",
            operationType,
            slots,
            slots.Count,
            operationType == OperationType.Load,
            new string('0', 64));
}
