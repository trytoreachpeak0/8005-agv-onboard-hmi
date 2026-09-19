using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 持货等单、装满与装货结束原因那一行的文案（批次7-13，<c>8005-agv-onboard-hmi#134</c>，REQ-0354、REQ-0355）。
/// </summary>
/// <remarks>
/// 期限只取快照里的整值，本类不推算、不判到期后的去向：过了期限只说「已到期，等待服务端」。时区作为参数传入，
/// 测试用固定的东八区，与车载端现场一致，结果不随跑测试的机器变。
/// </remarks>
public sealed class LoadingPhaseTextTests
{
    private static readonly TimeZoneInfo China = TimeZoneInfo.CreateCustomTimeZone("CST+8", TimeSpan.FromHours(8), "CST", "CST");
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoLoadingPhaseShowsNoLine()
    {
        LoadingPhaseView view = LoadingPhaseText.Describe(null, Now, China);

        Assert.Equal(LoadingPhaseLine.None, view.Line);
        Assert.Equal(string.Empty, view.Text);
    }

    [Fact]
    public void LoadingShowsNoLine()
    {
        LoadingPhaseView view = LoadingPhaseText.Describe(new WireToGateLoadingPhase("LOADING", null, null), Now, China);

        Assert.Equal(LoadingPhaseLine.None, view.Line);
    }

    /// <summary>
    /// 等单期限给出时：最迟几点离站（车载端本地时间）与还剩多久，剩余向上取整到秒。
    /// </summary>
    [Fact]
    public void CargoHoldingWithADeadlineShowsTheLatestDepartureAndTheTimeLeft()
    {
        LoadingPhaseView view = LoadingPhaseText.Describe(
            new WireToGateLoadingPhase("CARGO_HOLDING_WAIT", Now.AddSeconds(754.2), null),
            Now,
            China);

        Assert.Equal(LoadingPhaseLine.CargoHolding, view.Line);
        Assert.Equal("等待更多任务，最迟 10:12 离站（剩 12:35）", view.Text);
        Assert.Equal("ACTIVE", view.Code);
    }

    [Fact]
    public void CargoHoldingWithoutADeadlineShowsNoCountdown()
    {
        LoadingPhaseView view = LoadingPhaseText.Describe(
            new WireToGateLoadingPhase("CARGO_HOLDING_WAIT", null, null),
            Now,
            China);

        Assert.Equal(LoadingPhaseLine.CargoHolding, view.Line);
        Assert.Equal("等待更多任务", view.Text);
        Assert.Equal("NO_DEADLINE", view.Code);
    }

    /// <summary>
    /// 过了期限车载端不自己判接下来发生什么，只说在等服务端。
    /// </summary>
    [Fact]
    public void CargoHoldingPastItsDeadlineOnlySaysItIsWaitingForTheServer()
    {
        LoadingPhaseView view = LoadingPhaseText.Describe(
            new WireToGateLoadingPhase("CARGO_HOLDING_WAIT", Now.AddSeconds(-1), null),
            Now,
            China);

        Assert.Equal(LoadingPhaseLine.CargoHolding, view.Line);
        Assert.Equal("已到期，等待服务端", view.Text);
        Assert.Equal("EXPIRED", view.Code);
    }

    [Fact]
    public void VehicleFullSaysItLeavesOnceThePromisedWorkIsLoaded()
    {
        LoadingPhaseView view = LoadingPhaseText.Describe(
            new WireToGateLoadingPhase("VEHICLE_FULL", null, null),
            Now,
            China);

        Assert.Equal(LoadingPhaseLine.VehicleFull, view.Line);
        Assert.Equal("已装满，装完已承诺的任务后离站", view.Text);
        Assert.Equal("VEHICLE_FULL", view.Code);
    }

    [Theory]
    [InlineData("WAITING_STATION_YIELD", "另一辆车需要本站，本车结束等单，前往卸货")]
    [InlineData("CARGO_HOLDING_TIMEOUT", "等单已到期，本车结束等单，前往卸货")]
    [InlineData("VEHICLE_FULL", "已装满，本站装货结束")]
    [InlineData("PLANNED_LOADING_COMPLETE", "本站计划装货已完成")]
    public void EachClosedReasonHasItsOwnSentence(string closedReason, string expected)
    {
        LoadingPhaseView view = LoadingPhaseText.Describe(
            new WireToGateLoadingPhase("CLOSED", null, closedReason),
            Now,
            China);

        Assert.Equal(LoadingPhaseLine.Closed, view.Line);
        Assert.Equal(expected, view.Text);
        Assert.Equal(closedReason, view.Code);
    }

    /// <summary>
    /// 表里没有的结束原因显示原始码，不映射成已知原因。
    /// </summary>
    [Fact]
    public void AnUnknownClosedReasonIsShownAsItsRawCode()
    {
        LoadingPhaseView view = LoadingPhaseText.Describe(
            new WireToGateLoadingPhase("CLOSED", null, "SOME_FUTURE_REASON"),
            Now,
            China);

        Assert.Equal(LoadingPhaseLine.Closed, view.Line);
        Assert.Equal("SOME_FUTURE_REASON", view.Text);
        Assert.Equal("SOME_FUTURE_REASON", view.Code);
    }
}
