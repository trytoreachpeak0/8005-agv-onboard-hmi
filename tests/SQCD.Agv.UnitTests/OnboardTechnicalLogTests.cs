using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 车载技术日志的七个覆盖面、默认分类、保留期与文件命名（REQ-0272，onboard-hmi#312）。
/// </summary>
/// <remarks>不挂 <c>IntegrationSlice</c> trait，理由同 <see cref="OnboardAlarmSnapshotTests"/>。</remarks>
public sealed class OnboardTechnicalLogTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(8));

    /// <summary>
    /// 枚举就是需求原文那七个面，按原文顺序，一个不多一个不少。此前是四类，其中「操作事件」需求里没有，
    /// 执行阶段、配置、重启恢复、本地异常四个面缺席。
    /// </summary>
    [Fact]
    public void TheCategoriesAreExactlyTheSevenAreasTheRequirementNames()
    {
        Assert.Equal(
            [
                OnboardTechnicalLogCategory.IoCommunication,
                OnboardTechnicalLogCategory.SignalChange,
                OnboardTechnicalLogCategory.SafetyDecision,
                OnboardTechnicalLogCategory.ExecutionPhase,
                OnboardTechnicalLogCategory.Configuration,
                OnboardTechnicalLogCategory.RestartRecovery,
                OnboardTechnicalLogCategory.LocalFault
            ],
            Enum.GetValues<OnboardTechnicalLogCategory>());
    }

    [Theory]
    [InlineData("ModbusTcpIoModuleClient", OnboardTechnicalLogCategory.IoCommunication)]
    [InlineData("OnboardAlarmMonitor", OnboardTechnicalLogCategory.SafetyDecision)]
    [InlineData("WireToGateBusinessService", OnboardTechnicalLogCategory.ExecutionPhase)]
    [InlineData("SlotGroupPresentation", OnboardTechnicalLogCategory.Configuration)]
    [InlineData("WireToGateSessionService", OnboardTechnicalLogCategory.RestartRecovery)]
    public void AnUntaggedEntryTakesItsSourcesDefault(string source, OnboardTechnicalLogCategory expected)
    {
        Assert.Equal(expected, OnboardTechnicalLogCategories.DefaultFor(source, LogSeverity.Information));
        Assert.Equal(expected, OnboardTechnicalLogCategories.DefaultFor(source, LogSeverity.Warning));
    }

    [Fact]
    public void AnUntaggedErrorIsALocalFaultWhateverItsSource()
    {
        Assert.Equal(
            OnboardTechnicalLogCategory.LocalFault,
            OnboardTechnicalLogCategories.DefaultFor("ModbusTcpIoModuleClient", LogSeverity.Error));
    }

    /// <summary>未登记的来源在运行时不抛异常：分类不能挡住写日志，更不能挡住业务。拦它的是架构测试。</summary>
    [Fact]
    public void AnUnregisteredSourceIsALocalFaultAndDoesNotThrow()
    {
        Assert.False(OnboardTechnicalLogCategories.IsRegisteredSource("NobodyRegisteredMe"));
        Assert.Equal(
            OnboardTechnicalLogCategory.LocalFault,
            OnboardTechnicalLogCategories.DefaultFor("NobodyRegisteredMe", LogSeverity.Information));
    }

    [Fact]
    public void RetentionDefaultsToThirtyDaysAndAShorterConfiguredPeriodIsRefused()
    {
        Assert.Equal(TimeSpan.FromDays(30), OnboardTechnicalLogRetentionPolicy.Minimum);
        Assert.Equal(TimeSpan.FromDays(30), OnboardTechnicalLogRetentionPolicy.Default.RetainFor);

        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(
            () => new OnboardTechnicalLogRetentionPolicy(TimeSpan.FromDays(7)));
        Assert.Contains("REQ-0272", failure.Message, StringComparison.Ordinal);

        // 可配置指的是可以更长。
        Assert.Equal(
            TimeSpan.FromDays(90),
            new OnboardTechnicalLogRetentionPolicy(TimeSpan.FromDays(90)).RetainFor);
    }

    /// <summary>
    /// 按天文件只有在那一天最晚的一条也满 30 天时才算过期：今天中午看，31 天前那一天整天都过了，
    /// 30 天前那一天的下午还没满 30 天，所以留。
    /// </summary>
    [Fact]
    public void ADayExpiresOnlyWhenItsLastPossibleEntryIsPastRetention()
    {
        OnboardTechnicalLogRetentionPolicy policy = OnboardTechnicalLogRetentionPolicy.Default;
        DateOnly today = DateOnly.FromDateTime(Now.DateTime);

        Assert.True(policy.IsDayExpired(today.AddDays(-31), Now));
        Assert.False(policy.IsDayExpired(today.AddDays(-30), Now));
        Assert.False(policy.IsDayExpired(today.AddDays(-29), Now));
        Assert.False(policy.IsDayExpired(today, Now));

        // 刚过午夜：30 天前那一天的最后一刻正好满 30 天。
        DateTimeOffset justAfterMidnight = new(today.ToDateTime(TimeOnly.MinValue), Now.Offset);
        Assert.True(policy.IsDayExpired(today.AddDays(-31), justAfterMidnight));
        Assert.False(policy.IsDayExpired(today.AddDays(-30), justAfterMidnight.AddTicks(-1)));
    }

    [Fact]
    public void ALongerConfiguredRetentionKeepsWhatTheDefaultWouldHavePurged()
    {
        DateOnly fortyDaysAgo = DateOnly.FromDateTime(Now.DateTime).AddDays(-40);

        Assert.True(OnboardTechnicalLogRetentionPolicy.Default.IsDayExpired(fortyDaysAgo, Now));
        Assert.False(new OnboardTechnicalLogRetentionPolicy(TimeSpan.FromDays(90)).IsDayExpired(fortyDaysAgo, Now));
    }

    [Theory]
    [InlineData("agv-20260909.log", true)]
    [InlineData("agv-20260230.log", false)]
    [InlineData("agv-2026090.log", false)]
    [InlineData("agv-202609091.log", false)]
    [InlineData("agv-20260909.log.bak", false)]
    [InlineData("AGV-20260909.log", false)]
    [InlineData("agv-20260909.LOG", false)]
    [InlineData("agv-2026-909.log", false)]
    [InlineData("agv-current.log", false)]
    [InlineData("hmi-state-20260909.ndjson", false)]
    [InlineData("control.err.log", false)]
    [InlineData("onboard.out.log", false)]
    public void OnlyTheExactDailyFileNameCountsAsATechnicalLog(string fileName, bool expected)
    {
        Assert.Equal(expected, OnboardTechnicalLogFileName.TryParseDay(fileName, out _));
    }

    [Fact]
    public void TheDailyFileNameRoundTrips()
    {
        DateOnly day = new(2026, 9, 9);

        Assert.Equal("agv-20260909.log", OnboardTechnicalLogFileName.For(day));
        Assert.True(OnboardTechnicalLogFileName.TryParseDay(OnboardTechnicalLogFileName.For(day), out DateOnly parsed));
        Assert.Equal(day, parsed);
    }
}
