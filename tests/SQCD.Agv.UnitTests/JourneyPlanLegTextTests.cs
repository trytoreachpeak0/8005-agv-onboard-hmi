using SQCD.Agv.Application;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 计划腿列表的文案（批次7-13，<c>8005-agv-onboard-hmi#134</c>）：停靠目的、取／卸、腿的状态各一张表，
/// 表里没有的值原样显示，不映射成已知值。
/// </summary>
public sealed class JourneyPlanLegTextTests
{
    [Theory]
    [InlineData("BUSINESS", "业务站")]
    [InlineData("WAITING_POINT", "等待点")]
    [InlineData("CHARGER", "充电桩")]
    [InlineData("SOMETHING_NEW", "SOMETHING_NEW")]
    public void EachStopPurposeCategoryHasItsText(string category, string expected) =>
        Assert.Equal(expected, JourneyPlanLegText.StopPurpose(category));

    [Theory]
    [InlineData("TO_PICKUP", "取货")]
    [InlineData("TO_DROPOFF", "卸货")]
    [InlineData(null, "")]
    public void TheLegTypeIsADirectionAndANullLegTypeHasNone(string? legType, string expected) =>
        Assert.Equal(expected, JourneyPlanLegText.LegType(legType));

    [Theory]
    [InlineData("PLANNED", "待前往")]
    [InlineData("ACTIVE", "前往中")]
    [InlineData("ARRIVED", "已到达")]
    [InlineData("COMPLETED", "已完成")]
    [InlineData("BLOCKED", "受阻")]
    [InlineData("PAUSED", "PAUSED")]
    public void EachLegStateHasItsText(string state, string expected) =>
        Assert.Equal(expected, JourneyPlanLegText.State(state));
}
