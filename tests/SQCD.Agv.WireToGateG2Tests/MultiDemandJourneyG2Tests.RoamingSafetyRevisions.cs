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

    /// <summary>
    /// A change that never left the vehicle, whose version one handshake's snapshot took for other content, is still
    /// owed when the session drops again before the business service has given it up: the next handshake does not
    /// replay it under that version, it is settled there, and the business service does not take it for delivered.
    /// </summary>
    /// <remarks>
    /// Review M1 of PR #397, the reviewer's probe: the shape of <c>LandedAfterTheOutboxRead</c> (the change's row lands
    /// after the second handshake has read the outbox, and its send finds the connection gone), then the business
    /// service's give-up of that row fails once -- a stand-in for anything that ends the session before it runs, a
    /// second roam above all. Before the fix the third handshake replayed the row: <c>[c2] Snapshot v4
    /// {departureSafe:false, SLOT_STATE_UNKNOWN}</c> then <c>[c3] SafetyStateChanged v4 {departureSafe:true}</c>.
    /// </remarks>
    [Fact]
    public async Task ARowOneHandshakeNumberedPastIsNotReplayedByTheNext()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        ReconnectRaceJournal race = null!;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.ShareSafetyRevisionAcrossChangeAndSnapshot = true;
                server.RequireSafeSafetyForReadiness = true;
                server.SendReadinessAfterSafetyStateChangedAck = true;
            },
            token,
            io: io,
            wrapJournal: inner => race = new ReconnectRaceJournal(inner));
        Task<WireToGateSessionSnapshot>? reconnect = null;
        try
        {
            await WaitForSafetyReportsToSettleAsync(harness, token);
            io.SetUnreadable(0);
            io.PublishSnapshot();
            await WaitForLatestOnFileAsync(harness, departureSafe: false, token);

            // The door reads locked again; that change is held just before its outbox row is written.
            race.HoldNextSafetyStateChange();
            io.CloseDoor(0, cargo: false);
            io.PublishSnapshot();
            await race.SafetyChangeHeld.WaitAsync(TimeSpan.FromSeconds(10), token);

            // A roam: the next handshake reads the outbox before the row lands.
            race.HoldNextHandshakeAfterOutboxRead();
            await harness.Session.Client.DisconnectAsync();
            reconnect = harness.Session.Client.ConnectAndRecoverAsync(token);
            await race.HandshakeHeld.WaitAsync(TimeSpan.FromSeconds(10), token);

            io.SetUnreadable(0);
            io.PublishSnapshot();

            race.ReleaseSafetyChange();
            await harness.WaitUntilAsync(
                () => SafetyReportFailures(harness).Length > 0,
                "the held change to find its connection gone",
                token);
            WireToGateDurableMessage passed = race.HeldRowAsSaved
                ?? throw new InvalidOperationException("The held change was never written.");
            long passedVersion = SafetyStateVersionOf(passed.WireLine);

            // The business service's give-up of that row fails once, and the session drops.
            race.FailNextAcknowledgement(passed.MessageId);
            race.ReleaseHandshake();
            await reconnect;
            await race.AcknowledgementFailed.WaitAsync(TimeSpan.FromSeconds(10), token);
            await harness.WaitUntilAsync(
                () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Disconnected,
                "the business service to disconnect after the failed give-up",
                token);
            int secondConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
            long secondSnapshotVersion = SafetyStateVersionOf(harness.Server.ReceivedEnvelopes
                .Single(envelope => envelope.Connection == secondConnection && envelope.MessageType == "SafetyStateSnapshot")
                .WireLine);
            Assert.True(secondSnapshotVersion >= passedVersion, $"the premise: the second handshake went out at v{secondSnapshotVersion}");

            // The next roam's handshake.
            harness.Server.SendReadinessAfterSafetyStateChangedAck = false;
            await harness.Session.Client.ConnectAndRecoverAsync(token);
            int thirdConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
            await harness.WaitUntilAsync(
                () => OnFile(harness, thirdConnection) is [.., var last]
                    && last.MessageType == "SafetyStateChanged"
                    && !last.DepartureSafe
                    && harness.Session.Current.SafetyStateVersion == last.Revision,
                "the present reading to be reported again in the third session and accepted",
                token);
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);

            TestContext.Current.TestOutputHelper?.WriteLine(string.Join(
                Environment.NewLine,
                harness.Server.ReceivedEnvelopes
                    .Where(envelope => envelope.MessageType is "SafetyStateChanged" or "SafetyStateSnapshot")
                    .Select(envelope => $"[c{envelope.Connection}] {envelope.MessageType} v{SafetyStateVersionOf(envelope.WireLine)} {SafetyOf(envelope.WireLine).GetRawText()}")));
            Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, envelope => envelope.MessageId == passed.MessageId);
            SafetyRevisionContent.AssertNone(harness.Server.ReceivedEnvelopes);
            Assert.Empty(harness.Server.SafetyRevisionConflicts);
            Assert.True(
                !OnFile(harness, thirdConnection).Any(entry => entry.DepartureSafe),
                "the third session had departure-safe on file while the IO read unsafe");
            Assert.DoesNotContain(
                await race.ReadUnacknowledgedOutgoingAsync(token),
                row => row.MessageType == "SafetyStateChanged");
        }
        finally
        {
            race.ReleaseSafetyChange();
            race.ReleaseHandshake();
        }
    }
}
