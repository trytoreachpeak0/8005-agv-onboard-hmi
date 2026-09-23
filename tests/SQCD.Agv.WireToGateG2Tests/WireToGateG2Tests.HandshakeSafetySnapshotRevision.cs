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
