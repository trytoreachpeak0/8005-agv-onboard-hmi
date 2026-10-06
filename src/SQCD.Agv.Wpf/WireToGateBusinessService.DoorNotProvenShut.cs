using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// A new slot operation command over a door the journal still names as possibly open (8005-agv-onboard-hmi#267).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is refused rather than run.</b> The executor starts every new operation from a clean journal, and the active
/// unlock set is what every later handshake reports as a door that may be open. A recovery that ended without proving
/// its doors shut -- an acknowledged <c>UNKNOWN</c> vector result (<c>ForgetSettledVector</c>), a recovery a maintainer
/// ended after the manual check -- keeps the set on purpose (onboard-hmi#255), and the server may still call the vehicle
/// ready and command the next load. Run, that load would erase the record; the single-door rule would stop its pulse,
/// but only after the clean journal was written and an <c>UNKNOWN</c> result was made up for an operation that opened
/// nothing.
/// </para>
/// <para>
/// <b>The way out is the door.</b> The server only acknowledges the refusal and sends the same command again each round
/// while the session is ready, so once a fresh reading proves the door shut the next copy runs, and nobody acts on the
/// server. A reading that is stale, disconnected or cannot read the door proves nothing and refuses too.
/// </para>
/// <para>
/// <b>Told once, logged every time.</b> The command comes back about once a second; the operator is told once per
/// command and set of doors, and the log records every copy refused, as the manual-check refusal does.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string DoorNotProvenShutRefusalCode = "ACTION_NOT_ALLOWED_IN_STATE";

    private async Task RefuseOverDoorNotProvenShutAsync(
        WireToGateSlotOperationCommand command,
        WireToGateDoorNotProvenShutException refused,
        WireToGateHmiOperationSnapshot? displayBefore,
        CancellationToken cancellationToken)
    {
        string doors = string.Join(",", refused.Doors);
        string key = $"slot-operation-refused-door-not-proven-shut:{command.SlotOperationAttemptId}:{doors}";
        try
        {
            await SendOperationRejectedAsync(command, DoorNotProvenShutRefusalCode, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Whether or not the answer got out at once -- written to the outbox first, it goes with the next handshake if
            // not -- the command was not taken. The screen put it up as being prepared, so what was shown before comes
            // back. With nothing before, the command's own entry is shown as ended, which the view reads as "no operation".
            WireToGateHmiOperationSnapshot shown = displayBefore ?? new WireToGateHmiOperationSnapshot(
                command.SlotOperationAttemptId,
                command.OperationType,
                command.Slots,
                WireToGateHmiOperationStage.Completed,
                "未执行。",
                _clock.Now.ToUniversalTime());
            string slots = FormatSlots(refused.Doors);
            PublishOperatorEvent(
                key,
                "DOOR_NOT_PROVEN_SHUT",
                (refused.ReadingFresh ? string.Empty : "IO 读数过期或已断开，")
                + $"无法确认{slots}的门已关好，本车已拒收新的仓位命令，未打开任何仓门。"
                + $"请先确认{slots}的门已关好，关好后这条命令会自动重新执行。 ",
                shown);

            // Last, after the answer and the screen: once a copy is logged, nothing of its handling is left but the
            // synchronous release of its claim, so the next copy is not taken for a concurrent duplicate.
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"拒收SlotOperationCommand：日志簿记录的仓门未能确认已关好，保留记录、未写日志簿、未操作仓门。"
                + $"attempt={command.SlotOperationAttemptId}，message={command.MessageId}，slots=[{doors}]，"
                + $"ioFresh={refused.ReadingFresh}，recordedAttempt={refused.RecordedSlotOperationAttemptId ?? "无"}，"
                + $"reasonCode={DoorNotProvenShutRefusalCode}。");
        }
    }

    /// <summary>
    /// Logs what a new slot operation's journal replaced (8005-agv-onboard-hmi#267): the earlier attempt and the active
    /// unlock set -- every door of it proven shut by then -- and the vector it was written beside, which stays.
    /// </summary>
    private void LogJournalOverwrite(WireToGateJournalOverwrite overwrite) =>
        _logger.Write(
            LogSeverity.Information,
            nameof(WireToGateBusinessService),
            $"新仓位操作替换了日志簿里上一次的操作记录：attempt={overwrite.NewSlotOperationAttemptId}，"
            + $"previousAttempt={overwrite.PreviousSlotOperationAttemptId ?? "无"}，"
            + $"activeUnlockSlots=[{string.Join(",", overwrite.ClearedActiveUnlockSlots)}]（已确认关好），"
            + $"在案恢复向量（未清除）vector={overwrite.RecoveryVectorType ?? "无"}，vectorId={overwrite.RecoveryVectorPrimaryId ?? "无"}。");
}
