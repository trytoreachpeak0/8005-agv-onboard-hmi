namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 「重新开门」 on WIRE_TO_GATE (8005-agv-onboard-hmi#385): the button where the MVP's 「重新打开仓门」 was, bound to the
/// business service instead of <c>OnboardController.CanReopenCurrentOperation</c>, which never turns true on v2.
/// </summary>
/// <remarks>
/// The business service offers it while the executor holds a slot shut over the opposite occupancy and waits for the
/// press; <see cref="ReopenWaitStatus"/> carries that state into the screen-state snapshot. Refreshed on the WIRE_TO_GATE
/// presentation path, which every operator event runs, so the button follows each progress report.
/// </remarks>
public sealed partial class MainViewModel
{
    private string _reopenWaitStatus = string.Empty;
    private Func<bool>? _wireToGateCanReopenSlot;
    private Func<string>? _wireToGateSlotReopenStatus;
    private Func<CancellationToken, Task<bool>>? _wireToGateSlotReopenRequester;

    /// <summary>
    /// <c>SHUT_OVER_OPPOSITE:&lt;slot&gt;</c> or <c>REOPEN_NOT_RELEASED:&lt;slot&gt;</c> while 「重新开门」 is offered on v2,
    /// otherwise empty.
    /// </summary>
    public string ReopenWaitStatus
    {
        get => _reopenWaitStatus;
        private set => SetProperty(ref _reopenWaitStatus, value);
    }

    /// <summary>Whether the reopen button acts on a WIRE_TO_GATE slot rather than on the MVP controller's operation.</summary>
    public bool ReopensWireToGateSlot => _wireToGateSlotReopenRequester is not null;

    /// <remarks>接线本身在 <c>SlotReopenWiring</c>，G2 夹具调的是同一个方法。</remarks>
    internal void ConfigureSlotReopen(
        Func<bool> canReopen,
        Func<string> status,
        Func<CancellationToken, Task<bool>> requester)
    {
        _wireToGateCanReopenSlot = canReopen ?? throw new ArgumentNullException(nameof(canReopen));
        _wireToGateSlotReopenStatus = status ?? throw new ArgumentNullException(nameof(status));
        _wireToGateSlotReopenRequester = requester ?? throw new ArgumentNullException(nameof(requester));
        RunOnUiThread(RefreshSlotReopenCore);
    }

    private async Task<bool> RequestSlotReopenAsync(
        Func<CancellationToken, Task<bool>> requester,
        CancellationToken cancellationToken)
    {
        bool taken = await requester(cancellationToken).ConfigureAwait(true);
        RunOnUiThread(RefreshSlotReopenCore);
        return taken;
    }

    private void RefreshSlotReopenCore()
    {
        bool offered = _wireToGateCanReopenSlot?.Invoke() == true;
        CanReopenOperation = offered;
        ReopenWaitStatus = offered ? _wireToGateSlotReopenStatus?.Invoke() ?? string.Empty : string.Empty;
    }
}
