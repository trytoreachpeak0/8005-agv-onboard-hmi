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

/// <summary>What the business offered for one recovery entry, and which refresh path read it.</summary>
internal sealed record RecoveryEntryOffer(bool Offered, string Path);

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

    /// <summary>The "before any sublot" half of the load cancellation entry, read with it.</summary>
    internal RecoveryEntryOffer? LoadCancellationBeforeAnySublotOffer => _loadCancellationBeforeAnySublotOffer;

    /// <summary>Records the business value of a recovery entry and returns it unchanged.</summary>
    private bool Offer(RecoveryEntry entry, bool offered, [CallerMemberName] string path = "")
    {
        _recoveryEntryOffers[(int)entry] = new RecoveryEntryOffer(offered, path);
        return offered;
    }

    /// <summary>Records the "before any sublot" half of the load cancellation entry and returns it unchanged.</summary>
    private bool OfferBeforeAnySublot(bool offered, [CallerMemberName] string path = "")
    {
        _loadCancellationBeforeAnySublotOffer = new RecoveryEntryOffer(offered, path);
        return offered;
    }
}
