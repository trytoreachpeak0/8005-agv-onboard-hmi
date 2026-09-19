using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 重启后遗留 attempt 与服务端重发同一命令的占位竞态（<c>trytoreachpeak0/8005-agv-onboard-hmi#124</c> 第 2 条，
/// onboard-hmi#120 审查后续）：重发的 <c>SlotOperationCommand</c> 占住 attempt、判定「已开始未结算」后只发
/// <c>OPERATION_REPLAY</c> 就释放占位。由 <c>Ready</c> 触发的恢复判断若正好撞上这段占位，得到 <c>InFlight</c> 静默退出，
/// 既不结算也不发布恢复投影，要等下一次会话状态变化才补上。
/// </summary>
/// <remarks>
/// <b>这条竞态在真服务端上到不了</b>（onboard-hmi#128）。它要的是「会话 <c>Ready</c>、而上个进程留下的 attempt 还没结算」：
/// 重启后的恢复状态报告带着这个遗留 attempt，真服务端据此答 <c>RECOVERY_REQUIRED</c>（<c>WireToGateStore.DecideReadinessAsync</c>
/// 的 <c>noPendingFacts</c>），遗留 attempt 的中断结算报 <c>UNKNOWN</c>，服务端判 <c>RecoveryRequired</c>、不结算，会话在管理员
/// 恢复之前一直不 <c>Ready</c>；重发命令又在 <c>JourneyRuntimeEngine.AdvanceAsync</c> 的就绪门之后，一直不发。即便服务端违约在
/// <c>RecoveryRequired</c> 下重发命令，车载端也按 onboard-hmi#127 直接以 <c>ACTION_NOT_ALLOWED_IN_STATE</c> 拒绝，走不到占位接手。
/// 所以这里用 <see cref="FakeControlServer.AnswerReadyOverPendingFactsForTest"/> 显式造出「带着遗留 attempt 仍答 <c>READY</c>」这一
/// 违约，测的是车载端占位逻辑在这种违约下仍只恢复一次，不是一条真服务端会走的路径。
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>
    /// 握手后的 <c>Ready</c> 触发的恢复判断被扣在读恢复状态那一步，重发的命令走到「已开始未结算」分支、持有占位时才放行，
    /// 于是它确定地撞上占位。占位释放后，遗留操作在没有任何新会话状态变化的情况下被中断结算（不开锁、报
    /// <c>UNKNOWN</c>）并发布 <c>RecoveryRequired</c> 投影，结果与投影都只出现一次。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ALeftoverWhoseRestoreRanIntoTheReplayedCommandsClaimIsStillRestoredOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        FakeControlServer first;
        await using (Harness beforeRestart = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server => server.StationDepartureDeadlineAt = null,
            journalPath: journalPath))
        {
            first = beforeRestart.Server;
            await beforeRestart.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        }

        HoldingJournal? holding = null;
        await using Harness afterRestart = await Harness.StartAsync(
            new FakeIoModuleClient(),
            token,
            server =>
            {
                // The stop's journey is not what this race is about, and pushed to a session over an unsettled leftover
                // it would be one more thing the real server never sends.
                server.SendJourneySnapshotsAfterRecovery = false;
                // The race needs the handshake to say READY over the leftover the report names, which the real server
                // never does (onboard-hmi#128); the outbox then resends the attempt's command after that readiness line.
                server.AnswerReadyOverPendingFactsForTest = true;
                // The leftover's UNKNOWN settlement makes the real server announce RECOVERY_REQUIRED on its ack, and the
                // vehicle then restores the same operation a second time (onboard-hmi#139). Kept off until #139 is
                // fixed: the restore this test counts is the one the replayed command's claim ran into.
                server.IgnoreRefusedResultsForReadinessForTest = true;
                server.SendSlotOperationCommandAfterRecovery = true;
                server.AdoptDurableRecoveryMemoryFrom(first);
                // Acknowledging the vehicle's first safety change would republish readiness -- the "next session
                // state change" the restore must not have to wait for.
                server.AnswerSafetyStateChanged = false;
            },
            journalPath: journalPath,
            baselineRevision: 2,
            observe: business => business.OperatorEventPublished += (_, args) =>
            {
                // Raised by the replayed command while it still holds the attempt's claim.
                if (args.Value.Kind == "OPERATION_REPLAY")
                {
                    holding!.Release();
                }
            },
            wrapJournal: inner => holding = new HoldingJournal(inner),
            observeSession: session => session.StateChanged += (_, args) =>
            {
                if (args.Value.Readiness == WireToGateSessionReadiness.Ready)
                {
                    holding!.HoldNextRecoveryStateReadOnThisThread();
                }
            });

        await afterRestart.WaitForInboundAsync("OperationResult", token);
        await afterRestart.WaitForEventAsync("OPERATION_RECOVERY_REQUIRED", token);
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);

        Assert.True(holding!.ReadHeld, "the restore that readiness started was never held");
        Assert.True(holding.ReleasedWhileClaimHeld, "the restore never ran into the replayed command's claim");
        Assert.Single(afterRestart.Server.ReceivedEnvelopes, item => item.MessageType == "OperationResult");
        Assert.Equal("UNKNOWN", afterRestart.SingleResult("OperationResult").GetProperty("overallOutcome").GetString());
        Assert.Equal(1, afterRestart.DescribeEvents().Split(Environment.NewLine).Count(
            line => line.StartsWith("OPERATION_RECOVERY_REQUIRED:", StringComparison.Ordinal)));
        Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, afterRestart.Business.CurrentOperationSnapshot?.Stage);
        Assert.Equal(AttemptId, afterRestart.Business.CurrentOperationSnapshot?.SlotOperationAttemptId);
        Assert.Equal(0, afterRestart.Io.UnlockCount);
    }

    /// <summary>
    /// 补跑只为「上个进程留下的 attempt」。授权的装货取消正在本进程执行、原装货命令被服务端重发时，接手分支同样成立，但那个
    /// attempt 归取消管：重发只出一条 <c>OPERATION_REPLAY</c>，界面快照与等待计时都不变，不补跑恢复判断把它投影成
    /// 「恢复向量尚未完成」（hmi#124 独立审查）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AResendDuringAnInFlightCancellationLeavesTheOperationProjectionAlone()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
            });
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        await Harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight load cancellation entry to be offered",
            token);

        Task<bool> cancellation = harness.Business.RequestLoadCancellationAsync("现场不装了。", token);
        await Harness.WaitUntilAsync(
            () => harness.ReadRecoveryState(token).RecoveryVector is not null,
            "the authorized cancellation to be journaled as a vector",
            token);
        await Task.Delay(TimeSpan.FromMilliseconds(300), token);
        WireToGateHmiOperationSnapshot? before = harness.Business.CurrentOperationSnapshot;
        object? waitBefore = harness.Business.CurrentExpectedActionWait;

        await harness.Server.ResendSlotOperationCommandAsync();
        await harness.WaitForEventCountAsync("OPERATION_REPLAY", 1, token);
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);

        Assert.Same(before, harness.Business.CurrentOperationSnapshot);
        Assert.Equal(waitBefore, harness.Business.CurrentExpectedActionWait);

        harness.Io.CloseDoor(0, cargo: false);
        Assert.True(await cancellation, harness.DescribeEvents());
        Assert.Equal(1, harness.Io.UnlockCount);
    }

    /// <summary>
    /// Holds one read of the recovery state -- the next one made on the thread that armed it -- until
    /// <see cref="Release"/>, and answers it with the state read at arming time.
    /// </summary>
    /// <remarks>
    /// Armed from a <c>Ready</c> handler that runs ahead of the business service's, the held call is the restore
    /// that readiness starts: an async method runs synchronously up to its first await, on the thread that raised
    /// the event. The completion source runs its continuations inline, so the restore goes on inside
    /// <see cref="Release"/> -- on the replaying command's thread, while that command still holds the claim.
    /// The restore reads through the business service's cached read, which is an
    /// <see cref="IWireToGateJournal.UpdateRecoveryStateAsync(Func{WireToGateRecoveryState, WireToGateRecoveryState?}, Action{WireToGateRecoveryState}, CancellationToken)"/>
    /// that writes nothing since onboard-hmi#129, so that call is held the same way as a plain read.
    /// </remarks>
    private sealed class HoldingJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private readonly TaskCompletionSource<WireToGateRecoveryState> _hold = new();
        private WireToGateRecoveryState? _heldState;
        private int _armedThread = -1;
        private int _armed;

        public bool ReleasedWhileClaimHeld { get; private set; }

        /// <summary>Whether the armed read was made and held, rather than answered straight away.</summary>
        public bool ReadHeld { get; private set; }

        public void HoldNextRecoveryStateReadOnThisThread()
        {
            if (Interlocked.Exchange(ref _armed, 1) != 0)
            {
                return;
            }

            _heldState = inner.ReadRecoveryStateAsync().GetAwaiter().GetResult();
            Volatile.Write(ref _armedThread, Environment.CurrentManagedThreadId);
        }

        public void Release()
        {
            if (_heldState is { } state && _hold.TrySetResult(state))
            {
                ReleasedWhileClaimHeld = true;
            }
        }

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default)
        {
            if (!TakeTheHeldRead())
            {
                return inner.ReadRecoveryStateAsync(cancellationToken);
            }

            ReadHeld = true;
            return _hold.Task;
        }

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default)
        {
            if (!TakeTheHeldRead())
            {
                return inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);
            }

            ReadHeld = true;
            return AnswerTheHeldReadAsync();

            async Task<WireToGateRecoveryState?> AnswerTheHeldReadAsync()
            {
                WireToGateRecoveryState state = await _hold.Task;
                if (change(state) is not null)
                {
                    throw new InvalidOperationException("The held call was expected to be a read.");
                }

                settled(state);
                return null;
            }
        }

        private bool TakeTheHeldRead() =>
            Interlocked.CompareExchange(ref _armedThread, -2, Environment.CurrentManagedThreadId)
            == Environment.CurrentManagedThreadId;

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

        public Task WriteRecoveryStateAsync(
            WireToGateRecoveryState state,
            CancellationToken cancellationToken = default) =>
            inner.WriteRecoveryStateAsync(state, cancellationToken);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, cancellationToken);

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

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
