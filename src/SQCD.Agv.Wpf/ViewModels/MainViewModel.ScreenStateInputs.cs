using System.Runtime.CompilerServices;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// The ten recovery entries whose final value is the business value through the fatal-fault latch gate
/// (<see cref="MainViewModel.AllowRecoveryEntry"/>, onboard-hmi#174/#176).
/// </summary>
internal enum RecoveryEntry
{
    WireToGateRecovery,
    LoadCancellation,
    LoadCompensation,
    LoadCorrection,
    FaultCargoHandoff,
    ForcedMechanicalRecovery,
    ManualChargingReturn,
    ConfirmForcedMechanicalRecovery,
    SubmitHardwareRecoveryRecord,
    HardwareRepairRelease,
}

/// <summary>
/// What the business offered for one recovery entry, which refresh path read it, and in which entry refresh
/// (<see cref="MainViewModel.RecoveryEntryRefresh"/>).
/// </summary>
internal sealed record RecoveryEntryOffer(bool Offered, string Path, long Refresh);

/// <summary>
/// The inputs <see cref="WireToGateHmiPresentation.Create"/> was last called with, as read for that call
/// (onboard-hmi#314). The guidance lines are left out: they can name a sublot (research section 4.1; the controller's
/// own, for one, says <c>正在确认物料条码 {sublot}</c>).
/// </summary>
internal sealed record WireToGatePresentationInputs(
    WireToGateSessionSnapshot Session,
    WireToGateHmiOperationSnapshot? Operation,
    bool CanSubmit,
    bool LoadCancellationPending,
    bool SublotRejected,
    bool EntryPausedUntilStopped);

public partial class MainViewModel
{
    // The screen-state snapshot's T1b inputs (onboard-hmi#314). Written on the dispatcher thread alongside the values
    // they explain, read by the snapshot on the same thread. Plain reference writes, so a G2 run, which has no
    // dispatcher and refreshes on several threads, cannot make them throw.
    private readonly RecoveryEntryOffer?[] _recoveryEntryOffers = new RecoveryEntryOffer?[Enum.GetValues<RecoveryEntry>().Length];
    private RecoveryEntryOffer? _loadCancellationBeforeAnySublotOffer;
    private WireToGatePresentationInputs? _presentationInputs;
    private long _recoveryEntryRefresh;

    /// <summary>The controller snapshot the banner and the latch gate last read.</summary>
    internal OnboardSnapshot? LastControllerSnapshot => _lastControllerSnapshot;

    /// <summary>Whether the recovery entries are closed by the latch: the gate's own criterion.</summary>
    internal bool RecoveryEntriesLatched => RecoveryEntriesBlockedByFatalFault;

    /// <summary>The inputs of the last <see cref="WireToGateHmiPresentation.Create"/>; null before the first.</summary>
    internal WireToGatePresentationInputs? PresentationInputs => _presentationInputs;

    /// <summary>
    /// What the business offered for each recovery entry when its final value was last written, indexed by
    /// <see cref="RecoveryEntry"/>; an element is null before its first write. A copy.
    /// </summary>
    internal IReadOnlyList<RecoveryEntryOffer?> RecoveryEntryOffers => [.. _recoveryEntryOffers];

    /// <summary>
    /// Counts the work items that write the ten entries' final values: each input refresh
    /// (<c>RefreshWireToGateInputStateCore</c>, which the presentation path follows in the same work item wherever both
    /// run) and each controller snapshot, the one caller that runs the presentation path alone. Every such work item
    /// writes all ten, but the presentation path's latch branch writes nine of them false without reading the business,
    /// so after a controller snapshot while latched those nine offers carry a lower number: the business value as of an
    /// earlier work item.
    /// </summary>
    internal long RecoveryEntryRefresh => _recoveryEntryRefresh;

    /// <summary>The "before any sublot" half of the load cancellation entry, read with it.</summary>
    internal RecoveryEntryOffer? LoadCancellationBeforeAnySublotOffer => _loadCancellationBeforeAnySublotOffer;

    /// <summary>Records the business value of a recovery entry and returns it unchanged.</summary>
    private bool Offer(RecoveryEntry entry, bool offered, [CallerMemberName] string path = "")
    {
        _recoveryEntryOffers[(int)entry] = new RecoveryEntryOffer(offered, path, _recoveryEntryRefresh);
        return offered;
    }

    /// <summary>Records the "before any sublot" half of the load cancellation entry and returns it unchanged.</summary>
    private bool OfferBeforeAnySublot(bool offered, [CallerMemberName] string path = "")
    {
        _loadCancellationBeforeAnySublotOffer = new RecoveryEntryOffer(offered, path, _recoveryEntryRefresh);
        return offered;
    }

    /// <summary>Starts a work item that writes the ten entries' final values; reads nothing.</summary>
    private void BeginRecoveryEntryRefresh() => _recoveryEntryRefresh++;
}
