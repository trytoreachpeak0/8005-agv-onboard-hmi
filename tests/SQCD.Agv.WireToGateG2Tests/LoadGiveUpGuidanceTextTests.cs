using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The prompt names only a way to give the load up that the screen offers then (8005-agv-onboard-hmi#392, comment
/// acceptance 5): 10-22 agv01 was told 「不装了请按取消」 with the entry not shown, and once loading has started a cancellation is
/// only for a load with no basket in and every door shut (CP-0021 revised <c>REQ-0211</c>).
/// </summary>
/// <remarks>
/// <c>null</c> for the caption is a load the cancellation entry is not lit over at all: its result is already with the server,
/// a recovery vector runs it (the 10-22 RESUME_AFTER_REPAIR), or no operator is signed in. With a basket in, the way to load
/// fewer is 「剩下的不装」; with none in and the door open, the door is shut first.
/// </remarks>
public sealed class LoadGiveUpGuidanceTextTests
{
    private static readonly WireToGateSlotOperationCommand Load = new(
        "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
        "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
        1,
        DateTimeOffset.UtcNow,
        "11111111-1111-4111-8111-111111111111",
        "22222222-2222-4222-8222-222222222222",
        "33333333-3333-4333-8333-333333333333",
        OperationType.Load,
        [1, 2],
        2,
        true,
        new string('0', 64));

    [Fact]
    public void ShutEmptyWithNoBasketInNamesTheCancellation()
    {
        Assert.Equal(
            "2号仓关门时没有放入篮子。请按「重新开门」继续装；不装了请按「取消装货」。",
            Guidance(new("WAITING_OPERATOR", [2], [], 0, WireToGatePromptCause.ShutOverOpposite), "取消装货"));
    }

    [Fact]
    public void ShutEmptyWithABasketInNamesTheSkipNotTheCancellation()
    {
        Assert.Equal(
            "2号仓关门时没有放入篮子。请按「重新开门」继续装；不装剩下的请按「剩下的不装」。",
            Guidance(new("WAITING_OPERATOR", [2], [1], 0, WireToGatePromptCause.ShutOverOpposite), "取消装货"));
    }

    [Fact]
    public void ShutEmptyWithTheEntryNotLitNamesNoWayToGiveUp()
    {
        Assert.Equal(
            "2号仓关门时没有放入篮子。请按「重新开门」继续装。",
            Guidance(new("WAITING_OPERATOR", [2], [], 0, WireToGatePromptCause.ShutOverOpposite), null));
    }

    [Fact]
    public void PastTheDeadlineWithTheDoorOpenTheCancellationComesAfterShuttingIt()
    {
        Assert.Equal(
            "请放入货物并关闭2号仓门；不装了请先关好仓门，再按「取消装货」。",
            Guidance(new("WAITING_OPERATOR", [2], []), "取消装货", deadlinePassed: true));
    }

    [Fact]
    public void PastTheDeadlineWithABasketInTheSkipComesAfterShuttingTheDoor()
    {
        Assert.Equal(
            "请放入货物并关闭2号仓门；不装剩下的请先关好仓门，再按「剩下的不装」。",
            Guidance(new("WAITING_OPERATOR", [2], [1]), "取消装货", deadlinePassed: true));
    }

    [Fact]
    public void PastTheDeadlineWithTheEntryNotLitOnlyTheDoorIsNamed()
    {
        Assert.Equal(
            "请放入货物并关闭2号仓门。",
            Guidance(new("WAITING_OPERATOR", [2], []), null, deadlinePassed: true));
    }

    private static string Guidance(
        WireToGateOperationProgress progress,
        string? loadCancellationCaption,
        bool deadlinePassed = false) =>
        WireToGateBusinessService.OperationGuidance(Load, progress, deadlinePassed, loadCancellationCaption);
}
