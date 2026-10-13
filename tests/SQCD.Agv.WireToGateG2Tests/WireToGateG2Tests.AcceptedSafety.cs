using System.Net;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// <see cref="WireToGateSessionClient.AcceptedSafety"/>, the safety state the server accepted as the screen-state
/// snapshot reports it (onboard-hmi#314, research section 4.1): on each of the three ways a summary goes out it is the
/// one the server acknowledged, and the three situations that made the research's plain "only up" rule wrong come out
/// right.
/// </summary>
/// <remarks>
/// <para>
/// Session client only, no business service: the business service numbers its own changes and ends the session when
/// one fails, which would hide the late acknowledgement and decide the versions these tests need to choose.
/// </para>
/// <para>
/// No <c>ProtocolVector</c> trait: this is observation, no vector of the protocol is proved here, so no slice either
/// (<c>IntegrationSliceTraitArchitectureTests</c>).
/// </para>
/// </remarks>
public sealed partial class WireToGateG2Tests
{
    private static readonly WireToGateSafetySummaryPayload SafeSummary = new(true, true, true, true, false, []);

    private static readonly WireToGateSafetySummaryPayload LockOpenSummary =
        new(false, true, false, true, false, ["LOCK_NOT_CLOSED"]);

    /// <summary>
    /// The handshake snapshot, at the configured version because the handshake resent nothing: the configured version
    /// starts with no summary, and the acknowledged snapshot gives it one at that same number (ruling B, the first
    /// same-version exception).
    /// </summary>
    [Fact]
    public async Task AcceptedSafetyAfterTheHandshakeIsTheSnapshotTheServerAcknowledged()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        await using WireToGateSessionClient client = CreateClient(server, new FakeIoModuleClient(), NewJournalPath(), safety: 1);
        Assert.Equal(new WireToGateAcceptedSafety(1, null, null, WireToGateAcceptedSafetySource.Configured), client.AcceptedSafety);

        await client.ConnectAndRecoverAsync(testToken);

        string snapshot = server.ReceivedEnvelopes.Single(item => item.MessageType == "SafetyStateSnapshot").WireLine;
        Assert.Equal(1, RevisionOf(snapshot));
        AssertAccepted(client.AcceptedSafety, snapshot, WireToGateAcceptedSafetySource.HandshakeSnapshot);
    }

    /// <summary>
    /// A snapshot the server asked for mid-session. The server follows its acknowledgement with a readiness, and the
    /// receive loop reads that before the send has registered the snapshot: either may register first, and either way
    /// it is the snapshot's own version and summary. Which one did goes to the test output.
    /// </summary>
    [Fact]
    public async Task AcceptedSafetyAfterARequestedSnapshotIsTheSnapshotTheServerAcknowledged()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);
        io.LeaveDoorUnlocked(2);
        long requested = client.Current.SafetyStateVersion + 1;

        await server.RequestSafetyStateSnapshotAsync();
        Assert.True(await client.PublishSafetyStateSnapshotAsync(requested, testToken));

        string snapshot = server.ReceivedEnvelopes.Last(item => item.MessageType == "SafetyStateSnapshot").WireLine;
        Assert.Equal(requested, RevisionOf(snapshot));
        Assert.False(SafetyOf(snapshot).DepartureSafe);
        WireToGateAcceptedSafety accepted = client.AcceptedSafety;
        AssertAccepted(
            accepted,
            snapshot,
            WireToGateAcceptedSafetySource.RequestedSnapshot,
            WireToGateAcceptedSafetySource.SessionReadiness);
        TestContext.Current.TestOutputHelper?.WriteLine($"registered first by {accepted.Source}");
    }

    /// <summary>A <c>SafetyStateChanged</c>, the way a safety change goes out most of the time.</summary>
    [Fact]
    public async Task AcceptedSafetyAfterASafetyStateChangedIsTheChangeTheServerAcknowledged()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        await using WireToGateSessionClient client = CreateClient(server, new FakeIoModuleClient(), NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);
        long changed = client.Current.SafetyStateVersion + 1;

        await client.SendSafetyStateChangedAsync(changed, DateTimeOffset.UtcNow, LockOpenSummary, [1], testToken);

        string change = server.ReceivedEnvelopes.Single(item => item.MessageType == "SafetyStateChanged").WireLine;
        AssertAccepted(client.AcceptedSafety, change, WireToGateAcceptedSafetySource.SafetyStateChanged);
        Assert.True(SameSafety(LockOpenSummary, client.AcceptedSafety.Safety!));
    }

    /// <summary>
    /// A send of a change already acknowledged returns the row on file without sending it, and what is registered is what
    /// the server took. The client registers the call's arguments rather than reading the row back (so the acknowledgement
    /// is published as soon as before onboard-hmi#314); that stands for the row only because the two cannot differ, and
    /// this pins it: a different summary under the same key is refused before anything is sent.
    /// </summary>
    [Fact]
    public async Task AResendOfAnAcknowledgedChangeRegistersWhatTheServerTookAndADifferentSummaryUnderItsKeyIsRefused()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        await using WireToGateSessionClient client = CreateClient(server, new FakeIoModuleClient(), NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);
        long changed = client.Current.SafetyStateVersion + 1;
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        string first = await client.SendSafetyStateChangedAsync(changed, observedAt, LockOpenSummary, [1], testToken);

        string again = await client.SendSafetyStateChangedAsync(changed, observedAt, LockOpenSummary, [1], testToken);

        Assert.Equal(first, again);
        string change = server.ReceivedEnvelopes.Single(item => item.MessageType == "SafetyStateChanged").WireLine;
        AssertAccepted(client.AcceptedSafety, change, WireToGateAcceptedSafetySource.SafetyStateChanged);

        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.SendSafetyStateChangedAsync(changed, observedAt, SafeSummary, [1], testToken));
        Assert.Equal("BUSINESS_ID_CONTENT_CONFLICT", refused.Message);
        Assert.Single(server.ReceivedEnvelopes, item => item.MessageType == "SafetyStateChanged");
        Assert.True(SameSafety(LockOpenSummary, client.AcceptedSafety.Safety!));
    }

    /// <summary>
    /// A <c>SessionReadiness</c> carries only a version. One this client never built a summary for is registered with
    /// the summary unknown, never with another version's.
    /// </summary>
    [Fact]
    public async Task AReadinessReportingAVersionNeverBuiltHereLeavesTheSummaryUnknown()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        await using WireToGateSessionClient client = CreateClient(server, new FakeIoModuleClient(), NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);
        long ahead = client.Current.SafetyStateVersion + 5;
        server.ReadinessAcceptedSafetyStateVersionOverride = ahead;

        await server.SendSessionReadinessAsync();
        await WaitUntilAsync(() => client.AcceptedSafety.SafetyStateVersion == ahead, testToken);

        WireToGateAcceptedSafety accepted = client.AcceptedSafety;
        Assert.Null(accepted.Safety);
        Assert.Equal(WireToGateAcceptedSafetySource.SessionReadiness, accepted.Source);
        Assert.Equal(client.Current.SessionGeneration, accepted.SessionGeneration);
    }

    /// <summary>
    /// The readiness that follows a change's acknowledgement is read before the send has registered the change: the
    /// readiness finds the summary this client built for the version, and the send that registers after it changes
    /// nothing. The order is forced here by holding the acknowledgement;
    /// <see cref="TheNaturalOrderOfAcknowledgementAndReadinessAlwaysEndsWithTheChangesSummary"/> shows it happening
    /// without that.
    /// </summary>
    [Fact]
    public async Task AReadinessThatOvertakesTheChangesOwnRegistrationFindsTheChangesSummary()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        await using WireToGateSessionClient client = CreateClient(server, new FakeIoModuleClient(), NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);
        long changed = client.Current.SafetyStateVersion + 1;
        FakeControlServer.DurableAckHold hold = server.HoldNextDurableAck("SafetyStateChanged");
        Task<string> send = client.SendSafetyStateChangedAsync(changed, DateTimeOffset.UtcNow, LockOpenSummary, [1], testToken);
        await hold.Held.WaitAsync(testToken);

        server.ReadinessAcceptedSafetyStateVersionOverride = changed;
        await server.SendSessionReadinessAsync();
        await WaitUntilAsync(() => client.AcceptedSafety.SafetyStateVersion == changed, testToken);

        WireToGateAcceptedSafety byReadiness = client.AcceptedSafety;
        Assert.Equal(WireToGateAcceptedSafetySource.SessionReadiness, byReadiness.Source);
        Assert.True(SameSafety(LockOpenSummary, byReadiness.Safety!));

        await hold.ReleaseAsync();
        await send.WaitAsync(testToken);
        Assert.Same(byReadiness, client.AcceptedSafety);
    }

    /// <summary>
    /// The real server's order -- the acknowledgement, then at once the readiness it decided -- many times over: whichever
    /// of the two the client registers first, it ends with the change's own version and summary. The count of times the
    /// readiness came first is written to the test output: the race the research inferred, measured.
    /// </summary>
    [Fact]
    public async Task TheNaturalOrderOfAcknowledgementAndReadinessAlwaysEndsWithTheChangesSummary()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendReadinessAfterSafetyStateChangedAck = true
        };
        await using WireToGateSessionClient client = CreateClient(server, new FakeIoModuleClient(), NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);
        const int Rounds = 40;
        int readinessFirst = 0;
        for (int round = 0; round < Rounds; round++)
        {
            long changed = client.Current.SafetyStateVersion + 1;
            // Codes from the protocol's closed set only: the outbound schema check runs over every line sent.
            WireToGateSafetySummaryPayload summary = (round % 3) switch
            {
                0 => LockOpenSummary,
                1 => new(false, true, true, false, false, ["UNLOCK_OUTPUT_NOT_RESET"]),
                _ => SafeSummary,
            };
            await client.SendSafetyStateChangedAsync(changed, DateTimeOffset.UtcNow, summary, [1], testToken);
            await WaitUntilAsync(() => client.Current.SafetyStateVersion == changed, testToken);

            WireToGateAcceptedSafety accepted = client.AcceptedSafety;
            Assert.Equal(changed, accepted.SafetyStateVersion);
            Assert.True(SameSafety(summary, accepted.Safety!), $"round {round}: {accepted}");
            readinessFirst += accepted.Source == WireToGateAcceptedSafetySource.SessionReadiness ? 1 : 0;
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"readiness registered first in {readinessFirst} of {Rounds} rounds");
    }

    /// <summary>
    /// A new generation's handshake resends nothing, so it reuses the accepted number for a summary read from the IO just
    /// then; the server holds that summary for the new generation. The accepted state follows it (ruling B, the second
    /// same-version exception), where "only up" would keep the old generation's.
    /// </summary>
    [Fact]
    public async Task ANewGenerationsHandshakeAtTheSameVersionReplacesTheOldGenerationsSummary()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);
        string firstSnapshot = server.ReceivedEnvelopes.Single(item => item.MessageType == "SafetyStateSnapshot").WireLine;
        AssertAccepted(client.AcceptedSafety, firstSnapshot, WireToGateAcceptedSafetySource.HandshakeSnapshot);

        server.CloseLatestConnection();
        await WaitUntilAsync(() => !client.Current.Connected, testToken);
        io.LeaveDoorUnlocked(3);
        await client.ConnectAndRecoverAsync(testToken);

        string secondSnapshot = server.ReceivedEnvelopes
            .Single(item => item.Connection == 2 && item.MessageType == "SafetyStateSnapshot").WireLine;
        // The premise, so that this test cannot pass by the version simply going up.
        Assert.Equal(RevisionOf(firstSnapshot), RevisionOf(secondSnapshot));
        Assert.True(GenerationOf(secondSnapshot) > GenerationOf(firstSnapshot));
        Assert.False(SameSafety(SafetyOf(firstSnapshot), SafetyOf(secondSnapshot)));
        AssertAccepted(client.AcceptedSafety, secondSnapshot, WireToGateAcceptedSafetySource.HandshakeSnapshot);
    }

    /// <summary>
    /// A late acknowledgement registers its change when nothing newer has been accepted, and changes nothing when
    /// something has: concurrent acknowledgements never take the version down or pair it with another's summary.
    /// </summary>
    [Fact]
    public async Task ALateAcknowledgementRegistersItsChangeOnlyWhileNothingNewerIsAccepted()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        await using WireToGateSessionClient client = CreateClient(
            server,
            new FakeIoModuleClient(),
            NewJournalPath(),
            messageTimeout: TimeSpan.FromSeconds(1));
        await client.ConnectAndRecoverAsync(testToken);
        long older = client.Current.SafetyStateVersion + 1;
        long newer = older + 1;
        long newest = newer + 1;

        // Older's acknowledgement held past its send; newer acknowledged in time; then older's arrives.
        FakeControlServer.DurableAckHold olderHold = HoldSafetyAck(server, older);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.SendSafetyStateChangedAsync(older, DateTimeOffset.UtcNow, SafeSummary, [1], testToken));
        await olderHold.Held.WaitAsync(testToken);
        await client.SendSafetyStateChangedAsync(newer, DateTimeOffset.UtcNow, LockOpenSummary, [1], testToken);
        WireToGateAcceptedSafety afterNewer = client.AcceptedSafety;
        Assert.Equal(newer, afterNewer.SafetyStateVersion);

        await ReleaseAndAwaitLateAckAsync(client, olderHold, testToken);
        Assert.Same(afterNewer, client.AcceptedSafety);

        // Newest's acknowledgement held past its send, and nothing after it: the late acknowledgement registers it.
        FakeControlServer.DurableAckHold newestHold = HoldSafetyAck(server, newest);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.SendSafetyStateChangedAsync(newest, DateTimeOffset.UtcNow, SafeSummary, [1], testToken));
        await newestHold.Held.WaitAsync(testToken);
        Assert.Same(afterNewer, client.AcceptedSafety);

        await ReleaseAndAwaitLateAckAsync(client, newestHold, testToken);
        WireToGateAcceptedSafety late = client.AcceptedSafety;
        Assert.Equal(newest, late.SafetyStateVersion);
        Assert.Equal(WireToGateAcceptedSafetySource.LateSafetyStateChangedAck, late.Source);
        Assert.True(SameSafety(SafeSummary, late.Safety!));
        Assert.Empty(server.DurableAckHoldPredicateFailures);
    }

    private static FakeControlServer.DurableAckHold HoldSafetyAck(FakeControlServer server, long version) =>
        server.HoldNextDurableAck(
            "SafetyStateChanged",
            payload => payload.GetProperty("safetyStateVersion").GetInt64() == version);

    private static async Task ReleaseAndAwaitLateAckAsync(
        WireToGateSessionClient client,
        FakeControlServer.DurableAckHold hold,
        CancellationToken cancellationToken)
    {
        string messageId = await hold.Held.WaitAsync(cancellationToken);
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLateAck(object? sender, ValueChangedEventArgs<WireToGateLateDurableAck> args)
        {
            if (args.Value.MessageId == messageId)
            {
                arrived.TrySetResult();
            }
        }

        client.LateDurableAckReceived += OnLateAck;
        try
        {
            await hold.ReleaseAsync();
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        finally
        {
            client.LateDurableAckReceived -= OnLateAck;
        }
    }

    /// <summary>The accepted state is the version, generation and summary of <paramref name="wireLine"/>.</summary>
    private static void AssertAccepted(
        WireToGateAcceptedSafety accepted,
        string wireLine,
        params WireToGateAcceptedSafetySource[] sources)
    {
        Assert.Equal(RevisionOf(wireLine), accepted.SafetyStateVersion);
        Assert.Equal(GenerationOf(wireLine), accepted.SessionGeneration);
        Assert.Contains(accepted.Source, sources);
        Assert.NotNull(accepted.Safety);
        Assert.True(SameSafety(SafetyOf(wireLine), accepted.Safety), $"accepted {accepted.Safety}, the line says {wireLine}");
    }
}
