using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A <c>DurableAck</c> that reaches the vehicle after the send it answers stopped waiting for it
/// (8005-agv-onboard-hmi#250): the row is acknowledged on file and the session goes on, instead of the receive loop
/// taking it for an unhandled message and ending the session.
/// </summary>
/// <remarks>
/// <para>
/// Two ways a send stops waiting while its ack is still on the way, one test each: its wait runs out
/// (<c>MessageTimeout</c>, 2 s in this harness), or the run that sent it is aborted under it -- an authorized load
/// cancellation, and on the batch-p3 line a slot fault declaration, cancel the executor's token, and that token is the
/// one the in-flight <c>OperationProgress</c> is waiting with.
/// </para>
/// <para>
/// This harness connects once and runs no reconnect loop, so a session ended here stays ended: before the fix the
/// late ack left the outbox row unacknowledged and every later message unsent, which is how
/// <c>TheSameDeclarationAgainIsAnsweredWithTheFirstAnswerAndNothingIsStoppedTwice</c> on batch-p3 timed out waiting
/// for its <c>OperationResult</c>.
/// </para>
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>
    /// The ack of a re-prompt arrives a second after the vehicle's wait for it ran out. The row is marked acknowledged,
    /// the session stays up, and the operator closing the door afterwards gets its <c>OperationResult</c> through.
    /// </summary>
    /// <remarks>
    /// Red before the fix: the late ack ended the session with
    /// <c>收到未处理的WIRE_TO_GATE消息：DurableAck</c>, the row stayed unacknowledged.
    /// </remarks>
    [Fact]
    public async Task AnAckArrivingAfterItsSendTimedOutIsTakenAndTheSessionGoesOn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true, KeepSnapshotFresh = true },
            token);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);

        string late = await DelayTheNextProgressAckAsync(
            harness,
            harness.Client.MessageTimeout + TimeSpan.FromSeconds(1),
            token);
        await WaitForLateAckAsync(harness, late, token);
        await WaitForAcknowledgedOnFileAsync(harness, late, token);
        Assert.True(harness.Client.Current.Connected, "the late ack must not end the session");
        Assert.Contains(harness.Logger.Entries, entry =>
            entry.Severity == LogSeverity.Information
            && entry.Message.Contains(late, StringComparison.Ordinal)
            && entry.Message.StartsWith("收到迟到的DurableAck", StringComparison.Ordinal));

        harness.Io.CloseDoor(0, cargo: true);
        await harness.WaitForInboundAsync("OperationResult", token);
        Assert.DoesNotContain(harness.Server.Received, item => item.Connection != 1);
    }

    /// <summary>
    /// The operator presses 取消装货 while a re-prompt waits for its ack, and the server authorizes it: the load's run is
    /// aborted, which cancels that wait, and the ack arrives after it. The row is marked acknowledged, the session stays
    /// up, and the cancellation's result reaches the server.
    /// </summary>
    /// <remarks>
    /// Red before the fix, as above: the late ack ended the session and the <c>LoadCancellationResult</c> never left.
    /// The abort itself is unchanged and stays so (the coordinator's ruling on #250): a send it cancels is still owed and
    /// on file, and only the answer arriving after it is what this is about.
    /// </remarks>
    [Fact]
    public async Task AnAckArrivingAfterALoadCancellationAbortedItsSendIsTakenAndTheSessionGoesOn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true, KeepSnapshotFresh = true },
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
            });
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        await Harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight load cancellation entry to be offered",
            token);

        // Pressed the moment the re-prompt reaches the server, so the abort lands well inside the second its ack takes.
        string late = await DelayTheNextProgressAckAsync(harness, TimeSpan.FromSeconds(1), token);
        _ = await harness.Business.RequestLoadCancellationAsync("现场不装了。", token);
        Assert.Contains(harness.Server.Received, item => item.MessageType == "LoadCancellationStartRequested");

        await WaitForLateAckAsync(harness, late, token);
        await WaitForAcknowledgedOnFileAsync(harness, late, token);
        Assert.True(harness.Client.Current.Connected, "the late ack must not end the session");
        await WaitForLateAckLogAsync(harness, late, LogSeverity.Information, "现记为已确认", token);
        await harness.WaitForInboundAsync("LoadCancellationResult", token);
        Assert.DoesNotContain(harness.Server.Received, item => item.Connection != 1);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "OperationResult");
    }

    /// <summary>
    /// A <c>SafetyStateChanged</c> whose ack arrives after the send's wait for it ran out: settling the row also moves
    /// the accepted safety state version on to the change's and publishes it, as an ack in time does (review S1 of #260).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sent through the session client directly. The business service, the one caller in the product, ends the session
    /// itself when a safety change fails to send, so through it a late ack only lands in the few milliseconds between the
    /// wait running out and that disconnect -- too narrow to build a test on, and the client owes every caller the same
    /// bookkeeping either way. The version is well past the business service's own, so its reports cannot collide.
    /// </para>
    /// <para>
    /// The double sends no readiness after a safety ack in this harness, so nothing but the late ack can move the version
    /// on.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ALateAckOfASafetyStateChangeMovesTheAcceptedSafetyStateVersionOn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true, KeepSnapshotFresh = true },
            token);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        long changed = harness.Client.Current.SafetyStateVersion + 10;
        harness.Server.SafetyStateChangedAckDelay = harness.Client.MessageTimeout + TimeSpan.FromSeconds(1);

        await Assert.ThrowsAsync<TimeoutException>(() => harness.Client.SendSafetyStateChangedAsync(
            changed,
            DateTimeOffset.UtcNow,
            new WireToGateSafetySummaryPayload(true, true, true, true, false, []),
            [1],
            token));
        Assert.True(harness.Client.Current.SafetyStateVersion < changed, "nothing has acknowledged the change yet");
        string late = harness.Server.ReceivedEnvelopes
            .Last(envelope => envelope.MessageType == "SafetyStateChanged")
            .MessageId;

        await WaitForLateAckAsync(harness, late, token);
        await WaitForLateAckLogAsync(harness, late, LogSeverity.Information, "现记为已确认", token);

        Assert.True(harness.Client.Current.Connected, "the late ack must not end the session");
        Assert.Equal(changed, harness.Client.Current.SafetyStateVersion);
    }

    /// <summary>
    /// A second ack of a row already acknowledged -- the server answering a send twice -- changes nothing on file, is
    /// logged as information, and the session goes on.
    /// </summary>
    [Fact]
    public async Task AFurtherAckOfARowAlreadyAcknowledgedChangesNothingAndTheSessionGoesOn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true, KeepSnapshotFresh = true },
            token);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        WireToGateDurableMessage row = await FirstAcknowledgedProgressAsync(harness, token);

        await harness.Server.SendDurableAckAsync(row.MessageId, row.MessageType, row.ContentSha256);
        await WaitForLateAckLogAsync(harness, row.MessageId, LogSeverity.Information, "早已确认", token);

        Assert.True(harness.Client.Current.Connected, "a second ack must not end the session");
        Assert.Equal(row, await harness.Journal.ReadOutgoingByMessageIdAsync(row.MessageId, token));
    }

    /// <summary>
    /// A late ack of a row given up (onboard-hmi#254) leaves it given up and not acknowledged: what the server holds
    /// under that identity is not this vehicle's content. Logged as a warning; the session goes on.
    /// </summary>
    /// <remarks>
    /// The row is given up on file while its ack is held back past the send's timeout -- the order a refusal and a
    /// stray ack would come in does not matter to the outcome, only that the ack finds the row given up.
    /// </remarks>
    [Fact]
    public async Task ALateAckOfARowGivenUpLeavesItGivenUpAndTheSessionGoesOn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true, KeepSnapshotFresh = true },
            token);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        string late = await DelayTheNextProgressAckAsync(
            harness,
            harness.Client.MessageTimeout + TimeSpan.FromSeconds(2),
            token);
        WireToGateDurableMessage row = Assert.IsType<WireToGateDurableMessage>(
            await harness.Journal.ReadOutgoingByMessageIdAsync(late, token));
        await harness.Journal.MarkOutgoingAbandonedAsync(
            row.MessageId,
            row.ContentSha256,
            "MESSAGE_ID_CONTENT_CONFLICT",
            token);

        await WaitForLateAckAsync(harness, late, token);
        await WaitForLateAckLogAsync(harness, late, LogSeverity.Warning, "保持放弃", token);
        Assert.Contains(harness.Logger.Entries, entry =>
            entry.Severity == LogSeverity.Warning
            && entry.Message.Contains($"contentSha256={row.ContentSha256}", StringComparison.Ordinal)
            && entry.Message.Contains("这次放弃很可能是误报，核对 MES 时不要重复补录", StringComparison.Ordinal));

        Assert.True(harness.Client.Current.Connected, "a late ack of a row given up must not end the session");
        WireToGateDurableMessage after = Assert.IsType<WireToGateDurableMessage>(
            await harness.Journal.ReadOutgoingByMessageIdAsync(late, token));
        Assert.True(after.Abandoned);
        Assert.False(after.Acknowledged);
    }

    /// <summary>
    /// An ack that is not a late answer to anything on file -- another content hash or message type than the row's, a
    /// messageId the outbox has never held, or an envelope correlated to another message than the one its payload
    /// accepts -- still ends the session as an unhandled message, as every such ack did before onboard-hmi#250, and
    /// changes no row.
    /// </summary>
    [Theory]
    [InlineData("content")]
    [InlineData("type")]
    [InlineData("unknown")]
    [InlineData("correlation")]
    public async Task AnAckMatchingNoRowOnFileStillEndsTheSession(string mismatch)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true, KeepSnapshotFresh = true },
            token);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        WireToGateDurableMessage row = await FirstAcknowledgedProgressAsync(harness, token);

        await (mismatch switch
        {
            "content" => harness.Server.SendDurableAckAsync(row.MessageId, row.MessageType, new string('0', 64)),
            "type" => harness.Server.SendDurableAckAsync(row.MessageId, "SafetyStateChanged", row.ContentSha256),
            "correlation" => harness.Server.SendDurableAckAsync(
                row.MessageId,
                row.MessageType,
                row.ContentSha256,
                correlationId: Guid.NewGuid().ToString("D")),
            _ => harness.Server.SendDurableAckAsync(Guid.NewGuid().ToString("D"), row.MessageType, row.ContentSha256)
        });
        await Harness.WaitUntilAsync(
            () => !harness.Client.Current.Connected,
            "an ack matching no row to end the session",
            token);

        Assert.Contains(harness.Logger.Entries, entry =>
            entry.Message.Contains("收到未处理的WIRE_TO_GATE消息：DurableAck", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Logger.Entries, entry =>
            entry.Message.StartsWith("收到迟到的DurableAck", StringComparison.Ordinal));
        Assert.Equal(row, await harness.Journal.ReadOutgoingByMessageIdAsync(row.MessageId, token));
    }

    /// <summary>The first <c>OperationProgress</c> of the load, once its ack has been taken on file.</summary>
    private static async Task<WireToGateDurableMessage> FirstAcknowledgedProgressAsync(
        Harness harness,
        CancellationToken cancellationToken)
    {
        string messageId = harness.Server.ReceivedEnvelopes
            .First(envelope => envelope.MessageType == "OperationProgress")
            .MessageId;
        await WaitForAcknowledgedOnFileAsync(harness, messageId, cancellationToken);
        return Assert.IsType<WireToGateDurableMessage>(
            await harness.Journal.ReadOutgoingByMessageIdAsync(messageId, cancellationToken));
    }

    private static Task WaitForLateAckLogAsync(
        Harness harness,
        string messageId,
        LogSeverity severity,
        string outcome,
        CancellationToken cancellationToken) =>
        Harness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Severity == severity
                && entry.Message.StartsWith("收到迟到的DurableAck", StringComparison.Ordinal)
                && entry.Message.Contains(messageId, StringComparison.Ordinal)
                && entry.Message.Contains(outcome, StringComparison.Ordinal)),
            $"the late ack of {messageId} to be logged ({severity}, {outcome})",
            cancellationToken,
            () => string.Join(
                Environment.NewLine,
                harness.Logger.Entries.Select(entry => $"{entry.Severity} {entry.Message}")));

    /// <summary>
    /// Holds back the ack of the next <c>OperationProgress</c> the vehicle sends -- the executor's next re-prompt -- by
    /// <paramref name="delay"/>, and returns that progress's messageId once the server has it. Acks after it go out at
    /// once again.
    /// </summary>
    private static async Task<string> DelayTheNextProgressAckAsync(
        Harness harness,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        int before = ProgressCount(harness);
        harness.Server.OperationProgressAckDelay = delay;
        await Harness.WaitUntilAsync(
            () => ProgressCount(harness) > before,
            "the executor's next re-prompt",
            cancellationToken,
            harness.DescribeEvents);
        harness.Server.OperationProgressAckDelay = TimeSpan.Zero;
        return harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "OperationProgress")
            .ElementAt(before)
            .MessageId;
    }

    private static int ProgressCount(Harness harness) =>
        harness.Server.ReceivedEnvelopes.Count(envelope => envelope.MessageType == "OperationProgress");

    private static Task WaitForLateAckAsync(Harness harness, string messageId, CancellationToken cancellationToken) =>
        Harness.WaitUntilAsync(
            () => harness.Server.SentEnvelopes.Any(envelope =>
                envelope.MessageType == "DurableAck"
                && envelope.WireLine.Contains(messageId, StringComparison.Ordinal)),
            $"the server to send the late ack of {messageId}",
            cancellationToken);

    private static Task WaitForAcknowledgedOnFileAsync(
        Harness harness,
        string messageId,
        CancellationToken cancellationToken) =>
        Harness.WaitUntilAsync(
            () => harness.Journal
                .ReadOutgoingByMessageIdAsync(messageId, cancellationToken)
                .GetAwaiter()
                .GetResult()?.Acknowledged == true,
            $"the late ack to mark {messageId} acknowledged on file",
            cancellationToken,
            () => string.Join(
                Environment.NewLine,
                harness.Logger.Entries.Select(entry => $"{entry.Severity} {entry.Message}")));
}
