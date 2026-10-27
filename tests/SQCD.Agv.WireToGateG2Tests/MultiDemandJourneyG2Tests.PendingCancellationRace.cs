using System.Diagnostics;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;
using ScriptStep = SQCD.Agv.WireToGateG2Tests.FakeIoModuleClient.ScriptStep;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 按取消与装货的下一次开锁撞在一起（8005-agv-onboard-hmi#392 第二阶段审查 L1）：两边谁先，另一边都看得见。
/// </summary>
/// <remarks>
/// <para>
/// 执行器每次开锁前读两次日志簿：门外一次（有待答复的取消就原地等），声明闸里一次（闸外那次之后才记下的取消，在这里截住）。
/// 按键在同一个闸里判可用条件并记下取消；执行器发过脉冲、还没看到关好的仓，按门没关好算。
/// </para>
/// <para>
/// <b>造法。</b>日志簿替身在执行器那两次读里的某一次返回之后扣住它，测试线程在扣住时按取消。哪一次读，从调用栈认：门外那次
/// 经 <c>HoldWhileLoadCancellationUnansweredAsync</c>，闸里那次不经。只认 1 号仓已写进开锁集、还没有待答复取消的那一次。
/// 审查变异：MA 去掉闸里那次读，MB 去掉「发过脉冲的仓按没关好算」，MD 让按键不进闸——各有一条在这里变红。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 执行器在门外读完（还没有取消）之后、进闸之前，取消被记下并发出：进闸那次读截住它，1 号仓不开；授权到达，取消报
    /// <c>ALL_EMPTY</c>。去掉闸里那次读（MA），1 号仓照开。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ACancellationJournaledBetweenTheHoldAndTheGateIsCaughtInsideTheGate()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        TaskCompletionSource authorizationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (Harness harness, ExecutorReadPauseJournal pause) = await StartRaceStopAsync(
            io,
            insideTheGate: false,
            server => server.LoadCancellationAuthorizationHold = authorizationHeld.Task,
            token);
        await using Harness _ = harness;
        using ReleaseOnExit releaseAuthorization = new(() => authorizationHeld.TrySetResult());

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await harness.WaitUntilAsync(() => pause.Held, "the executor held after its hold's read", token);
        Task<bool> press = harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        await harness.WaitUntilAsync(
            () => ReceivedPayloads(harness, "LoadCancellationStartRequested").Length == 1 || press.IsCompleted,
            "the cancellation request to reach the server",
            token);
        Assert.False(press.IsCompleted);
        pause.Release();

        await HoldsForAWhileAsync(() => Assert.Equal(0, io.UnlockCount), token);
        authorizationHeld.SetResult();
        Assert.True(await press);
        Assert.Equal(
            "ALL_EMPTY",
            Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult")).GetProperty("overallOutcome").GetString());
        Assert.Equal(0, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 执行器在闸里读完（还没有取消）、正要发脉冲时按取消：按键等闸，执行器先开 1 号仓，按键看到这扇门没关好，不发申请。
    /// 按键不进闸（MD），申请在门开着时发出。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task APressWhileTheExecutorIsInsideTheGateWaitsForThePulseAndIsRefused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        (Harness harness, ExecutorReadPauseJournal pause) = await StartRaceStopAsync(
            io,
            insideTheGate: true,
            _ => { },
            token);
        await using Harness _ = harness;

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await harness.WaitUntilAsync(() => pause.Held, "the executor held inside the gate after its read", token);
        Task<bool> press = harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        await HoldsForHalfASecondAsync(
            () =>
            {
                Assert.False(press.IsCompleted);
                Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
                Assert.Null(ReadJournal(harness, token).PendingLoadCancellation);
            },
            token);
        pause.Release();

        Assert.True(await press);
        Assert.Equal(1, io.UnlockCount);
        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.Null(ReadJournal(harness, token).PendingLoadCancellation);
        Assert.Contains(harness.Events, item => item.Kind == "RECOVERY_BLOCKED");
    }

    /// <summary>
    /// 1 号仓刚发过脉冲、锁还读成锁着、输出已复位、仓空：取消照样按门没关好算，不发申请。只看读数（MB）就会把申请发出去，门随即弹开。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task APressRightAfterAPulseWhoseLockHasNotLetGoYetIsRefused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        // The output falls back at once; the lock reads released only a second later.
        io.ScriptPulse(
            1,
            new ScriptStep(TimeSpan.Zero, (fake, slot) => fake.SetUnlockOutput(slot, energised: false)),
            new ScriptStep(TimeSpan.FromSeconds(1), (fake, slot) => fake.SetLockFeedback(slot, locked: false)));
        (Harness harness, ExecutorReadPauseJournal _) = await StartRaceStopAsync(io, insideTheGate: null, _ => { }, token);
        await using Harness __ = harness;

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await harness.WaitUntilAsync(
            () => io.UnlockCount == 1
                && io.CurrentSnapshot.Lockers[0] is { IsLocked: true, HasCargo: false, UnlockOutputRaw: false },
            "slot 1 pulsed, its lock still reading shut and its output reset",
            token);

        Assert.True(await harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token));
        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.Contains(
            harness.Events,
            item => item.Kind == "RECOVERY_BLOCKED" && item.Message.Contains("LOCK_NOT_CLOSED", StringComparison.Ordinal));
        await io.ScriptsFinishedAsync();
    }

    /// <summary>
    /// A pickup stop with A's two-slot load to come, its journal wrapped to hold the load's executor after one of its reads
    /// before slot 1's pulse: <paramref name="insideTheGate"/> picks which, and <c>null</c> holds nothing.
    /// </summary>
    private static async Task<(Harness Harness, ExecutorReadPauseJournal Pause)> StartRaceStopAsync(
        FakeIoModuleClient io,
        bool? insideTheGate,
        Action<FakeControlServer> configure,
        CancellationToken token)
    {
        ExecutorReadPauseJournal? pause = null;
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
                server.RespondToLoadCancellationRequests = true;
                configure(server);
            },
            token,
            Harness.NewJournalPath(),
            io: io,
            messageTimeout: TimeSpan.FromSeconds(30),
            wrapJournal: inner => pause = new ExecutorReadPauseJournal(inner, insideTheGate));
        try
        {
            await harness.WaitUntilAsync(
                () => harness.Session.CurrentJourney.CurrentStopWorklist is not null
                    && harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
                "the worklist on a ready session",
                token);
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }

        return (harness, pause!);
    }

    /// <summary>
    /// Holds the load's executor once, after the read it makes for a pending cancellation before slot 1's pulse -- the one
    /// through the hold (outside the gate) or the one inside the gate -- until released; every other call passes.
    /// </summary>
    /// <remarks>
    /// The read is told by the call stack, which runs synchronously from the executor into this journal up to the read: the
    /// hold's goes through <c>HoldWhileLoadCancellationUnansweredAsync</c>, the gate's does not. Both are the executor's
    /// <c>IsLoadCancellationUnansweredAsync</c>; the business service never calls it.
    /// </remarks>
    private sealed class ExecutorReadPauseJournal(IWireToGateJournal inner, bool? insideTheGate) : IWireToGateJournal
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public bool Held => Volatile.Read(ref _held) != 0;

        public void Release() => _release.TrySetResult();

        public async Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default)
        {
            string stack = insideTheGate is null ? string.Empty : new StackTrace().ToString();
            WireToGateRecoveryState state = await inner.ReadRecoveryStateAsync(cancellationToken);
            if (insideTheGate is { } inside
                && stack.Contains("IsLoadCancellationUnansweredAsync", StringComparison.Ordinal)
                && stack.Contains("HoldWhileLoadCancellationUnansweredAsync", StringComparison.Ordinal) != inside
                && state is { UnsettledSlotOperationAttemptId: AttemptA, PendingLoadCancellation: null }
                && state.ActiveUnlockSlots.Contains(1)
                && Interlocked.Exchange(ref _held, 1) == 0)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            return state;
        }

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, cancellationToken);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

        public Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default) =>
            inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);

        public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
            WireToGateDurableMessage expected,
            WireToGateDurableMessage replacement,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceOutgoingForReplayAsync(expected, replacement, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByMessageIdAsync(messageId, cancellationToken);

        public Task MarkOutgoingAcknowledgedAsync(
            string messageId,
            string acceptedContentSha256,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAcknowledgedAsync(messageId, acceptedContentSha256, cancellationToken);

        public Task<WireToGateDurableMessage> MarkOutgoingAbandonedAsync(
            string messageId,
            string contentSha256,
            string reasonCode,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAbandonedAsync(messageId, contentSha256, reasonCode, cancellationToken);

        public Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadUnacknowledgedOutgoingAsync(cancellationToken);

        public Task<IReadOnlyList<WireToGateAppliedJourneySnapshot>> ReadAppliedJourneySnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadAppliedJourneySnapshotsAsync(cancellationToken);

        public Task<WireToGateAppliedJourneySnapshot> SaveAppliedJourneySnapshotAsync(
            WireToGateAppliedJourneySnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            inner.SaveAppliedJourneySnapshotAsync(snapshot, cancellationToken);

        public Task<string> ComputeContentSha256Async(CancellationToken cancellationToken = default) =>
            inner.ComputeContentSha256Async(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Release();
            return inner.DisposeAsync();
        }
    }
}
