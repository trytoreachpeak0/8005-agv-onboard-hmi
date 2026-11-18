using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A lock reading that bounces 1→0→1 while the vehicle answers a mid-session <c>SafetyStateSnapshotRequested</c>
/// (8005-agv-onboard-hmi#419, agv02 on 2026-11-18).
/// </summary>
/// <remarks>
/// <para>
/// <b>The field case.</b> agv02 completed a load on slots 5 and 6; slot 6's lock DI read 1→0 at 07:56:42.25 and 0→1 at
/// 07:56:42.36. The server had asked for a snapshot just then (the slot carried an expected-action-overdue alarm,
/// control-server <c>AffectsAnOverdueSlotAsync</c>). The answer read the IO mid-bounce -- slot 6 UNLOCKED -- and held
/// the safety send gate while it waited for its ack. The passes that the two IO changes asked for ran after the ack, read
/// the lock closed again, and found the same signature as the last <c>SafetyStateChanged</c> (the safe one before the
/// bounce): nothing was sent. The server kept the snapshot's UNLOCKED for two hours (DEPARTURE_SAFETY_NOT_READY).
/// </para>
/// <para>
/// <b>The layer.</b> Not the IO sampling (the Modbus client publishes every poll) nor the pass coalescing (each pass reads
/// the live IO): the signature de-duplication in <c>WireToGateBusinessService.QueueSafetyStateChangeAsync</c> stood for
/// "what the server holds" but a mid-session snapshot replaced what the server holds without touching it. The handshake's
/// snapshot was taught the same thing in onboard-hmi#399/#401; the mid-session answer was not.
/// </para>
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>
    /// The load completes, the safe reading is reported; the server asks for a snapshot, the vehicle answers it with the
    /// lock open mid-bounce and waits for its ack, and the lock closes again about 100 ms later, before the ack. The last
    /// safety state the server holds must read the lock closed, at a version above the snapshot's.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-REPLACE-AND-ACK")]
    public async Task ALockBounceCaughtByAMidSessionSnapshotEndsWithTheClosedLockReported()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource ackRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server => server.StationDepartureDeadlineAt = null);
        long safeVersion = await CompleteLoadAndSettleSafeAsync(harness, token);

        // The bounce begins: the lock reads open. Its IO event is announced only once the answer below has read the IO,
        // as on agv02, where the answer won the safety send gate and the change's pass waited behind it.
        harness.Server.BeforeSnapshotAck = (messageType, _) =>
            messageType == "SafetyStateSnapshot" ? ackRelease.Task : Task.CompletedTask;
        harness.Io.SetLockFeedback(0, locked: false);
        await harness.Server.RequestSafetyStateSnapshotAsync();
        await Harness.WaitUntilAsync(
            () => HeldSafety(harness.Server).Version > safeVersion,
            "the snapshot answer, read mid-bounce",
            token,
            () => DescribeSafety(harness.Server));
        HeldSafetyState midBounce = HeldSafety(harness.Server);
        Assert.True(midBounce.MessageType == "SafetyStateSnapshot", DescribeSafety(harness.Server));
        Assert.False(midBounce.DepartureSafe);
        Assert.Contains("LOCK_NOT_CLOSED", midBounce.ReasonCodes);

        harness.Io.PublishSnapshot();
        await Task.Delay(TimeSpan.FromMilliseconds(100), token);
        harness.Io.SetLockFeedback(0, locked: true);
        harness.Io.PublishSnapshot();
        ackRelease.SetResult();

        await Harness.WaitUntilAsync(
            () => HeldSafety(harness.Server) is { DepartureSafe: true } held && held.Version > midBounce.Version,
            "a safety state above the mid-bounce snapshot that reads the lock closed",
            token,
            () => DescribeSafety(harness.Server));
        HeldSafetyState settled = HeldSafety(harness.Server);
        Assert.Equal("SafetyStateChanged", settled.MessageType);
        Assert.DoesNotContain("LOCK_NOT_CLOSED", settled.ReasonCodes);
        Assert.True(harness.Client.Current.Connected);
    }

    /// <summary>
    /// The same bounce, and the snapshot's ack is lost after the server took it. What the server holds is then not known
    /// to the vehicle; the closed lock is reported above the snapshot's version rather than taken for already reported.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-REPLACE-AND-ACK")]
    public async Task ALockBounceCaughtByASnapshotWhoseAckIsLostEndsWithTheClosedLockReported()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server => server.StationDepartureDeadlineAt = null);
        long safeVersion = await CompleteLoadAndSettleSafeAsync(harness, token);

        harness.Server.MidSessionSafetySnapshotAcksToDrop = 1;
        harness.Io.SetLockFeedback(0, locked: false);
        await harness.Server.RequestSafetyStateSnapshotAsync();
        await Harness.WaitUntilAsync(
            () => HeldSafety(harness.Server).Version > safeVersion,
            "the snapshot answer, read mid-bounce",
            token,
            () => DescribeSafety(harness.Server));
        HeldSafetyState midBounce = HeldSafety(harness.Server);
        Assert.True(midBounce.MessageType == "SafetyStateSnapshot", DescribeSafety(harness.Server));
        Assert.False(midBounce.DepartureSafe);
        await Harness.WaitUntilAsync(
            () => harness.Server.AppliedSnapshots.Contains(("SafetyStateSnapshot", midBounce.Version)),
            "the server to take the mid-bounce snapshot whose ack it drops",
            token);

        harness.Io.PublishSnapshot();
        await Task.Delay(TimeSpan.FromMilliseconds(100), token);
        harness.Io.SetLockFeedback(0, locked: true);
        harness.Io.PublishSnapshot();

        // The answer gives up on its ack after the session's message timeout (2 s here).
        await Harness.WaitUntilAsync(
            () => HeldSafety(harness.Server) is { DepartureSafe: true } held && held.Version > midBounce.Version,
            "a safety state above the unacknowledged snapshot that reads the lock closed",
            token,
            () => DescribeSafety(harness.Server));
        Assert.True(harness.Client.Current.Connected);
    }

    /// <summary>
    /// A snapshot that carries the present reading needs nothing after it: the IO keeps publishing, as the Modbus client
    /// does every poll, and no <c>SafetyStateChanged</c> follows. The control server asks for a snapshot after every change
    /// that touches a slot with an overdue alarm, so a change sent after every snapshot would never stop.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-REPLACE-AND-ACK")]
    public async Task ASnapshotThatCarriesThePresentReadingIsNotFollowedByAChange()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server => server.StationDepartureDeadlineAt = null);
        long safeVersion = await CompleteLoadAndSettleSafeAsync(harness, token);

        await harness.Server.RequestSafetyStateSnapshotAsync();
        await Harness.WaitUntilAsync(
            () => harness.Client.Current.SafetyStateVersion > safeVersion,
            "the snapshot answer to be acknowledged",
            token,
            () => DescribeSafety(harness.Server));
        HeldSafetyState snapshot = HeldSafety(harness.Server);
        Assert.True(snapshot.MessageType == "SafetyStateSnapshot", DescribeSafety(harness.Server));
        Assert.True(snapshot.DepartureSafe);

        for (int poll = 0; poll < 5; poll++)
        {
            harness.Io.PublishSnapshot();
            await Task.Delay(TimeSpan.FromMilliseconds(100), token);
        }

        Assert.Equal(snapshot.Version, HeldSafety(harness.Server).Version);
    }

    /// <summary>
    /// Shuts slot 1 on the waiting load, completes it, and waits until the server holds a safe reading for it; returns
    /// that reading's version.
    /// </summary>
    private static async Task<long> CompleteLoadAndSettleSafeAsync(Harness harness, CancellationToken token)
    {
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        harness.Io.CloseDoor(0, cargo: true);
        await harness.WaitForEventAsync("OPERATION_COMPLETED", token);
        harness.Io.PublishSnapshot();
        await Harness.WaitUntilAsync(
            () => HeldSafety(harness.Server) is { DepartureSafe: true } held
                && harness.Client.Current.SafetyStateVersion >= held.Version,
            "the safe reading after the load to be reported and acknowledged",
            token,
            () => DescribeSafety(harness.Server));

        // A completed load reports more than one change (the lock, then the output reset), each from its own pass: the
        // reports have settled once nothing new has arrived for a while.
        int seen = -1;
        for (int round = 0; ; round++)
        {
            int now = SafetyStates(harness.Server).Length;
            if (now == seen && harness.Client.Current.SafetyStateVersion >= HeldSafety(harness.Server).Version)
            {
                break;
            }

            Assert.True(round < 30, $"the safety reports after the load did not settle:{Environment.NewLine}"
                + DescribeSafety(harness.Server));
            seen = now;
            await Task.Delay(TimeSpan.FromMilliseconds(300), token);
        }

        Assert.True(HeldSafety(harness.Server).DepartureSafe, DescribeSafety(harness.Server));
        return HeldSafety(harness.Server).Version;
    }

    private sealed record HeldSafetyState(
        string MessageType,
        long Version,
        bool DepartureSafe,
        IReadOnlyList<string> ReasonCodes);

    /// <summary>
    /// The safety state the server holds on the latest connection: the <c>SafetyStateChanged</c> or
    /// <c>SafetyStateSnapshot</c> with the highest version, as the real server keeps the higher revision
    /// (control-server <c>WireToGateStore.ApplyRevision</c>).
    /// </summary>
    private static HeldSafetyState HeldSafety(FakeControlServer server) =>
        SafetyStates(server).MaxBy(state => state.Version)
            ?? throw new InvalidOperationException("No safety state was received.");

    private static HeldSafetyState[] SafetyStates(FakeControlServer server)
    {
        var envelopes = server.ReceivedEnvelopes.ToArray();
        int connection = envelopes.Max(item => item.Connection);
        return
        [
            .. envelopes
                .Where(item => item.Connection == connection
                    && item.MessageType is "SafetyStateChanged" or "SafetyStateSnapshot")
                .Select(item =>
                {
                    using JsonDocument document = JsonDocument.Parse(item.WireLine);
                    JsonElement payload = document.RootElement.GetProperty("payload");
                    JsonElement safety = payload.GetProperty("safety");
                    return new HeldSafetyState(
                        item.MessageType,
                        payload.GetProperty("safetyStateVersion").GetInt64(),
                        safety.GetProperty("departureSafe").GetBoolean(),
                        [.. safety.GetProperty("reasonCodes").EnumerateArray().Select(code => code.GetString()!)]);
                })
        ];
    }

    private static string DescribeSafety(FakeControlServer server) =>
        string.Join(
            Environment.NewLine,
            SafetyStates(server).Select(state =>
                $"{state.MessageType} v{state.Version} departureSafe={state.DepartureSafe} "
                + $"[{string.Join(',', state.ReasonCodes)}]"));
}
