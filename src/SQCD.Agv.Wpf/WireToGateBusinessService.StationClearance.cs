using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The operator's entry for <c>CV-MANUAL-STATION-CLEARANCE</c> (batch 9-16, <c>8005-agv-onboard-hmi#221</c>).
/// </summary>
public sealed partial class WireToGateBusinessService
{
    public WireToGateStationClearanceView StationClearance
    {
        get
        {
            // Stub: the entry is never offered yet.
            _ = _session.CurrentJourney;
            return WireToGateStationClearanceView.NotClearing;
        }
    }

    public bool CanConfirmStationClearance => StationClearance.Prompt is not null;

    public Task<bool> ConfirmStationClearanceAsync(
        WireToGateStationClearancePrompt shown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ThrowIfDisposed();
        return Task.FromResult(false);
    }
}
