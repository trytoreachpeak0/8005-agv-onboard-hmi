using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The handshake's <c>SafetyStateSnapshot</c> and <c>RecoveryStateReport</c> report the IO as it reads when each goes out,
/// not as it read before the <c>CapabilitySnapshot</c>'s round trip (8005-agv-onboard-hmi#373).
/// </summary>
/// <remarks>
/// <para>
/// The field's shape (10-19 15:45, agv01 generation 21): the link was slow, the capability snapshot's ack came back a
/// little over two seconds after the vehicle took its IO snapshot, and the safety summary was then judged on that
/// snapshot against the clock of that moment. Older than the one-second freshness limit, it said
/// <c>SLOT_STATE_UNKNOWN</c> -- the module had not failed a single read all day -- and the server stopped the moving
/// vehicle (REQ-0246).
/// </para>
/// <para>
/// These tests build their client with the vehicle's own freshness limit (<c>ioSnapshotMaxAgeMs: 1000</c>), not the
/// thirty seconds the other G2 session tests give theirs, and with an IO module that keeps polling
/// (<see cref="FakeIoModuleClient.KeepSnapshotFresh"/>). The server double holds the capability ack back with
/// <see cref="FakeControlServer.BeforeCapabilitySnapshotAck"/>, which is also where a test changes the IO while the
/// vehicle waits.
/// </para>
/// </remarks>
public sealed partial class WireToGateG2Tests
{
    private static readonly TimeSpan VehicleIoSnapshotMaxAge = TimeSpan.FromMilliseconds(1000);

    // Comfortably above the freshness limit, comfortably below the message timeout of HandshakeFreshnessClient.
    private static readonly TimeSpan SlowCapabilityAck = TimeSpan.FromMilliseconds(1600);

    /// <summary>
    /// The capability ack comes back later than the freshness limit while the IO reads normally throughout: the handshake
    /// snapshot says nothing is unknown, and its <c>slotStates</c> are the readings of the moment it goes out -- a basket
    /// put into slot 2 during the wait shows there, while the capability snapshot sent before it still has the slot empty.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task AHandshakeWhoseCapabilityAckIsSlowerThanTheIoFreshnessLimitReportsTheIoOfTheMomentItSends()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            BeforeCapabilitySnapshotAck = async _ =>
            {
                io.SetCargoPresent(1, true);
                await Task.Delay(SlowCapabilityAck, testToken);
            }
        };
        ConcurrentQueue<string> diagnostics = new();
        await using WireToGateSessionClient client = HandshakeFreshnessClient(server, io, diagnostics);

        await client.ConnectAndRecoverAsync(testToken);

        string capabilityLine = server.ReceivedEnvelopes.Single(item => item.MessageType == "CapabilitySnapshot").WireLine;
        string snapshotLine = server.ReceivedEnvelopes.Single(item => item.MessageType == "SafetyStateSnapshot").WireLine;
        WireToGateSafetySummaryPayload safety = SafetyOf(snapshotLine);
        Assert.True(
            !safety.ReasonCodes.Contains("SLOT_STATE_UNKNOWN") && !safety.UnknownPresent,
            $"the handshake snapshot reported [{string.Join(",", safety.ReasonCodes)}] unknownPresent={safety.UnknownPresent}");
        Assert.True(safety.DepartureSafe);
        Assert.Equal("EMPTY", SlotStateOf(capabilityLine, 2, "physicalState"));
        Assert.Equal("OCCUPIED", SlotStateOf(snapshotLine, 2, "physicalState"));
        Assert.All(
            Enumerable.Range(1, 8),
            slot => Assert.Equal("OPERABLE", SlotStateOf(snapshotLine, slot, "operability")));
        Assert.Equal(WireToGateSessionReadiness.Ready, client.Current.Readiness);

        // What went out is on the vehicle's log, reason codes included.
        string logged = Assert.Single(diagnostics, line => line.StartsWith("握手安全快照：", StringComparison.Ordinal));
        Assert.Contains("reasonCodes=[]", logged, StringComparison.Ordinal);
        Assert.Contains($"safetyStateVersion={RevisionOf(snapshotLine)}", logged, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>RecoveryStateReport</c> reads the IO when it goes out too: an unlock output that came on during the wait is
    /// among its <c>activeUnlockSlots</c>, as it is in the safety snapshot before it.
    /// </summary>
    /// <remarks>
    /// The report's only reading is which outputs are energised, the server's evidence that a result may still be on its
    /// way (control-server <c>OnboardRecoveryCoordinator</c>). It goes out after two more round trips than the safety
    /// snapshot, so the reading it carries is the latest the handshake has.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task AHandshakeRecoveryReportNamesTheUnlockOutputsEnergisedWhenItSends()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            BeforeCapabilitySnapshotAck = async _ =>
            {
                io.SetUnlockOutputActive(3);
                await Task.Delay(SlowCapabilityAck, testToken);
            }
        };
        ConcurrentQueue<string> diagnostics = new();
        await using WireToGateSessionClient client = HandshakeFreshnessClient(server, io, diagnostics);

        await client.ConnectAndRecoverAsync(testToken);

        string snapshotLine = server.ReceivedEnvelopes.Single(item => item.MessageType == "SafetyStateSnapshot").WireLine;
        WireToGateSafetySummaryPayload safety = SafetyOf(snapshotLine);
        Assert.Contains("UNLOCK_OUTPUT_NOT_RESET", safety.ReasonCodes);
        Assert.DoesNotContain("SLOT_STATE_UNKNOWN", safety.ReasonCodes);
        Assert.Equal("ACTIVE", SlotStateOf(snapshotLine, 4, "unlockOutputState"));
        string reportLine = server.ReceivedEnvelopes.Single(item => item.MessageType == "RecoveryStateReport").WireLine;
        Assert.Equal([4], ActiveUnlockSlotsOf(reportLine));
    }

    /// <summary>
    /// The second handshake of the same client -- the link dropped mid-session, as on agv01 -- is held up the same way and
    /// reports the same: nothing unknown over IO that reads normally.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task ASecondHandshakeOfTheSameClientWithASlowCapabilityAckReportsNothingUnknown()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            BeforeCapabilitySnapshotAck = connection => connection == 2
                ? Task.Delay(SlowCapabilityAck, testToken)
                : Task.CompletedTask
        };
        ConcurrentQueue<string> diagnostics = new();
        await using WireToGateSessionClient client = HandshakeFreshnessClient(server, io, diagnostics);

        await client.ConnectAndRecoverAsync(testToken);
        long firstGeneration = client.Current.SessionGeneration!.Value;
        server.CloseLatestConnection();
        await client.ConnectAndRecoverAsync(testToken);

        Assert.True(client.Current.SessionGeneration > firstGeneration);
        string[] snapshots = server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SafetyStateSnapshot")
            .Select(item => item.WireLine)
            .ToArray();
        Assert.Equal(2, snapshots.Length);
        WireToGateSafetySummaryPayload second = SafetyOf(snapshots[1]);
        Assert.True(
            !second.ReasonCodes.Contains("SLOT_STATE_UNKNOWN") && !second.UnknownPresent,
            $"the second handshake snapshot reported [{string.Join(",", second.ReasonCodes)}]");
        Assert.Equal(WireToGateSessionReadiness.Ready, client.Current.Readiness);
        Assert.Equal(2, diagnostics.Count(line => line.StartsWith("握手安全快照：", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The IO really fails while the vehicle waits for the capability ack: the handshake snapshot still says
    /// <c>SLOT_STATE_UNKNOWN</c>. Taking the reading at the moment of sending must not lose a true unknown -- not one slot
    /// whose feedback can no longer be read, and not a module that has gone quiet and stopped refreshing its readings.
    /// </summary>
    /// <param name="moduleGoesQuiet">
    /// True: the whole module stops answering, its last readings three seconds old. False: slot 3's lock feedback can no
    /// longer be read, the rest still reads.
    /// </param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task AnIoFailureDuringTheCapabilityAckWaitIsStillReportedAsUnknown(bool moduleGoesQuiet)
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            BeforeCapabilitySnapshotAck = _ =>
            {
                if (moduleGoesQuiet)
                {
                    io.KeepSnapshotFresh = false;
                    io.MakeStale(TimeSpan.FromSeconds(3));
                }
                else
                {
                    io.SetUnreadable(2);
                }

                return Task.CompletedTask;
            }
        };
        ConcurrentQueue<string> diagnostics = new();
        await using WireToGateSessionClient client = HandshakeFreshnessClient(server, io, diagnostics);

        await client.ConnectAndRecoverAsync(testToken);

        string capabilityLine = server.ReceivedEnvelopes.Single(item => item.MessageType == "CapabilitySnapshot").WireLine;
        string snapshotLine = server.ReceivedEnvelopes.Single(item => item.MessageType == "SafetyStateSnapshot").WireLine;
        WireToGateSafetySummaryPayload safety = SafetyOf(snapshotLine);
        Assert.Contains("SLOT_STATE_UNKNOWN", safety.ReasonCodes);
        Assert.True(safety.UnknownPresent);
        Assert.False(safety.DepartureSafe);
        if (!moduleGoesQuiet)
        {
            // Read before the failure, the capability snapshot has the slot; the safety snapshot does not.
            Assert.Equal("OPERABLE", SlotStateOf(capabilityLine, 3, "operability"));
            Assert.Equal("UNKNOWN", SlotStateOf(snapshotLine, 3, "lockState"));
        }

        string logged = Assert.Single(diagnostics, line => line.StartsWith("握手安全快照：", StringComparison.Ordinal));
        Assert.Contains("SLOT_STATE_UNKNOWN", logged, StringComparison.Ordinal);
    }

    /// <summary>
    /// The capability ack comes too late for the handshake that asked for it: that handshake fails on its message timeout
    /// and sends no safety snapshot, and the next one, whose ack is merely slow, reports nothing unknown over IO that
    /// reads normally.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task ACapabilityAckPastTheMessageTimeoutFailsThatHandshakeAndTheNextReportsNothingUnknown()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        TaskCompletionSource releaseFirstAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            BeforeCapabilitySnapshotAck = connection => connection == 1
                ? releaseFirstAck.Task
                : Task.Delay(SlowCapabilityAck, testToken)
        };
        ConcurrentQueue<string> diagnostics = new();
        await using WireToGateSessionClient client = HandshakeFreshnessClient(server, io, diagnostics);

        Exception? firstFailure = await Record.ExceptionAsync(() => client.ConnectAndRecoverAsync(testToken));
        Assert.True(firstFailure is TimeoutException, $"the first handshake ended with {firstFailure?.GetType().Name}: {firstFailure?.Message}");
        Assert.DoesNotContain(
            server.ReceivedEnvelopes,
            item => item.Connection == 1 && item.MessageType == "SafetyStateSnapshot");
        // The ack the first handshake gave up on goes out now, onto a connection the vehicle has left.
        releaseFirstAck.SetResult();

        await client.ConnectAndRecoverAsync(testToken);

        string snapshotLine = Assert.Single(
            server.ReceivedEnvelopes,
            item => item.MessageType == "SafetyStateSnapshot").WireLine;
        WireToGateSafetySummaryPayload safety = SafetyOf(snapshotLine);
        Assert.True(
            !safety.ReasonCodes.Contains("SLOT_STATE_UNKNOWN") && !safety.UnknownPresent,
            $"the handshake after the timeout reported [{string.Join(",", safety.ReasonCodes)}]");
        Assert.Equal(WireToGateSessionReadiness.Ready, client.Current.Readiness);
    }

    private static WireToGateSessionClient HandshakeFreshnessClient(
        FakeControlServer server,
        FakeIoModuleClient io,
        ConcurrentQueue<string> diagnostics)
    {
        WireToGateSessionClient client = new(
            CreateSessionOptions(server, messageTimeout: TimeSpan.FromSeconds(5)),
            io,
            new SqliteWireToGateJournal(NewJournalPath()),
            new SystemClock(),
            new DelegateVehicleSafetySignalProvider(() => true),
            new OnboardAlarmBoard("AGV-G2", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
            VehicleIoSnapshotMaxAge,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));
        client.DiagnosticRecorded += (_, args) => diagnostics.Enqueue(args.Value);
        return client;
    }

    private static string SlotStateOf(string wireLine, int slotNo, string property)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement
            .GetProperty("payload")
            .GetProperty("slotStates")
            .EnumerateArray()
            .Single(slot => slot.GetProperty("slotNo").GetInt32() == slotNo)
            .GetProperty(property)
            .GetString()!;
    }

    private static int[] ActiveUnlockSlotsOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement
            .GetProperty("payload")
            .GetProperty("activeUnlockSlots")
            .EnumerateArray()
            .Select(slot => slot.GetInt32())
            .ToArray();
    }
}
