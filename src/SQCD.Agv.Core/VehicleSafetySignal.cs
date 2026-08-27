namespace SQCD.Agv.Core;

public enum VehicleMotionState
{
    Stopped,
    Moving,
    Unknown
}

public sealed record VehicleSafetySignal(
    VehicleMotionState MotionState,
    DateTimeOffset ObservedAt,
    string Source,
    string? VehicleKey = null,
    IReadOnlyList<string>? ReasonCodes = null)
{
    /// <summary>
    /// Gets the server reason codes without exposing a nullable collection to callers.
    /// Providers should still preserve the response codes when the response is trusted.
    /// </summary>
    public IReadOnlyList<string> EffectiveReasonCodes => ReasonCodes ?? Array.Empty<string>();

    public bool IsFresh(DateTimeOffset now, TimeSpan maxAge)
    {
        if (maxAge <= TimeSpan.Zero
            || ObservedAt == default
            || ObservedAt > now)
        {
            return false;
        }

        return now - ObservedAt <= maxAge;
    }

    public bool IsStoppedAndFresh(DateTimeOffset now, TimeSpan maxAge) =>
        MotionState == VehicleMotionState.Stopped && IsFresh(now, maxAge);
}

public interface IVehicleSafetySignalProvider
{
    public VehicleSafetySignal Read();
}

/// <summary>
/// Safe default used until a trusted vehicle stop/park signal is wired.
/// Unknown is intentionally different from stopped so it cannot authorize IO.
/// </summary>
public sealed class UnavailableVehicleSafetySignalProvider : IVehicleSafetySignalProvider
{
    public VehicleSafetySignal Read() =>
        new(VehicleMotionState.Unknown, DateTimeOffset.UtcNow, "UNAVAILABLE");
}

/// <summary>
/// Deterministic provider for local tests and recorded signal playback.
/// </summary>
public sealed class RecordedVehicleSafetySignalProvider : IVehicleSafetySignalProvider
{
    private VehicleSafetySignal _current;

    public RecordedVehicleSafetySignalProvider(
        VehicleSafetySignal? initial = null)
    {
        _current = initial
            ?? new VehicleSafetySignal(
                VehicleMotionState.Unknown,
                DateTimeOffset.UtcNow,
                "RECORDED");
    }

    public VehicleSafetySignal Read() => Volatile.Read(ref _current);

    public void Set(
        VehicleMotionState motionState,
        DateTimeOffset observedAt,
        string source = "RECORDED")
    {
        if (!Enum.IsDefined(motionState))
        {
            throw new ArgumentOutOfRangeException(nameof(motionState));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Volatile.Write(
            ref _current,
            new VehicleSafetySignal(motionState, observedAt, source));
    }
}
