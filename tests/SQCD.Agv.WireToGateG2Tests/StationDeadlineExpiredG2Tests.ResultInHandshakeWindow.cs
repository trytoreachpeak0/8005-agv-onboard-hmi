using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 结果行在「握手读完发件箱之后、就绪之前」落盘（<c>trytoreachpeak0/8005-agv-onboard-hmi#132</c>，来源是 onboard-hmi#127
/// 审查后续第 4 条）。这一次握手的补发已经读过发件箱，看不到这一行；会话又还没就绪，结果发送口拒绝它
/// （<c>WIRE_TO_GATE_NOT_READY</c>，先落盘再拒绝）。能把它送出去的只剩就绪之后的恢复投影。两种坏法都要抓：
/// 结果根本不发（车停在 <c>RecoveryRequired</c> 等一条永远不来的结果），或者发了、结算了两次。
/// </summary>
/// <remarks>
/// 窗口是确定性构造的，不靠时序碰运气：<see cref="HandshakeWindowJournal"/> 把握手卡在窗口的一端，卡住期间让装货
/// 走完，结果行就只能落在窗口里；测试先断言它确实落在里面（卡住期间写入、当时会话是 <c>Recovering</c>、服务端还没
/// 收到它），再放行握手、断言结果。两端各跑一遍：刚读完发件箱（之后的恢复状态报告会看到这次还没结的 attempt），
/// 和恢复状态报告已被确认、就绪还没读（报告发出时这一行还不存在）。
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    public enum HandshakeWindowEnd
    {
        /// <summary>The handshake has just read the unacknowledged outbox for its replay.</summary>
        AfterOutboxRead,

        /// <summary>The handshake's RecoveryStateReport has been acknowledged; the readiness is not read yet.</summary>
        AfterRecoveryReportAck
    }

    /// <summary>
    /// 结果在就绪之后才发出，服务端恰好收到一次；attempt 恰好结算一次（<c>MarkResultRecordedAsync</c> 只被调用一次、
    /// 未结算清掉一次）；会话回到 <c>Ready</c>，锁只开过一次。
    /// </summary>
    [Theory]
    [InlineData(HandshakeWindowEnd.AfterOutboxRead)]
    [InlineData(HandshakeWindowEnd.AfterRecoveryReportAck)]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-CONNECTION-LOSS-SAFE-FINISH")]
    public async Task AResultPutOnFileInsideTheHandshakeWindowIsSentAfterTheReadinessAndSettledOnce(
        HandshakeWindowEnd windowEnd)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        HandshakeWindowJournal window = null!;
        HandshakeTimeline timeline = new();
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.ReplayJourneySnapshotsWithStableIdentity = true;
                server.EnvelopeReceived += (connection, messageType, _) =>
                    timeline.Add($"received:{connection}:{messageType}");
            },
            wrapJournal: inner => window = new HandshakeWindowJournal(inner),
            // Registered before the business service's own handler, so a readiness is on the timeline before the
            // restore it starts can send anything.
            observeSession: session => session.StateChanged += (_, args) =>
                timeline.Add($"readiness:{args.Value.SessionGeneration}:{args.Value.Readiness}"));
        window.Observe(harness.Client);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);

        await harness.Client.DisconnectAsync();
        window.HoldAt(windowEnd);
        Task<WireToGateSessionSnapshot> reconnect = harness.Client.ConnectAndRecoverAsync(token);
        await window.Entered.WaitAsync(TimeSpan.FromSeconds(10), token);
        long generation = harness.Client.Current.SessionGeneration
            ?? throw new InvalidOperationException("The held handshake has no session generation.");
        int connection = harness.Server.Received.Max(item => item.Connection);

        // The load ends while the handshake is held.
        harness.Io.CloseDoor(0, cargo: true);
        await Harness.WaitUntilAsync(
            () => window.ResultSavedWhileHeld is not null && harness.HasEvent("RESULT_ACK_PENDING"),
            "the load's result on file and refused as not ready",
            token,
            harness.DescribeEvents);

        // The premise: the row landed inside the window -- written while the handshake was held, on a connected session
        // of this generation that was not yet ready -- and nothing has sent it.
        WireToGateSessionSnapshot atSave = window.ResultSavedWhileHeld!;
        Assert.True(atSave.Connected);
        Assert.Equal(generation, atSave.SessionGeneration);
        Assert.Equal(WireToGateSessionReadiness.Recovering, atSave.Readiness);
        Assert.False(reconnect.IsCompleted);
        Assert.Equal(WireToGateSessionReadiness.Recovering, harness.Client.Current.Readiness);
        Assert.False((await harness.Journal.ReadOutgoingByDeduplicationKeyAsync(ResultKey, token))!.Acknowledged);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "OperationResult");

        window.Release();
        await reconnect;
        await Harness.WaitUntilAsync(
            () => harness.Client.Current.Readiness == WireToGateSessionReadiness.Ready
                && harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId is null,
            "the session back to Ready and the load settled",
            token,
            () => $"{harness.DescribeEvents()}{Environment.NewLine}{timeline.Describe()}");
        // Room for a second send or a second settlement to show itself.
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);

        // Sent once, on this connection, after the vehicle took this generation's readiness.
        var result = Assert.Single(harness.Server.ReceivedEnvelopes, item => item.MessageType == "OperationResult");
        Assert.Equal(connection, result.Connection);
        Assert.Equal(AttemptId, result.MessageId);
        Assert.Equal("COMPLETED", harness.SingleResult("OperationResult").GetProperty("overallOutcome").GetString());
        string[] entries = timeline.Snapshot();
        int readiness = Array.FindIndex(
            entries,
            entry => entry == $"readiness:{generation}:{WireToGateSessionReadiness.Ready}"
                || entry == $"readiness:{generation}:{WireToGateSessionReadiness.RecoveryRequired}");
        int arrival = Array.IndexOf(entries, $"received:{connection}:OperationResult");
        Assert.True(
            readiness >= 0 && arrival > readiness,
            $"The result has to arrive after this generation's readiness.{Environment.NewLine}{timeline.Describe()}");

        // Settled once: one recording attempt, and it is the one that cleared the attempt.
        Assert.True(
            window.RecordingCalls == 1,
            $"Expected one recording of the attempt, got {window.RecordingCalls}:{Environment.NewLine}"
            + window.DescribeRecordings());
        // A "got 2" is not by itself a defect. One schedule makes a second call with no effect: a second restore started
        // by the readiness reads the recovery state before the first restore's MarkResultRecordedAsync, takes the claim
        // only after the first has released it, finds the row acknowledged and asks to record again -- the conditional
        // write finds nothing unsettled and ends in SLOT_OPERATION_CONFLICT, which RecordAcknowledgedCompletedResultAsync
        // catches. When CI reports "got 2", read the two stacks in the message first: if the second comes from that path,
        // the product behaved correctly and this assertion is too strict for that schedule; only otherwise is it a
        // second settlement.
        Assert.Equal(1, window.RecordingsThatClearedTheAttempt);
        WireToGateRecoveryState state = harness.ReadRecoveryState(token);
        Assert.Null(state.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, state.ProvenRecoveryCheckpoint);
        Assert.Equal(1, harness.CountEvents("OPERATION_COMPLETED"));
        Assert.Equal(1, harness.Io.UnlockCount);
    }

    private const string ResultKey = $"operation-result:{AttemptId}";

    /// <summary>Readiness changes and server arrivals, in the one order they happened.</summary>
    private sealed class HandshakeTimeline
    {
        private readonly List<string> _entries = [];

        public void Add(string entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        public string[] Snapshot()
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }

        public string Describe() => string.Join(Environment.NewLine, Snapshot());
    }

    /// <summary>
    /// Holds the next handshake at one end of the window between its outbox read and its readiness, records when the
    /// load's result row is written, and counts the recordings of <see cref="AttemptId"/>.
    /// </summary>
    /// <remarks>
    /// Only the vehicle's own reads and writes pass through here, so where it holds is exactly where the handshake is:
    /// <see cref="ReadUnacknowledgedOutgoingAsync"/> is held only for the handshake's call (the pass over stale rows reads
    /// it too, and is let through), and the RecoveryStateReport's
    /// acknowledgement is marked right before the handshake reads its readiness.
    /// </remarks>
    private sealed class HandshakeWindowJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private HandshakeWindowEnd? _holdAt;
        private string? _reportMessageId;
        private int _holding;
        private int _recordingCalls;
        private int _recordingsThatCleared;
        private readonly TaskCompletionSource _resultSaveHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resultSaveReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holdResultSave;

        /// <summary>Set by the test once the session client exists; read for the session state at the save.</summary>
        private WireToGateSessionClient? _client;

        public Task Entered => _entered.Task;

        /// <summary>The session as it was when the load's result row was first written while the handshake was held.</summary>
        public WireToGateSessionSnapshot? ResultSavedWhileHeld { get; private set; }

        /// <summary>How many times <c>MarkResultRecordedAsync</c> asked to record <see cref="AttemptId"/>.</summary>
        public int RecordingCalls => Volatile.Read(ref _recordingCalls);

        private readonly List<string> _recordingStacks = [];

        /// <summary>Where each counted recording came from, for the failure message.</summary>
        public string DescribeRecordings()
        {
            lock (_recordingStacks)
            {
                return string.Join($"{Environment.NewLine}----{Environment.NewLine}", _recordingStacks);
            }
        }

        /// <summary>How many journal writes took <see cref="AttemptId"/> from unsettled to recorded.</summary>
        public int RecordingsThatClearedTheAttempt => Volatile.Read(ref _recordingsThatCleared);

        public void HoldAt(HandshakeWindowEnd end) => _holdAt = end;

        /// <summary>Completes once the load's result is parked in front of its outbox write.</summary>
        public Task ResultSaveHeld => _resultSaveHeld.Task;

        /// <summary>Parks the next outbox write of the load's result before it reaches the journal.</summary>
        public void HoldNextResultSave() => Volatile.Write(ref _holdResultSave, 1);

        public void ReleaseResultSave() => _resultSaveReleased.TrySetResult();

        /// <summary>The load's result row as it was when first written, before anything could rebind it.</summary>
        public string? ResultWireLineAsSaved { get; private set; }

        public void Release() => _released.TrySetResult();

        public void Observe(WireToGateSessionClient client) => _client = client;

        private async Task HoldAsync()
        {
            Volatile.Write(ref _holding, 1);
            _entered.TrySetResult();
            try
            {
                await _released.Task;
            }
            finally
            {
                Volatile.Write(ref _holding, 0);
            }
        }

        private bool TakeHold(HandshakeWindowEnd end)
        {
            if (_holdAt != end)
            {
                return false;
            }

            _holdAt = null;
            return true;
        }

        public async Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
            CancellationToken cancellationToken = default)
        {
            // Not only the handshake reads the outbox any more: the session client's pass over stale rows
            // (RequestStaleResend, onboard-hmi#204) does too, and holding it here would report the handshake held while
            // it is still before SessionAccepted. Told apart by the flag the pass sets.
            bool fromStalePass = WireToGateSessionClient.InStaleResendPass;
            IReadOnlyList<WireToGateDurableMessage> read = await inner.ReadUnacknowledgedOutgoingAsync(cancellationToken);
            if (!fromStalePass && TakeHold(HandshakeWindowEnd.AfterOutboxRead))
            {
                await HoldAsync();
            }

            return read;
        }

        public async Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default)
        {
            if (message.DeduplicationKey == ResultKey && Interlocked.Exchange(ref _holdResultSave, 0) == 1)
            {
                _resultSaveHeld.TrySetResult();
                await _resultSaveReleased.Task;
            }

            if (message.MessageType == "RecoveryStateReport" && _holdAt == HandshakeWindowEnd.AfterRecoveryReportAck)
            {
                _reportMessageId = message.MessageId;
            }

            bool resultInWindow = message.DeduplicationKey == ResultKey && Volatile.Read(ref _holding) == 1;
            WireToGateSessionSnapshot? atSave = resultInWindow ? _client?.Current : null;
            WireToGateDurableMessage saved = await inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);
            if (message.DeduplicationKey == ResultKey && ResultWireLineAsSaved is null)
            {
                ResultWireLineAsSaved = saved.WireLine;
            }
            if (atSave is not null && ResultSavedWhileHeld is null)
            {
                ResultSavedWhileHeld = atSave;
            }

            return saved;
        }

        public async Task MarkOutgoingAcknowledgedAsync(
            string messageId,
            string acceptedContentSha256,
            CancellationToken cancellationToken = default)
        {
            await inner.MarkOutgoingAcknowledgedAsync(messageId, acceptedContentSha256, cancellationToken);
            if (messageId == _reportMessageId && TakeHold(HandshakeWindowEnd.AfterRecoveryReportAck))
            {
                await HoldAsync();
            }
        }

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            UpdateRecoveryStateAsync(change, static _ => { }, cancellationToken);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default)
        {
            // The immediate caller, not "anywhere on the stack": a write that runs as the synchronous continuation of a
            // finished MarkResultRecordedAsync -- RecordAcknowledgedCompletedResultAsync's cache refresh right after it
            // -- still has that frame underneath, and counting it would report a second recording that never happened.
            string stack = Environment.StackTrace;
            string? caller = stack
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("at ", StringComparison.Ordinal)
                    && !line.StartsWith("at System.", StringComparison.Ordinal)
                    && !line.Contains(nameof(HandshakeWindowJournal), StringComparison.Ordinal));
            if (caller?.Contains("WireToGateSlotOperationExecutor.MarkResultRecordedAsync(", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref _recordingCalls);
                lock (_recordingStacks)
                {
                    _recordingStacks.Add(stack);
                }
            }

            return inner.UpdateRecoveryStateAsync(
                state =>
                {
                    WireToGateRecoveryState? next = change(state);
                    if (state.UnsettledSlotOperationAttemptId == AttemptId
                        && next is { UnsettledSlotOperationAttemptId: null }
                        && next.ProvenRecoveryCheckpoint == WireToGateRecoveryCheckpoint.ResultRecorded)
                    {
                        Interlocked.Increment(ref _recordingsThatCleared);
                    }

                    return next;
                },
                settled,
                cancellationToken);
        }

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

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
