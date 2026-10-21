using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 「剩下的不装（已装 k/N 篮）」 on a WIRE_TO_GATE multi-basket load (8005-agv-onboard-hmi#388, CP-0021): beside 「重新开门」,
/// bound to the business service.
/// </summary>
/// <remarks>
/// Shown while the business service offers it, and kept on screen, disabled, while a press waits for the server, so the
/// operator sees it is pending and cannot press it again. <see cref="RemainingBasketsSkipStatus"/> carries the state into
/// the screen-state snapshot. Refreshed on the WIRE_TO_GATE presentation path, as 「重新开门」 is.
/// </remarks>
public sealed partial class MainViewModel
{
    private bool _canRequestRemainingBasketsSkip;
    private bool _showsRemainingBasketsSkip;
    private string _remainingBasketsSkipCaption = string.Empty;
    private string _remainingBasketsSkipStatus = string.Empty;
    private Func<WireToGateRemainingBasketsSkipOffer?>? _wireToGateRemainingBasketsSkipOffer;
    private Func<string>? _wireToGateRemainingBasketsSkipStatus;
    private Func<WireToGateRemainingBasketsSkipOffer, CancellationToken, Task<bool>>? _wireToGateRemainingBasketsSkipRequester;

    /// <summary>Whether 「剩下的不装」 can be pressed now.</summary>
    public bool CanRequestRemainingBasketsSkip
    {
        get => _canRequestRemainingBasketsSkip;
        private set => SetProperty(ref _canRequestRemainingBasketsSkip, value);
    }

    /// <summary>Whether the button is on screen: offered, or pressed and waiting for the server.</summary>
    public bool ShowsRemainingBasketsSkip
    {
        get => _showsRemainingBasketsSkip;
        private set => SetProperty(ref _showsRemainingBasketsSkip, value);
    }

    /// <summary>「剩下的不装（已装 k/N 篮）」, or empty when the button is not on screen.</summary>
    public string RemainingBasketsSkipCaption
    {
        get => _remainingBasketsSkipCaption;
        private set => SetProperty(ref _remainingBasketsSkipCaption, value);
    }

    /// <summary><c>OFFERED:k/N</c> or <c>PENDING:k/N</c>, otherwise empty.</summary>
    public string RemainingBasketsSkipStatus
    {
        get => _remainingBasketsSkipStatus;
        private set => SetProperty(ref _remainingBasketsSkipStatus, value);
    }

    /// <summary>What a press would be made over now; the confirmation dialog is built from it.</summary>
    public WireToGateRemainingBasketsSkipOffer? RemainingBasketsSkipOffer => _wireToGateRemainingBasketsSkipOffer?.Invoke();

    /// <remarks>接线本身在 <c>RemainingBasketsSkipWiring</c>，G2 夹具调的是同一个方法。</remarks>
    internal void ConfigureRemainingBasketsSkip(
        Func<WireToGateRemainingBasketsSkipOffer?> offer,
        Func<string> status,
        Func<WireToGateRemainingBasketsSkipOffer, CancellationToken, Task<bool>> requester)
    {
        _wireToGateRemainingBasketsSkipOffer = offer ?? throw new ArgumentNullException(nameof(offer));
        _wireToGateRemainingBasketsSkipStatus = status ?? throw new ArgumentNullException(nameof(status));
        _wireToGateRemainingBasketsSkipRequester = requester ?? throw new ArgumentNullException(nameof(requester));
        RunOnUiThread(RefreshRemainingBasketsSkipCore);
    }

    /// <summary>
    /// The operator confirmed 「剩下的不装」 over <paramref name="shown"/>. True when the request is on file; false when the
    /// button was not wired or the offer changed under the dialog.
    /// </summary>
    public async Task<bool> RequestRemainingBasketsSkipAsync(
        WireToGateRemainingBasketsSkipOffer shown,
        CancellationToken cancellationToken = default)
    {
        if (_wireToGateRemainingBasketsSkipRequester is not { } requester)
        {
            return false;
        }

        bool taken = await requester(shown, cancellationToken).ConfigureAwait(true);
        RunOnUiThread(RefreshRemainingBasketsSkipCore);
        return taken;
    }

    private void RefreshRemainingBasketsSkipCore()
    {
        WireToGateRemainingBasketsSkipOffer? offer = _wireToGateRemainingBasketsSkipOffer?.Invoke();
        string status = _wireToGateRemainingBasketsSkipStatus?.Invoke() ?? string.Empty;
        (int Loaded, int Expected)? counts = offer is not null
            ? (offer.LoadedSlots.Count, offer.ExpectedBasketCount)
            : ParseCounts(status);
        CanRequestRemainingBasketsSkip = offer is not null;
        ShowsRemainingBasketsSkip = counts is not null;
        RemainingBasketsSkipCaption = counts is { } shown
            ? WireToGateRemainingBasketsSkipText.Caption(shown.Loaded, shown.Expected)
            : string.Empty;
        RemainingBasketsSkipStatus = counts is null ? string.Empty : status;
    }

    /// <summary>k and N out of <c>PENDING:k/N</c>, or <c>null</c>.</summary>
    private static (int Loaded, int Expected)? ParseCounts(string status)
    {
        int colon = status.IndexOf(':', StringComparison.Ordinal);
        int slash = status.IndexOf('/', StringComparison.Ordinal);
        return colon > 0
            && slash > colon
            && int.TryParse(status.AsSpan(colon + 1, slash - colon - 1), out int loaded)
            && int.TryParse(status.AsSpan(slash + 1), out int expected)
                ? (loaded, expected)
                : null;
    }
}
