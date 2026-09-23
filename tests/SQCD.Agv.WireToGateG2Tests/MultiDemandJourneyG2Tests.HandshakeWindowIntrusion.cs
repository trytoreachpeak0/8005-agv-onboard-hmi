using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 上一代会话没送到的安全上报，不能进入下一次握手（8005-agv-onboard-hmi#204）。
/// </summary>
/// <remarks>
/// <para>
/// <b>缺陷一，cs#323 那次红运行里发生的：握手把它补发了。</b>握手一开始补发上一代没等到 <c>DurableAck</c> 的持久报文，
/// <c>SafetyStateChanged</c> 也在内。它若从没到过服务端，补发就是第一次受理，服务端按
/// <c>OnboardMessageProcessor</c> 的 <c>SafetyStateChanged</c> 分支回 <c>DurableAck</c> 并**无条件**跟一条
/// <c>SessionReadiness</c>；车载端补发只读走 ack，接着发 <c>CapabilitySnapshot</c>、等它的 ack，读到的却是那条就绪，
/// 报 <c>HANDSHAKE_SEQUENCE_INVALID：期望SnapshotAppliedAck，实际SessionReadiness</c>。那次运行的服务端库
/// <c>ProtocolInbox</c> 里，ver5 生成于第 1 代（sentAt 16:20:38.992），带着第 2 代号、排在第 2 代
/// <c>SessionHello</c> 之后 <c>CapabilitySnapshot</c> 之前，首次回复正是 ack + 就绪。补发它也是错的：握手里的全量
/// <c>SafetyStateSnapshot</c> 已经说了此刻的真相，那一份是更早的，晚到只会把服务端退回过时的状态。
/// 与 <c>RecoveryStateReport</c> 不补发是同一个理由，见 <see cref="AnUnsentSafetyChangeIsNotReplayedIntoTheNextHandshake"/>。
/// </para>
/// <para>
/// <b>缺陷二，查缺陷一时读代码发现的：在途的那一份写进了新连接。</b><c>SendDurableCoreAsync</c> 先判「现在可以发」，
/// 再 <c>await</c> 写发件箱，<c>SendLineAsync</c> 这之后才读 <c>_writer</c>。这一段里会话换了代，报文就带着旧代次写进
/// 新连接、插在 <c>SessionHello</c> 与 <c>CapabilitySnapshot</c> 中间；它随后的失败又被当成当前会话的，把正在握手的
/// 新会话断掉。见 <see cref="ASafetyReportStillInFlightStaysOutOfTheNextHandshake"/>。
/// </para>
/// <para>
/// <b>两个触发源。</b><see cref="SafetyReportTrigger.FatalFaultLatch"/> 是 #197 新接的那根线
/// （<c>OnboardController.StateChanged</c> → <c>RefreshSafetyAfterFatalFaultLatchChange</c>），
/// <see cref="SafetyReportTrigger.IoSnapshot"/> 是 #197 之前就有的那根线（<c>IIoModuleClient.SnapshotChanged</c>）。
/// 缺陷在发送口与握手，与是谁按下去的无关，所以两格跑同一段判据。
/// </para>
/// <para>
/// <b>窗口是构造出来的，不靠时序碰运气</b>（<see cref="ReconnectRaceJournal"/>）：把这一份卡在写发件箱之前或之后，
/// 需要时再把重连卡在「读完发件箱、还没发能力快照」那一刻。
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
            // taken as the old session's, so the handshake under way was left alone. The first half without the second is
            // what cs#323 saw next: the refusal itself disconnects the new session in the middle of its handshake.
            (string message, Exception refusal) = Assert.Single(SafetyReportFailures(harness));
            Assert.IsType<IOException>(refusal);
            Assert.Contains("换代", refusal.Message, StringComparison.Ordinal);
            Assert.Contains($"会话代{firstGeneration}", message, StringComparison.Ordinal);
            Assert.Contains("不断开当前会话", message, StringComparison.Ordinal);

            race.ReleaseHandshake();
            await reconnect;
            await harness.WaitUntilAsync(
                () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
                "the new session to become Ready",
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

    /// <summary>
    /// 一份写进了发件箱、却从没送到服务端的安全上报：下一次握手不补发它，只发自己的全量快照；握手照常走完、会话就绪；
    /// 服务端拿到的是此刻的读数而不是那一份；那一份之后也不会再发出去，也不妨碍下一次真实变化的上报。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 假服务端打开 <c>SendReadinessAfterSafetyStateChangedAck</c>，按真服务端对**第一次受理**的作答：ack 后跟一条就绪
    /// （control-server <c>OnboardMessageProcessor</c> 的 <c>SafetyStateChanged</c> 分支）。那一份从没发出去，补发它就是
    /// 第一次受理，cs#323 的 ver5 正是这样。
    /// </para>
    /// <para>
    /// 卡住期间读数恢复，那一份（读不到）就不再是真的。最后三段判据靠这一点分辨：服务端拿到的是全量快照还是晚到的过时那一份；
    /// 那一行是否已了结、不会被后面的握手再带出去；下一次又读不到时是照常上报，还是被那份过时内容留下的去重签名吞掉——
    /// 后者会让服务端一直以为这辆车可以出发。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    public async Task AnUnsentSafetyChangeIsNotReplayedIntoTheNextHandshake()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        ReconnectRaceJournal race = null!;
        await using Harness harness = await Harness.StartAsync(
            server => server.SendReadinessAfterSafetyStateChangedAck = true,
            token,
            io: io,
            wrapJournal: inner => race = new ReconnectRaceJournal(inner));
        try
        {
            await WaitForSafetyReportsToSettleAsync(harness, token);
            int firstConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);

            // A lock feedback stops reading; the report of it is on file and held before it reaches the wire.
            race.HoldNextSafetyStateChangeAfterOutboxWrite();
            io.SetUnreadable(0);
            io.PublishSnapshot();
            await race.SafetyChangeHeld.WaitAsync(TimeSpan.FromSeconds(10), token);
            // The reading comes back, unannounced, before the vehicle reconnects: what is on file is no longer true.
            io.CloseDoor(0, cargo: false);

            await harness.Session.Client.DisconnectAsync();
            await harness.Session.Client.ConnectAndRecoverAsync(token);
            int secondConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
            Assert.NotEqual(firstConnection, secondConnection);

            // The handshake is its own five messages in order: nothing replayed ahead of the capability snapshot.
            Assert.Equal(
                ["SessionHello", "CapabilitySnapshot", "SafetyStateSnapshot", "OnboardAlarmSnapshot", "RecoveryStateReport"],
                MessageTypesOn(harness, secondConnection).Take(5));
            // Its full snapshot says what is true now.
            JsonElement handshakeSafety = SafetyOf(Assert.Single(
                harness.Server.ReceivedEnvelopes,
                envelope => envelope.Connection == secondConnection && envelope.MessageType == "SafetyStateSnapshot").WireLine);
            Assert.False(handshakeSafety.GetProperty("unknownPresent").GetBoolean());

            // The held report goes nowhere, and takes nothing with it. The next new report -- what the business service
            // sends once the new session is ready -- is parked before the wire, so the moment in between can be read.
            race.HoldFollowingSafetyStateChange();
            race.ReleaseSafetyChange();
            await race.FollowingSafetyChangeHeld.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.NotEmpty(SafetyReportFailures(harness));
            Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
            // What the vehicle takes the server to have accepted is what the server holds for this session: the
            // handshake snapshot's version, and nothing past it. The stale report never reached the server; had the
            // business service "resent" it, the settled outbox row would have made that a silent success that still
            // advances the accepted version (SendSafetyStateChangedAsync), leaving the vehicle one ahead of the server.
            long serverHolds = SafetyStateVersionOf(Assert.Single(
                harness.Server.ReceivedEnvelopes,
                envelope => envelope.Connection == secondConnection && envelope.MessageType == "SafetyStateSnapshot").WireLine);
            Assert.Equal(serverHolds, harness.Session.Current.SafetyStateVersion);
            // And what being one ahead costs: the server announces its readiness now, as it does on many events, with
            // its own number; ApplySessionReadiness refuses a number below its own as HANDSHAKE_SEQUENCE_INVALID and the
            // ready session is dropped.
            await harness.Server.SendSessionReadinessAsync();
            race.ReleaseFollowingSafetyChange();
            // Room for a late send of the stale report, or a late disconnect, to show itself.
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
            Assert.Equal(secondConnection, harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection));
            // Nothing stale reached the server afterwards: every safety change on this connection is the live reading.
            Assert.DoesNotContain(
                SafetyChangesOn(harness, secondConnection),
                safety => safety.GetProperty("unknownPresent").GetBoolean());
            // The superseded row is settled, so no later handshake carries it either.
            Assert.DoesNotContain(
                await race.ReadUnacknowledgedOutgoingAsync(token),
                message => message.MessageType == "SafetyStateChanged");

            // The next real change is still reported: the stale report's content must not be left as the deduplication
            // signature, or this one -- the same content -- would be swallowed and the server keep calling the car safe.
            // Today that also holds without dropping the stale report, only because every successful send Publishes
            // the session and OnSessionStateChanged judges the safety again at once; this step keeps holding it either way.
            io.SetUnreadable(0);
            io.PublishSnapshot();
            await harness.WaitUntilAsync(
                () => SafetyChangesOn(harness, secondConnection)
                    .Any(safety => safety.GetProperty("unknownPresent").GetBoolean()),
                "the reading that stopped again to be reported",
                token);
            Assert.Empty(harness.UiErrors);
        }
        finally
        {
            // Whatever failed above, nothing may stay parked: the harness's disposal waits for the business
            // service's tasks, and a report still held here would keep this test from ever finishing.
            race.ReleaseSafetyChange();
            race.ReleaseFollowingSafetyChange();
            race.ReleaseHandshake();
        }
    }

    private static long SafetyStateVersionOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64();
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

    private static long? GenerationOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.TryGetProperty("sessionGeneration", out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;
    }

    /// <summary>
    /// Holds one <c>SafetyStateChanged</c> on either side of its outbox write, and the next handshake right after it
    /// has read the outbox -- the point where its SessionHello is out and its CapabilitySnapshot is not.
    /// </summary>
    /// <remarks>
    /// Before the write (<see cref="HoldNextSafetyStateChange"/>): the row never reaches the outbox, so the handshake
    /// cannot replay it and whatever arrives on the new connection arrived by the send path. After the write
    /// (<see cref="HoldNextSafetyStateChangeAfterOutboxWrite"/>): the row is on file and was never sent, which is where
    /// cs#323's ver5 was when its first session stalled, and only the handshake's replay can carry it.
    /// </remarks>
    private sealed class ReconnectRaceJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private readonly TaskCompletionSource _safetyHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _safetyReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _handshakeHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _handshakeReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _followingHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _followingReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holdSafety;
        private int _holdSafetyAfterWrite;
        private int _holdFollowing;
        private int _holdHandshake;

        /// <summary>Completes once a safety report is parked in front of its outbox write.</summary>
        public Task SafetyChangeHeld => _safetyHeld.Task;

        /// <summary>Completes once a handshake is parked with its SessionHello out and nothing else.</summary>
        public Task HandshakeHeld => _handshakeHeld.Task;

        public void HoldNextSafetyStateChange() => Volatile.Write(ref _holdSafety, 1);

        public void HoldNextSafetyStateChangeAfterOutboxWrite() => Volatile.Write(ref _holdSafetyAfterWrite, 1);

        /// <summary>
        /// A second, independent hold for a later report: the next <c>SafetyStateChanged</c> written as a new outbox row
        /// is parked before its write. A settled row "resent" under the same key goes through
        /// <see cref="ReplaceOutgoingForReplayAsync"/> instead and is not caught here.
        /// </summary>
        public void HoldFollowingSafetyStateChange() => Volatile.Write(ref _holdFollowing, 1);

        public Task FollowingSafetyChangeHeld => _followingHeld.Task;

        public void ReleaseFollowingSafetyChange() => _followingReleased.TrySetResult();

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
            else if (message.MessageType == "SafetyStateChanged" && Interlocked.Exchange(ref _holdFollowing, 0) == 1)
            {
                _followingHeld.TrySetResult();
                await _followingReleased.Task;
            }

            WireToGateDurableMessage saved = await inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);
            if (message.MessageType == "SafetyStateChanged" && Interlocked.Exchange(ref _holdSafetyAfterWrite, 0) == 1)
            {
                _safetyHeld.TrySetResult();
                await _safetyReleased.Task;
            }

            return saved;
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
