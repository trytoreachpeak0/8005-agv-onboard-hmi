using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// 「不装了」 over a resumed load whose slots all read empty (8005-agv-onboard-hmi#410, agv01 2026-10-22): the way out of a
/// <c>RESUME_AFTER_REPAIR</c> run the operator has nothing to load for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is needed.</b> A resume runs over a load whose own result is with the server, so the server refuses a
/// cancellation over it and a 「剩下的不装」 alike, and the cancellation entry is shut. A resume whose door is shut empty waits
/// for 「重新开门」 (onboard-hmi#385) -- and had nothing else to press: on 10-22 it reopened the empty slot again and again
/// until the onboard program was stopped and the session closed by hand.
/// </para>
/// <para>
/// <b>What it does.</b> The executor ends the resume with what the slots read: <c>FAILED</c>, every slot EMPTY, LOCKED and
/// RESET, no door opened on the way. The server judges that replacement not reconciled and closes the session, leaving
/// the load RecoveryRequired (control-server <c>OnboardRecoveryCoordinator.AdvanceSessionAfterResultAsync</c>); the
/// recovery grant outlives the session, so 「补偿清空」 is the next press, and the line beside the entries names it. No new
/// message, no new reason code and nothing on the server changed: the replacement result is the one the resume always sends.
/// </para>
/// <para>
/// <b>Offered only where it is true.</b> The executor offers it while it waits for 「重新开门」 in a resume, with nothing
/// loaded in this run and every target slot read fresh, known, locked, output reset and empty -- see
/// <see cref="WireToGateSlotOperationExecutor.ResumeAbandonableAttempt"/>. A slot read loaded would leave cargo in a slot
/// this load no longer claims, so it is not offered then.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    /// <summary>Whether 「不装了」 is offered now.</summary>
    public bool CanAbandonResume => _executor.ResumeAbandonableAttempt is not null;

    /// <summary>
    /// The operator pressed 「不装了」. True when the executor took the press; false, and nothing happens, when it is not
    /// offered now -- not a resume, a slot that does not read empty and shut, or the wait already answered.
    /// </summary>
    public Task<bool> AbandonResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? attempt = _executor.ResumeAbandonableAttempt;
        bool taken = attempt is not null && _executor.AbandonResume(attempt);
        _logger.Write(
            taken ? LogSeverity.Information : LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            attempt is null
                ? "操作员按了「不装了」，但此刻没有可以放弃的续行，未做任何事。"
                : $"操作员按了「不装了」：attempt={attempt}，{(taken ? "已交给执行器，续行按现读的空仓收尾" : "执行器已不在等这一按，未做任何事")}。");
        return Task.FromResult(taken);
    }
}

/// <summary>The words of 「不装了」 (8005-agv-onboard-hmi#410), the one place they are written.</summary>
public static class WireToGateResumeAbandonText
{
    /// <summary>The button's caption, and the name the resume's prompt gives it.</summary>
    public const string Caption = "不装了";
}
