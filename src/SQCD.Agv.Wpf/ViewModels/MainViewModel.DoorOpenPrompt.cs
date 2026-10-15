using System.Windows.Threading;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>The door-open banner's state: absent, waiting (yellow), or overdue (red, blinking).</summary>
public enum DoorOpenPromptTier
{
    None,
    Waiting,
    Overdue
}

/// <summary>
/// The door-open banner (onboard-hmi#354): a full-width line under the status bar, shown while a door of the current slot
/// operation reads unlocked, so the operator standing at the vehicle cannot miss that the vehicle is waiting on a door.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three facts, all of them.</b> The operation's stage is unlocking or waiting for the operator; the slot is one of
/// the operation's targets; its lock feedback reads unlocked. The stage alone is not enough: a close whose cargo did not
/// match, with the vehicle's safety unconfirmed (<c>ReopenHeldBySafety</c>), waits for the operator with the door shut.
/// The lock alone is not enough either: a door outside the operation opened by hand is not one the vehicle waits on.
/// </para>
/// <para>
/// <b>The clock is this screen's own.</b> It starts when the screen first sees the door open under an attempt and
/// restarts only when no target door reads open any more or the attempt changes; a repeated progress event does not
/// restart it. It drives only the banner -- the expected-action alarm keeps its own threshold and its own report.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    private bool _hasDoorOpenPrompt;
    private DoorOpenPromptTier _doorOpenPromptTier;
    private string _doorOpenPromptHeadline = string.Empty;
    private string _doorOpenPromptDetail = string.Empty;
    private bool _doorOpenPromptDimmed;
    private string? _doorOpenAttemptId;
    private DateTimeOffset _doorOpenSince;
    private DispatcherTimer? _doorOpenPromptTimer;

    public bool HasDoorOpenPrompt
    {
        get => _hasDoorOpenPrompt;
        private set => SetProperty(ref _hasDoorOpenPrompt, value);
    }

    public DoorOpenPromptTier DoorOpenPromptTier
    {
        get => _doorOpenPromptTier;
        private set => SetProperty(ref _doorOpenPromptTier, value);
    }

    public string DoorOpenPromptHeadline
    {
        get => _doorOpenPromptHeadline;
        private set => SetProperty(ref _doorOpenPromptHeadline, value);
    }

    public string DoorOpenPromptDetail
    {
        get => _doorOpenPromptDetail;
        private set => SetProperty(ref _doorOpenPromptDetail, value);
    }

    /// <summary>The off phase of the overdue blink, one second on and one off, counted from when the door was first seen open.</summary>
    public bool DoorOpenPromptDimmed
    {
        get => _doorOpenPromptDimmed;
        private set => SetProperty(ref _doorOpenPromptDimmed, value);
    }

    /// <summary>Re-reads the banner against the clock. The timer calls it; tests call it after moving the clock.</summary>
    internal void RefreshDoorOpenPrompt() => RunOnUiThread(RefreshDoorOpenPromptCore);

    private void RefreshDoorOpenPromptCore()
    {
        WireToGateHmiOperationSnapshot? operation = _wireToGateOperation;
        int[] openSlots = operation is { Stage: WireToGateHmiOperationStage.Unlocking or WireToGateHmiOperationStage.WaitingOperator }
            ? [.. operation.Slots
                .Where(slot => Lockers.FirstOrDefault(locker => locker.PhysicalNumber == slot) is { IsKnown: true, IsLocked: false })
                .Distinct()
                .Order()]
            : [];
        if (openSlots.Length == 0)
        {
            _doorOpenAttemptId = null;
            HasDoorOpenPrompt = false;
            DoorOpenPromptTier = DoorOpenPromptTier.None;
            DoorOpenPromptHeadline = string.Empty;
            DoorOpenPromptDetail = string.Empty;
            DoorOpenPromptDimmed = false;
            _doorOpenPromptTimer?.Stop();
            return;
        }

        DateTimeOffset now = Clock.Now;
        if (!string.Equals(_doorOpenAttemptId, operation!.SlotOperationAttemptId, StringComparison.Ordinal))
        {
            _doorOpenAttemptId = operation.SlotOperationAttemptId;
            _doorOpenSince = now;
        }

        TimeSpan openFor = now - _doorOpenSince;
        bool overdue = openFor >= WireToGateDoorOpenPromptText.EscalationDelay;
        DoorOpenPromptTier = overdue ? DoorOpenPromptTier.Overdue : DoorOpenPromptTier.Waiting;
        DoorOpenPromptHeadline = WireToGateDoorOpenPromptText.Headline(operation.OperationType, openSlots);
        DoorOpenPromptDetail = overdue
            ? WireToGateDoorOpenPromptText.OverdueDetail(openSlots, openFor)
            : WireToGateDoorOpenPromptText.WaitingDetail(operation.OperationType);
        DoorOpenPromptDimmed = overdue && (long)openFor.TotalSeconds % 2 == 1;
        HasDoorOpenPrompt = true;
        SyncDoorOpenPromptTimerCore();
    }

    /// <summary>
    /// Ticks only while the banner shows. 250 ms for the same reason as the departure countdown: a tick as long as the
    /// blink's second drifts against its edges and now and then lands twice in one phase, which reads as a stall.
    /// </summary>
    private void SyncDoorOpenPromptTimerCore()
    {
        if (_stationDepartureCountdownStopped)
        {
            _doorOpenPromptTimer?.Stop();
            return;
        }

        if (_doorOpenPromptTimer is null)
        {
            if (StationDepartureCountdownDispatcher is not { } dispatcher)
            {
                return;
            }

            _doorOpenPromptTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _doorOpenPromptTimer.Tick += (_, _) => RefreshDoorOpenPromptCore();
        }

        if (!_doorOpenPromptTimer.IsEnabled)
        {
            _doorOpenPromptTimer.Start();
        }
    }
}
