using System.IO;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// Settles the recovery vector on file from its result's row in the durable outbox, whichever way the server's answer
/// to that row reached it (onboard-hmi#150), and takes up a result the server refused for good (onboard-hmi#254 part 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why from the row and not from the send.</b> The send that first produced a vector's result settles the vector when
/// it returns with the <c>DurableAck</c> (<c>ExecuteRecoveryVectorAndReportAsync</c>). Three other ways of acknowledging
/// that same row have no such return: the handshake's replay of an unacknowledged row, a late ack the session settles
/// without a reconnect (onboard-hmi#250), and an ack recorded just before the process stopped. Each left the vector and
/// the recovery session's identity on file, so the same action's entry refused every press with
/// <c>RECOVERY_SESSION_STATE_PENDING</c> and every other entry stayed grey. The row is what all of them have in common:
/// once it reads acknowledged, the vector is settled exactly as the send would have settled it.
/// </para>
/// <para>
/// <b>When.</b> On every session state that brings the restore projection (after the handshake, so a replayed row has
/// had its answer), on every late ack of a vector result, and when such a row is given up. Under the recovery request
/// gate, so a press or a command executing this very vector finishes first and this then finds it gone; and every write
/// is guarded on the vector's identity, so a second caller writes nothing.
/// </para>
/// <para>
/// <b>Same outcome, same settlement, both paths.</b> <c>COMPLETED</c> (<c>ALL_EMPTY</c>, <c>HANDED_OFF</c>,
/// <c>COMPLETED</c>) settles the vector and its attempt; <c>MECHANICALLY_ISOLATED</c> records the isolation; anything
/// else forgets the vector and the session and keeps the unsettled attempt -- except the load cancellation the operator
/// started, which the send path keeps on purpose, because pressing the entry again re-executes that very vector.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string RecoveryVectorResultKeyPrefix = "recovery-vector-result:";

    private static string RecoveryVectorResultKey(WireToGateRecoveryVectorContext vector) =>
        $"{RecoveryVectorResultKeyPrefix}{vector.VectorType}:{vector.PrimaryId}";

    private static bool IsSameVector(WireToGateRecoveryVectorContext left, WireToGateRecoveryVectorContext right) =>
        string.Equals(left.VectorType, right.VectorType, StringComparison.Ordinal)
        && string.Equals(left.PrimaryId, right.PrimaryId, StringComparison.Ordinal);

    /// <summary>
    /// A late <c>DurableAck</c> of a recovery vector result: settles the vector it was about, if that is still the one on
    /// file.
    /// </summary>
    private void OnLateDurableAckReceived(object? sender, ValueChangedEventArgs<WireToGateLateDurableAck> args)
    {
        if (args.Value.Outcome == WireToGateLateDurableAckOutcome.Acknowledged
            && args.Value.DeduplicationKey.StartsWith(RecoveryVectorResultKeyPrefix, StringComparison.Ordinal)
            && !_disposed)
        {
            // Not here: this runs on the session's receive loop.
            TrackTask(SettleVectorOnFileByItsResultRowAsync(_stopping.Token));
        }
    }

    /// <summary>Reads the vector on file and settles it by its result row, if it has one to go by.</summary>
    private async Task SettleVectorOnFileByItsResultRowAsync(CancellationToken cancellationToken)
    {
        try
        {
            WireToGateRecoveryState state = await _session.Journal.ReadRecoveryStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (state.RecoveryVector is { } vector)
            {
                _ = await SettleVectorByItsResultRowAsync(vector, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                "按发件箱结果行收尾恢复向量失败，保留向量记录，下次会话再试。",
                exception);
        }
    }

    /// <summary>
    /// Settles <paramref name="vector"/> when its result's row is acknowledged, or takes it up as awaiting a manual check
    /// when the row was given up. Answers whether it did either, so the caller shows nothing more for the vector.
    /// </summary>
    /// <remarks>
    /// The row is read outside the gate, so the common case -- no row, or one still owed -- costs one read and touches
    /// nothing, the cache included: a refresh here would change what every entry gate reads on every session state.
    /// </remarks>
    private async Task<bool> SettleVectorByItsResultRowAsync(
        WireToGateRecoveryVectorContext vector,
        CancellationToken cancellationToken)
    {
        WireToGateDurableMessage? row = await _session.Journal
            .ReadOutgoingByDeduplicationKeyAsync(RecoveryVectorResultKey(vector), cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }

        if (row.Abandoned)
        {
            WireToGateRecoveryState onFile = await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
            return onFile.RecoveryVector is { } current
                && IsSameVector(current, vector)
                && ResultIsThisVectors(row, current, onFile)
                && await TakeUpGivenUpVectorResultAsync(current, row, cancellationToken).ConfigureAwait(false);
        }

        if (!row.Acknowledged)
        {
            return false;
        }

        await _recoveryRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
            if (state.RecoveryVector is not { } current || !IsSameVector(current, vector))
            {
                // Settled while this waited for the gate -- by the send itself, or by another caller of this.
                return true;
            }

            if (!ResultIsThisVectors(row, current, state))
            {
                return false;
            }

            string outcome = ResultOutcome(row);
            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"恢复向量结果的DurableAck不在首次发送时到达，按发件箱已确认的结果收尾：type={current.VectorType}，"
                + $"id={current.PrimaryId}，outcome={outcome}，messageId={row.MessageId}。");
            return await SettleAcknowledgedVectorAsync(current, outcome, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _recoveryRequestGate.Release();
        }
    }

    /// <summary>
    /// The settlement the send would have made on this acknowledged outcome. Answers <c>false</c> where the send keeps the
    /// vector, so the caller restores it as unfinished.
    /// </summary>
    private async Task<bool> SettleAcknowledgedVectorAsync(
        WireToGateRecoveryVectorContext vector,
        string outcome,
        CancellationToken cancellationToken)
    {
        if (WireToGateRecoveryVectorTypes.IsLoadCancellationBeforeSublot(vector))
        {
            await SettleLoadCancellationBeforeSublotAsync(vector, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (vector.VectorType == WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery)
        {
            if (outcome != "MECHANICALLY_ISOLATED")
            {
                return false;
            }

            await SettleForcedIsolationAsync(vector, cancellationToken).ConfigureAwait(false);
            PublishForcedIsolationAcknowledged(vector);
            return true;
        }

        if (outcome is "ALL_EMPTY" or "HANDED_OFF" or "COMPLETED")
        {
            await CompleteRecoveryVectorStateAsync(vector, cancellationToken).ConfigureAwait(false);
            PublishRecoveryVectorCompleted(vector);
            return true;
        }

        if (vector.VectorType == WireToGateRecoveryVectorTypes.LoadCancellation)
        {
            return false;
        }

        await ForgetSettledRecoveryVectorAsync(vector, cancellationToken).ConfigureAwait(false);
        PublishSettledRecoveryVectorStillUnfinished(vector);
        return true;
    }

    /// <summary>The result's <c>overallOutcome</c>, as it went on the wire.</summary>
    private static string ResultOutcome(WireToGateDurableMessage row)
    {
        using JsonDocument document = JsonDocument.Parse(row.WireLine);
        return document.RootElement.TryGetProperty("payload", out JsonElement payload)
            && payload.TryGetProperty("overallOutcome", out JsonElement outcome)
            && outcome.ValueKind == JsonValueKind.String
                ? outcome.GetString() ?? string.Empty
                : string.Empty;
    }

    /// <summary>
    /// Whether the row's result was produced by <paramref name="vector"/> as it is on file now, not only filed under its
    /// key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The key is not enough. A load correction's id is derived from the load (<c>RequestLoadCorrectionCoreAsync</c>), so
    /// a second correction of the same load after the first one's non-<c>COMPLETED</c> result was forgotten is prepared
    /// under the very key whose acknowledged row the first one left; settled from it, the second would end before its
    /// command arrived. And a recovery command that did not bind is answered under the same key with the command's own
    /// identities (<c>AnswerUnbindableCommandAsync</c>).
    /// </para>
    /// <para>
    /// What ties a result to one preparation is its observation time: preparing a vector clears
    /// <c>RecoveryResultObservedAt</c>, the executor (or the forced recovery's confirmation) stamps it with the result, and
    /// that is the time the result carries (onboard-hmi#255: replayed byte for byte). A vector prepared afresh has none;
    /// a bind refusal's answer carries the time it was built.
    /// </para>
    /// </remarks>
    private static bool ResultIsThisVectors(
        WireToGateDurableMessage row,
        WireToGateRecoveryVectorContext vector,
        WireToGateRecoveryState state)
    {
        using JsonDocument document = JsonDocument.Parse(row.WireLine);
        if (!document.RootElement.TryGetProperty("payload", out JsonElement payload)
            || state.RecoveryResultObservedAt is not { } recordedAt
            || !payload.TryGetProperty("observedAt", out JsonElement observed)
            || !observed.TryGetDateTimeOffset(out DateTimeOffset observedAt)
            || observedAt.ToUniversalTime() != recordedAt.ToUniversalTime())
        {
            return false;
        }

        bool Matches(string property, string? expected) =>
            !payload.TryGetProperty(property, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null && expected is null
            || value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), expected, StringComparison.Ordinal);

        int[] slots = payload.TryGetProperty("slotResults", out JsonElement slotResults)
            ? [.. slotResults.EnumerateArray().Select(item => item.GetProperty("slotNo").GetInt32()).Order()]
            : payload.TryGetProperty("slots", out JsonElement listed)
                ? [.. listed.EnumerateArray().Select(item => item.GetInt32()).Order()]
                : [];
        return Matches("demandId", vector.DemandId)
            && Matches("slotOperationAttemptId", vector.SlotOperationAttemptId)
            && Matches("exceptionRecoverySessionId", vector.ExceptionRecoverySessionId)
            && Matches("handoffId", vector.HandoffId)
            && slots.SequenceEqual(vector.Slots.Order());
    }

    private void PublishRecoveryVectorCompleted(WireToGateRecoveryVectorContext context)
    {
        PublishRecoveryVectorOperation(
            context,
            WireToGateHmiOperationStage.Completed,
            "恢复向量结果已确认，目标仓位已回到安全状态。",
            "completed");
        PublishOperatorEvent(
            $"recovery-vector-completed:{context.VectorType}:{context.PrimaryId}",
            "RECOVERY_VECTOR_COMPLETED",
            $"恢复向量 {context.VectorType} 已完成并收到服务端确认。 ");
    }

    private void PublishSettledRecoveryVectorStillUnfinished(WireToGateRecoveryVectorContext context) =>
        PublishOperatorEvent(
            $"recovery-vector-recovery-required:{context.VectorType}:{context.PrimaryId}",
            "OPERATION_RECOVERY_REQUIRED",
            "恢复结果已上报，但物理状态仍未达到可确认条件；请保持车辆停稳并等待下一步处理。 ");

    private void PublishForcedIsolationAcknowledged(WireToGateRecoveryVectorContext context)
    {
        PublishRecoveryVectorOperation(
            context,
            WireToGateHmiOperationStage.RecoveryRequired,
            $"强制机械取出已上报；{FormatSlots(context.Slots)}物理状态未知，禁止操作，等待提交硬件恢复记录。",
            "isolated");
        PublishOperatorEvent(
            $"forced-recovery-isolated:{context.PrimaryId}",
            "RECOVERY_VECTOR_COMPLETED",
            $"强制机械取出已由服务端确认；{FormatSlots(context.Slots)}物理状态未知，修复后请提交硬件恢复记录。 ");
    }
}
