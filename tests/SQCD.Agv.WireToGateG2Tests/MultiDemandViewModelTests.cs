using System.Windows.Threading;
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
    private static readonly TimeZoneInfo China =
        TimeZoneInfo.CreateCustomTimeZone("CST+8", TimeSpan.FromHours(8), "CST", "CST");

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

    /// <summary>
    /// 计划腿列表按 <c>sequence</c> 显示完整计划，与服务端下发的顺序无关，本地不重排；每条腿显示站点、停靠目的、
    /// 取／卸与状态。等待点与充电桩腿不带方向。<c>ItemStatus</c> 给 UIA 读的原始值 <c>sequence|category|state</c>。
    /// </summary>
    [Fact]
    public async Task ThePlanIsShownInSequenceOrderWithEveryLeg()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);

        viewModel.UpdateWireToGateJourney(Journey(
            null,
            Plan(
                1,
                Leg(3, "TO_DROPOFF", "BUSINESS", DemandA, "ST-GATE", "PLANNED"),
                Leg(1, "TO_PICKUP", "BUSINESS", DemandA, "ST-01", "COMPLETED"),
                Leg(2, null, "WAITING_POINT", null, "WP-07", "ACTIVE"),
                Leg(4, null, "CHARGER", null, "CH-02", "PLANNED"))));

        Assert.Equal(
            [
                ("1|BUSINESS|COMPLETED", "ST-01", "业务站", "取货", "已完成"),
                ("2|WAITING_POINT|ACTIVE", "WP-07", "等待点", string.Empty, "前往中"),
                ("3|BUSINESS|PLANNED", "ST-GATE", "业务站", "卸货", "待前往"),
                ("4|CHARGER|PLANNED", "CH-02", "充电桩", string.Empty, "待前往")
            ],
            viewModel.JourneyPlanLegs.Select(row =>
                (row.ItemStatus, row.StationId, row.StopPurposeText, row.LegTypeText, row.StateText)));
        Assert.True(viewModel.HasJourneyPlanLegs);
    }

    /// <summary>
    /// 修订号前进的新计划整张替换旧计划：旧表里有、新表里没有的腿不留，顺序只随新表的 <c>sequence</c>。
    /// </summary>
    [Fact]
    public async Task ANewPlanRevisionReplacesTheWholePlan()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.UpdateWireToGateJourney(Journey(
            null,
            Plan(
                1,
                Leg(1, "TO_PICKUP", "BUSINESS", DemandA, "ST-01", "ARRIVED"),
                Leg(2, "TO_DROPOFF", "BUSINESS", DemandA, "ST-GATE", "PLANNED"),
                Leg(3, "TO_DROPOFF", "BUSINESS", DemandB, "ST-OPT", "PLANNED"))));

        viewModel.UpdateWireToGateJourney(Journey(
            null,
            Plan(
                2,
                Leg(2, "TO_DROPOFF", "BUSINESS", DemandB, "ST-OPT", "PLANNED"),
                Leg(1, "TO_PICKUP", "BUSINESS", DemandA, "ST-01", "ARRIVED"))));

        Assert.Equal(["ST-01", "ST-OPT"], viewModel.JourneyPlanLegs.Select(row => row.StationId));
        Assert.Equal(["1|BUSINESS|ARRIVED", "2|BUSINESS|PLANNED"], viewModel.JourneyPlanLegs.Select(row => row.ItemStatus));
    }

    /// <summary>
    /// 断线清投影时计划腿列表一起清掉。
    /// </summary>
    [Fact]
    public async Task AnEmptyJourneyClearsThePlan()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.UpdateWireToGateJourney(Journey(
            null,
            Plan(1, Leg(1, "TO_PICKUP", "BUSINESS", DemandA, "ST-01", "ARRIVED"))));

        viewModel.UpdateWireToGateJourney(WireToGateJourneySnapshot.Empty);

        Assert.Empty(viewModel.JourneyPlanLegs);
        Assert.False(viewModel.HasJourneyPlanLegs);
    }

    /// <summary>
    /// 持货等单：倒计时那一行出现，剩余时间来自快照里的期限、随车载端时钟走；站点离站倒计时在它自己那一行，
    /// 两个期限并排、不合成一个。过了期限只写「已到期，等待服务端」，车载端不自己判接下来的事。
    /// </summary>
    [Fact]
    public async Task CargoHoldingCountsDownToTheSnapshotsDeadlineBesideTheStationDeadline()
    {
        ManualClock clock = new(Now);
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, clock);

        viewModel.UpdateWireToGateJourney(Journey(
            WorklistAt(1, Now.AddMinutes(30), Item(DemandA, "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2)),
            loadingPhase: new WireToGateLoadingPhase("CARGO_HOLDING_WAIT", Now.AddSeconds(90), null)));

        Assert.True(viewModel.HasCargoHoldingCountdown);
        Assert.Equal("等待更多任务，最迟 10:01 离站（剩 01:30）", viewModel.CargoHoldingCountdownText);
        Assert.Equal("ACTIVE", viewModel.CargoHoldingCountdownStatus);
        Assert.Equal("30:00", viewModel.StationDepartureCountdownText);
        Assert.False(viewModel.HasLoadingClosedReason);
        Assert.False(viewModel.HasVehicleFullNotice);

        clock.Advance(TimeSpan.FromSeconds(60));
        viewModel.RefreshLoadingPhase();
        Assert.Equal("等待更多任务，最迟 10:01 离站（剩 00:30）", viewModel.CargoHoldingCountdownText);

        clock.Advance(TimeSpan.FromSeconds(31));
        viewModel.RefreshLoadingPhase();
        Assert.Equal("已到期，等待服务端", viewModel.CargoHoldingCountdownText);
        Assert.Equal("EXPIRED", viewModel.CargoHoldingCountdownStatus);
        Assert.True(viewModel.HasCargoHoldingCountdown);
    }

    /// <summary>
    /// 期限整值取自最新快照：服务端换了期限，倒计时跟着换，不沿用上一份。
    /// </summary>
    [Fact]
    public async Task ANewCargoHoldingDeadlineReplacesTheOldOne()
    {
        ManualClock clock = new(Now);
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, clock);
        viewModel.UpdateWireToGateJourney(Journey(
            null,
            loadingPhase: new WireToGateLoadingPhase("CARGO_HOLDING_WAIT", Now.AddSeconds(90), null)));

        viewModel.UpdateWireToGateJourney(Journey(
            null,
            loadingPhase: new WireToGateLoadingPhase("CARGO_HOLDING_WAIT", Now.AddMinutes(10), null)));

        Assert.Equal("等待更多任务，最迟 10:10 离站（剩 10:00）", viewModel.CargoHoldingCountdownText);
    }

    [Fact]
    public async Task VehicleFullShowsItsNotice()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);

        viewModel.UpdateWireToGateJourney(Journey(
            null,
            loadingPhase: new WireToGateLoadingPhase("VEHICLE_FULL", null, null)));

        Assert.True(viewModel.HasVehicleFullNotice);
        Assert.Equal("已装满，装完已承诺的任务后离站", viewModel.VehicleFullNoticeText);
        Assert.False(viewModel.HasCargoHoldingCountdown);
        Assert.False(viewModel.HasLoadingClosedReason);
    }

    /// <summary>
    /// 让站：结束原因那一行出现，<c>ItemStatus</c> 是原始码；下一份快照的 <c>loadingPhase</c> 不再是 <c>CLOSED</c>
    /// 时这一行消失——变成 <c>null</c> 与变成 <c>LOADING</c> 都一样，车载端不自己计时清除。
    /// </summary>
    [Fact]
    public async Task TheClosedReasonFollowsTheLatestSnapshotAndDisappearsWithIt()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);

        viewModel.UpdateWireToGateJourney(Journey(
            null,
            loadingPhase: new WireToGateLoadingPhase("CLOSED", null, "WAITING_STATION_YIELD")));

        Assert.True(viewModel.HasLoadingClosedReason);
        Assert.Equal("另一辆车需要本站，本车结束等单，前往卸货", viewModel.LoadingClosedReasonText);
        Assert.Equal("WAITING_STATION_YIELD", viewModel.LoadingClosedReasonCode);

        viewModel.UpdateWireToGateJourney(Journey(null, loadingPhase: null));

        Assert.False(viewModel.HasLoadingClosedReason);
        Assert.Equal(string.Empty, viewModel.LoadingClosedReasonCode);

        viewModel.UpdateWireToGateJourney(Journey(
            null,
            loadingPhase: new WireToGateLoadingPhase("CLOSED", null, "CARGO_HOLDING_TIMEOUT")));
        viewModel.UpdateWireToGateJourney(Journey(
            null,
            loadingPhase: new WireToGateLoadingPhase("LOADING", null, null)));

        Assert.False(viewModel.HasLoadingClosedReason);
        Assert.False(viewModel.HasCargoHoldingCountdown);
        Assert.False(viewModel.HasVehicleFullNotice);
    }

    /// <summary>
    /// 断线清投影：持货倒计时与结束原因一起清掉，倒计时的定时器也停，不留旧倒计时。
    /// </summary>
    [Fact]
    public async Task AnEmptyJourneyClearsTheCargoHoldingLineAndStopsItsTimer()
    {
        using DispatcherThread ui = new();
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, new ManualClock(Now), ui.Dispatcher);
        await ui.Dispatcher.InvokeAsync(() => viewModel.UpdateWireToGateJourney(Journey(
            null,
            loadingPhase: new WireToGateLoadingPhase("CARGO_HOLDING_WAIT", Now.AddMinutes(5), null))));
        Assert.True(await ui.Dispatcher.InvokeAsync(() => viewModel.IsCargoHoldingCountdownTicking));

        await ui.Dispatcher.InvokeAsync(() => viewModel.UpdateWireToGateJourney(WireToGateJourneySnapshot.Empty));

        Assert.False(viewModel.HasCargoHoldingCountdown);
        Assert.Equal(string.Empty, viewModel.CargoHoldingCountdownText);
        Assert.False(viewModel.HasLoadingClosedReason);
        Assert.False(await ui.Dispatcher.InvokeAsync(() => viewModel.IsCargoHoldingCountdownTicking));
    }

    /// <summary>
    /// 清单项的侧：带这条需求的仓位命令一到，那一行就按命令的 <c>slots</c> 标前侧或后侧；别的需求那一行仍是
    /// 「待分配」，不串过去。重启后从日志里的操作上下文恢复。
    /// </summary>
    [Fact]
    public async Task EachRowTakesItsSideFromItsOwnDemandsSlotCommand()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.UpdateWireToGateJourney(Journey(Worklist(
            1,
            Item(DemandA, "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2),
            Item(DemandB, "SUBLOT-B", "WIRE_TO_GATE", "PICKUP", 1))));
        Assert.All(viewModel.WorklistItems, row => Assert.Equal(("待分配", "UNASSIGNED"), (row.SideText, row.SideCode)));

        viewModel.RecordSlotOperationCommand(Command(DemandA, [5, 6]));

        Assert.Equal(
            [("SUBLOT-A", "后侧", "REAR"), ("SUBLOT-B", "待分配", "UNASSIGNED")],
            viewModel.WorklistItems.Select(row => (row.Sublot, row.SideText, row.SideCode)));

        viewModel.UpdateJournaledOperations(WireToGateRecoveryState.Empty with
        {
            LastCompletedLoadOperationContext = WireToGateRecoveryOperationContext.FromCommand(Command(DemandB, [2]))
        });

        Assert.Equal(
            [("SUBLOT-A", "后侧", "REAR"), ("SUBLOT-B", "前侧", "FRONT")],
            viewModel.WorklistItems.Select(row => (row.Sublot, row.SideText, row.SideCode)));
    }

    /// <summary>
    /// 业务层说要先选需求、而清单里还没有选中行时：入口出现但按不动，旁边一句提示；选中一行之后按得动、提示消失。
    /// 确认框复述所选那一条（批次7-14）。
    /// </summary>
    /// <remarks>
    /// 批次7-13 时这里断言的是「暂不可用」那个占位提示，因为当时业务层根本不提供多需求下的扫码前取消。
    /// 本票把选需求交给操作员，所以同一处要断言的换成了「等一次选择」——断言没有变少：按不动仍然被钉住，
    /// 而「什么能让它按得动」是新加的。
    /// </remarks>
    [Fact]
    public async Task WithASelectionRequiredTheCancellationBeforeSublotWaitsForAPick()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => true,
            canRequestLoadCancellation: () => true,
            loadCancellationRequester: (_, _) => Task.FromResult(true),
            loadCancellationSelectionRequired: () => true);
        viewModel.UpdateWireToGateStatus(Session());

        viewModel.UpdateWireToGateJourney(Journey(Worklist(
            1,
            Item(DemandA, "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2),
            Item(DemandB, "SUBLOT-B", "WIRE_TO_GATE", "PICKUP", 1))));

        Assert.True(viewModel.CanRequestLoadCancellation);
        Assert.False(viewModel.CanPressLoadCancellation);
        Assert.True(viewModel.HasLoadCancellationSelectionHint);
        Assert.Equal("请先在清单中选择要取消的任务", viewModel.LoadCancellationSelectionHintText);
        Assert.Equal(string.Empty, viewModel.LoadCancellationConfirmationDetailText);

        viewModel.SelectedWorklistItem = viewModel.WorklistItems.Single(row => row.DemandId == DemandB);

        Assert.True(viewModel.CanPressLoadCancellation);
        Assert.False(viewModel.HasLoadCancellationSelectionHint);
        Assert.Equal(
            "将要取消的任务：子批 SUBLOT-B，焊线→质检关卡，1 篮。",
            viewModel.LoadCancellationConfirmationDetailText);
    }

    /// <summary>
    /// 只有一条清单项时与今天相同：取消按钮照常出现、按得动，没有提示——业务层不要求选择。
    /// </summary>
    [Fact]
    public async Task WithOneItemTheCancellationBeforeSublotIsOfferedAsBefore()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => true,
            canRequestLoadCancellation: () => true,
            loadCancellationRequester: (_, _) => Task.FromResult(true));
        viewModel.UpdateWireToGateStatus(Session());

        viewModel.UpdateWireToGateJourney(Journey(Worklist(
            1,
            Item(DemandA, "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2))));

        Assert.True(viewModel.CanRequestLoadCancellation);
        Assert.True(viewModel.CanPressLoadCancellation);
        Assert.False(viewModel.HasLoadCancellationSelectionHint);
    }

    /// <summary>
    /// 多条清单项、但业务层不要求选择时（例如在途装货的取消，主体是那次装货）没有提示，按钮照常按得动。
    /// 「要不要选」不是按清单条数推出来的，这一条钉的就是这件事。
    /// </summary>
    [Fact]
    public async Task WithTwoItemsAnOfferedCancellationNeedingNoPickShowsNoHint()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => true,
            canRequestLoadCancellation: () => true,
            loadCancellationRequester: (_, _) => Task.FromResult(true));
        viewModel.UpdateWireToGateStatus(Session());

        viewModel.UpdateWireToGateJourney(Journey(Worklist(
            1,
            Item(DemandA, "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2),
            Item(DemandB, "SUBLOT-B", "WIRE_TO_GATE", "PICKUP", 1))));

        Assert.True(viewModel.CanRequestLoadCancellation);
        Assert.True(viewModel.CanPressLoadCancellation);
        Assert.False(viewModel.HasLoadCancellationSelectionHint);
    }

    /// <summary>
    /// 「修正装货」照旧针对本站最后一次装货；入口旁标出那一次的子批号，多条清单项时操作员知道改的是哪一条。
    /// 那条需求已不在清单里时（装完被服务端移出），用本次运行见过的清单查子批号。
    /// </summary>
    [Fact]
    public async Task TheLoadCorrectionEntryNamesTheSublotOfTheLastCompletedLoad()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => false,
            canRequestLoadCorrection: () => true,
            loadCorrectionRequester: _ => Task.FromResult(true));
        viewModel.UpdateWireToGateStatus(Session());
        viewModel.UpdateWireToGateJourney(Journey(Worklist(
            1,
            Item(DemandA, "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2),
            Item(DemandB, "SUBLOT-B", "WIRE_TO_GATE", "PICKUP", 1))));

        viewModel.UpdateJournaledOperations(WireToGateRecoveryState.Empty with
        {
            LastCompletedLoadOperationContext = WireToGateRecoveryOperationContext.FromCommand(Command(DemandB, [2]))
        });

        Assert.True(viewModel.CanRequestLoadCorrection);
        Assert.Equal("修正对象：子批 SUBLOT-B", viewModel.LoadCorrectionTargetText);

        viewModel.UpdateWireToGateJourney(Journey(Worklist(
            2,
            Item(DemandA, "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2))));

        Assert.Equal("修正对象：子批 SUBLOT-B", viewModel.LoadCorrectionTargetText);
    }

    /// <summary>
    /// 说不出子批号（重启后清单里已没有那条需求）时如实写「本站最后一次装货」，不挑一条来冒充。
    /// </summary>
    [Fact]
    public async Task AnUnknownCorrectionTargetIsNamedAsTheLastLoadAtThisStop()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => false,
            canRequestLoadCorrection: () => true,
            loadCorrectionRequester: _ => Task.FromResult(true));
        viewModel.UpdateWireToGateStatus(Session());
        viewModel.UpdateWireToGateJourney(Journey(Worklist(
            1,
            Item(DemandA, "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2))));

        viewModel.UpdateJournaledOperations(WireToGateRecoveryState.Empty with
        {
            LastCompletedLoadOperationContext = WireToGateRecoveryOperationContext.FromCommand(Command(DemandC, [2]))
        });

        Assert.Equal("修正对象：本站最后一次装货", viewModel.LoadCorrectionTargetText);
    }

    internal static WireToGateSlotOperationCommand Command(string demandId, IReadOnlyList<int> slots) => new(
        Guid.NewGuid().ToString("D"),
        null,
        1,
        Now,
        demandId,
        "77777777-7777-4777-8777-777777777777",
        Guid.NewGuid().ToString("D"),
        OperationType.Load,
        slots,
        1,
        true,
        new string('0', 64));

    internal static WireToGateSessionSnapshot Session() => new(
        Connected: true,
        SessionGeneration: 1,
        Readiness: WireToGateSessionReadiness.Ready,
        ReasonCodes: [],
        CapabilityVersion: 1,
        SafetyStateVersion: 1,
        UpdatedAt: Now);

    internal static WireToGateUpcomingStopPlan Plan(long revision, params WireToGateMovementLeg[] legs) =>
        new(revision, legs, new string('c', 64));

    internal static WireToGateMovementLeg Leg(
        int sequence,
        string? legType,
        string stopPurposeCategory,
        string? demandId,
        string stationId,
        string state) =>
        new($"22222222-2222-4222-8222-{sequence:D12}", legType, stopPurposeCategory, demandId, null, sequence, stationId, "MAP-26", state);

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
        WorklistAt(revision, null, items);

    internal static WireToGateCurrentStopWorklist WorklistAt(
        long revision,
        DateTimeOffset? stationDepartureDeadlineAt,
        params WireToGateWorklistItem[] items) =>
        new("ST-01", revision, null, stationDepartureDeadlineAt, items, new string('b', 64));

    internal static WireToGateWorklistItem Item(
        string demandId,
        string sublot,
        string workType,
        string stopRole,
        int expectedBasketCount) =>
        new(demandId, $"TD-{sublot}", sublot, workType, stopRole, expectedBasketCount);

    internal static async Task<MainViewModel> ViewModel(
        OnboardController controller,
        IClock? clock = null,
        Dispatcher? dispatcher = null)
    {
        MainViewModel viewModel = new(
            controller,
            new RecordingLogger(),
            "agv02",
            // Slots 1-4 front, 5-8 rear.
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()))
        {
            Clock = clock ?? new SystemClock(),
            StationDepartureCountdownDispatcher = dispatcher,
            DisplayTimeZone = China
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

    internal sealed class ManualClock(DateTimeOffset now) : IClock
    {
        private long _ticks = now.UtcTicks;

        public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>一条跑着消息循环的线程，给定时器一个真的会触发的 Dispatcher。</summary>
    internal sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;

        public DispatcherThread()
        {
            using ManualResetEventSlim ready = new();
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            Dispatcher = dispatcher!;
        }

        public Dispatcher Dispatcher { get; }

        public void Dispose()
        {
            Dispatcher.InvokeShutdown();
            _thread.Join();
        }
    }
}
