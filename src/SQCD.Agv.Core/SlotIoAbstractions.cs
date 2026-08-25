using System.Text;
using System.Text.Json;

namespace SQCD.Agv.Core;

public enum SlotOccupancy
{
    Empty,
    Occupied,
    Unknown
}

public enum SlotDoorLock
{
    Locked,
    NotLocked,
    Unknown
}

public enum UnlockOutputState
{
    Reset,
    Active,
    Unknown
}

public sealed record SlotIoState(
    int PhysicalSlotNumber,
    bool Online,
    SlotOccupancy Occupancy,
    SlotDoorLock DoorLock,
    UnlockOutputState UnlockOutput,
    DateTimeOffset ObservedAt);

public interface ISlotIoProvider : IAsyncDisposable
{
    Task<IReadOnlyList<SlotIoState>> ReadAllAsync(CancellationToken cancellationToken);

    Task PulseUnlockAsync(
        IReadOnlyList<int> physicalSlotNumbers,
        CancellationToken cancellationToken);
}

public interface IWireToGateSessionPlanner
{
    ReadOnlyMemory<byte> CreateSessionHello(string agvId, long sessionGeneration);
}

public sealed class WireToGateSessionPlanner : IWireToGateSessionPlanner
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _onboardInstanceId;
    private readonly string _credentialProof;
    private readonly string _buildCommit;

    public WireToGateSessionPlanner(
        string onboardInstanceId = "OBU-8005-01",
        string credentialProof = "CONFIGURE_EXTERNAL_CREDENTIAL",
        string buildCommit = "WORKTREE_BUILD")
    {
        _onboardInstanceId = Require(onboardInstanceId, nameof(onboardInstanceId));
        _credentialProof = Require(credentialProof, nameof(credentialProof));
        _buildCommit = Require(buildCommit, nameof(buildCommit));
    }

    public ReadOnlyMemory<byte> CreateSessionHello(string agvId, long sessionGeneration)
    {
        _ = sessionGeneration; // Server assigns the transport session generation.
        string messageId = Guid.NewGuid().ToString("D");
        var envelope = WireToGateProtocol.CreateEnvelope(
            "SessionHello",
            messageId,
            Require(agvId, nameof(agvId)),
            null,
            new
            {
                onboardInstanceId = _onboardInstanceId,
                onboardBuildCommit = _buildCommit,
                supportedProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                profileId = ProtocolCandidateIdentity.ProfileId,
                protocolReleaseIdentity = WireToGateProtocol.ReleaseIdentity(),
                credentialProof = _credentialProof
            });
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, SerializerOptions) + "\n");
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
}
