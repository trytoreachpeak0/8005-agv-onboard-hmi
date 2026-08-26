using SQCD.Agv.Core;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed class FakeIoModuleClient : IIoModuleClient
{
    private readonly LockerSnapshot[] _lockers;
    private readonly object _sync = new();

    public FakeIoModuleClient()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _lockers = Enumerable.Range(0, 8)
            .Select(index => new LockerSnapshot(index, index + 1, false, true, true, now))
            .ToArray();
        CurrentSnapshot = new IoSnapshot(true, _lockers.ToArray(), now);
    }

    public int UnlockCount { get; private set; }

    public bool IsConnected => true;

    public IoSnapshot CurrentSnapshot { get; private set; }

    public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

    public event EventHandler<ValueChangedEventArgs<IoSnapshot>>? SnapshotChanged;

    public void SetCargoPresent(int slotIndex, bool present)
    {
        lock (_sync)
        {
            LockerSnapshot locker = _lockers[slotIndex];
            _lockers[slotIndex] = locker with { LightCurtainRaw = !present };
            CurrentSnapshot = CurrentSnapshot with { Lockers = _lockers.ToArray() };
        }
    }

    public Task StartAsync(CancellationToken applicationStopping) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PulseUnlockAsync(int slotIndex, CancellationToken cancellationToken)
    {
        UnlockCount++;
        return Task.CompletedTask;
    }

    public Task<LockerSnapshot> WaitForLockerAsync(
        int slotIndex,
        Func<LockerSnapshot, bool> predicate,
        TimeSpan timeout,
        TimeSpan stableWindow,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("G2会话测试不执行仓位操作。");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
