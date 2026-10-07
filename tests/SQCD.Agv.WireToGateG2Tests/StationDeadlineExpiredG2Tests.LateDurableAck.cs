using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
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
/// <para>
/// <b>How these tests order things (onboard-hmi#270).</b> The double holds the ack back until the test releases it,
/// and the test releases it only once it has seen the send stop waiting. They used to hold it back by a fixed delay
/// instead, which raced the ack's clock against the send's: under load the ack could land while the send still waited,
/// was taken as an ordinary ack, and the late-ack path never ran -- CI run 37440854933 shows the row acknowledged, the
/// abort logged and no late-ack line at all. The outcome is then waited for on the session's own
/// <c>LateDurableAckReceived</c>, raised right after the line is logged, or on the session ending, which is what the ack
/// does when nothing takes it as late; either way the wait ends on an event, and a test without the late-ack branch
/// goes red at once rather than at a deadline.
/// </para>
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>
    /// The ack of a re-prompt arrives after the vehicle's wait for it ran out. The row is marked acknowledged, the
    /// session stays up, and the operator closing the door afterwards gets its <c>OperationResult</c> through.
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

        FakeControlServer.DurableAckHold hold = harness.Server.HoldNextDurableAck("OperationProgress");
        string late = await HeldAsync(harness, hold, token);
        int timedOutBefore = ProgressTimeouts(harness);
        await WaitForProgressTimeoutAsync(harness, timedOutBefore, token);

        WireToGateLateDurableAck ack = await ReleaseAndObserveLateAckAsync(harness, hold, late, token);

        Assert.Equal(WireToGateLateDurableAckOutcome.Acknowledged, ack.Outcome);
        await AssertAcknowledgedOnFileAsync(harness, late, token);
        Assert.True(harness.Client.Current.Connected, "the late ack must not end the session");
        AssertLateAckLogged(harness, late, LogSeverity.Information, "现记为已确认");

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
    /// <para>
    /// Red before the fix, as above: the late ack ended the session and the <c>LoadCancellationResult</c> never left.
    /// The abort itself is unchanged and stays so (the coordinator's ruling on #250): a send it cancels is still owed and
    /// on file, and only the answer arriving after it is what this is about.
    /// </para>
    /// <para>
    /// The message timeout is a minute here, so the only thing that can stop the re-prompt's wait is the abort; the
    /// test checks that no progress send timed out, so it cannot pass as the timed-out case above.
    /// </para>
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
            },
            messageTimeout: TimeSpan.FromMinutes(1));
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        await Harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight load cancellation entry to be offered",
            token);

        FakeControlServer.DurableAckHold hold = harness.Server.HoldNextDurableAck("OperationProgress");
        string late = await HeldAsync(harness, hold, token);
        _ = await harness.Business.RequestLoadCancellationAsync("现场不装了。", token);
        Assert.Contains(harness.Server.Received, item => item.MessageType == "LoadCancellationStartRequested");
        // Logged once the executor has unwound, so the re-prompt's send has stopped waiting by then.
        await Harness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry => entry.Message.StartsWith(
                "装货已被授权的装货取消中止",
                StringComparison.Ordinal)),
            "the authorized load cancellation to abort the load's run",
            token,
            DescribeLog(harness));
        Assert.Equal(0, ProgressTimeouts(harness));

        WireToGateLateDurableAck ack = await ReleaseAndObserveLateAckAsync(harness, hold, late, token);

        Assert.Equal(WireToGateLateDurableAckOutcome.Acknowledged, ack.Outcome);
        await AssertAcknowledgedOnFileAsync(harness, late, token);
        Assert.True(harness.Client.Current.Connected, "the late ack must not end the session");
        AssertLateAckLogged(harness, late, LogSeverity.Information, "现记为已确认");
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
    /// bookkeeping either way. The version is well past the business service's own, so its reports cannot collide, and
    /// only this change's ack is held.
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
        FakeControlServer.DurableAckHold hold = harness.Server.HoldNextDurableAck(
            "SafetyStateChanged",
            payload => payload.GetProperty("safetyStateVersion").GetInt64() == changed);

        // Nothing but its own timeout can end this send: the ack it waits for is held until released below.
        await Assert.ThrowsAsync<TimeoutException>(() => harness.Client.SendSafetyStateChangedAsync(
            changed,
            DateTimeOffset.UtcNow,
            new WireToGateSafetySummaryPayload(true, true, true, true, false, []),
            [1],
            token));
        Assert.True(harness.Client.Current.SafetyStateVersion < changed, "nothing has acknowledged the change yet");
        string late = await HeldAsync(harness, hold, token);

        WireToGateLateDurableAck ack = await ReleaseAndObserveLateAckAsync(harness, hold, late, token);

        Assert.Equal(WireToGateLateDurableAckOutcome.Acknowledged, ack.Outcome);
        AssertLateAckLogged(harness, late, LogSeverity.Information, "现记为已确认");
        Assert.True(harness.Client.Current.Connected, "the late ack must not end the session");
        Assert.Equal(changed, harness.Client.Current.SafetyStateVersion);
        Assert.Empty(harness.Server.DurableAckHoldPredicateFailures);
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

        WireToGateLateDurableAck ack = await ObserveLateAckAsync(
            harness,
            row.MessageId,
            () => harness.Server.SendDurableAckAsync(row.MessageId, row.MessageType, row.ContentSha256),
            token);

        Assert.Equal(WireToGateLateDurableAckOutcome.AlreadyAcknowledged, ack.Outcome);
        AssertLateAckLogged(harness, row.MessageId, LogSeverity.Information, "早已确认");
        Assert.True(harness.Client.Current.Connected, "a second ack must not end the session");
        Assert.Equal(row, await harness.Journal.ReadOutgoingByMessageIdAsync(row.MessageId, token));
    }

    /// <summary>
    /// A late ack of a row given up (onboard-hmi#254) leaves it given up and not acknowledged: what the server holds
    /// under that identity is not this vehicle's content. Logged as a warning; the session goes on.
    /// </summary>
    /// <remarks>
    /// The row is given up on file after its send timed out and before its held ack is released -- the order a refusal
    /// and a stray ack would come in does not matter to the outcome, only that the ack finds the row given up.
    /// </remarks>
    [Fact]
    public async Task ALateAckOfARowGivenUpLeavesItGivenUpAndTheSessionGoesOn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true, KeepSnapshotFresh = true },
            token);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        FakeControlServer.DurableAckHold hold = harness.Server.HoldNextDurableAck("OperationProgress");
        string late = await HeldAsync(harness, hold, token);
        int timedOutBefore = ProgressTimeouts(harness);
        await WaitForProgressTimeoutAsync(harness, timedOutBefore, token);
        WireToGateDurableMessage row = Assert.IsType<WireToGateDurableMessage>(
            await harness.Journal.ReadOutgoingByMessageIdAsync(late, token));
        await harness.Journal.MarkOutgoingAbandonedAsync(
            row.MessageId,
            row.ContentSha256,
            "MESSAGE_ID_CONTENT_CONFLICT",
            token);

        WireToGateLateDurableAck ack = await ReleaseAndObserveLateAckAsync(harness, hold, late, token);

        Assert.Equal(WireToGateLateDurableAckOutcome.Abandoned, ack.Outcome);
        AssertLateAckLogged(harness, late, LogSeverity.Warning, "保持放弃");
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
        await Harness.WaitUntilAsync(
            () => harness.Journal
                .ReadOutgoingByMessageIdAsync(messageId, cancellationToken)
                .GetAwaiter()
                .GetResult()?.Acknowledged == true,
            $"the first progress {messageId} to be acknowledged on file",
            cancellationToken,
            DescribeLog(harness));
        return Assert.IsType<WireToGateDurableMessage>(
            await harness.Journal.ReadOutgoingByMessageIdAsync(messageId, cancellationToken));
    }

    /// <summary>The messageId whose ack <paramref name="hold"/> holds, once the double has received it.</summary>
    private static async Task<string> HeldAsync(
        Harness harness,
        FakeControlServer.DurableAckHold hold,
        CancellationToken cancellationToken)
    {
        await Harness.WaitUntilAsync(
            () => hold.Held.IsCompleted,
            "the message whose ack is held to reach the control server",
            cancellationToken,
            harness.DescribeEvents);
        return await hold.Held;
    }

    /// <summary>
    /// Releases the held ack and returns how the session settled it; see <see cref="ObserveLateAckAsync"/>.
    /// </summary>
    private static Task<WireToGateLateDurableAck> ReleaseAndObserveLateAckAsync(
        Harness harness,
        FakeControlServer.DurableAckHold hold,
        string messageId,
        CancellationToken cancellationToken) =>
        ObserveLateAckAsync(harness, messageId, hold.ReleaseAsync, cancellationToken);

    /// <summary>
    /// Runs <paramref name="deliver"/>, which puts an ack of <paramref name="messageId"/> on the wire, and returns how the
    /// session settled it; see <see cref="LateDurableAckObservation.SettleAsync"/>.
    /// </summary>
    private static Task<WireToGateLateDurableAck> ObserveLateAckAsync(
        Harness harness,
        string messageId,
        Func<Task> deliver,
        CancellationToken cancellationToken) =>
        LateDurableAckObservation.SettleAsync(harness.Session, messageId, deliver, DescribeLog(harness), cancellationToken);

    private static void AssertLateAckLogged(Harness harness, string messageId, LogSeverity severity, string outcome) =>
        Assert.True(
            harness.Logger.Entries.Any(entry =>
                entry.Severity == severity
                && entry.Message.StartsWith("收到迟到的DurableAck", StringComparison.Ordinal)
                && entry.Message.Contains(messageId, StringComparison.Ordinal)
                && entry.Message.Contains(outcome, StringComparison.Ordinal)),
            $"the late ack of {messageId} should be logged ({severity}, {outcome}){Environment.NewLine}"
            + DescribeLog(harness)());

    private static async Task AssertAcknowledgedOnFileAsync(
        Harness harness,
        string messageId,
        CancellationToken cancellationToken)
    {
        WireToGateDurableMessage row = Assert.IsType<WireToGateDurableMessage>(
            await harness.Journal.ReadOutgoingByMessageIdAsync(messageId, cancellationToken));
        Assert.True(row.Acknowledged, $"the late ack should have marked {messageId} acknowledged on file");
    }

    /// <summary>
    /// The business service's warnings that a progress send ended in its own timeout, the one way a re-prompt's send
    /// stops waiting without its run being aborted.
    /// </summary>
    private static int ProgressTimeouts(Harness harness) =>
        harness.Logger.Entries.Count(entry =>
            entry.Message.StartsWith("仓位操作进度未能发送", StringComparison.Ordinal)
            && entry.Message.Contains("error=TimeoutException", StringComparison.Ordinal));

    /// <summary>
    /// Waits for the held re-prompt's send to time out, counting from <paramref name="before"/>, which the caller takes
    /// once the held re-prompt has reached the double.
    /// </summary>
    /// <remarks>
    /// Not before that. The executor sends its progress one at a time and waits out each send before the next
    /// (<c>WireToGateSlotOperationExecutor.SendProgressAsync</c>), so by the time the held re-prompt arrives, the send
    /// before it has ended and any timeout warning of its own is already written. Counted earlier, that earlier send --
    /// often still in flight when the load reaches <c>WaitingOperator</c>, and timing out if its ack is slow -- would
    /// satisfy this wait while the held re-prompt still waits, and the ack released then would be taken as an ordinary
    /// one (the independent review of onboard-hmi#270, reproduced by holding that earlier ack for 2.5 s).
    /// </remarks>
    private static Task WaitForProgressTimeoutAsync(Harness harness, int before, CancellationToken cancellationToken) =>
        Harness.WaitUntilAsync(
            () => ProgressTimeouts(harness) > before,
            "the held re-prompt's send to time out",
            cancellationToken,
            DescribeLog(harness));

    private static Func<string> DescribeLog(Harness harness) =>
        () => string.Join(
            Environment.NewLine,
            harness.Logger.Entries.Select(entry => $"{entry.Severity} {entry.Message}"));

    private sealed partial class Harness
    {
        /// <summary>The session the vehicle runs, for a test that watches its events.</summary>
        public WireToGateSessionService Session => _session;
    }
}
