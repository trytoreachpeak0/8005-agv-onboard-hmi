using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 装货取消已经发出、还没答复时，这次装货不再开任何门（8005-agv-onboard-hmi#392，#868 审查）。
/// </summary>
/// <remarks>
/// <para>
/// 服务端对同一 cancellationId 照记录答复：答复之前车上收进的篮子，服务端看不到。所以取消一发出，装货就停在原地等答复：
/// 授权了由取消接手，拒绝了照常装下去，一直没有答复就一直不开门，站点期限照常走，重启以后也一样。
/// </para>
/// <para>
/// <b>修复之前</b>，可用条件只在按下那一刻看仓位；命令到了、第一次开锁之前按下（门都锁着、没篮，条件过得去），装货执行器
/// 不看这条待答复的取消，照常开 1 号仓、收下一篮、再开 2 号仓。10-26 探针：授权到达后取消照实报 <c>UNKNOWN</c>，不会报
/// <c>ALL_EMPTY</c>——那是第二道防线，这里关的是第一道。
/// </para>
/// <para>
/// <b>造法。</b>日志簿替身在执行器写下 Prepared（尝试未结、还没有开锁）之后扣住它，测试线程这时按取消；屏上入口此刻还没亮，
/// 所以直接走按键路径。按键写下待答复的取消、发出申请之后才放开执行器。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 授权扣着：执行器放开以后不开锁，操作员放进篮子也没有门可放；授权到达，取消接手、不开门，报 <c>ALL_EMPTY</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ACancellationPressedBeforeTheFirstPulseKeepsEveryDoorShutUntilItIsAnswered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        TaskCompletionSource authorizationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (Harness harness, BeforeFirstPulseJournal fence) = await StartPendingCancellationStopAsync(
            io,
            Harness.NewJournalPath(),
            server => server.LoadCancellationAuthorizationHold = authorizationHeld.Task,
            token);
        await using Harness _ = harness;
        using ReleaseOnExit releaseAuthorization = new(() => authorizationHeld.TrySetResult());

        Task<bool> press = await PressBeforeTheFirstPulseAsync(harness, io, fence, token);
        await HoldsForAWhileAsync(
            () =>
            {
                Assert.Equal(0, io.UnlockCount);
                Assert.Empty(ReadJournal(harness, token).ActiveUnlockSlots);
                Assert.False(press.IsCompleted);
            },
            token);

        authorizationHeld.SetResult();
        Assert.True(await press);
        JsonElement result = Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(0, io.UnlockCount);
        Assert.DoesNotContain(
            ReceivedPayloads(harness, "OperationResult"),
            item => item.GetProperty("slotOperationAttemptId").GetString() == AttemptA);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// Starts a pickup stop with A's two-slot load to come, its journal wrapped so the load's executor can be held at its
    /// Prepared write.
    /// </summary>
    private static async Task<(Harness Harness, BeforeFirstPulseJournal Fence)> StartPendingCancellationStopAsync(
        FakeIoModuleClient io,
        string journalPath,
        Action<FakeControlServer> configure,
        CancellationToken token,
        DateTimeOffset? stationDepartureDeadlineAt = null)
    {
        BeforeFirstPulseJournal? fence = null;
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.WorklistAt(
                        1,
                        stationDepartureDeadlineAt,
                        Payloads.ItemA,
                        Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
                server.RespondToLoadCancellationRequests = true;
                configure(server);
            },
            token,
            journalPath,
            io: io,
            messageTimeout: TimeSpan.FromSeconds(30),
            wrapJournal: inner => fence = new BeforeFirstPulseJournal(inner));
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

        return (harness, fence!);
    }

    /// <summary>
    /// Sends A's load, holds its executor at the Prepared write, presses the cancellation there and lets the executor go
    /// once the request is on the wire. Returns the press, still waiting for its answer.
    /// </summary>
    private static async Task<Task<bool>> PressBeforeTheFirstPulseAsync(
        Harness harness,
        FakeIoModuleClient io,
        BeforeFirstPulseJournal fence,
        CancellationToken token)
    {
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await harness.WaitUntilAsync(() => fence.Held, "A's executor held at its Prepared write", token);
        Assert.Equal(0, io.UnlockCount);
        Assert.Null(harness.Business.LoadCancellationBlockedReason);

        Task<bool> press = harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        await harness.WaitUntilAsync(
            () => ReceivedPayloads(harness, "LoadCancellationStartRequested").Length == 1 || press.IsCompleted,
            "the cancellation request to reach the server",
            token);
        Assert.False(press.IsCompleted);
        Assert.Equal(AttemptA, ReadJournal(harness, token).PendingLoadCancellation?.SlotOperationAttemptId);
        fence.Release();
        return press;
    }

    /// <summary>
    /// The assertion holds for three of the harness's operation timeouts: long past the moment the load would have pulsed
    /// slot 1, and past any wait of the executor's own running out.
    /// </summary>
    private static async Task HoldsForAWhileAsync(Action assertion, CancellationToken token)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTimeOffset.UtcNow < until)
        {
            assertion();
            await Task.Delay(50, token);
        }

        assertion();
    }

    /// <summary>
    /// Holds the load's executor once its Prepared write has landed -- the first write that leaves A's attempt unsettled
    /// with nothing unlocked -- until released; everything else passes.
    /// </summary>
    private sealed class BeforeFirstPulseJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public bool Held => Volatile.Read(ref _held) != 0;

        public void Release() => _release.TrySetResult();

        public async Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default)
        {
            WireToGateRecoveryState? written = await inner.UpdateRecoveryStateAsync(change, cancellationToken);
            if (written is { UnsettledSlotOperationAttemptId: AttemptA, RecoveryVector: null, PendingLoadCancellation: null }
                && written.ActiveUnlockSlots.Count == 0
                && Interlocked.Exchange(ref _held, 1) == 0)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            return written;
        }

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

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
