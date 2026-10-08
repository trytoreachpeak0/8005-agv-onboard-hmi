using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SQCD.Agv.Core;

public enum WireToGateRecoveryCheckpoint
{
    None,
    Prepared,
    ActiveUnlockSet,
    SafeFinishReached,
    ResultRecorded
}

public enum WireToGateSessionReadiness
{
    Disconnected,
    Recovering,
    Ready,
    RecoveryRequired
}

public sealed record WireToGatePendingResult(
    string MessageType,
    string MessageId,
    string BusinessId,
    string ContentSha256);

public static class WireToGateRecoveryVectorTypes
{
    public const string LoadCancellation = "LOAD_CANCELLATION";
    public const string LoadCompensation = "LOAD_COMPENSATION";
    public const string LoadCorrection = "LOAD_CORRECTION";
    public const string FaultCargoHandoff = "FAULT_CARGO_HANDOFF";
    public const string ForcedMechanicalRecovery = "FORCED_MECHANICAL_RECOVERY";

    public static bool IsKnown(string value) => value is
        LoadCancellation
        or LoadCompensation
        or LoadCorrection
        or FaultCargoHandoff
        or ForcedMechanicalRecovery;

    /// <summary>
    /// Whether <paramref name="vector"/> is a load cancellation authorized before any sublot was
    /// entered (ADR-cross-0046, first case; 批次5-27, onboard-hmi#76): no slot operation, no recovery
    /// session, and an empty slot set.
    /// </summary>
    /// <remarks>
    /// It is the one vector allowed an empty slot set. Nothing was commanded, so there is no slot to
    /// prove empty and the vehicle's whole report is <c>ALL_EMPTY</c> with no slot results. Every
    /// other vector exists to put named slots into a proven state, and an empty set there would be a
    /// vector that proves nothing -- which is why the rule is this shape and not a lower bound of 0.
    /// </remarks>
    public static bool IsLoadCancellationBeforeSublot(WireToGateRecoveryVectorContext vector) =>
        vector.VectorType == LoadCancellation
        && vector.SlotOperationAttemptId is null
        && vector.ExceptionRecoverySessionId is null
        && vector.HandoffId is null
        && vector.Slots is { Count: 0 };
}

public static class WireToGateRecoveryCommandHash
{
    public static string Compute(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts))))
            .ToLowerInvariant();

    public static string ForLoadCompensation(
        string recoveryActionId,
        string demandId,
        string slotOperationAttemptId,
        IReadOnlyList<int> slots) =>
        Compute(
            recoveryActionId,
            demandId,
            slotOperationAttemptId,
            JsonSerializer.Serialize(slots));

    public static string ForLoadCorrection(
        string correctionId,
        string demandId,
        string slotOperationAttemptId,
        IReadOnlyList<int> slots) =>
        Compute(
            correctionId,
            demandId,
            slotOperationAttemptId,
            JsonSerializer.Serialize(slots));

    public static string ForRecoveryAction(
        string recoveryActionId,
        string demandId,
        string slotOperationAttemptId,
        IReadOnlyList<int> slots,
        long forcedRecoveryGeneration) =>
        Compute(
            recoveryActionId,
            demandId,
            slotOperationAttemptId,
            JsonSerializer.Serialize(slots),
            forcedRecoveryGeneration.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// Durable identity for a recovery vector.  The command and result are bound to
/// this exact scope; a reconnect may replay the result, but it may not invent a
/// different demand, attempt, handoff, or slot set.
/// </summary>
public sealed record WireToGateRecoveryVectorContext(
    string VectorType,
    string PrimaryId,
    string? ExceptionRecoverySessionId,
    string DemandId,
    string? SlotOperationAttemptId,
    string? HandoffId,
    IReadOnlyList<int> Slots,
    string? CommandContentSha256,
    string? OperatorId,
    string? OperatorVerificationMethod,
    DateTimeOffset? OperatorVerifiedAt)
{
    /// <summary>
    /// The <c>forcedRecoveryGeneration</c> the authorizing command carried, for
    /// <see cref="WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery"/> only; null for every
    /// other vector, none of which is fenced by generation.
    /// </summary>
    /// <remarks>
    /// It is durable here rather than read back from
    /// <see cref="WireToGateRecoveryState.ForcedRecoveryGeneration"/> at send time because the
    /// result is <c>durableBeforeSend</c>: a replay after a later generation arrives must still
    /// report the generation the command it answers was issued under, byte for byte.
    /// </remarks>
    public long? ForcedRecoveryGeneration { get; init; }
}

public sealed record WireToGateRecoveryVectorExecutionResult(
    string VectorType,
    string PrimaryId,
    string? ExceptionRecoverySessionId,
    string DemandId,
    string? SlotOperationAttemptId,
    string? HandoffId,
    string OverallOutcome,
    IReadOnlyList<WireToGateSlotExecutionResult> SlotResults,
    DateTimeOffset ObservedAt,
    string JournalCheckpoint);

/// <summary>
/// The immutable operation identity needed to resume a physical operation after
/// a process or connection interruption.  This is deliberately a copy of the
/// server command's business fields; the original command is never reconstructed
/// from the current screen or from a newly selected slot set.
/// </summary>
public sealed record WireToGateRecoveryOperationContext(
    string MessageId,
    string? CorrelationId,
    long SessionGeneration,
    DateTimeOffset SentAt,
    string DemandId,
    string OperationSessionId,
    string SlotOperationAttemptId,
    OperationType OperationType,
    IReadOnlyList<int> Slots,
    int ExpectedBasketCount,
    bool ExpectedOccupied,
    string CommandContentSha256)
{
    public static WireToGateRecoveryOperationContext FromCommand(
        WireToGateSlotOperationCommand command) =>
        new(
            command.MessageId,
            command.CorrelationId,
            command.SessionGeneration,
            command.SentAt,
            command.DemandId,
            command.OperationSessionId,
            command.SlotOperationAttemptId,
            command.OperationType,
            command.Slots.ToArray(),
            command.ExpectedBasketCount,
            command.ExpectedOccupied,
            command.CommandContentSha256);

    public WireToGateSlotOperationCommand ToCommand() =>
        new(
            MessageId,
            CorrelationId,
            SessionGeneration,
            SentAt,
            DemandId,
            OperationSessionId,
            SlotOperationAttemptId,
            OperationType,
            Slots,
            ExpectedBasketCount,
            ExpectedOccupied,
            CommandContentSha256);
}

/// <summary>
/// One demand's cargo on board, as <see cref="WireToGateRecoveryState.LoadsOnBoard"/> keeps it (8005-agv-onboard-hmi#209).
/// </summary>
/// <param name="Load">The recorded load of the demand: the subject a handoff or a forced recovery is opened for.</param>
public sealed record WireToGateLoadOnBoard(WireToGateRecoveryOperationContext Load)
{
    /// <summary>
    /// The anchor demand of the journey the load was made in, as the plan names it; <c>null</c> until a plan naming exactly
    /// one demand has been seen (<see cref="WireToGateRecoveryState.WithLoadsOnBoardFor"/>).
    /// </summary>
    public string? JourneyAnchorDemandId { get; init; }

    /// <summary>
    /// A recovery that ends the demand was acknowledged over it -- a handoff, a forced recovery, a compensation, a
    /// cancellation -- and only the server knows whether that result reconciled. The load stays a subject: if it did not, a
    /// second handoff is the way out; if it did, the server refuses the session.
    /// </summary>
    public bool HandedOffAwaitingServer { get; init; }

    /// <summary>The demand this load is for. Not serialized: it is <see cref="Load"/>'s.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DemandId => Load.DemandId;
}

public sealed record WireToGateRecoveryState(
    string? UnsettledSlotOperationAttemptId,
    WireToGateRecoveryCheckpoint ProvenRecoveryCheckpoint,
    IReadOnlyList<int> ActiveUnlockSlots,
    long ForcedRecoveryGeneration,
    IReadOnlyList<WireToGatePendingResult> PendingResults)
{
    /// <summary>
    /// Optional for backward compatibility with journals created before resume
    /// context was introduced.  A missing context must be treated as a hard
    /// recovery block by the business layer; it must never be guessed.
    /// </summary>
    public WireToGateRecoveryOperationContext? OperationContext { get; init; }

    public IReadOnlyList<int> CompletedSlots { get; init; } = [];

    public IReadOnlyList<WireToGateSlotExecutionResult> SlotResults { get; init; } = [];

    /// <summary>
    /// The authenticated recovery identifiers are persisted so a reconnect or
    /// process restart cannot silently accept a different recovery action.
    /// Authentication proof itself is intentionally never persisted.
    /// </summary>
    public string? ExceptionRecoverySessionId { get; init; }

    public string? RecoveryActionId { get; init; }

    public string? RecoverySessionRequestId { get; init; }

    public string? RecoveryActionRequestId { get; init; }

    public string? RecoveryReason { get; init; }

    public string? RecoveryOperatorId { get; init; }

    public DateTimeOffset? RecoveryOperatorVerifiedAt { get; init; }

    public WireToGateRecoveryVectorContext? RecoveryVector { get; init; }

    /// <summary>
    /// Stable observation time for the recovery result currently being
    /// reported.  Keeping it in the journal makes a retry byte-for-byte
    /// identical to the first durable send.
    /// </summary>
    public DateTimeOffset? RecoveryResultObservedAt { get; init; }

    /// <summary>
    /// The last successfully recorded LOAD command is retained for the bounded
    /// post-commit correction entry. It is never used to authorize a new load.
    /// </summary>
    public WireToGateRecoveryOperationContext? LastCompletedLoadOperationContext { get; init; }

    /// <summary>
    /// One recorded LOAD per demand whose cargo may still be on board: the subjects a fault cargo handoff and a forced
    /// mechanical recovery choose from once nothing is armed (8005-agv-onboard-hmi#209).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A recorded load enters it (a later load of the same demand replaces the earlier one). A recovery vector the server
    /// acknowledged as ending the demand -- a handoff, a forced recovery, a compensation, a cancellation -- does <b>not</b> take
    /// it out: it marks it <see cref="WireToGateLoadOnBoard.HandedOffAwaitingServer"/>. The vehicle cannot tell whether the
    /// server reconciled that result: the session closes either way, and a result that did not reconcile keeps the demand, which
    /// is then handed off again (control-server <c>AdvanceSessionAfterResultAsync</c>, <c>PrepareCargoHandoffAsync</c>). The
    /// server refuses a session over a demand it ended (control-server#505, A3), so a second press there is answered, not
    /// carried out.
    /// </para>
    /// <para>
    /// What does take it out is the server's or the vehicle's own fact that the cargo is gone: a recorded unload of the demand,
    /// the journey's closure (a business state whose purpose is no longer a transport), or a plan that no longer carries the
    /// journey the load was made in.
    /// </para>
    /// <para>
    /// <c>null</c> is a journal written before this field existed: the subject is then
    /// <see cref="LastCompletedLoadOperationContext"/>, as it always was. A version without this field reads the journal and
    /// ignores it, and its next write drops it -- back to only the last load being offered.
    /// </para>
    /// </remarks>
    public IReadOnlyList<WireToGateLoadOnBoard>? LoadsOnBoard { get; init; }

    /// <summary>
    /// The loads a handoff or a forced recovery may be about: <see cref="LoadsOnBoard"/>, or for a journal that predates it,
    /// the last completed load alone. A method, not a property: the journal serializes every public property.
    /// </summary>
    public IReadOnlyList<WireToGateLoadOnBoard> SettledLoadSubjects() =>
        LoadsOnBoard
        ?? (LastCompletedLoadOperationContext is { } last ? [new WireToGateLoadOnBoard(last)] : []);

    /// <summary>The state with <paramref name="load"/> recorded as its demand's load on board.</summary>
    /// <remarks>
    /// A journal that predates the list starts it empty rather than from its last completed load: that load may belong to a
    /// journey long delivered, and seeded here it would stand beside the new one as a second subject.
    /// </remarks>
    public WireToGateRecoveryState WithLoadOnBoard(WireToGateRecoveryOperationContext load) =>
        this with
        {
            LoadsOnBoard =
            [
                .. (LoadsOnBoard ?? []).Where(item => !string.Equals(item.DemandId, load.DemandId, StringComparison.Ordinal)),
                new WireToGateLoadOnBoard(load)
            ]
        };

    /// <summary>The state with <paramref name="demandId"/>'s load no longer on board: its unload was recorded.</summary>
    public WireToGateRecoveryState WithoutLoadOnBoard(string demandId) =>
        this with
        {
            LoadsOnBoard =
                [.. SettledLoadSubjects().Where(item => !string.Equals(item.DemandId, demandId, StringComparison.Ordinal))]
        };

    /// <summary>
    /// The state with <paramref name="demandId"/>'s load marked as handed off, awaiting the server: still on the list and still
    /// a subject, because the vehicle cannot tell a result the server reconciled from one it did not.
    /// </summary>
    public WireToGateRecoveryState WithLoadHandedOff(string demandId) =>
        this with
        {
            LoadsOnBoard =
            [
                .. SettledLoadSubjects().Select(item => string.Equals(item.DemandId, demandId, StringComparison.Ordinal)
                    ? item with { HandedOffAwaitingServer = true }
                    : item)
            ]
        };

    /// <summary>
    /// The state with the loads on board brought in line with the journey the server describes now:
    /// <list type="bullet">
    /// <item>a business state whose purpose is not a transport -- the journey's closure, or a journey of another kind -- empties
    /// the list;</item>
    /// <item>a plan whose legs name exactly one demand names the journey's anchor (control-server <c>JourneyPlanBuilder.Plan</c>
    /// puts the anchor on every leg), and loads not yet stamped with an anchor are stamped with it;</item>
    /// <item>a plan whose legs name demands drops the loads stamped with an anchor it does not name: they were made in another
    /// journey, whose closure this vehicle missed.</item>
    /// </list>
    /// The anchor is written once, when the server accepts the journey, and never changes in its life (control-server
    /// <c>WireToGateStore</c> is the only writer of <c>JourneyRuntimeRow.DemandId</c>). The same demand may anchor a later journey
    /// after a redispatch, so an anchor that repeats is not proof of the same journey; that journey's closure clears the list
    /// in between, and a missed closure followed by a redispatch of the same anchor is the one shape this leaves on the list.
    /// </summary>
    public WireToGateRecoveryState WithLoadsOnBoardFor(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        if (LoadsOnBoard is not { Count: > 0 } loads)
        {
            return this;
        }

        if (journey.VehicleBusinessState is { } business
            && !string.Equals(business.ActivePurpose, TransportPurpose, StringComparison.Ordinal))
        {
            return this with { LoadsOnBoard = [] };
        }

        IReadOnlyList<string> planned = journey.UpcomingStopPlan?.DemandIds ?? [];
        if (planned.Count == 0)
        {
            return this;
        }

        string? anchor = planned.Count == 1 ? planned[0] : null;
        WireToGateLoadOnBoard[] kept =
        [
            .. loads
                .Select(item => item.JourneyAnchorDemandId is null && anchor is not null
                    ? item with { JourneyAnchorDemandId = anchor }
                    : item)
                .Where(item => item.JourneyAnchorDemandId is null
                    || planned.Contains(item.JourneyAnchorDemandId, StringComparer.Ordinal))
        ];
        return kept.SequenceEqual(loads) ? this : this with { LoadsOnBoard = kept };
    }

    private const string TransportPurpose = "TRANSPORT";

    /// <summary>
    /// A load cancellation that went out and has had no answer yet. The server keeps an
    /// authorization under the cancellationId and compares every later request's whole payload
    /// with the first, so a retry must repeat this operator and reason -- verifiedAt included --
    /// rather than take the retrying press's. A refusal leaves nothing on the server, so the entry
    /// goes as soon as either answer arrives. It also goes wherever the recovery state is reset -- a
    /// settled operation, a completed vector, a new slot operation -- and is only ever reused for the
    /// same cancellationId, so one left behind cannot speak for another cancellation.
    /// </summary>
    public WireToGatePendingLoadCancellation? PendingLoadCancellation { get; init; }

    /// <summary>
    /// The device half of a forced mechanical recovery the server acknowledged as
    /// <c>MECHANICALLY_ISOLATED</c> (REQ-0241, REQ-0242, onboard-hmi#107). The business half settled at
    /// that acknowledgement, so this outlives the attempt, the operation context and the recovery
    /// session fields, and every path that resets those carries it over. Only an acknowledged hardware
    /// recovery record over live readings clears it.
    /// </summary>
    public WireToGateForcedIsolation? ForcedIsolation { get; init; }

    /// <summary>
    /// The unsettled attempt a recovery vector held when that vector was forgotten on a non-completed result, or ended
    /// after a maintainer's manual check (8005-agv-onboard-hmi#278). The vector is gone, the attempt, its context and the
    /// active unlock set stay -- and nothing else on file says that a vector took the attempt over: the vector results
    /// are keyed by their own action ids. A COMPLETED result of the attempt acknowledged after that is the attempt's own
    /// account from before the vector, not a settlement of it, and recording it as one would write the active unlock set
    /// empty over the doors the vector left in doubt. Compared by attempt id, so a marker naming another attempt says
    /// nothing; cleared when the attempt settles.
    /// </summary>
    public string? TakenOverSlotOperationAttemptId { get; init; }

    public static WireToGateRecoveryState Empty { get; } = new(
        null,
        WireToGateRecoveryCheckpoint.None,
        [],
        0,
        []);
}

/// <param name="SlotOperationAttemptId">
/// The load operation being cancelled, or <c>null</c> for a cancellation raised before any load was
/// commanded -- the cancellation before any sublot is entered, 批次5-27 (onboard-hmi#76).
/// </param>
/// <summary>
/// Slots a qualified person opened by hand after the power was cut (REQ-0241): their physical state is
/// unknown, so no slot operation may touch them, until a hardware recovery record for the whole set is
/// recorded by the server and the live readings of every slot in it are valid again.
/// </summary>
/// <param name="ExceptionRecoverySessionId">The session the forced recovery ran in.</param>
/// <param name="RecoveryActionId">The forced recovery action the hardware record answers.</param>
/// <param name="PhysicallyUnknownSlots">The forced recovery's whole slot set, ascending.</param>
public sealed record WireToGateForcedIsolation(
    string ExceptionRecoverySessionId,
    string RecoveryActionId,
    IReadOnlyList<int> PhysicallyUnknownSlots)
{
    /// <summary>
    /// A hardware recovery record that went out and has had no answer yet. A retry repeats it
    /// field for field under a new messageId, for the reason <see cref="WireToGatePendingLoadCancellation"/>
    /// does: the server keeps the first content under the recordId.
    /// </summary>
    public WireToGatePendingHardwareRecoveryRecord? PendingRecord { get; init; }
}

public sealed record WireToGatePendingHardwareRecoveryRecord(
    string RecordId,
    string OperatorId,
    string OperatorVerificationMethod,
    DateTimeOffset OperatorVerifiedAt,
    string AdministratorRole,
    string Observations,
    DateTimeOffset ObservedAt);

public sealed record WireToGatePendingLoadCancellation(
    string CancellationId,
    string? SlotOperationAttemptId,
    string OperatorId,
    string OperatorVerificationMethod,
    DateTimeOffset OperatorVerifiedAt,
    string Reason);

public sealed record WireToGateDurableMessage(
    string DeduplicationKey,
    string MessageType,
    string MessageId,
    string ContentSha256,
    string WireLine,
    DateTimeOffset CreatedAt,
    bool Acknowledged,
    string? AbandonedReasonCode = null)
{
    /// <summary>
    /// Given up because the server refused it with a <c>MANUAL_REVIEW</c> code (onboard-hmi#254): it is owed to nobody
    /// any more and is never sent again, by any path. Not the same as <see cref="Acknowledged"/>: the server took
    /// something else under this identity, not this content, so nothing that waits for this message's acknowledgement
    /// may read it as delivered.
    /// </summary>
    public bool Abandoned => AbandonedReasonCode is not null;
}

/// <summary>
/// Durable adoption record for a server-owned journey snapshot.  The raw payload
/// is retained so the projection can be rebuilt after an onboard restart without
/// treating a stale session generation as a live wire message. ContentSha256 is
/// the hash of the received envelope used by SnapshotAppliedAck; revision identity
/// is derived independently from the retained payload.
/// </summary>
public sealed record WireToGateAppliedJourneySnapshot(
    string MessageType,
    string MessageId,
    long Revision,
    string ContentSha256,
    string PayloadJson,
    DateTimeOffset AppliedAt);

public interface IWireToGateJournal : IAsyncDisposable
{
    public Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the UUID created with this journal.  It remains stable when the
    /// same journal is reopened and differs for every newly-created journal, so
    /// durable message identities cannot collide merely because business
    /// revisions restart from the same server baseline.
    /// </summary>
    public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default);

    public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the recovery state, applies <paramref name="change"/> and writes what it returns, as one
    /// step against every other read and write of this journal; <c>null</c> from
    /// <paramref name="change"/> writes nothing. Returns what was written, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a writer whose decision rests on what it read. A separate read and write lets another
    /// writer land in between, and the second write then puts the stale read back over it
    /// (onboard-hmi#123 review A: a CLOSED snapshot's release rolling a recorded result back).
    /// </para>
    /// <para>
    /// <b>This is the only way to write the recovery state.</b> There was a
    /// <c>WriteRecoveryStateAsync</c> taking a whole record until onboard-hmi#136, and every one of its
    /// callers had read the state, decided outside the lock and written the whole thing back -- so each
    /// of them silently undid whatever landed in between, with nothing thrown and nothing logged. A
    /// change function that ignores <paramref name="change"/>'s argument is the same fault wearing this
    /// method's name; <c>RecoveryStateWriteFunnelArchitectureTests</c> holds both lines.
    /// </para>
    /// </remarks>
    public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
        Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="UpdateRecoveryStateAsync(Func{WireToGateRecoveryState, WireToGateRecoveryState?}, CancellationToken)"/>,
    /// and hands the state the journal holds once the step is done -- what was written, or what was read
    /// when <paramref name="change"/> wrote nothing -- to <paramref name="settled"/> before the step lets
    /// any other read or write in. Not called when the step fails.
    /// </summary>
    /// <remarks>
    /// For a copy of the state kept outside the journal. Updated after the step, a copy of an earlier
    /// step can land over the copy of a later one and show the older state until the next read
    /// (onboard-hmi#129). Updated inside it, copies are ordered exactly like the journal's steps.
    /// </remarks>
    public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
        Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
        Action<WireToGateRecoveryState> settled,
        CancellationToken cancellationToken = default);

    public Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
        WireToGateDurableMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces the wire representation of an unacknowledged durable
    /// message before replaying it on a new connection.  The business identity and
    /// payload stay the same; only transport fields such as session generation may
    /// be rebound.  The expected row is checked so a concurrent acknowledgement or
    /// content change cannot silently overwrite a newer journal state.
    /// </summary>
    public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
        WireToGateDurableMessage expected,
        WireToGateDurableMessage replacement,
        CancellationToken cancellationToken = default);

    public Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
        string deduplicationKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a durable message by its wire identity. A pending result is recorded by the message it
    /// was sent as, not by the business key that produced it.
    /// </summary>
    public Task<WireToGateDurableMessage?> ReadOutgoingByMessageIdAsync(
        string messageId,
        CancellationToken cancellationToken = default);

    public Task MarkOutgoingAcknowledgedAsync(
        string messageId,
        string acceptedContentSha256,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the row on file under <paramref name="messageId"/> with this content as given up for
    /// <paramref name="reasonCode"/>, a protocol code whose <c>retryDisposition</c> is <c>MANUAL_REVIEW</c>
    /// (onboard-hmi#254). Nothing else in the row changes: not its content, not its messageId, not whether it was
    /// acknowledged. A row already given up keeps its first reason. Returns the row as it now stands.
    /// </summary>
    /// <remarks>
    /// Throws <c>InvalidDataException("DURABLE_OUTBOX_ROW_MISSING")</c> when no row has this messageId and content.
    /// </remarks>
    public Task<WireToGateDurableMessage> MarkOutgoingAbandonedAsync(
        string messageId,
        string contentSha256,
        string reasonCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The rows still owed to the server: not acknowledged and not given up (<see cref="MarkOutgoingAbandonedAsync"/>).
    /// </summary>
    public Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<WireToGateAppliedJourneySnapshot>> ReadAppliedJourneySnapshotsAsync(
        CancellationToken cancellationToken = default);

    public Task<WireToGateAppliedJourneySnapshot> SaveAppliedJourneySnapshotAsync(
        WireToGateAppliedJourneySnapshot snapshot,
        CancellationToken cancellationToken = default);

    public Task<string> ComputeContentSha256Async(CancellationToken cancellationToken = default);
}

public sealed record WireToGateSessionSnapshot(
    bool Connected,
    long? SessionGeneration,
    WireToGateSessionReadiness Readiness,
    IReadOnlyList<string> ReasonCodes,
    long CapabilityVersion,
    long SafetyStateVersion,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A durable outbox row given up because the control server refused it with a <c>MANUAL_REVIEW</c> code
/// (onboard-hmi#254), as the session client reports it to the business layer.
/// </summary>
/// <param name="DeduplicationKey">The row's business key: what the business layer finds its own state by.</param>
/// <param name="WireLine">The line as it stands on file -- what this vehicle holds, for the operator log.</param>
/// <param name="ServerDisplayMessage">The refusal's <c>displayMessage</c>, when the server gave one.</param>
public sealed record WireToGateDurableMessageAbandonment(
    string DeduplicationKey,
    string MessageType,
    string MessageId,
    string ContentSha256,
    string ReasonCode,
    string? ServerDisplayMessage,
    string WireLine);

/// <summary>
/// What a <c>DurableAck</c> that arrived after its send had stopped waiting for it did to its outbox row
/// (onboard-hmi#250). An ack that matches no row on file -- or not its type or content -- is none of these: the session
/// still ends on it as an unhandled message.
/// </summary>
public enum WireToGateLateDurableAckOutcome
{
    /// <summary>The row was still owed, and is acknowledged now.</summary>
    Acknowledged,

    /// <summary>The row was acknowledged already; nothing changed.</summary>
    AlreadyAcknowledged,

    /// <summary>
    /// The row was given up (<see cref="WireToGateDurableMessage.Abandoned"/>) and stays given up: giving a row up is a
    /// record the operator has already been told of, and an ack that contradicts it does not undo it.
    /// </summary>
    Abandoned
}

/// <summary>
/// A late <c>DurableAck</c> the session client settled against its outbox row (onboard-hmi#250), as it reports it.
/// </summary>
/// <param name="ContentSha256">The content hash the ack accepted, which is the row's: an ack naming another is never
/// settled.</param>
public sealed record WireToGateLateDurableAck(
    string DeduplicationKey,
    string MessageType,
    string MessageId,
    string ContentSha256,
    WireToGateLateDurableAckOutcome Outcome);
