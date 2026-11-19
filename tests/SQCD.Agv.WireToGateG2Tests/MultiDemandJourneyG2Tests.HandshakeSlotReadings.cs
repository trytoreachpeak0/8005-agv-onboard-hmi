using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The handshake's <c>SafetyStateSnapshot</c> compared by its slot readings too, not by its summary alone
/// (8005-agv-onboard-hmi#419, the handshake side of review M1 of PR #420).
/// </summary>
/// <remarks>
/// The server keeps the handshake snapshot's slot states and reads them as the slots' readings until a later
/// <c>SafetyStateChanged</c> names the slots (control-server <c>SessionSlotReadings</c>). onboard-hmi#399/#401 took the
/// present reading for reported when the handshake's summary matched it; a light curtain read empty by the handshake and
/// occupied again by the time the session is up leaves the summary as it was. No <c>IntegrationSlice</c> or
/// <c>ProtocolVector</c> traits, for the reason <c>HandshakeIoFreshness</c> gives for the roaming tests.
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// The handshake reads slot 1's light curtain empty; by the time the session is up it reads occupied again, as it
    /// did before the roam. The present reading is reported as a change in the new session.
    /// </summary>
    /// <param name="repeatGeneration">The new session repeats the generation number, as after a replaced store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALightCurtainThatMovedOnAfterTheHandshakeReadItIsReportedAgain(bool repeatGeneration)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        await using Harness harness = await Harness.StartAsync(
            server => server.ShareSafetyRevisionAcrossChangeAndSnapshot = true,
            token,
            io: io);
        await WaitForSafetyReportsToSettleAsync(harness, token);

        // Before the roam: slot 1 locked with a basket in it, reported and accepted.
        io.CloseDoor(0, cargo: true);
        io.PublishSnapshot();
        int first = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
        await harness.WaitUntilAsync(
            () => LatestSnapshotOrChangeOn(harness, first) is { } latest
                && harness.Session.Current.SafetyStateVersion == SafetyStateVersionOf(latest),
            "the occupied slot to be reported and accepted before the roam",
            token);
        await Task.Delay(TimeSpan.FromMilliseconds(300), token);

        TaskCompletionSource readinessHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.HandshakeReadinessHold = readinessHold.Task;
        try
        {
            await harness.Session.Client.DisconnectAsync();
            if (repeatGeneration)
            {
                harness.Server.RepeatGenerationOnNextSession();
            }

            io.SetCargoPresent(0, present: false);
            Task<WireToGateSessionSnapshot> reconnect = harness.Session.Client.ConnectAndRecoverAsync(token);
            int connection = 0;
            await harness.WaitUntilAsync(
                () =>
                {
                    connection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
                    return connection != first && OnFile(harness, connection) is [{ MessageType: "SafetyStateSnapshot" }];
                },
                "the handshake snapshot to go on file with the light curtain read empty",
                token);
            string handshakeLine = harness.Server.ReceivedEnvelopes
                .Single(envelope => envelope.Connection == connection && envelope.MessageType == "SafetyStateSnapshot")
                .WireLine;
            Assert.Equal("EMPTY", SlotStateOf(handshakeLine, 1, "physicalState"));

            io.SetCargoPresent(0, present: true);
            readinessHold.SetResult();
            await reconnect;
            await harness.WaitUntilAsync(
                () => OnFile(harness, connection) is [_, { MessageType: "SafetyStateChanged" } last]
                    && harness.Session.Current.SafetyStateVersion == last.Revision,
                "the occupied slot to be reported again in the new session",
                token);

            // Nothing more after it: the change carries the present reading, and the IO stays where it is.
            for (int poll = 0; poll < 5; poll++)
            {
                io.PublishSnapshot();
                await Task.Delay(TimeSpan.FromMilliseconds(100), token);
            }

            Assert.Equal(
                ["SafetyStateSnapshot", "SafetyStateChanged"],
                OnFile(harness, connection).Select(entry => entry.MessageType));
            Assert.Empty(harness.Server.SafetyRevisionConflicts);
        }
        finally
        {
            readinessHold.TrySetResult();
            harness.Server.HandshakeReadinessHold = null;
        }
    }

    private static string? LatestSnapshotOrChangeOn(Harness harness, int connection) =>
        harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.Connection == connection
                && envelope.MessageType is "SafetyStateSnapshot" or "SafetyStateChanged")
            .Select(envelope => envelope.WireLine)
            .LastOrDefault();

    private static string SlotStateOf(string wireLine, int slotNo, string property)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("payload").GetProperty("slotStates").EnumerateArray()
            .Single(slot => slot.GetProperty("slotNo").GetInt32() == slotNo)
            .GetProperty(property).GetString()!;
    }
}
