using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

public sealed class WireToGateRecoveryVectorExecutorTests
{
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
        await fixture.Journal.WriteRecoveryStateAsync(
            new WireToGateRecoveryState(
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
        await fixture.Journal.WriteRecoveryStateAsync(
            new WireToGateRecoveryState(
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

    private static WireToGateRecoveryVectorContext CreateContext(
        string vectorType,
        string primaryId,
        IReadOnlyList<int> slots) =>
        new(
            vectorType,
            primaryId,
            vectorType is WireToGateRecoveryVectorTypes.LoadCompensation
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
            ScriptedIo io,
            SqliteWireToGateJournal journal,
            WireToGateRecoveryVectorExecutor executor)
        {
            Io = io;
            Journal = journal;
            Executor = executor;
        }

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
            return new TestFixture(io, journal, executor);
        }

        public async ValueTask DisposeAsync()
        {
            await Executor.DisposeAsync();
            await Journal.DisposeAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now => now;
    }

    private sealed class ScriptedIo : IIoModuleClient
    {
        private readonly IClock _clock;
        private readonly bool _correction;
        private LockerSnapshot[] _lockers;
        private IoSnapshot _snapshot;
        private int _waitCallCount;

        public ScriptedIo(
            IClock clock,
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

        public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnlockCount++;
            UpdateLocker(slotIndex, locker => locker with
            {
                LockFeedbackRaw = false,
                UnlockOutputRaw = true
            });
            return Task.CompletedTask;
        }

        public Task PulseUnlockBatchAsync(IReadOnlyCollection<int> slotIndexes, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This path never batch-unlocks.");

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
                    UpdateLocker(slotIndex, current => current with { UnlockOutputRaw = false });
                }
                else if (locker.IsKnown && !locker.IsLocked && locker.UnlockOutputRaw is false)
                {
                    if (_correction && locker.HasCargo)
                    {
                        UpdateLocker(slotIndex, current => current with { LightCurtainRaw = true });
                    }
                    else
                    {
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
