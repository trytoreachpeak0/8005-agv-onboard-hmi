namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 「不装了」 on WIRE_TO_GATE (8005-agv-onboard-hmi#410): beside 「重新开门」, while a resumed load over empty slots waits for
/// it, bound to the business service.
/// </summary>
/// <remarks>
/// Refreshed with 「重新开门」 (<see cref="RefreshSlotReopenCore"/>): it is offered in the same wait, which ends with the
/// latch as that one does, so it is refreshed ahead of the latch guard as well.
/// </remarks>
public sealed partial class MainViewModel
{
    private bool _canAbandonResume;
    private Func<bool>? _wireToGateCanAbandonResume;
    private Func<CancellationToken, Task<bool>>? _wireToGateResumeAbandoner;

    /// <summary>Whether 「不装了」 is on screen and can be pressed.</summary>
    public bool CanAbandonResume
    {
        get => _canAbandonResume;
        private set => SetProperty(ref _canAbandonResume, value);
    }

    /// <remarks>接线本身在 <c>SlotReopenWiring</c>，G2 夹具调的是同一个方法。</remarks>
    internal void ConfigureResumeAbandon(Func<bool> canAbandon, Func<CancellationToken, Task<bool>> abandoner)
    {
        _wireToGateCanAbandonResume = canAbandon ?? throw new ArgumentNullException(nameof(canAbandon));
        _wireToGateResumeAbandoner = abandoner ?? throw new ArgumentNullException(nameof(abandoner));
        RunOnUiThread(RefreshResumeAbandonCore);
    }

    /// <summary>The operator pressed 「不装了」. True when the executor took the press.</summary>
    public async Task<bool> AbandonResumeAsync(CancellationToken cancellationToken = default)
    {
        if (_wireToGateResumeAbandoner is not { } abandoner)
        {
            return false;
        }

        bool taken = await abandoner(cancellationToken).ConfigureAwait(true);
        RunOnUiThread(RefreshResumeAbandonCore);
        return taken;
    }

    private void RefreshResumeAbandonCore() =>
        CanAbandonResume = _wireToGateCanAbandonResume?.Invoke() == true;
}
