using System.IO;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The vehicle's half of releasing a hold for an unproven door (CP-0009, REQ-0364, 8005-agv-onboard-hmi#219;
/// vector <c>CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE</c>): an administrator opens a recovery session over the held
/// slots with no demand, selects <c>HARDWARE_REPAIR_RELEASE</c>, and submits the repair record on that action.
/// </summary>
/// <remarks>
/// <para>
/// <b>No slot IO on this path</b> (<c>NEVER_SLOT_IO_FOR_REPAIR_RELEASE</c>): the server sends no command for the
/// release, and nothing here pulses a door. What lifts the hold is the server's, after the record: the fresh
/// readings it asks for (<c>SafetyStateSnapshotRequested</c>, answered as any other) and one <c>SAFE</c>
/// <c>HOLD_RELEASE</c> check (<see cref="WireToGateBusinessService"/>.<c>EvaluateHoldRelease</c>).
/// </para>
/// <para>
/// <b>The held slots are the server's</b>, read from the vehicle business state's <c>blockingFacts</c> through
/// <see cref="WireToGateDoorHoldText"/>, and the session is asked over exactly those: the server offers the release
/// only when the session's slots equal what it holds.
/// </para>
/// <para>
/// <b>Journaled before it leaves</b> (<c>JOURNAL_RELEASE_ACTION_BEFORE_SUBMITTING</c>): the request and action ids
/// are in <see cref="WireToGateRecoveryState.RepairRelease"/> before the session request goes out, the session id
/// once it is answered, and acceptance once the action is accepted. After a restart the same ids are repeated, and
/// an accepted release offers the repair record form again (<c>RESTORE_REPAIR_RECORD_FORM_AFTER_RESTART</c>) --
/// the same form a forced recovery's isolation uses, submitted against the release action instead.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string HardwareRepairReleaseAction = "HARDWARE_REPAIR_RELEASE";

    /// <summary>The reason a press with nothing typed sends.</summary>
    public const string HardwareRepairReleaseDefaultReason = "门锁已修复，申请维修放行解除扣车。";

    /// <summary>The slots the server holds for an unproven door, ascending; empty when none.</summary>
    public IReadOnlyList<int> DoorHeldSlots => WireToGateDoorHoldText.HeldSlots(_session.CurrentJourney);

    /// <summary>
    /// Whether an administrator can ask for the repair release: the server holds slots for an unproven door, no
    /// release is accepted yet, and no other recovery session stands open in its way.
    /// </summary>
    public bool CanRequestHardwareRepairRelease
    {
        get
        {
            if (!CanUseRecoveryOperator(requireProof: true) || DoorHeldSlots.Count == 0)
            {
                return false;
            }

            WireToGateRepairRelease? release = Volatile.Read(ref _lastRecoveryState).RepairRelease;
            return release is not { Accepted: true }
                && (Volatile.Read(ref _recoverySessionSnapshot) is not { State: not "CLOSED" } open
                    || string.Equals(
                        open.ExceptionRecoverySessionId,
                        release?.ExceptionRecoverySessionId,
                        StringComparison.Ordinal));
        }
    }

    /// <param name="reason">The administrator's reason for the session and the action; blank sends the default.</param>
    public Task<bool> RequestHardwareRepairReleaseAsync(
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        RunRecoveryRequestAsync(
            HardwareRepairReleaseAction,
            () => RequestHardwareRepairReleaseCoreAsync(
                ReasonOrDefault(reason, HardwareRepairReleaseDefaultReason),
                cancellationToken),
            cancellationToken);

    private async Task<bool> RequestHardwareRepairReleaseCoreAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<int> held = DoorHeldSlots;
        if (held.Count == 0)
        {
            throw new InvalidOperationException("REPAIR_RELEASE_NOT_REQUIRED");
        }

        string proof = ReadRecoveryProof();
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
        WireToGateRepairRelease? release = state.RepairRelease;
        if (release is { Accepted: true })
        {
            PublishOperatorResponse(
                "RECOVERY_ACTION_SUBMITTED",
                "维修放行已获服务端授权，请填写维修记录并提交。 ");
            return false;
        }

        WireToGateExceptionRecoverySessionSnapshot? open = Volatile.Read(ref _recoverySessionSnapshot) is
            { State: not "CLOSED" } snapshot
                ? snapshot
                : null;
        // A release on file for other slots is about a hold that has since changed; it is asked again from the start.
        if (release is not null && !release.Slots.SequenceEqual(held))
        {
            release = null;
        }

        if (release?.ExceptionRecoverySessionId is null)
        {
            // No session of the release's own on file: another one standing open is in the way, and the server would
            // refuse a second session anyway.
            if (open is not null)
            {
                throw new InvalidOperationException("RECOVERY_SESSION_NOT_READY");
            }

            WireToGateOperatorContextPayload administrator = ReadOperatorContext();
            string requestId = Guid.NewGuid().ToString("D");
            release = new WireToGateRepairRelease(
                requestId,
                requestId,
                release?.RecoveryActionId ?? Guid.NewGuid().ToString("D"),
                held.ToArray(),
                administrator.OperatorId,
                administrator.VerificationMethod,
                administrator.VerifiedAt,
                reason);
            WireToGateRepairRelease journaled = release;
            await UpdateRecoveryStateCachedAsync(
                    current => current with { RepairRelease = journaled },
                    cancellationToken)
                .ConfigureAwait(false);

            ExceptionRecoverySessionOpenedPayload opened = await _session
                .RequestExceptionRecoverySessionAsync(
                    requestId,
                    new ExceptionRecoverySessionRequestedPayload(
                        requestId,
                        administrator,
                        _recoveryOptions.AdministratorRole,
                        release.EventId,
                        null,
                        release.Slots,
                        reason,
                        proof),
                    cancellationToken)
                .ConfigureAwait(false);
            ObserveRecoverySessionAttempt(opened.ExceptionRecoverySessionId, opened.SlotOperationAttemptId);
            if (!string.Equals(opened.RequestId, requestId, StringComparison.Ordinal)
                || !string.Equals(opened.EventId, release.EventId, StringComparison.Ordinal)
                || opened.DemandId is not null
                || opened.SlotOperationAttemptId is not null
                || !opened.Slots.SequenceEqual(release.Slots))
            {
                throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
            }

            release = release with { ExceptionRecoverySessionId = opened.ExceptionRecoverySessionId };
            WireToGateRepairRelease withSession = release;
            bool replaced = false;
            await UpdateRecoveryStateCachedAsync(
                    current =>
                    {
                        replaced = false;
                        if (!string.Equals(current.RepairRelease?.RequestId, requestId, StringComparison.Ordinal))
                        {
                            replaced = true;
                            return null;
                        }

                        return current with { RepairRelease = withSession };
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (replaced)
            {
                throw new InvalidDataException("RECOVERY_SESSION_SCOPE_MISMATCH");
            }
        }
        else if (open is not null
            && !string.Equals(
                open.ExceptionRecoverySessionId,
                release.ExceptionRecoverySessionId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("RECOVERY_SESSION_NOT_READY");
        }

        string sessionId = release.ExceptionRecoverySessionId!;
        RecoveryActionAcceptedPayload accepted = await _session
            .SubmitRecoveryActionAsync(
                Guid.NewGuid().ToString("D"),
                new RecoveryActionSubmittedPayload(
                    release.RecoveryActionId,
                    sessionId,
                    HardwareRepairReleaseAction,
                    release.EventId,
                    null,
                    release.Slots,
                    new WireToGateOperatorContextPayload(
                        release.OperatorId,
                        release.OperatorVerificationMethod,
                        release.OperatorVerifiedAt),
                    release.Reason),
                cancellationToken)
            .ConfigureAwait(false);
        ObserveRecoverySessionAttempt(accepted.ExceptionRecoverySessionId, accepted.SlotOperationAttemptId);
        if (!string.Equals(accepted.RecoveryActionId, release.RecoveryActionId, StringComparison.Ordinal)
            || !string.Equals(accepted.ExceptionRecoverySessionId, sessionId, StringComparison.Ordinal)
            || accepted.AcceptedAction != HardwareRepairReleaseAction
            || accepted.SlotOperationAttemptId is not null)
        {
            throw new InvalidDataException("RECOVERY_RESPONSE_SCOPE_MISMATCH");
        }

        string actionId = release.RecoveryActionId;
        await UpdateRecoveryStateCachedAsync(
                current => current.RepairRelease is { } onFile
                    && string.Equals(onFile.RecoveryActionId, actionId, StringComparison.Ordinal)
                        ? current with { RepairRelease = onFile with { Accepted = true } }
                        : null,
                cancellationToken)
            .ConfigureAwait(false);
        PublishOperatorResponse(
            "RECOVERY_ACTION_SUBMITTED",
            $"维修放行已获服务端授权（{FormatSlots(release.Slots)}），不开任何仓门。修好门锁后填写维修记录并提交，"
                + "服务端核对新读数并做一次扣车解除检查，通过后解除扣车。 ");
        return true;
    }

    /// <summary>
    /// Sends the repair record on the accepted release action. Either answer ends the record in flight; only a
    /// <c>RECORDED</c> ends the release here. The hold itself stays until the server lifts it.
    /// </summary>
    private async Task<bool> SubmitRepairReleaseRecordCoreAsync(
        WireToGateRepairRelease release,
        string observations,
        CancellationToken cancellationToken)
    {
        RequireValidLiveSignals(release.Slots);
        WireToGatePendingHardwareRecoveryRecord record = release.PendingRecord ?? NewHardwareRecoveryRecord(observations);
        string actionId = release.RecoveryActionId;
        if (release.PendingRecord is null)
        {
            await UpdateRecoveryStateCachedAsync(
                    current => current.RepairRelease is { } onFile
                        && string.Equals(onFile.RecoveryActionId, actionId, StringComparison.Ordinal)
                            ? current with { RepairRelease = onFile with { PendingRecord = record } }
                            : null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        HardwareRecoveryRecordResultPayload result = await _session
            .SubmitHardwareRecoveryRecordAsync(
                Guid.NewGuid().ToString("D"),
                new HardwareRecoveryRecordSubmittedPayload(
                    record.RecordId,
                    release.ExceptionRecoverySessionId
                        ?? throw new InvalidDataException("RECOVERY_SESSION_SCOPE_MISMATCH"),
                    actionId,
                    new WireToGateOperatorContextPayload(
                        record.OperatorId,
                        record.OperatorVerificationMethod,
                        record.OperatorVerifiedAt),
                    record.AdministratorRole,
                    release.Slots,
                    [HardwareRecoveryCheckPerformed],
                    [HardwareRecoveryActionPerformed],
                    [record.Observations],
                    record.ObservedAt),
                cancellationToken)
            .ConfigureAwait(false);

        bool recorded = result.Outcome == "RECORDED";
        await UpdateRecoveryStateCachedAsync(
                current => current.RepairRelease is { } onFile
                    && string.Equals(onFile.RecoveryActionId, actionId, StringComparison.Ordinal)
                        ? current with { RepairRelease = recorded ? null : onFile with { PendingRecord = null } }
                        : null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!recorded)
        {
            string reason = result.Problem?.ReasonCode ?? "HARDWARE_RECOVERY_RECORD_REJECTED";
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                $"服务端拒绝维修记录：{reason}。{FormatSlots(release.Slots)}仍被扣，可修正后重新提交。 ");
            return false;
        }

        PublishOperatorResponse(
            "HARDWARE_RECOVERY_RECORDED",
            $"维修记录已由服务端记录（{FormatSlots(release.Slots)}）；服务端将核对新读数并做一次扣车解除检查，"
                + "通过后解除扣车。不会自动开任何仓门。 ");
        return true;
    }

    /// <summary>
    /// Forgets the release when the server closes its session: whatever it ended on -- recorded, voided, timed out --
    /// a record can no longer be made on it, and the entry asks again from the start.
    /// </summary>
    private async Task ForgetRepairReleaseOfClosedSessionAsync(
        string exceptionRecoverySessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await UpdateRecoveryStateCachedAsync(
                    current => current.RepairRelease is { } onFile
                        && string.Equals(
                            onFile.ExceptionRecoverySessionId,
                            exceptionRecoverySessionId,
                            StringComparison.Ordinal)
                            ? current with { RepairRelease = null }
                            : null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"维修放行所在的恢复会话已关闭，但清除放行记录失败：session={exceptionRecoverySessionId}。",
                exception);
        }
    }
}
