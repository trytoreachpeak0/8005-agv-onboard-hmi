using System.Diagnostics;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The Modbus client against a wall clock that does not run true (8005-agv-onboard-hmi#404): workspace#55 steps the
/// vehicle PC's clock by <c>Set-Date -Adjust</c> every hour, by 0.2 to 5 seconds either way. The clocks here step a whole
/// second at every reading, so whatever a reading's age or a stable window is measured on, a wall clock would show it.
/// </summary>
public sealed partial class ModbusTcpIoModuleClientIntegrationTests
{
    /// <summary>
    /// Every reading carries the monotonic timestamp it was taken at, and with the wall clock running backwards the last
    /// one is still fresh: none comes "from the future".
    /// </summary>
    [Fact]
    public async Task ReadingsCarryTheMonotonicTimestampAndStayFreshWhileTheWallClockRunsBackwards()
    {
        await using FakeModbusServer server = new();
        server.Start();
        SteppingWallClock clock = new(TimeSpan.FromSeconds(-1));
        await using ModbusTcpIoModuleClient client = new(Settings(server), new NullLogger(), clock);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await client.StartAsync(timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);

        IoSnapshot first = client.CurrentSnapshot;
        await WaitUntilAsync(() => client.CurrentSnapshot.ObservedAt < first.ObservedAt, timeout.Token);
        IoSnapshot later = client.CurrentSnapshot;

        Assert.NotNull(first.ObservedTimestamp);
        Assert.NotNull(later.ObservedTimestamp);
        Assert.True(later.ObservedTimestamp > first.ObservedTimestamp);
        Assert.All(later.Lockers, locker => Assert.Equal(later.ObservedTimestamp, locker.ObservedTimestamp));
        Assert.True(later.GetLocker(0).IsObservedNoEarlierThan(first.GetLocker(0)));
        Assert.True(SafetyRules.IsSnapshotFresh(later, clock, TimeSpan.FromSeconds(1)));

        await client.StopAsync(timeout.Token);
    }

    /// <summary>
    /// With the wall clock running forwards, a reading must still hold for the whole stable window of real time before
    /// the wait returns it. Measured on the wall clock the window closed at the second reading, some 20 ms in.
    /// </summary>
    [Fact]
    public async Task TheStableWindowIsNotShortenedByAWallClockRunningForwards()
    {
        await using FakeModbusServer server = new();
        server.Start();
        SteppingWallClock clock = new(TimeSpan.FromSeconds(1));
        await using ModbusTcpIoModuleClient client = new(Settings(server), new NullLogger(), clock);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await client.StartAsync(timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);
        TimeSpan window = TimeSpan.FromMilliseconds(300);

        long started = Stopwatch.GetTimestamp();
        await client.WaitForLockerAsync(0, locker => locker.IsLocked, TimeSpan.FromSeconds(5), window, timeout.Token);
        TimeSpan waited = Stopwatch.GetElapsedTime(started);

        Assert.True(
            waited >= window - TimeSpan.FromMilliseconds(30),
            $"the reading was taken as stable after {waited.TotalMilliseconds:0} ms of a {window.TotalMilliseconds:0} ms window");

        await client.StopAsync(timeout.Token);
    }

    /// <summary>
    /// With the wall clock running backwards, a reading that holds for the stable window is returned once it has: on the
    /// wall clock the window never closed, and the wait ran out.
    /// </summary>
    [Fact]
    public async Task TheStableWindowIsNotLengthenedByAWallClockRunningBackwards()
    {
        await using FakeModbusServer server = new();
        server.Start();
        SteppingWallClock clock = new(TimeSpan.FromSeconds(-1));
        await using ModbusTcpIoModuleClient client = new(Settings(server), new NullLogger(), clock);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await client.StartAsync(timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);

        LockerSnapshot locker = await client.WaitForLockerAsync(
            0,
            candidate => candidate.IsLocked,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(300),
            timeout.Token);

        Assert.True(locker.IsLocked);

        await client.StopAsync(timeout.Token);
    }

    /// <summary>
    /// The three waits <c>WireToGateSlotOperationExecutor.WaitForClosedDoorAsync</c> races for a load -- the door shut over
    /// a basket, shut over an empty slot, the slot unreadable -- with the wall clock running forwards. The slot reads shut
    /// and empty for a brief 100 ms before the basket shows, far less than the window, so the door shut over the basket
    /// wins. Measured on the wall clock, the brief reading was "stable" by its second sample and the load was judged
    /// shut over the opposite occupancy.
    /// </summary>
    /// <remarks>
    /// The predicates are the executor's, copied: its own tests run it against IO doubles that implement the stable window
    /// themselves, and the window under test is this client's. The window is a second, not the configured 300 ms, so the
    /// brief reading ends with 900 ms to spare however late this test gets round to ending it.
    /// </remarks>
    [Fact]
    public async Task ABriefOppositeReadingDoesNotWinTheClosedDoorRaceWhileTheWallClockRunsForwards()
    {
        await using FakeModbusServer server = new();
        server.Start();
        SteppingWallClock clock = new(TimeSpan.FromSeconds(1));
        await using ModbusTcpIoModuleClient client = new(Settings(server), new NullLogger(), clock);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await client.StartAsync(timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);
        Assert.False(client.CurrentSnapshot.GetLocker(0).HasCargo);
        TimeSpan window = TimeSpan.FromSeconds(1);
        TimeSpan waitTimeout = TimeSpan.FromSeconds(5);
        using CancellationTokenSource race = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);

        Task<LockerSnapshot> reached = client.WaitForLockerAsync(
            0,
            locker => locker.IsKnown && locker.IsLocked && locker.HasCargo,
            waitTimeout,
            window,
            race.Token);
        Task<LockerSnapshot> opposite = client.WaitForLockerAsync(
            0,
            locker => locker.IsKnown && locker.IsLocked && !locker.HasCargo,
            waitTimeout,
            window,
            race.Token);
        Task<LockerSnapshot> unknown = client.WaitForLockerAsync(
            0,
            locker => !locker.IsKnown,
            waitTimeout,
            window,
            race.Token);
        await Task.Delay(100, timeout.Token);
        // The basket breaks the light curtain of slot 1 (DI 8 in the default mapping).
        server.SetInput(8, false);
        Task<LockerSnapshot> winner = await Task.WhenAny(reached, opposite, unknown);
        await race.CancelAsync();

        Assert.True(winner == reached, winner == opposite ? "shut over the opposite occupancy won" : "unknown won");
        Assert.True((await winner).HasCargo);

        await client.StopAsync(timeout.Token);
    }

    private static IoModuleSettings Settings(FakeModbusServer server) => new()
    {
        Host = "127.0.0.1",
        Port = server.Port,
        PollIntervalMs = 20,
        RequestTimeoutMs = 500,
        ReconnectDelayMs = 50
    };

    /// <summary>
    /// A wall clock that steps by <paramref name="step"/> every time it is read; its monotonic clock is the machine's.
    /// </summary>
    private sealed class SteppingWallClock(TimeSpan step) : IClock
    {
        private long _offsetTicks;

        public DateTimeOffset Now =>
            DateTimeOffset.UtcNow.AddTicks(Interlocked.Add(ref _offsetTicks, step.Ticks));

        public long GetTimestamp() => Stopwatch.GetTimestamp();
    }
}
