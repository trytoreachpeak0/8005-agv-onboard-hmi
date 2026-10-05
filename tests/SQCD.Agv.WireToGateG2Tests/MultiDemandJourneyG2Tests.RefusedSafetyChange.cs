using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A <c>SafetyStateChanged</c> the control server refuses for good (onboard-hmi#254, paired with control-server#478).
/// </summary>
/// <remarks>
/// Since control-server#478 the server keeps the connection, distrusts the session's safety baseline and asks for a
/// fresh <c>SafetyStateSnapshot</c>, again at most every five seconds until a valid one arrives. The vehicle left such a
/// request unanswered for as long as it held an unacknowledged change -- and the refused one was exactly that, kept as
/// pending and resent by its key -- so the server never got its new baseline and never dispatched the vehicle again.
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// Refused mid-session: the change is given up together with the pending work, the session is not dropped, the
    /// present reading goes out as a change of its own, the server's snapshot request is answered at a higher version,
    /// and the session is READY again once the vehicle reads safe.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-SAME-REVISION-CONFLICT")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ARefusedSafetyChangeIsGivenUpAndTheServersSnapshotRequestIsAnswered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.RequireSafeSafetyForReadiness = true;
                server.SendReadinessAfterSafetyStateChangedAck = true;
            },
            token,
            io: io);
        await WaitForSafetyReportsToSettleAsync(harness, token);
        int connection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
        int changesBefore = RefusalSafetyChangesOn(harness, connection).Length;

        harness.Server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SafetyStateChanged"] = "SNAPSHOT_REVISION_CONTENT_CONFLICT"
        };
        io.SetUnreadable(0);
        io.PublishSnapshot();
        await harness.WaitUntilAsync(
            () => harness.Events.Any(item => item.Kind == "DURABLE_MESSAGE_ABANDONED"),
            "the refused safety change to be given up and reported",
            token);
        harness.Server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal);
        var refused = RefusalSafetyChangesOn(harness, connection)[changesBefore];
        long refusedVersion = SafetyStateVersionOf(refused.WireLine);

        // The present reading goes out as a change of its own, above the given-up version: never the refused one again.
        await harness.WaitUntilAsync(
            () => RefusalSafetyChangesOn(harness, connection).Any(change =>
                change.MessageId != refused.MessageId && SafetyStateVersionOf(change.WireLine) > refusedVersion),
            "the present reading to go out as a new change",
            token);
        Assert.Single(RefusalSafetyChangesOn(harness, connection), change => change.MessageId == refused.MessageId);

        // The door reads again and the vehicle is safe; then the server asks for its new baseline, as it does after a
        // refused safety message, and gets it at a higher version.
        io.CloseDoor(0, cargo: false);
        io.PublishSnapshot();
        await harness.Server.RequestSafetyStateSnapshotAsync();
        await harness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(envelope => envelope.Connection == connection
                && envelope.MessageType == "SafetyStateSnapshot"
                && SafetyStateVersionOf(envelope.WireLine) > refusedVersion),
            "the vehicle to answer the server's snapshot request",
            token);
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the session to be READY again",
            token);

        Assert.Equal(connection, harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection));
        Assert.DoesNotContain(
            harness.Logger.Entries,
            entry => entry.Message.StartsWith("SafetyStateChanged发送失败，正在断开会话", StringComparison.Ordinal));
    }

    /// <summary>
    /// Refused where the next handshake replays it: a change whose connection dropped before the server took it is still
    /// pending at the business service and on file; the handshake's replay is refused, given up, and the handshake goes
    /// on. The pending change is dropped rather than sent again under its key, the server's snapshot request is answered,
    /// and the session is READY once the vehicle reads safe (review of PR #258, N2).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-SAME-REVISION-CONFLICT")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ASafetyChangeRefusedInTheHandshakeReplayIsGivenUpAndTheSessionRecovers()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.RequireSafeSafetyForReadiness = true;
                server.SendReadinessAfterSafetyStateChangedAck = true;
            },
            token,
            io: io);
        await WaitForSafetyReportsToSettleAsync(harness, token);
        int firstConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
        int changesBefore = RefusalSafetyChangesOn(harness, firstConnection).Length;

        // The change reaches the server and the connection ends before it is taken: still owed, still pending.
        harness.Server.DropSafetyStateChangedBeforeAccepting = true;
        io.SetUnreadable(0);
        io.PublishSnapshot();
        await harness.WaitUntilAsync(
            () => RefusalSafetyChangesOn(harness, firstConnection).Length > changesBefore
                && !harness.Session.Current.Connected,
            "the change's connection to drop before the server took it",
            token);
        var dropped = RefusalSafetyChangesOn(harness, firstConnection)[changesBefore];
        long droppedVersion = SafetyStateVersionOf(dropped.WireLine);
        harness.Server.DropSafetyStateChangedBeforeAccepting = false;
        harness.Server.ProtocolProblemByMessageId = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [dropped.MessageId] = "SNAPSHOT_REVISION_CONTENT_CONFLICT"
        };

        Exception? handshake = await Record.ExceptionAsync(() => harness.Session.Client.ConnectAndRecoverAsync(token));
        Assert.True(handshake is null, $"the handshake failed: {handshake?.GetType().Name}: {handshake?.Message}");
        int secondConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
        Assert.Single(RefusalSafetyChangesOn(harness, secondConnection), change => change.MessageId == dropped.MessageId);
        await harness.WaitUntilAsync(
            () => harness.Events.Any(item => item.Kind == "DURABLE_MESSAGE_ABANDONED"),
            "the refused change to be given up and reported",
            token);

        // The door reads again; the server asks for its baseline and gets it above the given-up version.
        io.CloseDoor(0, cargo: false);
        io.PublishSnapshot();
        await harness.Server.RequestSafetyStateSnapshotAsync();
        await harness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(envelope => envelope.Connection == secondConnection
                && envelope.MessageType == "SafetyStateSnapshot"
                && SafetyStateVersionOf(envelope.WireLine) > droppedVersion),
            "the vehicle to answer the server's snapshot request",
            token);
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the session to be READY again",
            token);

        Assert.Single(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.MessageId == dropped.MessageId && envelope.Connection == secondConnection);
        Assert.Equal(secondConnection, harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection));
        Assert.DoesNotContain(
            harness.Logger.Entries,
            entry => entry.Message.Contains("有未确认的SafetyStateChanged等待重发", StringComparison.Ordinal));
    }

    private static (int Connection, string MessageType, string MessageId, string WireLine)[] RefusalSafetyChangesOn(
        Harness harness,
        int connection) =>
    [
        .. harness.Server.ReceivedEnvelopes.Where(envelope =>
            envelope.Connection == connection && envelope.MessageType == "SafetyStateChanged")
    ];
}
