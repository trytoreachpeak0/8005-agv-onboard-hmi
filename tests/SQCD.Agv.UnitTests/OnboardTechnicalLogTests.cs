using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 车载技术日志覆盖四类事件，保留期至少 30 天且可配置（REQ-0272）。
/// </summary>
/// <remarks>不挂 <c>IntegrationSlice</c> trait，理由同 <see cref="OnboardAlarmSnapshotTests"/>。</remarks>
public sealed class OnboardTechnicalLogTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AllFourCategoriesAreCoveredAndKeptApart()
    {
        OnboardTechnicalLog log = new();
        foreach (OnboardTechnicalLogCategory category in Enum.GetValues<OnboardTechnicalLogCategory>())
        {
            log.Append(Now, category, $"{category} 的一条记录。");
        }

        Assert.Equal(
            [
                OnboardTechnicalLogCategory.IoCommunication,
                OnboardTechnicalLogCategory.SignalChange,
                OnboardTechnicalLogCategory.SafetyDecision,
                OnboardTechnicalLogCategory.OperatorEvent
            ],
            Enum.GetValues<OnboardTechnicalLogCategory>());
        Assert.Equal(4, log.Entries.Select(entry => entry.Category).Distinct().Count());
    }

    [Fact]
    public void RetentionDefaultsToThirtyDaysAndAShorterConfiguredPeriodIsRefused()
    {
        Assert.Equal(TimeSpan.FromDays(30), OnboardTechnicalLogRetentionPolicy.Minimum);
        Assert.Equal(TimeSpan.FromDays(30), new OnboardTechnicalLog().RetentionPolicy.RetainFor);

        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(
            () => new OnboardTechnicalLogRetentionPolicy(TimeSpan.FromDays(7)));
        Assert.Contains("REQ-0272", failure.Message, StringComparison.Ordinal);

        // 可配置指的是可以更长。
        Assert.Equal(
            TimeSpan.FromDays(90),
            new OnboardTechnicalLogRetentionPolicy(TimeSpan.FromDays(90)).RetainFor);
    }

    [Fact]
    public void NothingInsideRetentionIsPurgedAndEverythingPastItCanBe()
    {
        OnboardTechnicalLog log = new();
        log.Append(Now.AddDays(-40), OnboardTechnicalLogCategory.IoCommunication, "四十天前");
        log.Append(Now.AddDays(-31), OnboardTechnicalLogCategory.SignalChange, "三十一天前");
        log.Append(Now.AddDays(-29), OnboardTechnicalLogCategory.SafetyDecision, "二十九天前");
        log.Append(Now, OnboardTechnicalLogCategory.OperatorEvent, "刚刚");

        // 未过期不清理：把「现在」放在最早那条还没满 30 天的时点上，一条都不该走。
        Assert.Equal(0, log.PurgeExpired(Now.AddDays(-20)));
        Assert.Equal(4, log.Entries.Count);

        // 已过期可清理：两条超过 30 天的走了，另外两条留下。
        Assert.Equal(2, log.PurgeExpired(Now));
        Assert.Equal(
            ["二十九天前", "刚刚"],
            log.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public void ALongerConfiguredRetentionKeepsWhatTheDefaultWouldHavePurged()
    {
        OnboardTechnicalLog log = new(new OnboardTechnicalLogRetentionPolicy(TimeSpan.FromDays(90)));
        log.Append(Now.AddDays(-40), OnboardTechnicalLogCategory.IoCommunication, "四十天前");

        Assert.Equal(0, log.PurgeExpired(Now));
        Assert.Single(log.Entries);
    }
}
