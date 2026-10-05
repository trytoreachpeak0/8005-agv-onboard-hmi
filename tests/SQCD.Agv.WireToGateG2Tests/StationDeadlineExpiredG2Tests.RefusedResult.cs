using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// An <c>OperationResult</c> the control server refuses for good (onboard-hmi#254).
/// </summary>
public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>
    /// The result is given up and reported, and the attempt stays unsettled on this vehicle: the recovery entry's
    /// projection comes up -- before onboard-hmi#254 the refusal escaped the send and no entry came -- and the result is
    /// never sent again, not by the restore's resend, not by the next handshake, and not named pending in its report.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ARefusedResultIsGivenUpAndTheRecoveryEntryComesUp()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { LockerWaitTimesOut = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["OperationResult"] = "MESSAGE_ID_CONTENT_CONFLICT"
                };
            });

        await harness.WaitForEventAsync("DURABLE_MESSAGE_ABANDONED", token);
        await Harness.WaitUntilAsync(
            () => harness.DescribeEvents().Split(Environment.NewLine).Any(line =>
                line.StartsWith("OPERATION_RECOVERY_REQUIRED:", StringComparison.Ordinal)
                && line.Contains("装货操作未完成：1号仓，需要管理员恢复。", StringComparison.Ordinal)),
            "the recovery entry's projection after the refused result",
            token,
            harness.DescribeEvents);

        WireToGateDurableMessage row = await harness.Journal.ReadOutgoingByDeduplicationKeyAsync(
            $"operation-result:{AttemptId}", token) ?? throw new InvalidOperationException("No result on file.");
        Assert.Equal("MESSAGE_ID_CONTENT_CONFLICT", row.AbandonedReasonCode);
        Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, harness.Business.CurrentOperationSnapshot?.Stage);
        Assert.Equal(AttemptId, harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId);

        harness.Server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal);
        // The resend by key the restore uses for a result whose ack was lost (ResendOperationResultAsync) is refused here.
        InvalidDataException resend = await Assert.ThrowsAsync<InvalidDataException>(
            () => harness.Client.ResendOperationResultAsync($"operation-result:{AttemptId}", token));
        Assert.Equal("DURABLE_MESSAGE_ABANDONED", resend.Message);
        await harness.Client.DisconnectAsync();
        await harness.Client.ConnectAndRecoverAsync(token);

        Assert.Single(harness.Server.ReceivedEnvelopes, item => item.MessageType == "OperationResult");
        using JsonDocument report = JsonDocument.Parse(harness.Server.ReceivedEnvelopes
            .Last(item => item.MessageType == "RecoveryStateReport").WireLine);
        Assert.Equal(0, report.RootElement.GetProperty("payload").GetProperty("pendingResults").GetArrayLength());
    }

    /// <summary>
    /// An acknowledged result the next handshake names as pending and replays (<c>CV-OPERATION-RESULT-UNKNOWN-RECONCILE</c>)
    /// is refused for good: the handshake still completes, the result is given up and stays acknowledged, and later
    /// handshakes neither name it as pending nor replay it. With its store intact the server answers such a replay from
    /// the result it holds; this is a store that no longer holds what it acknowledged, such as a replaced one.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task AnAcknowledgedResultRefusedOnItsReplayIsGivenUpAndTheHandshakeCompletes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { LockerWaitTimesOut = true },
            token,
            server => server.StationDepartureDeadlineAt = null);
        await Harness.WaitUntilAsync(
            () => harness.Journal.ReadOutgoingByDeduplicationKeyAsync($"operation-result:{AttemptId}", token)
                .GetAwaiter().GetResult() is { Acknowledged: true },
            "the unknown result to be acknowledged",
            token,
            harness.DescribeEvents);

        harness.Server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OperationResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
        };
        await harness.Client.DisconnectAsync();
        Exception? handshake = await Record.ExceptionAsync(() => harness.Client.ConnectAndRecoverAsync(token));
        Assert.True(handshake is null, $"the handshake failed: {handshake?.GetType().Name}: {handshake?.Message}");

        WireToGateDurableMessage row = await harness.Journal.ReadOutgoingByDeduplicationKeyAsync(
            $"operation-result:{AttemptId}", token) ?? throw new InvalidOperationException("No result on file.");
        Assert.True(row.Acknowledged);
        Assert.Equal("BUSINESS_ID_CONTENT_CONFLICT", row.AbandonedReasonCode);
        int sent = harness.Server.ReceivedEnvelopes.Count(item => item.MessageType == "OperationResult");
        Assert.Equal(2, sent);

        harness.Server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal);
        await harness.Client.DisconnectAsync();
        await harness.Client.ConnectAndRecoverAsync(token);

        Assert.Equal(sent, harness.Server.ReceivedEnvelopes.Count(item => item.MessageType == "OperationResult"));
        using JsonDocument report = JsonDocument.Parse(harness.Server.ReceivedEnvelopes
            .Last(item => item.MessageType == "RecoveryStateReport").WireLine);
        Assert.Equal(0, report.RootElement.GetProperty("payload").GetProperty("pendingResults").GetArrayLength());
    }
}
