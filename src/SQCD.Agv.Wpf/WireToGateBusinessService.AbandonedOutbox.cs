using System.Globalization;
using System.IO;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// What this vehicle does once the control server refuses one of its durable messages with a <c>MANUAL_REVIEW</c> code
/// and the session client gives the outbox row up (onboard-hmi#254).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every type: the operator is told and the log keeps both sides' facts.</b> The refusal says the server already holds
/// something else under this message's identity. It does not say what -- a <c>ProtocolProblem</c> carries no copy of
/// the content the server kept -- so the log records what this vehicle holds and where the server's copy is to be found.
/// </para>
/// <para>
/// <b>Then, per type, whatever waited for this message's acknowledgement is given a way out that does not need an
/// edit of either database</b> (the coordinator's ruling on onboard-hmi#254):
/// </para>
/// <list type="bullet">
/// <item><c>SafetyStateChanged</c>: the change still pending is dropped and its version skipped, as for one the IO has
/// moved past (onboard-hmi#208). Kept, it was resent under the same key and refused before it went out, and the
/// snapshot request the server makes after a refused safety message (control-server#478) went unanswered for as long as
/// it stayed (<see cref="AnswerSafetyStateSnapshotRequestAsync"/>).</item>
/// <item><c>ForcedMechanicalRecoveryResult</c>: the isolation is recorded as on an acknowledgement. The server already
/// holds a result for this recovery and waits for the hardware recovery record, which only a person submits; without
/// the isolation the vehicle never offers that entry.</item>
/// <item>The other four recovery results: the vector stays, and an entry is offered for a verified maintainer to close
/// it after checking the cargo on site (<see cref="CloseConflictedRecoveryAfterReviewAsync"/>). Not closed on its own:
/// the server's conclusion and this vehicle's disagree, and settling here unseen would bury that.</item>
/// <item><c>OperationResult</c>: the attempt stays unsettled here, which keeps the recovery entry on offer
/// (<see cref="TrySettleInterruptedOperationAsync"/>).</item>
/// <item>The rest wait for nothing.</item>
/// </list>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string RecoveryVectorResultKeyPrefix = "recovery-vector-result:";
    private const string SafetyStateChangedKeyPrefix = "safety-state-changed:";

    /// <summary>The <c>SafetyStateChanged</c> given up last, until the safety work drops it as pending.</summary>
    private AbandonedSafetyChange? _abandonedSafetyChange;

    /// <summary>
    /// The result of the vector on file that was given up, while that vector is still on file: what the manual close entry
    /// is about. Found again from the outbox after a restart (<see cref="RestoreAbandonedRecoveryResultAsync"/>).
    /// </summary>
    private ConflictedRecovery? _conflictedRecovery;

    /// <summary>
    /// The recovery whose result the server refused for good and which waits for a maintainer's review on site, or
    /// <c>null</c>. Shown with <see cref="CanCloseConflictedRecoveryAfterReview"/>.
    /// </summary>
    public WireToGateConflictedRecoveryView? ConflictedRecoveryView =>
        Volatile.Read(ref _conflictedRecovery) is { } conflicted
        && StillOnFile(conflicted.Vector, Volatile.Read(ref _lastRecoveryState))
            ? new WireToGateConflictedRecoveryView(
                conflicted.Vector.VectorType,
                conflicted.Vector.PrimaryId,
                conflicted.Vector.Slots,
                conflicted.ReasonCode,
                DescribeConflictedRecovery(conflicted))
            : null;

    /// <summary>
    /// Whether a verified maintainer can close the conflicted recovery: the same operator and proof checks as the other
    /// recovery entries (<see cref="CanUseRecoveryOperator"/>).
    /// </summary>
    public bool CanCloseConflictedRecoveryAfterReview =>
        CanUseRecoveryOperator(requireProof: true) && ConflictedRecoveryView is not null;

    /// <summary>
    /// Closes the recovery whose result the server refused for good, after a verified maintainer checked the cargo on
    /// site: the vector is cleared as an acknowledged one would be, and nothing is sent. The press is logged with the
    /// maintainer's id.
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
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _conflictedRecovery) is not { } conflicted || !StillOnFile(conflicted.Vector, state))
        {
            throw new InvalidOperationException("CONFLICTED_RECOVERY_NOT_PENDING");
        }

        WireToGateRecoveryVectorContext vector = conflicted.Vector;
        _logger.Write(
            LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"维护人员现场核对后结束服务端拒收结果的恢复：operator={maintainer.OperatorId}，"
            + $"verification={maintainer.VerificationMethod}，vector={vector.VectorType}，id={vector.PrimaryId}，"
            + $"slots=[{string.Join(",", vector.Slots)}]，refusedMessageId={conflicted.MessageId}，reasonCode={conflicted.ReasonCode}。");
        if (WireToGateRecoveryVectorTypes.IsLoadCancellationBeforeSublot(vector))
        {
            // The same settlement an acknowledged result gets: the vector goes and the entry request it held is let go.
            await SettleLoadCancellationBeforeSublotAsync(vector, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CompleteRecoveryVectorStateAsync(vector, cancellationToken).ConfigureAwait(false);
        }

        Interlocked.CompareExchange(ref _conflictedRecovery, null, conflicted);
        PublishRecoveryVectorOperation(
            vector,
            WireToGateHmiOperationStage.Completed,
            "维护人员已现场核对并结束此恢复；服务端收下的结论以服务端为准。",
            "conflict-review-closed");
        PublishOperatorResponse(
            "CONFLICTED_RECOVERY_CLOSED",
            $"维护人员 {maintainer.OperatorId} 已现场核对{FormatSlots(vector.Slots)}并结束此恢复。本车与服务端对这次恢复的结论不一致，已记入日志。 ");
        return true;
    }

    private void OnDurableMessageAbandoned(
        object? sender,
        ValueChangedEventArgs<WireToGateDurableMessageAbandonment> args)
    {
        WireToGateDurableMessageAbandonment abandoned = args.Value;
        ReportAbandonment(abandoned);
        if (abandoned.DeduplicationKey.StartsWith(SafetyStateChangedKeyPrefix, StringComparison.Ordinal)
            && SafetyChangeOf(abandoned.WireLine) is { } change)
        {
            // Set here, on the thread that read the refusal and before it is thrown, so the safety work that sent it
            // finds it in its catch; dropped as pending under the safety gate, never here.
            Volatile.Write(ref _abandonedSafetyChange, change);
            if (!_disposed)
            {
                _ = Task.Run(RequestSafetyStateChange);
            }

            return;
        }

        if (abandoned.DeduplicationKey.StartsWith(RecoveryVectorResultKeyPrefix, StringComparison.Ordinal)
            && !_disposed)
        {
            // Not here: inside a handshake this runs on the handshake's thread, which waits for it.
            TrackTask(RestoreAbandonedRecoveryResultAsync(_stopping.Token));
        }
    }

    /// <summary>
    /// Tells the operator and the log that a durable message was refused for good and given up.
    /// </summary>
    private void ReportAbandonment(WireToGateDurableMessageAbandonment abandoned)
    {
        _logger.Write(
            LogSeverity.Error,
            nameof(WireToGateBusinessService),
            $"服务端以{abandoned.ReasonCode}拒收本车的持久报文，已放弃该发件箱行、不再重发，需人工核对："
            + $"messageType={abandoned.MessageType}，messageId={abandoned.MessageId}，key={abandoned.DeduplicationKey}，"
            + $"本车内容sha256={abandoned.ContentSha256}，服务端说明={abandoned.ServerDisplayMessage ?? "(无)"}。"
            + "服务端收下的那一份不随拒绝回传，请在服务端收件箱按同一messageId或业务号查看。"
            + $"本车内容：{abandoned.WireLine.TrimEnd('\r', '\n')}");
        PublishOperatorEvent(
            $"durable-message-abandoned:{abandoned.MessageId}",
            "DURABLE_MESSAGE_ABANDONED",
            $"服务端以 {abandoned.ReasonCode} 拒收本车的 {abandoned.MessageType}（messageId {abandoned.MessageId}）："
            + "服务端已收下另一份内容，两端不一致。本车已放弃这条报文、不再重发，请联系维护人员核对。 ");
    }

    /// <summary>
    /// Gives whatever waited for a recovery vector result its way out once that result was given up: run when it is
    /// given up, and on every session coming up, so a restart in between loses nothing. Does nothing when the vector on
    /// file has no given-up result.
    /// </summary>
    private async Task RestoreAbandonedRecoveryResultAsync(CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
        if (state.RecoveryVector is not { } vector)
        {
            Volatile.Write(ref _conflictedRecovery, null);
            return;
        }

        WireToGateDurableMessage? result = await _session.Journal
            .ReadOutgoingByDeduplicationKeyAsync(
                $"{RecoveryVectorResultKeyPrefix}{vector.VectorType}:{vector.PrimaryId}",
                cancellationToken)
            .ConfigureAwait(false);
        if (result is not { Abandoned: true })
        {
            return;
        }

        if (string.Equals(vector.VectorType, WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery, StringComparison.Ordinal))
        {
            // The server holds a result for this recovery and waits for the hardware record; only the isolation makes the
            // vehicle offer that entry. Recorded exactly as on an acknowledgement.
            await SettleForcedIsolationAsync(vector, cancellationToken).ConfigureAwait(false);
            PublishRecoveryVectorOperation(
                vector,
                WireToGateHmiOperationStage.RecoveryRequired,
                $"强制机械取出结果被服务端拒收（{result.AbandonedReasonCode}），两端内容不一致；{FormatSlots(vector.Slots)}物理状态未知，禁止操作，"
                + "请核对后提交硬件恢复记录。",
                "isolated-after-refusal");
            PublishOperatorEvent(
                $"forced-isolation-after-refusal:{vector.PrimaryId}",
                "DURABLE_MESSAGE_ABANDONED",
                $"强制机械取出结果被服务端拒收，已按取出完成隔离{FormatSlots(vector.Slots)}；请维护人员核对后提交硬件恢复记录。 ");
            return;
        }

        ConflictedRecovery conflicted = new(vector, result.MessageId, result.AbandonedReasonCode!);
        Volatile.Write(ref _conflictedRecovery, conflicted);
        PublishRecoveryVectorOperation(
            vector,
            WireToGateHmiOperationStage.RecoveryRequired,
            DescribeConflictedRecovery(conflicted),
            "conflict-review");
        PublishOperatorEvent(
            $"conflicted-recovery:{vector.VectorType}:{vector.PrimaryId}",
            "DURABLE_MESSAGE_ABANDONED",
            DescribeConflictedRecovery(conflicted) + " ");
    }

    /// <summary>
    /// Drops the pending safety change when it is the one given up, skipping its version, and reports whether it did.
    /// Under <see cref="_safetySendGate"/>.
    /// </summary>
    private bool ForgetAbandonedSafetyChange()
    {
        if (Volatile.Read(ref _abandonedSafetyChange) is not { } abandoned
            || _pendingSafetyChange is not { } pending
            || pending.Version != abandoned.Version
            || pending.ObservedAt.ToUniversalTime() != abandoned.ObservedAt)
        {
            return false;
        }

        _nextSafetyStateVersion = Math.Max(_nextSafetyStateVersion, checked(pending.Version + 1));
        _pendingSafetyChange = null;
        // What the server holds is not this change, so the present reading goes out as a change of its own.
        _lastSafetySignature = null;
        Interlocked.CompareExchange(ref _abandonedSafetyChange, null, abandoned);
        _logger.Write(
            LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"SafetyStateChanged（版本{pending.Version}）被服务端拒收并已放弃；跳过该版本，改报此刻读数，并回应服务端的快照请求。");
        return true;
    }

    private static AbandonedSafetyChange? SafetyChangeOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.TryGetProperty("payload", out JsonElement payload)
            && payload.TryGetProperty("safetyStateVersion", out JsonElement version)
            && version.TryGetInt64(out long safetyStateVersion)
            && payload.TryGetProperty("observedAt", out JsonElement observedAt)
            && observedAt.TryGetDateTimeOffset(out DateTimeOffset at)
                ? new AbandonedSafetyChange(safetyStateVersion, at.ToUniversalTime())
                : null;
    }

    private static bool StillOnFile(WireToGateRecoveryVectorContext vector, WireToGateRecoveryState state) =>
        state.RecoveryVector is { } onFile
        && string.Equals(onFile.VectorType, vector.VectorType, StringComparison.Ordinal)
        && string.Equals(onFile.PrimaryId, vector.PrimaryId, StringComparison.Ordinal);

    private static string DescribeConflictedRecovery(ConflictedRecovery conflicted) =>
        $"恢复结果（{conflicted.Vector.VectorType}）被服务端以 {conflicted.ReasonCode} 拒收，服务端收下的结论与本车不一致。"
        + $"请维护人员到现场核对{FormatSlots(conflicted.Vector.Slots)}的实物后，按「人工核对后结束此恢复」。";

    private sealed record AbandonedSafetyChange(long Version, DateTimeOffset ObservedAt);

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
