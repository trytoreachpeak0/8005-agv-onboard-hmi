using System.Text.Json;
using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

public sealed class WireToGateSlotExecutor(
    ISlotIoProvider ioProvider,
    IOnboardExecutionJournal journal,
    TimeProvider timeProvider)
{
    public async Task<SlotOperationExecutionResult> ExecuteAsync(
        SlotOperationRequest request,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        Validate(request);
        JournalAttempt? existing = await journal.GetAsync(request.SlotOperationAttemptId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureSame(existing, request);
            if (existing.Status is JournalAttemptStatus.ResultPendingAck or JournalAttemptStatus.Completed)
            {
                SlotOperationExecutionResult? replay = JsonSerializer.Deserialize<SlotOperationExecutionResult>(
                    existing.ResultJson ?? string.Empty);
                return replay is null
                    ? Recovery(request, [], "JOURNAL_RESULT_MISSING", replay: true)
                    : replay with { Replay = true };
            }

            // Once IO may have started, never emit another unlock pulse for the same attempt.
            IReadOnlyList<SlotIoState> recovered = await SafeReadAsync(cancellationToken).ConfigureAwait(false);
            SlotOperationExecutionResult recovery = Recovery(
                request, recovered, "UNSETTLED_ATTEMPT_REQUIRES_RECONCILIATION", replay: true);
            await journal.SetStatusAsync(
                request.SlotOperationAttemptId,
                JournalAttemptStatus.RecoveryRequired,
                JsonSerializer.Serialize(recovery),
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return recovery;
        }

        int[] slots = request.TargetSlots.Distinct().Order().ToArray();
        await journal.PrepareAsync(
            new JournalAttempt(
                request.SlotOperationAttemptId,
                request.MessageId,
                request.ContentHash,
                slots,
                request.ExpectedOccupancy,
                request.ForcedRecoveryGeneration,
                JournalAttemptStatus.Prepared,
                null,
                request.RequestedAt),
            cancellationToken).ConfigureAwait(false);

        // Persist the ambiguity boundary before the first physical side effect.
        await journal.SetStatusAsync(
            request.SlotOperationAttemptId,
            JournalAttemptStatus.IoStarted,
            null,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await ioProvider.PulseUnlockAsync(slots, cancellationToken).ConfigureAwait(false);
            DateTimeOffset deadline = timeProvider.GetUtcNow() + timeout;
            while (timeProvider.GetUtcNow() <= deadline)
            {
                IReadOnlyList<SlotIoState> states = await ioProvider.ReadAllAsync(cancellationToken).ConfigureAwait(false);
                SlotIoState[] targets = states.Where(item => slots.Contains(item.PhysicalSlotNumber)).ToArray();
                if (targets.Length == slots.Length && targets.All(item => IsClosed(item, request.ExpectedOccupancy)))
                {
                    SlotOperationExecutionResult success = new(
                        request.SlotOperationAttemptId, true, false, false, states, "COMPLETED");
                    await journal.SetStatusAsync(
                        request.SlotOperationAttemptId,
                        JournalAttemptStatus.ResultPendingAck,
                        JsonSerializer.Serialize(success),
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    return success;
                }
                await Task.Delay(pollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            IReadOnlyList<SlotIoState> timedOut = await SafeReadAsync(cancellationToken).ConfigureAwait(false);
            return await PersistRecoveryAsync(request, timedOut, "PHYSICAL_CLOSURE_TIMEOUT", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
        {
            IReadOnlyList<SlotIoState> unknown = await SafeReadAsync(cancellationToken).ConfigureAwait(false);
            return await PersistRecoveryAsync(request, unknown, "IO_RESULT_UNKNOWN", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task AcknowledgeResultAsync(
        string attemptId,
        CancellationToken cancellationToken) => journal.SetStatusAsync(
            attemptId,
            JournalAttemptStatus.Completed,
            null,
            timeProvider.GetUtcNow(),
            cancellationToken);

    public async Task<IReadOnlyList<SlotIoState>> SafelyFinishActiveUnlockSetAsync(
        IReadOnlyCollection<int> activeUnlockSet,
        CancellationToken cancellationToken)
    {
        int[] allowed = activeUnlockSet.Distinct().Order().ToArray();
        if (allowed.Any(slot => slot is < 1 or > 8))
        {
            throw new ArgumentOutOfRangeException(nameof(activeUnlockSet));
        }
        // Fail-closed containment performs no new pulse. It only observes the frozen set.
        IReadOnlyList<SlotIoState> all = await ioProvider.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return all.Where(item => allowed.Contains(item.PhysicalSlotNumber)).ToArray();
    }

    private async Task<SlotOperationExecutionResult> PersistRecoveryAsync(
        SlotOperationRequest request,
        IReadOnlyList<SlotIoState> states,
        string reason,
        CancellationToken cancellationToken)
    {
        SlotOperationExecutionResult result = Recovery(request, states, reason, replay: false);
        await journal.SetStatusAsync(
            request.SlotOperationAttemptId,
            JournalAttemptStatus.RecoveryRequired,
            JsonSerializer.Serialize(result),
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<IReadOnlyList<SlotIoState>> SafeReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ioProvider.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
        {
            return Enumerable.Range(1, 8)
                .Select(slot => new SlotIoState(
                    slot, false, SlotOccupancy.Unknown, SlotDoorLock.Unknown,
                    UnlockOutputState.Unknown, timeProvider.GetUtcNow()))
                .ToArray();
        }
    }

    private static bool IsClosed(SlotIoState state, SlotOccupancy expected) =>
        state.Online && state.Occupancy == expected && state.DoorLock == SlotDoorLock.Locked &&
        state.UnlockOutput == UnlockOutputState.Reset;

    private static SlotOperationExecutionResult Recovery(
        SlotOperationRequest request,
        IReadOnlyList<SlotIoState> states,
        string reason,
        bool replay) => new(request.SlotOperationAttemptId, false, replay, true, states, reason);

    private static void Validate(SlotOperationRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SlotOperationAttemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ContentHash);
        int[] slots = request.TargetSlots.Distinct().ToArray();
        if (slots.Length == 0 || slots.Length != request.TargetSlots.Count || slots.Any(slot => slot is < 1 or > 8) ||
            request.ExpectedOccupancy == SlotOccupancy.Unknown)
        {
            throw new ArgumentException("Slot operation identity or physical target is invalid.", nameof(request));
        }
    }

    private static void EnsureSame(JournalAttempt existing, SlotOperationRequest request)
    {
        if (existing.MessageId != request.MessageId || existing.ContentHash != request.ContentHash ||
            existing.ForcedRecoveryGeneration != request.ForcedRecoveryGeneration ||
            existing.ExpectedOccupancy != request.ExpectedOccupancy ||
            !existing.TargetSlots.SequenceEqual(request.TargetSlots.Order()))
        {
            throw new InvalidDataException("SlotOperationAttemptId replay has conflicting content.");
        }
    }
}
