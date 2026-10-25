namespace SQCD.Agv.Wpf;

// Stub so the hmi#410 tests compile; the implementation follows in its own commit.
public sealed partial class WireToGateBusinessService
{
    public bool CanAbandonResume => _executor is null;

    public Task<bool> AbandonResumeAsync(CancellationToken cancellationToken = default) => Task.FromResult(_executor is null);
}
