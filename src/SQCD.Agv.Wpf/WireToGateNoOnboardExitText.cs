using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// What the operator is told while an operation's result that is not completed waits for the server and the vehicle has no
/// exit of its own (<c>8005-agv-onboard-hmi#370</c>, agv03 2026-10-19), the one place it is written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it sends the operator to a maintainer and nowhere else.</b> Once an operation's own result that is not completed is
/// on file, the server puts it into <c>RecoveryRequired</c> and refuses a cancellation over it; every other recovery vector is
/// behind <c>recoveryResumeEnabled</c>, which vehicles ship with off. The user decided on 2026-10-19 that the way out of such a
/// stuck operation is a manual entry on the server, not that switch. So the screen says nothing on the vehicle can settle it
/// and names who can -- never the switch, a ticket or the coordinator (onboard-hmi#254's rule for operator text).
/// </para>
/// <para>
/// <b>「未知」 only when it is.</b> A result whose every slot was read -- a FAILED one a latch refused a door over, the door
/// shut and locked -- is not unknown, and calling it so tells the operator something about the slots that is not true
/// (onboard-hmi#375, review S2 of PR #371). See <see cref="WireToGateNoOnboardExit.ResultKnown"/>.
/// </para>
/// </remarks>
public static class WireToGateNoOnboardExitText
{
    private const string ContactMaintainer = "请联系系统维护人员在服务端处理。";

    /// <summary>The line the recovery area shows while the vehicle offers no exit.</summary>
    public static string Notice(OperationType operationType, bool resultKnown) =>
        $"这次{Describe(operationType)}{Outcome(resultKnown)}，已交服务端处理。车上没有可以处理它的按钮，{ContactMaintainer}";

    /// <summary>
    /// The line the recovery area shows over a load whose result waits for the server while 「补偿清空」 is offered
    /// (<c>8005-agv-onboard-hmi#410</c>, agv01 2026-10-22): the way out, in the order it is taken. The entry is offered
    /// whether or not maintenance has opened recovery for the vehicle -- the server refuses it until then -- so the line
    /// names that step first, and names 「补偿清空」 rather than 「申请恢复」, which runs the load again and opens the door.
    /// </summary>
    public static string CompensationExit(bool resultKnown) =>
        $"这次装货{Outcome(resultKnown)}，已交服务端处理。" + CompensationSteps;

    /// <summary>
    /// The line's <c>ItemStatus</c> for the screen-state snapshot: the operation and whether its result is known, for example
    /// <c>LOAD:UNKNOWN</c> or <c>UNLOAD:NOT_COMPLETED</c>.
    /// </summary>
    public static string Status(OperationType operationType, bool resultKnown) =>
        $"{(operationType == OperationType.Unload ? "UNLOAD" : "LOAD")}:{(resultKnown ? "NOT_COMPLETED" : "UNKNOWN")}";

    /// <summary>
    /// A 「取消装货」 confirmed after the load it was pressed over ended with its result handed to the server: the press did
    /// nothing, and pressing again cannot help, because that entry does not come back over this load.
    /// </summary>
    /// <param name="compensationOffered">
    /// Whether 「补偿清空」 is offered over the load: the answer then names it as what to press now, as the recovery area's line
    /// does (onboard-hmi#410, acceptance 2), instead of only sending the operator to a maintainer.
    /// </param>
    public static string LoadCancellationAfterResultHandedOver(bool resultKnown, bool compensationOffered = false) =>
        $"这次装货{Outcome(resultKnown)}，已交服务端处理，车上已不能再取消。这次取消没有执行，也没有开任何仓门。"
        + (compensationOffered ? CompensationSteps : ContactMaintainer);

    private const string CompensationSteps = "仓里没有放货的话：请联系系统维护人员开放这辆车的恢复，开放后按「补偿清空」。";

    private static string Outcome(bool resultKnown) => resultKnown ? "没有完成" : "的结果未知";

    private static string Describe(OperationType operationType) =>
        operationType == OperationType.Unload ? "卸货" : "装货";
}

/// <summary>
/// The operation the 「车上没有出口」 line is about (onboard-hmi#370, #375): its type, and whether its result is known.
/// </summary>
/// <param name="ResultKnown">
/// Whether every slot of the operation has a result on file and none of them is <c>UNKNOWN</c>: what the vehicle read of
/// each slot is then what is true of it, whether the operation ended FAILED or was stopped before a door it never opened.
/// Anything less -- a slot in doubt, or results that do not cover every slot -- is unknown.
/// </param>
/// <param name="CompensationOffered">
/// Whether 「补偿清空」 is offered over the load: the line then names it as the way out instead of saying the vehicle has
/// none (onboard-hmi#410).
/// </param>
public sealed record WireToGateNoOnboardExit(OperationType OperationType, bool ResultKnown, bool CompensationOffered = false)
{
    /// <summary>The line the screen shows.</summary>
    public string Text => CompensationOffered
        ? WireToGateNoOnboardExitText.CompensationExit(ResultKnown)
        : WireToGateNoOnboardExitText.Notice(OperationType, ResultKnown);

    /// <summary>The line's <c>ItemStatus</c>.</summary>
    public string Status => WireToGateNoOnboardExitText.Status(OperationType, ResultKnown);
}
