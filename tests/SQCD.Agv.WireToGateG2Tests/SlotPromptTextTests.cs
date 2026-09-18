using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// With one door at a time (REQ-0357, ADR-cross-0061) the prompt names the one door the operator is at:
/// 「请在 N 号仓放入／取出货物并关门」 (program#111). Only the prompt line changes; the slot area is not
/// touched.
/// </summary>
public sealed class SlotPromptTextTests
{
    [Theory]
    [InlineData(OperationType.Load, "请在3号仓放入货物并关门。")]
    [InlineData(OperationType.Unload, "请在3号仓取出货物并关门。")]
    public void ALoadOrAnUnloadNamesTheOneDoorTheOperatorIsAt(OperationType operationType, string expected)
    {
        WireToGateSlotOperationCommand command = new(
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            1,
            DateTimeOffset.UtcNow,
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            "33333333-3333-4333-8333-333333333333",
            operationType,
            [2, 3],
            2,
            operationType == OperationType.Load,
            new string('0', 64));

        string first = WireToGateBusinessService.OperationGuidance(
            command,
            new WireToGateOperationProgress("WAITING_OPERATOR", [3], [2]),
            deadlinePassed: false);
        string again = WireToGateBusinessService.OperationGuidance(
            command,
            new WireToGateOperationProgress("WAITING_OPERATOR", [3], [2], 1, WireToGatePromptCause.PromptCadence),
            deadlinePassed: false);

        Assert.Equal(expected, first);
        Assert.Equal(expected + "（第2次提示）", again);
    }

    [Theory]
    [InlineData(WireToGateRecoveryVectorTypes.LoadCancellation)]
    [InlineData(WireToGateRecoveryVectorTypes.LoadCompensation)]
    [InlineData(WireToGateRecoveryVectorTypes.FaultCargoHandoff)]
    public void AClearNamesTheOneDoorToEmpty(string vectorType)
    {
        WireToGateRecoveryVectorContext context = new(
            vectorType,
            "44444444-4444-4444-8444-444444444444",
            null,
            "55555555-5555-4555-8555-555555555555",
            "66666666-6666-4666-8666-666666666666",
            null,
            [2, 3],
            null,
            null,
            null,
            null);

        string prompt = WireToGateBusinessService.RecoveryVectorGuidance(context, "WAITING_OPERATOR", [3], [2]);

        Assert.Equal("请在3号仓取出货物并关门。", prompt);
    }
}
