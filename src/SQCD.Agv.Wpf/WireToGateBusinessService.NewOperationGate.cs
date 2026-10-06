using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// What a new slot operation command meets before it may start (8005-agv-onboard-hmi#267): a door the journal still names
/// as possibly open, and a recovery vector still on file.
/// </summary>
/// <remarks>
/// <para>
/// <b>A door not proven shut refuses.</b> The active unlock set is what every later handshake reports as a door that may
/// be open, and a new operation replaces it. A recovery that ended without proving its doors shut -- an acknowledged
/// <c>UNKNOWN</c> vector result (<c>ForgetSettledVector</c>), a recovery a maintainer ended after the manual check --
/// keeps the set on purpose (onboard-hmi#255), and the server may still call the vehicle ready and command the next
/// load. Run, that load would erase the record; the single-door rule would stop its pulse, but only after the journal
/// was written and an <c>UNKNOWN</c> result made up for an operation that opened nothing. A reading that is stale,
/// disconnected or cannot read the door proves nothing and refuses too.
/// </para>
/// <para>
/// <b>A vector on file is settled first, or refuses.</b> A checkpoint carries every field it does not own as the journal
/// holds it (onboard-hmi#136), the vector among them, so a new operation used to be written beside the vector, and the
/// late acknowledgement of the vector's result then settled the new attempt as the vector's. When the vector's own
/// result is already in the outbox the executor takes the vector off in the operation's first write; when it has none
/// yet, the command is refused. A vector whose result was given up never gets this far: the manual-check refusal answers
/// first (<see cref="RefuseWhileRecoveryAwaitsManualCheckAsync"/>).
/// </para>
/// <para>
/// <b>The way out needs no one at the server.</b> The server only acknowledges the refusal and sends the same command
/// again each round while the session is ready, so once the door is proven shut, or the vector has left the journal, the
/// next copy runs. The operator is told once per command and cause; the log records every copy refused, as the
/// manual-check refusal does.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string NewOperationRefusalCode = "ACTION_NOT_ALLOWED_IN_STATE";

    private Task RefuseOverDoorNotProvenShutAsync(
        WireToGateSlotOperationCommand command,
        WireToGateDoorNotProvenShutException refused,
        WireToGateHmiOperationSnapshot? displayBefore,
        CancellationToken cancellationToken)
    {
        string doors = string.Join(",", refused.Doors);
        string slots = FormatSlots(refused.Doors);
        return RefuseNewOperationAsync(
            command,
            displayBefore,
            $"slot-operation-refused-door-not-proven-shut:{command.SlotOperationAttemptId}:{doors}",
            "DOOR_NOT_PROVEN_SHUT",
            (refused.ReadingFresh ? string.Empty : "IO 读数过期或已断开，")
            + $"无法确认{slots}的门已关好，本车已拒收新的仓位命令，未打开任何仓门。"
            + $"请先确认{slots}的门已关好，关好后这条命令会自动重新执行。 ",
            $"拒收SlotOperationCommand：日志簿记录的仓门未能确认已关好，保留记录、未写日志簿、未操作仓门。"
            + $"attempt={command.SlotOperationAttemptId}，message={command.MessageId}，slots=[{doors}]，"
            + $"ioFresh={refused.ReadingFresh}，recordedAttempt={refused.RecordedSlotOperationAttemptId ?? "无"}，"
            + $"reasonCode={NewOperationRefusalCode}。",
            cancellationToken);
    }

    private Task RefuseOverUnsettledRecoveryVectorAsync(
        WireToGateSlotOperationCommand command,
        WireToGateRecoveryVectorUnsettledException refused,
        WireToGateHmiOperationSnapshot? displayBefore,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryVectorContext vector = refused.Vector;
        return RefuseNewOperationAsync(
            command,
            displayBefore,
            $"slot-operation-refused-vector-unsettled:{command.SlotOperationAttemptId}:{vector.VectorType}:{vector.PrimaryId}",
            "RECOVERY_VECTOR_UNSETTLED",
            $"上一次{RecoveryKindName(vector.VectorType)}还没有结束，本车已拒收新的仓位命令，未打开任何仓门。"
            + "上一次恢复处理结束后，这条命令会自动重新执行。 ",
            $"拒收SlotOperationCommand：日志簿上的恢复向量还没有它自己的结果，保留记录、未写日志簿、未操作仓门。"
            + $"attempt={command.SlotOperationAttemptId}，message={command.MessageId}，vector={vector.VectorType}，"
            + $"id={vector.PrimaryId}，vectorAttempt={vector.SlotOperationAttemptId ?? "无"}，"
            + $"reasonCode={NewOperationRefusalCode}。",
            cancellationToken);
    }

    private async Task RefuseNewOperationAsync(
        WireToGateSlotOperationCommand command,
        WireToGateHmiOperationSnapshot? displayBefore,
        string eventKey,
        string eventKind,
        string operatorText,
        string logLine,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendOperationRejectedAsync(command, NewOperationRefusalCode, cancellationToken).ConfigureAwait(false);
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
            PublishOperatorEvent(eventKey, eventKind, operatorText, shown);

            // Last, after the answer and the screen: once a copy is logged, nothing of its handling is left but the
            // synchronous release of its claim, so the next copy is not taken for a concurrent duplicate.
            _logger.Write(LogSeverity.Warning, nameof(WireToGateBusinessService), logLine);
        }
    }

    /// <summary>
    /// The outbox key of the result <paramref name="vector"/> produced itself and that was not given up, or <c>null</c>:
    /// what the executor asks before it settles a vector on file ahead of a new operation (8005-agv-onboard-hmi#267).
    /// </summary>
    /// <remarks>
    /// The row under the vector's key is not enough: a vector prepared afresh under the key of an earlier one's row has
    /// produced nothing yet (<see cref="ResultIsThisVectors"/>).
    /// </remarks>
    private async Task<string?> VectorResultKeyOnFileAsync(
        WireToGateRecoveryVectorContext vector,
        WireToGateRecoveryState state,
        CancellationToken cancellationToken)
    {
        string key = RecoveryVectorResultKey(vector);
        WireToGateDurableMessage? row = await _session.Journal
            .ReadOutgoingByDeduplicationKeyAsync(key, cancellationToken)
            .ConfigureAwait(false);
        return row is { Abandoned: false } && ResultIsThisVectors(row, vector, state) ? key : null;
    }

    /// <summary>
    /// Logs what a new slot operation's first write replaced (8005-agv-onboard-hmi#267): the earlier attempt, the active
    /// unlock set -- every door of it proven shut by then -- and the recovery vector it settled first.
    /// </summary>
    private void LogJournalOverwrite(WireToGateJournalOverwrite overwrite)
    {
        _logger.Write(
            LogSeverity.Information,
            nameof(WireToGateBusinessService),
            $"新仓位操作替换了日志簿里上一次的操作记录：attempt={overwrite.NewSlotOperationAttemptId}，"
            + $"previousAttempt={overwrite.PreviousSlotOperationAttemptId ?? "无"}，"
            + $"activeUnlockSlots=[{string.Join(",", overwrite.ClearedActiveUnlockSlots)}]（已确认关好）。");
        if (overwrite.ClearedRecoveryVector is { } vector)
        {
            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"新仓位操作起步前先收尾在案的恢复向量（其结果已在发件箱），连同恢复会话一并去掉："
                + $"attempt={overwrite.NewSlotOperationAttemptId}，vector={vector.VectorType}，id={vector.PrimaryId}，"
                + $"vectorAttempt={vector.SlotOperationAttemptId ?? "无"}，resultKey={overwrite.ClearedRecoveryVectorResultKey}。");
        }
    }
}
