using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A recovery vector result whose <c>DurableAck</c> reaches the vehicle anywhere but inside the send that produced it
/// settles the vector all the same (onboard-hmi#150): the handshake's replay, a late ack the session settles on its own
/// (onboard-hmi#250), and an ack recorded just before the process stopped.
/// </summary>
/// <remarks>
/// <para>
/// Until #150 the vector and the recovery session's identity were cleared only by the return of the send that first
/// produced the result (<c>ExecuteRecoveryVectorAndReportAsync</c>). An ack that arrived any other way left both on file:
/// the entry for the same action stayed lit and refused every press with <c>RECOVERY_SESSION_STATE_PENDING</c>, and every
/// other recovery entry stayed grey, with nothing on the server still waiting. One dropped connection is enough.
/// </para>
/// <para>
/// The <c>COMPLETED</c> and the non-<c>COMPLETED</c> paths have the same gap and are pinned in the same shape.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// <c>UNKNOWN</c> (the door pulsed, its lock never answered) or <c>FAILED</c> (a slot unreadable before any pulse), its
    /// ack dropped, the connection lost and the result replayed in the handshake and acknowledged: the vector and the
    /// session are forgotten as on an ack inside the send, once, and the next press opens a second session.
    /// </summary>
    [Theory]
    [InlineData("UNKNOWN")]
    [InlineData("FAILED")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ANonCompletedResultAcknowledgedByTheHandshakeReplayIsForgotten(string outcome)
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
            lockerWaitTimesOut: outcome == "UNKNOWN");

        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        if (outcome == "FAILED")
        {
            // After the vector is prepared, so only the executor's precheck fails.
            harness.Io.SetUnreadable(1);
        }

        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal(outcome, result.GetProperty("overallOutcome").GetString());
        await WaitForAckPendingAsync(harness, token);

        await ReconnectAsync(harness, token);

        // The second fact: the replay was acknowledged. Asserting the record before it would pass on a vehicle that
        // simply had not been told yet.
        await WaitForCompensationResultAcknowledgedAsync(harness, prepared, token);
        Assert.Equal(2, harness.ResultsOfType("LoadCompensationResult").Count);
        await AssertTheVectorAndSessionAreForgottenAsync(harness, token);

        // Another session state, another restore: nothing is settled twice.
        await ReconnectAsync(harness, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ResultsOfType("RecoveryStateReport").Count >= 3,
            "the third handshake's recovery state report",
            token);
        Assert.Single(harness.Logger.Entries, entry =>
            entry.Message.StartsWith("恢复向量结果的DurableAck不在首次发送时到达", StringComparison.Ordinal));
        Assert.Single(harness.OperatorEvents, item => item.Kind == "OPERATION_RECOVERY_REQUIRED"
            && item.Message.StartsWith("恢复结果已上报，但物理状态仍未达到可确认条件", StringComparison.Ordinal));
    }

    /// <summary>
    /// The operator opens a second recovery session in the moment between the settlement from the row reading the journal
    /// and its write. Nothing of that session is forgotten: guard and write are one journal update.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ASettlementFromTheRowRacingASecondSessionForgetsNothingOfIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        RivalSessionJournal? rival = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.LoadCompensationResultAcksToDrop = 1;
            },
            cargoInTargetSlots: true,
            lockerWaitTimesOut: true,
            wrapJournal: inner => rival = new RivalSessionJournal(inner));

        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
        await WaitForAckPendingAsync(harness, token);

        rival!.OpenASecondSessionWhenTheSettlementRuns(prepared.RecoveryVector!);
        await ReconnectAsync(harness, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => rival.SettlementFinished,
            "the settlement from the row to run across the second session",
            token);
        Assert.True(rival.SecondSessionOpened, "the second session was never opened inside the settlement");

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(RivalSessionId, after.ExceptionRecoverySessionId);
        Assert.Equal(RivalActionId, after.RecoveryActionId);
        Assert.Equal(RivalActionId, after.RecoveryVector?.PrimaryId);
    }

    /// <summary>
    /// <c>COMPLETED</c>, the same shape: the vector is settled as on an ack inside the send -- attempt, operation context
    /// and session gone -- and the operator is told it completed.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompletedResultAcknowledgedByTheHandshakeReplayIsSettled()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.LoadCompensationResultAcksToDrop = 1;
            });

        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        await WaitForAckPendingAsync(harness, token);

        await ReconnectAsync(harness, token);

        await WaitForCompensationResultAcknowledgedAsync(harness, prepared, token);
        await AssertTheVectorIsSettledAsCompletedAsync(harness, prepared, token);
    }

    /// <summary>
    /// The ack comes after the send stopped waiting, and the session settles it against the outbox without a reconnect
    /// (onboard-hmi#250). No handshake runs, so nothing that hangs off a session coming up can be what clears the record.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnUnknownResultWhoseAckArrivesLateIsForgottenWithoutAReconnect()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
                // Past the harness's 2 s message timeout.
                server.LoadCompensationResultAckDelay = TimeSpan.FromSeconds(3);
            },
            cargoInTargetSlots: true,
            lockerWaitTimesOut: true);
        long generation = harness.Session.Current.SessionGeneration
            ?? throw new InvalidOperationException("The session has no generation.");

        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
        await WaitForAckPendingAsync(harness, token);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry => entry.Message.StartsWith("收到迟到的DurableAck", StringComparison.Ordinal)
                && entry.Message.Contains("LoadCompensationResult", StringComparison.Ordinal)),
            "the late DurableAck of the compensation result to be settled by the session",
            token);
        Assert.Equal(generation, harness.Session.Current.SessionGeneration);
        Assert.Single(harness.ResultsOfType("LoadCompensationResult"));
        await AssertTheVectorAndSessionAreForgottenAsync(harness, token);
    }

    /// <summary>
    /// The ack is recorded and the process stops before the vector is forgotten -- here the forgetting write fails, which
    /// leaves the journal exactly as a crash at that point would. After a restart the record is cleared, once, with no
    /// result sent again.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnAcknowledgedResultWhoseRecordOutlivedTheProcessIsForgottenAfterARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Path.Combine(
            Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.SendRecoveryVectorCommandAfterRecoveryAction = false;
        server.RecoverySlotOperationAttemptId = AttemptId;

        WireToGateRecoveryState prepared;
        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            cargoInTargetSlots: true,
            lockerWaitTimesOut: true,
            wrapJournal: inner => new FailingSettlementJournal(inner)))
        {
            prepared = await PrepareCompensationAsync(beforeRestart, token);
            await server.SendCommandAsync(
                "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
            await WaitForCompensationResultAcknowledgedAsync(beforeRestart, prepared, token);
            await RecoveryVectorHarness.WaitUntilAsync(
                () => beforeRestart.Logger.Entries.Any(entry =>
                    entry.Message.StartsWith("恢复向量结果已收到服务端确认，但清除向量与恢复会话记录失败", StringComparison.Ordinal)),
                "the forgetting write to fail after the acknowledgement",
                token);
            Assert.NotNull((await beforeRestart.ReadRecoveryStateAsync(token)).RecoveryVector);
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

        await AssertTheVectorAndSessionAreForgottenAsync(afterRestart, token, sessionRequestsAfterThePress: 1);
        Assert.Empty(afterRestart.ResultsOfType("LoadCompensationResult"));
    }

    /// <summary>
    /// A load correction's id is derived from the load, so a second correction of the same load -- after the first one's
    /// <c>FAILED</c> result was acknowledged and forgotten -- is prepared under the key whose acknowledged row the first
    /// left. That row is not the second correction's result: across the next session's restore it stays on file, waiting
    /// for its own command.
    /// </summary>
    /// <param name="recordedAtDiffers">
    /// Whether the vector on file has an observation time of its own, different from the row's -- what a second
    /// preparation that went on to observe its own result would hold -- rather than none.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACorrectionPreparedAgainUnderAnAcknowledgedKeyIsNotSettledByTheOldRow(bool recordedAtDiffers)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoveryVectorSlotOperationAttemptId = AttemptId,
            loadAlreadySettled: true);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCorrection,
            "the load correction entry to be offered",
            token);
        Assert.True(await harness.Business.RequestLoadCorrectionAsync("现场确认需要修正已完成的装货结果。", token));
        WireToGateRecoveryVectorContext first = (await harness.ReadRecoveryStateAsync(token)).RecoveryVector!;
        harness.Io.SetUnreadable(1);
        await SendCorrectionCommandAsync(harness.Server, first, Guid.NewGuid().ToString("D"));
        JsonElement result = await harness.WaitForResultAsync("LoadCorrectionResult", token);
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector is null,
            "the acknowledged FAILED correction to be forgotten",
            token);

        Assert.True(await harness.Business.RequestLoadCorrectionAsync("现场确认需要修正已完成的装货结果。", token));
        WireToGateRecoveryVectorContext second = (await harness.ReadRecoveryStateAsync(token)).RecoveryVector!;
        Assert.Equal(first.PrimaryId, second.PrimaryId);
        Assert.True((await harness.ReadOutgoingAsync(
            $"recovery-vector-result:{second.VectorType}:{second.PrimaryId}", token))?.Acknowledged);
        if (recordedAtDiffers)
        {
            await harness.RewriteRecoveryStateAsync(
                state => state with { RecoveryResultObservedAt = DateTimeOffset.UtcNow.AddMinutes(-5) },
                token);
        }

        await ReconnectAsync(harness, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.OperatorEvents.Any(item => item.Kind == "OPERATION_PROGRESS"
                && item.Message.StartsWith($"恢复向量 {second.VectorType} 尚未完成", StringComparison.Ordinal)),
            "the restore to show the second correction as unfinished",
            token);
        Assert.Equal(second.PrimaryId, (await harness.ReadRecoveryStateAsync(token)).RecoveryVector?.PrimaryId);
        Assert.DoesNotContain(harness.Logger.Entries, entry =>
            entry.Message.StartsWith("恢复向量结果的DurableAck不在首次发送时到达", StringComparison.Ordinal));
    }

    /// <summary>
    /// The ack does not come: the session stays up and a restore runs over the still-owed row -- the vector and the
    /// recovery session stay on file. The result is sent again by the next handshake, and only its acknowledgement settles
    /// the vector (ticket acceptance 4). Settling from a row that is still owed would read "the server has it" off a row
    /// that says the opposite.
    /// </summary>
    /// <remarks>
    /// The ack is held rather than dropped. Dropped, the handshake's replay of the row is never answered either, the
    /// handshake does not come up, and no restore runs at all -- which proves nothing about the restore. Held, the session
    /// goes on, and the double announces a readiness to bring the restore round while the row is owed.
    /// </remarks>
    [Theory]
    [InlineData("ALL_EMPTY")]
    [InlineData("UNKNOWN")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AResultWhoseAckDoesNotComeKeepsItsVectorUntilTheReplayIsAcknowledged(string outcome)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.LoadCompensationResultAckDelay = TimeSpan.FromHours(1);
            },
            cargoInTargetSlots: outcome == "UNKNOWN",
            lockerWaitTimesOut: outcome == "UNKNOWN");

        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal(outcome, result.GetProperty("overallOutcome").GetString());
        await WaitForAckPendingAsync(harness, token);

        await harness.Server.SendSessionReadinessAsync();
        // The second fact: a restore read the row and found it owed. Not the "unfinished" projection: operator events are
        // deduplicated per session generation, and an earlier restore may already have published it.
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Message.StartsWith("恢复向量结果尚未得到服务端确认，保留向量与恢复会话记录", StringComparison.Ordinal)),
            "a restore to find the result still owed",
            token);

        string key = CompensationResultKey(prepared.RecoveryVector!.PrimaryId);
        Assert.False((await harness.ReadOutgoingAsync(key, token))!.Acknowledged);
        WireToGateRecoveryState kept = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(prepared.RecoveryVector.PrimaryId, kept.RecoveryVector?.PrimaryId);
        Assert.Equal(prepared.ExceptionRecoverySessionId, kept.ExceptionRecoverySessionId);
        Assert.Equal(prepared.RecoveryActionId, kept.RecoveryActionId);
        Assert.Single(harness.ResultsOfType("LoadCompensationResult"));
        Assert.DoesNotContain(harness.Logger.Entries, entry =>
            entry.Message.StartsWith("恢复向量结果的DurableAck不在首次发送时到达", StringComparison.Ordinal));

        // The next handshake sends it again; its acknowledgement, and nothing before it, settles the vector.
        harness.Server.LoadCompensationResultAckDelay = TimeSpan.Zero;
        await ReconnectAsync(harness, token);
        await WaitForCompensationResultAcknowledgedAsync(harness, prepared, token);
        Assert.Equal(2, harness.ResultsOfType("LoadCompensationResult").Count);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector is null,
            "the acknowledged replay to settle the vector",
            token);
    }

    private static Task WaitForAckPendingAsync(RecoveryVectorHarness harness, CancellationToken token) =>
        RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Message.Contains("恢复向量结果暂未收到DurableAck", StringComparison.Ordinal)),
            "the vehicle to record that the result has no acknowledgement yet",
            token);

    private static async Task ReconnectAsync(RecoveryVectorHarness harness, CancellationToken token)
    {
        await harness.Session.Client.DisconnectAsync();
        WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);
        Assert.True(reconnected.Connected);
    }

    private static async Task AssertTheVectorIsSettledAsCompletedAsync(
        RecoveryVectorHarness harness,
        WireToGateRecoveryState prepared,
        CancellationToken token)
    {
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector is null,
            "the acknowledged COMPLETED result to settle the vector",
            token);
        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(after.ExceptionRecoverySessionId);
        Assert.Null(after.RecoveryActionId);
        Assert.Null(after.UnsettledSlotOperationAttemptId);
        Assert.Null(after.OperationContext);
        Assert.Contains(harness.OperatorEvents, item => item.Kind == "RECOVERY_VECTOR_COMPLETED"
            && item.Message.StartsWith("补偿清空已完成", StringComparison.Ordinal));
        Assert.NotNull(prepared.RecoveryVector);
    }

    /// <summary>
    /// Fails the one journal update that forgets a settled non-<c>COMPLETED</c> vector, once: what a process stopping
    /// between the acknowledgement and the forgetting leaves on disk.
    /// </summary>
    private sealed class FailingSettlementJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private int _failed;

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            UpdateRecoveryStateAsync(change, static _ => { }, cancellationToken);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default) =>
            Environment.StackTrace.Contains("ForgetSettledRecoveryVectorAsync", StringComparison.Ordinal)
            && Interlocked.Exchange(ref _failed, 1) == 0
                ? throw new IOException("injected: the process stopped before the vector was forgotten")
                : inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

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

        public Task<WireToGateDurableMessage> MarkOutgoingAbandonedAsync(
            string messageId,
            string contentSha256,
            string reasonCode,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAbandonedAsync(messageId, contentSha256, reasonCode, cancellationToken);

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
