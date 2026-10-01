using System.Diagnostics;
using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A journal that fails under a resume still leaves the server with an answer (8005-agv-onboard-hmi#233): an
/// UNKNOWN replacement result once door IO may have happened, a rejection while it certainly has not, and a result
/// that reaches the outbox even when the first write of it does not.
/// </summary>
/// <remarks>
/// Until then an exception the executor let out after its first journal write reached the resume's last catch,
/// which told the operator and nobody else; that catch's comment said the server would hear through the
/// interrupted settlement's result, which it would not -- that result is keyed <c>operation-result:{attempt}</c>
/// and already on file. A failed save of the result itself was reported as "persisted, awaiting ack".
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// The resume pulses slot 1, the door never comes back, and the journal fails under the checkpoint the
    /// executor writes for that failure: the exception leaves the executor with the door IO behind it.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    [InlineData(JournalFault.Injected)]
    [InlineData(JournalFault.ReadOnlyDatabase)]
    public async Task AJournalFailureAfterTheFirstPulseIsAnsweredUnknown(JournalFault kind)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FaultInjectingJournal? faults = null;
        string journalPath = NewJournalPath();
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            lockerWaitTimesOut: true,
            journalPath: journalPath,
            wrapJournal: inner => faults = new FaultInjectingJournal(inner, kind, journalPath));
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
        // Every checkpoint write of the executor's slot loop fails once a door has been pulsed. The write in
        // front of the first pulse goes through; the one after the pulse is in the executor's own catch, and
        // the one that catch makes for the failure is not caught by anything inside the executor. The answer
        // that follows runs as a continuation inside that same failing stack, so its pending-result write is
        // let through by name: this case is the escape, AResumeResultWhoseFirstWriteFails... is that write.
        faults!.UpdateFault = () =>
            harness.Io.UnlockCount > 0
            && Environment.StackTrace is var stack
            && stack.Contains("ExecuteRemainingSlotsAsync", StringComparison.Ordinal)
            && !stack.Contains("RecordPendingResultAsync", StringComparison.Ordinal);

        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(state));

        string resumeResultId = FakeControlServerIdentifiers.StableUuid(
            $"recovery-operation-result:{AttemptId}:{state.RecoveryActionId}");
        await WaitLongAsync(
            () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                    envelope.MessageType == "OperationResult" && envelope.MessageId == resumeResultId)
                || Rejections(harness).Any(item =>
                    item.GetProperty("correlationId").GetString() == ResumeMessageId),
            "an answer to the resume whose checkpoint write failed after its first pulse",
            TimeSpan.FromSeconds(20),
            token);
        Assert.True(faults.Fired, "The journal fault never fired: this run did not test the escape.");
        // The answer came from the escape path, not from an executor that wrote its failure checkpoint after all.
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Message.StartsWith("恢复命令执行中断，无法确定执行到哪一步", StringComparison.Ordinal));
        Assert.Empty(Rejections(harness));
        using JsonDocument result = JsonDocument.Parse(harness.Server.ReceivedEnvelopes.Single(envelope =>
            envelope.MessageType == "OperationResult" && envelope.MessageId == resumeResultId).WireLine);
        JsonElement payload = result.RootElement.GetProperty("payload");
        Assert.Equal("UNKNOWN", payload.GetProperty("overallOutcome").GetString());
        Assert.Equal("ACTIVE_UNLOCK_SET", payload.GetProperty("journalCheckpoint").GetString());

        // The one pulse the resume made before the failure, and no other -- held over a stretch, since a
        // door opened after the answer is exactly what must not happen.
        int unlocks = harness.Io.UnlockCount;
        Assert.Equal(1, unlocks);
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(1))
        {
            Assert.Equal(unlocks, harness.Io.UnlockCount);
            await Task.Delay(20, token);
        }
    }

    /// <summary>
    /// The journal fails on the first read the resume makes, before it claims the attempt: no door can have
    /// moved, so the server is refused with <c>VEHICLE_NOT_READY</c> and the operator told.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AJournalFailureBeforeTheClaimIsAnsweredWithARejection()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FaultInjectingJournal? faults = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            wrapJournal: inner => faults = new FaultInjectingJournal(inner));
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
        int once = 0;
        faults!.ReadOutgoingFault = key =>
            key.StartsWith("slot-operation-resume-rejected:", StringComparison.Ordinal)
            && Interlocked.Exchange(ref once, 1) == 0;

        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(state));

        await harness.WaitForRecoveryBlockedAsync(token);
        JsonElement rejection = await WaitForSingleRejectionAsync(harness, token);
        Assert.True(faults.Fired, "The journal fault never fired: this run did not test the escape.");
        Assert.Equal(ResumeMessageId, rejection.GetProperty("correlationId").GetString());
        Assert.Equal(
            "VEHICLE_NOT_READY",
            rejection.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        await AssertNoUnlockOverAsync(harness, TimeSpan.FromSeconds(1), token);
    }

    /// <summary>
    /// The journal refuses the pending-result entry and then the first outbox write of the resume's result. The
    /// result is still written on the second try and reaches the server; until onboard-hmi#233 both failures
    /// ended in RESULT_ACK_PENDING, "persisted", with nothing persisted and nothing sent.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    [InlineData(JournalFault.Injected)]
    [InlineData(JournalFault.ReadOnlyDatabase)]
    public async Task AResumeResultWhoseFirstWriteFailsStillReachesTheServer(JournalFault kind)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FaultInjectingJournal? faults = null;
        string journalPath = NewJournalPath();
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            lockerWaitTimesOut: true,
            journalPath: journalPath,
            wrapJournal: inner => faults = new FaultInjectingJournal(inner, kind, journalPath));
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
        string resultKey = $"recovery-operation-result:{AttemptId}:{state.RecoveryActionId}";
        int pendingOnce = 0;
        int saveOnce = 0;
        faults!.UpdateFault = () =>
            Environment.StackTrace.Contains("RecordPendingResultAsync", StringComparison.Ordinal)
            && Interlocked.Exchange(ref pendingOnce, 1) == 0;
        faults.SaveOutgoingFault = message =>
            message.DeduplicationKey == resultKey && Interlocked.Exchange(ref saveOnce, 1) == 0;

        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(state));

        string resumeResultId = FakeControlServerIdentifiers.StableUuid(resultKey);
        await WaitLongAsync(
            () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                envelope.MessageType == "OperationResult" && envelope.MessageId == resumeResultId),
            "the resume's result to reach the server after its first write failed",
            TimeSpan.FromSeconds(20),
            token);
        Assert.Equal(1, Volatile.Read(ref pendingOnce));
        Assert.Equal(1, Volatile.Read(ref saveOnce));
        // Both writes did fail, whichever way: the result reached the server through the paths under test.
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Message.StartsWith("恢复结果未能记为待报结果，仍照常发送", StringComparison.Ordinal));
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Message.StartsWith("恢复OperationResult未能写入发件箱，重试一次", StringComparison.Ordinal));
        Assert.Empty(Rejections(harness));
        Assert.Equal(1, harness.Io.UnlockCount);
    }

    /// <summary>
    /// The resume's result reaches the outbox, and the send breaks before it is put on the wire. On file is not
    /// sent: left there, it would wait for the next handshake's replay, and on a link that stays up the server
    /// would wait until it dropped. The vehicle sends it once more in the session it is in.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeResultOnFileButNotSentGoesOutInTheSameSession()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FaultInjectingJournal? faults = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            lockerWaitTimesOut: true,
            wrapJournal: inner => faults = new FaultInjectingJournal(inner));
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
        string resultKey = $"recovery-operation-result:{AttemptId}:{state.RecoveryActionId}";
        int once = 0;
        faults!.SaveOutgoingFaultAfterWrite = message =>
            message.DeduplicationKey == resultKey && Interlocked.Exchange(ref once, 1) == 0;
        int connection = harness.Server.ReceivedEnvelopes[^1].Connection;

        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(state));

        string resumeResultId = FakeControlServerIdentifiers.StableUuid(resultKey);
        await WaitLongAsync(
            () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                envelope.MessageType == "OperationResult" && envelope.MessageId == resumeResultId),
            "the resume's result, on file but never sent, to go out in the same session",
            TimeSpan.FromSeconds(20),
            token);
        Assert.Equal(1, Volatile.Read(ref once));
        Assert.All(
            harness.Server.ReceivedEnvelopes.Where(envelope => envelope.MessageId == resumeResultId),
            envelope => Assert.Equal(connection, envelope.Connection));
        // Waited for, not read once: the vehicle records the ack after it has arrived, so the server holding the
        // result says nothing yet about the vehicle's outbox (second incremental review of PR #234: red 2 runs in 3
        // of the full suite, always with a 300 ms gap between the two).
        await WaitLongAsync(
            () => harness.IsOutgoingAcknowledgedAsync(resumeResultId, token).GetAwaiter().GetResult(),
            "the resent result to be recorded as acknowledged in the session it was resent in",
            TimeSpan.FromSeconds(10),
            token);
        Assert.Empty(Rejections(harness));
    }

    /// <summary>How a picked journal call fails.</summary>
    public enum JournalFault
    {
        /// <summary>The wrapper throws <see cref="IOException"/> itself, before the real journal is reached.</summary>
        Injected,

        /// <summary>
        /// The real database file is made read-only for that one call, so SQLite itself fails (Error 8) and the
        /// production journal's own conversion is what the caller sees (review of PR #234: P4, T1).
        /// </summary>
        ReadOnlyDatabase
    }

    private static string NewJournalPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>
    /// Passes everything through to the real journal, except a write or read a test has picked, which fails --
    /// the journal failing under the caller. A write fails either way <see cref="JournalFault"/> names; a read,
    /// and a failure after the outbox write, only as <see cref="JournalFault.Injected"/>, since a read-only file
    /// does not fail a read and cannot fail after a write that went through.
    /// </summary>
    private sealed class FaultInjectingJournal(
        IWireToGateJournal inner,
        JournalFault kind = JournalFault.Injected,
        string? databasePath = null) : IWireToGateJournal
    {
        private int _fired;

        /// <summary>Asked before every recovery-state write; true fails that write.</summary>
        public Func<bool>? UpdateFault { get; set; }

        /// <summary>
        /// Asked before every recovery-state write; a task returned holds that write -- and whoever is making it --
        /// until it completes. Taken before the real journal's lock, so nothing else waits on it.
        /// </summary>
        public Func<Task?>? UpdateHold { get; set; }

        /// <summary>Asked before every outbox read by key; true fails that read.</summary>
        public Func<string, bool>? ReadOutgoingFault { get; set; }

        /// <summary>
        /// Asked before every outbox read by key; a task returned holds that read -- and whoever is making it --
        /// until it completes.
        /// </summary>
        public Func<string, Task?>? ReadOutgoingHold { get; set; }

        /// <summary>Asked before every outbox write; true fails that write.</summary>
        public Func<WireToGateDurableMessage, bool>? SaveOutgoingFault { get; set; }

        /// <summary>
        /// Asked after every outbox write; true throws once the row is on file -- the send breaking between the
        /// write and the wire.
        /// </summary>
        public Func<WireToGateDurableMessage, bool>? SaveOutgoingFaultAfterWrite { get; set; }

        public bool Fired => Volatile.Read(ref _fired) == 1;

        private void Fail(string what)
        {
            Volatile.Write(ref _fired, 1);
            throw new IOException($"injected journal failure: {what}");
        }

        /// <summary>
        /// Runs a picked write so that it fails: thrown here, or by SQLite against a file made read-only for the
        /// length of the call and writable again after it.
        /// </summary>
        private async Task<T> FailingWriteAsync<T>(string what, Func<Task<T>> write)
        {
            if (kind == JournalFault.Injected)
            {
                Fail(what);
            }

            string path = databasePath ?? throw new InvalidOperationException("A read-only fault needs the database path.");
            Volatile.Write(ref _fired, 1);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                return await write();
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            UpdateRecoveryStateAsync(change, static _ => { }, cancellationToken);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default) =>
            UpdateHold?.Invoke() is { } hold
                ? HeldUpdateAsync(hold, change, settled, cancellationToken)
                : UpdateOrFailAsync(change, settled, cancellationToken);

        private async Task<WireToGateRecoveryState?> HeldUpdateAsync(
            Task hold,
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken)
        {
            await hold.WaitAsync(cancellationToken);
            return await UpdateOrFailAsync(change, settled, cancellationToken);
        }

        private Task<WireToGateRecoveryState?> UpdateOrFailAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken) =>
            UpdateFault?.Invoke() == true
                ? FailingWriteAsync(
                    "recovery state write",
                    () => inner.UpdateRecoveryStateAsync(change, settled, cancellationToken))
                : inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

        public Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default)
        {
            if (SaveOutgoingFault?.Invoke(message) == true)
            {
                return FailingWriteAsync(
                    "outbox write",
                    () => inner.SaveOutgoingBeforeSendAsync(message, cancellationToken));
            }

            return SaveThenMaybeFailAsync(message, cancellationToken);
        }

        private async Task<WireToGateDurableMessage> SaveThenMaybeFailAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken)
        {
            WireToGateDurableMessage saved = await inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);
            if (SaveOutgoingFaultAfterWrite?.Invoke(message) == true)
            {
                Fail("after the outbox write, before the send");
            }

            return saved;
        }

        public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
            WireToGateDurableMessage expected,
            WireToGateDurableMessage replacement,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceOutgoingForReplayAsync(expected, replacement, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            if (ReadOutgoingFault?.Invoke(deduplicationKey) == true)
            {
                Fail("outbox read");
            }

            return ReadOutgoingHold?.Invoke(deduplicationKey) is { } hold
                ? HeldReadAsync(hold, deduplicationKey, cancellationToken)
                : inner.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);
        }

        private async Task<WireToGateDurableMessage?> HeldReadAsync(
            Task hold,
            string deduplicationKey,
            CancellationToken cancellationToken)
        {
            await hold.WaitAsync(cancellationToken);
            return await inner.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);
        }

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
