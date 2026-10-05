using System.Text.Json;
using SQCD.Agv.Contracts;
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
        Assert.Contains("服务端在同一 messageId 下已收下另一份内容", harness.DescribeEvents(), StringComparison.Ordinal);
        Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, harness.Business.CurrentOperationSnapshot?.Stage);
        Assert.Equal(AttemptId, harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId);

        // The server's outbox sends the command again until it has a result it takes: the vehicle neither runs it again
        // nor claims to replay a result it has given up (review of PR #258, N3).
        await harness.Server.ResendSlotOperationCommandAsync();
        await Harness.WaitUntilAsync(
            () => harness.DescribeEvents().Split(Environment.NewLine).Any(line =>
                line.StartsWith("OPERATION_REPLAY:", StringComparison.Ordinal)
                && line.Contains("不会重放", StringComparison.Ordinal)),
            "the repeated command to be answered as a result given up, not replayed",
            token,
            harness.DescribeEvents);
        Assert.DoesNotContain("已保持原结果重放", harness.DescribeEvents(), StringComparison.Ordinal);

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
    /// A <c>COMPLETED</c> result refused for good is no finished work waiting for its ack: the server holds another
    /// conclusion for the attempt. It comes up as an operation for the administrator to recover, not as
    /// <c>RESULT_ACK_PENDING</c> for good.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ARefusedCompletedResultComesUpForRecoveryNotAsAwaitingItsAck()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { SimulateOperatorLoad = true },
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
        Assert.Equal("COMPLETED", harness.FirstResult("OperationResult").GetProperty("overallOutcome").GetString());
        await Harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.RecoveryRequired,
            "the refused completed result to come up for recovery",
            token,
            harness.DescribeEvents);

        Assert.DoesNotContain("RESULT_ACK_PENDING", harness.DescribeEvents(), StringComparison.Ordinal);
        Assert.Equal(AttemptId, harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId);
        Assert.Single(harness.Server.ReceivedEnvelopes, item => item.MessageType == "OperationResult");
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
        Assert.Contains("服务端对同一业务号已有另一份记录", harness.DescribeEvents(), StringComparison.Ordinal);
        int sent = harness.Server.ReceivedEnvelopes.Count(item => item.MessageType == "OperationResult");
        Assert.Equal(2, sent);

        harness.Server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal);
        await harness.Client.DisconnectAsync();
        await harness.Client.ConnectAndRecoverAsync(token);

        Assert.Equal(sent, harness.Server.ReceivedEnvelopes.Count(item => item.MessageType == "OperationResult"));
        using JsonDocument report = JsonDocument.Parse(harness.Server.ReceivedEnvelopes
            .Last(item => item.MessageType == "RecoveryStateReport").WireLine);
        Assert.Equal(0, report.RootElement.GetProperty("payload").GetProperty("pendingResults").GetArrayLength());

        // Sent again by its key, the row is refused here, not answered as done (review of PR #258, R9): what made it
        // "done" was the old server's ack, and the server now refuses that content. Before onboard-hmi#254 an
        // acknowledged row returned at once.
        string agvId = JsonDocument.Parse(row.WireLine).RootElement.GetProperty("agvId").GetString()!;
        WireToGateOperationResultPayload payload = WireToGateProtocolSerializer
            .DeserializePayload<WireToGateOperationResultPayload>(
                WireToGateProtocolSerializer.DeserializeAndValidate(row.WireLine.TrimEnd('\r', '\n'), agvId));
        InvalidDataException connected = await Assert.ThrowsAsync<InvalidDataException>(() =>
            harness.Client.SendRecoveryOperationResultAsync($"operation-result:{AttemptId}", row.MessageId, payload, token));
        Assert.Equal("DURABLE_MESSAGE_ABANDONED", connected.Message);
        // And with no session to send on, it is refused the same way, not taken as "on file, the next handshake sends
        // it" (WIRE_TO_GATE_NOT_READY), which a row given up never is (review of PR #258, R8).
        await harness.Client.DisconnectAsync();
        InvalidDataException offline = await Assert.ThrowsAsync<InvalidDataException>(() =>
            harness.Client.SendRecoveryOperationResultAsync($"operation-result:{AttemptId}", row.MessageId, payload, token));
        Assert.Equal("DURABLE_MESSAGE_ABANDONED", offline.Message);
        Assert.Equal(sent, harness.Server.ReceivedEnvelopes.Count(item => item.MessageType == "OperationResult"));
    }
}
