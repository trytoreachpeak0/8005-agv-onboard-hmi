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

    public static WireToGateRecoveryState Empty { get; } = new(
        null,
        WireToGateRecoveryCheckpoint.None,
        [],
        0,
        []);
}

public sealed record WireToGateDurableMessage(
    string DeduplicationKey,
    string MessageType,
    string MessageId,
    string ContentSha256,
    string WireLine,
    DateTimeOffset CreatedAt,
    bool Acknowledged);

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

    public Task WriteRecoveryStateAsync(
        WireToGateRecoveryState state,
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

    public Task MarkOutgoingAcknowledgedAsync(
        string messageId,
        string acceptedContentSha256,
        CancellationToken cancellationToken = default);

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
