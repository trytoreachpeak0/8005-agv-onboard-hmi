using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 一站多条需求与多停靠计划在界面上的样子（批次7-13，<c>trytoreachpeak0/8005-agv-onboard-hmi#134</c>）：
/// 服务端快照经 <see cref="MainViewModel"/> 真实的更新路径变成清单列表、计划腿列表与持货等单那几行。
/// </summary>
/// <remarks>
/// 不开窗口，照 <c>StationDepartureCountdownViewModelTests</c> 的写法。文案本身由单元测试覆盖；这里钉的是
/// 界面那一步：行序只随服务端，最新快照整值替换，旧的不留。经假服务端的整条链路在
/// <see cref="MultiDemandJourneyG2Tests"/>。
/// </remarks>
public sealed class MultiDemandViewModelTests
{
    private const string DemandA = "aaaaaaaa-0000-4000-8000-00000000000a";
    private const string DemandB = "bbbbbbbb-0000-4000-8000-00000000000b";
    private const string DemandC = "cccccccc-0000-4000-8000-00000000000c";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 2, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 清单是一张列表，每条需求一行，行序等于服务端 <c>items[]</c> 的顺序——这里故意不是子批号或需求号的顺序，
    /// 本地不排序。每行带自己的子批号、取／卸、任务类型与花篮数。
    /// </summary>
    [Fact]
    public async Task TheWorklistIsOneRowPerDemandInTheServersOrder()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);

        viewModel.UpdateWireToGateJourney(Journey(Worklist(
            1,
            Item(DemandC, "SUBLOT-C", "WIRE_TO_NITROGEN", "DROPOFF", 3),
            Item(DemandA, "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2),
            Item(DemandB, "SUBLOT-B", "WIRE_TO_OPTICAL", "PICKUP", 1))));

        Assert.Equal(
            [
                ("SUBLOT-C", "卸货", "焊线→氮气柜", "3 篮"),
                ("SUBLOT-A", "取货", "焊线→质检关卡", "2 篮"),
                ("SUBLOT-B", "取货", "焊线→三光", "1 篮")
            ],
            viewModel.WorklistItems.Select(row =>
                (row.Sublot, row.DirectionText, row.TaskTypeText, row.ExpectedBasketCountText)));
        Assert.True(viewModel.HasWorklistItems);
    }

    /// <summary>
    /// 到站那一格只留站名；子批号在清单列表里。两条清单项方向不同时顶栏方向为空，不挑一条来显示。
    /// </summary>
    [Fact]
    public async Task TheVisitShowsOnlyTheStationAndTheTopBarShowsNoDirectionWhenItemsDisagree()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);

        viewModel.UpdateWireToGateJourney(Journey(Worklist(
            1,
            Item(DemandA, "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2),
            Item(DemandB, "SUBLOT-B", "WIRE_TO_GATE", "DROPOFF", 1))));

        Assert.Equal("ST-01", viewModel.VisitText);
        Assert.Equal(string.Empty, viewModel.StopDirectionText);
        Assert.Equal("焊线→质检关卡", viewModel.TaskTypeText);
    }

    /// <summary>
    /// 新修订号的清单整张替换旧的：少了的行消失，不与旧表合并；清单变为空时写「无待处理任务」。
    /// </summary>
    [Fact]
    public async Task ANewWorklistRevisionReplacesTheWholeList()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.UpdateWireToGateJourney(Journey(Worklist(
            1,
            Item(DemandA, "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2),
            Item(DemandB, "SUBLOT-B", "WIRE_TO_GATE", "PICKUP", 1))));

        viewModel.UpdateWireToGateJourney(Journey(Worklist(
            2,
            Item(DemandB, "SUBLOT-B", "WIRE_TO_GATE", "PICKUP", 1))));

        Assert.Equal(["SUBLOT-B"], viewModel.WorklistItems.Select(row => row.Sublot));

        viewModel.UpdateWireToGateJourney(Journey(Worklist(3)));

        Assert.Empty(viewModel.WorklistItems);
        Assert.False(viewModel.HasWorklistItems);
        Assert.Equal("ST-01 / 无待处理任务", viewModel.VisitText);
    }

    /// <summary>
    /// 断线时会话客户端把投影清成空旅程：清单列表一起清掉，不留上一站的行。
    /// </summary>
    [Fact]
    public async Task AnEmptyJourneyClearsTheWorklist()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.UpdateWireToGateJourney(Journey(Worklist(
            1,
            Item(DemandA, "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2),
            Item(DemandB, "SUBLOT-B", "WIRE_TO_GATE", "PICKUP", 1))));

        viewModel.UpdateWireToGateJourney(WireToGateJourneySnapshot.Empty);

        Assert.Empty(viewModel.WorklistItems);
        Assert.False(viewModel.HasWorklistItems);
        Assert.Equal("旅程未同步", viewModel.VisitText);
    }

    internal static WireToGateJourneySnapshot Journey(
        WireToGateCurrentStopWorklist? worklist,
        WireToGateUpcomingStopPlan? plan = null,
        WireToGateLoadingPhase? loadingPhase = null) => new(
        new WireToGateVehicleBusinessState(
            1, "READY", "TRANSPORT", false, "SUFFICIENT", "NOT_CHARGING", loadingPhase, [], Now, new string('a', 64)),
        worklist,
        plan,
        Now);

    internal static WireToGateCurrentStopWorklist Worklist(long revision, params WireToGateWorklistItem[] items) =>
        new("ST-01", revision, null, null, items, new string('b', 64));

    internal static WireToGateWorklistItem Item(
        string demandId,
        string sublot,
        string workType,
        string stopRole,
        int expectedBasketCount) =>
        new(demandId, $"TD-{sublot}", sublot, workType, stopRole, expectedBasketCount);

    internal static async Task<MainViewModel> ViewModel(OnboardController controller, IClock? clock = null)
    {
        MainViewModel viewModel = new(
            controller,
            new RecordingLogger(),
            "agv02",
            // Slots 1-4 front, 5-8 rear.
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()))
        {
            Clock = clock ?? new SystemClock(),
            StationDepartureCountdownDispatcher = null
        };
        await viewModel.InitializeAsync();
        return viewModel;
    }

    internal static OnboardController Controller() => new(
        new FakeIoModuleClient(),
        new DisabledRuleGateway(),
        new RecordingLogger(),
        new SystemClock(),
        new OnboardWorkflowOptions(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromSeconds(1),
            128,
            2));
}
