using SQCD.Agv.Application;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 本站倒计时的显示规则，来自 ADR-cross-0055 的 Consequences 与 ADR-cross-0058 决策 3。
///
/// 三档配色（>60 秒、最后 60 秒、最后 10 秒逐秒闪烁）是原有措辞；两个边界态是决策 4 之后新增的，
/// 也是这里真正容易做错的地方：**期限缺席不是 00:00**，而**到期之后不是结束**——决策 4 让期限
/// 到期不必然结束本站，仓门未闭时服务端只发告警并继续等，车辆会带着一个已耗尽的倒计时停很久。
/// </summary>
public sealed class StationDepartureCountdownFormatterTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void AbsentDeadlineSaysSoInsteadOfShowingZero()
    {
        // 纯卸货站、关卡站、超时被配置为禁用，服务端都发 null。显示 00:00 会被读成「时间到了」。
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(null, Now);

        Assert.Equal("无倒计时", view.Text);
        Assert.Equal(StationDepartureCountdownTier.Absent, view.Tier);
        Assert.False(view.Dimmed);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void ComfortableRemainingTimeStaysOnTheNormalTier()
    {
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(
            Now + TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(30)),
            Now);

        Assert.Equal("04:30", view.Text);
        Assert.Equal(StationDepartureCountdownTier.Normal, view.Tier);
        Assert.False(view.Dimmed);
    }

    [Theory]
    [InlineData(61, nameof(StationDepartureCountdownTier.Normal))]
    [InlineData(60, nameof(StationDepartureCountdownTier.Warning))]
    [InlineData(11, nameof(StationDepartureCountdownTier.Warning))]
    [InlineData(10, nameof(StationDepartureCountdownTier.Critical))]
    [InlineData(1, nameof(StationDepartureCountdownTier.Critical))]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void TierBoundariesAreInclusiveOfTheStatedLastSeconds(int remainingSeconds, string expectedTier)
    {
        // 「最后 60 秒转黄」把 60 秒本身算在内，「最后 10 秒转红」同理。差一秒的判据在现场看不出来，
        // 但它决定了操作员是在第 60 秒还是第 59 秒收到第一个视觉提醒。
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(
            Now + TimeSpan.FromSeconds(remainingSeconds),
            Now);

        Assert.Equal(expectedTier, view.Tier.ToString());
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void SubSecondRemainderRoundsUpSoZeroNeverShowsWhileTimeIsLeft()
    {
        // 还剩 0.4 秒时显示 00:01。00:00 只属于「已到期」那一档，而那一档有自己的文案。
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(
            Now + TimeSpan.FromMilliseconds(400),
            Now);

        Assert.Equal("00:01", view.Text);
        Assert.Equal(StationDepartureCountdownTier.Critical, view.Tier);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void LastTenSecondsBlinkOnAlternatingWallClockSeconds()
    {
        DateTimeOffset deadline = Now + TimeSpan.FromSeconds(5);

        bool[] phases =
        [
            StationDepartureCountdownFormatter.Format(deadline, Now).Dimmed,
            StationDepartureCountdownFormatter.Format(deadline, Now + TimeSpan.FromSeconds(1)).Dimmed,
            StationDepartureCountdownFormatter.Format(deadline, Now + TimeSpan.FromSeconds(2)).Dimmed
        ];

        Assert.Equal([false, true, false], phases);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void NothingOutsideTheLastTenSecondsEverBlinks()
    {
        DateTimeOffset deadline = Now + TimeSpan.FromSeconds(30);

        Assert.False(StationDepartureCountdownFormatter.Format(deadline, Now).Dimmed);
        Assert.False(StationDepartureCountdownFormatter
            .Format(deadline, Now + TimeSpan.FromSeconds(1)).Dimmed);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void AnExhaustedDeadlineSaysTheServerStillOwnsTheDecision()
    {
        // 决策 4：仓门未闭时超时只转告警，车辆继续等。这一档不是终态，界面不能写成「本站结束」。
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(
            Now - TimeSpan.FromMinutes(7),
            Now);

        Assert.Equal("已到期，等待服务端结算", view.Text);
        Assert.Equal(StationDepartureCountdownTier.Expired, view.Tier);
        Assert.False(view.Dimmed);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void ExactlyAtTheDeadlineIsAlreadyExpired()
    {
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(Now, Now);

        Assert.Equal(StationDepartureCountdownTier.Expired, view.Tier);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void ADeadlineThatJumpsForwardIsRecomputedFromTheAbsoluteInstant()
    {
        // 同一次停靠内截止时间会往后跳：每批 LoadBatch 闭环后服务端重置计时并随下一份清单重发。
        // 这里锁的是「不持有递减状态」——先算一个快到期的，再算一个跳远之后的，第二次必须变回充裕。
        StationDepartureCountdownView nearlyDue =
            StationDepartureCountdownFormatter.Format(Now + TimeSpan.FromSeconds(3), Now);
        StationDepartureCountdownView afterReset =
            StationDepartureCountdownFormatter.Format(Now + TimeSpan.FromMinutes(5), Now);

        Assert.Equal(StationDepartureCountdownTier.Critical, nearlyDue.Tier);
        Assert.Equal(StationDepartureCountdownTier.Normal, afterReset.Tier);
        Assert.Equal("05:00", afterReset.Text);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void RemainingTimePastAnHourKeepsTheHoursInsteadOfWrappingToMinutes()
    {
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(
            Now + TimeSpan.FromMinutes(90),
            Now);

        Assert.Equal("1:30:00", view.Text);
    }
}
