using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

public sealed class WireToGateRecoveryVectorExecutorTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-04")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
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
    [Trait("IntegrationSlice", "W2G-IS-06")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
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
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
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
    [Trait("IntegrationSlice", "W2G-IS-03")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
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
    [Trait("IntegrationSlice", "W2G-IS-07")]
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
