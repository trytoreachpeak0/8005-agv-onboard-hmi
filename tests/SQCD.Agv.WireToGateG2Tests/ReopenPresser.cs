using SQCD.Agv.Wpf;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The operator who presses 「重新开门」 whenever the vehicle offers it (8005-agv-onboard-hmi#385), for a test whose door is
/// shut over the opposite occupancy on the way to its real subject -- the prompts, the wait clock -- and that used to rely
/// on the vehicle reopening by itself.
/// </summary>
internal sealed class ReopenPresser : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _loop;
    private int _presses;

    private ReopenPresser(WireToGateBusinessService business, CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = PressAsync(business, _stop.Token);
    }

    /// <summary>How many presses the vehicle took.</summary>
    public int Presses => Volatile.Read(ref _presses);

    public static ReopenPresser Start(WireToGateBusinessService business, CancellationToken cancellationToken) =>
        new(business, cancellationToken);

    private async Task PressAsync(WireToGateBusinessService business, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (business.CanReopenWaitingSlot && await business.RequestSlotReopenAsync(cancellationToken))
            {
                Interlocked.Increment(ref _presses);
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Stopped, which is what was asked.
        }

        _stop.Dispose();
    }
}
