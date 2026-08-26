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

public sealed record WireToGateRecoveryState(
    string? UnsettledSlotOperationAttemptId,
    WireToGateRecoveryCheckpoint ProvenRecoveryCheckpoint,
    IReadOnlyList<int> ActiveUnlockSlots,
    long ForcedRecoveryGeneration,
    IReadOnlyList<WireToGatePendingResult> PendingResults)
{
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
/// treating a stale session generation as a live wire message.
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
