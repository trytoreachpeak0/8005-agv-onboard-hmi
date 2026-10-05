using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The pass over stale outbox rows meets one the server refuses for good (onboard-hmi#254, review of PR #258, S4).
/// </summary>
/// <remarks>
/// The rows land in the outbox after the next handshake read it -- the window <see cref="ReconnectRaceJournal"/>
/// holds open -- so that handshake does not replay them and the pass it asks for afterwards is what sends them. Written
/// directly under the earlier generation, the way a send judged on the earlier connection leaves them (onboard-hmi#204).
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// The first stale row is refused as a content conflict and given up; the pass goes on, and the row behind it is
    /// delivered and acknowledged in the same session rather than left for the next handshake.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task AStaleRowGivenUpDoesNotStopThePassBeforeTheRowsBehindIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ReconnectRaceJournal race = null!;
        await using Harness harness = await Harness.StartAsync(
            _ => { },
            token,
            wrapJournal: inner => race = new ReconnectRaceJournal(inner));
        try
        {
            await WaitForSafetyReportsToSettleAsync(harness, token);
            long firstGeneration = harness.Session.Current.SessionGeneration
                ?? throw new InvalidOperationException("The first session has no generation.");

            race.HoldNextHandshakeAfterOutboxRead();
            await harness.Session.Client.DisconnectAsync();
            Task<WireToGateSessionSnapshot> reconnect = harness.Session.Client.ConnectAndRecoverAsync(token);
            await race.HandshakeHeld.WaitAsync(TimeSpan.FromSeconds(10), token);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            WireToGateDurableMessage refused = await race.SaveOutgoingBeforeSendAsync(
                StaleProgressRow("stale-row-refused", firstGeneration, now.AddSeconds(-2)), token);
            WireToGateDurableMessage behind = await race.SaveOutgoingBeforeSendAsync(
                StaleProgressRow("stale-row-behind", firstGeneration, now.AddSeconds(-1)), token);
            harness.Server.ProtocolProblemByMessageId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [refused.MessageId] = "MESSAGE_ID_CONTENT_CONFLICT"
            };

            race.ReleaseHandshake();
            await reconnect;
            int connection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);

            await harness.WaitUntilAsync(
                () => race.ReadOutgoingByMessageIdAsync(behind.MessageId, token).GetAwaiter().GetResult()
                    is { Acknowledged: true },
                "the row behind the given-up one to be delivered and acknowledged in this session",
                token);
            Assert.Equal(
                "MESSAGE_ID_CONTENT_CONFLICT",
                (await race.ReadOutgoingByMessageIdAsync(refused.MessageId, token))?.AbandonedReasonCode);
            Assert.Single(harness.Server.ReceivedEnvelopes, envelope => envelope.MessageId == refused.MessageId);
            Assert.Contains(
                harness.Server.ReceivedEnvelopes,
                envelope => envelope.MessageId == behind.MessageId && envelope.Connection == connection);
            Assert.True(race.StalePassReads > 0, "The rows were delivered but no pass over stale rows was recognised.");
        }
        finally
        {
            race.ReleaseHandshake();
        }
    }

    private static readonly int[] StaleRowActiveSlots = [1];

    private static WireToGateDurableMessage StaleProgressRow(string key, long generation, DateTimeOffset createdAt)
    {
        WireToGateEnvelope envelope = WireToGateProtocolSerializer.Create(
            "OperationProgress",
            Guid.NewGuid().ToString("D"),
            null,
            "AGV-8005-01",
            generation,
            createdAt,
            new
            {
                slotOperationAttemptId = "25425425-4254-4254-8254-000000000004",
                phase = "UNLOCKING",
                activeUnlockSlots = StaleRowActiveSlots,
                completedSlots = Array.Empty<int>(),
                observedAt = createdAt
            });
        return new WireToGateDurableMessage(
            key,
            envelope.MessageType,
            envelope.MessageId,
            WireToGateProtocolSerializer.ComputeContentSha256(envelope),
            WireToGateProtocolSerializer.SerializeLine(envelope),
            createdAt,
            false);
    }
}
