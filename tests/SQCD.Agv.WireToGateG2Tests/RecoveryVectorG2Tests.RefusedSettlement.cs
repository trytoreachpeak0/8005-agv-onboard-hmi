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
}
