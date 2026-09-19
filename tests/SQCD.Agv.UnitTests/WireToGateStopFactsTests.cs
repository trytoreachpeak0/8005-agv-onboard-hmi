using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 旅程事实一行里的方向与任务类型文案（批次6-03，<c>8005-agv-onboard-hmi#115</c>）。
/// </summary>
/// <remarks>
/// 期望文案取自 MES 统一查询六个分支的路线注释（program 仓第 13 号调研），不从被测代码里抄。
/// 不带 <c>IntegrationSlice</c>／<c>ProtocolVector</c> trait：两条向量的具名测试在
/// <c>TaskTypeAndDirectionVectorG2Tests</c>，这里只钉显示规则本身。
/// </remarks>
public sealed class WireToGateStopFactsTests
{
    [Theory]
    [InlineData("DIE_TO_WIRE_STAGING", "装片→焊线待送")]
    [InlineData("DIE_TO_OVEN", "装片→烘箱")]
    [InlineData("WIRE_TO_GATE", "焊线→质检关卡")]
    [InlineData("WIRE_TO_OPTICAL", "焊线→三光")]
    [InlineData("STAGING_TO_WIRE", "待送→焊线机台")]
    [InlineData("WIRE_TO_NITROGEN", "焊线→氮气柜")]
    public void EachOfTheSixTaskTypesHasItsOwnText(string workType, string expected)
    {
        Assert.Equal(expected, WireToGateStopFacts.TaskTypeText(Journey(Item(workType, "PICKUP"))));
    }

    [Theory]
    [InlineData("WIRE_TO_OVEN")]
    [InlineData("wire_to_gate")]
    [InlineData("")]
    public void AValueOutsideTheTableIsUnknownRatherThanTheNearestKnownType(string workType)
    {
        // 入站校验会先拒掉六值之外的 workType；文案表仍不把它映射成任何已知类型，缺一行时显示「未知」而不是猜。
        Assert.Equal("未知", WireToGateStopFacts.TaskTypeText(Journey(Item(workType, "PICKUP"))));
    }

    [Fact]
    public void WithoutAWorklistItemNoTaskTypeIsShownEvenWhenThePlanNamesAStation()
    {
        // 只有计划、没有清单项：不从计划腿、站名或站点功能推任务类型。
        WireToGateJourneySnapshot planOnly = new(
            BusinessState(new WireToGateBlockingFact("ACTION_NOT_ALLOWED_IN_STATE", "TASK_TYPE", "STAGING_TO_WIRE")),
            null,
            Plan(Leg("TO_PICKUP", "PLANNED", publicStationFunction: "WIRE_STAGING")),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(string.Empty, WireToGateStopFacts.TaskTypeText(planOnly));

        WireToGateJourneySnapshot emptyWorklist = new(
            null,
            new WireToGateCurrentStopWorklist("ST-01", 1, null, null, [], new string('a', 64)),
            Plan(Leg("TO_PICKUP", "ARRIVED")),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(string.Empty, WireToGateStopFacts.TaskTypeText(emptyWorklist));
        Assert.Equal(string.Empty, WireToGateStopFacts.TaskTypeText(WireToGateJourneySnapshot.Empty));
    }

    [Theory]
    [InlineData("PICKUP", "取货")]
    [InlineData("DROPOFF", "卸货")]
    public void TheDirectionIsTheWorklistItemsStopRole(string stopRole, string expected)
    {
        Assert.Equal(expected, WireToGateStopFacts.DirectionText(Journey(Item("WIRE_TO_GATE", stopRole))));
    }

    [Theory]
    [InlineData("DIE_TO_WIRE_STAGING")]
    [InlineData("DIE_TO_OVEN")]
    [InlineData("WIRE_TO_GATE")]
    [InlineData("WIRE_TO_OPTICAL")]
    [InlineData("STAGING_TO_WIRE")]
    [InlineData("WIRE_TO_NITROGEN")]
    public void TheSameStopRoleShowsTheSameDirectionWhateverTheTaskType(string workType)
    {
        // STAGING_TO_WIRE 在 AREA 机台是卸货，WIRE_TO_GATE 在 AREA 机台是取货——方向随服务端的 stopRole，
        // 不随任务类型：同一个 stopRole，六类显示一样。
        Assert.Equal("取货", WireToGateStopFacts.DirectionText(Journey(Item(workType, "PICKUP"))));
        Assert.Equal("卸货", WireToGateStopFacts.DirectionText(Journey(Item(workType, "DROPOFF"))));
    }

    [Fact]
    public void TheWorklistItemWinsOverThePlanAtTheStop()
    {
        // 到站后清单项是本站的权威；计划腿说的是去程，二者不一致时以清单为准。
        WireToGateJourneySnapshot journey = Journey(
            Item("STAGING_TO_WIRE", "DROPOFF"),
            Plan(Leg("TO_PICKUP", "COMPLETED"), Leg("TO_DROPOFF", "ARRIVED", sequence: 2)));

        Assert.Equal("卸货", WireToGateStopFacts.DirectionText(journey));
    }

    [Theory]
    [InlineData("TO_PICKUP", "取货")]
    [InlineData("TO_DROPOFF", "卸货")]
    public void WithoutAWorklistItemTheDirectionIsTheCurrentLegsLegType(string legType, string expected)
    {
        WireToGateJourneySnapshot journey = new(
            null,
            null,
            Plan(Leg(legType, "ACTIVE")),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, WireToGateStopFacts.DirectionText(journey));
    }

    [Fact]
    public void TheCurrentLegIsTheFirstOneNotYetCompletedInSequenceOrder()
    {
        // 反向旅程两腿：先去派工待送点取货，再去 AREA 机台卸货。第一腿完成后显示第二腿的方向。
        WireToGateJourneySnapshot journey = new(
            null,
            null,
            Plan(Leg("TO_DROPOFF", "PLANNED", sequence: 2), Leg("TO_PICKUP", "COMPLETED")),
            DateTimeOffset.UnixEpoch);

        Assert.Equal("卸货", WireToGateStopFacts.DirectionText(journey));
    }

    [Fact]
    public void AnEmptyWorklistShowsNoDirectionEvenWhenThePlanHasALeg()
    {
        // 清单已下发但本站没有任务（界面同一行显示「无待处理任务」）：方向为空，不退回去读计划腿，
        // 否则会和「无待处理任务」自相矛盾。
        WireToGateJourneySnapshot journey = new(
            null,
            new WireToGateCurrentStopWorklist("ST-01", 1, null, null, [], new string('a', 64)),
            Plan(Leg("TO_PICKUP", "ARRIVED")),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(string.Empty, WireToGateStopFacts.DirectionText(journey));
    }

    [Fact]
    public void NoDirectionIsShownWhenNeitherTheWorklistNorThePlanSaysOne()
    {
        // legType 为 null 的腿（等待点、充电桩）不带方向；什么都没下发时也不显示。
        WireToGateJourneySnapshot nullLegType = new(
            null,
            null,
            Plan(Leg(null, "ACTIVE")),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(string.Empty, WireToGateStopFacts.DirectionText(nullLegType));
        Assert.Equal(string.Empty, WireToGateStopFacts.DirectionText(WireToGateJourneySnapshot.Empty));
    }

    private static WireToGateJourneySnapshot Journey(WireToGateWorklistItem item, WireToGateUpcomingStopPlan? plan = null) =>
        new(
            null,
            new WireToGateCurrentStopWorklist("ST-01", 1, null, null, [item], new string('a', 64)),
            plan,
            DateTimeOffset.UnixEpoch);

    private static WireToGateWorklistItem Item(string workType, string stopRole) => new(
        "11111111-1111-4111-8111-111111111111",
        "TD-001",
        "SUBLOT-001",
        workType,
        stopRole,
        2);

    private static WireToGateUpcomingStopPlan Plan(params WireToGateMovementLeg[] legs) =>
        new(1, legs, new string('b', 64));

    private static WireToGateMovementLeg Leg(
        string? legType,
        string state,
        int sequence = 1,
        string? publicStationFunction = null) => new(
        $"22222222-2222-4222-8222-22222222222{sequence}",
        legType,
        "BUSINESS",
        "11111111-1111-4111-8111-111111111111",
        publicStationFunction,
        sequence,
        $"ST-0{sequence}",
        "26",
        state);

    private static WireToGateVehicleBusinessState BusinessState(params WireToGateBlockingFact[] facts) => new(
        1,
        "READY",
        "TRANSPORT",
        false,
        "SUFFICIENT",
        "NOT_CHARGING",
        null,
        facts,
        DateTimeOffset.UnixEpoch,
        new string('c', 64));
}
