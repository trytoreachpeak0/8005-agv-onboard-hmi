using SQCD.Agv.Core;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The real time less a lag that only shrinks: moving it on brings the clock closer to the real time, never past it.
/// </summary>
/// <remarks>
/// For a test that has to put minutes between two presses or reconnects while the vehicle still executes afterwards.
/// The vehicle's safety signal and IO readings are stamped with the real time, so a manual clock moved minutes ahead of
/// them refuses every command as stale (VEHICLE_NOT_READY) -- and a test that means "past the window, so not run" then
/// passes on a vehicle that would have run it (onboard-hmi#239: two tests green on the base for that reason). Starting
/// behind and catching up keeps the last step at the real time.
/// </remarks>
internal sealed class RealTimeLaggingClock(TimeSpan lag) : IClock
{
    private long _lagTicks = lag.Ticks;

    public DateTimeOffset Now => DateTimeOffset.UtcNow - TimeSpan.FromTicks(Interlocked.Read(ref _lagTicks));

    /// <summary>Moves the clock on by <paramref name="by"/>, as far as the real time at most.</summary>
    public void Advance(TimeSpan by)
    {
        long current;
        long next;
        do
        {
            current = Interlocked.Read(ref _lagTicks);
            next = Math.Max(0, current - by.Ticks);
        }
        while (Interlocked.CompareExchange(ref _lagTicks, next, current) != current);
    }
}
