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
    public ReadOnlyMemory<byte> CreateSessionHello(string agvId, long sessionGeneration) =>
        throw new NotImplementedException("W2G-IS-00 must create SessionHello from the materialized protocol schema.");
}
