using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SQCD.Agv.Core;
using Xunit;
using Xunit.Sdk;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// What a wait of this harness says when it runs out (8005-agv-onboard-hmi#383).
/// </summary>
/// <remarks>
/// Twice on the new CI guest a wait here ran its five seconds out with nothing given back for process stalls: the test
/// thread kept looking and the fact it waited for never moved. Reproduced once on one core, the vehicle had sent
/// <c>WAITING_OPERATOR</c> and was waiting for its <c>DurableAck</c>, under the 30-second message timeout of the take-over
/// stop. The failure said none of that -- only "timed out" and an empty list of UI errors -- so each one had to be chased
/// again before anyone could say whether the double, the harness or the vehicle had stopped.
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    private const int SceneMessages = 10;
    private const int SceneLogEntries = 15;

    /// <summary>How long the failure of a wait may spend reading the vehicle's outbox, which takes the journal's gate.</summary>
    private static readonly TimeSpan SceneOutboxReadBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// What a wait that ran out appends to its message: where things stopped, when the wait began and ended, this double's
    /// read loops, what the vehicle's outbox still owes, the last messages each way with when each ack was written, and the
    /// vehicle's last log entries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why both sides of the acknowledgements.</b> The vehicle waits for a <c>DurableAck</c> after every durable
    /// message, for up to <paramref name="messageTimeout"/> -- 30 seconds at the take-over stop -- and a wait here that runs
    /// out while it does looks like any other. Three places can be where it stopped, and the scene names each: this double
    /// never wrote the ack (the message is in <see cref="FakeControlServer.ReceivedEnvelopes"/> with no ack written); this
    /// double's read loop is still handling an earlier message, so the one the vehicle waits on has not even been read
    /// (review S1: it is then in no list of the double's, only in the vehicle's outbox); or the ack was written and the
    /// vehicle did not act on it. Which message types are acknowledged is read from what this double has acknowledged in
    /// this run, not from a list kept here. The outbox is read through the journal's own read, the one the handshake uses
    /// to replay it: nothing of the vehicle is changed to say this.
    /// </para>
    /// <para>
    /// <b>Why the wait stays at five seconds.</b> 30 seconds is how long the vehicle tolerates a missing ack before it gives
    /// the message up, not how long one takes: on loopback it comes back in milliseconds, and every wait of this harness
    /// is for a fact that follows within a second or two of the last thing the test did. Waiting out the message timeout
    /// would turn an ack that never comes into a test that takes half a minute to say so, and would hide the one that comes
    /// late. So the budget is left where it is, and the failure says which of the two it ran into.
    /// </para>
    /// </remarks>
    private static async Task<string> DescribeWaitFailureSceneAsync(
        FakeControlServer server,
        RecordingLogger logger,
        IWireToGateJournal journal,
        TimeSpan messageTimeout,
        DateTimeOffset waitStartedAt,
        DateTimeOffset timedOutAt)
    {
        var received = server.ReceivedEnvelopes;
        var receivedAt = server.ReceivedAt;
        var sent = server.SentEnvelopes;
        // The ack of each message of the vehicle's this double has queued, by the vehicle's messageId: the ack's own id,
        // for when it was written.
        Dictionary<string, string> ackOf = new(StringComparer.Ordinal);
        HashSet<string> acknowledgedTypes = new(StringComparer.Ordinal);
        foreach (var envelope in sent.Where(item => item.MessageType == "DurableAck"))
        {
            using JsonDocument? document = TryParse(envelope.WireLine);
            if (document?.RootElement.TryGetProperty("payload", out JsonElement payload) == true
                && payload.TryGetProperty("acceptedMessageId", out JsonElement id)
                && payload.TryGetProperty("acceptedMessageType", out JsonElement type))
            {
                ackOf[id.GetString()!] = envelope.MessageId;
                acknowledgedTypes.Add(type.GetString()!);
            }
        }

        string AckState(string messageId) =>
            !ackOf.TryGetValue(messageId, out string? ackId) ? "no DurableAck"
            : server.WrittenAt(ackId) is { } writtenAt ? $"DurableAck written {Clock(writtenAt)}"
            : "DurableAck queued, not written yet";

        IReadOnlyList<WireToGateDurableMessage>? owedByVehicle = null;
        string? outboxUnreadable = null;
        try
        {
            owedByVehicle = await journal.ReadUnacknowledgedOutgoingAsync().WaitAsync(SceneOutboxReadBudget);
        }
        catch (Exception exception) when (exception is TimeoutException or ObjectDisposedException or InvalidOperationException)
        {
            outboxUnreadable = $"not readable within {SceneOutboxReadBudget.TotalSeconds:0}s ({exception.GetType().Name})";
        }

        var readLoops = server.ReadLoops();
        var owed = received.LastOrDefault(item =>
            acknowledgedTypes.Contains(item.MessageType) && server.WrittenAt(ackOf.GetValueOrDefault(item.MessageId) ?? string.Empty) is null);
        var busy = readLoops.LastOrDefault(loop => loop.State.StartsWith("handling ", StringComparison.Ordinal));
        HashSet<string> receivedIds = [.. received.Select(item => item.MessageId)];
        WireToGateDurableMessage? unread = owedByVehicle?.LastOrDefault(row => !receivedIds.Contains(row.MessageId));

        StringBuilder scene = new();
        scene.AppendLine();
        if (owed.MessageId is not null)
        {
            scene.AppendLine(CultureInfo.InvariantCulture, $"Stuck on: {Name(owed)}, which this double has not acknowledged "
                + $"({AckState(owed.MessageId)}); the vehicle waits up to {messageTimeout.TotalSeconds:0.##}s for its DurableAck.");
        }
        else if (unread is not null)
        {
            // In the vehicle's outbox and in no list of this double's: written to the wire, or about to be, and not read.
            var loops = string.Join("; ", readLoops.Select(loop => $"connection {loop.Connection} {loop.State} since {Clock(loop.Since)}"));
            scene.AppendLine(CultureInfo.InvariantCulture, $"Stuck on: {Name((0, unread.MessageType, unread.MessageId, unread.WireLine))}, on file in the vehicle's outbox since {Clock(unread.CreatedAt)} but not read by this double (read loops: {loops}); the vehicle waits up to {messageTimeout.TotalSeconds:0.##}s for its DurableAck.");
        }
        else if (busy.State is not null)
        {
            scene.AppendLine(CultureInfo.InvariantCulture,
                $"Stuck on: this double's read loop on connection {busy.Connection}, {busy.State} since {Clock(busy.Since)}; a message behind it has not been read.");
        }
        else
        {
            scene.AppendLine(owedByVehicle is { Count: > 0 }
                ? "Stuck on: the vehicle side -- this double read and acknowledged everything the vehicle's outbox still "
                    + "owes (below), and its read loops are idle."
                : "Stuck on: nothing on the wire -- this double owes no ack, its read loops are idle and the vehicle's outbox "
                    + "owes nothing.");
        }

        scene.AppendLine(CultureInfo.InvariantCulture, $"Wait: started {Clock(waitStartedAt)}, timed out {Clock(timedOutAt)}.");
        scene.AppendLine(CultureInfo.InvariantCulture, $"Thread pool: {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} work items queued.");
        scene.AppendLine("Read loops of this double:");
        foreach (var loop in readLoops)
        {
            scene.AppendLine(CultureInfo.InvariantCulture, $"  connection {loop.Connection}: {loop.State} since {Clock(loop.Since)}");
        }

        scene.AppendLine("Owed by the vehicle's outbox (not acknowledged, not given up):");
        if (outboxUnreadable is not null)
        {
            scene.AppendLine(CultureInfo.InvariantCulture, $"  {outboxUnreadable}");
        }
        else
        {
            foreach (WireToGateDurableMessage row in owedByVehicle!.TakeLast(SceneMessages))
            {
                using JsonDocument? document = TryParse(row.WireLine);
                string detail = document is null ? string.Empty : Detail(document.RootElement);
                scene.AppendLine(CultureInfo.InvariantCulture, $"  {row.MessageType} {row.MessageId}{detail} on file {Clock(row.CreatedAt)}");
            }
        }

        scene.AppendLine(CultureInfo.InvariantCulture, $"Sent by the vehicle, last {SceneMessages}:");
        for (int index = Math.Max(0, received.Count - SceneMessages); index < received.Count; index++)
        {
            var envelope = received[index];
            string ack = acknowledgedTypes.Contains(envelope.MessageType) ? $" -> {AckState(envelope.MessageId)}" : string.Empty;
            scene.AppendLine(CultureInfo.InvariantCulture, $"  {Line(envelope, receivedAt[index])}{ack}");
        }

        scene.AppendLine(CultureInfo.InvariantCulture, $"Sent by this double, last {SceneMessages}:");
        foreach (var envelope in sent.TakeLast(SceneMessages))
        {
            string written = server.WrittenAt(envelope.MessageId) is { } at ? $"written {Clock(at)}" : "not written yet";
            scene.AppendLine(CultureInfo.InvariantCulture, $"  {Name(envelope)} {written}");
        }

        scene.AppendLine("Vehicle log, last entries:");
        foreach (string entry in logger.Tail(SceneLogEntries))
        {
            scene.AppendLine(CultureInfo.InvariantCulture, $"  {entry}");
        }

        return scene.ToString();
    }

    private static string Name((int Connection, string MessageType, string MessageId, string WireLine) envelope)
    {
        using JsonDocument? document = TryParse(envelope.WireLine);
        return $"{envelope.MessageType} {envelope.MessageId}{(document is null ? string.Empty : Detail(document.RootElement))}";
    }

    /// <summary>
    /// A message of the vehicle's as this double read it: when it was read, which is when it reached the wire as near as this
    /// side can tell, and the <c>sentAt</c> the vehicle stamped on it. Not <c>sentAt</c> alone: a resend carries the first
    /// copy's, so an <c>OperationResult</c> resent seconds later read as sent at the same instant twice
    /// (8005-agv-onboard-hmi#393 review S4).
    /// </summary>
    private static string Line(
        (int Connection, string MessageType, string MessageId, string WireLine) envelope,
        DateTimeOffset readAt)
    {
        using JsonDocument? document = TryParse(envelope.WireLine);
        DateTimeOffset? sentAt = document is null ? null : SentAt(document.RootElement);
        return $"{Name(envelope)} read {Clock(readAt)} (sentAt {(sentAt is { } at ? Clock(at) : "?")})";
    }

    private static string Clock(DateTimeOffset at) => at.ToUniversalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static JsonDocument? TryParse(string wireLine)
    {
        try
        {
            return JsonDocument.Parse(wireLine);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? SentAt(JsonElement envelope) =>
        envelope.TryGetProperty("sentAt", out JsonElement sentAt) && sentAt.TryGetDateTimeOffset(out DateTimeOffset value)
            ? value
            : null;

    /// <summary>The one payload field that tells two messages of a type apart, where the type has one.</summary>
    private static string Detail(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (string field in (string[])["phase", "overallOutcome", "readiness", "acceptedMessageType"])
        {
            if (payload.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                return $" ({value.GetString()})";
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// After <paramref name="after"/>, queues more 800 ms sleeps than the pool has threads, so the next poll of a wait --
    /// a timer continuation queued behind them -- runs late, well past <see cref="StallAwareDeadline.StallThreshold"/>.
    /// </summary>
    /// <remarks>
    /// Sized from the pool as it stands, not from the processor count: on four cores twice the processors left threads
    /// free and nothing was given back four runs in five. The surplus covers the threads the pool adds while starved.
    /// </remarks>
    private static async Task StallThePoolOnceAsync(TimeSpan after, CancellationToken token)
    {
        await Task.Delay(after, token);
        int sleepers = ThreadPool.ThreadCount + (Environment.ProcessorCount * 2) + 8;
        await Task.WhenAll(Enumerable.Range(0, sleepers).Select(_ => Task.Run(() => Thread.Sleep(800), token)));
    }

    /// <summary>
    /// The progress acks of a fault cargo handoff are held back by the double, so the handoff's result never comes. The
    /// wait for it still fails in its own time, and its message names the message the vehicle is stuck on, says the
    /// double never acknowledged it, and carries the vehicle's log and the wire as the double saw them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With <paramref name="stallTheThreadPool"/>, every pool thread is kept busy for a moment once the wait has started,
    /// so a poll comes back late and the deadline gives time back: the message then says so between "5s" and "waiting
    /// for" (8005-agv-onboard-hmi#383 review M1, the reviewer's own injection), and what it asserts must hold either way.
    /// </para>
    /// <para>
    /// The vector trait is here because <c>IntegrationSliceTraitArchitectureTests</c> takes a slice only as the projection
    /// of the test's own vectors (review S3). What this test proves is the harness's failure message, not the fault cargo
    /// handoff; the handoff is only the path that gives it a durable message whose ack can be held.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AWaitThatRunsOutNamesTheMessageTheVehicleIsStuckOn(bool stallTheThreadPool)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptA;
                server.RecoveryVectorSlotOperationAttemptId = AttemptA;
                server.RecoverySessionScopeByDemand = TwoLoadScopes();
            },
            token);
        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await LoadAndRecordAsync(harness, io, DemandB, AttemptB, slot: 5, token);
        await HoldTheSessionForRecoveryAsync(harness, token);
        await WaitForChoicesAsync(harness, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓"], token);

        TaskCompletionSource acksHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.OperationProgressAckHold = acksHeld.Task;
        try
        {
            harness.OnUi(() => harness.ViewModel.SelectedRecoveryDemandChoice =
                harness.ViewModel.RecoveryDemandChoices.Single(row => row.DemandId == DemandA));
            Assert.True(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));

            Stopwatch elapsed = Stopwatch.StartNew();
            Task stall = stallTheThreadPool ? StallThePoolOnceAsync(TimeSpan.FromSeconds(1), token) : Task.CompletedTask;
            FailException failure = await Assert.ThrowsAsync<FailException>(() => harness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Any(item => item.MessageType == "FaultCargoRecoveryResult"),
                "the control server to receive FaultCargoRecoveryResult",
                token));
            elapsed.Stop();
            await stall;
            TestContext.Current.TestOutputHelper?.WriteLine(failure.Message);
            if (stallTheThreadPool)
            {
                Assert.Contains("given back for process stalls", failure.Message);
            }

            // In its own time: the five seconds, at most what a stall gives back on top, and the outbox read.
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(5) + StallAwareDeadline.StallBudget + SceneOutboxReadBudget,
                $"the wait took {elapsed.Elapsed}");
            // The handoff's first progress, the one whose ack the vehicle waits for before it goes on.
            var received = harness.Server.ReceivedEnvelopes;
            int submitted = received.Select(item => item.MessageType).ToList().LastIndexOf("RecoveryActionSubmitted");
            string progressId = received.Skip(submitted + 1).First(item => item.MessageType == "OperationProgress").MessageId;
            // A wait that was given time back says how much between "5s" and "waiting for" (review M1).
            Assert.Matches(
                new Regex(@"Timed out after 5s( \(\+[^)]*\))? waiting for: the control server to receive FaultCargoRecoveryResult\."),
                failure.Message);
            Assert.Contains(
                $"Stuck on: OperationProgress {progressId} (PREPARING), which this double has not acknowledged (no DurableAck)",
                failure.Message);
            Assert.Matches(new Regex(@"Wait: started \d\d:\d\d:\d\d\.\d{3}, timed out \d\d:\d\d:\d\d\.\d{3}\."), failure.Message);
            Assert.Matches(new Regex(@"connection \d+: waiting in ReadLineAsync since "), failure.Message);
            Assert.Matches(
                new Regex($@"Owed by the vehicle's outbox[^\n]*\n(  [^\n]*\n)*  OperationProgress {progressId} \(PREPARING\) on file "),
                failure.Message);
            Assert.Contains($"OperationProgress {progressId} (PREPARING) read ", failure.Message);
            Assert.Matches(new Regex(@"DurableAck [0-9a-f-]+ \(OperationProgress\) written \d\d:"), failure.Message);
            Assert.Contains("Vehicle log, last entries:", failure.Message);
        }
        finally
        {
            acksHeld.TrySetResult();
        }
    }

    /// <summary>
    /// The message the vehicle waits on has not even been read by the double (review S1): reading is paused while the
    /// handoff runs, so its next progress is on file in the vehicle's outbox and in no list of the double's. The wait's
    /// message names it as unread, not as the vehicle's own stop.
    /// </summary>
    /// <remarks>
    /// How it is held without timing: the handoff's first progress has its ack held, so the vector waits; reading is
    /// paused; then the ack goes out. A read already under way when the pause was set still completes, so one more
    /// progress may be read and acknowledged first -- the one after it is the one left unread, whichever it is. The vector
    /// trait is the projection rule's, as above.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AWaitThatRunsOutNamesAMessageTheDoubleHasNotRead()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptA;
                server.RecoveryVectorSlotOperationAttemptId = AttemptA;
                server.RecoverySessionScopeByDemand = TwoLoadScopes();
            },
            token);
        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await LoadAndRecordAsync(harness, io, DemandB, AttemptB, slot: 5, token);
        await HoldTheSessionForRecoveryAsync(harness, token);
        await WaitForChoicesAsync(harness, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓"], token);

        TaskCompletionSource acksHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.OperationProgressAckHold = acksHeld.Task;
        try
        {
            harness.OnUi(() => harness.ViewModel.SelectedRecoveryDemandChoice =
                harness.ViewModel.RecoveryDemandChoices.Single(row => row.DemandId == DemandA));
            Assert.True(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));
            int submitted = harness.Server.ReceivedEnvelopes.Select(item => item.MessageType).ToList()
                .LastIndexOf("RecoveryActionSubmitted");
            await harness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Skip(submitted + 1).Any(item => item.MessageType == "OperationProgress"),
                "the handoff's first progress to reach the double",
                token);
            harness.Server.PauseReading = true;
            acksHeld.TrySetResult();

            FailException failure = await Assert.ThrowsAsync<FailException>(() => harness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Any(item => item.MessageType == "FaultCargoRecoveryResult"),
                "the control server to receive FaultCargoRecoveryResult",
                token));
            TestContext.Current.TestOutputHelper?.WriteLine(failure.Message);

            Match stuck = Regex.Match(
                failure.Message,
                @"Stuck on: OperationProgress (?<id>[0-9a-f-]{36}) \(\w+\), on file in the vehicle's outbox since \d\d:\d\d:\d\d\.\d{3} but not read by this double \(read loops: [^)]*\); the vehicle waits up to 30s for its DurableAck\.");
            Assert.True(stuck.Success, "no unread message named");
            Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, item => item.MessageId == stuck.Groups["id"].Value);
            Assert.Matches(
                new Regex($@"Owed by the vehicle's outbox[^\n]*\n(  [^\n]*\n)*  OperationProgress {stuck.Groups["id"].Value} \(\w+\) on file "),
                failure.Message);
        }
        finally
        {
            harness.Server.PauseReading = false;
            acksHeld.TrySetResult();
        }
    }
}
