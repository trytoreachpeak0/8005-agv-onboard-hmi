using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The interrupted settlement's own <c>OperationResult</c> refused for good (onboard-hmi#254).
/// </summary>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// The result a previous process's attempt is settled with at start is refused with a <c>MANUAL_REVIEW</c> code: it is
    /// given up and reported, the attempt is not taken over, and the recovery entry's projection comes up -- the refusal
    /// no longer escapes the settlement.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task AnInterruptedSettlementWhoseResultIsRefusedIsNotTakenOver()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OperationResult"] = "MESSAGE_ID_CONTENT_CONFLICT"
            },
            awaitStartSettlement: false);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.OperatorEvents.Any(item => item.Kind == "DURABLE_MESSAGE_ABANDONED"),
            "the settlement's refused result to be given up and reported",
            token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry => entry.Message.StartsWith(
                "中断操作的结算结果被服务端拒收并已放弃，转由管理员恢复：", StringComparison.Ordinal)),
            "the settlement to hand the attempt over to recovery",
            token);

        Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, harness.Business.CurrentOperationSnapshot?.Stage);
        Assert.Equal(AttemptId, (await harness.ReadRecoveryStateAsync(token)).UnsettledSlotOperationAttemptId);
        Assert.Single(harness.ResultsOfType("OperationResult"));
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Message.StartsWith(
            "WIRE_TO_GATE后台任务异常", StringComparison.Ordinal));
    }

    /// <summary>
    /// The replacement result a resume reports is refused for good: the operator is told it was given up and that no
    /// acknowledgement is coming, not to wait for one (review of PR #258, S2), and it is not sent again.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task AResumeResultRefusedForGoodIsNotAwaitedAsAnAck()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            lockerWaitTimesOut: true);
        WireToGateRecoveryState state = await OpenResumeActionAsync(harness, token);
        string resultKey = $"recovery-operation-result:{AttemptId}:{state.RecoveryActionId}";
        string resumeResultId = FakeControlServerIdentifiers.StableUuid(resultKey);
        harness.Server.ProtocolProblemByMessageId = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // What the server answers a replacement result outside its resume authorization with: it keeps nothing.
            [resumeResultId] = "RECOVERY_SCOPE_MISMATCH"
        };

        await harness.Server.SendCommandAsync(
            "SlotOperationResumeCommand",
            ResumeMessageId,
            ResumePayload(state));

        await WaitLongAsync(
            () => harness.OperatorEvents.Any(item => item.Kind == "OPERATION_RECOVERY_REQUIRED"
                && item.Message.Contains("拒收并已放弃", StringComparison.Ordinal)),
            "the refused resume result to be reported as given up",
            TimeSpan.FromSeconds(20),
            token);

        Assert.DoesNotContain(harness.OperatorEvents, item => item.Kind == "RESULT_ACK_PENDING"
            && item.Message.Contains("恢复结果已持久化", StringComparison.Ordinal));
        Assert.Equal(
            "RECOVERY_SCOPE_MISMATCH",
            (await harness.ReadOutgoingAsync(resultKey, token))?.AbandonedReasonCode);
        // Worded for what the server did -- kept nothing -- not as "it kept another copy" (review of PR #258, S1).
        WireToGateOperatorEvent reported = Assert.Single(
            harness.OperatorEvents, item => item.Kind == "DURABLE_MESSAGE_ABANDONED");
        Assert.Contains("服务端没有收下这条结果", reported.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("已收下另一份内容", reported.Message, StringComparison.Ordinal);
        Assert.Single(harness.Server.ReceivedEnvelopes, envelope => envelope.MessageId == resumeResultId);
        // The vehicle answers the resume there and then, so the operator is told the recovery is being ended, and the
        // log names control-server#483 for a vehicle that never comes back.
        Assert.Contains(
            harness.OperatorEvents,
            item => item.Kind == "OPERATION_RECOVERY_REQUIRED"
                && item.Message.Contains("已通知服务端结束这次恢复", StringComparison.Ordinal));
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Message.Contains("control-server#483", StringComparison.Ordinal));
    }

    /// <summary>
    /// A resume that completes and whose replacement result is refused for good: the attempt is not recorded as done
    /// and the operator is not told the result was reported (review of PR #258, R2d). The test above runs the UNKNOWN
    /// path, which records nothing either way; only a COMPLETED result is recorded after the send.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ACompletedResumeResultRefusedForGoodLeavesTheAttemptUnsettled()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        WireToGateRecoveryState state = await OpenResumeOverFinishedSlotsAsync(harness, token);
        string resultKey = $"recovery-operation-result:{AttemptId}:{state.RecoveryActionId}";
        harness.Server.ProtocolProblemByMessageId = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FakeControlServerIdentifiers.StableUuid(resultKey)] = "RECOVERY_SCOPE_MISMATCH"
        };

        await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));
        await WaitLongAsync(
            () => harness.OperatorEvents.Any(item => item.Kind == "OPERATION_RECOVERY_REQUIRED"
                && item.Message.Contains("拒收并已放弃", StringComparison.Ordinal)),
            "the refused resume result to be reported as given up",
            TimeSpan.FromSeconds(20),
            token);

        WireToGateDurableMessage? sent = await harness.ReadOutgoingAsync(resultKey, token);
        Assert.NotNull(sent);
        Assert.Contains("\"overallOutcome\":\"COMPLETED\"", sent.WireLine, StringComparison.Ordinal);
        Assert.Equal("RECOVERY_SCOPE_MISMATCH", sent.AbandonedReasonCode);
        Assert.Equal(AttemptId, (await harness.ReadRecoveryStateAsync(token)).UnsettledSlotOperationAttemptId);
        Assert.DoesNotContain(
            harness.OperatorEvents,
            item => item.Message.Contains("恢复后的原操作结果已上报", StringComparison.Ordinal));
    }

    /// <summary>
    /// The way out of a resume whose result was given up (review of PR #258): the server's session waits for that result
    /// forever and refuses any other action or session, so the vehicle answers the resume there and then -- refused,
    /// correlated to the command, which the server closes the session on (control-server ObserveCommandRejectedAsync,
    /// #187). No reconnect is needed: since control-server#479 a refusal keeps the connection, and the server sends the
    /// command again only in a handshake (review S-1). When it does, the rejection on file is sent again, the same one.
    /// The vehicle forgets the session, keeps the operation unsettled, opens no door, and the operator can ask for a new
    /// recovery. Both codes that leave the server with nothing on file (review P).
    /// </summary>
    [Theory]
    [InlineData("RECOVERY_SCOPE_MISMATCH")]
    [InlineData("BUSINESS_ID_CONTENT_CONFLICT")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task AResumeWhoseResultWasGivenUpIsRefusedThereAndThen(string reasonCode)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        WireToGateRecoveryState state = await OpenResumeOverFinishedSlotsAsync(harness, token);
        string resultKey = $"recovery-operation-result:{AttemptId}:{state.RecoveryActionId}";
        string resumeResultId = FakeControlServerIdentifiers.StableUuid(resultKey);
        harness.Server.ProtocolProblemByMessageId = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [resumeResultId] = reasonCode
        };
        long? generation = harness.Session.Current.SessionGeneration;
        await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));

        JsonElement rejection = await WaitForSingleRejectionAsync(harness, token);
        Assert.Equal(generation, harness.Session.Current.SessionGeneration);
        Assert.Equal(ResumeMessageId, rejection.GetProperty("correlationId").GetString());
        JsonElement payload = rejection.GetProperty("payload");
        Assert.Equal(AttemptId, payload.GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal(
            "SLOT_OPERATION_CONFLICT",
            payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(reasonCode, (await harness.ReadOutgoingAsync(resultKey, token))?.AbandonedReasonCode);
        Assert.Contains(
            harness.OperatorEvents,
            item => item.Kind == "OPERATION_RECOVERY_REQUIRED"
                && item.Message.Contains("已通知服务端结束这次恢复", StringComparison.Ordinal));
        await AssertNoUnlockOverAsync(harness, TimeSpan.FromSeconds(1), token);

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(after.ExceptionRecoverySessionId);
        Assert.Null(after.RecoveryActionId);
        Assert.Equal(AttemptId, after.UnsettledSlotOperationAttemptId);

        // The server sends the command again in the next handshake: answered from the rejection on file, not run, and
        // the given-up result is not sent again either. That rejection was acknowledged, so the answer puts nothing new
        // on the wire (onboard-hmi#119): one rejection, as before.
        harness.Server.CloseLatestConnection();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Message.StartsWith("收到已拒绝过的SlotOperationResumeCommand", StringComparison.Ordinal)),
            "the resent resume to be answered from the rejection on file",
            token);
        Assert.Single(Rejections(harness));
        await AssertNoUnlockOverAsync(harness, TimeSpan.FromSeconds(1), token);
        Assert.Single(harness.Server.ReceivedEnvelopes, envelope => envelope.MessageId == resumeResultId);

        // A new recovery can be asked for: the vehicle no longer holds the closed session's action.
        WireToGateRecoveryState reopened = await OpenResumeActionAsync(harness, token);
        Assert.NotEqual(state.RecoveryActionId, reopened.RecoveryActionId);
    }

    /// <summary>
    /// The rejection sent when the result was given up never reached the outbox, so the server's next send of the command
    /// is what gets answered -- ahead of the safety gate, whichever checkpoint the command was sent at (review of PR #258,
    /// S-3). Behind the gate it was refused there instead: the session is already forgotten, and a resume sent at
    /// ACTIVE_UNLOCK_SET no longer matches the SAFE_FINISH_REACHED its run left on file.
    /// </summary>
    [Theory]
    [InlineData(WireToGateRecoveryCheckpoint.SafeFinishReached)]
    [InlineData(WireToGateRecoveryCheckpoint.ActiveUnlockSet)]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task AResumeWhoseResultWasGivenUpAndWhoseRejectionWasNotWrittenIsRefusedAheadOfTheGate(
        WireToGateRecoveryCheckpoint sentAt)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        int failed = 0;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true,
            wrapJournal: inner => new FaultInjectingJournal(inner)
            {
                SaveOutgoingFault = message => message.MessageType == "SlotOperationCommandRejected"
                    && Interlocked.Exchange(ref failed, 1) == 0
            });
        WireToGateRecoveryState state = await OpenResumeOverFinishedSlotsAsync(harness, token, sentAt);
        Assert.Equal(sentAt, state.ProvenRecoveryCheckpoint);
        string resultKey = $"recovery-operation-result:{AttemptId}:{state.RecoveryActionId}";
        harness.Server.ProtocolProblemByMessageId = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FakeControlServerIdentifiers.StableUuid(resultKey)] = "RECOVERY_SCOPE_MISMATCH"
        };
        await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Message.StartsWith("续行命令的拒绝未能写入发件箱", StringComparison.Ordinal)),
            "the rejection sent on giving the result up to fail its outbox write",
            token);
        Assert.Empty(Rejections(harness));

        await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));

        JsonElement rejection = await WaitForSingleRejectionAsync(harness, token);
        Assert.Equal(ResumeMessageId, rejection.GetProperty("correlationId").GetString());
        Assert.Equal(
            "SLOT_OPERATION_CONFLICT",
            rejection.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        // The operator is told what this command met (review K), not the gate's text.
        Assert.Contains(
            harness.OperatorEvents,
            item => item.Kind == "RECOVERY_BLOCKED"
                && item.Message.Contains("恢复结果已被服务端拒收并放弃", StringComparison.Ordinal));
        Assert.DoesNotContain(
            harness.OperatorEvents,
            item => item.Message.StartsWith("恢复命令被安全门禁阻断", StringComparison.Ordinal));
        await AssertNoUnlockOverAsync(harness, TimeSpan.FromSeconds(1), token);
    }

    /// <summary>
    /// A resume result only waiting for its ack is not one given up: the server's resend of the command is answered by
    /// replaying that result, never refused (review of PR #258, S-2). Refused, the server would judge the resume
    /// RecoveryRequired and close the session, and the real result, arriving after, would be refused as a content
    /// conflict -- a completed load lost to the server's books.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeResultAwaitingItsAckIsReplayedNotRefusedWhenTheCommandComesAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            // One for the start settlement's own result, two for the resume result's send and its resend.
            server => server.OperationResultAcksToDrop = 3,
            cargoInTargetSlots: true);
        WireToGateRecoveryState state = await OpenResumeOverFinishedSlotsAsync(harness, token);
        await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));
        await WaitLongAsync(
            () => harness.OperatorEvents.Any(item => item.Kind == "RESULT_ACK_PENDING"
                && item.Message.StartsWith("恢复结果已持久化", StringComparison.Ordinal)),
            "the resume result to be on file and waiting for its ack",
            TimeSpan.FromSeconds(20),
            token);

        await harness.Server.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.OperatorEvents.Any(item => item.Kind == "OPERATION_REPLAY"
                && item.Message.Contains("保持原恢复结果重放", StringComparison.Ordinal)),
            "the resent resume to be answered by the result on file",
            token);
        Assert.Empty(Rejections(harness));
        Assert.Null((await harness.ReadOutgoingAsync(
            $"recovery-operation-result:{AttemptId}:{state.RecoveryActionId}", token))?.AbandonedReasonCode);
    }

    /// <summary>
    /// Opens a resume over slots this attempt already brought to their final state -- loaded, locked, recorded done --
    /// so the resume touches no door and its replacement result is <c>COMPLETED</c>. The G2 IO stub runs no slot
    /// operation, so this is the way to reach a completed resume here.
    /// </summary>
    private static async Task<WireToGateRecoveryState> OpenResumeOverFinishedSlotsAsync(
        RecoveryVectorHarness harness,
        CancellationToken token,
        WireToGateRecoveryCheckpoint checkpoint = WireToGateRecoveryCheckpoint.SafeFinishReached)
    {
        await OpenResumeActionAsync(harness, token);
        await harness.RewriteRecoveryStateAsync(
            persisted => persisted with
            {
                ProvenRecoveryCheckpoint = checkpoint,
                ActiveUnlockSlots = [],
                CompletedSlots = [1, 2],
                SlotResults =
                [
                    new WireToGateSlotExecutionResult(1, "COMPLETED", "OCCUPIED", "LOCKED", "RESET", []),
                    new WireToGateSlotExecutionResult(2, "COMPLETED", "OCCUPIED", "LOCKED", "RESET", [])
                ]
            },
            token);
        return await harness.ReadRecoveryStateAsync(token);
    }
}
