using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 装货结果的确认落地时，这次尝试已被同一尝试号的恢复向量接管（8005-agv-onboard-hmi#259）。
/// </summary>
/// <remarks>
/// 只收掉结果自己的那一份：待补发列表里这次尝试的 <c>OperationResult</c> 移除、这次装货记为最近完成的装货；向量接管的
/// 字段一个不动，尝试留给向量结清。G2 用例走的路径上 COMPLETED 结果从不进待补发列表，所以「移除待补发」只能在这里钉住。
/// </remarks>
public sealed class WireToGateSlotOperationExecutorResultUnderVectorTests
{
    private const string AttemptA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string AttemptC = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string DemandA = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    private const string CancellationId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AResultAcknowledgedUnderAVectorClosesOnlyItsOwnShare()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WireToGateRecoveryOperationContext load = new(
            "11111111-1111-4111-8111-111111111111",
            null,
            1,
            DateTimeOffset.UnixEpoch,
            DemandA,
            "22222222-2222-4222-8222-222222222222",
            AttemptA,
            OperationType.Load,
            [1, 2],
            2,
            true,
            new string('a', 64));
        WireToGateRecoveryVectorContext cancellation = new(
            WireToGateRecoveryVectorTypes.LoadCancellation,
            CancellationId,
            null,
            DemandA,
            AttemptA,
            null,
            [1, 2],
            null,
            "operator-1",
            "SESSION",
            DateTimeOffset.UnixEpoch);
        WireToGatePendingResult ownResult = new("OperationResult", AttemptA, AttemptA, new string('b', 64));
        WireToGatePendingResult otherResult = new("OperationResult", AttemptC, AttemptC, new string('c', 64));
        WireToGateSlotExecutionResult slotResult = new(
            1, "UNKNOWN", "OCCUPIED", "UNLOCKED", "INACTIVE", ["LOCK_NOT_CLOSED"]);
        WireToGateRecoveryState seeded = WireToGateRecoveryState.Empty with
        {
            UnsettledSlotOperationAttemptId = AttemptA,
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.Prepared,
            ActiveUnlockSlots = [1],
            CompletedSlots = [2],
            SlotResults = [slotResult],
            OperationContext = load,
            RecoveryVector = cancellation,
            RecoveryOperatorId = "operator-1",
            PendingResults = [ownResult, otherResult]
        };

        (WireToGateResultRecording recording, WireToGateRecoveryState after) = await RecordAsync(seeded, token);

        Assert.Equal(WireToGateResultRecording.TakenOverByRecoveryVector, recording);
        // The result's own share.
        Assert.Equal([otherResult], after.PendingResults);
        Assert.Equivalent(load, after.LastCompletedLoadOperationContext, strict: true);
        // What the vector took over, untouched.
        Assert.Equal(AttemptA, after.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.Prepared, after.ProvenRecoveryCheckpoint);
        Assert.Equal([1], after.ActiveUnlockSlots);
        Assert.Equal([2], after.CompletedSlots);
        Assert.Equivalent(new[] { slotResult }, after.SlotResults, strict: true);
        Assert.Equivalent(load, after.OperationContext, strict: true);
        Assert.Equivalent(cancellation, after.RecoveryVector, strict: true);
        Assert.Equal("operator-1", after.RecoveryOperatorId);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AResultAcknowledgedWithNoVectorStillSettlesTheAttempt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WireToGateRecoveryState seeded = WireToGateRecoveryState.Empty with
        {
            UnsettledSlotOperationAttemptId = AttemptA,
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.SafeFinishReached
        };

        (WireToGateResultRecording recording, WireToGateRecoveryState after) = await RecordAsync(seeded, token);

        Assert.Equal(WireToGateResultRecording.Settled, recording);
        Assert.Null(after.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, after.ProvenRecoveryCheckpoint);
    }

    /// <summary>
    /// 向量已经被忘掉（交接或补偿以 UNKNOWN 结束），它接管的尝试、上下文与存疑的门留着，标记记着这次尝试
    /// （8005-agv-onboard-hmi#278）。这时到的确认同样只收结果自己的那一份：不结清，活动开锁集不动，标记留着。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AResultAcknowledgedAfterTheVectorThatHeldItWasForgottenClosesOnlyItsOwnShare()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WireToGateRecoveryOperationContext load = LoadA();
        WireToGateRecoveryState seeded = WireToGateRecoveryState.Empty with
        {
            UnsettledSlotOperationAttemptId = AttemptA,
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ActiveUnlockSet,
            ActiveUnlockSlots = [1],
            OperationContext = load,
            TakenOverSlotOperationAttemptId = AttemptA
        };

        (WireToGateResultRecording recording, WireToGateRecoveryState after) = await RecordAsync(seeded, token);

        Assert.Equal(WireToGateResultRecording.TakenOverByRecoveryVector, recording);
        Assert.Equal(AttemptA, after.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.ActiveUnlockSet, after.ProvenRecoveryCheckpoint);
        Assert.Equal([1], after.ActiveUnlockSlots);
        Assert.Equivalent(load, after.OperationContext, strict: true);
        Assert.Equivalent(load, after.LastCompletedLoadOperationContext, strict: true);
        Assert.Equal(AttemptA, after.TakenOverSlotOperationAttemptId);
    }

    /// <summary>
    /// 标记只对它记着的那次尝试说话：日志簿换成了下一次尝试 C，C 的确认照常结清，旧标记随之清掉，不会挡住以后的结清
    /// （8005-agv-onboard-hmi#278）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AMarkerNamingAnEarlierAttemptDoesNotHoldTheNextOnesSettlementAndGoesWithIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WireToGateRecoveryState seeded = WireToGateRecoveryState.Empty with
        {
            UnsettledSlotOperationAttemptId = AttemptC,
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.SafeFinishReached,
            TakenOverSlotOperationAttemptId = AttemptA
        };

        (WireToGateResultRecording recording, WireToGateRecoveryState after) = await RecordAsync(seeded, token, AttemptC);

        Assert.Equal(WireToGateResultRecording.Settled, recording);
        Assert.Null(after.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, after.ProvenRecoveryCheckpoint);
        Assert.Null(after.TakenOverSlotOperationAttemptId);
    }

    /// <summary>
    /// 记录一次完成的装货，这条需求进入车上已装列表；记录同一条需求完成的卸货，它离开列表（8005-agv-onboard-hmi#209）。
    /// </summary>
    [Fact]
    public async Task ARecordedLoadPutsItsDemandOnBoardAndItsRecordedUnloadTakesItOff()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WireToGateRecoveryOperationContext other = LoadA() with
        {
            DemandId = "ffffffff-ffff-4fff-8fff-ffffffffffff",
            SlotOperationAttemptId = AttemptC,
            Slots = [5],
            ExpectedBasketCount = 1
        };
        WireToGateRecoveryState loading = WireToGateRecoveryState.Empty.WithLoadOnBoard(other) with
        {
            UnsettledSlotOperationAttemptId = AttemptA,
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ActiveUnlockSet,
            OperationContext = LoadA()
        };

        (_, WireToGateRecoveryState loaded) = await RecordAsync(loading, token);
        Assert.Equal([other.DemandId, DemandA], loaded.LoadedDemandOperationContexts!.Select(load => load.DemandId));

        string unloadAttempt = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        WireToGateRecoveryState unloading = loaded with
        {
            UnsettledSlotOperationAttemptId = unloadAttempt,
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ActiveUnlockSet,
            OperationContext = LoadA() with
            {
                SlotOperationAttemptId = unloadAttempt,
                OperationType = OperationType.Unload,
                ExpectedOccupied = false
            }
        };

        (_, WireToGateRecoveryState unloaded) = await RecordAsync(unloading, token, unloadAttempt);
        Assert.Equal([other.DemandId], unloaded.LoadedDemandOperationContexts!.Select(load => load.DemandId));
    }

    private static WireToGateRecoveryOperationContext LoadA() => new(
        "11111111-1111-4111-8111-111111111111",
        null,
        1,
        DateTimeOffset.UnixEpoch,
        DemandA,
        "22222222-2222-4222-8222-222222222222",
        AttemptA,
        OperationType.Load,
        [1, 2],
        2,
        true,
        new string('a', 64));

    private static async Task<(WireToGateResultRecording Recording, WireToGateRecoveryState After)> RecordAsync(
        WireToGateRecoveryState seeded,
        CancellationToken token,
        string attemptId = AttemptA)
    {
        string directory = Path.Combine(Path.GetTempPath(), "w2g-executor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
        await journal.InitializeAsync(token);
        await journal.UpdateRecoveryStateAsync(_ => seeded, token);
        await using WireToGateSlotOperationExecutor executor = new(
            new NoIo(),
            journal,
            new SystemClock(),
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(1)),
            () => true);

        WireToGateResultRecording recording = await executor.MarkResultRecordedAsync(attemptId, token);
        return (recording, await journal.ReadRecoveryStateAsync(token));
    }

    /// <summary>Recording a result touches no IO; any use of it here is a test bug.</summary>
    private sealed class NoIo : IIoModuleClient
    {
        public bool IsConnected => true;

        public IoSnapshot CurrentSnapshot => throw new NotSupportedException();

        public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken applicationStopping) => throw new NotSupportedException();

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LockerSnapshot> WaitForLockerAsync(
            int slotIndex,
            Func<LockerSnapshot, bool> predicate,
            TimeSpan timeout,
            TimeSpan stableWindow,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
