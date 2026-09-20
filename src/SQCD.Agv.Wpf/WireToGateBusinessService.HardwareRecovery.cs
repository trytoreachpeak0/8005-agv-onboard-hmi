using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The device half of a forced mechanical recovery: the hardware recovery record that clears the
/// slots it left physically unknown (ADR-cross-0036, REQ-0242, onboard-hmi#107).
/// </summary>
public sealed partial class WireToGateBusinessService
{
    /// <summary>
    /// What this vehicle checked itself before sending a record: the live readings of every slot in
    /// it are valid. Archived by the server, not interpreted.
    /// </summary>
    internal const string HardwareRecoveryCheckPerformed = "LIVE_SLOT_SIGNALS_VALID";

    /// <summary>
    /// What the record asserts was done: the administrator confirms the hardware was repaired.
    /// Archived by the server, not interpreted.
    /// </summary>
    internal const string HardwareRecoveryActionPerformed = "ADMINISTRATOR_CONFIRMED_HARDWARE_REPAIRED";

    /// <summary>
    /// Whether an administrator can submit the hardware recovery record for the slots a forced
    /// mechanical recovery left physically unknown.
    /// </summary>
    public bool CanSubmitHardwareRecoveryRecord =>
        CanUseRecoveryOperator(requireProof: true)
        && Volatile.Read(ref _lastRecoveryState).ForcedIsolation is not null;

    /// <summary>
    /// Submits the hardware recovery record for the forced recovery's whole slot set, and clears the
    /// set only when the server records it and the live readings of every slot are still valid.
    /// Nothing resumes on its own afterwards.
    /// </summary>
    /// <param name="observations">The administrator's account of the repair; required.</param>
    public Task<bool> SubmitHardwareRecoveryRecordAsync(
        string observations,
        CancellationToken cancellationToken = default) =>
        RunRecoveryRequestAsync(
            "HARDWARE_RECOVERY_RECORD",
            () => SubmitHardwareRecoveryRecordCoreAsync(observations, cancellationToken),
            cancellationToken);

    /// <remarks>
    /// <para>
    /// The whole set, never a subset: the server records a hardware recovery only for exactly the
    /// forced recovery's slots, and the isolation is scoped to that set, so it is cleared as one.
    /// </para>
    /// <para>
    /// Signals are checked twice. Before sending, so a record is never made for slots the vehicle
    /// cannot read; and after <c>RECORDED</c>, because clearing is this vehicle's own decision and
    /// has to rest on what it reads at that moment. Unreadable at the second check, the record stays
    /// with the server and the slots stay unknown here -- a later press asks the same record again.
    /// </para>
    /// <para>
    /// A record that went out unanswered is repeated field for field, like a load cancellation:
    /// the server keeps the first content under its recordId. A refusal leaves nothing behind there,
    /// so it is forgotten here too and the next press is a new record.
    /// </para>
    /// </remarks>
    private async Task<bool> SubmitHardwareRecoveryRecordCoreAsync(
        string observations,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken)
            .ConfigureAwait(false);
        WireToGateForcedIsolation isolation = state.ForcedIsolation
            ?? throw new InvalidOperationException("HARDWARE_RECOVERY_NOT_REQUIRED");
        RequireValidLiveSignals(isolation.PhysicallyUnknownSlots);

        WireToGatePendingHardwareRecoveryRecord record = isolation.PendingRecord ?? NewRecord();
        if (isolation.PendingRecord is null)
        {
            // This write owns ForcedIsolation and nothing else, so every other field is what the
            // journal holds when the step runs -- an executor checkpoint or a released session that
            // landed since this press read is not undone (onboard-hmi#136 point 7).
            //
            // The isolation itself is still the one read at the top of this press, and is not
            // re-checked inside the step the way SettleRecoveryVectorStateAsync re-checks its own.
            // That is the behaviour this path has today and onboard-hmi#136 did not change it. The two
            // are not the same claim: there, the write must not replace a standing isolation, which is
            // a statement about the journal at the moment of the write; here, the write records a
            // pending record inside the isolation this press is about, and a press about an isolation
            // that has since been replaced is refused upstream -- a second forced recovery cannot be
            // requested while one is uncleared.
            await UpdateRecoveryStateCachedAsync(
                    current => current with
                    {
                        ForcedIsolation = isolation with { PendingRecord = record }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        HardwareRecoveryRecordResultPayload result = await _session
            .SubmitHardwareRecoveryRecordAsync(
                Guid.NewGuid().ToString("D"),
                new HardwareRecoveryRecordSubmittedPayload(
                    record.RecordId,
                    isolation.ExceptionRecoverySessionId,
                    isolation.RecoveryActionId,
                    new WireToGateOperatorContextPayload(
                        record.OperatorId,
                        record.OperatorVerificationMethod,
                        record.OperatorVerifiedAt),
                    record.AdministratorRole,
                    isolation.PhysicallyUnknownSlots,
                    [HardwareRecoveryCheckPerformed],
                    [HardwareRecoveryActionPerformed],
                    [record.Observations],
                    record.ObservedAt),
                cancellationToken)
            .ConfigureAwait(false);

        // Kept as a read whose answer nothing uses: the writes below no longer need a copy of the
        // state, but this refresh of the cache every entry gate reads happens at this point today, and
        // a signal check below can leave by exception before any write would refresh it.
        await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
        if (result.Outcome != "RECORDED")
        {
            await UpdateRecoveryStateCachedAsync(
                    current => current with
                    {
                        ForcedIsolation = isolation with { PendingRecord = null }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            string reason = result.Problem?.ReasonCode ?? "HARDWARE_RECOVERY_RECORD_REJECTED";
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                $"服务端拒绝硬件恢复记录：{reason}。{FormatSlots(isolation.PhysicallyUnknownSlots)}仍为物理状态未知。 ");
            return false;
        }

        RequireValidLiveSignals(isolation.PhysicallyUnknownSlots);
        await UpdateRecoveryStateCachedAsync(
                current => current with { ForcedIsolation = null },
                cancellationToken)
            .ConfigureAwait(false);
        PublishOperatorResponse(
            "HARDWARE_RECOVERY_RECORDED",
            $"硬件恢复记录已由服务端记录，{FormatSlots(isolation.PhysicallyUnknownSlots)}已解除物理状态未知；不会自动续作任何操作。 ");
        return true;

        WireToGatePendingHardwareRecoveryRecord NewRecord()
        {
            if (string.IsNullOrWhiteSpace(observations))
            {
                throw new InvalidOperationException("HARDWARE_RECOVERY_OBSERVATIONS_REQUIRED");
            }

            WireToGateOperatorContextPayload administrator = ReadOperatorContext();
            return new(
                Guid.NewGuid().ToString("D"),
                administrator.OperatorId,
                administrator.VerificationMethod,
                administrator.VerifiedAt,
                _recoveryOptions.AdministratorRole,
                observations.Trim(),
                _clock.Now.ToUniversalTime());
        }
    }

    /// <summary>
    /// Throws unless the IO snapshot is connected and fresh and every one of <paramref name="slots"/>
    /// reads as known.
    /// </summary>
    private void RequireValidLiveSignals(IReadOnlyList<int> slots)
    {
        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        if (!snapshot.IsConnected
            || !SafetyRules.IsSnapshotFresh(snapshot, _clock.Now, _ioSnapshotMaxAge)
            || slots.Any(slot => !snapshot.GetLocker(slot - 1).IsKnown))
        {
            throw new InvalidOperationException("SLOT_STATE_UNKNOWN");
        }
    }
}
