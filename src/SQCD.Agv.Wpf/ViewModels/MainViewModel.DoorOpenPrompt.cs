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
/// <b>Not over a recovery vector's doors</b> (review M1, the coordinator's ruling of 2026-10-16). Compensation, correction,
/// cancellation, fault cargo handoff and forced mechanical recovery carry their own detailed prompt, and the words the
/// user approved cover ordinary loads and unloads only: a correction reads as a load yet first waits for the slot to be
/// emptied, so "put the basket in" would contradict the line under it. A resume after repair runs the original operation
/// and shows the banner as usual.
/// </para>
/// <para>
/// <b>The clock is this screen's own, one per slot of an attempt</b> (review M2). A slot's clock starts when the screen
/// first sees its door open under the attempt and stops only on a definite "locked" reading, the operation leaving the
/// door-open stages, or another attempt; a repeated progress event does not restart it, and nor does a reading that
/// cannot be trusted. While a slot reads unknown the banner says nothing of it -- the slot card says "状态未知" -- but its
/// clock runs on, so a Modbus link that drops now and then cannot keep the banner from ever turning red. It drives only
/// the banner: the expected-action alarm keeps its own threshold and its own report. After a restart the clocks start
/// again from zero; the interrupted operation is settled from live IO into Completed or RecoveryRequired, neither of
/// which shows the banner, which matches a vehicle no longer waiting on that door (review L1).
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
    private readonly Dictionary<int, DateTimeOffset> _doorOpenSince = [];
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
        if (operation is not
            {
                Stage: WireToGateHmiOperationStage.Unlocking or WireToGateHmiOperationStage.WaitingOperator,
                IsRecoveryVector: false
            })
        {
            _doorOpenAttemptId = null;
            _doorOpenSince.Clear();
            HideDoorOpenPromptCore();
            return;
        }

        DateTimeOffset now = Clock.Now;
        if (!string.Equals(_doorOpenAttemptId, operation.SlotOperationAttemptId, StringComparison.Ordinal))
        {
            _doorOpenAttemptId = operation.SlotOperationAttemptId;
            _doorOpenSince.Clear();
        }

        List<int> openSlots = [];
        foreach (int slot in operation.Slots.Distinct().Order())
        {
            switch (Lockers.FirstOrDefault(locker => locker.PhysicalNumber == slot))
            {
                case { IsKnown: true, IsLocked: false }:
                    _doorOpenSince.TryAdd(slot, now);
                    openSlots.Add(slot);
                    break;
                case { IsKnown: true, IsLocked: true }:
                    _doorOpenSince.Remove(slot);
                    break;
            }
        }

        if (openSlots.Count == 0)
        {
            HideDoorOpenPromptCore();
            return;
        }

        TimeSpan openFor = now - openSlots.Min(slot => _doorOpenSince[slot]);
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

    private void HideDoorOpenPromptCore()
    {
        HasDoorOpenPrompt = false;
        DoorOpenPromptTier = DoorOpenPromptTier.None;
        DoorOpenPromptHeadline = string.Empty;
        DoorOpenPromptDetail = string.Empty;
        DoorOpenPromptDimmed = false;
        _doorOpenPromptTimer?.Stop();
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
