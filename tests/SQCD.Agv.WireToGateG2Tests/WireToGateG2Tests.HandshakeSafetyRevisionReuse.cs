using System.Collections.Concurrent;
using System.Net;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A reconnect handshake's <c>SafetyStateSnapshot</c> never keeps a <c>safetyStateVersion</c> already spent on other
/// content (8005-agv-onboard-hmi#394).
/// </summary>
/// <remarks>
/// <para>
/// The field's shape (10-21 13:52, agv03): generation 114 ended with <c>SafetyStateChanged</c> v94, every door locked. The
/// link roamed, and the handshake of generation 116 sent its snapshot at v94 again with content worked out there and then
/// -- this time with <c>SLOT_STATE_UNKNOWN</c>, the shape #373 had fixed in a build agv03 was not yet running (its log
/// starts <c>+a3fdb194</c>). The server stopped the moving vehicle on it. One number then stood for two states: the
/// vehicle's outbox said v94 was all locked, the server's log said v94 was unknown, and the analysis started from the
/// wrong one.
/// </para>
/// <para>
/// Keeping the accepted version was deliberate: the real server clears its safety revision at every generation, so the
/// number meant nothing there yet (#206). These tests hold the vehicle to the stricter rule the protocol states -- a
/// version, once on the wire, names one content -- and check it over everything the double received
/// (<see cref="SafetyRevisionContent"/>). They run with the vehicle's own freshness limit and a capability ack held back
/// as long as the field's handshake took (3.5 s), the way <c>HandshakeIoFreshness</c> does.
/// </para>
/// <para>
/// No <c>IntegrationSlice</c> or <c>ProtocolVector</c> traits, for the reason <c>HandshakeIoFreshness</c> gives.
/// </para>
/// </remarks>
public sealed partial class WireToGateG2Tests
{
    // As long as generation 116's handshake took from SessionAccepted to SessionReadiness; below the 5 s message timeout of
    // HandshakeFreshnessClient.
    private static readonly TimeSpan FieldHandshakeAckDelay = TimeSpan.FromMilliseconds(3500);

    /// <summary>
    /// A change accepted in one generation, a roam, a slow handshake in the next: the handshake snapshot goes out under a
    /// version above the change's, reports nothing unknown over IO that reads normally, and the session is ready at it.
    /// </summary>
    [Fact]
    public async Task AReconnectHandshakeAfterAnAcceptedSafetyChangeTakesAVersionOfItsOwn()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            ShareSafetyRevisionAcrossChangeAndSnapshot = true,
            BeforeSnapshotAck = (messageType, connection) => messageType == "CapabilitySnapshot" && connection == 2
                ? Task.Delay(FieldHandshakeAckDelay, testToken)
                : Task.CompletedTask
        };
        ConcurrentQueue<string> diagnostics = new();
        await using WireToGateSessionClient client = HandshakeFreshnessClient(server, io, diagnostics);
        await client.ConnectAndRecoverAsync(testToken);

        // What generation 114 ended on: every door locked, not ready to leave for a reason of the vehicle's own.
        long changeVersion = client.Current.SafetyStateVersion + 1;
        await client.SendSafetyStateChangedAsync(
            changeVersion,
            DateTimeOffset.UtcNow,
            new WireToGateSafetySummaryPayload(false, true, true, true, false, ["ACTION_NOT_ALLOWED_IN_STATE"]),
            [1, 2, 3, 4, 5, 6, 7, 8],
            testToken);
        Assert.Equal(changeVersion, client.Current.SafetyStateVersion);

        server.CloseLatestConnection();
        await client.ConnectAndRecoverAsync(testToken);

        string snapshotLine = server.ReceivedEnvelopes
            .Single(item => item.Connection == 2 && item.MessageType == "SafetyStateSnapshot")
            .WireLine;
        long snapshotVersion = RevisionOf(snapshotLine);
        SafetyRevisionContent.AssertNone(server.ReceivedEnvelopes);
        Assert.True(
            snapshotVersion > changeVersion,
            $"the reconnect handshake snapshot went out at v{snapshotVersion}, not above the change at v{changeVersion}");
        WireToGateSafetySummaryPayload safety = SafetyOf(snapshotLine);
        Assert.True(
            !safety.ReasonCodes.Contains("SLOT_STATE_UNKNOWN") && !safety.UnknownPresent,
            $"the reconnect handshake snapshot reported [{string.Join(",", safety.ReasonCodes)}]");
        Assert.Empty(server.SafetyRevisionConflicts);
        Assert.Equal(WireToGateSessionReadiness.Ready, client.Current.Readiness);
        Assert.Equal(snapshotVersion, client.Current.SafetyStateVersion);
    }

    /// <summary>
    /// A handshake snapshot that went out but whose ack never came -- the link roamed in the middle of it -- still spent
    /// its version: the next handshake goes above it, although the server never accepted it.
    /// </summary>
    /// <remarks>
    /// The case that tells "the version the vehicle wrote" from "the version the server accepted": the accepted version
    /// stays where it was, so a handshake numbered from it alone would put a second content under the lost snapshot's
    /// version.
    /// </remarks>
    [Fact]
    public async Task AHandshakeSnapshotWhoseAckWasLostStillSpentItsVersion()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        TaskCompletionSource releaseLostAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            ShareSafetyRevisionAcrossChangeAndSnapshot = true,
            BeforeSnapshotAck = (messageType, connection) => messageType == "SafetyStateSnapshot" && connection == 2
                ? releaseLostAck.Task
                : Task.CompletedTask
        };
        ConcurrentQueue<string> diagnostics = new();
        await using WireToGateSessionClient client = HandshakeFreshnessClient(server, io, diagnostics);
        await client.ConnectAndRecoverAsync(testToken);
        long acceptedBefore = client.Current.SafetyStateVersion;

        server.CloseLatestConnection();
        Exception? lost = await Record.ExceptionAsync(() => client.ConnectAndRecoverAsync(testToken));
        Assert.True(lost is TimeoutException, $"the second handshake ended with {lost?.GetType().Name}: {lost?.Message}");
        Assert.Equal(acceptedBefore, client.Current.SafetyStateVersion);
        releaseLostAck.SetResult();
        // Something to tell the two snapshots apart by, as the IO of a moving vehicle would.
        io.SetCargoPresent(4, true);

        await client.ConnectAndRecoverAsync(testToken);

        long lostVersion = RevisionOf(server.ReceivedEnvelopes
            .Single(item => item.Connection == 2 && item.MessageType == "SafetyStateSnapshot")
            .WireLine);
        long thirdVersion = RevisionOf(server.ReceivedEnvelopes
            .Single(item => item.Connection == 3 && item.MessageType == "SafetyStateSnapshot")
            .WireLine);
        SafetyRevisionContent.AssertNone(server.ReceivedEnvelopes);
        Assert.True(lostVersion > acceptedBefore, $"the lost snapshot kept the accepted v{acceptedBefore}");
        Assert.True(thirdVersion > lostVersion, $"the third handshake went out at v{thirdVersion}, not above the lost v{lostVersion}");
        Assert.Equal(WireToGateSessionReadiness.Ready, client.Current.Readiness);
        Assert.Equal(thirdVersion, client.Current.SafetyStateVersion);
    }

    /// <summary>
    /// The IO's readings have not come in when the link comes back -- the module's last reading 3.5 s old -- and then they
    /// do: the handshake snapshot says what is true, <c>SLOT_STATE_UNKNOWN</c>, but under a version of its own; the
    /// reading that follows once the IO reads again says nothing unknown, under a higher version still.
    /// </summary>
    /// <remarks>
    /// Waiting for a fresh reading in the handshake was the other way the ticket allowed, and is not taken: the IO module
    /// sits on the vehicle and does not drop with the server link (agv03 logged no IO interruption all 10-21, agv01 none
    /// on 10-19), so a reading that is stale at the handshake is a real fault, and the protocol's answer to a real fault
    /// is to report it (#373 kept that true report). What made both field stops was a reading the vehicle itself had held
    /// across a round trip (#373, fixed by #374) and a version that already named other content (this ticket).
    /// </remarks>
    [Fact]
    public async Task IoReadingsLateAtTheReconnectAreReportedUnknownUnderANewVersionThenNormallyOnceTheyCome()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            ShareSafetyRevisionAcrossChangeAndSnapshot = true
        };
        ConcurrentQueue<string> diagnostics = new();
        await using WireToGateSessionClient client = HandshakeFreshnessClient(server, io, diagnostics);
        await client.ConnectAndRecoverAsync(testToken);
        long firstVersion = client.Current.SafetyStateVersion;

        io.KeepSnapshotFresh = false;
        io.MakeStale(FieldHandshakeAckDelay);
        server.CloseLatestConnection();
        await client.ConnectAndRecoverAsync(testToken);

        string lateLine = server.ReceivedEnvelopes
            .Single(item => item.Connection == 2 && item.MessageType == "SafetyStateSnapshot")
            .WireLine;
        WireToGateSafetySummaryPayload late = SafetyOf(lateLine);
        Assert.Contains("SLOT_STATE_UNKNOWN", late.ReasonCodes);
        Assert.True(late.UnknownPresent);
        Assert.True(
            RevisionOf(lateLine) > firstVersion,
            $"the unknown went out at v{RevisionOf(lateLine)}, the version that had said v{firstVersion}'s content");

        // The readings come in; the vehicle reports them as the business service would, at the next version.
        io.KeepSnapshotFresh = true;
        long recoveredVersion = client.Current.SafetyStateVersion + 1;
        Assert.True(await client.PublishSafetyStateSnapshotAsync(recoveredVersion, testToken));
        string recoveredLine = server.ReceivedEnvelopes
            .Last(item => item.MessageType == "SafetyStateSnapshot")
            .WireLine;
        WireToGateSafetySummaryPayload recovered = SafetyOf(recoveredLine);
        Assert.True(
            !recovered.ReasonCodes.Contains("SLOT_STATE_UNKNOWN") && !recovered.UnknownPresent,
            $"the reading after the IO came back reported [{string.Join(",", recovered.ReasonCodes)}]");

        // And the roam after that, with the IO reading normally, reports nothing unknown either.
        server.CloseLatestConnection();
        await client.ConnectAndRecoverAsync(testToken);
        string thirdLine = server.ReceivedEnvelopes
            .Single(item => item.Connection == 3 && item.MessageType == "SafetyStateSnapshot")
            .WireLine;
        Assert.DoesNotContain("SLOT_STATE_UNKNOWN", SafetyOf(thirdLine).ReasonCodes);
        Assert.True(RevisionOf(thirdLine) > recoveredVersion);
        SafetyRevisionContent.AssertNone(server.ReceivedEnvelopes);
        Assert.Empty(server.SafetyRevisionConflicts);
    }

    /// <summary>
    /// The roam is the normal case: eight reconnects in a row, every other one with the capability ack held back as long as
    /// the field's handshake, the IO changing and a change reported between some of them. Every handshake snapshot goes
    /// out above everything before it, none says unknown, and no version ever carries two contents.
    /// </summary>
    [Fact]
    public async Task RepeatedRoamingReconnectsNeverPutTwoContentsUnderOneVersion()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            ShareSafetyRevisionAcrossChangeAndSnapshot = true,
            BeforeSnapshotAck = (messageType, connection) => messageType == "CapabilitySnapshot" && connection % 2 == 0
                ? Task.Delay(FieldHandshakeAckDelay, testToken)
                : Task.CompletedTask
        };
        ConcurrentQueue<string> diagnostics = new();
        await using WireToGateSessionClient client = HandshakeFreshnessClient(server, io, diagnostics);
        await client.ConnectAndRecoverAsync(testToken);

        for (int roam = 1; roam <= 8; roam++)
        {
            io.SetCargoPresent(roam % 8, roam % 2 == 0);
            if (roam % 3 == 0)
            {
                await client.SendSafetyStateChangedAsync(
                    client.Current.SafetyStateVersion + 1,
                    DateTimeOffset.UtcNow,
                    new WireToGateSafetySummaryPayload(true, true, true, true, false, []),
                    [(roam % 8) + 1],
                    testToken);
            }

            server.CloseLatestConnection();
            await client.ConnectAndRecoverAsync(testToken);
            Assert.Equal(WireToGateSessionReadiness.Ready, client.Current.Readiness);
        }

        long[] handshakeVersions = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SafetyStateSnapshot")
            .Select(item => RevisionOf(item.WireLine))
            .ToArray();
        Assert.Equal(9, handshakeVersions.Length);
        Assert.True(
            handshakeVersions.Zip(handshakeVersions.Skip(1)).All(pair => pair.Second > pair.First),
            $"handshake snapshot versions [{string.Join(",", handshakeVersions)}] are not strictly increasing");
        Assert.All(
            server.ReceivedEnvelopes.Where(item => item.MessageType == "SafetyStateSnapshot"),
            item => Assert.DoesNotContain("SLOT_STATE_UNKNOWN", SafetyOf(item.WireLine).ReasonCodes));
        SafetyRevisionContent.AssertNone(server.ReceivedEnvelopes);
        Assert.Empty(server.SafetyRevisionConflicts);
    }
}
