using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// The boundary of the overwrite at the next command (8005-agv-onboard-hmi#372): the load is still unsettled here, a
    /// compensation for it was pressed and its result refused for good (onboard-hmi#254), and then the server concludes the
    /// load on its own -- the withdrawal of trytoreachpeak0/8005-agv-control-server#771 -- answers READY and commands a new
    /// load. The vehicle refuses it with <c>ACTION_NOT_ALLOWED_IN_STATE</c> before the executor is reached, and keeps the
    /// load's attempt and the refused vector for the manual check.
    /// </summary>
    /// <remarks>
    /// <see cref="ANewSlotCommandWhileARefusedRecoveryAwaitsItsCheckIsRefused"/> pins the same refusal over a correction of
    /// a load that had settled; this one reaches READY the way the withdrawal does, over a load the vehicle still reports.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ACommandAfterTheServerSettlesTheLoadIsRefusedWhileARefusedRecoveryAwaitsItsCheck()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.RecoveryVectorSlotOperationAttemptId = AttemptId;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LoadCompensationResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            });
        await RunRefusedVectorAsync(harness, RefusedVector.LoadCompensation, token);
        WireToGateRecoveryVectorContext vector = await WaitForGivenUpResultAsync(harness, token);
        Assert.Equal(AttemptId, (await harness.ReadRecoveryStateAsync(token)).UnsettledSlotOperationAttemptId);

        harness.Server.SettleOperationOnServer(AttemptId);
        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        // The seeded load's result is still in the outbox, so the handshake reports it pending; READY follows once the
        // replayed result arrives and the server, which settled the operation, only acknowledges it.
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the server to answer READY once it settled the load",
            token);
        using (JsonDocument report = JsonDocument.Parse(harness.Server.ReceivedEnvelopes
                   .Last(envelope => envelope.MessageType == "RecoveryStateReport").WireLine))
        {
            // The vehicle still reports the load; READY comes from the server having settled it.
            Assert.Equal(
                AttemptId,
                report.RootElement.GetProperty("payload").GetProperty("unsettledSlotOperationAttemptId").GetString());
        }

        int unlocksBefore = harness.Io.UnlockCount;
        const string newAttemptId = "9e9e9e9e-9e9e-4e9e-8e9e-9e9e9e9e9e9e";
        await SendNewLoadCommandAsync(harness.Server, Guid.NewGuid().ToString("D"), newAttemptId);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Message.StartsWith("拒收SlotOperationCommand：上一次恢复的结果被服务端拒收", StringComparison.Ordinal)
                && entry.Message.Contains(newAttemptId, StringComparison.Ordinal))
                // This refusal is logged before it is sent (WireToGateBusinessService.ConflictedRecovery.cs).
                && harness.ResultsOfType("SlotOperationCommandRejected").Any(line =>
                    line.Contains(newAttemptId, StringComparison.Ordinal)),
            "the new command to be refused while the refused recovery awaits its check",
            token);

        string refusal = Assert.Single(
            harness.ResultsOfType("SlotOperationCommandRejected"),
            line => line.Contains(newAttemptId, StringComparison.Ordinal));
        using (JsonDocument line = JsonDocument.Parse(refusal))
        {
            Assert.Equal(
                "ACTION_NOT_ALLOWED_IN_STATE",
                line.RootElement.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        }

        Assert.Equal(unlocksBefore, harness.Io.UnlockCount);
        Assert.DoesNotContain(harness.Logger.Entries, entry =>
            entry.Message.StartsWith("新仓位操作替换了日志簿里上一次的操作记录", StringComparison.Ordinal));
        WireToGateRecoveryState onFile = await harness.ReadRecoveryStateAsync(token);
        AssertStillOnFile(vector, onFile);
        Assert.Equal(AttemptId, onFile.UnsettledSlotOperationAttemptId);
    }
}
