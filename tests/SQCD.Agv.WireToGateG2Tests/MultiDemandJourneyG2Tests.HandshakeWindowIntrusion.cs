using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 一份还在路上的安全上报，既不能写进下一次握手，它随后的失败也不能把下一代会话断开（8005-agv-onboard-hmi#204）。
/// </summary>
/// <remarks>
/// <para>
/// <b>缺陷：</b><c>SendDurableCoreAsync</c> 先判「现在可以发」，再 <c>await</c> 写发件箱，<c>SendLineAsync</c> 这之后
/// 才读 <c>_writer</c>。这一段里会话换了代，报文就带着旧代次写进新连接、插在 <c>SessionHello</c> 与
/// <c>CapabilitySnapshot</c> 中间；它随后的失败又被当成当前会话的，把正在握手或刚就绪的新会话断掉。
/// </para>
/// <para>
/// <b>它不是 cs#323 那次 <c>HANDSHAKE_SEQUENCE_INVALID</c> 的来源。</b>那一次是握手按 RELIABLE 补发上一代没送到的
/// <c>SafetyStateChanged</c>，服务端在握手未完成时于 ack 之后附带一条 <c>SessionReadiness</c>，违反它自己
/// 「握手内每行只读一个答复」的约定；在服务端修，见 control-server#340。这里的缺陷是查那一次时读代码发现的，
/// 与它无关、各自成立。
/// </para>
/// <para>
/// <b>两个触发源。</b><see cref="SafetyReportTrigger.FatalFaultLatch"/> 是 #197 新接的那根线
/// （<c>OnboardController.StateChanged</c> → <c>RefreshSafetyAfterFatalFaultLatchChange</c>），
/// <see cref="SafetyReportTrigger.IoSnapshot"/> 是 #197 之前就有的那根线（<c>IIoModuleClient.SnapshotChanged</c>）。
/// 缺陷在发送口，与是谁按下去的无关，所以两格跑同一段判据。
/// </para>
/// <para>
/// <b>窗口是构造出来的，不靠时序碰运气</b>（<see cref="ReconnectRaceJournal"/>、<c>RecordingLogger</c> 的卡点）。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>What makes the vehicle judge its safety again while the session is still Ready.</summary>
    public enum SafetyReportTrigger
    {
        /// <summary>The fatal-fault latch, over the line App.xaml.cs added in #197.</summary>
        FatalFaultLatch,

        /// <summary>A fresh IO reading, over the line that has always been there.</summary>
        IoSnapshot
    }

    /// <summary>
    /// 新连接在握手走完之前只收到握手自己的报文，而且一条带旧代次的都没有；会话照常就绪，没有因为那一份发不出去
    /// 而再断一次。
    /// </summary>
    [Theory]
    [InlineData(SafetyReportTrigger.FatalFaultLatch)]
    [InlineData(SafetyReportTrigger.IoSnapshot)]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    public async Task ASafetyReportStillInFlightStaysOutOfTheNextHandshake(SafetyReportTrigger trigger)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        ReconnectRaceJournal race = null!;
        await using Harness harness = await Harness.StartAsync(
            _ => { },
            token,
            io: io,
            wrapJournal: inner => race = new ReconnectRaceJournal(inner));
        try
        {
            await WaitForSafetyReportsToSettleAsync(harness, token);
            long firstGeneration = harness.Session.Current.SessionGeneration
                ?? throw new InvalidOperationException("The first session has no generation.");
            int firstConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);

            // One safety report is under way and held just before its outbox row is written.
            race.HoldNextSafetyStateChange();
            TriggerSafetyChange(harness, io, trigger);
            await race.SafetyChangeHeld.WaitAsync(TimeSpan.FromSeconds(10), token);

            // The session changes generation underneath it, and the new handshake is held with only its
            // SessionHello out.
            race.HoldNextHandshakeAfterOutboxRead();
            await harness.Session.Client.DisconnectAsync();
            Task<WireToGateSessionSnapshot> reconnect = harness.Session.Client.ConnectAndRecoverAsync(token);
            await race.HandshakeHeld.WaitAsync(TimeSpan.FromSeconds(10), token);
            long secondGeneration = harness.Session.Current.SessionGeneration
                ?? throw new InvalidOperationException("The held handshake has no session generation.");
            Assert.NotEqual(firstGeneration, secondGeneration);
            int secondConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
            Assert.NotEqual(firstConnection, secondConnection);
            Assert.Equal(["SessionHello"], MessageTypesOn(harness, secondConnection));

            // Released here, the held report finds a connection that is no longer the one it was judged against.
            race.ReleaseSafetyChange();
            // Wait for the report to settle one way or the other -- refused and logged, or on the new connection -- rather
            // than for a fixed time: either way the assertions below then read a finished outcome, on both sides.
            await harness.WaitUntilAsync(
                () => SafetyReportFailures(harness).Length > 0 || MessageTypesOn(harness, secondConnection).Length > 1,
                "the held safety report to be refused or to reach the new connection",
                token);

            // Nothing but the handshake's own SessionHello has reached the new connection.
            Assert.Equal(["SessionHello"], MessageTypesOn(harness, secondConnection));
            // And nothing on it carries a generation that is not this one.
            Assert.Empty(ForeignGenerationsOn(harness, secondConnection, secondGeneration));
            // Kept out by the check on the connection it was judged against, not lost some other way; and the failure was
            // taken for what it is -- its connection gone -- so the handshake under way was left alone. The first half
            // without the second still loses the session: the refusal itself would disconnect the new one mid-handshake.
            (string message, Exception refusal) = Assert.Single(SafetyReportFailures(harness));
            Assert.IsType<WireToGateConnectionGoneException>(refusal);
            Assert.Contains("换代", refusal.Message, StringComparison.Ordinal);
            Assert.Contains($"会话代{firstGeneration}", message, StringComparison.Ordinal);
            Assert.Contains("不断开当前会话", message, StringComparison.Ordinal);

            // The refused report is on file, unacknowledged: its identity is what must reach the new session.
            WireToGateDurableMessage refused = Assert.Single(
                await race.ReadUnacknowledgedOutgoingAsync(token),
                row => row.MessageType == "SafetyStateChanged");
            long refusedVersion = SafetyStateVersionOf(refused.WireLine);

            race.ReleaseHandshake();
            await reconnect;
            await harness.WaitUntilAsync(
                () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
                "the new session to become Ready",
                token);

            // Not lost: once the new session is Ready, the same report -- the same messageId, the same
            // safetyStateVersion -- goes out on the new connection and is acknowledged. Refusing it is only safe because
            // this happens; a refusal that dropped the report would leave the server without that change.
            // Two independent triggers resend it, so switching off one leaves this green: the session turning Ready
            // (OnSessionStateChanged) and #197's line, which judges the safety again on every OnboardController
            // StateChanged. Only with both off does the IoSnapshot cell go red here (the FatalFaultLatch cell then
            // never reports at all and stops at its precondition). Dropping the refused report instead goes red in both.
            await harness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                    envelope.Connection == secondConnection && envelope.MessageId == refused.MessageId),
                $"the refused report {refused.MessageId} to be sent again on the new connection",
                token);
            var resent = Assert.Single(
                harness.Server.ReceivedEnvelopes,
                envelope => envelope.Connection == secondConnection && envelope.MessageId == refused.MessageId);
            Assert.Equal("SafetyStateChanged", resent.MessageType);
            Assert.Equal(refusedVersion, SafetyStateVersionOf(resent.WireLine));
            Assert.Equal(secondGeneration, GenerationOf(resent.WireLine));
            await harness.WaitUntilAsync(
                () => race.ReadOutgoingByMessageIdAsync(refused.MessageId, token).GetAwaiter().GetResult()
                    is { Acknowledged: true },
                $"the refused report {refused.MessageId} to be acknowledged",
                token);
            // Room for a late disconnect or a late write to show itself; the report above has already settled, so this is
            // only for something the fix failed to stop.
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            Assert.Equal(
                secondConnection,
                harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection));
            Assert.Empty(ForeignGenerationsOn(harness, secondConnection, secondGeneration));
            Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
            Assert.Empty(harness.UiErrors);
        }
        finally
        {
            // Whatever failed above, nothing may stay parked: the harness's disposal waits for the business
            // service's tasks, and a report still held here would keep this test from ever finishing.
            race.ReleaseSafetyChange();
            race.ReleaseHandshake();
        }
    }

    /// <summary>Where the next session is when the old session's failed report is finally acted on.</summary>
    public enum NextSessionState
    {
        /// <summary>Its handshake is under way: SessionHello out, CapabilitySnapshot not.</summary>
        Handshaking,

        /// <summary>Its handshake is done and it is Ready.</summary>
        Ready
    }

    /// <summary>
    /// 一份已经发出、正在等 <c>DurableAck</c> 的安全上报，因为它的连接被关掉而失败：业务服务不去断开会话，此后连上的
    /// 新会话——还在握手的，或已经就绪的——一直连着。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>钉住的时序：</b>等确认的一方由 <c>CloseConnectionAsync</c> 叫醒，而 <c>DisconnectAsync</c> 要到关完才
    /// <c>Publish</c>。失败处理若在这段空档里读会话状态，读到的仍是旧那一代、仍连着，按「读到的代号」判就会判成当前会话
    /// 的失败而去断开；等它真去断开时，新会话已经连上，被断开的就是新会话。
    /// </para>
    /// <para>
    /// <b>三个卡点，每一个都挪到「对方已不可能再做那件事」的时刻，不靠调度碰运气：</b>
    /// </para>
    /// <list type="number">
    /// <item><description>关连接的线程：<c>CloseConnectionAsync</c> 叫醒等确认的一方之后清空旅程，同步触发
    /// <c>JourneyChanged</c>——此刻 <c>DisconnectAsync</c> 还没 <c>Publish</c>。测试在这个事件里把它停住，一直停到业务服务
    /// 做完判断，所以判断时读到的必然是旧那一代（用例先断言了这一点）。车上要有旅程，这个事件才会发，所以假服务端握手后
    /// 下发旅程快照。</description></item>
    /// <item><description>业务服务：写下那条失败日志之后、接着动手之前，被 <c>RecordingLogger</c> 停住（写日志正是判断之后、
    /// 断开之前那一刻）。</description></item>
    /// <item><description>新会话：<see cref="NextSessionState.Handshaking"/> 停在「读完发件箱、还没发能力快照」，
    /// <see cref="NextSessionState.Ready"/> 走完握手。然后才放行业务服务。</description></item>
    /// </list>
    /// <para>
    /// 放行之后留 1 秒看它会不会去断开：修前它在放行后立刻同步调 <c>DisconnectAsync</c>，没有任何等待，1 秒只是给
    /// 「不该发生的事」一个发生的机会，红的那一侧不靠它。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(NextSessionState.Handshaking)]
    [InlineData(NextSessionState.Ready)]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    public async Task ASafetyReportWhoseConnectionClosedUnderItDoesNotDropTheNextSession(NextSessionState next)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        ReconnectRaceJournal race = null!;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
            },
            token,
            io: io,
            wrapJournal: inner => race = new ReconnectRaceJournal(inner));

        // Hold 1: the closing thread, parked where DisconnectAsync has woken the waiters and not yet published.
        int parkClosingThread = 0;
        bool judgedWhileParked = false;
        WireToGateSessionSnapshot? sessionAtJudgement = null;
        harness.Session.JourneyChanged += (_, args) =>
        {
            if (args.Value == WireToGateJourneySnapshot.Empty && Interlocked.Exchange(ref parkClosingThread, 0) == 1)
            {
                judgedWhileParked = harness.Logger.HoldEntered.Wait(TimeSpan.FromSeconds(10));
                sessionAtJudgement = harness.Session.Current;
            }
        };
        try
        {
            await WaitForSafetyReportsToSettleAsync(harness, token);
            await harness.WaitUntilAsync(
                () => harness.Session.CurrentJourney != WireToGateJourneySnapshot.Empty,
                "a journey on the vehicle, so closing the connection clears it",
                token);
            long firstGeneration = harness.Session.Current.SessionGeneration
                ?? throw new InvalidOperationException("The first session has no generation.");
            int firstConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);

            // A lock feedback stops reading; the report of it reaches the server, which takes it and never answers.
            harness.Server.AnswerSafetyStateChanged = false;
            int reportsBefore = SafetyChangesOn(harness, firstConnection).Length;
            io.SetUnreadable(0);
            io.PublishSnapshot();
            await harness.WaitUntilAsync(
                () => SafetyChangesOn(harness, firstConnection).Length > reportsBefore,
                "the report to reach the server and wait there for its acknowledgement",
                token);

            // Hold 2: the business service, right after it has judged the failure and logged it.
            harness.Logger.HoldNextEntryStartingWith("SafetyStateChanged");
            Volatile.Write(ref parkClosingThread, 1);
            await harness.Session.Client.DisconnectAsync();

            // The premise, or the rest proves nothing: the failure was judged while the closing thread was parked, and
            // what the session said at that moment was still the old generation, connected.
            Assert.True(judgedWhileParked, "The business service did not judge the failure while DisconnectAsync was parked.");
            Assert.NotNull(sessionAtJudgement);
            Assert.True(sessionAtJudgement.Connected);
            Assert.Equal(firstGeneration, sessionAtJudgement.SessionGeneration);

            // Hold 3: the next session, in its handshake or Ready, before the business service is let go.
            harness.Server.AnswerSafetyStateChanged = true;
            Task<WireToGateSessionSnapshot> reconnect;
            if (next == NextSessionState.Handshaking)
            {
                race.HoldNextHandshakeAfterOutboxRead();
                reconnect = harness.Session.Client.ConnectAndRecoverAsync(token);
                await race.HandshakeHeld.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
            else
            {
                reconnect = harness.Session.Client.ConnectAndRecoverAsync(token);
                await reconnect;
                Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
            }

            int secondConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
            long secondGeneration = harness.Session.Current.SessionGeneration
                ?? throw new InvalidOperationException("The new session has no generation.");
            Assert.NotEqual(firstConnection, secondConnection);

            harness.Logger.ReleaseHold();
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            // Still connected, still this generation, mid-handshake or Ready as it was.
            Assert.True(harness.Session.Current.Connected);
            Assert.Equal(secondGeneration, harness.Session.Current.SessionGeneration);

            race.ReleaseHandshake();
            await reconnect;
            Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
            Assert.Equal(secondGeneration, harness.Session.Current.SessionGeneration);
            Assert.Equal(secondConnection, harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection));
            // Because the failure was taken for what it is -- its connection was gone -- and not for a failure of the
            // session now in place.
            (string message, Exception failure) = Assert.Single(SafetyReportFailures(harness));
            Assert.IsType<WireToGateConnectionGoneException>(failure);
            Assert.Contains("不断开当前会话", message, StringComparison.Ordinal);
            Assert.Empty(harness.UiErrors);
        }
        finally
        {
            // Nothing may stay parked, or the harness's disposal waits forever.
            Volatile.Write(ref parkClosingThread, 0);
            harness.Logger.ReleaseHold();
            race.ReleaseHandshake();
        }
    }

    /// <summary>
    /// 反过来：连接还活着时的失败——服务端收下上报、连接一直开着、就是不回 <c>DurableAck</c>，等确认超时——业务服务仍然
    /// 断开会话，由重连后的新会话以同一版本和内容重试。
    /// </summary>
    /// <remarks>
    /// 守的是 <see cref="WireToGateConnectionGoneException"/> 的边界：它只标「连接已经不在了」，不能把活连接上该断的也吞掉。
    /// 这个夹具不跑会话服务的心跳循环，服务端也不关连接，所以会话若变成断开，只可能是业务服务自己断的。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    public async Task ASafetyReportUnansweredOnALiveConnectionStillDisconnectsTheSession()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await Harness.StartAsync(_ => { }, token, io: io);
        await WaitForSafetyReportsToSettleAsync(harness, token);
        int firstConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);

        harness.Server.AnswerSafetyStateChanged = false;
        int reportsBefore = SafetyChangesOn(harness, firstConnection).Length;
        io.SetUnreadable(0);
        io.PublishSnapshot();
        await harness.WaitUntilAsync(
            () => SafetyChangesOn(harness, firstConnection).Length > reportsBefore,
            "the report to reach the server and wait there for its acknowledgement",
            token);

        // The acknowledgement times out (MessageTimeout is 2 s here) on a connection that is still open.
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Disconnected,
            "the business service to disconnect the session after the unanswered report",
            token);
        (string message, Exception failure) = Assert.Single(SafetyReportFailures(harness));
        Assert.IsType<TimeoutException>(failure);
        Assert.StartsWith("SafetyStateChanged发送失败，正在断开会话", message, StringComparison.Ordinal);
        // The vehicle's own disconnect, not the server's: it never opened a second connection or lost the first one
        // any other way.
        Assert.Equal(firstConnection, harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection));
        Assert.Empty(harness.UiErrors);
    }

    private static JsonElement[] SafetyChangesOn(Harness harness, int connection) =>
    [
        .. harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.Connection == connection && envelope.MessageType == "SafetyStateChanged")
            .Select(envelope => SafetyOf(envelope.WireLine))
    ];

    private static void TriggerSafetyChange(Harness harness, FakeIoModuleClient io, SafetyReportTrigger trigger)
    {
        switch (trigger)
        {
            case SafetyReportTrigger.FatalFaultLatch:
                LatchNow(harness);
                break;
            case SafetyReportTrigger.IoSnapshot:
                // A lock feedback that stopped reading: the summary gains unknownPresent, so the report is a
                // change in content and not deduplicated away.
                io.SetUnreadable(0);
                io.PublishSnapshot();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown trigger.");
        }
    }

    /// <summary>What the business service logged, with its exception, for a SafetyStateChanged that did not go out.</summary>
    private static (string Message, Exception Exception)[] SafetyReportFailures(Harness harness) =>
    [
        .. harness.Logger.Exceptions
            .Where(entry => entry.Message.StartsWith("SafetyStateChanged", StringComparison.Ordinal))
    ];

    private static string[] MessageTypesOn(Harness harness, int connection) =>
    [
        .. harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.Connection == connection)
            .Select(envelope => envelope.MessageType)
    ];

    /// <summary>Each message on the connection whose generation is neither absent nor the one it belongs to.</summary>
    private static string[] ForeignGenerationsOn(Harness harness, int connection, long generation) =>
    [
        .. harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.Connection == connection)
            .Select(envelope => (envelope.MessageType, Generation: GenerationOf(envelope.WireLine)))
            .Where(item => item.Generation is not null && item.Generation != generation)
            .Select(item => $"{item.MessageType}@{item.Generation}")
    ];

    private static long SafetyStateVersionOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64();
    }

    private static long? GenerationOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.TryGetProperty("sessionGeneration", out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;
    }

    /// <summary>
    /// Holds one <c>SafetyStateChanged</c> just before its outbox row is written, and the next handshake right
    /// after it has read the outbox -- the point where its SessionHello is out and its CapabilitySnapshot is not.
    /// </summary>
    /// <remarks>
    /// Held before the inner write on purpose: the row never reaches the outbox, so the handshake's own replay
    /// cannot carry it and whatever arrives on the new connection arrived by the send path under test.
    /// </remarks>
    private sealed class ReconnectRaceJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private readonly TaskCompletionSource _safetyHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _safetyReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _handshakeHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _handshakeReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holdSafety;
        private int _holdHandshake;

        /// <summary>Completes once a safety report is parked in front of its outbox write.</summary>
        public Task SafetyChangeHeld => _safetyHeld.Task;

        /// <summary>Completes once a handshake is parked with its SessionHello out and nothing else.</summary>
        public Task HandshakeHeld => _handshakeHeld.Task;

        public void HoldNextSafetyStateChange() => Volatile.Write(ref _holdSafety, 1);

        public void HoldNextHandshakeAfterOutboxRead() => Volatile.Write(ref _holdHandshake, 1);

        public void ReleaseSafetyChange() => _safetyReleased.TrySetResult();

        public void ReleaseHandshake() => _handshakeReleased.TrySetResult();

        public async Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<WireToGateDurableMessage> read =
                await inner.ReadUnacknowledgedOutgoingAsync(cancellationToken);
            if (Interlocked.Exchange(ref _holdHandshake, 0) == 1)
            {
                _handshakeHeld.TrySetResult();
                await _handshakeReleased.Task;
            }

            return read;
        }

        public async Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default)
        {
            if (message.MessageType == "SafetyStateChanged" && Interlocked.Exchange(ref _holdSafety, 0) == 1)
            {
                _safetyHeld.TrySetResult();
                await _safetyReleased.Task;
            }

            return await inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);
        }

        public Task MarkOutgoingAcknowledgedAsync(
            string messageId,
            string acceptedContentSha256,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAcknowledgedAsync(messageId, acceptedContentSha256, cancellationToken);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, cancellationToken);

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
