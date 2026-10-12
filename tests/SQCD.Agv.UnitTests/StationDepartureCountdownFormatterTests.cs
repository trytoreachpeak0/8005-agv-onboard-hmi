using SQCD.Agv.Application;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 离站期限倒计时的五种显示状态（<c>8005-agv-onboard-hmi#75</c>，规则来自 ADR-cross-0055 的 Consequences）。
/// </summary>
/// <remarks>
/// 「现在」一律由测试给定，不读系统时钟。不带 <c>IntegrationSlice</c>／<c>ProtocolVector</c> trait：纯显示规则，
/// 不证明任何协议向量。
/// </remarks>
public sealed class StationDepartureCountdownFormatterTests
{
    /// <summary>偶数 Unix 秒，闪烁处于「亮」相位。</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AbsentDeadlineSaysThereIsNoCountdownInsteadOfShowingZero()
    {
        // 纯卸货站、关卡站、超时禁用时服务端发 null。显示 00:00 会被读成「时间到了」。
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(null, Now);

        Assert.Equal("无倒计时", view.Text);
        Assert.Equal(StationDepartureCountdownTier.Absent, view.Tier);
        Assert.False(view.Dimmed);
    }

    /// <summary>
    /// 持货等单时换成「本站作业」口径（onboard-hmi#310）：在走时标签「本站作业剩余」、数值照旧 mm:ss；到期与无期限时
    /// 标签「本站作业」，到期那一句说清后果。不持货时一字不变。
    /// </summary>
    [Theory]
    [InlineData(true, 90, "本站作业剩余", "01:30", true)]
    [InlineData(true, -1, "本站作业", "已结束，车辆继续等单", true)]
    [InlineData(true, null, "本站作业", "无倒计时", true)]
    [InlineData(false, 90, "离站剩余", "01:30", false)]
    [InlineData(false, -1, "离站剩余", "已到期，等待本站结束", false)]
    [InlineData(false, null, "离站剩余", "无倒计时", false)]
    public void CargoHoldingWordsTheCountdownForTheStationsWork(
        bool cargoHolding,
        int? secondsLeft,
        string label,
        string text,
        bool stationWork)
    {
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(
            secondsLeft is { } left ? Now.AddSeconds(left) : null,
            Now);

        StationDepartureCountdownCaption caption = StationDepartureCountdownFormatter.Caption(view, cargoHolding);

        Assert.Equal(new StationDepartureCountdownCaption(label, text, stationWork), caption);
    }

    [Fact]
    public void MoreThanAMinuteLeftIsTheNormalTier()
    {
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(
            Now + TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(30),
            Now);

        Assert.Equal("04:30", view.Text);
        Assert.Equal(StationDepartureCountdownTier.Normal, view.Tier);
        Assert.False(view.Dimmed);
    }

    [Fact]
    public void TheLastMinuteTurnsYellowWithoutFlashing()
    {
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(
            Now + TimeSpan.FromSeconds(45),
            Now + TimeSpan.FromSeconds(1));

        Assert.Equal("00:44", view.Text);
        Assert.Equal(StationDepartureCountdownTier.Warning, view.Tier);
        Assert.False(view.Dimmed);
    }

    [Fact]
    public void TheLastTenSecondsTurnRedAndFlashOncePerSecond()
    {
        DateTimeOffset deadline = Now + TimeSpan.FromSeconds(8);

        StationDepartureCountdownView lit = StationDepartureCountdownFormatter.Format(deadline, Now);
        StationDepartureCountdownView dim = StationDepartureCountdownFormatter.Format(deadline, Now + TimeSpan.FromSeconds(1));
        StationDepartureCountdownView litAgain = StationDepartureCountdownFormatter.Format(deadline, Now + TimeSpan.FromSeconds(2));

        Assert.Equal(("00:08", StationDepartureCountdownTier.Critical, false), (lit.Text, lit.Tier, lit.Dimmed));
        Assert.Equal(("00:07", StationDepartureCountdownTier.Critical, true), (dim.Text, dim.Tier, dim.Dimmed));
        Assert.Equal(("00:06", StationDepartureCountdownTier.Critical, false), (litAgain.Text, litAgain.Tier, litAgain.Dimmed));
    }

    [Fact]
    public void FlashPhaseWithinASecondFollowsTheClockNotTheRefreshCount()
    {
        // 定时器 250 ms 刷一次；同一秒内的四次刷新必须是同一相位，否则看着是乱闪而不是逐秒闪。
        DateTimeOffset deadline = Now + TimeSpan.FromSeconds(9);

        bool[] phases = [.. Enumerable.Range(0, 4).Select(tick =>
            StationDepartureCountdownFormatter.Format(deadline, Now + TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(250 * tick)).Dimmed)];

        Assert.All(phases, Assert.True);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(90)]
    [InlineData(3 * 3600)]
    public void ExpiredDeadlineNeverShowsANegativeNumber(int secondsPastDeadline)
    {
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(
            Now - TimeSpan.FromSeconds(secondsPastDeadline),
            Now);

        Assert.Equal("已到期，等待本站结束", view.Text);
        Assert.Equal(StationDepartureCountdownTier.Expired, view.Tier);
        Assert.False(view.Dimmed);
        Assert.DoesNotContain("-", view.Text);
    }

    [Theory]
    [InlineData(61_000, "01:01", StationDepartureCountdownTier.Normal)]
    [InlineData(60_001, "01:01", StationDepartureCountdownTier.Normal)]
    [InlineData(60_000, "01:00", StationDepartureCountdownTier.Warning)]
    [InlineData(11_000, "00:11", StationDepartureCountdownTier.Warning)]
    [InlineData(10_000, "00:10", StationDepartureCountdownTier.Critical)]
    [InlineData(400, "00:01", StationDepartureCountdownTier.Critical)]
    public void TierBoundariesIncludeTheStatedLastSeconds(int remainingMs, string expectedText, StationDepartureCountdownTier expectedTier)
    {
        // 向上取整到秒：剩 0.4 秒显示 00:01，00:00 从不出现——到期有自己的文案。
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(
            Now + TimeSpan.FromMilliseconds(remainingMs),
            Now);

        Assert.Equal(expectedText, view.Text);
        Assert.Equal(expectedTier, view.Tier);
    }

    [Fact]
    public void DeadlinesOverAnHourShowTotalHours()
    {
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(
            Now + TimeSpan.FromHours(26) + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(7),
            Now);

        Assert.Equal("26:05:07", view.Text);
        Assert.Equal(StationDepartureCountdownTier.Normal, view.Tier);
    }

    [Fact]
    public void OffsetsOfTheTwoClocksDoNotChangeTheRemainingTime()
    {
        // 服务端发 UTC，车载端时钟可能带 +08:00：比较的是时刻，不是墙上读数。
        DateTimeOffset localNow = Now.ToOffset(TimeSpan.FromHours(8));

        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(Now + TimeSpan.FromSeconds(90), localNow);

        Assert.Equal("01:30", view.Text);
    }
}
