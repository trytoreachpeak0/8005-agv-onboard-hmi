namespace SQCD.Agv.Core;

/// <summary>
/// One press of 「剩下的不装」 on a multi-basket load (8005-agv-onboard-hmi#388, CP-0021 REQ-0367, protocol 6.0.0 candidate),
/// as the journal holds it from before <c>RemainingBasketsSkipRequested</c> is sent until the attempt it belongs to settles.
/// </summary>
/// <remarks>
/// <para>
/// <b>Journaled before it is sent</b> (<c>JOURNAL_SKIP_REQUEST_BEFORE_SENDING</c>), and every send of it is built from these
/// fields, so the payload is the same whichever send it is. The messageId is not kept here: a transport retry inside one
/// session generation sends the very line again, and a resubmission after a reconnect or a restart is a new messageId with
/// the same <see cref="SkipRequestId"/> and payload (protocol wire notes, <c>RemainingBasketsSkipRequested</c>).
/// </para>
/// <para>
/// <b>Three ends.</b> <see cref="Result"/> is the server's answer once journaled; <see cref="DroppedReason"/> says the request
/// ended without effect before one came -- a <c>ProtocolProblem</c> answered it, or the operation ended another way -- so a
/// late <c>AUTHORIZED</c> is journaled and never acted on. While both are <c>null</c> the request is pending: no slot opens and
/// it is resent.
/// </para>
/// </remarks>
/// <param name="LoadedSlots">The target slots that had closed safely <c>OCCUPIED</c> at the press, ascending; at least one.</param>
/// <param name="ExpectedBasketCount">The command's N, kept for the log and the screen.</param>
/// <param name="Operator">The operator when the vehicle knew one at the press, otherwise <c>null</c>; never a reason to refuse.</param>
public sealed record WireToGateRemainingBasketsSkip(
    string SkipRequestId,
    string DemandId,
    string OperationSessionId,
    string SlotOperationAttemptId,
    IReadOnlyList<int> LoadedSlots,
    int ExpectedBasketCount,
    WireToGateRemainingBasketsSkipOperator? Operator,
    DateTimeOffset RequestedAt)
{
    /// <summary>The server's <c>RemainingBasketsSkipResult</c>, once journaled.</summary>
    public WireToGateRemainingBasketsSkipOutcome? Result { get; init; }

    /// <summary>Why the request ended without effect before an answer came, or <c>null</c>.</summary>
    public string? DroppedReason { get; init; }

    /// <summary>Sent or about to be, and neither answered nor dropped.</summary>
    public bool IsPending => Result is null && DroppedReason is null;

    /// <summary>Answered <c>AUTHORIZED</c> while it was still pending: the one case a skip may be reported.</summary>
    public bool IsAuthorized => Result?.Outcome == WireToGateRemainingBasketsSkipOutcome.Authorized && DroppedReason is null;
}

/// <summary>The operator the request names: the session operator the vehicle knew at the press.</summary>
public sealed record WireToGateRemainingBasketsSkipOperator(
    string OperatorId,
    string VerificationMethod,
    DateTimeOffset VerifiedAt);

/// <summary>The server's <c>RemainingBasketsSkipResult</c> as journaled, before the vehicle acknowledges it.</summary>
/// <param name="ResultMessageId">The result's own <c>messageId</c>, which the <c>DurableAck</c> names.</param>
/// <param name="LoadedSlots">The authorized slots on <c>AUTHORIZED</c>; empty on <c>REJECTED</c>.</param>
/// <param name="ReasonCode"><c>null</c> on <c>AUTHORIZED</c>; the problem's <c>reasonCode</c> on <c>REJECTED</c>.</param>
public sealed record WireToGateRemainingBasketsSkipOutcome(
    string ResultMessageId,
    string ResultContentSha256,
    string Outcome,
    IReadOnlyList<int> LoadedSlots,
    string? ReasonCode,
    DateTimeOffset ReceivedAt)
{
    public const string Authorized = "AUTHORIZED";

    public const string Rejected = "REJECTED";
}

/// <summary>
/// The server's <c>RemainingBasketsSkipResult</c>, checked against its schema and handed to the business service, which
/// journals it and then acknowledges it (<c>durableBeforeAck</c>).
/// </summary>
/// <param name="ContentSha256">The received envelope's content hash, which the <c>DurableAck</c> carries.</param>
public sealed record WireToGateRemainingBasketsSkipResult(
    string MessageId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string SkipRequestId,
    string DemandId,
    string SlotOperationAttemptId,
    string Outcome,
    IReadOnlyList<int> LoadedSlots,
    string? ReasonCode,
    string ContentSha256);

/// <summary>
/// What the executor offers 「剩下的不装」 over right now (<c>WireToGateSlotOperationExecutor.RemainingBasketsSkipOffer</c>): a
/// multi-basket load held shut on <see cref="HeldSlot"/> for 「重新开门」, with <see cref="LoadedSlots"/> closed safely
/// <c>OCCUPIED</c>, at least one slot still to go, and every target door read locked with its output reset.
/// </summary>
public sealed record WireToGateRemainingBasketsSkipOffer(
    string SlotOperationAttemptId,
    string DemandId,
    string OperationSessionId,
    int ExpectedBasketCount,
    IReadOnlyList<int> LoadedSlots,
    int HeldSlot)
{
    /// <summary>Compared by content: an offer read twice over the same hold is the same offer.</summary>
    public bool Equals(WireToGateRemainingBasketsSkipOffer? other) =>
        other is not null
        && SlotOperationAttemptId == other.SlotOperationAttemptId
        && DemandId == other.DemandId
        && OperationSessionId == other.OperationSessionId
        && ExpectedBasketCount == other.ExpectedBasketCount
        && HeldSlot == other.HeldSlot
        && LoadedSlots.SequenceEqual(other.LoadedSlots);

    public override int GetHashCode() =>
        HashCode.Combine(SlotOperationAttemptId, ExpectedBasketCount, HeldSlot, LoadedSlots.Count);
}
