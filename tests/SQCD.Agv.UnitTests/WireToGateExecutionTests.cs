using System.Net;
using System.Text;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

public sealed class WireToGateExecutionTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public void RecoveryReportUsesCandidateCheckpointAndPendingResultShape()
    {
        JournalAttempt attempt = new(
            "00000000-0000-4000-8000-000000000201",
            "00000000-0000-4000-8000-000000000202",
            new string('d', 64),
            [1, 2],
            SlotOccupancy.Occupied,
            2,
            JournalAttemptStatus.ResultPendingAck,
            "{\"outcome\":\"COMPLETED\"}",
            DateTimeOffset.UtcNow);

        PendingResultReference pending = WireToGateProtocol.ToPendingResultReference(attempt);

        Assert.Equal("RESULT_RECORDED", WireToGateProtocol.SelectRecoveryCheckpoint([attempt]));
        Assert.Equal("OperationResult", pending.MessageType);
        Assert.Equal(attempt.MessageId, pending.MessageId);
        Assert.Equal(attempt.SlotOperationAttemptId, pending.BusinessId);
        Assert.Matches("^[0-9a-f]{64}$", pending.ContentSha256);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void AuthoritativeProjectionRejectsSameRevisionConflictAndLocalSlotChoice()
    {
        AuthoritativeJourneyProjectionStore store = new();
        AuthoritativeJourneyProjection first = new(
            "D-001", 7, new string('a', 64), "WIRE_TO_GATE", "WB-17", "A1-GATE-01", 3, [1, 2, 3]);
        store.Apply(first);
        store.Apply(first);

        Assert.Equal(first, store.Current);
        Assert.Throws<InvalidDataException>(() => store.Apply(first with
        {
            ContentHash = new string('b', 64),
            TargetSlots = [4, 5, 6]
        }));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task JournaledBatchExecutesPhysicalPulseOnlyOnceAcrossReplay()
    {
        string directory = Path.Combine(Path.GetTempPath(), "onboard-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteOnboardExecutionJournal journal = new(Path.Combine(directory, "journal.db"));
            await journal.InitializeAsync(TestContext.Current.CancellationToken);
            RecordingSlotProvider provider = new();
            WireToGateSlotExecutor executor = new(provider, journal, TimeProvider.System);
            SlotOperationRequest request = new(
                "ATTEMPT-001", "MESSAGE-001", new string('a', 64), [1, 2, 3],
                SlotOccupancy.Occupied, 0, DateTimeOffset.UtcNow);

            SlotOperationExecutionResult first = await executor.ExecuteAsync(
                request, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1),
                TestContext.Current.CancellationToken);
            SlotOperationExecutionResult replay = await executor.ExecuteAsync(
                request, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1),
                TestContext.Current.CancellationToken);

            Assert.True(first.Success);
            Assert.True(replay.Success);
            Assert.True(replay.Replay);
            Assert.Equal(1, provider.PulseCount);
            Assert.Equal([1, 2, 3], provider.LastPulse);
            Assert.Single(await journal.ReadUnsettledAsync(TestContext.Current.CancellationToken));
            await executor.AcknowledgeResultAsync("ATTEMPT-001", TestContext.Current.CancellationToken);
            Assert.Empty(await journal.ReadUnsettledAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    public async Task DisconnectContainmentNeverPulsesOrExpandsActiveUnlockSet()
    {
        string directory = Path.Combine(Path.GetTempPath(), "onboard-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteOnboardExecutionJournal journal = new(Path.Combine(directory, "journal.db"));
            await journal.InitializeAsync(TestContext.Current.CancellationToken);
            RecordingSlotProvider provider = new();
            WireToGateSlotExecutor executor = new(provider, journal, TimeProvider.System);

            IReadOnlyList<SlotIoState> result = await executor.SafelyFinishActiveUnlockSetAsync(
                [2, 4], TestContext.Current.CancellationToken);

            Assert.Equal([2, 4], result.Select(item => item.PhysicalSlotNumber));
            Assert.Equal(0, provider.PulseCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task UnsettledIoStartedAttemptRecoversWithoutSecondPulse()
    {
        string directory = Path.Combine(Path.GetTempPath(), "onboard-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteOnboardExecutionJournal journal = new(Path.Combine(directory, "journal.db"));
            await journal.InitializeAsync(TestContext.Current.CancellationToken);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await journal.PrepareAsync(new JournalAttempt(
                "ATTEMPT-RECOVERY", "MESSAGE-RECOVERY", new string('b', 64), [1, 2],
                SlotOccupancy.Empty, 3, JournalAttemptStatus.IoStarted, null, now),
                TestContext.Current.CancellationToken);
            RecordingSlotProvider provider = new();
            WireToGateSlotExecutor executor = new(provider, journal, TimeProvider.System);

            SlotOperationExecutionResult result = await executor.ExecuteAsync(
                new SlotOperationRequest(
                    "ATTEMPT-RECOVERY", "MESSAGE-RECOVERY", new string('b', 64), [1, 2],
                    SlotOccupancy.Empty, 3, now),
                TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1),
                TestContext.Current.CancellationToken);

            Assert.True(result.RecoveryRequired);
            Assert.True(result.Replay);
            Assert.Equal(0, provider.PulseCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task HttpProviderConsumesBusinessStatesAndConfiguredEndpoint()
    {
        RecordingHttpHandler handler = new(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/slots", request.RequestUri?.AbsolutePath);
            string json = "[" + string.Join(',', Enumerable.Range(1, 8).Select(slot =>
                $$"""{"slotNo":{{slot}},"online":true,"occupancy":"EMPTY","lockState":"LOCKED","unlockOutputState":"RESET","lightCurtainState":"CLEAR","observedAt":"2026-08-25T00:00:00Z"}""")) + "]";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        await using HttpSimulatorSlotIoProvider provider = new(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:58006")
        });

        IReadOnlyList<SlotIoState> states = await provider.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(8, states.Count);
        Assert.All(states, state =>
        {
            Assert.Equal(SlotOccupancy.Empty, state.Occupancy);
            Assert.Equal(SlotDoorLock.Locked, state.DoorLock);
            Assert.Equal(UnlockOutputState.Reset, state.UnlockOutput);
        });
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public void DepartureSafetyRequiresFreshKnownEightSlotClosure()
    {
        DateTimeOffset now = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        SlotIoState[] safe = Enumerable.Range(1, 8).Select(slot => new SlotIoState(
            slot, true, SlotOccupancy.Empty, SlotDoorLock.Locked, UnlockOutputState.Reset,
            now - TimeSpan.FromMilliseconds(100))).ToArray();

        Assert.True(PreDepartureSafetyEvaluator.IsSafe(safe, now, TimeSpan.FromSeconds(1)));
        Assert.False(PreDepartureSafetyEvaluator.IsSafe(
            safe.Select(item => item.PhysicalSlotNumber == 4
                ? item with { Occupancy = SlotOccupancy.Unknown }
                : item).ToArray(), now, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-04")]
    public async Task UnloadClosesOnlyAfterEveryTargetIsEmptyLockedAndReset()
    {
        string directory = Path.Combine(Path.GetTempPath(), "onboard-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteOnboardExecutionJournal journal = new(Path.Combine(directory, "journal.db"));
            await journal.InitializeAsync(TestContext.Current.CancellationToken);
            RecordingSlotProvider provider = new() { PulseMakesOccupied = false };
            WireToGateSlotExecutor executor = new(provider, journal, TimeProvider.System);

            SlotOperationExecutionResult result = await executor.ExecuteAsync(
                new SlotOperationRequest(
                    "UNLOAD-001", "MESSAGE-UNLOAD", new string('c', 64), [1, 2, 3],
                    SlotOccupancy.Empty, 0, DateTimeOffset.UtcNow),
                TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1),
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.All(result.FinalStates.Where(item => item.PhysicalSlotNumber <= 3), item =>
            {
                Assert.Equal(SlotOccupancy.Empty, item.Occupancy);
                Assert.Equal(SlotDoorLock.Locked, item.DoorLock);
                Assert.Equal(UnlockOutputState.Reset, item.UnlockOutput);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingSlotProvider : ISlotIoProvider
    {
        private readonly HashSet<int> _occupied = [];
        public int PulseCount { get; private set; }
        public IReadOnlyList<int> LastPulse { get; private set; } = [];
        public bool PulseMakesOccupied { get; init; } = true;

        public Task<IReadOnlyList<SlotIoState>> ReadAllAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            IReadOnlyList<SlotIoState> states = Enumerable.Range(1, 8).Select(slot => new SlotIoState(
                slot, true, _occupied.Contains(slot) ? SlotOccupancy.Occupied : SlotOccupancy.Empty,
                SlotDoorLock.Locked, UnlockOutputState.Reset, DateTimeOffset.UtcNow)).ToArray();
            return Task.FromResult(states);
        }

        public Task PulseUnlockAsync(IReadOnlyList<int> physicalSlotNumbers, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            PulseCount++;
            LastPulse = physicalSlotNumbers.ToArray();
            foreach (int slot in physicalSlotNumbers)
            {
                if (PulseMakesOccupied) _occupied.Add(slot); else _occupied.Remove(slot);
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(response(request));
        }
    }
}
