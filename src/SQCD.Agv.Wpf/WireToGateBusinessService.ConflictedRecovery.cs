using System.IO;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// A recovery vector whose result the control server refused for good and the outbox gave up (onboard-hmi#254): what
/// stands in for the acknowledgement that will never come (part 2, folded into onboard-hmi#150).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why nothing settles it on its own.</b> The two refusals a recovery result can meet are
/// <c>MESSAGE_ID_CONTENT_CONFLICT</c> and <c>BUSINESS_ID_CONTENT_CONFLICT</c> (control-server <c>WireToGateStore.cs</c>
/// 2388/2398, <c>OnboardRecoveryCoordinator.cs</c> 168/204-207/1949-1963): the server holds another conclusion for this
/// recovery, or this result does not match the one it has on file. Either way the two ends disagree, and settling here
/// unseen would bury that.
/// </para>
/// <para>
/// <b>The forced mechanical recovery goes on to its isolation</b>, exactly as on an acknowledgement, removing from the
/// active unlock set only the doors the isolation covers (onboard-hmi#255). What clears it is the hardware recovery
/// record, which a person submits anyway.
/// </para>
/// <para>
/// <b>The other four wait for a verified maintainer</b> to check the cargo on site and end the recovery here
/// (<see cref="CloseConflictedRecoveryAfterReviewAsync"/>). The entry is not behind <c>recoveryResumeEnabled</c>: that
/// switch governs starting a recovery from the HMI, and this one starts nothing, sends nothing and opens no door; gating
/// it would mean changing the vehicle's configuration to get out. It still asks for the operator's id and the recovery
/// proof, as every maintenance entry does. Ending it is written the way an acknowledged non-<c>COMPLETED</c> result is
/// (<see cref="ForgetSettledVector"/>): the vector and the recovery session go, the unsettled attempt, its context and
/// the active unlock set stay, so the server -- which holds the conclusion -- decides what the attempt still needs, and
/// while it holds the operation in <c>RecoveryRequired</c> the recovery entries find it here. The load cancellation keeps
/// its attempt too: the aborted load's, whose conclusion is the cancellation's (ADR-cross-0046). Its result's row in the
/// outbox marks it so, and the interrupted settlement and the cancellation entry both read that row
/// (<see cref="IsCancellationConcludedAsync"/>), so nothing is reported for the load and nothing is cancelled twice.
/// </para>
/// <para>
/// <b>Not behind onboard-hmi#255's door gate.</b> That gate refuses a press that would prepare a vector over a door in
/// doubt, because preparing one used to drop the door from the active set. Ending this recovery prepares nothing and
/// leaves the set exactly as it is, so the door stays reported in every handshake and the next recovery press is still
/// gated on it; a door that will not shut is no reason to keep a recovery the server has already concluded.
/// </para>
/// <para>
/// <b>Until it is ended, a new slot operation command is refused</b> with <c>ACTION_NOT_ALLOWED_IN_STATE</c>. The server
/// takes a reported attempt whose operation it has settled off the pending facts
/// (<c>WireToGateStore.TryTakeOffSettledReportedAttemptsAsync</c>) and may then answer READY and command a new load; run,
/// it would start from a fresh journal over the refused vector. The server only acknowledges such a refusal and sends the
/// same command again each round while the session is ready (<c>OnboardMessageProcessor.cs</c> 533-538,
/// <c>JourneyRuntimeEngine.cs</c> 1080-1084), so the command that comes after the manual check is executed with no one
/// acting on the server.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string AwaitingCheckRefusalCode = "ACTION_NOT_ALLOWED_IN_STATE";

    /// <summary>
    /// The vector on file whose result was given up, as last taken up; read through <see cref="ConflictedRecoveryView"/>,
    /// which drops it once another vector, or none, is on file.
    /// </summary>
    private ConflictedRecovery? _conflictedRecovery;

    /// <summary>
    /// The recovery whose result the server refused for good and which waits for a maintainer's manual check, or
    /// <c>null</c>.
    /// </summary>
    public WireToGateConflictedRecoveryView? ConflictedRecoveryView =>
        Volatile.Read(ref _conflictedRecovery) is { } conflicted
        && Volatile.Read(ref _lastRecoveryState).RecoveryVector is { } onFile
        && IsSameVector(onFile, conflicted.Vector)
            ? new WireToGateConflictedRecoveryView(
                conflicted.Vector.VectorType,
                conflicted.Vector.PrimaryId,
                conflicted.Vector.Slots,
                conflicted.ReasonCode,
                DescribeConflictedRecovery(conflicted.Vector, conflicted.ReasonCode))
            : null;

    /// <summary>
    /// Whether a maintainer can end the conflicted recovery now: an operator id and the recovery proof, and no
    /// maintenance switch.
    /// </summary>
    public bool CanCloseConflictedRecoveryAfterReview =>
        ConflictedRecoveryView is not null
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(_operatorIdEnvironmentVariable))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            _recoveryOptions.AuthenticationProofEnvironmentVariable));

    /// <summary>
    /// Ends the recovery whose result the server refused for good, after a verified maintainer checked the cargo on site.
    /// Nothing is sent and no door is touched; the press is logged with the maintainer's id.
    /// </summary>
    public Task<bool> CloseConflictedRecoveryAfterReviewAsync(CancellationToken cancellationToken = default) =>
        RunRecoveryRequestAsync(
            "CONFLICTED_RECOVERY_REVIEW",
            () => CloseConflictedRecoveryAfterReviewCoreAsync(cancellationToken),
            cancellationToken);

    private async Task<bool> CloseConflictedRecoveryAfterReviewCoreAsync(CancellationToken cancellationToken)
    {
        WireToGateOperatorContextPayload maintainer = ReadOperatorContext();
        _ = ReadRecoveryProof();

        // Asked of the journal and the outbox, not of what the screen showed: another press, or the server's next
        // command, may have moved on since.
        WireToGateRecoveryState state = await _session.Journal.ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state.RecoveryVector is not { } vector
            || vector.VectorType == WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery
            || await GivenUpResultOfAsync(vector, state, cancellationToken).ConfigureAwait(false) is not { } refused)
        {
            throw new InvalidOperationException("CONFLICTED_RECOVERY_NOT_PENDING");
        }

        WireToGateRecoveryState? ended = await _session.Journal.UpdateRecoveryStateAsync(
                current => current.RecoveryVector is { } onFile && IsSameVector(onFile, vector)
                    ? EndAfterManualCheck(current, onFile)
                    : null,
                CacheRecoveryState,
                cancellationToken)
            .ConfigureAwait(false);
        if (ended is null)
        {
            throw new InvalidOperationException("CONFLICTED_RECOVERY_NOT_PENDING");
        }

        if (Volatile.Read(ref _conflictedRecovery) is { } shown && IsSameVector(shown.Vector, vector))
        {
            Interlocked.CompareExchange(ref _conflictedRecovery, null, shown);
        }

        if (vector.VectorType == WireToGateRecoveryVectorTypes.LoadCancellation
            && vector.SlotOperationAttemptId is { } cancelled)
        {
            Volatile.Write(ref _cancellationConcludedAttemptId, cancelled);
        }

        _logger.Write(
            LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"维护人员现场核对后结束服务端拒收结果的恢复：operator={maintainer.OperatorId}，"
            + $"verification={maintainer.VerificationMethod}，vector={vector.VectorType}，id={vector.PrimaryId}，"
            + $"slots=[{string.Join(",", vector.Slots)}]，refusedMessageId={refused.MessageId}，"
            + $"reasonCode={refused.AbandonedReasonCode}，activeUnlockSlots=[{string.Join(",", ended.ActiveUnlockSlots)}]，"
            + $"unsettledAttempt={ended.UnsettledSlotOperationAttemptId ?? "null"}。未发送任何报文，未操作仓门。");
        PublishRecoveryVectorOperation(
            vector,
            WireToGateHmiOperationStage.Completed,
            "维护人员已现场核对并结束此恢复；这次恢复的结论以服务端为准。",
            "conflict-review-closed");
        PublishOperatorResponse(
            "CONFLICTED_RECOVERY_CLOSED",
            $"维护人员 {maintainer.OperatorId} 已现场核对{FormatSlots(vector.Slots)}并结束此恢复。"
            + "本车与服务端对这次恢复的结论不一致，已记入日志。 ");
        return true;
    }

    /// <summary>
    /// <paramref name="state"/> once a maintainer ended <paramref name="vector"/> after a manual check. Never writes the
    /// active unlock set: a door in doubt stays reported (onboard-hmi#255).
    /// </summary>
    private static WireToGateRecoveryState EndAfterManualCheck(
        WireToGateRecoveryState state,
        WireToGateRecoveryVectorContext vector)
    {
        WireToGateRecoveryState ended = ForgetSettledVector(state, vector);
        return WireToGateRecoveryVectorTypes.IsLoadCancellationBeforeSublot(vector)
            ? ended with { PendingLoadCancellation = null }
            : ended;
    }

    /// <summary>
    /// The aborted load whose cancellation has a result on file, as last seen in this process; the journal-backed answer
    /// is <see cref="IsCancellationConcludedAsync"/>. Read by the cancellation entry, which reads cached state only.
    /// </summary>
    private string? _cancellationConcludedAttemptId;

    /// <summary>
    /// Whether the load <paramref name="slotOperationAttemptId"/> was taken over by a load cancellation that has a result
    /// on file -- acknowledged, owed or given up. Such an attempt's conclusion is the cancellation's (ADR-cross-0046): it
    /// is never settled as an interrupted load, and never cancelled again. The row is the mark and nothing else records
    /// it; <c>PendingLoadCancellation</c> could not be, because the interrupted settlement resends what that names.
    /// </summary>
    private async Task<bool> IsCancellationConcludedAsync(
        string demandId,
        string slotOperationAttemptId,
        CancellationToken cancellationToken)
    {
        bool concluded = await _session.Journal
            .ReadOutgoingByDeduplicationKeyAsync(
                $"{RecoveryVectorResultKeyPrefix}{WireToGateRecoveryVectorTypes.LoadCancellation}:"
                + InFlightLoadCancellationId(demandId, slotOperationAttemptId),
                cancellationToken)
            .ConfigureAwait(false) is not null;
        if (concluded)
        {
            Volatile.Write(ref _cancellationConcludedAttemptId, slotOperationAttemptId);
        }

        return concluded;
    }

    private bool IsCancellationConcluded(string slotOperationAttemptId) =>
        string.Equals(Volatile.Read(ref _cancellationConcludedAttemptId), slotOperationAttemptId, StringComparison.Ordinal);

    /// <summary>
    /// Takes up a vector whose result row was given up: the forced mechanical recovery is isolated, the other four wait for
    /// the manual check. Answers whether the vector was taken up, so the restore shows nothing more for it.
    /// </summary>
    private async Task<bool> TakeUpGivenUpVectorResultAsync(
        WireToGateRecoveryVectorContext vector,
        WireToGateDurableMessage row,
        CancellationToken cancellationToken)
    {
        string reasonCode = row.AbandonedReasonCode!;
        if (vector.VectorType != WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery)
        {
            Volatile.Write(ref _conflictedRecovery, new ConflictedRecovery(vector, row.MessageId, reasonCode));
            string text = DescribeConflictedRecovery(vector, reasonCode);
            PublishRecoveryVectorOperation(vector, WireToGateHmiOperationStage.RecoveryRequired, text, "conflict-review");
            PublishOperatorEvent(
                $"conflicted-recovery:{vector.VectorType}:{vector.PrimaryId}",
                "CONFLICTED_RECOVERY_PENDING",
                text + " ");
            return true;
        }

        await _recoveryRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
            if (state.RecoveryVector is not { } current || !IsSameVector(current, vector))
            {
                return true;
            }

            await SettleForcedIsolationAsync(current, cancellationToken).ConfigureAwait(false);
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"强制机械取出结果被服务端以{reasonCode}拒收，已按取出完成记录隔离：id={current.PrimaryId}，"
                + $"slots=[{string.Join(",", current.Slots)}]，refusedMessageId={row.MessageId}。服务端若没有收下这次强制恢复的结果，"
                + "之后的硬件恢复记录可能被拒，届时隔离只能在服务端结束这次恢复后清除。");
            PublishRecoveryVectorOperation(
                current,
                WireToGateHmiOperationStage.RecoveryRequired,
                $"强制机械取出结果被服务端拒收（{reasonCode}），两端内容不一致；{FormatSlots(current.Slots)}物理状态未知，禁止操作，"
                + "请维护人员核对后提交硬件恢复记录。",
                "isolated-after-refusal");
            PublishOperatorEvent(
                $"forced-isolation-after-refusal:{current.PrimaryId}",
                "CONFLICTED_RECOVERY_PENDING",
                $"强制机械取出结果被服务端拒收，已按取出完成隔离{FormatSlots(current.Slots)}；请维护人员核对后提交硬件恢复记录。 ");
            return true;
        }
        finally
        {
            _recoveryRequestGate.Release();
        }
    }

    /// <summary>
    /// Refuses a new slot operation command while a recovery waits for its manual check, and answers whether it did.
    /// </summary>
    /// <remarks>
    /// Read from the journal and the outbox rather than from <see cref="_conflictedRecovery"/>: a command can come in the
    /// moment the session is ready, before the restore that takes the vector up has run. A command for the vector's own
    /// attempt is not this refusal's: it is recognised as taken over further on, as it always was.
    /// </remarks>
    private async Task<bool> RefuseWhileRecoveryAwaitsManualCheckAsync(
        WireToGateSlotOperationCommand command,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await _session.Journal.ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state.RecoveryVector is not { } vector
            || vector.VectorType == WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery
            || string.Equals(vector.SlotOperationAttemptId, command.SlotOperationAttemptId, StringComparison.Ordinal)
            || await GivenUpResultOfAsync(vector, state, cancellationToken).ConfigureAwait(false) is not { } refused)
        {
            return false;
        }

        _logger.Write(
            LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"拒收SlotOperationCommand：上一次恢复的结果被服务端拒收，正等待维护人员现场核对。attempt={command.SlotOperationAttemptId}，"
            + $"message={command.MessageId}，vector={vector.VectorType}，id={vector.PrimaryId}，"
            + $"refusedMessageId={refused.MessageId}，reasonCode={AwaitingCheckRefusalCode}。未操作仓门。");
        await SendOperationRejectedAsync(command, AwaitingCheckRefusalCode, cancellationToken).ConfigureAwait(false);
        PublishOperatorEvent(
            $"conflicted-recovery-command-refused:{command.SlotOperationAttemptId}",
            "CONFLICTED_RECOVERY_PENDING",
            "服务端下发了新的仓位命令，但上一次恢复的结果被服务端拒收、正等待维护人员现场核对，本车已拒收这条命令，未打开任何仓门。"
            + "核对后按「人工核对后结束此恢复」，服务端会重新下发这条命令。 ");
        return true;
    }

    /// <summary>
    /// The given-up result <paramref name="vector"/> itself produced, or <c>null</c>. A vector prepared afresh under the
    /// key of one given up earlier -- a second correction of the same load -- has produced none
    /// (<see cref="ResultIsThisVectors"/>).
    /// </summary>
    private async Task<WireToGateDurableMessage?> GivenUpResultOfAsync(
        WireToGateRecoveryVectorContext vector,
        WireToGateRecoveryState state,
        CancellationToken cancellationToken) =>
        await _session.Journal
            .ReadOutgoingByDeduplicationKeyAsync(RecoveryVectorResultKey(vector), cancellationToken)
            .ConfigureAwait(false) is { Abandoned: true } row
        && ResultIsThisVectors(row, vector, state)
            ? row
            : null;

    private static string DescribeConflictedRecovery(WireToGateRecoveryVectorContext vector, string reasonCode) =>
        $"恢复结果（{vector.VectorType}）被服务端以 {reasonCode} 拒收，服务端收下的结论与本车不一致。"
        + $"请维护人员到现场核对{FormatSlots(vector.Slots)}的实物（货物在不在、仓门是否关好上锁）后，按「人工核对后结束此恢复」。";

    private sealed record ConflictedRecovery(
        WireToGateRecoveryVectorContext Vector,
        string MessageId,
        string ReasonCode);
}

/// <summary>
/// A recovery whose result the control server refused for good, as the HMI shows it (onboard-hmi#254).
/// </summary>
public sealed record WireToGateConflictedRecoveryView(
    string VectorType,
    string PrimaryId,
    IReadOnlyList<int> Slots,
    string ReasonCode,
    string Text);
