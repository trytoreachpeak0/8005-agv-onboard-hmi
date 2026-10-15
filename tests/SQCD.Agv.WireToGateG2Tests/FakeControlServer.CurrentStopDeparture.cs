using System.Collections.Concurrent;
using System.Text.Json;
using SQCD.Agv.Contracts;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The control server's half of the staging pickup stop's departure request, as far as these tests need it (protocol 4.0.0
/// candidate wire notes, <c>8005-agv-onboard-hmi#349</c>; the real half is <c>8005-agv-control-server#713</c>).
/// </summary>
/// <remarks>
/// <para>
/// Every request is acknowledged with a <c>DurableAck</c> (unless a test drops it) and recorded. With
/// <see cref="RespondToCurrentStopDepartures"/> set, it is answered with a <c>CurrentStopDepartureResult</c>: the outcome the
/// test configured the first time a <c>departureRequestId</c> is seen, the stored result replayed exactly for the same id with
/// the same content, and a <c>ProtocolProblem</c> <c>BUSINESS_ID_CONTENT_CONFLICT</c> for the same id with different content
/// -- the three rules the wire notes give the server.
/// </para>
/// <para>
/// This double does not judge the stop: which outcome comes back is the test's to say. The order of the server's checks is
/// the control server's to keep; the vehicle only shows what it is told.
/// </para>
/// </remarks>
public sealed partial class FakeControlServer
{
    private readonly ConcurrentDictionary<string, (string Content, object Result)> _currentStopDepartureResults =
        new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<JsonElement> _currentStopDepartureRequests = new();

    private int _currentStopDepartureAcksToDrop;

    /// <summary>Answers each departure request with a result; left false, only the <c>DurableAck</c> goes back.</summary>
    public bool RespondToCurrentStopDepartures { get; set; }

    /// <summary>The reason a new departure request is refused with; <c>null</c> accepts it.</summary>
    public string? RejectCurrentStopDeparturesWith { get; set; }

    /// <summary>The <c>currentWorklistRevision</c> a new result names; <c>null</c> echoes the request's.</summary>
    public long? CurrentStopDepartureCurrentWorklistRevision { get; set; }

    /// <summary>Snapshots to send after an <c>ACCEPTED</c> result, in order, as the departure travels in them.</summary>
    public IReadOnlyList<(string MessageType, object Payload)> SnapshotsAfterAcceptedDeparture { get; set; } = [];

    /// <summary>How many departure requests to take without a word -- no ack, no result -- as a link that dropped would.</summary>
    public int CurrentStopDepartureAcksToDrop
    {
        get => Volatile.Read(ref _currentStopDepartureAcksToDrop);
        set => Volatile.Write(ref _currentStopDepartureAcksToDrop, value);
    }

    /// <summary>
    /// The reason the next departure request is answered with a <c>ProtocolProblem</c> instead of a <c>DurableAck</c>,
    /// once; <c>null</c> answers normally.
    /// </summary>
    public string? NextCurrentStopDepartureProtocolProblem
    {
        get => Volatile.Read(ref _nextCurrentStopDepartureProtocolProblem);
        set => Volatile.Write(ref _nextCurrentStopDepartureProtocolProblem, value);
    }

    private string? _nextCurrentStopDepartureProtocolProblem;

    /// <summary>Every departure request payload received, in order, resends included.</summary>
    public IReadOnlyList<JsonElement> CurrentStopDepartureRequests => [.. _currentStopDepartureRequests];

    /// <summary>
    /// A <c>CurrentStopDepartureResult</c> the test composes, on the latest session, under a new messageId.
    /// </summary>
    public Task SendCurrentStopDepartureResultAsync(
        string departureRequestId,
        string outcome,
        string? reasonCode,
        long currentWorklistRevision,
        string? messageId = null) =>
        SendCommandAsync(
            "CurrentStopDepartureResult",
            messageId ?? Guid.NewGuid().ToString("D"),
            DepartureResult(departureRequestId, outcome, reasonCode, currentWorklistRevision));

    private static object DepartureResult(
        string departureRequestId,
        string outcome,
        string? reasonCode,
        long currentWorklistRevision) =>
        new
        {
            departureRequestId,
            outcome,
            problem = reasonCode is null
                ? null
                : new WireToGateProblemPayload(reasonCode, null, null),
            currentWorklistRevision
        };

    private async Task HandleCurrentStopDepartureRequestedAsync(ConnectionContext context, JsonElement root)
    {
        JsonElement payload = root.GetProperty("payload").Clone();
        _currentStopDepartureRequests.Enqueue(payload);
        if (TryConsume(ref _currentStopDepartureAcksToDrop))
        {
            return;
        }

        if (Interlocked.Exchange(ref _nextCurrentStopDepartureProtocolProblem, null) is { } problem)
        {
            await WriteEnvelopeAsync(
                context,
                CreateProtocolProblem(
                    context,
                    root.GetProperty("messageId").GetString()!,
                    "CurrentStopDepartureRequested",
                    problem)).ConfigureAwait(false);
            return;
        }

        await WriteEnvelopeAsync(context, CreateDurableAck(context, root)).ConfigureAwait(false);
        if (!RespondToCurrentStopDepartures)
        {
            return;
        }

        string departureRequestId = payload.GetProperty("departureRequestId").GetString()!;
        string content = payload.GetRawText();
        if (_currentStopDepartureResults.TryGetValue(departureRequestId, out (string Content, object Result) first))
        {
            if (!string.Equals(first.Content, content, StringComparison.Ordinal))
            {
                await WriteEnvelopeAsync(
                    context,
                    CreateProtocolProblem(
                        context,
                        root.GetProperty("messageId").GetString()!,
                        "CurrentStopDepartureRequested",
                        "BUSINESS_ID_CONTENT_CONFLICT")).ConfigureAwait(false);
                return;
            }

            await SendCommandAsync("CurrentStopDepartureResult", Guid.NewGuid().ToString("D"), first.Result)
                .ConfigureAwait(false);
            return;
        }

        string outcome = RejectCurrentStopDeparturesWith is null ? "ACCEPTED" : "REJECTED";
        object result = DepartureResult(
            departureRequestId,
            outcome,
            RejectCurrentStopDeparturesWith,
            CurrentStopDepartureCurrentWorklistRevision ?? payload.GetProperty("worklistRevision").GetInt64());
        _currentStopDepartureResults[departureRequestId] = (content, result);
        await SendCommandAsync("CurrentStopDepartureResult", Guid.NewGuid().ToString("D"), result).ConfigureAwait(false);
        if (outcome == "ACCEPTED")
        {
            foreach ((string messageType, object snapshot) in SnapshotsAfterAcceptedDeparture)
            {
                await SendJourneySnapshotAsync(messageType, snapshot).ConfigureAwait(false);
            }
        }
    }
}
