using System.Text.Json;
using SQCD.Agv.Application;
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
    /// 服务端拒绝：待答复的取消去掉，装货照常往下走，开 1 号仓等放货；不报取消结果。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ARefusedCancellationPressedBeforeTheFirstPulseLetsTheLoadGoOn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        TaskCompletionSource authorizationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (Harness harness, BeforeFirstPulseJournal fence) = await StartPendingCancellationStopAsync(
            io,
            Harness.NewJournalPath(),
            server =>
            {
                server.LoadCancellationAuthorizationHold = authorizationHeld.Task;
                server.LoadCancellationDecision = "REJECTED";
            },
            token);
        await using Harness _ = harness;
        using ReleaseOnExit releaseAuthorization = new(() => authorizationHeld.TrySetResult());

        Task<bool> press = await PressBeforeTheFirstPulseAsync(harness, io, fence, token);
        await HoldsForAWhileAsync(() => Assert.Equal(0, io.UnlockCount), token);

        authorizationHeld.SetResult();
        Assert.False(await press);
        await harness.WaitUntilAsync(
            () => io.UnlockCount == 1 && ReadJournal(harness, token).ActiveUnlockSlots.SequenceEqual([1]),
            "the load to open slot 1 once the cancellation is refused",
            token);
        Assert.Null(ReadJournal(harness, token).PendingLoadCancellation);
        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Contains(harness.Events, item => item.Kind == "RECOVERY_BLOCKED");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 一直没有答复、站点期限已过：倒计时行照期限说「正在取消本站装货」，门一直不开，装货不因这段等待结成 UNKNOWN、不报结果；
    /// 之后被拒绝，装货照常开门，提示是期限过后的那一句。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnUnansweredCancellationPastTheDeadlineKeepsTheDeadlineAndTheDoorsShut()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        TaskCompletionSource authorizationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTimeOffset deadline = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(20);
        (Harness harness, BeforeFirstPulseJournal fence) = await StartPendingCancellationStopAsync(
            io,
            Harness.NewJournalPath(),
            server =>
            {
                server.LoadCancellationAuthorizationHold = authorizationHeld.Task;
                server.LoadCancellationDecision = "REJECTED";
            },
            token,
            stationDepartureDeadlineAt: deadline);
        await using Harness _ = harness;
        using ReleaseOnExit releaseAuthorization = new(() => authorizationHeld.TrySetResult());

        Task<bool> press = await PressBeforeTheFirstPulseAsync(harness, io, fence, token);
        await HoldsForAWhileAsync(
            () =>
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                Assert.Equal(
                    WireToGateStationDeadlineText.CancellingText,
                    harness.Business.DescribeExpiredStationDeadline(
                        new(deadline, now, StationDepartureCountdownFormatter.Format(deadline, now))));
                Assert.Equal(0, io.UnlockCount);
                Assert.Equal(AttemptA, ReadJournal(harness, token).UnsettledSlotOperationAttemptId);
                Assert.Empty(ReceivedPayloads(harness, "OperationResult"));
                Assert.Empty(ReceivedPayloads(harness, "LoadCancellationResult"));
            },
            token);

        authorizationHeld.SetResult();
        Assert.False(await press);
        await harness.WaitUntilAsync(
            () => io.UnlockCount == 1
                && harness.Events.Any(item => item.Kind == "OPERATION_PROGRESS"
                    && item.Message.StartsWith("请放入货物并关闭1号仓门", StringComparison.Ordinal)),
            "the load to open slot 1 with the prompt past the deadline",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 等答复时车载程序重启：日志簿里留着待答复的取消，续跑装货的路径照同一口径不续跑、不开门；本进程没人按过，结算不自动再问，
    /// 等现场。操作员再按一次，用原来的 cancellationId 再问，授权到达，取消不开门，报 <c>ALL_EMPTY</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ARestartWhileTheCancellationWaitsForItsAnswerOpensNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        TaskCompletionSource authorizationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (Harness first, BeforeFirstPulseJournal fence) = await StartPendingCancellationStopAsync(
            io,
            journalPath,
            server => server.LoadCancellationAuthorizationHold = authorizationHeld.Task,
            token);
        await using Harness _ = first;
        using ReleaseOnExit releaseAuthorization = new(() => authorizationHeld.TrySetResult());

        Task<bool> press = await PressBeforeTheFirstPulseAsync(first, io, fence, token);
        await HoldsForAWhileAsync(() => Assert.Equal(0, io.UnlockCount), token);
        string? cancellationId = ReadJournal(first, token).PendingLoadCancellation?.CancellationId;
        Assert.NotNull(cancellationId);
        await first.StopVehicleAsync();
        await Record.ExceptionAsync(() => press);
        first.Server.SimulateOnboardProcessRestart();

        await using Harness afterRestart = await Harness.StartAgainstAsync(
            first.Server,
            token,
            journalPath,
            io,
            messageTimeout: TimeSpan.FromSeconds(30));
        // Nobody has pressed in this process, so the settlement does not ask again by itself; it waits for the operator.
        await afterRestart.WaitUntilAsync(
            () => afterRestart.Logger.Entries.Any(entry => entry.Message.StartsWith(
                "未收到答复的装货取消不自动重新申请，等待现场确认",
                StringComparison.Ordinal)),
            "the restarted vehicle to keep the unanswered cancellation for the operator",
            token);
        await HoldsForAWhileAsync(
            () =>
            {
                Assert.Equal(0, io.UnlockCount);
                Assert.Empty(ReadJournal(afterRestart, token).ActiveUnlockSlots);
                Assert.Equal(cancellationId, ReadJournal(afterRestart, token).PendingLoadCancellation?.CancellationId);
            },
            token);

        // Pressed again: the first press's request, asked again under its cancellationId.
        authorizationHeld.SetResult();
        Assert.True(await afterRestart.Business.RequestLoadCancellationAsync("现场确认不装了。", token));
        await afterRestart.WaitUntilAsync(
            () => ReceivedPayloads(afterRestart, "LoadCancellationResult").Length == 1,
            "the cancellation result after the restart",
            token);
        JsonElement result = Assert.Single(ReceivedPayloads(afterRestart, "LoadCancellationResult"));
        Assert.Equal(cancellationId, result.GetProperty("cancellationId").GetString());
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(0, io.UnlockCount);
        Assert.Empty(afterRestart.UiErrors);
    }

    /// <summary>
    /// 第二阶段审查 S1：答复丢了、装货停着等，这时锁存严重安全故障。复位因为装货没结束被拒，所以必须能再按一次取消：v6 的取消
    /// 从不开门，锁存期间照样放行同一 cancellationId 的重问。屏上说明怎么收口；再按，用原 id 再问，授权到达，报 <c>ALL_EMPTY</c>、
    /// 不开门；之后故障可以复位。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ALatchWhileTheLoadWaitsForALostAnswerLeavesTheSameCancellationToBeAskedAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        (Harness harness, BeforeFirstPulseJournal fence) = await StartPendingCancellationStopAsync(
            io,
            Harness.NewJournalPath(),
            server => server.LoadCancellationAuthorizationsToDrop = 1,
            token,
            messageTimeout: TimeSpan.FromSeconds(3));
        await using Harness _ = harness;

        Task<bool> press = await PressBeforeTheFirstPulseAsync(harness, io, fence, token);
        await Record.ExceptionAsync(() => press);
        string? cancellationId = ReadJournal(harness, token).PendingLoadCancellation?.CancellationId;
        Assert.NotNull(cancellationId);
        Assert.Equal(0, io.UnlockCount);

        LatchNow(harness);
        Assert.True(harness.Controller.IsFatalFaultLatched);
        await harness.WaitUntilAsync(
            () => harness.Events.Any(item => item.Kind == "RECOVERY_AUTHORIZATION_UNKNOWN"
                && item.Message.Contains("再按一次「取消装货」", StringComparison.Ordinal)
                && item.Message.Contains("复位", StringComparison.Ordinal)),
            "the operator told to press 「取消装货」 again before the reset",
            token);

        Assert.True(await harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token));
        JsonElement result = Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Equal(cancellationId, result.GetProperty("cancellationId").GetString());
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(0, io.UnlockCount);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { UnsettledSlotOperationAttemptId: null, RecoveryVector: null },
            "the cancellation to settle A's attempt",
            token);
        Assert.True(await harness.Controller.ClearFatalFaultAsync("operator-134", token));
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
        DateTimeOffset? stationDepartureDeadlineAt = null,
        TimeSpan? messageTimeout = null)
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
            messageTimeout: messageTimeout ?? TimeSpan.FromSeconds(30),
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
