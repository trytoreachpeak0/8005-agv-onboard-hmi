using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

public sealed class WireToGateRecoveryVectorExecutorTests
{
    /// <summary>
    /// A local line behind the entries (review of onboard-hmi#110): a clear over a slot a forced
    /// mechanical recovery left physically unknown is refused before anything is journaled or
    /// pulsed. Nothing proves what state that slot was left in (REQ-0241).
    /// </summary>
    [Theory]
    [InlineData(WireToGateRecoveryVectorTypes.LoadCompensation)]
    [InlineData(WireToGateRecoveryVectorTypes.FaultCargoHandoff)]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AClearOverAPhysicallyUnknownSlotIsRefusedBeforeAnyPulse(string vectorType)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, true], cancellationToken: token);
        await fixture.Journal.UpdateRecoveryStateAsync(
            _ => WireToGateRecoveryState.Empty with
            {
                ForcedIsolation = new WireToGateForcedIsolation(
                    "77777777-7777-4777-8777-777777777777",
                    "88888888-8888-4888-8888-888888888888",
                    [2])
            },
            token);

        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Executor.ExecuteClearAsync(
                CreateContext(vectorType, "cdcdcdcd-cdcd-4cdc-8cdc-cdcdcdcdcdcd", [1, 2]),
                null,
                token));

        Assert.Equal("SLOT_INOPERABLE", refused.Message);
        Assert.Equal(0, fixture.Io.UnlockCount);
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Null(state.RecoveryVector);
        Assert.Equal([2], state.ForcedIsolation!.PhysicallyUnknownSlots);
    }

    /// <summary>
    /// <c>HasOperationInFlight</c> 在恢复向量执行期间也跟着门走（8005-agv-onboard-hmi#171）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 恢复向量是全仓**第四个开门点**（见 <c>FatalFaultScopeArchitectureTests</c> 那张表）：补偿
    /// 清空、修正装货、强制机械取出都在这里真的开门，而严重安全故障锁存拦不住它。所以复位复核的
    /// 「现在有没有在开门」必须也问这一侧——只问仓位命令那一侧，操作员按出来的开门就漏了。
    /// </para>
    /// <para>
    /// 第一版只测了 <c>WireToGateSlotOperationExecutor</c> 那一侧，这一条是补上的对称覆盖：
    /// 两个执行器各有自己的门，**聚合读的是它们的并**，少测一侧就等于那一侧没有判据。
    /// </para>
    /// <para>
    /// <b>没有覆盖到的一处，写在这里而不是假装覆盖了</b>：聚合本身
    /// （<c>WireToGateBusinessService.HasSlotWorkInFlight</c> 那个 <c>||</c>）没有直接的测试。
    /// 两侧各自的判据都在，但把 <c>||</c> 写成 <c>&amp;&amp;</c> 的话两条都不会红——要造一条真正
    /// 分辨它的用例，得让一侧忙、另一侧闲，而那需要在 G2 夹具里挂住一个执行器。留给下一个人，
    /// 或者留给第一次真出事的时候。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task HasOperationInFlightFollowsTheGateWhileARecoveryVectorRuns()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true, true],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(fixture.Executor.HasOperationInFlight);

        List<bool> observedDuringRun = [];
        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(
                WireToGateRecoveryVectorTypes.LoadCancellation,
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                [1, 2]),
            (_, _, _, _) =>
            {
                observedDuringRun.Add(fixture.Executor.HasOperationInFlight);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.NotEmpty(observedDuringRun);
        Assert.All(observedDuringRun, Assert.True);
        Assert.False(fixture.Executor.HasOperationInFlight);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ClearSkipsEmptySlotsAndUnlocksOnlyOccupiedSlots()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [false, true, false],
            cancellationToken: TestContext.Current.CancellationToken);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(
                WireToGateRecoveryVectorTypes.LoadCancellation,
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                [1, 2, 3]),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(1, fixture.Io.UnlockCount);
        Assert.Equal([1, 2, 3], result.SlotResults.Select(item => item.SlotNo));
        Assert.All(result.SlotResults, item =>
        {
            Assert.Equal("COMPLETED", item.Outcome);
            Assert.Equal("EMPTY", item.FinalPhysicalState);
            Assert.Equal("LOCKED", item.LockState);
            Assert.Equal("RESET", item.UnlockOutputState);
        });
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-REQUEST-FIRST-RESULT-REPLAY")]
    public async Task ReplayingACompletedVectorKeepsTheSameObservedAtAndDoesNotPulse()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true],
            cancellationToken: TestContext.Current.CancellationToken);
        WireToGateRecoveryVectorContext context = CreateContext(
            WireToGateRecoveryVectorTypes.LoadCancellation,
            "12121212-1212-4212-8212-121212121212",
            [1]);

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            null,
            TestContext.Current.CancellationToken);
        WireToGateRecoveryVectorExecutionResult replay = await fixture.Executor.ExecuteClearAsync(
            context,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", replay.OverallOutcome);
        Assert.Equal(first.ObservedAt, replay.ObservedAt);
        Assert.Equal(1, fixture.Io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task CorrectionRequiresEmptyThenOccupiedSequence()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true],
            correction: true,
            cancellationToken: TestContext.Current.CancellationToken);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteCorrectionAsync(
            CreateContext(
                WireToGateRecoveryVectorTypes.LoadCorrection,
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                [1]),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(1, fixture.Io.UnlockCount);
        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults);
        Assert.Equal("OCCUPIED", slot.FinalPhysicalState);
        Assert.Equal("LOCKED", slot.LockState);
        Assert.Equal("RESET", slot.UnlockOutputState);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task UnknownSnapshotFailsClosedWithoutUnlock()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true],
            cancellationToken: TestContext.Current.CancellationToken);
        fixture.Io.SetUnknown();

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(
                WireToGateRecoveryVectorTypes.LoadCancellation,
                "cccccccc-cccc-cccc-cccc-cccccccccccc",
                [1]),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("FAILED", result.OverallOutcome);
        Assert.Equal(0, fixture.Io.UnlockCount);
        Assert.Contains("SLOT_STATE_UNKNOWN", Assert.Single(result.SlotResults).ReasonCodes);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ActiveUnlockCheckpointIsNotPulsedAgainAfterUncertainFailure()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true],
            failOnWaitCall: 2,
            cancellationToken: TestContext.Current.CancellationToken);
        WireToGateRecoveryVectorContext context = CreateContext(
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "dddddddd-dddd-dddd-dddd-dddddddddddd",
            [1]);

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", first.OverallOutcome);
        Assert.Equal(1, fixture.Io.UnlockCount);
        WireToGateRecoveryState checkpoint = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(WireToGateRecoveryCheckpoint.ActiveUnlockSet, checkpoint.ProvenRecoveryCheckpoint);
        Assert.Equal([1], checkpoint.ActiveUnlockSlots);

        fixture.Io.FailOnWaitCall = null;
        WireToGateRecoveryVectorExecutionResult replay = await fixture.Executor.ExecuteClearAsync(
            context,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", replay.OverallOutcome);
        Assert.Equal(1, fixture.Io.UnlockCount);
        Assert.Contains("SLOT_STATE_UNKNOWN", Assert.Single(replay.SlotResults).ReasonCodes);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AFailedVectorSlotKeepsItsUnknownAndSlotsNeverStartedKeepTheirReadings()
    {
        // The same overwrite the slot operation executor had (ADR-cross-0058 decision 6): the slot
        // whose feedback failed stays UNKNOWN, and the slot never opened is NOT_STARTED with what the
        // IO reads and no reason code.
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true, true, true],
            failOnWaitCall: 4,
            cancellationToken: TestContext.Current.CancellationToken);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(
                WireToGateRecoveryVectorTypes.LoadCompensation,
                "abababab-abab-4bab-8bab-abababababab",
                [1, 2, 3]),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal([1, 2, 3], result.SlotResults.Select(item => item.SlotNo));
        Assert.Equal("COMPLETED", result.SlotResults[0].Outcome);
        Assert.Equal("UNKNOWN", result.SlotResults[1].Outcome);
        Assert.Equal(["SLOT_STATE_UNKNOWN"], result.SlotResults[1].ReasonCodes);
        WireToGateSlotExecutionResult neverStarted = result.SlotResults[2];
        Assert.Equal("NOT_STARTED", neverStarted.Outcome);
        Assert.Empty(neverStarted.ReasonCodes);
        Assert.Equal("OCCUPIED", neverStarted.FinalPhysicalState);
        Assert.Equal("LOCKED", neverStarted.LockState);
        Assert.Equal("RESET", neverStarted.UnlockOutputState);
        Assert.Equal(2, fixture.Io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ARefusedVectorPutsTheReasonOnlyOnTheSlotThatFailsThePrecheck()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true, true],
            cancellationToken: TestContext.Current.CancellationToken);
        fixture.Io.OpenDoor(1);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(
                WireToGateRecoveryVectorTypes.LoadCompensation,
                "cdcdcdcd-cdcd-4dcd-8dcd-cdcdcdcdcdcd",
                [1, 2]),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("FAILED", result.OverallOutcome);
        Assert.All(result.SlotResults, item => Assert.Equal("NOT_STARTED", item.Outcome));
        Assert.Empty(result.SlotResults[0].ReasonCodes);
        Assert.Equal("OCCUPIED", result.SlotResults[0].FinalPhysicalState);
        Assert.Equal("LOCKED", result.SlotResults[0].LockState);
        Assert.Equal(["LOCK_NOT_CLOSED"], result.SlotResults[1].ReasonCodes);
        Assert.Equal("UNLOCKED", result.SlotResults[1].LockState);
        Assert.Equal(0, fixture.Io.UnlockCount);
    }

    /// <summary>
    /// 扫码前取消（onboard-hmi#76）走执行器的空集合分支：不读 IO、不开锁，结果是没有逐仓结果的
    /// COMPLETED；读数未知、目标仓有货都不影响，因为没有要证明的仓。重放拿到同一个 observedAt。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ACancellationBeforeAnySublotCompletesWithoutTouchingIo()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true, true],
            cancellationToken: TestContext.Current.CancellationToken);
        fixture.Io.SetUnknown();
        WireToGateRecoveryVectorContext context = CreateContext(
            WireToGateRecoveryVectorTypes.LoadCancellation,
            "13131313-1313-4313-8313-131313131313",
            []) with
        {
            SlotOperationAttemptId = null
        };

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            null,
            TestContext.Current.CancellationToken);
        WireToGateRecoveryVectorExecutionResult replay = await fixture.Executor.ExecuteClearAsync(
            context,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", first.OverallOutcome);
        Assert.Empty(first.SlotResults);
        Assert.Equal(first.ObservedAt, replay.ObservedAt);
        Assert.Equal("COMPLETED", replay.OverallOutcome);
        Assert.Equal(0, fixture.Io.UnlockCount);
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, state.ProvenRecoveryCheckpoint);
        Assert.Empty(state.ActiveUnlockSlots);
    }

    /// <summary>
    /// 空仓位集合只属于扫码前取消。带着 attempt 的取消、补偿与修正拿到空集合都是无效命令，照旧拒绝。
    /// </summary>
    [Theory]
    [InlineData(WireToGateRecoveryVectorTypes.LoadCancellation, false)]
    [InlineData(WireToGateRecoveryVectorTypes.LoadCompensation, false)]
    [InlineData(WireToGateRecoveryVectorTypes.LoadCorrection, true)]
    public async Task AnEmptySlotSetIsRefusedForEveryOtherVector(string vectorType, bool correction)
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true],
            correction,
            cancellationToken: TestContext.Current.CancellationToken);
        WireToGateRecoveryVectorContext context = CreateContext(
            vectorType,
            "14141414-1414-4414-8414-141414141414",
            []);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => correction
            ? fixture.Executor.ExecuteCorrectionAsync(context, null, TestContext.Current.CancellationToken)
            : fixture.Executor.ExecuteClearAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Equal("RECOVERY_COMMAND_INVALID", failure.Message);
        Assert.Equal(0, fixture.Io.UnlockCount);
    }

    /// <summary>
    /// ADR-cross-0046 for a load cancelled in flight (onboard-hmi#78): a door the aborted load left open
    /// when the authorization arrived is not pulsed again. The operator takes the basket out -- or never
    /// put one in -- and shuts it; <c>EMPTY</c>, locked and output reset counts as cleared. Other target
    /// slots confirmed occupied are unlocked to be emptied as before.
    /// </summary>
    /// <remarks>
    /// The business service hands the open door over in the journal: the prepared vector keeps the
    /// aborted load's active unlock set. Before this ticket that set read as a crash fence, and the open
    /// door made the whole cancellation UNKNOWN.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ADoorLeftOpenByTheAbortedLoadCountsAsClearedOnceShutEmptyWithoutAnotherPulse(bool basketIn)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([basketIn, true], cancellationToken: token);
        fixture.Io.OpenDoor(0);
        WireToGateRecoveryVectorContext context = CreateContext(
            WireToGateRecoveryVectorTypes.LoadCancellation,
            "15151515-1515-4515-8515-151515151515",
            [1, 2]);
        await fixture.Journal.UpdateRecoveryStateAsync(
            _ => new WireToGateRecoveryState(
                context.SlotOperationAttemptId,
                WireToGateRecoveryCheckpoint.Prepared,
                [1],
                0,
                [])
            {
                RecoveryVector = context
            },
            token);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            context,
            null,
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal(1, fixture.Io.UnlockCount);
        Assert.All(result.SlotResults, item =>
        {
            Assert.Equal("COMPLETED", item.Outcome);
            Assert.Equal("EMPTY", item.FinalPhysicalState);
            Assert.Equal("LOCKED", item.LockState);
            Assert.Equal("RESET", item.UnlockOutputState);
        });
    }

    /// <summary>
    /// A handover is only the load cancellation's. The same journal shape under a compensation is still
    /// a crash fence: the open door is not proven safe, so the vector is UNKNOWN and nothing is pulsed.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnOpenDoorInTheActiveSetOfAnotherVectorIsStillAFence()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true], cancellationToken: token);
        fixture.Io.OpenDoor(0);
        WireToGateRecoveryVectorContext context = CreateContext(
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "16161616-1616-4616-8616-161616161616",
            [1]);
        await fixture.Journal.UpdateRecoveryStateAsync(
            _ => new WireToGateRecoveryState(
                context.SlotOperationAttemptId,
                WireToGateRecoveryCheckpoint.Prepared,
                [1],
                0,
                [])
            {
                RecoveryVector = context
            },
            token);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            context,
            null,
            token);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal(0, fixture.Io.UnlockCount);
    }

    /// <summary>
    /// REQ-0357 and ADR-cross-0061, the one defect program#111 found: a load of [1,2,3] is cancelled
    /// with 1 loaded and locked and 2 standing open. The door the aborted load left open is the one
    /// open door on the vehicle, so it is brought to its end first -- 1 is not pulsed while 2 is open,
    /// and the journal's active unlock set stays [2] until 2 is done instead of being rewritten to [1],
    /// which would have lost the open door to a restart.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ACancelledLoadsOpenDoorIsFinishedBeforeAnyLoadedSlotIsUnlocked()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, false, false], cancellationToken: token);
        fixture.Io.OpenDoor(1);
        WireToGateRecoveryVectorContext context = await HandOverAsync(
            fixture,
            "17171717-1717-4717-8717-171717171717",
            [1, 2, 3],
            [2],
            token);
        List<(int[] Active, int[] Completed)> journaled = [];
        async Task SampleAsync()
        {
            WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
            journaled.Add((state.ActiveUnlockSlots.ToArray(), state.CompletedSlots.ToArray()));
        }

        fixture.Io.BeforePulse = _ => SampleAsync();

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            context,
            (_, _, _, _) => SampleAsync(),
            token);

        Assert.All(
            journaled.Where(sample => !sample.Completed.Contains(2)),
            sample => Assert.Equal([2], sample.Active));
        Assert.Equal([(1, true)], fixture.Io.Pulses);
        Assert.Contains(journaled, sample => sample.Completed.Contains(2));
        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.All(result.SlotResults, item =>
        {
            Assert.Equal("COMPLETED", item.Outcome);
            Assert.Equal("EMPTY", item.FinalPhysicalState);
            Assert.Equal("LOCKED", item.LockState);
            Assert.Equal("RESET", item.UnlockOutputState);
        });
    }

    /// <summary>
    /// The door handed over open cannot be proven closed -- its output never falls back, or the operator
    /// shuts it over the basket -- so it ends UNKNOWN, and the vector stops there: neither 1 nor 3 is
    /// opened, both report NOT_STARTED, and 2 stays the active unlock set.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AHandedOverDoorThatEndsUnknownStopsTheClearBeforeAnyOtherDoorOpens(bool stuckOutput)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, true, true], cancellationToken: token);
        if (stuckOutput)
        {
            fixture.Io.OpenDoorWithOutputActive(1);
            fixture.Io.StuckOutputSlots.Add(2);
        }
        else
        {
            fixture.Io.OpenDoor(1);
            fixture.Io.NeverEmptiedSlots.Add(2);
        }

        WireToGateRecoveryVectorContext context = await HandOverAsync(
            fixture,
            "18181818-1818-4818-8818-181818181818",
            [1, 2, 3],
            [2],
            token);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            context,
            null,
            token);

        Assert.Empty(fixture.Io.Pulses);
        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal([1, 2, 3], result.SlotResults.Select(item => item.SlotNo));
        Assert.Equal("NOT_STARTED", result.SlotResults[0].Outcome);
        Assert.Equal("UNKNOWN", result.SlotResults[1].Outcome);
        Assert.NotEmpty(result.SlotResults[1].ReasonCodes);
        Assert.Equal("NOT_STARTED", result.SlotResults[2].Outcome);
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal([2], state.ActiveUnlockSlots);
        Assert.Empty(state.CompletedSlots);
    }

    /// <summary>
    /// With one door at a time the operator empties the slots one after another, so each slot gets the
    /// whole OperationTimeout from the moment its turn comes. Three slots taking three seconds each
    /// fit a five-second timeout; one deadline shared from the vector's start would have left the
    /// third slot nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task EverySlotOfAClearGetsTheWholeOperationTimeoutFromItsOwnTurn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, true, true], cancellationToken: token);
        fixture.Io.OperatorDelay = TimeSpan.FromSeconds(3);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(
                WireToGateRecoveryVectorTypes.LoadCompensation,
                "19191919-1919-4919-8919-191919191919",
                [1, 2, 3]),
            null,
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal([1, 2, 3], fixture.Io.Pulses.Select(pulse => pulse.Slot));
        Assert.All(result.SlotResults, item => Assert.Equal("COMPLETED", item.Outcome));
    }

    /// <summary>
    /// REQ-0357 across the clears: every unlock pulse of a load cancellation, a compensation or a fault
    /// cargo handoff finds every other door of the vehicle locked with its output reset.
    /// </summary>
    [Theory]
    [InlineData(WireToGateRecoveryVectorTypes.LoadCancellation)]
    [InlineData(WireToGateRecoveryVectorTypes.LoadCompensation)]
    [InlineData(WireToGateRecoveryVectorTypes.FaultCargoHandoff)]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task EveryUnlockOfAClearFindsEveryOtherDoorShut(string vectorType)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true, false, true, true],
            cancellationToken: token);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(vectorType, "1a1a1a1a-1a1a-4a1a-8a1a-1a1a1a1a1a1a", [1, 2, 3, 4]),
            null,
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal([1, 3, 4], fixture.Io.Pulses.Select(pulse => pulse.Slot));
        Assert.All(fixture.Io.Pulses, pulse => Assert.True(pulse.OtherDoorsShut));
    }

    /// <summary>A correction replay is one door at a time too (ADR-cross-0061: correction is no exception).</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task EveryUnlockOfACorrectionFindsEveryOtherDoorShut()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true, true, true],
            correction: true,
            cancellationToken: token);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteCorrectionAsync(
            CreateContext(
                WireToGateRecoveryVectorTypes.LoadCorrection,
                "1b1b1b1b-1b1b-4b1b-8b1b-1b1b1b1b1b1b",
                [1, 2, 3]),
            null,
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal([1, 2, 3], fixture.Io.Pulses.Select(pulse => pulse.Slot));
        Assert.All(fixture.Io.Pulses, pulse => Assert.True(pulse.OtherDoorsShut));
    }

    public static TheoryData<string, string> OtherDoorConditions => new()
    {
        { "open", "LOCK_NOT_CLOSED" },
        { "output-active", "UNLOCK_OUTPUT_NOT_RESET" },
        { "unreadable", "SLOT_STATE_UNKNOWN" }
    };

    /// <summary>
    /// The check before a clear's pulse looks at the whole vehicle: a door outside the vector that is not
    /// proven shut stops the vector before its first pulse. The slot about to be opened is UNKNOWN with
    /// the reason, the rest NOT_STARTED, and since nothing was pulsed nothing stays in the active set.
    /// </summary>
    [Theory]
    [MemberData(nameof(OtherDoorConditions))]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ADoorOutsideTheVectorThatIsNotShutRefusesTheUnlock(string condition, string reason)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, true], cancellationToken: token);
        switch (condition)
        {
            case "open":
                fixture.Io.OpenDoor(4);
                break;
            case "output-active":
                fixture.Io.OpenDoorWithOutputActive(4);
                fixture.Io.CloseDoorKeepingOutput(4);
                break;
            default:
                fixture.Io.LoseLockFeedback(4);
                break;
        }

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(
                WireToGateRecoveryVectorTypes.LoadCompensation,
                "1c1c1c1c-1c1c-4c1c-8c1c-1c1c1c1c1c1c",
                [1, 2]),
            null,
            token);

        Assert.Empty(fixture.Io.Pulses);
        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal("UNKNOWN", result.SlotResults[0].Outcome);
        Assert.Equal([reason], result.SlotResults[0].ReasonCodes);
        Assert.Equal("LOCKED", result.SlotResults[0].LockState);
        Assert.Equal("NOT_STARTED", result.SlotResults[1].Outcome);
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Empty(state.ActiveUnlockSlots);
    }

    /// <summary>
    /// onboard-hmi#123: a vector the vehicle refuses before touching anything -- the prepared state
    /// the business service wrote and nothing since -- is recorded as FAILED, every slot NOT_STARTED
    /// with what the IO reads and the reason it was refused, and nothing is pulsed.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AVectorRefusedBeforeAnyUnlockIsRecordedFailedWithEverySlotNotStartedAsRead()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, false], cancellationToken: token);
        WireToGateRecoveryVectorContext context = await PrepareAsync(
            fixture,
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "a1a1a1a1-a1a1-4a1a-8a1a-a1a1a1a1a1a1",
            [1, 2],
            token);

        WireToGateRecoveryVectorExecutionResult? refused = await fixture.Executor.RefuseBeforeUnlockAsync(
            context,
            "VEHICLE_NOT_READY",
            token);

        Assert.NotNull(refused);
        Assert.Equal("FAILED", refused.OverallOutcome);
        Assert.Equal("PREPARED", refused.JournalCheckpoint);
        Assert.Equal([1, 2], refused.SlotResults.Select(item => item.SlotNo));
        Assert.All(refused.SlotResults, item =>
        {
            Assert.Equal("NOT_STARTED", item.Outcome);
            Assert.Equal(["VEHICLE_NOT_READY"], item.ReasonCodes);
            Assert.Equal("LOCKED", item.LockState);
            Assert.Equal("RESET", item.UnlockOutputState);
        });
        Assert.Equal("OCCUPIED", refused.SlotResults[0].FinalPhysicalState);
        Assert.Equal("EMPTY", refused.SlotResults[1].FinalPhysicalState);
        Assert.Equal(0, fixture.Io.UnlockCount);

        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(WireToGateRecoveryCheckpoint.Prepared, state.ProvenRecoveryCheckpoint);
        Assert.Empty(state.ActiveUnlockSlots);
        Assert.Empty(state.CompletedSlots);
        Assert.Equal(refused.ObservedAt, state.RecoveryResultObservedAt);
    }

    /// <summary>
    /// A refusal is answered once: asked again, the executor returns what it recorded -- the same
    /// observedAt and the same readings -- not a second refusal built from what the IO reads now.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ARefusalAskedForAgainReturnsTheRecordedResultNotANewReading()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, true], cancellationToken: token);
        WireToGateRecoveryVectorContext context = await PrepareAsync(
            fixture,
            WireToGateRecoveryVectorTypes.FaultCargoHandoff,
            "a2a2a2a2-a2a2-4a2a-8a2a-a2a2a2a2a2a2",
            [1, 2],
            token);

        WireToGateRecoveryVectorExecutionResult first = (await fixture.Executor.RefuseBeforeUnlockAsync(
            context,
            "VEHICLE_NOT_READY",
            token))!;
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        fixture.Io.OpenDoor(1);
        WireToGateRecoveryVectorExecutionResult again = (await fixture.Executor.RefuseBeforeUnlockAsync(
            context,
            "VEHICLE_NOT_READY",
            token))!;

        Assert.Equal("FAILED", again.OverallOutcome);
        Assert.Equal(first.ObservedAt, again.ObservedAt);
        Assert.Equal(first.SlotResults, again.SlotResults, SlotResultComparer.Instance);
        Assert.Equal(0, fixture.Io.UnlockCount);
    }

    /// <summary>
    /// Anything past the prepared state means the vector may have acted, so the executor will not
    /// call it refused before an unlock: an active unlock set, a slot already counted complete, a
    /// later checkpoint. It answers <c>null</c> and writes nothing, and the caller keeps the
    /// settlement it already had (onboard-hmi#123, point 2).
    /// </summary>
    [Theory]
    [MemberData(nameof(StatesThatMayHaveActed))]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AVectorThatMayHaveActedIsNeverReportedAsRefusedBeforeAnUnlock(
        WireToGateRecoveryCheckpoint checkpoint,
        int[] active,
        int[] completed)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, true], cancellationToken: token);
        WireToGateRecoveryVectorContext context = CreateContext(
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "a3a3a3a3-a3a3-4a3a-8a3a-a3a3a3a3a3a3",
            [1, 2]);
        WireToGateRecoveryState started = new(
            context.SlotOperationAttemptId,
            checkpoint,
            active,
            0,
            [])
        {
            RecoveryVector = context,
            CompletedSlots = completed,
            SlotResults =
            [
                .. completed.Select(slot => new WireToGateSlotExecutionResult(
                    slot, "COMPLETED", "EMPTY", "LOCKED", "RESET", []))
            ]
        };
        await fixture.Journal.UpdateRecoveryStateAsync(_ => started, token);

        WireToGateRecoveryVectorExecutionResult? refused = await fixture.Executor.RefuseBeforeUnlockAsync(
            context,
            "VEHICLE_NOT_READY",
            token);

        Assert.Null(refused);
        WireToGateRecoveryState after = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(checkpoint, after.ProvenRecoveryCheckpoint);
        Assert.Equal(active, after.ActiveUnlockSlots);
        Assert.Equal(completed, after.CompletedSlots);
        Assert.Null(after.RecoveryResultObservedAt);
        Assert.Equal(0, fixture.Io.UnlockCount);
    }

    /// <summary>Checkpoint, active unlock set, completed slots.</summary>
    public static TheoryData<WireToGateRecoveryCheckpoint, int[], int[]> StatesThatMayHaveActed => new()
    {
        { WireToGateRecoveryCheckpoint.ActiveUnlockSet, [1], [] },
        { WireToGateRecoveryCheckpoint.ActiveUnlockSet, [], [1] },
        { WireToGateRecoveryCheckpoint.Prepared, [], [2] },
        { WireToGateRecoveryCheckpoint.SafeFinishReached, [], [1, 2] }
    };

    private sealed class SlotResultComparer : IEqualityComparer<WireToGateSlotExecutionResult>
    {
        public static readonly SlotResultComparer Instance = new();

        public bool Equals(WireToGateSlotExecutionResult? x, WireToGateSlotExecutionResult? y) =>
            x is not null
            && y is not null
            && x.SlotNo == y.SlotNo
            && x.Outcome == y.Outcome
            && x.FinalPhysicalState == y.FinalPhysicalState
            && x.LockState == y.LockState
            && x.UnlockOutputState == y.UnlockOutputState
            && x.ReasonCodes.SequenceEqual(y.ReasonCodes);

        public int GetHashCode(WireToGateSlotExecutionResult obj) => obj.SlotNo;
    }

    /// <summary>What <c>WriteRecoveryVectorPreparedAsync</c> leaves behind: the vector, and nothing done.</summary>
    private static async Task<WireToGateRecoveryVectorContext> PrepareAsync(
        TestFixture fixture,
        string vectorType,
        string primaryId,
        IReadOnlyList<int> slots,
        CancellationToken token)
    {
        WireToGateRecoveryVectorContext context = CreateContext(vectorType, primaryId, slots);
        await fixture.Journal.UpdateRecoveryStateAsync(
            _ => new WireToGateRecoveryState(
                context.SlotOperationAttemptId,
                WireToGateRecoveryCheckpoint.Prepared,
                [],
                0,
                [])
            {
                RecoveryVector = context
            },
            token);
        return context;
    }

    /// <summary>What the business service journals when an authorized cancellation takes a load over.</summary>
    private static async Task<WireToGateRecoveryVectorContext> HandOverAsync(
        TestFixture fixture,
        string cancellationId,
        IReadOnlyList<int> slots,
        IReadOnlyList<int> handedOverOpen,
        CancellationToken token)
    {
        WireToGateRecoveryVectorContext context = CreateContext(
            WireToGateRecoveryVectorTypes.LoadCancellation,
            cancellationId,
            slots);
        await fixture.Journal.UpdateRecoveryStateAsync(
            _ => new WireToGateRecoveryState(
                context.SlotOperationAttemptId,
                WireToGateRecoveryCheckpoint.Prepared,
                handedOverOpen,
                0,
                [])
            {
                RecoveryVector = context
            },
            token);
        return context;
    }

    private static WireToGateRecoveryVectorContext CreateContext(
        string vectorType,
        string primaryId,
        IReadOnlyList<int> slots) =>
        new(
            vectorType,
            primaryId,
            vectorType is WireToGateRecoveryVectorTypes.LoadCompensation
                or WireToGateRecoveryVectorTypes.FaultCargoHandoff
                ? "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"
                : null,
            "ffffffff-ffff-4fff-8fff-ffffffffffff",
            "11111111-1111-4111-8111-111111111111",
            vectorType == WireToGateRecoveryVectorTypes.FaultCargoHandoff
                ? "22222222-2222-4222-8222-222222222222"
                : null,
            slots,
            null,
            null,
            null,
            null);

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            FixedClock clock,
            ScriptedIo io,
            SqliteWireToGateJournal journal,
            WireToGateRecoveryVectorExecutor executor)
        {
            Clock = clock;
            Io = io;
            Journal = journal;
            Executor = executor;
        }

        public FixedClock Clock { get; }

        public ScriptedIo Io { get; }

        public SqliteWireToGateJournal Journal { get; }

        public WireToGateRecoveryVectorExecutor Executor { get; }

        public static async Task<TestFixture> CreateAsync(
            IReadOnlyList<bool> cargo,
            bool correction = false,
            int? failOnWaitCall = null,
            CancellationToken cancellationToken = default)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "w2g-recovery-vector",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
            await journal.InitializeAsync(cancellationToken);
            FixedClock clock = new(DateTimeOffset.UtcNow);
            ScriptedIo io = new(clock, cargo, correction, failOnWaitCall);
            WireToGateRecoveryVectorExecutor executor = new(
                io,
                journal,
                clock,
                new WireToGateSlotOperationExecutorOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromSeconds(1)));
            return new TestFixture(clock, io, journal, executor);
        }

        public async ValueTask DisposeAsync()
        {
            await Executor.DisposeAsync();
            await Journal.DisposeAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; private set; } = now;

        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class ScriptedIo : IIoModuleClient
    {
        private readonly FixedClock _clock;
        private readonly bool _correction;
        private LockerSnapshot[] _lockers;
        private IoSnapshot _snapshot;
        private int _waitCallCount;

        public ScriptedIo(
            FixedClock clock,
            IReadOnlyList<bool> cargo,
            bool correction,
            int? failOnWaitCall)
        {
            _clock = clock;
            _correction = correction;
            FailOnWaitCall = failOnWaitCall;
            _lockers = Enumerable.Range(0, 8)
                .Select(index => new LockerSnapshot(
                    index,
                    index + 1,
                    false,
                    true,
                    cargo.Count > index && cargo[index] ? false : true,
                    clock.Now))
                .ToArray();
            _snapshot = new IoSnapshot(true, _lockers.ToArray(), clock.Now);
            _ = ConnectionChanged;
        }

        public int? FailOnWaitCall { get; set; }

        public int UnlockCount { get; private set; }

        public bool IsConnected => _snapshot.IsConnected;

        public IoSnapshot CurrentSnapshot => _snapshot;

        public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

        public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged;

        public Task StartAsync(CancellationToken applicationStopping) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>Physical slots whose unlock output never falls back after the pulse.</summary>
        public HashSet<int> StuckOutputSlots { get; } = [];

        /// <summary>Physical slots the operator shuts again without taking the basket out.</summary>
        public HashSet<int> NeverEmptiedSlots { get; } = [];

        /// <summary>
        /// How long the operator takes to empty a door and shut it; the clock moves on by this much each
        /// time, so a deadline the executor keeps is measured against the operator, not the test.
        /// </summary>
        public TimeSpan OperatorDelay { get; set; }

        /// <summary>Runs before a pulse touches anything, with the physical slot it is for.</summary>
        public Func<int, Task>? BeforePulse { get; set; }

        /// <summary>Every pulse, in order: the physical slot and whether every other door was shut then.</summary>
        public List<(int Slot, bool OtherDoorsShut)> Pulses { get; } = [];

        public async Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforePulse is { } beforePulse)
            {
                await beforePulse(slotIndex + 1);
            }

            UnlockCount++;
            Pulses.Add((
                slotIndex + 1,
                _lockers.Where(locker => locker.SlotIndex != slotIndex)
                    .All(locker => locker.IsKnown && locker.IsLocked && locker.UnlockOutputRaw is false)));
            UpdateLocker(slotIndex, locker => locker with
            {
                LockFeedbackRaw = false,
                UnlockOutputRaw = true
            });
        }

        public Task<LockerSnapshot> WaitForLockerAsync(
            int slotIndex,
            Func<LockerSnapshot, bool> predicate,
            TimeSpan timeout,
            TimeSpan stableWindow,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _waitCallCount++;
            if (FailOnWaitCall == _waitCallCount)
            {
                throw new IOException("模拟反馈中断");
            }

            for (int attempt = 0; attempt < 8; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LockerSnapshot locker = _snapshot.GetLocker(slotIndex);
                if (predicate(locker))
                {
                    return Task.FromResult(locker);
                }

                if (locker.IsKnown && !locker.IsLocked && locker.UnlockOutputRaw is true)
                {
                    if (!StuckOutputSlots.Contains(slotIndex + 1))
                    {
                        UpdateLocker(slotIndex, current => current with { UnlockOutputRaw = false });
                    }
                }
                else if (locker.IsKnown && !locker.IsLocked && locker.UnlockOutputRaw is false)
                {
                    if (NeverEmptiedSlots.Contains(slotIndex + 1))
                    {
                        UpdateLocker(slotIndex, current => current with { LockFeedbackRaw = true });
                    }
                    else if (_correction && locker.HasCargo)
                    {
                        UpdateLocker(slotIndex, current => current with { LightCurtainRaw = true });
                    }
                    else
                    {
                        _clock.Advance(OperatorDelay);
                        UpdateLocker(slotIndex, current => current with
                        {
                            LockFeedbackRaw = true,
                            LightCurtainRaw = _correction ? false : true
                        });
                    }
                }
            }

            throw new TimeoutException("模拟反馈超时");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void OpenDoor(int slotIndex) =>
            UpdateLocker(slotIndex, current => current with { LockFeedbackRaw = false });

        /// <summary>The door is open and the unlock output still reads energised, as right after a pulse.</summary>
        public void OpenDoorWithOutputActive(int slotIndex) =>
            UpdateLocker(slotIndex, current => current with { LockFeedbackRaw = false, UnlockOutputRaw = true });

        /// <summary>The door is shut again and the lock closed, with the unlock output left as it reads.</summary>
        public void CloseDoorKeepingOutput(int slotIndex) =>
            UpdateLocker(slotIndex, current => current with { LockFeedbackRaw = true });

        /// <summary>The lock feedback input stops reading while the bus stays up.</summary>
        public void LoseLockFeedback(int slotIndex) =>
            UpdateLocker(slotIndex, current => current with { LockFeedbackRaw = null });

        public void SetUnknown()
        {
            _snapshot = IoSnapshot.Unknown(_clock.Now);
            SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(_snapshot));
        }

        private void UpdateLocker(int slotIndex, Func<LockerSnapshot, LockerSnapshot> update)
        {
            _lockers[slotIndex] = update(_lockers[slotIndex]) with { ObservedAt = _clock.Now };
            _snapshot = new IoSnapshot(true, _lockers.ToArray(), _clock.Now);
            SnapshotChanged?.Invoke(this, new ValueChangedEventArgs<IoSnapshot>(_snapshot));
        }
    }
}
