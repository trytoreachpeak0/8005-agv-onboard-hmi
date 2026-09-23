using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 一份还在路上的安全上报，不能落进下一次握手（8005-agv-onboard-hmi#204）。
/// </summary>
/// <remarks>
/// <para>
/// <b>缺陷：</b><c>SendDurableCoreAsync</c> 先读会话快照判「现在可以发」，再 <c>await</c> 写发件箱，最后才
/// <c>SendLineAsync</c>；而 <c>SendLineAsync</c> 是在那之后才去读 <c>_writer</c> 字段的。写发件箱这一段里会话要是
/// 换了代，这条报文就写进了**新连接**——带着旧代次、插在 <c>SessionHello</c> 与 <c>CapabilitySnapshot</c> 中间。
/// 服务端看到的正是 cs#323 那次红运行里读到的顺序，车载端随后报 <c>HANDSHAKE_SEQUENCE_INVALID</c>。
/// </para>
/// <para>
/// <b>两个触发源都要挡住。</b><see cref="SafetyReportTrigger.FatalFaultLatch"/> 是 #197 新接的那根线
/// （<c>OnboardController.StateChanged</c> → <c>RefreshSafetyAfterFatalFaultLatchChange</c>），现场那次 IO 抖动正是
/// 经它多出一路并发上报；<see cref="SafetyReportTrigger.IoSnapshot"/> 是 #197 之前就有的那根线
/// （<c>IIoModuleClient.SnapshotChanged</c>）。根因在发送口，与是谁按下去的无关，所以两格跑同一段判据。
/// </para>
/// <para>
/// <b>窗口是构造出来的，不靠时序碰运气</b>（<see cref="ReconnectRaceJournal"/>）：先把这一份卡在写发件箱之前，
/// 再把重连卡在「读完发件箱、还没发能力快照」那一刻，然后放行前者。两头都卡住，缺陷在时才可能有报文挤进来。
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
