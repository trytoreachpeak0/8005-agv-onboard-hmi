using System.Collections.Concurrent;
using System.Text.Json;
using SQCD.Agv.Contracts;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The control server's half of 「剩下的不装」, as far as these tests need it (protocol 6.0.0 candidate wire notes,
/// <c>8005-agv-onboard-hmi#388</c>; the real half is <c>8005-agv-control-server#830</c>).
/// </summary>
/// <remarks>
/// Every request is acknowledged with a <c>DurableAck</c> (unless a test drops it or answers it with a
/// <c>ProtocolProblem</c>) and recorded with its messageId. With <see cref="RespondToRemainingBasketsSkips"/> set it is
/// answered with a <c>RemainingBasketsSkipResult</c>: <c>AUTHORIZED</c> over the request's slots, or <c>REJECTED</c> with
/// <see cref="RejectRemainingBasketsSkipsWith"/>; the stored result replayed for the same <c>skipRequestId</c> with the same
/// content, <c>BUSINESS_ID_CONTENT_CONFLICT</c> for different content. Which outcome comes back is the test's to say.
/// </remarks>
public sealed partial class FakeControlServer
{
    private readonly ConcurrentDictionary<string, (string Content, string MessageId, object Result)> _remainingBasketsSkipResults =
        new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<(string MessageId, JsonElement Payload)> _remainingBasketsSkipRequests = new();

    private int _remainingBasketsSkipAcksToDrop;

    private string? _nextRemainingBasketsSkipProtocolProblem;

    /// <summary>The slots of the <c>SlotOperationCommand</c> after recovery, ascending; the basket count is their number.</summary>
    public IReadOnlyList<int> SlotOperationSlots { get; set; } = SingleSlot;

    /// <summary>Answers each skip request with a result; left false, only the <c>DurableAck</c> goes back.</summary>
    public bool RespondToRemainingBasketsSkips { get; set; }

    /// <summary>The reason a new skip request is refused with; <c>null</c> authorizes it.</summary>
    public string? RejectRemainingBasketsSkipsWith { get; set; }

    /// <summary>How many skip requests to take without a word -- no ack, no result -- as a link that dropped would.</summary>
    public int RemainingBasketsSkipAcksToDrop
    {
        get => Volatile.Read(ref _remainingBasketsSkipAcksToDrop);
        set => Volatile.Write(ref _remainingBasketsSkipAcksToDrop, value);
    }

    /// <summary>The reason the next skip request is answered with a <c>ProtocolProblem</c>, once; <c>null</c> answers normally.</summary>
    public string? NextRemainingBasketsSkipProtocolProblem
    {
        get => Volatile.Read(ref _nextRemainingBasketsSkipProtocolProblem);
        set => Volatile.Write(ref _nextRemainingBasketsSkipProtocolProblem, value);
    }

    /// <summary>Every skip request received, in order, with the messageId it came under; resends included.</summary>
    public IReadOnlyList<(string MessageId, JsonElement Payload)> RemainingBasketsSkipRequests => [.. _remainingBasketsSkipRequests];

    /// <summary>A <c>RemainingBasketsSkipResult</c> the test composes, on the latest session.</summary>
    public Task SendRemainingBasketsSkipResultAsync(
        string skipRequestId,
        string outcome,
        IReadOnlyList<int> loadedSlots,
        string? reasonCode,
        string? messageId = null) =>
        SendCommandAsync(
            "RemainingBasketsSkipResult",
            messageId ?? Guid.NewGuid().ToString("D"),
            SkipResult(
                skipRequestId,
                "11111111-1111-1111-1111-111111111111",
                CommandSlotOperationAttemptId,
                outcome,
                loadedSlots,
                reasonCode));

    private static object SkipResult(
        string skipRequestId,
        string demandId,
        string slotOperationAttemptId,
        string outcome,
        IReadOnlyList<int> loadedSlots,
        string? reasonCode) =>
        new
        {
            skipRequestId,
            demandId,
            slotOperationAttemptId,
            outcome,
            loadedSlots = outcome == "AUTHORIZED" ? loadedSlots.ToArray() : [],
            problem = reasonCode is null ? null : new WireToGateProblemPayload(reasonCode, null, null)
        };

    private async Task HandleRemainingBasketsSkipRequestedAsync(ConnectionContext context, JsonElement root)
    {
        JsonElement payload = root.GetProperty("payload").Clone();
        string requestMessageId = root.GetProperty("messageId").GetString()!;
        _remainingBasketsSkipRequests.Enqueue((requestMessageId, payload));
        if (TryConsume(ref _remainingBasketsSkipAcksToDrop))
        {
            return;
        }

        if (Interlocked.Exchange(ref _nextRemainingBasketsSkipProtocolProblem, null) is { } problem)
        {
            await WriteEnvelopeAsync(
                context,
                CreateProtocolProblem(context, requestMessageId, "RemainingBasketsSkipRequested", problem)).ConfigureAwait(false);
            return;
        }

        string skipRequestId = payload.GetProperty("skipRequestId").GetString()!;
        string content = payload.GetRawText();
        if (_remainingBasketsSkipResults.TryGetValue(skipRequestId, out (string Content, string MessageId, object Result) first)
            && !string.Equals(first.Content, content, StringComparison.Ordinal))
        {
            await WriteEnvelopeAsync(
                context,
                CreateProtocolProblem(context, requestMessageId, "RemainingBasketsSkipRequested", "BUSINESS_ID_CONTENT_CONFLICT"))
                .ConfigureAwait(false);
            return;
        }

        await WriteEnvelopeAsync(context, CreateDurableAck(context, root)).ConfigureAwait(false);
        if (!RespondToRemainingBasketsSkips)
        {
            return;
        }

        if (first.Result is not null)
        {
            // The stored result replayed exactly as first decided, never recomputed.
            await SendCommandAsync("RemainingBasketsSkipResult", first.MessageId, first.Result).ConfigureAwait(false);
            return;
        }

        string outcome = RejectRemainingBasketsSkipsWith is null ? "AUTHORIZED" : "REJECTED";
        object result = SkipResult(
            skipRequestId,
            payload.GetProperty("demandId").GetString()!,
            payload.GetProperty("slotOperationAttemptId").GetString()!,
            outcome,
            [.. payload.GetProperty("loadedSlots").EnumerateArray().Select(slot => slot.GetInt32())],
            RejectRemainingBasketsSkipsWith);
        string resultMessageId = Guid.NewGuid().ToString("D");
        _remainingBasketsSkipResults[skipRequestId] = (content, resultMessageId, result);
        await SendCommandAsync("RemainingBasketsSkipResult", resultMessageId, result).ConfigureAwait(false);
    }
}
