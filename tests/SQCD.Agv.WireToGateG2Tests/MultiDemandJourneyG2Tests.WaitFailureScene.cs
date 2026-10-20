using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// What a wait that ran out appends to its message: the message the vehicle is stuck on, the last messages each way with
    /// whether this double acknowledged the vehicle's, and the vehicle's last log entries, each with the time it happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the double's side of the acknowledgements.</b> The vehicle waits for a <c>DurableAck</c> after every durable
    /// message, for up to <paramref name="messageTimeout"/> -- 30 seconds at the take-over stop -- and a wait here that runs
    /// out while it does looks like any other. Whether this double sent that ack is what splits the cases: not sent, the
    /// double's read loop is where it stopped; sent, the vehicle had it and did not act on it. Which message types are
    /// acknowledged is read from what this double has acknowledged in this run, not from a list kept here.
    /// </para>
    /// <para>
    /// <b>Why the wait stays at five seconds.</b> 30 seconds is how long the vehicle tolerates a missing ack before it gives
    /// the message up, not how long one takes: on loopback it comes back in milliseconds, and every wait of this harness
    /// is for a fact that follows within a second or two of the last thing the test did. Waiting out the message timeout
    /// would turn an ack that never comes into a test that takes half a minute to say so, and would hide the one that comes
    /// late. So the budget is left where it is, and the failure says which of the two it ran into.
    /// </para>
    /// </remarks>
    private static string DescribeWaitFailureScene(
        FakeControlServer server,
        RecordingLogger logger,
        TimeSpan messageTimeout)
    {
        var received = server.ReceivedEnvelopes;
        var sent = server.SentEnvelopes;
        Dictionary<string, DateTimeOffset?> acknowledged = [];
        HashSet<string> acknowledgedTypes = [];
        foreach (var envelope in sent.Where(item => item.MessageType == "DurableAck"))
        {
            using JsonDocument? document = TryParse(envelope.WireLine);
            if (document?.RootElement.TryGetProperty("payload", out JsonElement payload) == true
                && payload.TryGetProperty("acceptedMessageId", out JsonElement id)
                && payload.TryGetProperty("acceptedMessageType", out JsonElement type))
            {
                acknowledged[id.GetString()!] = SentAt(document.RootElement);
                acknowledgedTypes.Add(type.GetString()!);
            }
        }

        string Name((int Connection, string MessageType, string MessageId, string WireLine) envelope)
        {
            using JsonDocument? document = TryParse(envelope.WireLine);
            return $"{envelope.MessageType} {envelope.MessageId}{(document is null ? string.Empty : Detail(document.RootElement))}";
        }

        string Line((int Connection, string MessageType, string MessageId, string WireLine) envelope)
        {
            using JsonDocument? document = TryParse(envelope.WireLine);
            return $"{Name(envelope)} sent {(document is null ? "?" : $"{SentAt(document.RootElement):HH:mm:ss.fff}")}";
        }

        StringBuilder scene = new();
        var owed = received.LastOrDefault(item =>
            acknowledgedTypes.Contains(item.MessageType) && !acknowledged.ContainsKey(item.MessageId));
        scene.AppendLine();
        scene.AppendLine(owed.MessageId is not null
            ? $"Stuck on: {Name(owed)}, which this double has not "
                + $"acknowledged; the vehicle waits up to {messageTimeout.TotalSeconds:0.##}s for its DurableAck."
            : "Stuck on: nothing this double owes -- every message of a type it acknowledges was acknowledged.");
        scene.AppendLine(CultureInfo.InvariantCulture, $"Thread pool: {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} work items queued.");
        scene.AppendLine(CultureInfo.InvariantCulture, $"Sent by the vehicle, last {SceneMessages}:");
        foreach (var envelope in received.TakeLast(SceneMessages))
        {
            string ack = acknowledged.TryGetValue(envelope.MessageId, out DateTimeOffset? at)
                ? $" -> DurableAck sent {at:HH:mm:ss.fff}"
                : acknowledgedTypes.Contains(envelope.MessageType) ? " -> no DurableAck" : string.Empty;
            scene.AppendLine(CultureInfo.InvariantCulture, $"  {Line(envelope)}{ack}");
        }

        scene.AppendLine(CultureInfo.InvariantCulture, $"Sent by this double, last {SceneMessages}:");
        foreach (var envelope in sent.TakeLast(SceneMessages))
        {
            scene.AppendLine(CultureInfo.InvariantCulture, $"  {Line(envelope)}");
        }

        scene.AppendLine("Vehicle log, last entries:");
        foreach (string entry in logger.Tail(SceneLogEntries))
        {
            scene.AppendLine(CultureInfo.InvariantCulture, $"  {entry}");
        }

        return scene.ToString();
    }

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
    /// The progress acks of a fault cargo handoff are held back by the double, so the handoff's result never comes. The
    /// wait for it still fails in its own time, and its message names the message the vehicle is stuck on, says the
    /// double never acknowledged it, and carries the vehicle's log and the wire as the double saw them.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AWaitThatRunsOutNamesTheMessageTheVehicleIsStuckOn()
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
            FailException failure = await Assert.ThrowsAsync<FailException>(() => harness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Any(item => item.MessageType == "FaultCargoRecoveryResult"),
                "the control server to receive FaultCargoRecoveryResult",
                token));
            elapsed.Stop();
            TestContext.Current.TestOutputHelper?.WriteLine(failure.Message);

            // In its own time: the five seconds, and at most what a stall gives back on top.
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(5) + StallAwareDeadline.StallBudget + TimeSpan.FromSeconds(1),
                $"the wait took {elapsed.Elapsed}");
            // The handoff's first progress, the one whose ack the vehicle waits for before it goes on.
            var received = harness.Server.ReceivedEnvelopes;
            int submitted = received.Select(item => item.MessageType).ToList().LastIndexOf("RecoveryActionSubmitted");
            string progressId = received.Skip(submitted + 1).First(item => item.MessageType == "OperationProgress").MessageId;
            Assert.Contains("Timed out after 5s waiting for: the control server to receive FaultCargoRecoveryResult", failure.Message);
            Assert.Contains(
                $"Stuck on: OperationProgress {progressId} (PREPARING), which this double has not acknowledged",
                failure.Message);
            Assert.Contains("Vehicle log, last entries:", failure.Message);
            Assert.Contains("SessionReadiness", failure.Message);
            Assert.Contains($"OperationProgress {progressId} (PREPARING) sent ", failure.Message);
        }
        finally
        {
            acksHeld.TrySetResult();
        }
    }
}
