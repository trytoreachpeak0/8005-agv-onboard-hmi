using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

public sealed class OnboardControllerTests
{
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

    private static OnboardController CreateController(FakeIoModule io, FakeRuleGateway rule)
    {
        return new OnboardController(
            io,
            rule,
            new FakeLogger(),
            new SystemClock(),
            new OnboardWorkflowOptions(
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromSeconds(2),
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                128,
                2));
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
