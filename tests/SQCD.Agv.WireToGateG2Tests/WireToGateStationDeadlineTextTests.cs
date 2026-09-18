using SQCD.Agv.Application;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 离站期限到期后的文案（批次5-29，onboard-hmi#78）：在途装货仓门未闭或反复空关时覆盖 #75 的通用到期文案，
/// 提示行显示「请放入货物并关闭 N 号仓门；不装了请按取消」，倒计时那一行显示已过期时长；装货取消挂着时倒计时那一行显示
/// 「已到期，正在取消本站装货」。
/// </summary>
/// <remarks>
/// 期望文案按票面与调度会话的决定逐字写死，不从实现里取。放在本项目是因为文案类在 <c>net8.0-windows</c> 的
/// WPF 程序集里。不带 trait：纯呈现，行为由 <see cref="StationDeadlineExpiredG2Tests"/> 证明。
/// </remarks>
public sealed class WireToGateStationDeadlineTextTests
{
    private static readonly DateTimeOffset Deadline = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnOpenDoorPastTheDeadlineShowsHowLongTheStopIsOverdue()
    {
        // 倒计时那一格宽度固定（200 px），装不下整句；整句在提示行，这一格随时钟走已过期时长。
        string? text = WireToGateStationDeadlineText.CountdownOverride(
            Context(Deadline, Deadline + TimeSpan.FromSeconds(125)),
            cancellationOpen: false,
            loadAwaitingOperatorSlots: [3]);

        Assert.Equal("已过期 02:05", text);
    }

    [Fact]
    public void ThePromptNamesTheDoorAndTheWayOut()
    {
        Assert.Equal("请放入货物并关闭3号仓门；不装了请按取消。", WireToGateStationDeadlineText.LoadPrompt([3]));
    }

    [Fact]
    public void SeveralDoorsAreNamedTogether()
    {
        Assert.Equal(
            "请放入货物并关闭2、5号仓门；不装了请按取消。",
            WireToGateStationDeadlineText.LoadPrompt([5, 2]));
    }

    [Fact]
    public void TheOverdueTimeIsRoundedDown()
    {
        Assert.Equal("已过期 00:07", WireToGateStationDeadlineText.Overdue(TimeSpan.FromSeconds(7.9)));
    }

    [Fact]
    public void AnHourOverdueIsShownWithHours()
    {
        Assert.Equal("已过期 1:00:05", WireToGateStationDeadlineText.Overdue(TimeSpan.FromSeconds(3605)));
    }

    [Fact]
    public void AnOpenCancellationPastTheDeadlineSaysTheLoadIsBeingCancelled()
    {
        // 调度会话 2026-09-18 定（选项 A）：取消挂着时服务端既不开始装货，也不按期限结束本站，
        // 「等待本站结束」不准确；不显示已过期时长。
        string? text = WireToGateStationDeadlineText.CountdownOverride(
            Context(Deadline, Deadline + TimeSpan.FromSeconds(30)),
            cancellationOpen: true,
            loadAwaitingOperatorSlots: null);

        Assert.Equal("已到期，正在取消本站装货", text);
    }

    [Fact]
    public void AnOpenCancellationOutranksTheDoorPrompt()
    {
        // 在途取消已经按下、应答未到，执行器还在等人：再叫操作员「不装了请按取消」是让他按第二次。
        string? text = WireToGateStationDeadlineText.CountdownOverride(
            Context(Deadline, Deadline + TimeSpan.FromSeconds(30)),
            cancellationOpen: true,
            loadAwaitingOperatorSlots: [1]);

        Assert.Equal("已到期，正在取消本站装货", text);
    }

    [Fact]
    public void NothingWaitingOnTheOperatorKeepsTheGenericExpiredText()
    {
        Assert.Null(WireToGateStationDeadlineText.CountdownOverride(
            Context(Deadline, Deadline + TimeSpan.FromSeconds(30)),
            cancellationOpen: false,
            loadAwaitingOperatorSlots: null));
    }

    [Fact]
    public void BeforeTheDeadlineTheCountdownIsNotReplaced()
    {
        Assert.Null(WireToGateStationDeadlineText.CountdownOverride(
            Context(Deadline, Deadline - TimeSpan.FromSeconds(30)),
            cancellationOpen: true,
            loadAwaitingOperatorSlots: [1]));
        Assert.Null(WireToGateStationDeadlineText.CountdownOverride(
            Context(null, Deadline),
            cancellationOpen: true,
            loadAwaitingOperatorSlots: [1]));
    }

    private static StationDepartureCountdownContext Context(DateTimeOffset? deadlineAt, DateTimeOffset now) =>
        new(deadlineAt, now, StationDepartureCountdownFormatter.Format(deadlineAt, now));
}
