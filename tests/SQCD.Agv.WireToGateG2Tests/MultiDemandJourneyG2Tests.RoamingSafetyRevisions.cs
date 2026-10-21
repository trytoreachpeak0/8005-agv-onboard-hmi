using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The vehicle as the App wires it -- session, business service and IO -- roaming from access point to access point:
/// whatever it reports across the reconnects, one <c>safetyStateVersion</c> names one content
/// (8005-agv-onboard-hmi#394).
/// </summary>
/// <remarks>
/// The session-level tests in <c>WireToGateG2Tests.HandshakeSafetyRevisionReuse</c> number the changes by hand. Here the
/// business service numbers them, the way it does on the vehicle, and reports the present reading again after every
/// handshake (ADR-cross-0022), so its numbering and the handshake's meet. No <c>IntegrationSlice</c> or
/// <c>ProtocolVector</c> traits, for the reason <c>HandshakeIoFreshness</c> gives.
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// Six roams, the lock feedback of slot 1 failing and coming back between them: every state is on file and accepted
    /// before the next roam, the server refuses nothing, every handshake snapshot goes out above everything before it, and
    /// no version ever carries two contents.
    /// </summary>
    [Fact]
    public async Task RoamingWithTheBusinessServiceNeverPutsTwoContentsUnderOneVersion()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.ShareSafetyRevisionAcrossChangeAndSnapshot = true;
                server.SendReadinessAfterSafetyStateChangedAck = true;
            },
            token,
            io: io);
        await WaitForSafetyReportsToSettleAsync(harness, token);

        for (int roam = 1; roam <= 6; roam++)
        {
            bool unsafeNow = roam % 2 == 1;
            if (unsafeNow)
            {
                io.SetUnreadable(0);
            }
            else
            {
                io.CloseDoor(0, cargo: false);
            }

            io.PublishSnapshot();
            await WaitForLatestOnFileAsync(harness, departureSafe: !unsafeNow, token);

            await harness.Session.Client.DisconnectAsync();
            await harness.Session.Client.ConnectAndRecoverAsync(token);
            await WaitForLatestOnFileAsync(harness, departureSafe: !unsafeNow, token);
        }

        // Room for anything still on its way to land.
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        Assert.Empty(harness.Server.SafetyRevisionConflicts);
        SafetyRevisionContent.AssertNone(harness.Server.ReceivedEnvelopes);
        long[] handshakeVersions = harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "SafetyStateSnapshot")
            .Select(envelope => SafetyStateVersionOf(envelope.WireLine))
            .ToArray();
        Assert.Equal(7, handshakeVersions.Length);
        long[] changeVersions = harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "SafetyStateChanged")
            .Select(envelope => SafetyStateVersionOf(envelope.WireLine))
            .ToArray();
        Assert.True(
            handshakeVersions.Zip(handshakeVersions.Skip(1)).All(pair => pair.Second > pair.First),
            $"handshake snapshot versions [{string.Join(",", handshakeVersions)}] are not strictly increasing");
        Assert.True(
            handshakeVersions.Skip(1).All(version => !changeVersions.Contains(version)),
            $"a reconnect handshake reused a change's version: snapshots [{string.Join(",", handshakeVersions)}], changes [{string.Join(",", changeVersions)}]");
        Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
    }
}
