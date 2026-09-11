using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

public sealed class WireToGateSlotOperationExecutorTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task LoadUsesOnlyServerFrozenSlotsAndJournalsBeforePulses()
    {
        string directory = Path.Combine(Path.GetTempPath(), "w2g-executor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string journalPath = Path.Combine(directory, "journal.db");
        SimulationIo io = new();
        await using SqliteWireToGateJournal journal = new(journalPath);
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        WireToGateSlotOperationExecutor executor = new(
            io,
            journal,
            new SystemClock(),
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1)));
        await using (executor)
        {
            WireToGateSlotOperationCommand command = new(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                1,
                DateTimeOffset.UtcNow,
                "11111111-1111-1111-1111-111111111111",
                "22222222-2222-2222-2222-222222222222",
                "33333333-3333-3333-3333-333333333333",
                OperationType.Load,
                [1, 2],
                2,
                true,
                new string('0', 64));

            WireToGateOperationExecutionResult result = await executor.ExecuteAsync(
                command,
                null,
                TestContext.Current.CancellationToken);

            Assert.Equal("COMPLETED", result.OverallOutcome);
            Assert.Equal([1, 2], result.SlotResults.Select(slot => slot.SlotNo));
            Assert.All(result.SlotResults, slot => Assert.Equal("COMPLETED", slot.Outcome));
            Assert.Equal(2, io.UnlockCount);
            WireToGateRecoveryState checkpoint = await journal.ReadRecoveryStateAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, checkpoint.ProvenRecoveryCheckpoint);
            Assert.Equal(command.SlotOperationAttemptId, checkpoint.UnsettledSlotOperationAttemptId);

            await executor.MarkResultRecordedAsync(
                command.SlotOperationAttemptId,
                TestContext.Current.CancellationToken);
            checkpoint = await journal.ReadRecoveryStateAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, checkpoint.ProvenRecoveryCheckpoint);
            Assert.Null(checkpoint.UnsettledSlotOperationAttemptId);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-04")]
    public async Task UnloadAllTargetSlotsRequiresEverySlotToReachEmpty()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            initialCargo: true,
            finalCargo: false,
            cancellationToken: TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Unload,
            [1, 2, 3, 4, 5, 6, 7, 8],
            expectedOccupied: false);

        WireToGateOperationExecutionResult result =
            await fixture.Executor.ExecuteAsync(
                command,
                null,
                TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(8, fixture.Io.UnlockCount);
        Assert.All(result.SlotResults, slot =>
        {
            Assert.Equal("EMPTY", slot.FinalPhysicalState);
            Assert.Equal("LOCKED", slot.LockState);
            Assert.Equal("RESET", slot.UnlockOutputState);
        });
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task UnknownSnapshotFailsClosedWithoutUnlock()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        fixture.Io.SetUnknown();
        WireToGateOperationExecutionResult result =
            await fixture.Executor.ExecuteAsync(
                CreateCommand(OperationType.Load, [1], expectedOccupied: true),
                null,
                TestContext.Current.CancellationToken);

        Assert.Equal("FAILED", result.OverallOutcome);
        Assert.Equal(0, fixture.Io.UnlockCount);
        Assert.Contains("SLOT_STATE_UNKNOWN", result.SlotResults.Single().ReasonCodes);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task ResumeSkipsSlotsAlreadyAtDesiredFinalState()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            initialCargo: false,
            finalCargo: true,
            cancellationToken: TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Load,
            [1, 2],
            expectedOccupied: true);
        WireToGateOperationExecutionResult first = await fixture.Executor.ExecuteAsync(
            command,
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal("COMPLETED", first.OverallOutcome);
        int unlocksBeforeResume = fixture.Io.UnlockCount;

        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        await fixture.Journal.WriteRecoveryStateAsync(state with
        {
            ExceptionRecoverySessionId = "44444444-4444-4444-8444-444444444444",
            RecoveryActionId = "55555555-5555-4555-8555-555555555555"
        }, TestContext.Current.CancellationToken);
        WireToGateSlotOperationResumeCommand resume = new(
            "66666666-6666-4666-8666-666666666666",
            2,
            DateTimeOffset.UtcNow,
            "44444444-4444-4444-8444-444444444444",
            "55555555-5555-4555-8555-555555555555",
            command.DemandId,
            command.SlotOperationAttemptId,
            WireToGateRecoveryCheckpoint.SafeFinishReached,
            command.Slots,
            command.CommandContentSha256);

        WireToGateOperationExecutionResult resumed = await fixture.Executor.ResumeAsync(
            resume,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", resumed.OverallOutcome);
        Assert.Equal(unlocksBeforeResume, fixture.Io.UnlockCount);
        Assert.All(resumed.SlotResults, slot => Assert.Equal("COMPLETED", slot.Outcome));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task ResumeWithoutOriginalContextFailsClosed()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        WireToGateSlotOperationResumeCommand resume = new(
            "66666666-6666-4666-8666-666666666666",
            2,
            DateTimeOffset.UtcNow,
            "44444444-4444-4444-8444-444444444444",
            "55555555-5555-4555-8555-555555555555",
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            WireToGateRecoveryCheckpoint.Prepared,
            [1],
            new string('0', 64));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Executor.ResumeAsync(
                resume,
                null,
                TestContext.Current.CancellationToken));

        Assert.Equal("RECOVERY_OPERATION_CONTEXT_MISSING", error.Message);
        Assert.Equal(0, fixture.Io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task ResumeRejectsDifferentCommandHashWithoutUnlock()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            initialCargo: false,
            finalCargo: true,
            cancellationToken: TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Load,
            [1],
            expectedOccupied: true);
        WireToGateOperationExecutionResult first = await fixture.Executor.ExecuteAsync(
            command,
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal("COMPLETED", first.OverallOutcome);

        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        await fixture.Journal.WriteRecoveryStateAsync(state with
        {
            ExceptionRecoverySessionId = "44444444-4444-4444-8444-444444444444",
            RecoveryActionId = "55555555-5555-4555-8555-555555555555"
        }, TestContext.Current.CancellationToken);
        WireToGateSlotOperationResumeCommand resume = new(
            "66666666-6666-4666-8666-666666666666",
            2,
            DateTimeOffset.UtcNow,
            "44444444-4444-4444-8444-444444444444",
            "55555555-5555-4555-8555-555555555555",
            command.DemandId,
            command.SlotOperationAttemptId,
            WireToGateRecoveryCheckpoint.SafeFinishReached,
            command.Slots,
            new string('1', 64));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Executor.ResumeAsync(
                resume,
                null,
                TestContext.Current.CancellationToken));

        Assert.Equal("RECOVERY_STATE_MISMATCH", error.Message);
        Assert.Equal(1, fixture.Io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task OppositeOccupancyReopensTheSlotInsteadOfFailingIt()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        List<(string Phase, int PromptRound)> phases = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (phase, active, completed, promptRound, token) =>
            {
                phases.Add((phase, promptRound));
                if (phase == "WAITING_OPERATOR")
                {
                    // 第一轮操作员关了门却什么都没放，第二轮才放料。
                    fixture.Io.CloseDoor(active[0] - 1, cargo: promptRound >= 1);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal("COMPLETED", result.SlotResults.Single().Outcome);
        Assert.Empty(result.SlotResults.Single().ReasonCodes);
        Assert.Equal(2, fixture.Io.UnlockCount(0));
        Assert.Equal(
            [("UNLOCKING", 0), ("WAITING_OPERATOR", 0), ("UNLOCKING", 1), ("WAITING_OPERATOR", 1)],
            phases.Where(item => item.Phase is "UNLOCKING" or "WAITING_OPERATOR").ToArray());
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task CompletedSlotIsNotReopenedWhileAnotherSlotKeepsReopening()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true),
            (phase, active, completed, promptRound, token) =>
            {
                if (phase == "WAITING_OPERATOR")
                {
                    int physicalSlot = active[0];
                    // 1 号仓一次到位，2 号仓来回折腾三轮。
                    fixture.Io.CloseDoor(
                        physicalSlot - 1,
                        cargo: physicalSlot == 1 || promptRound >= 2);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.All(result.SlotResults, slot => Assert.Equal("COMPLETED", slot.Outcome));
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Equal(3, fixture.Io.UnlockCount(1));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task OperationTimeoutOnlyPromptsAgainInsteadOfFailingTheSlot()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        List<(string Phase, int PromptRound)> phases = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (phase, active, completed, promptRound, token) =>
            {
                phases.Add((phase, promptRound));
                // 第一轮操作员干脆没动，仓门一直开着，等到 OperationTimeout 走完。
                if (phase == "WAITING_OPERATOR" && promptRound >= 1)
                {
                    fixture.Io.CloseDoor(active[0] - 1, cargo: true);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        // 提示节拍到期时仓门还开着，不重复脉冲。
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Equal([0], phases.Where(item => item.Phase == "UNLOCKING").Select(item => item.PromptRound));
        Assert.Equal(
            [0, 1],
            phases.Where(item => item.Phase == "WAITING_OPERATOR").Select(item => item.PromptRound));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task IoGoingUnknownDuringOperatorWaitStillEntersRecovery()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (phase, active, completed, promptRound, token) =>
            {
                if (phase == "WAITING_OPERATOR")
                {
                    fixture.Io.Disconnect();
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal("UNKNOWN", result.SlotResults.Single().Outcome);
        Assert.Contains("SLOT_STATE_UNKNOWN", result.SlotResults.Single().ReasonCodes);
        Assert.Equal("ACTIVE_UNLOCK_SET", result.JournalCheckpoint);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task SlotsNeverStartedKeepRealReadingsAndCarryNoReasonCodes()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        // 1 号仓的锁卡住不弹，开锁反馈等不到——决策 2 认定的三件事之一。
        fixture.Io.JamLock(0);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult failed = result.SlotResults.Single(slot => slot.SlotNo == 1);
        Assert.Equal("UNKNOWN", failed.Outcome);
        Assert.NotEmpty(failed.ReasonCodes);

        WireToGateSlotExecutionResult neverStarted = result.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal("NOT_STARTED", neverStarted.Outcome);
        Assert.Empty(neverStarted.ReasonCodes);
        Assert.Equal("EMPTY", neverStarted.FinalPhysicalState);
        Assert.Equal("LOCKED", neverStarted.LockState);
        Assert.Equal("RESET", neverStarted.UnlockOutputState);
        Assert.Equal(0, fixture.Io.UnlockCount(1));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task AnExpiredStationDeadlineSettlesAsDeterminateFailureAfterOneGraceRound()
    {
        // 本站期限早就过了，操作员每一轮都只关门、不放料。期限不会立刻判死：先花掉
        // 宽限的那一轮（再开一次门、再提示一次），第二次读到相反态才结算。
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken,
            () => DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
        List<(string Phase, int PromptRound)> phases = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (phase, active, completed, promptRound, token) =>
            {
                phases.Add((phase, promptRound));
                if (phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(active[0] - 1, cargo: false);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("FAILED", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = result.SlotResults.Single();
        Assert.Equal("FAILED", slot.Outcome);
        Assert.Equal(["OPERATOR_TIMEOUT"], slot.ReasonCodes);
        // 服务端的 determinateFailure 判据要的三样：状态已知、门已闭、开锁输出已复位。
        Assert.Equal("EMPTY", slot.FinalPhysicalState);
        Assert.Equal("LOCKED", slot.LockState);
        Assert.Equal("RESET", slot.UnlockOutputState);
        // 门已闭、输出已复位，这是安全收尾，与达成目标态时同一个检查点。
        Assert.Equal("SAFE_FINISH_REACHED", result.JournalCheckpoint);
        // 宽限那一轮真的又开了一次门，之后不再开。
        Assert.Equal(2, fixture.Io.UnlockCount(0));
        Assert.Equal(
            [0, 1],
            phases.Where(item => item.Phase == "UNLOCKING").Select(item => item.PromptRound));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task AStationDeadlineStillRunningNeverSettlesTheSlotAsFailed()
    {
        // 期限还没到，决策 1 的「不设次数上限」原封不动：关六次门也不判失败。
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken,
            () => DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5));

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (phase, active, completed, promptRound, token) =>
            {
                if (phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(active[0] - 1, cargo: promptRound >= 5);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(6, fixture.Io.UnlockCount(0));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task AnExpiredDeadlineWithTheDoorStillOpenKeepsPromptingInsteadOfSettling()
    {
        // 期限过了但仓门根本没关：ADR-cross-0058 决策 4 的「仓门未闭超时转告警并持续等待」。
        // 车这边照旧提示，不结算——而且门开着时 determinateFailure 的三个条件本来就不成立。
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken,
            () => DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
        List<(string Phase, int PromptRound)> phases = [];

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (phase, active, completed, promptRound, token) =>
            {
                phases.Add((phase, promptRound));
                // 前三轮门一直开着，第三轮之后操作员才把料放进去并关门。
                if (phase == "WAITING_OPERATOR" && promptRound >= 2)
                {
                    fixture.Io.CloseDoor(active[0] - 1, cargo: true);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        // 提示节拍到期时仓门还开着，不重复脉冲，也不因为期限已过就判死。
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Equal(
            [0, 1, 2],
            phases.Where(item => item.Phase == "WAITING_OPERATOR").Select(item => item.PromptRound));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task ADeterminateFailureLeavesEverySlotReadable()
    {
        // 决策 5 的 determinateFailure 判的是 SlotEvidence.All(...)：只要有一个仓位报
        // UNKNOWN，服务端就只能当成 RecoveryRequired。到期结算时那些从未开启的仓位
        // 因此必须按真实 IO 读数填（决策 6），不能图省事写 UNKNOWN。
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken,
            () => DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true),
            (phase, active, completed, promptRound, token) =>
            {
                if (phase == "WAITING_OPERATOR")
                {
                    fixture.Io.CloseDoor(active[0] - 1, cargo: false);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("FAILED", result.OverallOutcome);
        WireToGateSlotExecutionResult neverStarted = result.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal("NOT_STARTED", neverStarted.Outcome);
        Assert.Empty(neverStarted.ReasonCodes);
        Assert.Equal(0, fixture.Io.UnlockCount(1));
        Assert.All(result.SlotResults, slot =>
        {
            Assert.NotEqual("UNKNOWN", slot.FinalPhysicalState);
            Assert.Equal("LOCKED", slot.LockState);
            Assert.Equal("RESET", slot.UnlockOutputState);
        });
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task AbortActiveOperationStopsAnOperationThatHasNoNaturalEnd()
    {
        // 没有期限时目标态闭环没有终点，操作员发起的装货取消必须能把它停下来——否则
        // 取消向量与这个执行器会同时驱动同一个 IO 模块。
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<WireToGateOperationExecutionResult> operation = fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (phase, active, completed, promptRound, token) =>
            {
                if (phase == "WAITING_OPERATOR")
                {
                    waiting.TrySetResult();
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        await waiting.Task;
        fixture.Executor.AbortActiveOperation();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        // 中止不写结果。日志里那次 attempt 仍然未结算，取消向量的 RequireUnsettledLoadOperation
        // 要靠它认出自己在取消谁。
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        Assert.NotNull(state.UnsettledSlotOperationAttemptId);
        Assert.NotNull(state.OperationContext);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task AbortActiveOperationWithNothingRunningIsANoOp()
    {
        // 操作员可以在没有在途操作时按取消（本站还没开始装），那条路走的是
        // RequestLoadCancellationBeforeLoadAsync，中止不能因此炸掉。
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);

        fixture.Executor.AbortActiveOperation();
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task AnOperationInterruptedWhileWaitingSettlesAsUnknownWithRealReadingsAndNoPulse()
    {
        // 8005-agv-program#40 的现场：开了锁、在等操作员时进程没了，操作员把门关上、没放货。
        // 重启后要交得出结果，否则两端互相等；但不得再开锁（ADR-cross-0017），也不得把读得到的
        // 物理字段报成不知道（决策 6）。
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);
        await InterruptWhileWaitingAsync(fixture, command);
        fixture.Io.CloseDoor(0, cargo: false);

        WireToGateOperationExecutionResult result = await fixture.Executor.SettleInterruptedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal(command.SlotOperationAttemptId, result.SlotOperationAttemptId);
        WireToGateSlotExecutionResult interrupted = result.SlotResults.Single(slot => slot.SlotNo == 1);
        Assert.Equal("UNKNOWN", interrupted.Outcome);
        Assert.Equal(["RECOVERY_CHECKPOINT_NOT_UNIQUE"], interrupted.ReasonCodes);
        Assert.Equal("EMPTY", interrupted.FinalPhysicalState);
        Assert.Equal("LOCKED", interrupted.LockState);
        Assert.Equal("RESET", interrupted.UnlockOutputState);
        WireToGateSlotExecutionResult neverStarted = result.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal("NOT_STARTED", neverStarted.Outcome);
        Assert.Empty(neverStarted.ReasonCodes);
        Assert.Equal("EMPTY", neverStarted.FinalPhysicalState);
        Assert.Equal("SAFE_FINISH_REACHED", result.JournalCheckpoint);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        Assert.Equal(0, fixture.Io.UnlockCount(1));

        // 日志与执行中途判 UNKNOWN 时同形：补偿与恢复向量认的正是这一次 attempt 与它的上下文。
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(command.SlotOperationAttemptId, state.UnsettledSlotOperationAttemptId);
        Assert.NotNull(state.OperationContext);
        Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, state.ProvenRecoveryCheckpoint);
        Assert.Empty(state.ActiveUnlockSlots);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task AnOperationTheOperatorFinishedAfterTheProcessDiedSettlesAsCompleted()
    {
        // 进程没了之后操作员把货放进去、关好门。仓位的最终态读得清清楚楚，日志说它是开着的那一仓，
        // 这是唯一解释——报 UNKNOWN 就是把一次已经确定的结果误判成未知，一单好好的货会被补偿清掉。
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        await InterruptWhileWaitingAsync(fixture, command);
        fixture.Io.CloseDoor(0, cargo: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.SettleInterruptedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = result.SlotResults.Single();
        Assert.Equal("COMPLETED", slot.Outcome);
        Assert.Empty(slot.ReasonCodes);
        Assert.Equal("OCCUPIED", slot.FinalPhysicalState);
        Assert.Equal("LOCKED", slot.LockState);
        Assert.Equal("RESET", slot.UnlockOutputState);
        Assert.Equal("SAFE_FINISH_REACHED", result.JournalCheckpoint);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task AnInterruptedOperationWithOnlyOneOfItsSlotsDoneIsNotCompleted()
    {
        // 第一仓装好了、第二仓还没开过。第二仓开不开要服务端授权（ADR-cross-0017），
        // 所以这不是完成，哪怕每一个读数都是已知的。
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);
        await InterruptWhileWaitingAsync(fixture, command);
        fixture.Io.CloseDoor(0, cargo: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.SettleInterruptedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal("COMPLETED", result.SlotResults.Single(slot => slot.SlotNo == 1).Outcome);
        Assert.Equal("NOT_STARTED", result.SlotResults.Single(slot => slot.SlotNo == 2).Outcome);
        Assert.Equal(0, fixture.Io.UnlockCount(1));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task AnInterruptedSlotWhoseDoorIsStillOpenStaysInTheActiveSet()
    {
        // 门还开着：不是安全收尾，检查点留在 ACTIVE_UNLOCK_SET、仓位留在开锁集合里。补偿向量
        // 不去驱动一扇开着的门，维护人员得先把门关上。
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        await InterruptWhileWaitingAsync(fixture, command);

        WireToGateOperationExecutionResult result = await fixture.Executor.SettleInterruptedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = result.SlotResults.Single();
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal("UNLOCKED", slot.LockState);
        Assert.Equal("ACTIVE_UNLOCK_SET", result.JournalCheckpoint);
        Assert.Equal(1, fixture.Io.UnlockCount(0));
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal([1], state.ActiveUnlockSlots);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task AnInterruptedOperationSettledWithoutIoSaysItCannotReadTheSlots()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        await InterruptWhileWaitingAsync(fixture, command);
        fixture.Io.Disconnect();

        WireToGateOperationExecutionResult result = await fixture.Executor.SettleInterruptedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = result.SlotResults.Single();
        Assert.Equal(["SLOT_STATE_UNKNOWN"], slot.ReasonCodes);
        Assert.Equal("UNKNOWN", slot.FinalPhysicalState);
        Assert.Equal("ACTIVE_UNLOCK_SET", result.JournalCheckpoint);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task SettlingWithNothingUnsettledFailsClosed()
    {
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Executor.SettleInterruptedAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 让一次操作停在「开了锁、在等操作员」，然后像进程消失那样把它掐断：不写结果，日志里只留下
    /// 未结算的 attempt 与开锁集合。
    /// </summary>
    private static async Task InterruptWhileWaitingAsync(
        ScriptedFixture fixture,
        WireToGateSlotOperationCommand command)
    {
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<WireToGateOperationExecutionResult> operation = fixture.Executor.ExecuteAsync(
            command,
            (phase, active, completed, promptRound, token) =>
            {
                if (phase == "WAITING_OPERATOR")
                {
                    waiting.TrySetResult();
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
        await waiting.Task;
        fixture.Executor.AbortActiveOperation();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    private static WireToGateSlotOperationCommand CreateCommand(
        OperationType operationType,
        IReadOnlyList<int> slots,
        bool expectedOccupied) =>
        new(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            1,
            DateTimeOffset.UtcNow,
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            Guid.NewGuid().ToString("D"),
            operationType,
            slots,
            slots.Count,
            expectedOccupied,
            new string('0', 64));

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            SimulationIo io,
            SqliteWireToGateJournal journal,
            WireToGateSlotOperationExecutor executor)
        {
            Io = io;
            Journal = journal;
            Executor = executor;
        }

        public SimulationIo Io { get; }

        public SqliteWireToGateJournal Journal { get; }

        public WireToGateSlotOperationExecutor Executor { get; }

        public static async Task<TestFixture> CreateAsync(
            bool initialCargo = false,
            bool finalCargo = true,
            CancellationToken cancellationToken = default)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "w2g-executor",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
            await journal.InitializeAsync(cancellationToken);
            SimulationIo io = new(initialCargo, finalCargo);
            WireToGateSlotOperationExecutor executor = new(
                io,
                journal,
                new SystemClock(),
                new WireToGateSlotOperationExecutorOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1)));
            return new TestFixture(io, journal, executor);
        }

        public async ValueTask DisposeAsync()
        {
            await Executor.DisposeAsync();
            await Journal.DisposeAsync();
        }
    }

    private sealed class SimulationIo : IIoModuleClient
    {
        private readonly LockerSnapshot[] _lockers;
        private readonly bool _finalCargo;
        private readonly object _sync = new();

        public bool IsConnected => true;

        public int UnlockCount { get; private set; }

        public IoSnapshot CurrentSnapshot { get; private set; } = null!;

        public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

        public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged;

        public SimulationIo(bool initialCargo = false, bool finalCargo = true)
        {
            _finalCargo = finalCargo;
            _lockers = Enumerable.Range(0, 8)
                .Select(index => new LockerSnapshot(
                    index,
                    index + 1,
                    false,
                    true,
                    initialCargo ? false : true,
                    DateTimeOffset.UtcNow))
                .ToArray();
            CurrentSnapshot = new IoSnapshot(true, _lockers.ToArray(), DateTimeOffset.UtcNow);
            _ = ConnectionChanged;
        }

        public Task StartAsync(CancellationToken applicationStopping) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                UnlockCount++;
                UpdateLocker(slotIndex, locker => locker with
                {
                    LockFeedbackRaw = false,
                    UnlockOutputRaw = true,
                    ObservedAt = DateTimeOffset.UtcNow
                });
            }

            return Task.CompletedTask;
        }

        public async Task<LockerSnapshot> WaitForLockerAsync(
            int slotIndex,
            Func<LockerSnapshot, bool> predicate,
            TimeSpan timeout,
            TimeSpan stableWindow,
            CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LockerSnapshot locker;
                lock (_sync)
                {
                    locker = CurrentSnapshot.GetLocker(slotIndex);
                    if (predicate(locker))
                    {
                        return locker;
                    }

                    if (locker.LockFeedbackRaw is false && locker.UnlockOutputRaw is true)
                    {
                        UpdateLocker(slotIndex, current => current with
                        {
                            UnlockOutputRaw = false,
                            ObservedAt = DateTimeOffset.UtcNow
                        });
                    }
                    else if (locker.LockFeedbackRaw is false && locker.UnlockOutputRaw is false)
                    {
                        UpdateLocker(slotIndex, current => current with
                        {
                            LockFeedbackRaw = true,
                            LightCurtainRaw = _finalCargo ? false : true,
                            ObservedAt = DateTimeOffset.UtcNow
                        });
                    }
                }

                await Task.Delay(1, cancellationToken);
            }

            throw new TimeoutException();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void SetUnknown()
        {
            lock (_sync)
            {
                CurrentSnapshot = new IoSnapshot(
                    false,
                    Enumerable.Range(0, 8)
                        .Select(index => LockerSnapshot.Unknown(index, DateTimeOffset.UtcNow))
                        .ToArray(),
                    DateTimeOffset.UtcNow);
            }
        }

        private void UpdateLocker(int slotIndex, Func<LockerSnapshot, LockerSnapshot> update)
        {
            _lockers[slotIndex] = update(_lockers[slotIndex]);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CurrentSnapshot = new IoSnapshot(true, _lockers.ToArray(), now);
            SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(CurrentSnapshot));
        }
    }

    private sealed class ScriptedFixture : IAsyncDisposable
    {
        private ScriptedFixture(
            ScriptedIo io,
            SqliteWireToGateJournal journal,
            WireToGateSlotOperationExecutor executor)
        {
            Io = io;
            Journal = journal;
            Executor = executor;
        }

        public ScriptedIo Io { get; }

        public SqliteWireToGateJournal Journal { get; }

        public WireToGateSlotOperationExecutor Executor { get; }

        /// <param name="stationDepartureDeadline">
        /// 服务端给本站的离站期限。默认不给——那时目标态闭环没有上限，与 ADR-cross-0058
        /// 决策 1 的原始形态一致，既有用例照旧。
        /// </param>
        public static async Task<ScriptedFixture> CreateAsync(
            CancellationToken cancellationToken,
            Func<DateTimeOffset?>? stationDepartureDeadline = null)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "w2g-executor",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
            await journal.InitializeAsync(cancellationToken);
            ScriptedIo io = new();
            WireToGateSlotOperationExecutor executor = new(
                io,
                journal,
                new SystemClock(),
                new WireToGateSlotOperationExecutorOptions(
                    TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromMilliseconds(200),
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(30)),
                stationDepartureDeadline);
            return new ScriptedFixture(io, journal, executor);
        }

        public async ValueTask DisposeAsync()
        {
            await Executor.DisposeAsync();
            await Journal.DisposeAsync();
        }
    }

    /// <summary>
    /// 与 SimulationIo 不同，这个替身不会自己把仓位推向目标态：状态只由测试显式
    /// 改动。目标态闭环要验的正是「操作员没动作」与「操作员做错了」两种停留，
    /// 一个会自己走完流程的替身表达不了它们。
    /// </summary>
    private sealed class ScriptedIo : IIoModuleClient
    {
        private readonly LockerSnapshot[] _lockers;
        private readonly int[] _unlockCounts = new int[8];
        private readonly HashSet<int> _jammed = [];
        private readonly object _sync = new();

        public ScriptedIo()
        {
            _lockers = Enumerable.Range(0, 8)
                .Select(index => new LockerSnapshot(
                    index,
                    index + 1,
                    false,
                    true,
                    true,
                    DateTimeOffset.UtcNow))
                .ToArray();
            CurrentSnapshot = new IoSnapshot(true, _lockers.ToArray(), DateTimeOffset.UtcNow);
            _ = ConnectionChanged;
            _ = SnapshotChanged;
        }

        public bool IsConnected => CurrentSnapshot.IsConnected;

        public IoSnapshot CurrentSnapshot { get; private set; }

        public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

        public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged;

        public int UnlockCount(int slotIndex)
        {
            lock (_sync)
            {
                return _unlockCounts[slotIndex];
            }
        }

        public void JamLock(int slotIndex)
        {
            lock (_sync)
            {
                _jammed.Add(slotIndex);
            }
        }

        public void CloseDoor(int slotIndex, bool cargo)
        {
            lock (_sync)
            {
                Update(slotIndex, locker => locker with
                {
                    LockFeedbackRaw = true,
                    UnlockOutputRaw = false,
                    LightCurtainRaw = !cargo,
                    ObservedAt = DateTimeOffset.UtcNow
                });
            }
        }

        public void Disconnect()
        {
            lock (_sync)
            {
                CurrentSnapshot = IoSnapshot.Unknown(DateTimeOffset.UtcNow);
            }
        }

        public Task StartAsync(CancellationToken applicationStopping) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _unlockCounts[slotIndex]++;
                bool jammed = _jammed.Contains(slotIndex);
                Update(slotIndex, locker => locker with
                {
                    UnlockOutputRaw = true,
                    LockFeedbackRaw = jammed,
                    ObservedAt = DateTimeOffset.UtcNow
                });
                Update(slotIndex, locker => locker with
                {
                    UnlockOutputRaw = false,
                    ObservedAt = DateTimeOffset.UtcNow
                });
            }

            return Task.CompletedTask;
        }

        public async Task<LockerSnapshot> WaitForLockerAsync(
            int slotIndex,
            Func<LockerSnapshot, bool> predicate,
            TimeSpan timeout,
            TimeSpan stableWindow,
            CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IoSnapshot snapshot = CurrentSnapshot;
                if (!snapshot.IsConnected)
                {
                    throw new IOException("等待仓位反馈时IO连接已断开。");
                }

                if (predicate(snapshot.GetLocker(slotIndex)))
                {
                    return snapshot.GetLocker(slotIndex);
                }

                await Task.Delay(5, cancellationToken);
            }

            throw new TimeoutException();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Update(int slotIndex, Func<LockerSnapshot, LockerSnapshot> update)
        {
            _lockers[slotIndex] = update(_lockers[slotIndex]);
            CurrentSnapshot = new IoSnapshot(true, _lockers.ToArray(), DateTimeOffset.UtcNow);
        }
    }
}
