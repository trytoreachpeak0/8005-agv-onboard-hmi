using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The server concludes an <c>UNKNOWN</c> load on its own -- the withdrawal of a stuck journey writes the operation
/// <c>Cancelled</c> (trytoreachpeak0/8005-agv-control-server#771) -- answers the vehicle READY and commands a load at another
/// slot. What the vehicle does with the record it still holds of the first load (8005-agv-onboard-hmi#372).
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape.</b> A loads slots 1 and 2; slot 1 is filled and shut, then slot 2's lock feedback goes unreadable while its
/// door is open, so A ends <c>UNKNOWN</c>: the journal keeps A as the unsettled attempt with slot 2 in the active unlock
/// set, and every handshake reports both. The server acknowledges the result and holds A for recovery until it settles A
/// itself (<see cref="FakeControlServer.SettleOperationOnServer"/>); the next handshake is then READY although the report
/// still names A, and B is commanded at slot 5.
/// </para>
/// <para>
/// <b>What is pinned.</b> The executor asks a fresh reading for every door of the active unlock set before anything is
/// written (<c>WireToGateSlotOperationExecutor.ExecuteExclusiveAsync</c>, 8005-agv-onboard-hmi#267): slot 2 proven shut, B
/// runs over a clean journal and the overwrite is logged, and A is no longer reported; slot 2 not proven shut, B is refused
/// before any write or pulse and A's record stays. The same holds after the vehicle process restarts on that journal. The
/// boundary -- a recovery whose result the server refused for good still on file -- is
/// <see cref="RecoveryVectorG2Tests.ACommandAfterTheServerSettlesTheLoadIsRefusedWhileARefusedRecoveryAwaitsItsCheck"/>.
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>Slot 2's index on the IO module: the door A leaves in doubt.</summary>
    private const int DoorInDoubt = 1;

    /// <summary>Slot 5's index on the IO module: where B loads.</summary>
    private const int SlotOfB = 4;

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AfterTheServerSettlesAnUnknownLoadACommandOverAShutDoorOverwritesTheRecordAndItIsNoLongerReported()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true, KeepSnapshotFresh = true };
        await using Harness harness = await StartLatchStopAsync(io, token);
        await EndALoadUnknownOverSlot2Async(harness, io, token);
        await SettleAOnTheServerAndReconnectAsync(harness, token);

        // The operator shut slot 2: the fresh reading proves it.
        io.CloseDoor(DoorInDoubt, cargo: true);
        await RunBOverTheRecordAsync(harness, io, pulsesBefore: 2, token);

        // The record of A is gone, and so is what every handshake replayed.
        int connection = await ReconnectAsync(harness, token);
        AssertReportNamesNeitherAttemptNorDoor(harness, connection, AttemptA);
        Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
        Assert.Equal(3, io.UnlockCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AfterTheServerSettlesAnUnknownLoadACommandOverADoorNotProvenShutIsRefusedAndTheRecordStays()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true, KeepSnapshotFresh = true };
        await using Harness harness = await StartLatchStopAsync(io, token);
        await EndALoadUnknownOverSlot2Async(harness, io, token);
        await SettleAOnTheServerAndReconnectAsync(harness, token);

        // Slot 2 still cannot be read.
        await SendSlotCommandAsync(harness, DemandB, AttemptB, [5]);
        await WaitForBRefusedOverSlot2Async(harness, refusals: 1, token);

        Assert.Equal(2, io.UnlockCount);
        AssertAStillOnFile(harness, token);
        Assert.DoesNotContain(harness.Logger.Entries, entry =>
            entry.Message.StartsWith("新仓位操作替换了日志簿里上一次的操作记录", StringComparison.Ordinal));
        Assert.DoesNotContain(ReceivedPayloads(harness, "OperationResult"), payload =>
            payload.GetProperty("slotOperationAttemptId").GetString() == AttemptB);

        // And every handshake still reports A and its door.
        int connection = await ReconnectAsync(harness, token);
        AssertReportNamesAttemptAndDoor(harness, connection, AttemptA);
    }

    /// <summary>
    /// The vehicle process restarts while the record of A is on file, after the server settled A: the restarted vehicle is
    /// answered READY, refuses B while slot 2 is not proven shut, and runs B over the record once it is.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AfterARestartTheServerSettledUnknownLoadIsStillRefusedOverADoorInDoubtAndOverwrittenOnceItIsShut()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true, KeepSnapshotFresh = true };
        await using Harness first = await StartLatchStopAsync(io, token);
        await EndALoadUnknownOverSlot2Async(first, io, token);
        first.Server.SettleOperationOnServer(AttemptA);
        await first.StopVehicleAsync();
        first.Server.SimulateOnboardProcessRestart();
        int connectionsBefore = first.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);

        await using Harness afterRestart = await Harness.StartAgainstAsync(first.Server, token, first.JournalPath, io);
        await afterRestart.WaitUntilAsync(
            () => afterRestart.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the restarted vehicle to be answered READY over the settled attempt",
            token);
        int connection = afterRestart.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
        Assert.True(connection > connectionsBefore);
        AssertReportNamesAttemptAndDoor(afterRestart, connection, AttemptA);
        AssertAStillOnFile(afterRestart, token);

        await SendSlotCommandAsync(afterRestart, DemandB, AttemptB, [5]);
        await WaitForBRefusedOverSlot2Async(afterRestart, refusals: 1, token);
        Assert.Equal(2, io.UnlockCount);
        AssertAStillOnFile(afterRestart, token);

        io.CloseDoor(DoorInDoubt, cargo: true);
        await RunBOverTheRecordAsync(afterRestart, io, pulsesBefore: 2, token);

        connection = await ReconnectAsync(afterRestart, token);
        AssertReportNamesNeitherAttemptNorDoor(afterRestart, connection, AttemptA);
    }

    /// <summary>
    /// A ends <c>UNKNOWN</c> with slot 2 open and unreadable, and the server holds it for recovery (the hmi#370 shape).
    /// </summary>
    private static async Task EndALoadUnknownOverSlot2Async(Harness harness, FakeIoModuleClient io, CancellationToken token)
    {
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 2, token);
        io.SetUnreadable(DoorInDoubt);
        JsonElement result = await WaitForOperationResultAsync(harness, AttemptA, token);
        Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the server to hold A for recovery",
            token);
        AssertAStillOnFile(harness, token);
    }

    /// <summary>
    /// The server settles A and the vehicle meets it at the next handshake, which is READY although the report still
    /// names A: the vehicle replays it, the server takes it off the pending facts.
    /// </summary>
    private static async Task SettleAOnTheServerAndReconnectAsync(Harness harness, CancellationToken token)
    {
        harness.Server.SettleOperationOnServer(AttemptA);
        int connection = await ReconnectAsync(harness, token);
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the handshake after the server settled A to be READY",
            token);
        AssertReportNamesAttemptAndDoor(harness, connection, AttemptA);
        AssertAStillOnFile(harness, token);
    }

    /// <summary>
    /// B runs at slot 5 over the record of A: one pulse, its result <c>COMPLETED</c>, and the overwrite of A and slot 2
    /// logged. Then the journal no longer holds A or slot 2.
    /// </summary>
    private static async Task RunBOverTheRecordAsync(
        Harness harness,
        FakeIoModuleClient io,
        int pulsesBefore,
        CancellationToken token)
    {
        io.CloseDoorAfterNextUnlock(SlotOfB, cargo: true);
        await SendSlotCommandAsync(harness, DemandB, AttemptB, [5]);
        JsonElement result = await WaitForOperationResultAsync(harness, AttemptB, token);
        Assert.Equal("COMPLETED", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(pulsesBefore + 1, io.UnlockCount);
        Assert.Contains(harness.Logger.Entries, entry => entry.Message.StartsWith(
            $"新仓位操作替换了日志簿里上一次的操作记录：attempt={AttemptB}，previousAttempt={AttemptA}，activeUnlockSlots=[2]",
            StringComparison.Ordinal));
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { } state
                && state.UnsettledSlotOperationAttemptId != AttemptA
                && !state.ActiveUnlockSlots.Contains(2),
            "the journal to hold neither A nor slot 2",
            token);
    }

    /// <summary>
    /// Waits for B's <paramref name="refusals"/>-th refusal over slot 2 -- the log line is written last, after the answer
    /// and the screen -- and checks the refusal on the wire is the executor's door refusal.
    /// </summary>
    private static async Task WaitForBRefusedOverSlot2Async(Harness harness, int refusals, CancellationToken token)
    {
        await harness.WaitUntilAsync(
            () => harness.Logger.Entries.Count(entry =>
                    entry.Message.StartsWith("拒收SlotOperationCommand：日志簿记录的仓门未能确认已关好", StringComparison.Ordinal)
                    && entry.Message.Contains($"attempt={AttemptB}", StringComparison.Ordinal)
                    && entry.Message.Contains("slots=[2]", StringComparison.Ordinal)
                    && entry.Message.Contains("unreadable=[2]", StringComparison.Ordinal)
                    && entry.Message.Contains($"recordedAttempt={AttemptA}", StringComparison.Ordinal)) >= refusals,
            "B to be refused over slot 2, which no fresh reading proves shut",
            token);
        JsonElement refusal = Assert.Single(
            ReceivedPayloads(harness, "SlotOperationCommandRejected"),
            payload => payload.GetProperty("slotOperationAttemptId").GetString() == AttemptB);
        Assert.Equal(
            "ACTION_NOT_ALLOWED_IN_STATE",
            refusal.GetProperty("problem").GetProperty("reasonCode").GetString());
    }

    private static void AssertAStillOnFile(Harness harness, CancellationToken token)
    {
        WireToGateRecoveryState onFile = ReadJournal(harness, token);
        Assert.Equal(AttemptA, onFile.UnsettledSlotOperationAttemptId);
        Assert.Equal([2], onFile.ActiveUnlockSlots);
    }

    /// <summary>Disconnects and reconnects the running client; returns the new connection's index.</summary>
    private static async Task<int> ReconnectAsync(Harness harness, CancellationToken token)
    {
        int before = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        int connection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
        Assert.True(connection > before);
        return connection;
    }

    private static void AssertReportNamesAttemptAndDoor(Harness harness, int connection, string attemptId)
    {
        JsonElement report = RecoveryStateReportOn(harness, connection);
        Assert.Equal(attemptId, report.GetProperty("unsettledSlotOperationAttemptId").GetString());
        Assert.Equal([2], report.GetProperty("activeUnlockSlots").EnumerateArray().Select(slot => slot.GetInt32()));
    }

    private static void AssertReportNamesNeitherAttemptNorDoor(Harness harness, int connection, string attemptId)
    {
        JsonElement report = RecoveryStateReportOn(harness, connection);
        JsonElement reported = report.GetProperty("unsettledSlotOperationAttemptId");
        Assert.NotEqual(attemptId, reported.ValueKind == JsonValueKind.String ? reported.GetString() : null);
        Assert.Empty(report.GetProperty("activeUnlockSlots").EnumerateArray());
    }

    private static JsonElement RecoveryStateReportOn(Harness harness, int connection)
    {
        (int _, string _, string _, string wireLine) = Assert.Single(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.Connection == connection && envelope.MessageType == "RecoveryStateReport");
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("payload").Clone();
    }
}
