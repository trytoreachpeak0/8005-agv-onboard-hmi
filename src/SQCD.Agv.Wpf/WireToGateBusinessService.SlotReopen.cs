using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The operator's 「重新开门」 over a door shut on the opposite occupancy (8005-agv-onboard-hmi#385, CP-0019): the executor
/// holds the slot shut and asks; this offers the press while it asks and hands the press over.
/// </summary>
/// <remarks>
/// <para>
/// <b>Offered while the executor waits for it, and not while a load cancellation over the same attempt is open.</b> The
/// executor publishes the wait before its first prompt and takes it off the moment a press answers it, so the button is not
/// shown before a press can be taken and a second press of the same wait finds nothing. A cancellation pressed and waiting
/// for its answer, or authorized and running, owns the attempt: a reopen beside it would open the door the cancellation is
/// about to empty.
/// </para>
/// <para>
/// <b>No operator id, no confirmation of its own.</b> It is the reopen the vehicle used to do by itself after every such
/// close, now done when the person at the door asks for it. The pulse it leads to goes through every check any pulse goes
/// through: the safety hold, the one-door rule (REQ-0357), the fatal-fault latch and the declaration gate.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    /// <summary>Whether 「重新开门」 is offered now.</summary>
    public bool CanReopenWaitingSlot => ReopenableWait() is not null;

    /// <summary>
    /// The waiting state for the screen-state snapshot: <c>SHUT_OVER_OPPOSITE:&lt;slot&gt;</c> or
    /// <c>REOPEN_NOT_RELEASED:&lt;slot&gt;</c> while 「重新开门」 is offered, otherwise empty.
    /// </summary>
    public string SlotReopenStatus => ReopenableWait() is { } wait
        ? $"{(wait.Cause == WireToGatePromptCause.ReopenNotReleased ? "REOPEN_NOT_RELEASED" : "SHUT_OVER_OPPOSITE")}:{wait.PhysicalSlot}"
        : string.Empty;

    /// <summary>
    /// The operator pressed 「重新开门」. True when the executor took the press; false, and nothing happens, when it no longer
    /// waits for one -- the wait ended, another press answered it first, or a load cancellation took the attempt over.
    /// </summary>
    public Task<bool> RequestSlotReopenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WireToGateOperatorRequestWait? wait = ReopenableWait();
        bool taken = wait is not null
            && _executor.SubmitOperatorRequest(
                wait.SlotOperationAttemptId,
                wait.PhysicalSlot,
                WireToGateOperatorSlotRequest.Reopen);
        _logger.Write(
            taken ? LogSeverity.Information : LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            wait is null
                ? "操作员按了「重新开门」，但此刻没有在等这一按的仓，未开锁。"
                : $"操作员按了「重新开门」：attempt={wait.SlotOperationAttemptId}，slot={wait.PhysicalSlot}，"
                    + $"cause={wait.Cause}，{(taken ? "已交给执行器" : "执行器已不在等这一按，未开锁")}。");
        return Task.FromResult(taken);
    }

    private WireToGateOperatorRequestWait? ReopenableWait()
    {
        if (_executor.OperatorRequestWait is not { } wait)
        {
            return null;
        }

        WireToGateRecoveryState state = Volatile.Read(ref _lastRecoveryState);
        bool cancellationOwnsIt = string.Equals(
                state.PendingLoadCancellation?.SlotOperationAttemptId,
                wait.SlotOperationAttemptId,
                StringComparison.Ordinal)
            || string.Equals(
                state.RecoveryVector?.SlotOperationAttemptId,
                wait.SlotOperationAttemptId,
                StringComparison.Ordinal);
        return cancellationOwnsIt ? null : wait;
    }
}
