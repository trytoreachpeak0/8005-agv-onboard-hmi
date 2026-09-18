namespace SQCD.Agv.Core;

/// <summary>
/// The slot a LOAD or UNLOAD is waiting on the operator for, and when this operation first unlocked it
/// (REQ-0358, CP-0005 implementation ticket 1, 8005-agv-onboard-hmi#109).
/// </summary>
public sealed record SlotExpectedActionWait(
    string SlotOperationAttemptId,
    OperationType OperationType,
    int PhysicalSlotNumber,
    DateTimeOffset FirstUnlockAt);

/// <summary>
/// Remembers when the current slot was first unlocked, from the executor's own progress reports.
/// </summary>
/// <remarks>
/// <para>
/// <b>Counted per slot from its first unlock, and a reopen does not reset it.</b> An unload whose light
/// curtain sticks at "occupied" goes shut, reopen, shut forever; a clock restarted on every reopen would
/// never reach the threshold, which is exactly the case REQ-0358 exists for.
/// </para>
/// <para>
/// <b>It only watches.</b> The executor is not asked anything and not told anything: the report changes
/// no behaviour -- no failure, no recovery, no stop to the closed loop, no change to the station deadline
/// (ADR-cross-0062 decision 1). Nothing is persisted either: a restart settles the interrupted attempt from
/// the live IO (<c>SettleInterruptedAsync</c>), so there is no wait left to resume timing.
/// </para>
/// <para>
/// A projection counts as waiting only when it names exactly one active slot, which is what the formal
/// executor reports while it unlocks or waits (REQ-0357, one door at a time). Every other projection --
/// the slot closing its loop, UNKNOWN, the result, a cancellation or recovery vector taking over -- stops
/// the clock.
/// </para>
/// </remarks>
public sealed class SlotExpectedActionWaitTracker
{
    private readonly object _gate = new();
    private SlotExpectedActionWait? _current;

    public SlotExpectedActionWait? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <param name="operation">The projection just published; its <c>ObservedAt</c> is taken as now.</param>
    /// <param name="activeSlots">
    /// The executor's active slots for a progress report; <c>null</c> for any projection that is not one.
    /// </param>
    public void Observe(WireToGateHmiOperationSnapshot operation, IReadOnlyList<int>? activeSlots)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            if (!IsWaitingStage(operation.Stage) || activeSlots is not [int slot])
            {
                _current = null;
                return;
            }

            if (_current is { } current
                && string.Equals(current.SlotOperationAttemptId, operation.SlotOperationAttemptId, StringComparison.Ordinal)
                && current.PhysicalSlotNumber == slot)
            {
                return;
            }

            _current = new(operation.SlotOperationAttemptId, operation.OperationType, slot, operation.ObservedAt);
        }
    }

    internal static bool IsWaitingStage(WireToGateHmiOperationStage stage) =>
        stage is WireToGateHmiOperationStage.Unlocking or WireToGateHmiOperationStage.WaitingOperator;
}
