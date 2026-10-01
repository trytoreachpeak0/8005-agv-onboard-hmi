using System.Diagnostics;
using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A <c>SlotOperationResumeCommand</c> that arrives while the interrupted settlement holds its attempt is
/// answered: executed once the settlement lets go, or refused (8005-agv-onboard-hmi#233).
/// </summary>
/// <remarks>
/// <para>
/// Until then the resume found the attempt claimed and returned without a word: no execution, no
/// <c>SlotOperationCommandRejected</c>, no <c>RECOVERY_BLOCKED</c>. The server's resume workflow waits for
/// one of those and has no timeout, so its session stayed <c>EXECUTING</c> and the vehicle could open no
/// other.
/// </para>
/// <para>
/// The settlement holds the claim across two awaits that can be long: the send of its own
/// <c>OperationResult</c> (start, and any later readiness that finds no result on file), and the resend of
/// an unacknowledged FAILED/UNKNOWN result on every later readiness. Both are opened here the way the
/// field opens them, with the result's <c>DurableAck</c> late: <see cref="FakeControlServer.OperationResultAckHold"/>
/// keeps the settlement in its send for as long as the test needs, and is let go once the vehicle says
/// the resume is waiting for it -- or after a bound, so that a vehicle which drops the resume reaches the
/// assertion that says so instead of hanging.
/// </para>
/// <para>
/// The answer is asserted, not "no unlock": a resume dropped after opening a door, or dropped before
/// one, is the same failure to the server.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// What the vehicle logs when a resume finds the attempt held by the settlement and waits for it.
    /// </summary>
    private const string ResumeWaitsForSettlementLog = "续行命令到达时attempt正被中断结算占用";

    /// <summary>What the vehicle logs when a waiting resume finds its session gone and leaves it to the replay.</summary>
    private const string ResumeAbandonedWithItsSessionLog = "续行命令等待中断结算期间会话已断开或换代";

    /// <summary>
    /// How long a test lets the vehicle take to say the resume is waiting before it lets the settlement go
    /// anyway. Generous: with the fix the line comes within milliseconds, and without it it never comes.
    /// </summary>
    private static readonly TimeSpan ResumeReachesSettlementBound = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The start window: the vehicle restarts over an armed attempt and settles it, and the operator's
    /// resume reaches the vehicle while the settlement's own <c>OperationResult</c> waits for its ack.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeArrivingWhileTheStartSettlementHoldsTheAttemptIsAnswered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource ackHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.OperationResultAckHold = ackHold.Task,
            lockerWaitTimesOut: true,
            messageTimeout: TimeSpan.FromSeconds(30),
            awaitStartSettlement: false);
        WireToGateRecoveryState state;
        try
        {
            // The settlement's result is with the server and its ack is held: the settlement is in its send
            // and holds the attempt for as long as the hold lasts.
            await RecoveryVectorHarness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                    envelope.MessageType == "OperationResult" && envelope.MessageId == AttemptId),
                "the start settlement to send its OperationResult and wait for the held ack",
                token);
            state = await OpenResumeActionAsync(harness, token);

            await harness.Server.SendCommandAsync(
                "SlotOperationResumeCommand",
                ResumeMessageId,
                ResumePayload(state));
            await WaitForResumeToReachTheSettlementAsync(harness, token);
            Assert.False(
                ackHold.Task.IsCompleted,
                "The settlement let go before the resume reached it: this run did not test the window.");
        }
        finally
        {
            ackHold.TrySetResult();
        }

        Stopwatch sinceAckReleased = Stopwatch.StartNew();
        await AssertResumeRanAfterTheSettlementAsync(harness, state.RecoveryActionId!, token);

        // The waiting resume must not keep the receive loop: the settlement waits on exactly that loop for its
        // DurableAck. Had the wait held it, the ack could not get in, the settlement would give up only when its
        // 30-second send timed out, and the resume would run then -- the same end as refusing outright, later.
        // So: the settlement's own result was acknowledged, it never logged the ack as missing, and the resume
        // ran well inside that timeout.
        Assert.True(
            await harness.IsOutgoingAcknowledgedAsync(AttemptId, token),
            "The settlement's result was never acknowledged: its DurableAck did not get in while the resume waited.");
        Assert.DoesNotContain(
            harness.Logger.Entries,
            entry => entry.Message.StartsWith("中断操作的结算结果暂未收到DurableAck", StringComparison.Ordinal));
        Assert.True(
            sinceAckReleased.Elapsed < TimeSpan.FromSeconds(10),
            $"The resume ran {sinceAckReleased.Elapsed.TotalSeconds:0.0}s after the ack was released, against a "
            + "30s message timeout: it ran on the settlement's timeout, not on its ack.");
    }

    /// <summary>
    /// The link drops while the resume waits on the settlement. The settlement's send fails, its claim is
    /// released, and the waiting resume wakes -- and gives up, rather than running, or being refused, in the
    /// session that follows on the strength of a command from the one that ended. Nothing opens. The server
    /// replays a recovery command still without a result once the new session reports its recovery state,
    /// under the same messageId; that copy, sent here by hand, is judged afresh and answered.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeWaitingOnTheSettlementWhenTheLinkDropsIsLeftToTheReplay()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource ackHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.OperationResultAckHold = ackHold.Task,
            lockerWaitTimesOut: true,
            messageTimeout: TimeSpan.FromSeconds(30),
            awaitStartSettlement: false);
        WireToGateRecoveryState state;
        long? firstGeneration;
        try
        {
            await RecoveryVectorHarness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                    envelope.MessageType == "OperationResult" && envelope.MessageId == AttemptId),
                "the start settlement to send its OperationResult and wait for the held ack",
                token);
            state = await OpenResumeActionAsync(harness, token);
            firstGeneration = harness.Session.Current.SessionGeneration;

            await harness.Server.SendCommandAsync(
                "SlotOperationResumeCommand",
                ResumeMessageId,
                ResumePayload(state));
            await WaitForResumeToReachTheSettlementAsync(harness, token);
            Assert.Contains(
                harness.Logger.Entries,
                entry => entry.Message.StartsWith(ResumeWaitsForSettlementLog, StringComparison.Ordinal));

            harness.Server.CloseLatestConnection();
            await WaitLongAsync(
                () => harness.Logger.Entries.Any(entry =>
                    entry.Message.StartsWith(ResumeAbandonedWithItsSessionLog, StringComparison.Ordinal)),
                "the waiting resume to wake when the link drops and give up with its session",
                TimeSpan.FromSeconds(10),
                token);
        }
        finally
        {
            ackHold.TrySetResult();
        }

        string resumeResultId = FakeControlServerIdentifiers.StableUuid(
            $"recovery-operation-result:{AttemptId}:{state.RecoveryActionId}");
        await AssertNoUnlockOverAsync(harness, TimeSpan.FromSeconds(1), token);
        Assert.Empty(Rejections(harness));
        Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.MessageType == "OperationResult" && envelope.MessageId == resumeResultId);

        // The new session -- this harness runs no reconnect loop, so the reconnect the session service makes in
        // the field is made here -- and the server's replay of the command into it.
        WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);
        Assert.True(reconnected.SessionGeneration > firstGeneration);
        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(state));
        await WaitLongAsync(
            () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                    envelope.MessageType == "OperationResult" && envelope.MessageId == resumeResultId)
                || Rejections(harness).Any(item =>
                    item.GetProperty("correlationId").GetString() == ResumeMessageId),
            "an answer to the replayed resume in the new session",
            TimeSpan.FromSeconds(20),
            token);
    }

    /// <summary>
    /// The vehicle stops while the resume waits on the settlement: the wait ends with the stop instead of
    /// hanging the shutdown, and nothing opens.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeWaitingOnTheSettlementEndsWithTheStop()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource ackHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.OperationResultAckHold = ackHold.Task,
            messageTimeout: TimeSpan.FromSeconds(30),
            awaitStartSettlement: false);
        try
        {
            await RecoveryVectorHarness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                    envelope.MessageType == "OperationResult" && envelope.MessageId == AttemptId),
                "the start settlement to send its OperationResult and wait for the held ack",
                token);
            WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
            await harness.Server.SendCommandAsync(
                "SlotOperationResumeCommand",
                ResumeMessageId,
                ResumePayload(state));
            await WaitForResumeToReachTheSettlementAsync(harness, token);
            Assert.Contains(
                harness.Logger.Entries,
                entry => entry.Message.StartsWith(ResumeWaitsForSettlementLog, StringComparison.Ordinal));

            // The hold is still on: neither the settlement nor the resume can end any way but by the stop.
            Stopwatch stopping = Stopwatch.StartNew();
            await harness.Business.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), token);
            Assert.True(
                stopping.Elapsed < TimeSpan.FromSeconds(15),
                "The business service did not stop while a resume waited on the settlement.");
            Assert.Equal(0, harness.Io.UnlockCount);
            Assert.Empty(Rejections(harness));
        }
        finally
        {
            ackHold.TrySetResult();
        }
    }

    /// <summary>
    /// The readiness window: the settlement's result is on file and unacknowledged, so every later
    /// <c>SessionReadiness</c> re-enters the settlement, which resends the result and waits for its ack
    /// while holding the attempt. The resume reaches the vehicle in that wait.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeArrivingWhileAReadinessReEntryHoldsTheAttemptIsAnswered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource ackHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.OperationResultAcksToDrop = 1,
            lockerWaitTimesOut: true,
            messageTimeout: TimeSpan.FromSeconds(5),
            awaitStartSettlement: false);

        // The start settlement's ack is lost: the result stays on file unacknowledged, which is what every
        // later readiness resends.
        await WaitLongAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Message.StartsWith("中断操作的结算结果暂未收到DurableAck", StringComparison.Ordinal)),
            "the start settlement to give up on its dropped ack",
            TimeSpan.FromSeconds(30),
            token);
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
        int resultsBefore = harness.Server.ReceivedEnvelopes.Count(envelope =>
            envelope.MessageType == "OperationResult" && envelope.MessageId == AttemptId);

        harness.Server.OperationResultAckHold = ackHold.Task;
        try
        {
            // A readiness after a requested safety snapshot, as the server sends one after every safety
            // change: the re-entry resends the unacknowledged result and waits on the held ack.
            await harness.Server.RequestSafetyStateSnapshotAsync();
            await RecoveryVectorHarness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Count(envelope =>
                    envelope.MessageType == "OperationResult" && envelope.MessageId == AttemptId) > resultsBefore,
                "a readiness re-entry to resend the unacknowledged settlement result and wait for the held ack",
                token);

            await harness.Server.SendCommandAsync(
                "SlotOperationResumeCommand",
                ResumeMessageId,
                ResumePayload(state));
            await WaitForResumeToReachTheSettlementAsync(harness, token);
            Assert.False(
                ackHold.Task.IsCompleted,
                "The re-entry let go before the resume reached it: this run did not test the window.");
        }
        finally
        {
            ackHold.TrySetResult();
        }

        await AssertResumeRanAfterTheSettlementAsync(harness, state.RecoveryActionId!, token);
    }

    /// <summary>
    /// A settlement that holds the attempt past the vehicle's limit gets the resume refused, not dropped:
    /// <c>SlotOperationCommandRejected</c> with <c>SLOT_OPERATION_CONFLICT</c>, correlated to the resume, and
    /// <c>RECOVERY_BLOCKED</c> to the operator. Nothing opens -- not while the settlement still holds the
    /// attempt, and not once it lets go, when a resume that had been refused must not run after all.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeTheSettlementHoldsPastTheLimitIsRefusedAndNeverRun()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource ackHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.OperationResultAckHold = ackHold.Task,
            messageTimeout: TimeSpan.FromSeconds(30),
            awaitStartSettlement: false,
            resumeSettlementWaitLimit: TimeSpan.FromSeconds(1));
        WireToGateRecoveryState state;
        try
        {
            await RecoveryVectorHarness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                    envelope.MessageType == "OperationResult" && envelope.MessageId == AttemptId),
                "the start settlement to send its OperationResult and wait for the held ack",
                token);
            state = await OpenResumeActionAsync(harness, token);

            await harness.Server.SendCommandAsync(
                "SlotOperationResumeCommand",
                ResumeMessageId,
                ResumePayload(state));

            await harness.WaitForRecoveryBlockedAsync("SLOT_OPERATION_CONFLICT", token);
            JsonElement rejection = await WaitForSingleRejectionAsync(harness, token);
            Assert.Equal(ResumeMessageId, rejection.GetProperty("correlationId").GetString());
            JsonElement payload = rejection.GetProperty("payload");
            Assert.Equal(AttemptId, payload.GetProperty("slotOperationAttemptId").GetString());
            Assert.Equal(
                "SLOT_OPERATION_CONFLICT",
                payload.GetProperty("problem").GetProperty("reasonCode").GetString());
            Assert.True(
                harness.Logger.Entries.Any(entry =>
                    entry.Message.StartsWith(ResumeWaitsForSettlementLog, StringComparison.Ordinal)),
                "The resume was refused without having waited for the settlement.");
            Assert.False(ackHold.Task.IsCompleted);
            await AssertNoUnlockOverAsync(harness, TimeSpan.FromMilliseconds(500), token);
        }
        finally
        {
            ackHold.TrySetResult();
        }

        // The settlement concludes now. A refused resume that then ran anyway would pulse slot 1 and send
        // its result; read over a stretch after the settlement's conclusion, not once.
        await WaitLongAsync(
            () => harness.IsOutgoingAcknowledgedAsync(AttemptId, token).GetAwaiter().GetResult(),
            "the start settlement's result to be acknowledged once the hold is released",
            TimeSpan.FromSeconds(10),
            token);
        await AssertNoUnlockOverAsync(harness, TimeSpan.FromSeconds(2), token);
        string resumeResultId = FakeControlServerIdentifiers.StableUuid(
            $"recovery-operation-result:{AttemptId}:{state.RecoveryActionId}");
        Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.MessageType == "OperationResult" && envelope.MessageId == resumeResultId);
        Assert.Single(Rejections(harness));
    }

    /// <summary>
    /// Waits, at most <see cref="ResumeReachesSettlementBound"/>, for the vehicle to say the resume is
    /// waiting for the settlement. Not an assertion: a vehicle that drops the resume never says it, and the
    /// test goes on to the assertion that names that failure.
    /// </summary>
    private static async Task WaitForResumeToReachTheSettlementAsync(
        RecoveryVectorHarness harness,
        CancellationToken token)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < ResumeReachesSettlementBound
            && !harness.Logger.Entries.Any(entry =>
                entry.Message.StartsWith(ResumeWaitsForSettlementLog, StringComparison.Ordinal)))
        {
            await Task.Delay(20, token);
        }
    }

    /// <summary>
    /// The resume waited for the settlement and then ran: the server gets the resume's replacement
    /// <c>OperationResult</c>, after the settlement's own result, and no rejection.
    /// </summary>
    /// <remarks>
    /// The wait is for either answer, so that a vehicle which drops the resume fails here with that named --
    /// and so does one that opens a door and then drops it (the review's M8): the door is not the answer.
    /// The resume's result is then required specifically: a settlement that holds the attempt is waited for,
    /// not refused (onboard-hmi#233, decided with the coordinator).
    /// </remarks>
    private static async Task AssertResumeRanAfterTheSettlementAsync(
        RecoveryVectorHarness harness,
        string recoveryActionId,
        CancellationToken token)
    {
        string resumeResultId = FakeControlServerIdentifiers.StableUuid(
            $"recovery-operation-result:{AttemptId}:{recoveryActionId}");
        await WaitLongAsync(
            () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                    envelope.MessageType == "OperationResult" && envelope.MessageId == resumeResultId)
                || Rejections(harness).Any(item =>
                    item.GetProperty("correlationId").GetString() == ResumeMessageId),
            "an answer to the resume -- its OperationResult or a rejection correlated to it. "
            + "Neither came: the vehicle dropped the resume while the settlement held the attempt",
            TimeSpan.FromSeconds(20),
            token);

        Assert.True(
            harness.Logger.Entries.Any(entry =>
                entry.Message.StartsWith(ResumeWaitsForSettlementLog, StringComparison.Ordinal)),
            "The resume was answered, but never found the attempt held by the settlement: "
            + "this run did not test the window.");
        Assert.Empty(Rejections(harness));
        var received = harness.Server.ReceivedEnvelopes
            .Select((envelope, index) => (envelope.MessageType, envelope.MessageId, Index: index))
            .ToList();
        int resumeResult = received.Single(item =>
            item.MessageType == "OperationResult" && item.MessageId == resumeResultId).Index;
        int lastSettlementResult = received.Last(item =>
            item.MessageType == "OperationResult" && item.MessageId == AttemptId).Index;
        Assert.True(
            lastSettlementResult < resumeResult,
            "The resume's result reached the server before the settlement's: it ran inside the settlement.");
    }

    /// <summary>
    /// Asserts the unlock count stays at zero for the whole of <paramref name="span"/>: a single read
    /// passes an unlock that lands a moment after it (onboard-hmi#233, the review's R1b).
    /// </summary>
    private static async Task AssertNoUnlockOverAsync(
        RecoveryVectorHarness harness,
        TimeSpan span,
        CancellationToken token)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        do
        {
            Assert.Equal(0, harness.Io.UnlockCount);
            await Task.Delay(20, token);
        }
        while (elapsed.Elapsed < span);

        Assert.Equal(0, harness.Io.UnlockCount);
    }

    private static async Task WaitLongAsync(
        Func<bool> predicate,
        string expectation,
        TimeSpan timeout,
        CancellationToken token)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            if (elapsed.Elapsed > timeout)
            {
                Assert.Fail($"Timed out after {timeout.TotalSeconds:0}s waiting for: {expectation}");
            }

            await Task.Delay(20, token);
        }
    }
}
