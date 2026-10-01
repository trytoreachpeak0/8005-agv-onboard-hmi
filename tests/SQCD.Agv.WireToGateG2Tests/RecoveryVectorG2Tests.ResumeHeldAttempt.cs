using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// Three branches of a resume that finds its attempt held, each one a mutation the first round of tests let
/// through (review of PR #234: O1, O2, O6) (8005-agv-onboard-hmi#233).
/// </summary>
/// <remarks>
/// The holds are made with <see cref="FaultInjectingJournal.UpdateHold"/>: a journal write of the holder's is
/// kept waiting until the test has put the resume where it needs it, so none of these depends on how fast the
/// vehicle runs.
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// The same resume, delivered again while the first copy has its door open, is left to the first copy: no
    /// rejection -- which the server would take as this command refused and close its session on, doors moving
    /// -- one pulse, and one answer, the first copy's result (review O2).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task TheSameResumeDeliveredWhileItExecutesIsLeftToTheFirstCopy()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource firstCopyHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FaultInjectingJournal? journal = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            lockerWaitTimesOut: true,
            wrapJournal: inner => journal = new FaultInjectingJournal(inner));
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
        // The first checkpoint the first copy writes after its pulse waits here, with the attempt claimed.
        int held = 0;
        journal!.UpdateHold = () =>
            harness.Io.UnlockCount > 0
            && Environment.StackTrace.Contains("ExecuteRemainingSlotsAsync", StringComparison.Ordinal)
            && Interlocked.Exchange(ref held, 1) == 0
                ? firstCopyHeld.Task
                : null;
        try
        {
            await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));
            await RecoveryVectorHarness.WaitUntilAsync(
                () => Volatile.Read(ref held) == 1,
                "the first copy to pulse and reach its next checkpoint",
                token);

            await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));
            await RecoveryVectorHarness.WaitUntilAsync(
                () => harness.Logger.Entries.Any(entry => entry.Message.StartsWith(
                    "收到正在执行的SlotOperationResumeCommand的重发", StringComparison.Ordinal)),
                "the second copy to find the attempt held by the first",
                token);
            Assert.Empty(Rejections(harness));
        }
        finally
        {
            firstCopyHeld.TrySetResult();
        }

        string resumeResultId = FakeControlServerIdentifiers.StableUuid(
            $"recovery-operation-result:{AttemptId}:{state.RecoveryActionId}");
        await WaitLongAsync(
            () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                envelope.MessageType == "OperationResult" && envelope.MessageId == resumeResultId),
            "the first copy to answer once it goes on",
            TimeSpan.FromSeconds(20),
            token);
        // Held over a stretch: a rejection or a second pulse arriving after the answer is the failure.
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(1))
        {
            Assert.Empty(Rejections(harness));
            Assert.Equal(1, harness.Io.UnlockCount);
            await Task.Delay(20, token);
        }

        Assert.Single(harness.Server.ReceivedEnvelopes, envelope =>
            envelope.MessageType == "OperationResult" && envelope.MessageId == resumeResultId);
    }

    /// <summary>
    /// What held while the resume waited need not hold when it wakes. The vehicle's motion goes unknown during
    /// the wait; the resume is judged again from the start and the service's own safety gate refuses it with
    /// <c>ACTION_NOT_ALLOWED_IN_STATE</c> -- not the executor, further in, and not a door (review O1).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeIsJudgedAgainAgainstWhatHoldsWhenTheSettlementLetsGo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource ackHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.OperationResultAckHold = ackHold.Task,
            cargoInTargetSlots: true,
            messageTimeout: TimeSpan.FromSeconds(30),
            awaitStartSettlement: false);
        WireToGateRecoveryState state;
        try
        {
            await RecoveryVectorHarness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                    envelope.MessageType == "OperationResult" && envelope.MessageId == AttemptId),
                "the start settlement to send its OperationResult and wait for the held ack",
                token);
            state = await OpenResumeActionAsync(harness, token);
            await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));
            await WaitForResumeToReachTheSettlementAsync(harness, token);
            Assert.Contains(
                harness.Logger.Entries,
                entry => entry.Message.StartsWith(ResumeWaitsForSettlementLog, StringComparison.Ordinal));
            harness.VehicleMotionUnknown();
        }
        finally
        {
            ackHold.TrySetResult();
        }

        JsonElement rejection = await WaitForSingleRejectionAsync(harness, token);
        Assert.Equal(ResumeMessageId, rejection.GetProperty("correlationId").GetString());
        Assert.Equal(
            "ACTION_NOT_ALLOWED_IN_STATE",
            rejection.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Message.StartsWith("收到SlotOperationResumeCommand但未执行物理动作", StringComparison.Ordinal));
        await AssertNoUnlockOverAsync(harness, TimeSpan.FromSeconds(1), token);
        string resumeResultId = FakeControlServerIdentifiers.StableUuid(
            $"recovery-operation-result:{AttemptId}:{state.RecoveryActionId}");
        Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.MessageType == "OperationResult" && envelope.MessageId == resumeResultId);
    }

    /// <summary>
    /// The wait runs out after the session the resume came on has been replaced, the settlement still holding the
    /// attempt. The resume is given up, not refused: a rejection sent into the new session on the strength of the
    /// old one's command would close a recovery session the server's replay is about to judge afresh (review O6).
    /// </summary>
    /// <remarks>
    /// The settlement is held at its pending-result write rather than in its send: a send fails the moment the
    /// link drops, which releases the attempt and takes the resume down the wake-up branch instead.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeWhoseWaitRunsOutAfterItsSessionEndedIsGivenUpNotRefused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource settlementHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FaultInjectingJournal? journal = null;
        int held = 0;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            messageTimeout: TimeSpan.FromSeconds(30),
            awaitStartSettlement: false,
            resumeSettlementWaitLimit: TimeSpan.FromSeconds(3),
            wrapJournal: inner =>
            {
                journal = new FaultInjectingJournal(inner)
                {
                    // The pending-result write, after the settlement has put RecoveryRequired on screen -- which
                    // the fixture's own start waits for -- and before it sends anything.
                    UpdateHold = () =>
                        Environment.StackTrace is var stack
                        && stack.Contains("TrySettleInterruptedOperationAsync", StringComparison.Ordinal)
                        && stack.Contains("RecordPendingResultAsync", StringComparison.Ordinal)
                        && Interlocked.Exchange(ref held, 1) == 0
                            ? settlementHeld.Task
                            : null
                };
                return journal;
            });
        try
        {
            await RecoveryVectorHarness.WaitUntilAsync(
                () => Volatile.Read(ref held) == 1,
                "the start settlement to reach its pending-result write and wait there",
                token);
            WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
            long? firstGeneration = harness.Session.Current.SessionGeneration;
            await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));
            await WaitForResumeToReachTheSettlementAsync(harness, token);
            Assert.Contains(
                harness.Logger.Entries,
                entry => entry.Message.StartsWith(ResumeWaitsForSettlementLog, StringComparison.Ordinal));

            harness.Server.CloseLatestConnection();
            WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);
            Assert.True(reconnected.SessionGeneration > firstGeneration);

            await WaitLongAsync(
                () => harness.Logger.Entries.Any(entry =>
                    entry.Message.StartsWith(ResumeAbandonedWithItsSessionLog, StringComparison.Ordinal)),
                "the wait to run out and the resume to be given up with its session",
                TimeSpan.FromSeconds(15),
                token);
            Assert.False(settlementHeld.Task.IsCompleted, "The settlement let go first: this run did not test the limit.");
            await AssertNoUnlockOverAsync(harness, TimeSpan.FromSeconds(1), token);
            Assert.Empty(Rejections(harness));
        }
        finally
        {
            settlementHeld.TrySetResult();
        }
    }
}
