using System.Security.Cryptography;
using System.Text;

namespace SQCD.Agv.Core;

public static class WireToGateProtocol
{
    public static object CreateEnvelope(
        string messageType,
        string messageId,
        string agvId,
        long? sessionGeneration,
        object payload,
        string? correlationId = null) => new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId,
            correlationId,
            agvId,
            sessionGeneration,
            sentAt = DateTimeOffset.UtcNow,
            payload
        };

    public static object ReleaseIdentity() => new
    {
        repository = "8005-agv-protocol",
        releaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
        tag = ProtocolCandidateIdentity.Tag,
        commit = ProtocolCandidateIdentity.RepositoryCommit,
        protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
        profileId = ProtocolCandidateIdentity.ProfileId,
        manifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
        schemaBundleSha256 = ProtocolCandidateIdentity.SchemaBundleSha256,
        vectorsSha256 = ProtocolCandidateIdentity.VectorsSha256
    };

    public static PendingResultReference ToPendingResultReference(JournalAttempt attempt)
    {
        if (attempt.Status != JournalAttemptStatus.ResultPendingAck || string.IsNullOrWhiteSpace(attempt.ResultJson))
        {
            throw new InvalidOperationException("Only a journaled result pending durable ack can be reported.");
        }
        string contentSha256 = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(attempt.ResultJson))).ToLowerInvariant();
        return new PendingResultReference(
            "OperationResult", attempt.MessageId, attempt.SlotOperationAttemptId, contentSha256);
    }

    public static string SelectRecoveryCheckpoint(IReadOnlyList<JournalAttempt> unsettled)
    {
        if (unsettled.Count == 0)
        {
            return "NONE";
        }
        if (unsettled.Any(item => item.Status == JournalAttemptStatus.ResultPendingAck))
        {
            return "RESULT_RECORDED";
        }
        if (unsettled.Any(item => item.Status is JournalAttemptStatus.IoStarted or JournalAttemptStatus.RecoveryRequired))
        {
            return "ACTIVE_UNLOCK_SET";
        }
        return "PREPARED";
    }
}

public sealed record PendingResultReference(
    string MessageType,
    string MessageId,
    string BusinessId,
    string ContentSha256);

public enum JournalAttemptStatus
{
    Prepared,
    IoStarted,
    ResultPendingAck,
    Completed,
    RecoveryRequired
}

public sealed record JournalAttempt(
    string SlotOperationAttemptId,
    string MessageId,
    string ContentHash,
    IReadOnlyList<int> TargetSlots,
    SlotOccupancy ExpectedOccupancy,
    long ForcedRecoveryGeneration,
    JournalAttemptStatus Status,
    string? ResultJson,
    DateTimeOffset UpdatedAt);

public interface IOnboardExecutionJournal : IAsyncDisposable
{
    Task<JournalAttempt?> GetAsync(string attemptId, CancellationToken cancellationToken);

    Task PrepareAsync(JournalAttempt attempt, CancellationToken cancellationToken);

    Task SetStatusAsync(
        string attemptId,
        JournalAttemptStatus status,
        string? resultJson,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<JournalAttempt>> ReadUnsettledAsync(CancellationToken cancellationToken);
}

public sealed record SlotOperationRequest(
    string SlotOperationAttemptId,
    string MessageId,
    string ContentHash,
    IReadOnlyList<int> TargetSlots,
    SlotOccupancy ExpectedOccupancy,
    long ForcedRecoveryGeneration,
    DateTimeOffset RequestedAt);

public sealed record SlotOperationExecutionResult(
    string SlotOperationAttemptId,
    bool Success,
    bool Replay,
    bool RecoveryRequired,
    IReadOnlyList<SlotIoState> FinalStates,
    string ReasonCode);

public enum VehicleBusinessReadiness
{
    RecoveryRequired,
    Ready
}

public sealed record OnboardJourneyProjection(
    string StageId,
    string StageTitle,
    string Guidance,
    string? DemandId,
    string? Sublot,
    int? ExpectedBasketCount,
    IReadOnlyList<int> TargetSlots,
    IReadOnlyList<int> ActiveUnlockSet,
    VehicleBusinessReadiness Readiness,
    bool ControlServerConnected,
    bool VehicleStopped,
    bool DepartureSafe,
    string PrimaryAction,
    bool PrimaryActionEnabled,
    bool EmergencyStopAvailable);

public sealed record AuthoritativeJourneyProjection(
    string DemandId,
    long Revision,
    string ContentHash,
    string WorkType,
    string PickupStationId,
    string GateStationId,
    int ExpectedBasketCount,
    IReadOnlyList<int> TargetSlots);

public sealed class AuthoritativeJourneyProjectionStore
{
    private AuthoritativeJourneyProjection? _current;

    public AuthoritativeJourneyProjection? Current => _current;

    public void Apply(AuthoritativeJourneyProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.WorkType != "WIRE_TO_GATE" || projection.ExpectedBasketCount <= 0 ||
            projection.TargetSlots.Count != projection.ExpectedBasketCount ||
            projection.TargetSlots.Distinct().Count() != projection.TargetSlots.Count ||
            projection.TargetSlots.Any(slot => slot is < 1 or > 8))
        {
            throw new InvalidDataException("Authoritative journey projection is invalid for the eight-slot MVP.");
        }
        if (_current is not null)
        {
            if (projection.Revision < _current.Revision ||
                projection.Revision == _current.Revision && projection.ContentHash != _current.ContentHash)
            {
                throw new InvalidDataException("Journey projection revision regressed or conflicts at the same revision.");
            }
            if (projection.Revision == _current.Revision)
            {
                return;
            }
        }
        _current = projection;
    }
}

public static class PreDepartureSafetyEvaluator
{
    public static bool IsSafe(
        IReadOnlyCollection<SlotIoState> states,
        DateTimeOffset now,
        TimeSpan maximumAge) => states.Count == 8 &&
            states.Select(item => item.PhysicalSlotNumber).Distinct().Count() == 8 &&
            states.All(item => item.PhysicalSlotNumber is >= 1 and <= 8 && item.Online &&
                item.Occupancy != SlotOccupancy.Unknown && item.DoorLock == SlotDoorLock.Locked &&
                item.UnlockOutput == UnlockOutputState.Reset && item.ObservedAt <= now &&
                now - item.ObservedAt <= maximumAge);
}
