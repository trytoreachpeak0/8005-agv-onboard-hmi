using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A recovery vector that ends on anything but <c>COMPLETED</c> is forgotten once the server has
/// acknowledged its result, exactly as a completed one is (onboard-hmi#145 (a)).
/// </summary>
/// <remarks>
/// <para>
/// Until #145 only the <c>COMPLETED</c> branch cleared anything. A <c>FAILED</c> or <c>UNKNOWN</c>
/// result went out, was acknowledged, and left the prepared vector and the recovery session's
/// identity on file for good: every later press of a recovery entry was refused locally with
/// <c>RECOVERY_SESSION_STATE_PENDING</c>, and the only way on was to clear the journal by hand. The
/// server was not stuck -- it closes the session on that result (control-server#169) -- the vehicle
/// was.
/// </para>
/// <para>
/// The <c>CLOSED</c> fallback does not cover this. It forgets only a vector the journal shows did
/// nothing (<c>ForgetRefusedVector</c>), and a vector that reported <c>UNKNOWN</c> after pulsing a
/// door is by definition one that may have acted.
/// </para>
/// <para>
/// Acknowledged first, forgotten second. The clearing hangs off the same return from the durable
/// send the <c>COMPLETED</c> branch uses, so a result whose <c>DurableAck</c> never came leaves the
/// record where it is and is replayed from the outbox on the next session, as it always was.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>The session the operator's second press opens while the first one is being settled.</summary>
    private const string RivalSessionId = "7b7b7b7b-7b7b-4b7b-8b7b-7b7b7b7b7b7b";

    private const string RivalActionId = "7c7c7c7c-7c7c-4c7c-8c7c-7c7c7c7c7c7c";

    private static string CompensationResultKey(string recoveryActionId) =>
        $"recovery-vector-result:{WireToGateRecoveryVectorTypes.LoadCompensation}:{recoveryActionId}";

    /// <summary>
    /// The door was pulsed and its lock never answered: <c>UNKNOWN</c>, with the slot still in the
    /// active unlock set. The vehicle forgets the vector and the session and can ask again.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnUnknownCompensationResultIsForgottenOnceTheServerAcknowledgesIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
            },
            cargoInTargetSlots: true,
            lockerWaitTimesOut: true);

        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));

        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString());
        await WaitForCompensationResultAcknowledgedAsync(harness, prepared, token);

        // The door was pulsed, so the CLOSED fallback would refuse to forget this one: nothing but
        // the result path can clear it.
        Assert.Equal(1, harness.Io.UnlockCount);
        await AssertTheVectorAndSessionAreForgottenAsync(harness, token);
    }

    /// <summary>
    /// The IO precheck could not read a target slot: <c>FAILED</c> before any pulse. Same clearing,
    /// same second session -- the outcome decides nothing here, the acknowledgement does.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AFailedCompensationResultIsForgottenOnceTheServerAcknowledgesIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
            },
            cargoInTargetSlots: true);

        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);

        // Made unreadable after the vector is prepared, so the request path still opens and only the
        // executor's precheck fails.
        harness.Io.SetUnreadable(1);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));

        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        await WaitForCompensationResultAcknowledgedAsync(harness, prepared, token);

        Assert.Equal(0, harness.Io.UnlockCount);
        await AssertTheVectorAndSessionAreForgottenAsync(harness, token);
    }

    /// <summary>
    /// The result reached the outbox and the server never acknowledged it. Nothing is forgotten: the
    /// record is what the next session's replay is judged against, and clearing it here would leave
    /// a replayed result naming a vector this end no longer has.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnUnacknowledgedCompensationResultLeavesTheVectorAndSessionOnFile()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.LoadCompensationResultAcksToDrop = 1;
            },
            cargoInTargetSlots: true,
            lockerWaitTimesOut: true);

        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));

        // RESULT_ACK_PENDING is published by the very branch that would otherwise have cleared the
        // record, so waiting on it is waiting for that decision to have been made.
        await harness.WaitForResultAsync("LoadCompensationResult", token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Message.Contains("恢复向量结果暂未收到DurableAck", StringComparison.Ordinal)),
            "the vehicle to record that the result has no acknowledgement yet",
            token);

        WireToGateDurableMessage? onFile = await harness.ReadOutgoingAsync(
            CompensationResultKey(prepared.RecoveryVector!.PrimaryId), token);
        Assert.NotNull(onFile);
        Assert.False(onFile.Acknowledged);

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.NotNull(after.RecoveryVector);
        Assert.Equal(prepared.ExceptionRecoverySessionId, after.ExceptionRecoverySessionId);
        Assert.Equal(prepared.RecoveryActionId, after.RecoveryActionId);
    }

    /// <summary>
    /// The unacknowledged result is replayed on the next session, once, under its own messageId -- and
    /// the vehicle settles it once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of the case above: having kept the record, this end has to actually get the
    /// result to the server. Nothing in the settlement path does that -- the outbox does, in the
    /// handshake, for every message whose <c>DurableAck</c> is missing. Asserted across a real restart
    /// over the same journal rather than by inspecting the outbox row, because "the row is still
    /// unacknowledged" would hold just as well for a message the handshake had quietly dropped.
    /// </para>
    /// <para>
    /// <b>What this test does not assert is the vector's fate after that acknowledgement.</b> The
    /// settlement runs inside the send that first produced the result; the replay is a different send,
    /// in the session client, and no business-side hook watches it -- so the record stays until the
    /// session's CLOSED snapshot clears it, or, for a vector that may have acted, not at all. The same
    /// gap exists on the <c>COMPLETED</c> path, unchanged since long before #145, which is why it is
    /// onboard-hmi#150's to close on both paths at once rather than this ticket's to patch on one.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnUnacknowledgedCompensationResultIsReplayedOnceOnTheNextSession()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Path.Combine(
            Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.SendRecoveryVectorCommandAfterRecoveryAction = false;
        server.RecoverySlotOperationAttemptId = AttemptId;
        server.LoadCompensationResultAcksToDrop = 1;

        string resultKey;
        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            cargoInTargetSlots: true,
            lockerWaitTimesOut: true))
        {
            WireToGateRecoveryState prepared = await PrepareCompensationAsync(beforeRestart, token);
            resultKey = CompensationResultKey(prepared.RecoveryVector!.PrimaryId);
            await server.SendCommandAsync(
                "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
            await beforeRestart.WaitForResultAsync("LoadCompensationResult", token);
            await RecoveryVectorHarness.WaitUntilAsync(
                () => beforeRestart.Logger.Entries.Any(entry =>
                    entry.Message.Contains("恢复向量结果暂未收到DurableAck", StringComparison.Ordinal)),
                "the vehicle to record that the result has no acknowledgement yet",
                token);
            Assert.Single(beforeRestart.ResultsOfType("LoadCompensationResult"));
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.SendRecoveryVectorCommandAfterRecoveryAction = false;
        serverAfterRestart.RecoverySlotOperationAttemptId = AttemptId;
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            baselineRevision: 2,
            restart: true);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.ResultsOfType("LoadCompensationResult").Count == 1,
            "the handshake to replay the unacknowledged compensation result exactly once",
            token);
        using JsonDocument replayed = JsonDocument.Parse(
            afterRestart.ResultsOfType("LoadCompensationResult")[0]);
        Assert.Equal(
            FakeControlServerIdentifiers.StableUuid(resultKey),
            replayed.RootElement.GetProperty("messageId").GetString());

        // Settled once: the replay was acknowledged, and nothing sent a second copy behind it.
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.ReadOutgoingAsync(resultKey, token)
                .GetAwaiter().GetResult()?.Acknowledged == true,
            "the replayed result to be acknowledged",
            token);
        Assert.Single(afterRestart.ResultsOfType("LoadCompensationResult"));
    }

    /// <summary>
    /// A second recovery session is opened in the moment between the settlement's guard reading the
    /// journal and its write. Nothing of that session is cleared, and nothing is cleared twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state injected here is what the journal holds once this very settlement has already run
    /// once and the operator has pressed again: the first vector gone, a second session open with a
    /// second vector prepared under it. A settlement that read the journal and then wrote what it read
    /// would forget that session -- the operator's second attempt would die the way the first one did,
    /// and for a reason nothing on either end records.
    /// </para>
    /// <para>
    /// It cannot happen, because guard and write are one journal update: the guard runs on the state
    /// the update itself reads, sees a vector that is not the one being settled, and answers "write
    /// nothing". The same shape answers the other two racers -- a second result for this vector, and
    /// the session's CLOSED fallback -- so one assertion covers all three.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ASettlementRacingASecondSessionForgetsNothingOfIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        RivalSessionJournal? rival = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
            },
            cargoInTargetSlots: true,
            lockerWaitTimesOut: true,
            wrapJournal: inner => rival = new RivalSessionJournal(inner));

        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        rival!.OpenASecondSessionWhenTheSettlementRuns(prepared.RecoveryVector!);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));

        await harness.WaitForResultAsync("LoadCompensationResult", token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => rival.SettlementFinished,
            "the settlement to run across the second session",
            token);
        Assert.True(rival.SecondSessionOpened, "the second session was never opened inside the settlement");

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(RivalSessionId, after.ExceptionRecoverySessionId);
        Assert.Equal(RivalActionId, after.RecoveryActionId);
        Assert.Equal(RivalActionId, after.RecoveryVector?.PrimaryId);
    }

    /// <summary>Waits for the vehicle to have recorded the server's DurableAck for the result.</summary>
    private static async Task WaitForCompensationResultAcknowledgedAsync(
        RecoveryVectorHarness harness,
        WireToGateRecoveryState prepared,
        CancellationToken token)
    {
        string key = CompensationResultKey(prepared.RecoveryVector!.PrimaryId);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadOutgoingAsync(key, token).GetAwaiter().GetResult()?.Acknowledged == true,
            "the server's DurableAck for the compensation result to be recorded",
            token);
    }

    /// <summary>
    /// The vector and the session are gone, the unsettled load is not, and the next press opens a
    /// second session for it.
    /// </summary>
    private static async Task AssertTheVectorAndSessionAreForgottenAsync(
        RecoveryVectorHarness harness,
        CancellationToken token)
    {
        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(after.RecoveryVector);
        Assert.Null(after.ExceptionRecoverySessionId);
        Assert.Null(after.RecoveryActionId);
        Assert.Null(after.RecoverySessionRequestId);
        Assert.Equal(AttemptId, after.UnsettledSlotOperationAttemptId);
        Assert.NotNull(after.OperationContext);

        harness.VehicleStopped();
        bool requested = await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token);
        Assert.True(requested, string.Join(" / ", harness.Logger.Entries
            .Where(entry => entry.Severity >= LogSeverity.Warning)
            .Select(entry => entry.Message)
            .TakeLast(3)));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ResultsOfType("ExceptionRecoverySessionRequested").Count == 2,
            "a second recovery session request for the same vehicle",
            token);
    }

    /// <summary>
    /// Opens a second recovery session, with its own vector prepared, inside the next settlement of a
    /// non-<c>COMPLETED</c> recovery vector result -- before the atomic update reads.
    /// </summary>
    /// <remarks>
    /// Armed by stack frame rather than by timing, the way <c>InterleavingJournal</c> is: the race this
    /// stands for is real but far too narrow to hit by sleeping, and a test that hit it by luck would
    /// stop covering anything the first time the code around it changed.
    /// </remarks>
    private sealed class RivalSessionJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private WireToGateRecoveryVectorContext? _settling;
        private int _armed;
        private int _fired;
        private int _finished;

        public bool SecondSessionOpened => Volatile.Read(ref _fired) == 1;

        public bool SettlementFinished => Volatile.Read(ref _finished) == 1;

        public void OpenASecondSessionWhenTheSettlementRuns(WireToGateRecoveryVectorContext settling)
        {
            _settling = settling;
            Volatile.Write(ref _armed, 1);
        }

        /// <summary>
        /// Armed on the read as well as on the atomic update, so that a settlement written as a read
        /// followed by a write -- the shape this test exists to forbid -- is raced too, and fails here
        /// rather than quietly never triggering.
        /// </summary>
        public async Task<WireToGateRecoveryState> ReadRecoveryStateAsync(
            CancellationToken cancellationToken = default)
        {
            if (!TakeTheSettlement())
            {
                return await inner.ReadRecoveryStateAsync(cancellationToken);
            }

            WireToGateRecoveryState read = await inner.ReadRecoveryStateAsync(cancellationToken);
            await OpenASecondSessionAsync(cancellationToken);
            return read;
        }

        public async Task WriteRecoveryStateAsync(
            WireToGateRecoveryState state,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteRecoveryStateAsync(state, cancellationToken);
            if (SecondSessionOpened)
            {
                Volatile.Write(ref _finished, 1);
            }
        }

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            UpdateRecoveryStateAsync(change, static _ => { }, cancellationToken);

        public async Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default)
        {
            bool settlement = TakeTheSettlement();
            if (settlement)
            {
                await OpenASecondSessionAsync(cancellationToken);
            }

            WireToGateRecoveryState? written = await inner.UpdateRecoveryStateAsync(
                change,
                settled,
                cancellationToken);
            if (settlement)
            {
                Volatile.Write(ref _finished, 1);
            }

            return written;
        }

        private bool TakeTheSettlement() =>
            Volatile.Read(ref _armed) == 1
            && Environment.StackTrace.Contains(
                "ForgetSettledRecoveryVectorAsync", StringComparison.Ordinal)
            && Interlocked.Exchange(ref _armed, 0) == 1;

        /// <summary>
        /// What the journal holds once this settlement has already run and the operator has pressed
        /// again: the settled vector gone, a second session open with its own vector prepared.
        /// </summary>
        private async Task OpenASecondSessionAsync(CancellationToken cancellationToken)
        {
            WireToGateRecoveryState state = await inner.ReadRecoveryStateAsync(cancellationToken);
            await inner.WriteRecoveryStateAsync(
                state with
                {
                    ExceptionRecoverySessionId = RivalSessionId,
                    RecoveryActionId = RivalActionId,
                    RecoverySessionRequestId = RivalSessionId,
                    RecoveryVector = _settling! with
                    {
                        PrimaryId = RivalActionId,
                        ExceptionRecoverySessionId = RivalSessionId
                    },
                    RecoveryResultObservedAt = null
                },
                cancellationToken);
            Volatile.Write(ref _fired, 1);
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

        public Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default) =>
            inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);

        public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
            WireToGateDurableMessage expected,
            WireToGateDurableMessage replacement,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceOutgoingForReplayAsync(expected, replacement, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByMessageIdAsync(messageId, cancellationToken);

        public Task MarkOutgoingAcknowledgedAsync(
            string messageId,
            string acceptedContentSha256,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAcknowledgedAsync(messageId, acceptedContentSha256, cancellationToken);

        public Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadUnacknowledgedOutgoingAsync(cancellationToken);

        public Task<IReadOnlyList<WireToGateAppliedJourneySnapshot>> ReadAppliedJourneySnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadAppliedJourneySnapshotsAsync(cancellationToken);

        public Task<WireToGateAppliedJourneySnapshot> SaveAppliedJourneySnapshotAsync(
            WireToGateAppliedJourneySnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            inner.SaveAppliedJourneySnapshotAsync(snapshot, cancellationToken);

        public Task<string> ComputeContentSha256Async(CancellationToken cancellationToken = default) =>
            inner.ComputeContentSha256Async(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
