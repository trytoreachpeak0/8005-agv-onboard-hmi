using System.Net;
using System.Text.Json;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The handshake's <c>SafetyStateSnapshot</c> after the same handshake resent a <c>SafetyStateChanged</c>
/// (onboard-hmi#206).
/// </summary>
/// <remarks>
/// <para>
/// The real server keeps one safety revision per session generation for both messages and compares them by the hash of
/// the whole line (<c>WireToGateStore.ApplySafetySnapshotAsync</c>). A snapshot at the number of the change resent
/// just before it is therefore refused as conflicting content even when its <c>safety</c> is word for word the
/// same -- the two lines differ in <c>messageType</c>, <c>messageId</c> and <c>sentAt</c> -- and the handshake
/// fails. The field's shape (bisect-cs323, generation 2): <c>SessionHello</c>, the resent change v5, the
/// <c>CapabilitySnapshot</c>, and then the snapshot at v5.
/// </para>
/// <para>
/// A revision names one safety state, and the vehicle issues the numbers: the protocol's "same revision, same
/// content" is the vehicle's to keep. These tests run with
/// <see cref="FakeControlServer.ShareSafetyRevisionAcrossChangeAndSnapshot"/>, which models that server rule; the
/// double's default keys its snapshots by message type, and that is why this went unseen.
/// </para>
/// <para>
/// Both sessions are separate generations on purpose, and the collision is inside the second one: the real server
/// clears the safety revision at every new generation, so a snapshot can only collide with a change delivered in the
/// same handshake. The change here never reached the server in the first generation
/// (<see cref="FakeControlServer.DropSafetyStateChangedBeforeAccepting"/>), so the second generation's resend is
/// its first delivery -- a resend of one already taken is answered from the inbox and writes no revision.
/// </para>
/// </remarks>
public sealed partial class WireToGateG2Tests
{
    /// <summary>
    /// Same <c>safety</c> or a different one, the snapshot that follows a resent change in the same handshake carries a
    /// higher revision than that change, the server takes both, and the session comes up ready at the snapshot's
    /// revision.
    /// </summary>
    /// <param name="sameSafety">
    /// True: the resent change says what the snapshot will say, so nothing but the revision can make them collide.
    /// False: it says what the field's v5 said while the snapshot reads the current, safe IO.
    /// </param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-SAME-REVISION-CONFLICT")]
    public async Task AHandshakeSnapshotAfterAResentSafetyChangeTakesAHigherRevisionInTheSameGeneration(bool sameSafety)
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            ShareSafetyRevisionAcrossChangeAndSnapshot = true
        };
        string journalPath = NewJournalPath();
        string onboardInstanceId = Guid.NewGuid().ToString("D");
        FakeIoModuleClient io = new();

        long unsentRevision;
        WireToGateSafetySummaryPayload unsentSafety;
        await using (WireToGateSessionClient firstClient = CreateClient(
            server,
            io,
            journalPath,
            onboardInstanceId: onboardInstanceId))
        {
            await firstClient.ConnectAndRecoverAsync(testToken);
            WireToGateSafetySummaryPayload handshakeSafety = SafetyOf(
                server.ReceivedEnvelopes.Single(item => item.MessageType == "SafetyStateSnapshot").WireLine);
            unsentSafety = sameSafety
                ? handshakeSafety
                : new WireToGateSafetySummaryPayload(
                    false,
                    true,
                    false,
                    false,
                    true,
                    ["SLOT_STATE_UNKNOWN", "LOCK_NOT_CLOSED", "UNLOCK_OUTPUT_NOT_RESET"]);
            Assert.Equal(sameSafety, SameSafety(handshakeSafety, unsentSafety));

            // The next revision, the way the business service numbers a change.
            unsentRevision = firstClient.Current.SafetyStateVersion + 1;
            server.DropSafetyStateChangedBeforeAccepting = true;
            await Assert.ThrowsAnyAsync<IOException>(() => firstClient.SendSafetyStateChangedAsync(
                unsentRevision,
                DateTimeOffset.UtcNow,
                unsentSafety,
                [1, 2, 3, 4, 5, 6, 7, 8],
                testToken));
        }

        server.DropSafetyStateChangedBeforeAccepting = false;
        await using WireToGateSessionClient secondClient = CreateClient(
            server,
            io,
            journalPath,
            onboardInstanceId: onboardInstanceId);
        Exception? handshakeFailure = await Record.ExceptionAsync(() => secondClient.ConnectAndRecoverAsync(testToken));

        // The server's own refusal first, spelled out in full (Assert.Empty cuts it short): before the fix this reads
        // "safety revision 2 has conflicting content".
        IReadOnlyList<string> refusals = server.SafetyRevisionConflicts;
        Assert.True(refusals.Count == 0, $"the server refused: {string.Join(" | ", refusals)}");
        Assert.True(handshakeFailure is null, $"the handshake failed: {handshakeFailure?.GetType().Name}: {handshakeFailure?.Message}");

        var second = server.ReceivedEnvelopes.Where(item => item.Connection == 2).ToArray();
        // The field's order, the change resent before the capability snapshot, both in one generation.
        Assert.Equal(
            ["SessionHello", "SafetyStateChanged", "CapabilitySnapshot", "SafetyStateSnapshot"],
            second.Take(4).Select(item => item.MessageType).ToArray());
        string resentLine = second[1].WireLine;
        string snapshotLine = second[3].WireLine;
        Assert.Equal(GenerationOf(resentLine), GenerationOf(snapshotLine));
        Assert.Equal(unsentRevision, RevisionOf(resentLine));
        Assert.Equal(sameSafety, SameSafety(SafetyOf(resentLine), SafetyOf(snapshotLine)));

        long snapshotRevision = RevisionOf(snapshotLine);
        Assert.True(
            snapshotRevision > unsentRevision,
            $"the handshake snapshot carries revision {snapshotRevision}, not above the change it resent at {unsentRevision}");
        Assert.Equal(WireToGateSessionReadiness.Ready, secondClient.Current.Readiness);
        Assert.Equal(snapshotRevision, secondClient.Current.SafetyStateVersion);
        Assert.Equal(0, io.UnlockCount);
    }

    /// <summary>
    /// Two changes left unacknowledged, both resent in the same handshake: the snapshot goes above the higher of the
    /// two, not merely above the first.
    /// </summary>
    /// <remarks>
    /// The server takes neither in the first generation (<see cref="FakeControlServer.AnswerSafetyStateChanged"/> off:
    /// not handled, not answered, the connection left open), so both are first deliveries in the second and both write
    /// the generation's revision, v2 then v3. The snapshot must be v4.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-SAME-REVISION-CONFLICT")]
    public async Task AHandshakeSnapshotAfterTwoResentSafetyChangesGoesAboveTheLaterOne()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            ShareSafetyRevisionAcrossChangeAndSnapshot = true,
            AnswerSafetyStateChanged = false
        };
        string journalPath = NewJournalPath();
        string onboardInstanceId = Guid.NewGuid().ToString("D");
        FakeIoModuleClient io = new();
        WireToGateSafetySummaryPayload unsafeSummary = new(
            false,
            true,
            false,
            false,
            true,
            ["SLOT_STATE_UNKNOWN", "LOCK_NOT_CLOSED", "UNLOCK_OUTPUT_NOT_RESET"]);

        await using (WireToGateSessionClient firstClient = CreateClient(
            server,
            io,
            journalPath,
            onboardInstanceId: onboardInstanceId,
            messageTimeout: TimeSpan.FromMilliseconds(200)))
        {
            await firstClient.ConnectAndRecoverAsync(testToken);
            Assert.Equal(1, firstClient.Current.SafetyStateVersion);
            foreach (long revision in new long[] { 2, 3 })
            {
                await Assert.ThrowsAsync<TimeoutException>(() => firstClient.SendSafetyStateChangedAsync(
                    revision,
                    DateTimeOffset.UtcNow,
                    unsafeSummary,
                    [1, 2, 3, 4, 5, 6, 7, 8],
                    testToken));
            }
        }

        server.AnswerSafetyStateChanged = true;
        await using WireToGateSessionClient secondClient = CreateClient(
            server,
            io,
            journalPath,
            onboardInstanceId: onboardInstanceId);
        Exception? handshakeFailure = await Record.ExceptionAsync(() => secondClient.ConnectAndRecoverAsync(testToken));

        IReadOnlyList<string> refusals = server.SafetyRevisionConflicts;
        Assert.True(refusals.Count == 0, $"the server refused: {string.Join(" | ", refusals)}");
        Assert.True(handshakeFailure is null, $"the handshake failed: {handshakeFailure?.GetType().Name}: {handshakeFailure?.Message}");
        var second = server.ReceivedEnvelopes.Where(item => item.Connection == 2).ToArray();
        Assert.Equal(
            ["SessionHello", "SafetyStateChanged", "SafetyStateChanged", "CapabilitySnapshot", "SafetyStateSnapshot"],
            second.Take(5).Select(item => item.MessageType).ToArray());
        Assert.Equal([2L, 3L], [RevisionOf(second[1].WireLine), RevisionOf(second[2].WireLine)]);
        Assert.Equal(4, RevisionOf(second[4].WireLine));
        Assert.Equal(WireToGateSessionReadiness.Ready, secondClient.Current.Readiness);
        Assert.Equal(4, secondClient.Current.SafetyStateVersion);
    }

    /// <summary>
    /// The resent change was already taken in the first generation and only its acknowledgement was lost: the server
    /// answers the resend from its inbox and writes no revision in the second generation. The snapshot still takes the
    /// next revision -- one number skipped, which is harmless -- and the handshake completes.
    /// </summary>
    /// <remarks>
    /// The vehicle cannot tell this case from a first delivery: both are answered with a <c>DurableAck</c>. The rule is
    /// therefore "a change was resent", not "a change was newly written on the server", and this pins that it does no
    /// harm when nothing was written.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-SAME-REVISION-CONFLICT")]
    public async Task AHandshakeSnapshotAfterResendingAChangeTheServerAlreadyTookStillCompletesTheHandshake()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            ShareSafetyRevisionAcrossChangeAndSnapshot = true,
            DropBeforeSafetyStateChangedAck = true
        };
        string journalPath = NewJournalPath();
        string onboardInstanceId = Guid.NewGuid().ToString("D");
        FakeIoModuleClient io = new();

        await using (WireToGateSessionClient firstClient = CreateClient(
            server,
            io,
            journalPath,
            onboardInstanceId: onboardInstanceId))
        {
            await firstClient.ConnectAndRecoverAsync(testToken);
            await Assert.ThrowsAnyAsync<IOException>(() => firstClient.SendSafetyStateChangedAsync(
                2,
                DateTimeOffset.UtcNow,
                new WireToGateSafetySummaryPayload(false, true, false, false, true, ["SLOT_STATE_UNKNOWN"]),
                [1, 2, 3, 4, 5, 6, 7, 8],
                testToken));
        }

        Assert.Equal(1, server.AcceptedSafetyStateChangedCount);
        server.DropBeforeSafetyStateChangedAck = false;
        await using WireToGateSessionClient secondClient = CreateClient(
            server,
            io,
            journalPath,
            onboardInstanceId: onboardInstanceId);
        Exception? handshakeFailure = await Record.ExceptionAsync(() => secondClient.ConnectAndRecoverAsync(testToken));

        IReadOnlyList<string> refusals = server.SafetyRevisionConflicts;
        Assert.True(refusals.Count == 0, $"the server refused: {string.Join(" | ", refusals)}");
        Assert.True(handshakeFailure is null, $"the handshake failed: {handshakeFailure?.GetType().Name}: {handshakeFailure?.Message}");
        var second = server.ReceivedEnvelopes.Where(item => item.Connection == 2).ToArray();
        Assert.Equal(
            ["SessionHello", "SafetyStateChanged", "CapabilitySnapshot", "SafetyStateSnapshot"],
            second.Take(4).Select(item => item.MessageType).ToArray());
        // Taken once: the resend was answered from the first acceptance, not accepted again.
        Assert.Equal(1, server.AcceptedSafetyStateChangedCount);
        Assert.Equal(3, RevisionOf(second[3].WireLine));
        Assert.Equal(WireToGateSessionReadiness.Ready, secondClient.Current.Readiness);
        Assert.Equal(3, secondClient.Current.SafetyStateVersion);
    }

    private static long RevisionOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64();
    }

    private static long GenerationOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("sessionGeneration").GetInt64();
    }

    private static WireToGateSafetySummaryPayload SafetyOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        JsonElement safety = document.RootElement.GetProperty("payload").GetProperty("safety");
        return new WireToGateSafetySummaryPayload(
            safety.GetProperty("departureSafe").GetBoolean(),
            safety.GetProperty("vehicleStopped").GetBoolean(),
            safety.GetProperty("allTargetSlotsLocked").GetBoolean(),
            safety.GetProperty("allUnlockOutputsReset").GetBoolean(),
            safety.GetProperty("unknownPresent").GetBoolean(),
            safety.GetProperty("reasonCodes").EnumerateArray().Select(code => code.GetString()!).ToArray());
    }

    // The record's generated equality compares ReasonCodes by reference, so both sides get the one empty array.
    private static bool SameSafety(WireToGateSafetySummaryPayload left, WireToGateSafetySummaryPayload right) =>
        left with { ReasonCodes = Array.Empty<string>() } == right with { ReasonCodes = Array.Empty<string>() }
        && left.ReasonCodes.SequenceEqual(right.ReasonCodes, StringComparer.Ordinal);
}
