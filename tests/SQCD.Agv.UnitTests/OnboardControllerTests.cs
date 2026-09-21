using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

public sealed class OnboardControllerTests
{
    [Fact]
    public async Task ExternalSafetyGateNotReadyBlocksScanUnlockAndDeparture()
    {
        bool externalSafetyReady = false;
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-GATED");
        await using OnboardController controller = CreateController(io, rule, () => externalSafetyReady);

        await controller.StartAsync(TestContext.Current.CancellationToken);
        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(0, io.PulseCount);
        Assert.Empty(rule.Results);
        Assert.Equal(OnboardState.Connecting, controller.Current.State);
        Assert.Equal("WIRE_TO_GATE_NOT_READY", controller.Current.ErrorCode);
        Assert.False(controller.Current.DeparturePermitted);

        externalSafetyReady = true;
        controller.RefreshExternalSafetyState();
        Assert.Equal(OnboardState.ReadyToScan, controller.Current.State);
        Assert.True(controller.Current.DeparturePermitted);
    }

    /// <summary>
    /// 两句「未就绪」文案各自只说它覆盖的每一种状态下都成立的事，并且与 <see cref="OnboardCommandRejectionText"/> 里
    /// 同码那一份逐字相同（8005-agv-onboard-hmi#177）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// WIRE_TO_GATE_NOT_READY 覆盖「会话没 Ready」与「会话 Ready、车没停稳」。hmi#177 之后两支下扫码都被挡住（后一支由
    /// 业务服务的停稳门挡，<c>LoadCancellationBeforeSublotG2Tests.AMovingVehicleClosesTheEntryAndRefusesTheScanAndAStopBringsItBack</c>
    /// 钉着），发车由控制器挡（上面第一条），所以它说「禁止扫码与发车」，并说出「车辆尚未停稳」这个原因。
    /// </para>
    /// <para>
    /// WIRE_TO_GATE_JOURNEY_NOT_READY 覆盖的几支下扫码入口都没被挡
    /// （<c>LoadCancellationBeforeSublotG2Tests.AJourneyThatCannotAcceptASublotDoesNotCloseTheEntry</c> 钉着），所以它不声称
    /// 禁止任何事。
    /// </para>
    /// <para>
    /// 逐字相同：操作员从哪条路径看到这个码，读到的都该是同一句；改了一份忘了另一份，界面上就同时挂着一句真的和
    /// 一句假的。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NotReadyGuidanceSaysOnlyWhatHoldsInEveryStateItCovers()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-WORDING");
        await using (OnboardController notReady = CreateController(io, rule, () => false))
        {
            await notReady.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal("WIRE_TO_GATE_NOT_READY", notReady.Current.ErrorCode);
            Assert.Equal(OnboardCommandRejectionText.Describe("WIRE_TO_GATE_NOT_READY"), notReady.Current.Guidance);
            Assert.Contains("车辆尚未停稳", notReady.Current.Guidance, StringComparison.Ordinal);
            Assert.Contains("禁止扫码与发车", notReady.Current.Guidance, StringComparison.Ordinal);
        }

        await using OnboardController journeyNotReady = CreateController(
            new FakeIoModule(),
            new FakeRuleGateway(OperationType.Load, "OP-WORDING-JOURNEY"),
            () => true,
            () => WireToGateJourneySnapshot.Empty);
        await journeyNotReady.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal("WIRE_TO_GATE_JOURNEY_NOT_READY", journeyNotReady.Current.ErrorCode);
        Assert.Equal(
            OnboardCommandRejectionText.Describe("WIRE_TO_GATE_JOURNEY_NOT_READY"),
            journeyNotReady.Current.Guidance);
        Assert.DoesNotContain("禁止", journeyNotReady.Current.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalSafetyGateDropDuringVerificationPreventsPhysicalUnlock()
    {
        bool externalSafetyReady = true;
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-GATE-DROP") { PauseVerification = true };
        await using OnboardController controller = CreateController(io, rule, () => externalSafetyReady);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        Task submit = controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);
        await rule.VerificationStarted.Task;
        externalSafetyReady = false;
        controller.RefreshExternalSafetyState();
        rule.ReleaseVerification();
        await submit;

        Assert.Equal(0, io.PulseCount);
        OperationResult result = Assert.Single(rule.Results);
        Assert.False(result.Success);
        Assert.Equal("WIRE_TO_GATE_NOT_READY", result.FailureCode);
        Assert.Equal(OnboardState.Connecting, controller.Current.State);
        Assert.False(controller.Current.DeparturePermitted);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AuthoritativeJourneyMustContainMatchingSublotBeforeScan()
    {
        WireToGateJourneySnapshot journey = WireToGateJourneySnapshot.Empty;
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-JOURNEY");
        await using OnboardController controller = CreateController(
            io,
            rule,
            () => true,
            () => journey);

        await controller.StartAsync(TestContext.Current.CancellationToken);
        await controller.SubmitScanAsync("SUBLOT-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(0, io.PulseCount);
        Assert.Empty(rule.Results);
        Assert.Equal("WIRE_TO_GATE_JOURNEY_NOT_READY", controller.Current.ErrorCode);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        journey = new WireToGateJourneySnapshot(
            new WireToGateVehicleBusinessState(
                1,
                "READY",
                "TRANSPORT",
                false,
                "SUFFICIENT",
                "NOT_CHARGING",
                null,
                [],
                now,
                new string('a', 64)),
            new WireToGateCurrentStopWorklist(
                "ST-01",
                1,
                null,
                null,
                [new WireToGateWorklistItem(
                    "11111111-1111-1111-1111-111111111111",
                    "TD-001",
                    "SUBLOT-001",
                    "WIRE_TO_GATE",
                    "PICKUP",
                    1)],
                new string('b', 64)),
            null,
            now);
        controller.RefreshExternalSafetyState();
        await controller.SubmitScanAsync("WRONG-SUBLOT", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(0, io.PulseCount);
        Assert.Empty(rule.Results);
        Assert.Equal("SUBLOT_NOT_IN_WORKLIST", controller.Current.ErrorCode);
    }

    /// <summary>
    /// With two worklist items the entry check is membership of the items' sublot set: the second
    /// item's sublot goes on to verification, one outside the set is <c>SUBLOT_NOT_IN_WORKLIST</c>,
    /// and neither throws (batch 7-13, <c>8005-agv-onboard-hmi#134</c>).
    /// </summary>
    /// <remarks>
    /// Before batch 7 the check read <c>Items.SingleOrDefault()</c>, which throws on two items; the
    /// exception text would have been taken as the refusal reason.
    /// </remarks>
    [Fact]
    public async Task AJourneyOfTwoItemsAcceptsEitherSublotAndRefusesOneOutsideTheSet()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        WireToGateJourneySnapshot journey = new(
            new WireToGateVehicleBusinessState(
                1, "READY", "TRANSPORT", false, "SUFFICIENT", "NOT_CHARGING", null, [], now, new string('a', 64)),
            new WireToGateCurrentStopWorklist(
                "ST-01",
                1,
                null,
                null,
                [
                    new WireToGateWorklistItem(
                        "11111111-1111-1111-1111-111111111111", "TD-001", "SUBLOT-001", "WIRE_TO_GATE", "PICKUP", 1),
                    new WireToGateWorklistItem(
                        "22222222-2222-2222-2222-222222222222", "TD-002", "SUBLOT-002", "WIRE_TO_GATE", "PICKUP", 1)
                ],
                new string('b', 64)),
            null,
            now);
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-TWO-ITEMS");
        await using OnboardController controller = CreateController(io, rule, () => true, () => journey);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.SubmitScanAsync("WRONG-SUBLOT", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(0, io.PulseCount);
        Assert.Equal("SUBLOT_NOT_IN_WORKLIST", controller.Current.ErrorCode);

        await controller.SubmitScanAsync("SUBLOT-002", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(1, io.PulseCount);
        Assert.True(Assert.Single(rule.Results).Success);
    }

    [Fact]
    public async Task LoadFlowUnlocksOnceAndReportsSuccess()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-LOAD-001");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(1, io.PulseCount);
        OperationResult result = Assert.Single(rule.Results);
        Assert.True(result.Success);
        Assert.True(result.FinalLocker.IsLocked);
        Assert.True(result.FinalLocker.HasCargo);
        Assert.Equal(OnboardState.ReadyToScan, controller.Current.State);
        Assert.True(controller.Current.DeparturePermitted);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-REQUEST-FIRST-RESULT-REPLAY")]
    public async Task DuplicateOperationIdDoesNotUnlockAgain()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-DUPLICATE");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);
        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(1, io.PulseCount);
        Assert.Equal("DUPLICATE_OPERATION", controller.Current.ErrorCode);
    }

    [Fact]
    public async Task UnlockFeedbackTimeoutDoesNotRetryPulseAndReportsFailure()
    {
        FakeIoModule io = new() { FailUnlockFeedback = true };
        FakeRuleGateway rule = new(OperationType.Load, "OP-TIMEOUT");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(1, io.PulseCount);
        OperationResult result = Assert.Single(rule.Results);
        Assert.False(result.Success);
        Assert.Equal("UNLOCK_FEEDBACK_TIMEOUT", result.FailureCode);
        Assert.Equal(OnboardState.Faulted, controller.Current.State);
        Assert.Contains("1号仓", controller.Current.Guidance);
        Assert.DoesNotContain("UNLOCK_FEEDBACK_TIMEOUT", controller.Current.Guidance);
    }

    [Fact]
    public async Task ScanWithoutActiveVisitDoesNotUnlock()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-001") { PublishVisit = false };
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(0, io.PulseCount);
        Assert.Empty(rule.Results);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("ProtocolVector", "CV-DESTINATION-UNLOAD-ALL-EMPTY")]
    public async Task UnloadFlowRequiresCargoAndReportsEmptySlot()
    {
        FakeIoModule io = new() { FinalHasCargo = false };
        FakeRuleGateway rule = new(OperationType.Unload, "OP-UNLOAD-001");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);
        io.SetCargo(slotIndex: 0, hasCargo: true);

        await controller.SubmitScanAsync("UNLOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(1, io.PulseCount);
        OperationResult result = Assert.Single(rule.Results);
        Assert.True(result.Success);
        Assert.False(result.FinalLocker.HasCargo);
        Assert.True(result.FinalLocker.IsLocked);
    }

    [Fact]
    public async Task InvalidSlotIndexDoesNotWriteOutput()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-INVALID-SLOT") { SlotIndex = 8 };
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(0, io.PulseCount);
        Assert.Equal("INVALID_RULE_RESPONSE", controller.Current.ErrorCode);
    }

    [Fact]
    public async Task ResultKeepsVisitFromOperationStartWhenCurrentVisitChanges()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-VISIT");
        io.BeforeFinalFeedback = () => rule.ReplaceVisit("VISIT-NEW");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        OperationResult result = Assert.Single(rule.Results);
        Assert.Equal("VISIT-001", result.VisitId);
    }

    [Fact]
    public async Task StartupWithLockedCargoCanBeRecoveredByControlledReview()
    {
        FakeIoModule io = new();
        io.SetCargo(0, hasCargo: true);
        FakeRuleGateway rule = new(OperationType.Unload, "OP-RECOVERY");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal("STARTUP_STATE_UNSAFE", controller.Current.ErrorCode);
        Assert.Contains("仓内已有货物", controller.Current.Guidance);
        Assert.DoesNotContain("DO=", controller.Current.Guidance);
        Assert.DoesNotContain("DI=", controller.Current.Guidance);

        bool recovered = await controller.ConfirmSafeStartupStateAsync(TestContext.Current.CancellationToken);

        Assert.True(recovered);
        Assert.Equal(OnboardState.ReadyToScan, controller.Current.State);
        Assert.True(controller.Current.Io.GetLocker(0).HasCargo);
    }

    [Fact]
    public async Task RuleTimeoutIsReportedAsRequestTimeoutWithoutUnlock()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-RULE-TIMEOUT") { FailVerificationWithTimeout = true };
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(0, io.PulseCount);
        Assert.Equal("REQUEST_TIMEOUT", controller.Current.ErrorCode);
        Assert.Equal("任务确认超时，请稍后重新扫描；持续出现请联系维护人员。", controller.Current.Guidance);
    }

    [Fact]
    public async Task ModbusWriteTimeoutIsReportedAsIoWriteFailedWithoutRetry()
    {
        FakeIoModule io = new() { FailPulseWithTimeout = true };
        FakeRuleGateway rule = new(OperationType.Load, "OP-WRITE-TIMEOUT");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(1, io.PulseCount);
        Assert.Equal("IO_WRITE_FAILED", controller.Current.ErrorCode);
        Assert.False(Assert.Single(rule.Results).Success);
        Assert.Contains("1号仓发出开门指令", controller.Current.Guidance);
    }

    [Fact]
    public async Task UnlockOutputThatDoesNotResetCannotBeReportedAsSuccess()
    {
        FakeIoModule io = new() { KeepUnlockOutputActive = true };
        FakeRuleGateway rule = new(OperationType.Load, "OP-OUTPUT-STUCK");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(1, io.PulseCount);
        OperationResult result = Assert.Single(rule.Results);
        Assert.False(result.Success);
        Assert.Equal("UNLOCK_OUTPUT_RESET_TIMEOUT", result.FailureCode);
        Assert.Equal(OnboardState.Faulted, controller.Current.State);
        Assert.False(controller.Current.DeparturePermitted);
        Assert.Contains("开门控制未按时复位", controller.Current.Guidance);
    }

    [Fact]
    public async Task FatalFaultCancelsVerificationAndCannotBeOverwritten()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-FATAL")
        {
            PauseVerification = true
        };
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        Task submitTask = controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);
        await rule.VerificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        controller.EnterFatalFault("UI_FATAL_TEST", "测试严重安全故障，禁止继续操作。");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await submitTask.ConfigureAwait(false));

        Assert.Equal(0, io.PulseCount);
        Assert.Equal(OnboardState.Faulted, controller.Current.State);
        Assert.Equal("UI_FATAL_TEST", controller.Current.ErrorCode);
        Assert.Equal("测试严重安全故障，禁止继续操作。", controller.Current.Guidance);
        Assert.False(controller.Current.DeparturePermitted);
    }

    /// <summary>
    /// The entry added by 8005-agv-onboard-hmi#171 must not appear in a run where nothing went wrong.
    /// It is offered only while a clearable fatal fault stands, and a normal run never latches one --
    /// so if this ever goes red, the entry's condition has been written too wide.
    /// </summary>
    [Fact]
    public async Task ANormalRunNeverOffersTheFatalFaultClearance()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-NO-FAULT");
        await using OnboardController controller = CreateController(io, rule);
        Assert.False(controller.CanClearFatalFault);

        await controller.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(controller.CanClearFatalFault);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(rule.Results).Success);
        Assert.False(controller.CanClearFatalFault);
    }

    /// <summary>
    /// The clearance is a door review, not a dismiss button: it lifts the latch only once the IO says
    /// what it has to say. Both halves are asserted, because "latch is null afterwards" alone would
    /// also be true of an implementation that cleared unconditionally.
    /// </summary>
    [Fact]
    public async Task ClearingAFatalFaultNeedsTheDoorReviewToPassAndThenLetsScanningResume()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-CLEARED");
        FakeLogger logger = new();
        List<LogEntry> entries = [];
        logger.EntryWritten += (_, args) => entries.Add(args.Entry);
        await using OnboardController controller = CreateController(io, rule, logger: logger);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        controller.EnterFatalFault("UI_COMMAND_FAILED", "操作界面出现异常，本界面已禁止扫码开门。");
        Assert.True(controller.CanClearFatalFault);

        // 3号仓门没关好：复核不成立，锁存保留。
        io.SetUnlocked(2);
        Assert.False(await controller.ClearFatalFaultAsync("OP-7", TestContext.Current.CancellationToken));
        Assert.Equal(OnboardState.Faulted, controller.Current.State);
        Assert.Equal("UI_COMMAND_FAILED", controller.Current.ErrorCode);
        // 原横幅还在，后面多了一句为什么没过 -- 现场线踩过的那个坑：原因若只是发布出去，
        // PublishCore 会立刻把它改写回原横幅。
        Assert.Contains("本界面已禁止扫码开门", controller.Current.Guidance);
        Assert.Contains("3号仓门未关好", controller.Current.Guidance);
        Assert.True(controller.CanClearFatalFault);

        io.SetLocked(2);
        Assert.True(await controller.ClearFatalFaultAsync("OP-7", TestContext.Current.CancellationToken));

        Assert.False(controller.CanClearFatalFault);
        Assert.NotEqual(OnboardState.Faulted, controller.Current.State);
        Assert.Equal(OnboardState.ReadyToScan, controller.Current.State);
        Assert.Contains(
            entries,
            entry => entry.Severity == LogSeverity.Warning
                && entry.Message.Contains("严重安全故障已复位", StringComparison.Ordinal)
                && entry.Message.Contains("UI_COMMAND_FAILED", StringComparison.Ordinal)
                && entry.Message.Contains("OP-7", StringComparison.Ordinal));

        // 复位之后回到的是可以继续作业的状态，不是一个看起来正常、实际不受理扫码的空壳。
        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(rule.Results).Success);
    }

    /// <summary>
    /// UNHANDLED_UI_ERROR is registered as terminal: after an exception from nowhere, this process's
    /// own view of the doors is what is in doubt, so it is not the thing to judge the review.
    /// </summary>
    [Fact]
    public async Task ATerminalFatalFaultIsNotClearedOnTheVehicle()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-TERMINAL");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        controller.EnterFatalFault("UNHANDLED_UI_ERROR", "软件运行异常，本界面已禁止继续操作。");

        Assert.False(controller.CanClearFatalFault);
        // 每一项物理判据都成立，仍然不给复位 -- 拒绝的理由只能是这个码本身。
        Assert.False(await controller.ClearFatalFaultAsync("OP-7", TestContext.Current.CancellationToken));
        Assert.Equal(OnboardState.Faulted, controller.Current.State);
        Assert.Equal("UNHANDLED_UI_ERROR", controller.Current.ErrorCode);
        Assert.Contains("不能在车上复位", controller.Current.Guidance);
        AssertStillLatched(controller, "UNHANDLED_UI_ERROR");
    }

    [Fact]
    public async Task ClearingWithNothingLatchedDoesNothing()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-NO-LATCH");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);
        OnboardSnapshot before = controller.Current;

        Assert.False(await controller.ClearFatalFaultAsync("OP-7", TestContext.Current.CancellationToken));

        Assert.Equal(before.State, controller.Current.State);
        Assert.Equal(before.Guidance, controller.Current.Guidance);
    }

    /// <summary>
    /// v2 那一半：仓位操作跑在本控制器之外，复位必须问得到它（8005-agv-onboard-hmi#171 审查 S2）。
    /// </summary>
    /// <remarks>
    /// 下面那条 <see cref="ClearingIsRefusedWhileASlotOperationIsStillRunning"/> 走的是
    /// <c>SubmitScanAsync</c> 的操作锁，那是 MVP 路径——**在 v2 上它永远拿得到，所以那条判据
    /// 在 v2 上是空的，而那条测试照样绿**。两条覆盖的不是同一件事，删任何一条都会留下一个洞。
    ///
    /// 这里 IO 快照全部安全（门全锁、开锁输出全 0、快照新鲜），所以拒绝的理由只可能是在途查询——
    /// 不这么摆的话，物理复核也会拒，这条测试就分不出「在途判据存在」和「门碰巧开着」。
    /// </remarks>
    [Fact]
    public async Task ClearingIsRefusedWhileTheWireToGateExecutorIsStillOpeningADoor()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-PEER-BUSY");
        bool peerBusy = true;
        await using OnboardController controller = CreateController(
            io,
            rule,
            peerSlotWorkInFlightProvider: () => peerBusy);
        await controller.StartAsync(TestContext.Current.CancellationToken);
        controller.EnterFatalFault("UI_COMMAND_FAILED", "操作界面出现异常，本界面已禁止扫码开门。");

        Assert.False(await controller.ClearFatalFaultAsync("OP-7", TestContext.Current.CancellationToken));

        Assert.Contains("当前装卸操作尚未结束", controller.Current.Guidance);
        AssertStillLatched(controller, "UI_COMMAND_FAILED");

        // 对端空下来之后，同一次复核就过了 -- 证明挡住它的是在途查询，不是别的判据。
        peerBusy = false;
        Assert.True(await controller.ClearFatalFaultAsync("OP-7", TestContext.Current.CancellationToken));
        Assert.NotEqual(OnboardState.Faulted, controller.Current.State);
    }

    /// <summary>
    /// MVP 那一半：<c>SubmitScanAsync</c> 持有的操作锁。判据的理由被断言了，不只是 false——
    /// 操作进行中门是开的，物理复核本来也会拒，只断 false 的话这条测试在没有操作锁判据时照样绿。
    /// </summary>
    [Fact]
    public async Task ClearingIsRefusedWhileASlotOperationIsStillRunning()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-BUSY-CLEAR");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        bool? clearedMidOperation = null;
        string? guidanceMidOperation = null;
        io.BeforeFinalFeedback = () =>
        {
            // 这个钩子在 SubmitScanAsync 持有操作锁期间同步调用，所以「锁被占」是确定的，不靠时序碰运气。
            controller.EnterFatalFault("UI_COMMAND_FAILED", "操作界面出现异常，本界面已禁止扫码开门。");
            clearedMidOperation = controller.ClearFatalFaultAsync("OP-7").GetAwaiter().GetResult();
            guidanceMidOperation = controller.Current.Guidance;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await controller.SubmitScanAsync(
                "LOAD-001",
                ScanInputMethod.Scanner,
                TestContext.Current.CancellationToken).ConfigureAwait(false));

        Assert.False(clearedMidOperation);
        Assert.Contains("当前装卸操作尚未结束", guidanceMidOperation);
        AssertStillLatched(controller, "UI_COMMAND_FAILED");
    }

    /// <summary>
    /// 锁存是不是真的还在。不能只看发布出来的状态：一次显式的 <c>Publish(Faulted, …)</c> 也能让界面
    /// 看起来像锁着，而锁存已经被悄悄清掉——注入验证抓到过这个盲点。探针用的是锁存本身的性质：
    /// <c>EnterFatalFault</c> 第一次胜出，所以锁存还在时新的码进不来，被清掉了就会顶上去。
    /// </summary>
    private static void AssertStillLatched(OnboardController controller, string expectedCode)
    {
        controller.EnterFatalFault("UI_FATAL_LATCH_PROBE", "探针：这条只有在锁存已被清掉时才会显示。");
        Assert.Equal(expectedCode, controller.Current.ErrorCode);
        Assert.Equal(OnboardState.Faulted, controller.Current.State);
    }

    [Fact]
    public async Task IoConnectionLossDuringOperationIsReportedAsSystemWideFault()
    {
        FakeIoModule io = new() { FailUnlockFeedbackWithIoDisconnect = true };
        FakeRuleGateway rule = new(OperationType.Load, "OP-IO-OFFLINE");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal("IO_OFFLINE", controller.Current.ErrorCode);
        Assert.Contains("所有仓位状态无法确认", controller.Current.Guidance);
        Assert.DoesNotContain("1号仓状态", controller.Current.Guidance);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AllUnknownIoStatesAreReportedWithoutImplyingSingleSlotFault()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-ALL-UNKNOWN");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);
        io.SetUnknown(Enumerable.Range(0, 8).ToArray());

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal("IO_STATE_UNKNOWN", controller.Current.ErrorCode);
        Assert.Contains("所有仓位状态无法确认", controller.Current.Guidance);
        Assert.DoesNotContain("1号仓状态", controller.Current.Guidance);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task SingleUnknownIoStateIdentifiesAffectedPhysicalSlot()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-ONE-UNKNOWN") { SlotIndex = 2 };
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);
        io.SetUnknown(2);

        await controller.SubmitScanAsync("LOAD-003", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal("IO_STATE_UNKNOWN", controller.Current.ErrorCode);
        Assert.Contains("3号仓状态无法确认", controller.Current.Guidance);
    }

    [Fact]
    public async Task ValidAuthorizationWithUnsafeSlotReportsPrecheckFailureWithoutUnlock()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-PRECHECK");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);
        io.SetCargo(0, hasCargo: true);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(0, io.PulseCount);
        OperationResult result = Assert.Single(rule.Results);
        Assert.False(result.Success);
        Assert.Equal("SLOT_NOT_EMPTY", result.FailureCode);
        Assert.Equal(OperationStage.Precheck, result.FailureStage);
    }

    [Fact]
    public async Task UnacknowledgedPrecheckFailureBlocksUntilAutomaticReportRetrySucceeds()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-PRECHECK-RETRY")
        {
            ResultAcknowledged = false
        };
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);
        io.SetCargo(0, hasCargo: true);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(OnboardState.Faulted, controller.Current.State);
        Assert.Equal("PRECHECK_RESULT_ACK_TIMEOUT", controller.Current.ErrorCode);
        Assert.True(controller.CanRetryPendingResult);
        Assert.Equal(0, io.PulseCount);

        rule.ResultAcknowledged = true;
        rule.SetConnection(false);
        rule.SetConnection(true);
        await WaitUntilAsync(() => controller.Current.State == OnboardState.ReadyToScan);

        Assert.Equal(2, rule.Results.Count);
        Assert.Equal(rule.Results[0].MessageId, rule.Results[1].MessageId);
        Assert.False(controller.CanRetryPendingResult);
        Assert.True(controller.Current.DeparturePermitted);
    }

    [Fact]
    public async Task PrecheckErrorRemainsVisibleAfterIoSnapshotRefresh()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Unload, "OP-NO-CARGO");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.SubmitScanAsync("UNLOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);
        io.RepublishSnapshot();

        Assert.Equal(OnboardState.ReadyToScan, controller.Current.State);
        Assert.Equal("SLOT_HAS_NO_CARGO", controller.Current.ErrorCode);
        Assert.Equal("1号仓当前为空，不能执行取货，请核对任务。", controller.Current.Guidance);
    }

    [Fact]
    public async Task PrematureCloseDuringLoadCanReopenAndComplete()
    {
        FakeIoModule io = new();
        io.QueueCargoOnClose(false, true);
        FakeRuleGateway rule = new(OperationType.Load, "OP-LOAD-REOPEN");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        Task submitTask = controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => controller.CanReopenCurrentOperation);

        Assert.Equal(OperationStage.WaitingOperatorRecovery, controller.Current.ActiveOperation?.Stage);
        Assert.Contains("尚未检测到货物", controller.Current.Guidance);
        Assert.True(controller.RequestReopenCurrentOperation());
        await submitTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, io.PulseCount);
        OperationResult result = Assert.Single(rule.Results);
        Assert.True(result.Success);
        Assert.True(result.FinalLocker.HasCargo);
        Assert.Equal(OnboardState.ReadyToScan, controller.Current.State);
    }

    [Fact]
    public async Task PrematureCloseDuringUnloadCanReopenAndComplete()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Unload, "OP-UNLOAD-REOPEN");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);
        io.SetCargo(0, hasCargo: true);
        io.QueueCargoOnClose(true, false);

        Task submitTask = controller.SubmitScanAsync("UNLOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => controller.CanReopenCurrentOperation);

        Assert.Contains("仍检测到货物", controller.Current.Guidance);
        Assert.True(controller.RequestReopenCurrentOperation());
        await submitTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, io.PulseCount);
        OperationResult result = Assert.Single(rule.Results);
        Assert.True(result.Success);
        Assert.False(result.FinalLocker.HasCargo);
    }

    [Fact]
    public async Task PrematureCloseCanBeSafelyCancelledAndReported()
    {
        FakeIoModule io = new();
        io.QueueCargoOnClose(false);
        FakeRuleGateway rule = new(OperationType.Load, "OP-LOAD-CANCEL");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        Task submitTask = controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => controller.CanCancelCurrentOperation);

        Assert.True(controller.RequestCancelCurrentOperation());
        await submitTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        OperationResult result = Assert.Single(rule.Results);
        Assert.False(result.Success);
        Assert.Equal("OPERATION_CANCELLED_BY_OPERATOR", result.FailureCode);
        Assert.Equal(OnboardState.ReadyToScan, controller.Current.State);
        Assert.Null(controller.Current.ErrorCode);
        Assert.True(controller.Current.DeparturePermitted);
        Assert.Null(controller.Current.ActiveOperation);
    }

    [Fact]
    public async Task DelayedCargoFeedbackCompletesWithoutReopening()
    {
        FakeIoModule io = new();
        io.QueueCargoOnClose(false);
        FakeRuleGateway rule = new(OperationType.Load, "OP-DELAYED-CARGO");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        Task submitTask = controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => controller.CanCancelCurrentOperation);
        io.SetCargo(0, hasCargo: true);
        await submitTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, io.PulseCount);
        Assert.True(Assert.Single(rule.Results).Success);
        Assert.Equal(OnboardState.ReadyToScan, controller.Current.State);
    }

    [Fact]
    public async Task CancelWithoutRuleAcknowledgementRemainsBlocking()
    {
        FakeIoModule io = new();
        io.QueueCargoOnClose(false);
        FakeRuleGateway rule = new(OperationType.Load, "OP-CANCEL-NO-ACK")
        {
            ResultAcknowledged = false
        };
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        Task submitTask = controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => controller.CanCancelCurrentOperation);
        Assert.True(controller.RequestCancelCurrentOperation());
        await submitTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(OnboardState.Faulted, controller.Current.State);
        Assert.Equal("CANCEL_RESULT_ACK_TIMEOUT", controller.Current.ErrorCode);
        Assert.False(controller.Current.DeparturePermitted);
        Assert.NotNull(controller.Current.ActiveOperation);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-CONNECTION-LOSS-SAFE-FINISH")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-SAME-CONTENT")]
    public async Task SuccessfulResultIsRetriedWithSameMessageAfterConnectionRecovers()
    {
        FakeIoModule io = new();
        FakeRuleGateway rule = new(OperationType.Load, "OP-RESULT-RETRY")
        {
            ResultAcknowledged = false
        };
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);

        Assert.Equal(OnboardState.Faulted, controller.Current.State);
        Assert.Equal("RESULT_ACK_TIMEOUT", controller.Current.ErrorCode);
        Assert.True(controller.CanRetryPendingResult);
        Assert.Single(rule.Results);

        rule.ResultAcknowledged = true;
        rule.SetConnection(false);
        rule.SetConnection(true);
        await WaitUntilAsync(() => controller.Current.State == OnboardState.ReadyToScan);

        Assert.Equal(2, rule.Results.Count);
        Assert.Equal(rule.Results[0].MessageId, rule.Results[1].MessageId);
        Assert.Equal(1, io.PulseCount);
        Assert.Null(controller.Current.ActiveOperation);
        Assert.False(controller.CanRetryPendingResult);
        Assert.True(controller.Current.DeparturePermitted);
    }

    [Fact]
    public async Task ReopenIsLimitedAndOtherScansRemainBlocked()
    {
        FakeIoModule io = new();
        io.QueueCargoOnClose(false, false, false);
        FakeRuleGateway rule = new(OperationType.Load, "OP-LOAD-REOPEN-LIMIT");
        await using OnboardController controller = CreateController(io, rule);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        Task submitTask = controller.SubmitScanAsync("LOAD-001", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => controller.CanReopenCurrentOperation);

        await controller.SubmitScanAsync("LOAD-002", ScanInputMethod.Scanner, TestContext.Current.CancellationToken);
        Assert.Equal(1, io.PulseCount);
        Assert.Equal("EARLY_DOOR_CLOSED", controller.Current.ErrorCode);
        Assert.Contains("重新打开仓门", controller.Current.Guidance);

        Assert.True(controller.RequestReopenCurrentOperation());
        await WaitUntilAsync(() => controller.Current.ActiveOperation?.ReopenAttempts == 1
            && controller.CanReopenCurrentOperation);
        Assert.True(controller.RequestReopenCurrentOperation());
        await WaitUntilAsync(() => controller.Current.ActiveOperation?.ReopenAttempts == 2
            && controller.CanCancelCurrentOperation);

        Assert.False(controller.CanReopenCurrentOperation);
        Assert.False(controller.RequestReopenCurrentOperation());
        Assert.Contains("次数已经用完", controller.Current.Guidance);
        Assert.True(controller.RequestCancelCurrentOperation());
        await submitTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(3, io.PulseCount);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private static OnboardController CreateController(
        FakeIoModule io,
        FakeRuleGateway rule,
        Func<bool>? externalSafetyReadyProvider = null,
        Func<WireToGateJourneySnapshot?>? journeyProvider = null,
        FakeLogger? logger = null,
        Func<bool>? peerSlotWorkInFlightProvider = null)
    {
        return new OnboardController(
            io,
            rule,
            logger ?? new FakeLogger(),
            new SystemClock(),
            new OnboardWorkflowOptions(
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromSeconds(2),
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                128,
                2),
            externalSafetyReadyProvider,
            journeyProvider,
            peerSlotWorkInFlightProvider);
    }

    private sealed class FakeIoModule : IIoModuleClient
    {
        private IoSnapshot _snapshot = CreateSafeSnapshot();
        private int _waitCount;
        private readonly Queue<bool> _cargoOnCloseSequence = new();
        private bool _beforeFinalFeedbackInvoked;

        public bool FailUnlockFeedback { get; init; }

        public bool FinalHasCargo { get; init; } = true;

        public bool FailPulseWithTimeout { get; init; }

        public bool FailUnlockFeedbackWithIoDisconnect { get; init; }

        public bool KeepUnlockOutputActive { get; init; }

        public Action? BeforeFinalFeedback { get; set; }

        public int PulseCount { get; private set; }

        public bool IsConnected => CurrentSnapshot.IsConnected;

        public IoSnapshot CurrentSnapshot => _snapshot;

        public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

        public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged;

        public Task StartAsync(CancellationToken applicationStopping)
        {
            ConnectionChanged?.Invoke(this, new ValueChangedEventArgs<bool>(true));
            SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(_snapshot));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken)
        {
            PulseCount++;
            if (FailPulseWithTimeout)
            {
                throw new TimeoutException("模拟Modbus写超时");
            }

            bool currentCargo = _snapshot.GetLocker(slotIndex).HasCargo;
            _snapshot = ReplaceLocker(
                _snapshot,
                slotIndex,
                isLocked: false,
                hasCargo: currentCargo,
                unlockOutputActive: KeepUnlockOutputActive);
            SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(_snapshot));
            return Task.CompletedTask;
        }

        public async Task<LockerSnapshot> WaitForLockerAsync(
            int slotIndex,
            Func<LockerSnapshot, bool> predicate,
            TimeSpan timeout,
            TimeSpan stableWindow,
            CancellationToken cancellationToken)
        {
            _waitCount++;
            if (_waitCount == 1 && FailUnlockFeedbackWithIoDisconnect)
            {
                _snapshot = _snapshot with { IsConnected = false, ObservedAt = DateTimeOffset.Now };
                ConnectionChanged?.Invoke(this, new ValueChangedEventArgs<bool>(false));
                throw new IOException("模拟仓门控制设备连接中断");
            }

            if (_waitCount == 1 && FailUnlockFeedback)
            {
                throw new TimeoutException("模拟开锁反馈超时");
            }

            LockerSnapshot current = _snapshot.GetLocker(slotIndex);
            bool keepWaitingForStuckOutput = _waitCount == 2 && KeepUnlockOutputActive;
            if (!predicate(current) && !current.IsLocked && !keepWaitingForStuckOutput)
            {
                if (!_beforeFinalFeedbackInvoked)
                {
                    _beforeFinalFeedbackInvoked = true;
                    BeforeFinalFeedback?.Invoke();
                }

                bool cargo = _cargoOnCloseSequence.Count > 0
                    ? _cargoOnCloseSequence.Dequeue()
                    : FinalHasCargo;
                _snapshot = ReplaceLocker(_snapshot, slotIndex, isLocked: true, hasCargo: cargo);
                SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(_snapshot));
            }

            using CancellationTokenSource timeoutCts = new(timeout);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    LockerSnapshot locker = _snapshot.GetLocker(slotIndex);
                    if (predicate(locker))
                    {
                        if (stableWindow > TimeSpan.Zero)
                        {
                            await Task.Delay(stableWindow, linked.Token);
                            locker = _snapshot.GetLocker(slotIndex);
                            if (!predicate(locker))
                            {
                                continue;
                            }
                        }

                        return locker;
                    }

                    await Task.Delay(2, linked.Token);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("模拟等待仓位反馈超时");
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void SetCargo(int slotIndex, bool hasCargo)
        {
            _snapshot = ReplaceLocker(_snapshot, slotIndex, isLocked: true, hasCargo: hasCargo);
            SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(_snapshot));
        }

        public void RepublishSnapshot()
        {
            SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(_snapshot));
        }

        public void QueueCargoOnClose(params bool[] cargoStates)
        {
            foreach (bool cargo in cargoStates)
            {
                _cargoOnCloseSequence.Enqueue(cargo);
            }
        }

        /// <summary>仓门反馈变成「未锁好」，其余不动。复位复核的判据之一。</summary>
        public void SetUnlocked(int slotIndex) => SetLockFeedback(slotIndex, false);

        /// <summary>仓门反馈变回「已锁好」。</summary>
        public void SetLocked(int slotIndex) => SetLockFeedback(slotIndex, true);

        private void SetLockFeedback(int slotIndex, bool locked)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            LockerSnapshot[] lockers = _snapshot.Lockers
                .Select(locker => locker.SlotIndex == slotIndex
                    ? locker with { LockFeedbackRaw = locked, ObservedAt = now }
                    : locker with { ObservedAt = now })
                .ToArray();
            _snapshot = new IoSnapshot(true, lockers, now);
            SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(_snapshot));
        }

        public void SetUnknown(params int[] slotIndexes)
        {
            HashSet<int> targets = slotIndexes.ToHashSet();
            DateTimeOffset now = DateTimeOffset.Now;
            LockerSnapshot[] lockers = _snapshot.Lockers
                .Select(locker => targets.Contains(locker.SlotIndex)
                    ? LockerSnapshot.Unknown(locker.SlotIndex, now)
                    : locker)
                .ToArray();
            _snapshot = new IoSnapshot(true, lockers, now);
            SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(_snapshot));
        }

        private static IoSnapshot CreateSafeSnapshot()
        {
            DateTimeOffset now = DateTimeOffset.Now;
            return new IoSnapshot(
                true,
                Enumerable.Range(0, 8)
                    .Select(index => new LockerSnapshot(index, index + 1, false, true, true, now))
                    .ToArray(),
                now);
        }

        private static IoSnapshot ReplaceLocker(
            IoSnapshot snapshot,
            int slotIndex,
            bool isLocked,
            bool hasCargo,
            bool unlockOutputActive = false)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            LockerSnapshot[] lockers = snapshot.Lockers
                .Select(locker => locker.SlotIndex == slotIndex
                    ? locker with
                    {
                        UnlockOutputRaw = unlockOutputActive,
                        LockFeedbackRaw = isLocked,
                        LightCurtainRaw = !hasCargo,
                        ObservedAt = now
                    }
                    : locker)
                .ToArray();
            return new IoSnapshot(true, lockers, now);
        }
    }

    private sealed class FakeRuleGateway(OperationType operationType, string operationId) : IRuleGateway
    {
        private readonly OperationType _operationType = operationType;
        private readonly string _operationId = operationId;

        public bool PublishVisit { get; init; } = true;

        public int SlotIndex { get; init; }

        public bool FailVerificationWithTimeout { get; init; }

        public bool PauseVerification { get; init; }

        public bool ResultAcknowledged { get; set; } = true;

        public TaskCompletionSource<bool> VerificationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<bool> VerificationRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<OperationResult> Results { get; } = [];

        public void ReleaseVerification() => VerificationRelease.TrySetResult(true);

        public bool IsConnected { get; private set; }

        public VisitContext? CurrentVisit { get; private set; }

        public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

        public event EventHandler<ValueChangedEventArgs<VisitContext?>>? VisitChanged;

        public Task StartAsync(CancellationToken applicationStopping)
        {
            IsConnected = true;
            ConnectionChanged?.Invoke(this, new ValueChangedEventArgs<bool>(true));
            if (PublishVisit)
            {
                CurrentVisit = new VisitContext("VISIT-001", "ST-01", "测试站点", true, DateTimeOffset.Now.AddHours(1));
                VisitChanged?.Invoke(this, new ValueChangedEventArgs<VisitContext?>(CurrentVisit));
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<ScanAuthorization> VerifyScanAsync(
            ScanVerificationRequest request,
            CancellationToken cancellationToken)
        {
            if (FailVerificationWithTimeout)
            {
                throw new TimeoutException("模拟规则核验超时");
            }

            if (PauseVerification)
            {
                VerificationStarted.TrySetResult(true);
                await VerificationRelease.Task.WaitAsync(cancellationToken);
            }

            return new ScanAuthorization(
                true,
                _operationId,
                "TASK-001",
                request.Sublot,
                SlotIndex,
                _operationType,
                _operationType == OperationType.Load,
                null,
                null);
        }

        public Task<bool> ReportOperationAsync(OperationResult result, CancellationToken cancellationToken)
        {
            Results.Add(result);
            return Task.FromResult(ResultAcknowledged);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void ReplaceVisit(string visitId)
        {
            CurrentVisit = new VisitContext(visitId, "ST-02", "新站点", true, DateTimeOffset.Now.AddHours(1));
            VisitChanged?.Invoke(this, new ValueChangedEventArgs<VisitContext?>(CurrentVisit));
        }

        public void SetConnection(bool connected)
        {
            IsConnected = connected;
            ConnectionChanged?.Invoke(this, new ValueChangedEventArgs<bool>(connected));
        }
    }

    private sealed class FakeLogger : IAppLogger
    {
        public event EventHandler<LogEntryEventArgs>? EntryWritten;

        public void Write(LogSeverity severity, string source, string message, Exception? exception = null)
        {
            EntryWritten?.Invoke(this, new LogEntryEventArgs(new LogEntry(DateTimeOffset.Now, severity, source, message)));
        }
    }
}
