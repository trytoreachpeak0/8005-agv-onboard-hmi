using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The <c>DurableAck</c> of a <c>SlotConfigurationActivationResult</c>, taken while the server keeps talking
/// (onboard-hmi#140).
/// </summary>
/// <remarks>
/// The real server replays a pending activation as the handshake completes and pushes the journey on the runtime's
/// next pass, so the line after the vehicle's activation result is not necessarily that result's ack. The vehicle
/// used to read the next line and require it to be the ack: a journey snapshot there stopped its receive loop.
/// </remarks>
public sealed partial class WireToGateG2Tests
{
    /// <summary>
    /// Activation first, the journey right behind it -- the real server's order. Every journey snapshot is applied
    /// and acknowledged, the activation result is acknowledged, and the session stays up.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task JourneySnapshotsRightBehindTheActivationAreAcknowledgedAndTheSessionStaysUp()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendSlotConfigurationActivationAfterRecovery = true,
            SendJourneySnapshotsAfterRecovery = true
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());

        await client.ConnectAndRecoverAsync(testToken);
        await WaitBrieflyUntilAsync(
            () => server.Received.Count(item => item.MessageType == "SnapshotAppliedAck") == 3,
            testToken);

        Assert.Equal(
            (3, 1, true, WireToGateSessionReadiness.Ready),
            (server.Received.Count(item => item.MessageType == "SnapshotAppliedAck"),
                server.ReceivedActivationResults.Count,
                client.Current.Connected,
                client.Current.Readiness));
        // A DurableAck round trip after all of that: only a live receive loop delivers it.
        await client.SendOperationProgressAsync(
            "44444444-4444-4444-4444-444444444444",
            "PREPARING",
            [],
            [],
            DateTimeOffset.UtcNow,
            testToken);
        Assert.Equal(1, server.Received.Count(item => item.MessageType == "OperationProgress"));
        Assert.Equal(0, io.UnlockCount);
    }

    /// <summary>
    /// A readiness announcement and a slot command land between the activation result and its ack, after the journey.
    /// Each is handled as it would be anywhere else in the session, and the ack still settles the result.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task MessagesBetweenTheActivationResultAndItsAckAreHandledAndTheSessionStaysUp()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendSlotConfigurationActivationAfterRecovery = true,
            SendJourneySnapshotsAfterRecovery = true,
            WriteBeforeActivationResultAck = ["SessionReadiness", "SlotOperationCommand"]
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());
        int slotCommands = 0;
        client.ServerCommandReceived += (_, args) =>
        {
            if (args.Value is WireToGateSlotOperationCommand)
            {
                Interlocked.Increment(ref slotCommands);
            }
        };

        await client.ConnectAndRecoverAsync(testToken);
        await WaitBrieflyUntilAsync(() => Volatile.Read(ref slotCommands) == 1, testToken);

        Assert.Equal(
            (3, 1, 1, true, WireToGateSessionReadiness.Ready),
            (server.Received.Count(item => item.MessageType == "SnapshotAppliedAck"),
                server.ReceivedActivationResults.Count,
                Volatile.Read(ref slotCommands),
                client.Current.Connected,
                client.Current.Readiness));
        await client.SendOperationProgressAsync(
            "44444444-4444-4444-4444-444444444444",
            "PREPARING",
            [],
            [],
            DateTimeOffset.UtcNow,
            testToken);
        Assert.Equal(1, server.Received.Count(item => item.MessageType == "OperationProgress"));
        Assert.Equal(0, io.UnlockCount);
    }

    /// <summary>
    /// A REJECTED activation is a result like any other: acknowledged through the same wait, with the journey right
    /// behind the command.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task ARejectedActivationResultIsAcknowledgedWithTheJourneyRightBehindIt()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendSlotConfigurationActivationAfterRecovery = true,
            SlotConfigurationActivationFingerprint = new string('b', 64),
            SendJourneySnapshotsAfterRecovery = true,
            WriteBeforeActivationResultAck = ["SessionReadiness"]
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());

        await client.ConnectAndRecoverAsync(testToken);
        await WaitBrieflyUntilAsync(
            () => server.Received.Count(item => item.MessageType == "SnapshotAppliedAck") == 3,
            testToken);

        Assert.Equal(
            (3, 1, true, WireToGateSessionReadiness.Ready),
            (server.Received.Count(item => item.MessageType == "SnapshotAppliedAck"),
                server.ReceivedActivationResults.Count,
                client.Current.Connected,
                client.Current.Readiness));
        Assert.Equal(
            "REJECTED",
            server.ReceivedActivationResults[0].GetProperty("payload").GetProperty("outcome").GetString());
        await client.SendOperationProgressAsync(
            "44444444-4444-4444-4444-444444444444",
            "PREPARING",
            [],
            [],
            DateTimeOffset.UtcNow,
            testToken);
        Assert.Equal(1, server.Received.Count(item => item.MessageType == "OperationProgress"));
    }

    /// <summary>
    /// No ack within the message timeout: the journey is still applied meanwhile, then the session fails closed the
    /// way it did before -- the vehicle cannot tell the server has the result. The next handshake gets the activation
    /// replayed, the vehicle reports the result it already settled, and the server acknowledges that one.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task AnActivationResultAckThatNeverComesFailsTheSessionAndTheReplayIsSettledOnce()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendSlotConfigurationActivationAfterRecovery = true,
            SendJourneySnapshotsAfterRecovery = true,
            ActivationResultAcksToDrop = 1,
            // The real server replays the journey on the next session with the same identity (onboard-hmi#128).
            ReplayJourneySnapshotsWithStableIdentity = true
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(
            server,
            io,
            NewJournalPath(),
            messageTimeout: TimeSpan.FromSeconds(1));

        await client.ConnectAndRecoverAsync(testToken);
        await WaitAtMostUntilAsync(
            () => client.Current.Readiness == WireToGateSessionReadiness.Disconnected,
            TimeSpan.FromSeconds(10),
            testToken);
        // The vehicle wrote every ack before its session failed; give the server time to read the last of them.
        await WaitBrieflyUntilAsync(
            () => server.Received.Count(item => item is { Connection: 1, MessageType: "SnapshotAppliedAck" }) == 3,
            testToken);

        Assert.Equal(
            (3, 1, WireToGateSessionReadiness.Disconnected),
            (server.Received.Count(item => item is { Connection: 1, MessageType: "SnapshotAppliedAck" }),
                server.ReceivedActivationResults.Count,
                client.Current.Readiness));
        Assert.Contains("SESSION_RECOVERY_REQUIRED", client.Current.ReasonCodes);

        await AssertTheReplayedActivationIsSettledOnceAsync(server, client, testToken);
        Assert.Equal(0, io.UnlockCount);
    }

    /// <summary>
    /// The connection closes after the server took the result and before its ack. The next handshake settles it, and
    /// the vehicle does not activate a second time.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task AConnectionLostBeforeTheActivationResultAckIsSettledOnceByTheNextHandshake()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendSlotConfigurationActivationAfterRecovery = true,
            DropBeforeActivationResultAck = true
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());

        await client.ConnectAndRecoverAsync(testToken);
        await WaitAtMostUntilAsync(
            () => client.Current.Readiness == WireToGateSessionReadiness.Disconnected,
            TimeSpan.FromSeconds(10),
            testToken);
        Assert.Equal(
            (1, WireToGateSessionReadiness.Disconnected),
            (server.ReceivedActivationResults.Count, client.Current.Readiness));

        server.DropBeforeActivationResultAck = false;
        await AssertTheReplayedActivationIsSettledOnceAsync(server, client, testToken);
        Assert.Equal(0, io.UnlockCount);
    }

    /// <summary>
    /// The same ack twice. The first settles the result; the second answers nothing the vehicle is waiting for, which
    /// fails the session closed exactly as a stray ack for any other RELIABLE message does. Nothing is activated or
    /// reported again on that connection, and the next handshake settles the replay once.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task ADuplicatedActivationResultAckReportsNothingTwiceAndTheReplayIsSettledOnce()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendSlotConfigurationActivationAfterRecovery = true,
            SendJourneySnapshotsAfterRecovery = true,
            DuplicateActivationResultAck = true,
            // The real server replays the journey on the next session with the same identity (onboard-hmi#128).
            ReplayJourneySnapshotsWithStableIdentity = true
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionClient client = CreateClient(server, io, NewJournalPath());

        await client.ConnectAndRecoverAsync(testToken);
        await WaitAtMostUntilAsync(
            () => client.Current.Readiness == WireToGateSessionReadiness.Disconnected,
            TimeSpan.FromSeconds(10),
            testToken);
        // The vehicle wrote every ack before its session failed; give the server time to read the last of them.
        await WaitBrieflyUntilAsync(
            () => server.Received.Count(item => item is { Connection: 1, MessageType: "SnapshotAppliedAck" }) == 3,
            testToken);

        Assert.Equal(
            (3, 1, WireToGateSessionReadiness.Disconnected),
            (server.Received.Count(item => item is { Connection: 1, MessageType: "SnapshotAppliedAck" }),
                server.ReceivedActivationResults.Count,
                client.Current.Readiness));

        server.DuplicateActivationResultAck = false;
        await AssertTheReplayedActivationIsSettledOnceAsync(server, client, testToken);
        Assert.Equal(0, io.UnlockCount);
    }

    /// <summary>
    /// Reconnects <paramref name="client"/> -- the same client, so the same activation store -- and checks the server
    /// replays the activation, the vehicle answers with the result it already settled (not a second activation), that
    /// result is acknowledged, and the session is up.
    /// </summary>
    private static async Task AssertTheReplayedActivationIsSettledOnceAsync(
        FakeControlServer server,
        WireToGateSessionClient client,
        CancellationToken testToken)
    {
        WireToGateSessionSnapshot resumed = await client.ConnectAndRecoverAsync(testToken);
        await WaitBrieflyUntilAsync(() => server.ReceivedActivationResults.Count == 2, testToken);

        Assert.Equal(WireToGateSessionReadiness.Ready, resumed.Readiness);
        Assert.Equal(2, server.ReceivedActivationResults.Count);
        JsonElement first = server.ReceivedActivationResults[0].GetProperty("payload");
        JsonElement replayed = server.ReceivedActivationResults[1].GetProperty("payload");
        // Same activation, same settledAt: the coordinator returned what it had, it did not activate again.
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(first.GetRawText()), JsonNode.Parse(replayed.GetRawText())),
            $"{first.GetRawText()} <> {replayed.GetRawText()}");
        await client.SendOperationProgressAsync(
            "44444444-4444-4444-4444-444444444444",
            "PREPARING",
            [],
            [],
            DateTimeOffset.UtcNow,
            testToken);
        Assert.Equal(1, server.Received.Count(item => item.MessageType == "OperationProgress"));
        Assert.Equal(WireToGateSessionReadiness.Ready, client.Current.Readiness);
    }

    private static async Task WaitAtMostUntilAsync(
        Func<bool> predicate,
        TimeSpan limit,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            while (!predicate())
            {
                await Task.Delay(5, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// <see cref="WaitUntilAsync"/> without the throw: the assertions after it say what did not happen, which a
    /// cancelled delay does not.
    /// </summary>
    private static async Task WaitBrieflyUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        try
        {
            await WaitUntilAsync(predicate, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}
