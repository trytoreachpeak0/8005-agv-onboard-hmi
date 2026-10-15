using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 开锁后门没关时的「请关门」横幅（<c>trytoreachpeak0/8005-agv-onboard-hmi#354</c>）：10-15 生产两次因仓门开着没人关，
/// 车在卸货站一停十几二十分钟，屏上只有两行小字。走 <see cref="MainViewModel"/> 真实的刷新路径：门锁读数来自假 IO 模块，
/// 当前操作来自操作员事件，时钟由测试推进。
/// </summary>
/// <remarks>
/// <para>
/// <b>出现的条件是「本次操作的目标仓、门锁读数没锁上、阶段是开锁或等操作员」三样同时成立。</b>只看阶段不够：
/// 关门时货物不符而车辆安全未确认、暂不重开的那种等待（<c>ReopenHeldBySafety</c>）阶段同样是等操作员，门却已经锁上，
/// 这时叫人「取出篮子后关门」是错的。只看门锁也不够：不是本次目标的仓被手动打开，不该说成车在等它。
/// </para>
/// <para>
/// 文案逐字比对：操作员看到的每个字都要用户确认过（票面第 4 条）。不带 trait：纯界面呈现，不证明任何协议向量，
/// 理由同 <see cref="SlotGroupHighlightTests"/>。
/// </para>
/// </remarks>
public sealed class DoorOpenPromptViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnUnloadDoorLeftOpenShowsWhichDoorAndWhatToDo()
    {
        FakeIoModuleClient io = new();
        await using OnboardController controller = Controller(io);
        MainViewModel viewModel = await ViewModel(controller, new ManualClock(Now));
        Assert.False(viewModel.HasDoorOpenPrompt);

        OpenDoors(io, 3);
        viewModel.ApplyWireToGateOperatorEvent(Operation(OperationType.Unload, WireToGateHmiOperationStage.WaitingOperator, 3));

        Assert.True(viewModel.HasDoorOpenPrompt);
        Assert.Equal(DoorOpenPromptTier.Waiting, viewModel.DoorOpenPromptTier);
        Assert.Equal("3号仓门开着：请取出篮子，然后关好仓门", viewModel.DoorOpenPromptHeadline);
        Assert.Equal("门关好、车确认仓空后，才算这一仓卸完。", viewModel.DoorOpenPromptDetail);
        Assert.False(viewModel.DoorOpenPromptDimmed);
    }

    [Fact]
    public async Task ALoadSaysPutTheBasketInAndNamesEveryOpenDoor()
    {
        FakeIoModuleClient io = new();
        await using OnboardController controller = Controller(io);
        MainViewModel viewModel = await ViewModel(controller, new ManualClock(Now));

        OpenDoors(io, 4, 3);
        viewModel.ApplyWireToGateOperatorEvent(Operation(OperationType.Load, WireToGateHmiOperationStage.WaitingOperator, 3, 4));

        Assert.Equal("3、4号仓门开着：请放入篮子，然后关好仓门", viewModel.DoorOpenPromptHeadline);
        Assert.Equal("门关好、车确认有货后，才算这一仓装完。", viewModel.DoorOpenPromptDetail);
    }

    /// <summary>逐仓卸货：命令点了三个仓，此刻只开着一个，横幅只说开着的那个。</summary>
    [Fact]
    public async Task OnlyTheTargetDoorsThatReadUnlockedAreNamed()
    {
        FakeIoModuleClient io = new();
        await using OnboardController controller = Controller(io);
        MainViewModel viewModel = await ViewModel(controller, new ManualClock(Now));

        OpenDoors(io, 2, 6);
        viewModel.ApplyWireToGateOperatorEvent(Operation(OperationType.Unload, WireToGateHmiOperationStage.WaitingOperator, 1, 2, 3));

        Assert.Equal("2号仓门开着：请取出篮子，然后关好仓门", viewModel.DoorOpenPromptHeadline);
    }

    /// <summary>关门时货物不符、安全未确认暂不重开：阶段仍是等操作员，但门已锁上，不能叫人去关门。</summary>
    [Fact]
    public async Task NoPromptWhileTheTargetDoorReadsLocked()
    {
        FakeIoModuleClient io = new();
        await using OnboardController controller = Controller(io);
        MainViewModel viewModel = await ViewModel(controller, new ManualClock(Now));

        viewModel.ApplyWireToGateOperatorEvent(Operation(OperationType.Unload, WireToGateHmiOperationStage.WaitingOperator, 3));

        Assert.False(viewModel.HasDoorOpenPrompt);
        Assert.Equal(DoorOpenPromptTier.None, viewModel.DoorOpenPromptTier);
        Assert.Equal(string.Empty, viewModel.DoorOpenPromptHeadline);
    }

    /// <summary>门开着但不是本次操作的目标仓，或者已经进入核对阶段：都不是「车在等关门」。</summary>
    [Fact]
    public async Task NoPromptForADoorOutsideTheOperationOrPastTheWaitingStages()
    {
        FakeIoModuleClient io = new();
        await using OnboardController controller = Controller(io);
        MainViewModel viewModel = await ViewModel(controller, new ManualClock(Now));
        OpenDoors(io, 5);

        viewModel.ApplyWireToGateOperatorEvent(Operation(OperationType.Unload, WireToGateHmiOperationStage.WaitingOperator, 3));
        Assert.False(viewModel.HasDoorOpenPrompt);

        viewModel.ApplyWireToGateOperatorEvent(Operation(OperationType.Unload, WireToGateHmiOperationStage.Verifying, 5));
        Assert.False(viewModel.HasDoorOpenPrompt);

        viewModel.ApplyWireToGateOperatorEvent(Operation(OperationType.Unload, WireToGateHmiOperationStage.Unlocking, 5));
        Assert.True(viewModel.HasDoorOpenPrompt);
    }

    [Fact]
    public async Task ClosingTheDoorTakesThePromptAway()
    {
        FakeIoModuleClient io = new();
        await using OnboardController controller = Controller(io);
        MainViewModel viewModel = await ViewModel(controller, new ManualClock(Now));
        OpenDoors(io, 3);
        viewModel.ApplyWireToGateOperatorEvent(Operation(OperationType.Unload, WireToGateHmiOperationStage.WaitingOperator, 3));
        Assert.True(viewModel.HasDoorOpenPrompt);

        io.CloseDoor(2, cargo: false);
        io.PublishSnapshot();

        Assert.False(viewModel.HasDoorOpenPrompt);
        Assert.Equal(string.Empty, viewModel.DoorOpenPromptDetail);
    }

    /// <summary>
    /// 60 秒前不升级，60 秒整起转红并写明车在等关门、门开了多久；秒数随时钟走，红底逐秒闪烁。
    /// </summary>
    [Fact]
    public async Task AfterSixtySecondsThePromptTurnsRedSaysTheVehicleIsWaitingAndBlinks()
    {
        FakeIoModuleClient io = new();
        ManualClock clock = new(Now);
        await using OnboardController controller = Controller(io);
        MainViewModel viewModel = await ViewModel(controller, clock);
        OpenDoors(io, 3);
        viewModel.ApplyWireToGateOperatorEvent(Operation(OperationType.Unload, WireToGateHmiOperationStage.WaitingOperator, 3));

        clock.Advance(TimeSpan.FromSeconds(59.75));
        viewModel.RefreshDoorOpenPrompt();
        Assert.Equal(DoorOpenPromptTier.Waiting, viewModel.DoorOpenPromptTier);
        Assert.False(viewModel.DoorOpenPromptDimmed);

        clock.Advance(TimeSpan.FromSeconds(0.25));
        viewModel.RefreshDoorOpenPrompt();
        Assert.Equal(DoorOpenPromptTier.Overdue, viewModel.DoorOpenPromptTier);
        Assert.Equal("3号仓门开着：请取出篮子，然后关好仓门", viewModel.DoorOpenPromptHeadline);
        Assert.Equal("车在等关门：3号仓门已开 1 分 00 秒。门不关好，车不会继续。", viewModel.DoorOpenPromptDetail);
        bool firstPhase = viewModel.DoorOpenPromptDimmed;

        clock.Advance(TimeSpan.FromSeconds(1));
        viewModel.RefreshDoorOpenPrompt();
        Assert.NotEqual(firstPhase, viewModel.DoorOpenPromptDimmed);

        clock.Advance(TimeSpan.FromSeconds(74));
        viewModel.RefreshDoorOpenPrompt();
        Assert.Equal("车在等关门：3号仓门已开 2 分 15 秒。门不关好，车不会继续。", viewModel.DoorOpenPromptDetail);
    }

    /// <summary>
    /// 计时从车载端第一次看到门开着算，不随同一次操作的重复进度事件重新计满；门关上再开、或换了一次操作，才重新计。
    /// </summary>
    [Fact]
    public async Task TheClockStartsWhenTheDoorIsFirstSeenOpenAndRestartsOnlyWhenItShutsOrTheAttemptChanges()
    {
        FakeIoModuleClient io = new();
        ManualClock clock = new(Now);
        await using OnboardController controller = Controller(io);
        MainViewModel viewModel = await ViewModel(controller, clock);
        OpenDoors(io, 3);
        viewModel.ApplyWireToGateOperatorEvent(Operation(OperationType.Unload, WireToGateHmiOperationStage.WaitingOperator, 3));

        clock.Advance(TimeSpan.FromSeconds(40));
        viewModel.ApplyWireToGateOperatorEvent(Operation(OperationType.Unload, WireToGateHmiOperationStage.WaitingOperator, 3));
        io.PublishSnapshot();
        clock.Advance(TimeSpan.FromSeconds(20));
        viewModel.RefreshDoorOpenPrompt();
        Assert.Equal(DoorOpenPromptTier.Overdue, viewModel.DoorOpenPromptTier);

        viewModel.ApplyWireToGateOperatorEvent(
            Operation(OperationType.Unload, WireToGateHmiOperationStage.WaitingOperator, 3, attemptId: "ATTEMPT-2"));
        Assert.Equal(DoorOpenPromptTier.Waiting, viewModel.DoorOpenPromptTier);

        clock.Advance(TimeSpan.FromSeconds(60));
        viewModel.RefreshDoorOpenPrompt();
        Assert.Equal(DoorOpenPromptTier.Overdue, viewModel.DoorOpenPromptTier);
        io.CloseDoor(2, cargo: false);
        io.PublishSnapshot();
        OpenDoors(io, 3);
        Assert.Equal(DoorOpenPromptTier.Waiting, viewModel.DoorOpenPromptTier);
    }

    [Fact]
    public async Task TheOperationEndingTakesThePromptAway()
    {
        FakeIoModuleClient io = new();
        await using OnboardController controller = Controller(io);
        MainViewModel viewModel = await ViewModel(controller, new ManualClock(Now));
        OpenDoors(io, 3);
        viewModel.ApplyWireToGateOperatorEvent(Operation(OperationType.Unload, WireToGateHmiOperationStage.WaitingOperator, 3));

        viewModel.ApplyWireToGateOperatorEvent(Operation(OperationType.Unload, WireToGateHmiOperationStage.Completed, 3));

        Assert.False(viewModel.HasDoorOpenPrompt);
    }

    /// <summary>门开着的仓（物理号）在假 IO 里读作没锁，并把读数发出去，等于真模块轮询到这一刻。</summary>
    private static void OpenDoors(FakeIoModuleClient io, params int[] physicalSlots)
    {
        foreach (int slot in physicalSlots)
        {
            io.OpenDoor(slot - 1);
        }

        io.PublishSnapshot();
    }

    private static WireToGateOperatorEvent Operation(
        OperationType type,
        WireToGateHmiOperationStage stage,
        params int[] slots) =>
        Operation(type, stage, slots, "ATTEMPT-1");

    private static WireToGateOperatorEvent Operation(
        OperationType type,
        WireToGateHmiOperationStage stage,
        int slot,
        string attemptId) =>
        Operation(type, stage, [slot], attemptId);

    private static WireToGateOperatorEvent Operation(
        OperationType type,
        WireToGateHmiOperationStage stage,
        int[] slots,
        string attemptId) =>
        new(Now, "OPERATION_PROGRESS", "进度", new WireToGateHmiOperationSnapshot(attemptId, type, slots, stage, "进度", Now));

    private static async Task<MainViewModel> ViewModel(OnboardController controller, IClock clock)
    {
        MainViewModel viewModel = new(
            controller,
            new RecordingLogger(),
            "agv02",
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()))
        {
            Clock = clock,
            StationDepartureCountdownDispatcher = null
        };
        await viewModel.InitializeAsync();
        return viewModel;
    }

    private static OnboardController Controller(FakeIoModuleClient io) => new(
        io,
        new IdleRuleGateway(),
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

    private sealed class ManualClock(DateTimeOffset now) : IClock
    {
        private long _ticks = now.UtcTicks;

        public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class IdleRuleGateway : IRuleGateway
    {
        public bool IsConnected => false;

        public VisitContext? CurrentVisit => null;

        public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

        public event EventHandler<ValueChangedEventArgs<VisitContext?>>? VisitChanged;

        public Task StartAsync(CancellationToken applicationStopping) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ScanAuthorization> VerifyScanAsync(
            ScanVerificationRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("本测试不扫码。");

        public Task<bool> ReportOperationAsync(OperationResult result, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("本测试不上报。");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
