using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

public sealed class WireToGateSlotOperationExecutorTests
{
    [Fact]
    public async Task LoadUsesOnlyServerFrozenSlotsAndJournalsBeforePulses()
    {
        string directory = Path.Combine(Path.GetTempPath(), "w2g-executor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string journalPath = Path.Combine(directory, "journal.db");
        SimulationIo io = new();
        await using SqliteWireToGateJournal journal = new(journalPath);
        await journal.InitializeAsync();
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

            WireToGateOperationExecutionResult result = await executor.ExecuteAsync(command, null);

            Assert.Equal("COMPLETED", result.OverallOutcome);
            Assert.Equal([1, 2], result.SlotResults.Select(slot => slot.SlotNo));
            Assert.All(result.SlotResults, slot => Assert.Equal("COMPLETED", slot.Outcome));
            Assert.Equal(2, io.UnlockCount);
            WireToGateRecoveryState checkpoint = await journal.ReadRecoveryStateAsync();
            Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, checkpoint.ProvenRecoveryCheckpoint);
            Assert.Equal(command.SlotOperationAttemptId, checkpoint.UnsettledSlotOperationAttemptId);

            await executor.MarkResultRecordedAsync(command.SlotOperationAttemptId);
            checkpoint = await journal.ReadRecoveryStateAsync();
            Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, checkpoint.ProvenRecoveryCheckpoint);
            Assert.Null(checkpoint.UnsettledSlotOperationAttemptId);
        }
    }

    [Fact]
    public async Task UnloadAllTargetSlotsRequiresEverySlotToReachEmpty()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(initialCargo: true, finalCargo: false);
        WireToGateSlotOperationCommand command = CreateCommand(
            OperationType.Unload,
            [1, 2, 3, 4, 5, 6, 7, 8],
            expectedOccupied: false);

        WireToGateOperationExecutionResult result =
            await fixture.Executor.ExecuteAsync(command, null);

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
    public async Task UnknownSnapshotFailsClosedWithoutUnlock()
    {
        await using TestFixture fixture = await TestFixture.CreateAsync();
        fixture.Io.SetUnknown();
        WireToGateOperationExecutionResult result =
            await fixture.Executor.ExecuteAsync(
                CreateCommand(OperationType.Load, [1], expectedOccupied: true),
                null);

        Assert.Equal("FAILED", result.OverallOutcome);
        Assert.Equal(0, fixture.Io.UnlockCount);
        Assert.Contains("SLOT_STATE_UNKNOWN", result.SlotResults.Single().ReasonCodes);
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
            bool finalCargo = true)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "w2g-executor",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
            await journal.InitializeAsync();
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
}
