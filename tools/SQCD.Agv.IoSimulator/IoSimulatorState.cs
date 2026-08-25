using System.Collections.Concurrent;

namespace SQCD.Agv.IoSimulator;

public sealed record UnlockRequest(IReadOnlyList<int> SlotNumbers);

public sealed record SlotMutation(bool? Online, string? Occupancy, string? LockState, string? UnlockOutputState);

public sealed record FaultRequest(string Mode, int? SlotNo = null, int DelayMs = 5_000);

public enum SimulatorFaultMode
{
    None,
    Offline,
    ResponseTimeout,
    Unknown,
    LockFeedbackAbnormal,
    OutputStuck,
    RequestLost,
    ResponseLost,
    Crashed
}

public sealed record SimulatorFault(SimulatorFaultMode Mode, int? SlotNo, int DelayMs);

public sealed record SimulatorSlot(
    int SlotNo,
    bool Online,
    string Occupancy,
    string LockState,
    string UnlockOutputState,
    string LightCurtainState,
    DateTimeOffset ObservedAt);

public sealed class IoSimulatorState
{
    private static readonly string[] OccupancyValues = ["EMPTY", "OCCUPIED", "UNKNOWN"];
    private static readonly string[] LockValues = ["LOCKED", "NOT_LOCKED", "UNKNOWN"];
    private static readonly string[] OutputValues = ["RESET", "ACTIVE", "UNKNOWN"];
    private readonly ConcurrentDictionary<int, SimulatorSlot> _slots = new(
        Enumerable.Range(1, 8).Select(slot => new KeyValuePair<int, SimulatorSlot>(
            slot,
            new SimulatorSlot(slot, true, "EMPTY", "LOCKED", "RESET", "CLEAR", DateTimeOffset.UtcNow))));

    public SimulatorFault Fault { get; private set; } = new(SimulatorFaultMode.None, null, 5_000);

    public IReadOnlyList<SimulatorSlot> Snapshot()
    {
        SimulatorFault fault = Fault;
        return _slots.Values.OrderBy(item => item.SlotNo).Select(item => ApplyFault(item, fault)).ToArray();
    }

    public void PulseUnlock(IReadOnlyList<int> slotNumbers)
    {
        int[] normalized = slotNumbers.Distinct().Order().ToArray();
        if (normalized.Length == 0 || normalized.Length != slotNumbers.Count || normalized.Any(slot => slot is < 1 or > 8))
        {
            throw new BadHttpRequestException("slotNumbers must be a unique non-empty subset of 1..8.");
        }
        foreach (int slot in normalized)
        {
            _slots.AddOrUpdate(
                slot,
                _ => throw new InvalidOperationException(),
                (_, current) => current with
                {
                    LockState = "NOT_LOCKED",
                    UnlockOutputState = Fault.Mode == SimulatorFaultMode.OutputStuck ? "ACTIVE" : "RESET",
                    ObservedAt = DateTimeOffset.UtcNow
                });
        }
    }

    public void Update(int slotNo, SlotMutation mutation)
    {
        if (slotNo is < 1 or > 8)
        {
            throw new BadHttpRequestException("slotNo must be in 1..8.");
        }
        ValidateValue(mutation.Occupancy, OccupancyValues, nameof(mutation.Occupancy));
        ValidateValue(mutation.LockState, LockValues, nameof(mutation.LockState));
        ValidateValue(mutation.UnlockOutputState, OutputValues, nameof(mutation.UnlockOutputState));
        _slots.AddOrUpdate(
            slotNo,
            _ => throw new InvalidOperationException(),
            (_, current) => current with
            {
                Online = mutation.Online ?? current.Online,
                Occupancy = mutation.Occupancy ?? current.Occupancy,
                LockState = mutation.LockState ?? current.LockState,
                UnlockOutputState = mutation.UnlockOutputState ?? current.UnlockOutputState,
                LightCurtainState = mutation.Occupancy switch
                {
                    "EMPTY" => "CLEAR",
                    "OCCUPIED" => "BLOCKED",
                    "UNKNOWN" => "UNKNOWN",
                    _ => current.LightCurtainState
                },
                ObservedAt = DateTimeOffset.UtcNow
            });
    }

    public void SetFault(FaultRequest request)
    {
        if (!Enum.TryParse(request.Mode, ignoreCase: true, out SimulatorFaultMode mode))
        {
            throw new BadHttpRequestException("Unknown fault mode.");
        }
        if (request.SlotNo is < 1 or > 8 || request.DelayMs < 0)
        {
            throw new BadHttpRequestException("Fault slotNo or delayMs is invalid.");
        }
        Fault = new SimulatorFault(mode, request.SlotNo, request.DelayMs);
    }

    public void Recover() => Fault = new SimulatorFault(SimulatorFaultMode.None, null, 5_000);

    public async Task<IResult?> BeforeRequestAsync(bool isMutation, CancellationToken cancellationToken)
    {
        switch (Fault.Mode)
        {
            case SimulatorFaultMode.ResponseTimeout:
                await Task.Delay(Fault.DelayMs, cancellationToken);
                return null;
            case SimulatorFaultMode.RequestLost:
                return Results.Json(new { error = "SIMULATED_REQUEST_LOSS" }, statusCode: 503);
            case SimulatorFaultMode.ResponseLost when isMutation:
                return Results.Json(new { error = "SIMULATED_RESULT_LOSS" }, statusCode: 503);
            case SimulatorFaultMode.Crashed:
                return Results.Json(new { error = "SIMULATED_PROCESS_UNAVAILABLE" }, statusCode: 503);
            default:
                return null;
        }
    }

    private static SimulatorSlot ApplyFault(SimulatorSlot slot, SimulatorFault fault)
    {
        bool target = fault.SlotNo is null || fault.SlotNo == slot.SlotNo;
        if (!target)
        {
            return slot;
        }
        return fault.Mode switch
        {
            SimulatorFaultMode.Offline => slot with { Online = false, Occupancy = "UNKNOWN", LockState = "UNKNOWN", UnlockOutputState = "UNKNOWN", LightCurtainState = "UNKNOWN" },
            SimulatorFaultMode.Unknown => slot with { Occupancy = "UNKNOWN", LockState = "UNKNOWN", UnlockOutputState = "UNKNOWN", LightCurtainState = "UNKNOWN" },
            SimulatorFaultMode.LockFeedbackAbnormal => slot with { LockState = "NOT_LOCKED" },
            SimulatorFaultMode.OutputStuck => slot with { UnlockOutputState = "ACTIVE" },
            _ => slot
        };
    }

    private static void ValidateValue(string? value, IReadOnlyCollection<string> allowed, string name)
    {
        if (value is not null && !allowed.Contains(value, StringComparer.Ordinal))
        {
            throw new BadHttpRequestException($"{name} is invalid.");
        }
    }
}
