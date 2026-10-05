using SQCD.Agv.Core;
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
        // No way out exists on the vehicle or the server until control-server#483: the operator is told so, not sent
        // to look for an entry, and the log names where the way out is being built.
        Assert.Contains(
            harness.OperatorEvents,
            item => item.Kind == "OPERATION_RECOVERY_REQUIRED"
                && item.Message.Contains("车上目前没有可结束这次恢复的入口", StringComparison.Ordinal));
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Message.Contains("control-server#483", StringComparison.Ordinal));
    }
}
