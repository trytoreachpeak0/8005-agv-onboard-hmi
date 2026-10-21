using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A roam's effect on the safety state version, and so on the pre-departure checks the server asks
/// (8005-agv-onboard-hmi#399).
/// </summary>
/// <remarks>
/// <para>
/// <b>Field:</b> the CPE roams between access points and the session reconnects often. Every reconnect handshake sends
/// a full <c>SafetyStateSnapshot</c> under a version of its own (onboard-hmi#394). The business service then reported
/// the same reading again as a <c>SafetyStateChanged</c> one version higher, because a new generation cleared its
/// signature (<c>82066c0</c>, ported from the MVP line). Each rise of the accepted version expires the checks asked
/// before it (CV-PREDEPARTURE-SAFETY-EXPIRES): a <c>DEPARTURE</c> check is asked again one round later, a
/// <c>HOLD_RELEASE</c> release is spent (control-server <c>OnboardRecoveryCoordinator.SpendReleaseCheckAsync</c>).
/// </para>
/// <para>
/// <b>What is asserted:</b> a roam with nothing changed raises the version once, for the handshake's snapshot, and a
/// check asked under that version is answered. A check asked before the roam is still refused: the handshake's own
/// version is #394's, and the release that refusal spends is the server's to ask again (control-server ticket C of
/// onboard-hmi#399). The reason for the resend is gone on v2: the server applies the handshake snapshot like a change
/// and decides readiness on it (control-server <c>OnboardMessageProcessor.cs:542-544</c>,
/// <c>WireToGateStore.DecideReadinessAsync</c>). A reading that changed, during the roam or after the handshake read it,
/// is still reported and still expires the check.
/// </para>
/// <para>
/// No <c>IntegrationSlice</c> or <c>ProtocolVector</c> traits on the roaming tests, for the reason
/// <c>HandshakeIoFreshness</c> gives.
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// The IO does not change across the roam: the handshake's snapshot is the only safety message of the new session,
    /// the accepted version rises once, and a check asked under it is answered.
    /// </summary>
    [Fact]
    public async Task ARoamWithNothingChangedRaisesTheSafetyVersionOnceAndTheCheckAskedAgainIsAnswered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true, KeepSnapshotFresh = true };
        await using Harness harness = await StartWithBusinessStateAsync(io, [], token);
        await WaitForSafetyReportsToSettleAsync(harness, token);
        // Nothing more on its way before the roam.
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        long beforeRoam = harness.Session.Current.SafetyStateVersion;

        int connection = await RoamAsync(harness, token);
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);

        string[] reported = SafetyMessagesOn(harness, connection);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"before the roam v{beforeRoam}; accepted after it v{harness.Session.Current.SafetyStateVersion}; {string.Join(" | ", reported)}");
        long snapshotVersion = SafetyStateVersionOf(harness.Server.ReceivedEnvelopes
            .Single(envelope => envelope.Connection == connection && envelope.MessageType == "SafetyStateSnapshot")
            .WireLine);
        Assert.Equal([$"SafetyStateSnapshot v{snapshotVersion}"], reported);
        Assert.True(snapshotVersion > beforeRoam, $"the premise (#394): the handshake went out at v{snapshotVersion}");
        Assert.Equal(snapshotVersion, harness.Session.Current.SafetyStateVersion);

        Assert.Equal("PREDEPARTURE_CHECK_EXPIRED", await AskDepartureCheckAsync(harness, beforeRoam, token));
        Assert.Equal("SAFE", await AskDepartureCheckAsync(harness, snapshotVersion, token));
        Assert.Equal(snapshotVersion, harness.Session.Current.SafetyStateVersion);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A server that holds no safety state for the vehicle any more -- restarted, or its store replaced -- answers the
    /// handshake READY on the handshake's snapshot alone, with the vehicle standing still and nothing reported after it:
    /// the shape <c>82066c0</c> fixed on the MVP line does not come back.
    /// </summary>
    /// <param name="storeReplaced">The new session repeats the generation number, as a replaced store numbers them from the start again.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AServerThatForgotTheSafetyStateIsReadyOnTheHandshakeSnapshotAlone(bool storeReplaced)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.ShareSafetyRevisionAcrossChangeAndSnapshot = true;
                server.RequireSafeSafetyForReadiness = true;
            },
            token,
            io: io);
        await WaitForSafetyReportsToSettleAsync(harness, token);
        await Task.Delay(TimeSpan.FromMilliseconds(300), token);

        await harness.Session.Client.DisconnectAsync();
        harness.Server.ForgetSafetyOnFile();
        if (storeReplaced)
        {
            harness.Server.RepeatGenerationOnNextSession();
        }

        WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);
        int connection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
        Assert.Equal(WireToGateSessionReadiness.Ready, reconnected.Readiness);
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);

        Assert.Equal(["READY"], ReadinessSentOn(harness, connection));
        Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
        Assert.Equal(
            [("SafetyStateSnapshot", true)],
            OnFile(harness, connection).Select(entry => (entry.MessageType, entry.DepartureSafe)));
        Assert.Empty(harness.Server.SafetyRevisionConflicts);
    }

    /// <summary>
    /// The IO changes while the vehicle is away: the handshake reports the new reading, a check asked before the roam is
    /// refused as expired, and the check asked again under the new version is answered with the new reading.
    /// </summary>
    [Fact]
    public async Task AReadingThatChangedDuringTheRoamExpiresTheCheckAndTheCheckAskedAgainSeesIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true, KeepSnapshotFresh = true };
        await using Harness harness = await StartWithBusinessStateAsync(io, [], token);
        await WaitForSafetyReportsToSettleAsync(harness, token);
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        long beforeRoam = harness.Session.Current.SafetyStateVersion;

        await harness.Session.Client.DisconnectAsync();
        io.SetUnreadable(0);
        io.PublishSnapshot();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        int connection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        long afterRoam = harness.Session.Current.SafetyStateVersion;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"before the roam v{beforeRoam}; accepted after it v{afterRoam}; {string.Join(" | ", SafetyMessagesOn(harness, connection))}");
        Assert.True(afterRoam > beforeRoam, $"the version stayed at v{afterRoam} over a changed reading");
        (int Connection, string MessageType, string MessageId, string WireLine) lastReported = harness.Server.ReceivedEnvelopes
            .Last(envelope => envelope.Connection == connection
                && envelope.MessageType is "SafetyStateSnapshot" or "SafetyStateChanged");
        Assert.Equal(afterRoam, SafetyStateVersionOf(lastReported.WireLine));
        Assert.False(SafetyOf(lastReported.WireLine).GetProperty("departureSafe").GetBoolean());

        Assert.Equal("PREDEPARTURE_CHECK_EXPIRED", await AskDepartureCheckAsync(harness, beforeRoam, token));
        Assert.Equal("UNKNOWN", await AskDepartureCheckAsync(harness, afterRoam, token));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The handshake read the IO in the middle of a change: its snapshot says unsafe, and by the time the session is up
    /// the doors read locked again, as they did before the roam. The present reading is reported as a change of its own:
    /// what the server holds is the handshake's reading, not the one reported before the roam.
    /// </summary>
    [Fact]
    public async Task AReadingThatMovedOnAfterTheHandshakeReadItIsReportedAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        await using Harness harness = await Harness.StartAsync(
            server => server.ShareSafetyRevisionAcrossChangeAndSnapshot = true,
            token,
            io: io);
        await WaitForSafetyReportsToSettleAsync(harness, token);
        await Task.Delay(TimeSpan.FromMilliseconds(300), token);

        TaskCompletionSource readinessHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.HandshakeReadinessHold = readinessHold.Task;
        Task<WireToGateSessionSnapshot>? reconnect = null;
        try
        {
            await harness.Session.Client.DisconnectAsync();
            io.SetUnreadable(0);
            io.PublishSnapshot();
            reconnect = harness.Session.Client.ConnectAndRecoverAsync(token);
            int connection = 0;
            await harness.WaitUntilAsync(
                () =>
                {
                    connection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
                    return OnFile(harness, connection) is [{ MessageType: "SafetyStateSnapshot", DepartureSafe: false }];
                },
                "the handshake snapshot to go on file unsafe",
                token);

            io.CloseDoor(0, cargo: false);
            io.PublishSnapshot();
            readinessHold.SetResult();
            await reconnect;
            await harness.WaitUntilAsync(
                () => OnFile(harness, connection) is [.., var last]
                    && last.DepartureSafe
                    && harness.Session.Current.SafetyStateVersion == last.Revision,
                "the present, safe reading to be reported in the new session and accepted",
                token);

            Assert.Equal(
                [("SafetyStateSnapshot", false), ("SafetyStateChanged", true)],
                OnFile(harness, connection).Select(entry => (entry.MessageType, entry.DepartureSafe)));
            Assert.Empty(harness.Server.SafetyRevisionConflicts);
        }
        finally
        {
            readinessHold.TrySetResult();
            harness.Server.HandshakeReadinessHold = null;
        }
    }

    /// <summary>Disconnects and reconnects the session; returns the new connection's number.</summary>
    private static async Task<int> RoamAsync(Harness harness, CancellationToken token)
    {
        int before = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        int after = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
        Assert.NotEqual(before, after);
        return after;
    }

    /// <summary>The safety messages the vehicle sent on one connection, as <c>Type vN</c>, oldest first.</summary>
    private static string[] SafetyMessagesOn(Harness harness, int connection) =>
    [
        .. harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.Connection == connection
                && envelope.MessageType is "SafetyStateSnapshot" or "SafetyStateChanged")
            .Select(envelope => $"{envelope.MessageType} v{SafetyStateVersionOf(envelope.WireLine)}")
    ];

    /// <summary>
    /// Asks a <c>DEPARTURE</c> check about <paramref name="version"/>; returns the answer's <c>outcome</c>, or the
    /// <c>ProtocolProblem</c>'s code when the vehicle refused it.
    /// </summary>
    private static async Task<string> AskDepartureCheckAsync(Harness harness, long version, CancellationToken token)
    {
        string checkMessageId = Guid.NewGuid().ToString("D");
        string checkId = Guid.NewGuid().ToString("D");
        await harness.Server.SendCommandAsync(
            "PreDepartureSafetyCheck",
            checkMessageId,
            new
            {
                preDepartureSafetyCheckId = checkId,
                checkPurpose = "DEPARTURE",
                demandId = DemandA,
                movementLegId = "22222222-2222-4222-8222-000000000002",
                expectedSafetyStateVersion = version,
                targetStationId = "ST-GATE"
            });
        string? answer = null;
        await harness.WaitUntilAsync(
            () =>
            {
                if (harness.Server.ReceivedEnvelopes.Where(item =>
                        item.MessageType == "PreDepartureSafetyCheckResult"
                        && item.WireLine.Contains(checkId, StringComparison.Ordinal)).Select(item => item.WireLine).FirstOrDefault() is { } result)
                {
                    using JsonDocument document = JsonDocument.Parse(result);
                    answer = document.RootElement.GetProperty("payload").GetProperty("outcome").GetString();
                    return true;
                }

                if (harness.Server.ReceivedEnvelopes.Where(item =>
                        item.MessageType == "ProtocolProblem"
                        && item.WireLine.Contains(checkMessageId, StringComparison.Ordinal)).Select(item => item.WireLine).FirstOrDefault() is { } problem)
                {
                    using JsonDocument document = JsonDocument.Parse(problem);
                    answer = document.RootElement.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString();
                    return true;
                }

                return false;
            },
            $"the check about v{version} to be answered or refused",
            token);
        return answer!;
    }
}
