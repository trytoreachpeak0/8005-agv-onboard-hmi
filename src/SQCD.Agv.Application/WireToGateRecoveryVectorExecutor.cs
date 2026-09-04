using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// Executes the physical part of the recovery vectors that must end in a
/// proven, locked and output-reset state.  A vector is bound to its persisted
/// identity and slot set; a replay never selects new slots and never repeats an
/// unlock after an active-unlock checkpoint was written.
/// </summary>
public sealed class WireToGateRecoveryVectorExecutor : IAsyncDisposable
{
    private readonly IIoModuleClient _ioModule;
    private readonly IWireToGateJournal _journal;
    private readonly IClock _clock;
    private readonly WireToGateSlotOperationExecutorOptions _options;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public WireToGateRecoveryVectorExecutor(
        IIoModuleClient ioModule,
        IWireToGateJournal journal,
        IClock clock,
        WireToGateSlotOperationExecutorOptions options)
    {
        _ioModule = ioModule ?? throw new ArgumentNullException(nameof(ioModule));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options;
        ValidateOptions(options);
    }

    public Task<WireToGateRecoveryVectorExecutionResult> ExecuteClearAsync(
        WireToGateRecoveryVectorContext context,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, CancellationToken, Task>? progress,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(context, correction: false, progress, cancellationToken);

    public Task<WireToGateRecoveryVectorExecutionResult> ExecuteCorrectionAsync(
        WireToGateRecoveryVectorContext context,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, CancellationToken, Task>? progress,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(context, correction: true, progress, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _operationGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<WireToGateRecoveryVectorExecutionResult> ExecuteAsync(
        WireToGateRecoveryVectorContext context,
        bool correction,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateContext(context, correction);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteExclusiveAsync(
                context,
                correction,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<WireToGateRecoveryVectorExecutionResult> ExecuteExclusiveAsync(
        WireToGateRecoveryVectorContext context,
        bool correction,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset started = _clock.Now;
        WireToGateRecoveryState state = await _journal
            .ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        WireToGateRecoveryVectorContext? persistedVector = state.RecoveryVector;
        bool resuming = persistedVector is not null;
        if (persistedVector is not null && !SameContext(persistedVector, context))
        {
            throw new InvalidDataException("RECOVERY_STATE_MISMATCH");
        }

        if (state.RecoveryResultObservedAt is { } recordedAt
            && persistedVector is not null)
        {
            string recordedOutcome = state.ProvenRecoveryCheckpoint switch
            {
                WireToGateRecoveryCheckpoint.SafeFinishReached
                    or WireToGateRecoveryCheckpoint.ResultRecorded => "COMPLETED",
                WireToGateRecoveryCheckpoint.Prepared => "FAILED",
                _ => "UNKNOWN"
            };
            return CreateResult(
                context,
                recordedOutcome,
                state.SlotResults.Where(result => context.Slots.Contains(result.SlotNo)).ToArray(),
                state.ProvenRecoveryCheckpoint,
                recordedAt);
        }

        List<int> completed = resuming
            ? state.CompletedSlots.Where(context.Slots.Contains).Distinct().Order().ToList()
            : [];
        List<WireToGateSlotExecutionResult> results = resuming
            ? state.SlotResults
                .Where(result => context.Slots.Contains(result.SlotNo))
                .GroupBy(result => result.SlotNo)
                .Select(group => group.Last())
                .OrderBy(result => result.SlotNo)
                .ToList()
            : [];

        if (resuming && state.ActiveUnlockSlots.Count > 0)
        {
            // An active set is a write-ahead fence.  The previous process may
            // have pulsed before it crashed, so an uncertain active slot is
            // never pulsed again.  Only a fresh snapshot proving the final
            // state lets us close the checkpoint without another IO action.
            IoSnapshot replaySnapshot = _ioModule.CurrentSnapshot;
            bool activeSetProvenSafe = state.ActiveUnlockSlots.All(slot =>
                TryGetLocker(replaySnapshot, slot, out LockerSnapshot? locker)
                && IsFinalState(locker!, correction));
            if (!activeSetProvenSafe)
            {
                AddUnknownResults(context.Slots, state.ActiveUnlockSlots, results);
                await WriteVectorStateAsync(
                    context,
                    WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                    state.ActiveUnlockSlots,
                    completed,
                    results,
                    state,
                    CancellationToken.None).ConfigureAwait(false);
                DateTimeOffset observedAt = await EnsureResultObservedAtAsync(
                    context,
                    CancellationToken.None).ConfigureAwait(false);
                return CreateResult(
                    context,
                    "UNKNOWN",
                    results,
                    WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                    observedAt);
            }

            foreach (int slot in state.ActiveUnlockSlots)
            {
                LockerSnapshot locker = GetLocker(replaySnapshot, slot);
                UpsertResult(results, CreateSlotResult(locker, "COMPLETED", []));
                completed.Add(slot);
            }

            await WriteVectorStateAsync(
                context,
                WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                [],
                completed,
                results,
                state,
                cancellationToken).ConfigureAwait(false);
            state = state with
            {
                ActiveUnlockSlots = [],
                CompletedSlots = completed.Distinct().Order().ToArray(),
                SlotResults = results.OrderBy(result => result.SlotNo).ToArray()
            };
        }

        IoSnapshot initial = _ioModule.CurrentSnapshot;
        string? precheckFailure = ValidateInitialSnapshot(initial, context.Slots, correction);
        if (precheckFailure is not null)
        {
            AddFailureResults(context.Slots, results, precheckFailure);
            await WriteVectorStateAsync(
                context,
                WireToGateRecoveryCheckpoint.Prepared,
                [],
                completed,
                results,
                state,
                CancellationToken.None).ConfigureAwait(false);
            DateTimeOffset observedAt = await EnsureResultObservedAtAsync(
                context,
                CancellationToken.None).ConfigureAwait(false);
            return CreateResult(
                context,
                "FAILED",
                results,
                WireToGateRecoveryCheckpoint.Prepared,
                observedAt);
        }

        if (!resuming)
        {
            foreach (int slot in context.Slots)
            {
                LockerSnapshot locker = GetLocker(initial, slot);
                if (!correction && !locker.HasCargo)
                {
                    completed.Add(slot);
                    UpsertResult(results, CreateSlotResult(locker, "COMPLETED", []));
                }
            }

            await WriteVectorStateAsync(
                context,
                WireToGateRecoveryCheckpoint.Prepared,
                [],
                completed,
                results,
                state,
                cancellationToken).ConfigureAwait(false);
            await SendProgressAsync(progress, "PREPARING", [], completed, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // A completed slot is reusable only if the current snapshot still
            // proves the same final state.  A changed slot is unresolved but
            // not safe to operate again under the old command identity.
            foreach (int slot in completed.ToArray())
            {
                LockerSnapshot locker = GetLocker(initial, slot);
                if (!IsFinalState(locker, correction))
                {
                    AddUnknownResults(context.Slots, [slot], results);
                    await WriteVectorStateAsync(
                        context,
                        WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                        [slot],
                        completed,
                        results,
                        state,
                        CancellationToken.None).ConfigureAwait(false);
                    DateTimeOffset observedAt = await EnsureResultObservedAtAsync(
                        context,
                        CancellationToken.None).ConfigureAwait(false);
                    return CreateResult(
                        context,
                        "UNKNOWN",
                        results,
                        WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                        observedAt);
                }
            }

            if (!correction)
            {
                foreach (int slot in context.Slots.Where(slot => !completed.Contains(slot)))
                {
                    LockerSnapshot locker = GetLocker(initial, slot);
                    if (!locker.HasCargo)
                    {
                        completed.Add(slot);
                        UpsertResult(results, CreateSlotResult(locker, "COMPLETED", []));
                    }
                }

                await WriteVectorStateAsync(
                    context,
                    WireToGateRecoveryCheckpoint.Prepared,
                    [],
                    completed,
                    results,
                    state,
                    cancellationToken).ConfigureAwait(false);
            }

            await SendProgressAsync(progress, "PREPARING", [], completed, cancellationToken)
                .ConfigureAwait(false);
        }

        DateTimeOffset deadline = started + _options.OperationTimeout;
        foreach (int physicalSlot in context.Slots)
        {
            if (completed.Contains(physicalSlot))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            int slotIndex = physicalSlot - 1;
            IoSnapshot beforePulse = _ioModule.CurrentSnapshot;
            string? slotPrecheckFailure = ValidateInitialSnapshot(
                beforePulse,
                [physicalSlot],
                correction);
            if (slotPrecheckFailure is not null)
            {
                AddFailureResults(context.Slots, results, slotPrecheckFailure);
                await WriteVectorStateAsync(
                    context,
                    WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                    [],
                    completed,
                    results,
                    state,
                    CancellationToken.None).ConfigureAwait(false);
                DateTimeOffset observedAt = await EnsureResultObservedAtAsync(
                    context,
                    CancellationToken.None).ConfigureAwait(false);
                return CreateResult(
                    context,
                    "FAILED",
                    results,
                    WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                    observedAt);
            }

            await WriteVectorStateAsync(
                context,
                WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                [physicalSlot],
                completed,
                results,
                state,
                cancellationToken).ConfigureAwait(false);
            await SendProgressAsync(
                progress,
                "UNLOCKING",
                [physicalSlot],
                completed,
                cancellationToken).ConfigureAwait(false);

            try
            {
                EnsureRemaining(deadline, cancellationToken);
                await _ioModule.PulseUnlockAsync(slotIndex, cancellationToken).ConfigureAwait(false);
                LockerSnapshot unlocked = await _ioModule.WaitForLockerAsync(
                    slotIndex,
                    locker => locker.IsKnown && !locker.IsLocked,
                    MinTimeout(_options.UnlockFeedbackTimeout, GetRemaining(deadline)),
                    _options.FeedbackStableWindow,
                    cancellationToken).ConfigureAwait(false);
                await _ioModule.WaitForLockerAsync(
                    slotIndex,
                    locker => locker.IsKnown
                        && locker.ObservedAt >= unlocked.ObservedAt
                        && locker.UnlockOutputRaw is false,
                    MinTimeout(_options.UnlockOutputResetTimeout, GetRemaining(deadline)),
                    _options.FeedbackStableWindow,
                    cancellationToken).ConfigureAwait(false);

                await SendProgressAsync(
                    progress,
                    "WAITING_OPERATOR",
                    [physicalSlot],
                    completed,
                    cancellationToken).ConfigureAwait(false);
                LockerSnapshot completedLocker = correction
                    ? await WaitForCorrectionAsync(slotIndex, deadline, cancellationToken)
                    : await _ioModule.WaitForLockerAsync(
                        slotIndex,
                        locker => locker.IsKnown
                            && locker.IsLocked
                            && !locker.HasCargo
                            && locker.UnlockOutputRaw is false,
                        MinTimeout(_options.OperationTimeout, GetRemaining(deadline)),
                        _options.FeedbackStableWindow,
                        cancellationToken).ConfigureAwait(false);

                WireToGateSlotExecutionResult result = CreateSlotResult(
                    completedLocker,
                    "COMPLETED",
                    []);
                UpsertResult(results, result);
                completed.Add(physicalSlot);
                await WriteVectorStateAsync(
                    context,
                    WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                    [],
                    completed,
                    results,
                    state,
                    cancellationToken).ConfigureAwait(false);
                await SendProgressAsync(progress, "VERIFYING", [], completed, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or InvalidDataException)
            {
                LockerSnapshot latest = GetLocker(_ioModule.CurrentSnapshot, slotIndex + 1);
                string reason = MapFailureReason(exception);
                UpsertResult(results, CreateSlotResult(latest, "UNKNOWN", [reason]));
                foreach (int notStarted in context.Slots.Where(slot => !completed.Contains(slot)))
                {
                    UpsertResult(
                        results,
                        new WireToGateSlotExecutionResult(
                            notStarted,
                            "NOT_STARTED",
                            "UNKNOWN",
                            "UNKNOWN",
                            "UNKNOWN",
                            [reason]));
                }

                await WriteVectorStateAsync(
                    context,
                    WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                    [physicalSlot],
                    completed,
                    results,
                    state,
                    CancellationToken.None).ConfigureAwait(false);
                await SendProgressAsync(
                        progress,
                        "PAUSED",
                        [physicalSlot],
                        completed,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                DateTimeOffset observedAt = await EnsureResultObservedAtAsync(
                    context,
                    CancellationToken.None).ConfigureAwait(false);
                return CreateResult(
                    context,
                    "UNKNOWN",
                    results,
                    WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                    observedAt);
            }
        }

        await WriteVectorStateAsync(
            context,
            WireToGateRecoveryCheckpoint.SafeFinishReached,
            [],
            completed,
            results,
            state,
            cancellationToken).ConfigureAwait(false);
        await SendProgressAsync(progress, "SAFE_FINISH", [], completed, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset finalObservedAt = await EnsureResultObservedAtAsync(
            context,
            CancellationToken.None).ConfigureAwait(false);
        return CreateResult(
            context,
            "COMPLETED",
            results,
            WireToGateRecoveryCheckpoint.SafeFinishReached,
            finalObservedAt);
    }

    private async Task<LockerSnapshot> WaitForCorrectionAsync(
        int slotIndex,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        LockerSnapshot empty = await _ioModule.WaitForLockerAsync(
            slotIndex,
            locker => locker.IsKnown && !locker.IsLocked && !locker.HasCargo,
            MinTimeout(_options.OperationTimeout, GetRemaining(deadline)),
            _options.FeedbackStableWindow,
            cancellationToken).ConfigureAwait(false);
        return await _ioModule.WaitForLockerAsync(
            slotIndex,
            locker => locker.IsKnown
                && locker.ObservedAt >= empty.ObservedAt
                && locker.IsLocked
                && locker.HasCargo
                && locker.UnlockOutputRaw is false,
            MinTimeout(_options.OperationTimeout, GetRemaining(deadline)),
            _options.FeedbackStableWindow,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DateTimeOffset> EnsureResultObservedAtAsync(
        WireToGateRecoveryVectorContext context,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await _journal
            .ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state.RecoveryVector is not { } persisted
            || !SameContext(persisted, context))
        {
            throw new InvalidDataException("RECOVERY_STATE_MISMATCH");
        }

        if (state.RecoveryResultObservedAt is { } observedAt)
        {
            return observedAt;
        }

        DateTimeOffset resultObservedAt = _clock.Now.ToUniversalTime();
        await _journal.WriteRecoveryStateAsync(
                state with { RecoveryResultObservedAt = resultObservedAt },
                cancellationToken)
            .ConfigureAwait(false);
        return resultObservedAt;
    }

    private async Task WriteVectorStateAsync(
        WireToGateRecoveryVectorContext context,
        WireToGateRecoveryCheckpoint checkpoint,
        IReadOnlyList<int> activeSlots,
        IReadOnlyList<int> completedSlots,
        IReadOnlyList<WireToGateSlotExecutionResult> results,
        WireToGateRecoveryState existingState,
        CancellationToken cancellationToken)
    {
        await _journal.WriteRecoveryStateAsync(
            existingState with
            {
                UnsettledSlotOperationAttemptId = context.SlotOperationAttemptId,
                ProvenRecoveryCheckpoint = checkpoint,
                ActiveUnlockSlots = activeSlots.Order().ToArray(),
                CompletedSlots = completedSlots.Distinct().Order().ToArray(),
                SlotResults = results
                    .GroupBy(result => result.SlotNo)
                    .Select(group => group.Last())
                    .OrderBy(result => result.SlotNo)
                    .ToArray(),
                RecoveryVector = context,
                ExceptionRecoverySessionId = context.ExceptionRecoverySessionId
                    ?? existingState.ExceptionRecoverySessionId,
                RecoveryActionId = context.VectorType is
                    WireToGateRecoveryVectorTypes.LoadCompensation
                    or WireToGateRecoveryVectorTypes.FaultCargoHandoff
                    ? context.PrimaryId
                    : existingState.RecoveryActionId,
                RecoveryOperatorId = context.OperatorId ?? existingState.RecoveryOperatorId,
                RecoveryOperatorVerifiedAt = context.OperatorVerifiedAt
                    ?? existingState.RecoveryOperatorVerifiedAt
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static WireToGateRecoveryVectorExecutionResult CreateResult(
        WireToGateRecoveryVectorContext context,
        string outcome,
        IReadOnlyList<WireToGateSlotExecutionResult> results,
        WireToGateRecoveryCheckpoint checkpoint,
        DateTimeOffset observedAt) =>
        new(
            context.VectorType,
            context.PrimaryId,
            context.ExceptionRecoverySessionId,
            context.DemandId,
            context.SlotOperationAttemptId,
            context.HandoffId,
            outcome,
            results.OrderBy(result => result.SlotNo).ToArray(),
            observedAt,
            checkpoint switch
            {
                WireToGateRecoveryCheckpoint.None => "NONE",
                WireToGateRecoveryCheckpoint.Prepared => "PREPARED",
                WireToGateRecoveryCheckpoint.ActiveUnlockSet => "ACTIVE_UNLOCK_SET",
                WireToGateRecoveryCheckpoint.SafeFinishReached => "SAFE_FINISH_REACHED",
                WireToGateRecoveryCheckpoint.ResultRecorded => "RESULT_RECORDED",
                _ => throw new ArgumentOutOfRangeException(nameof(checkpoint))
            });

    private string? ValidateInitialSnapshot(
        IoSnapshot snapshot,
        IReadOnlyList<int> slots,
        bool correction)
    {
        return !IsFresh(snapshot)
            ? "SLOT_STATE_UNKNOWN"
            : ValidateKnownLockerStates(snapshot, slots, correction);
    }

    private bool IsFresh(IoSnapshot snapshot) =>
        snapshot.IsConnected
        && SafetyRules.IsSnapshotFresh(snapshot, _clock.Now, _options.IoSnapshotMaxAge);

    private static string? ValidateKnownLockerStates(
        IoSnapshot snapshot,
        IReadOnlyList<int> slots,
        bool correction)
    {
        foreach (int slot in slots)
        {
            LockerSnapshot locker = GetLocker(snapshot, slot);
            if (!locker.IsKnown)
            {
                return "SLOT_STATE_UNKNOWN";
            }

            if (locker.UnlockOutputRaw is not false)
            {
                return "UNLOCK_OUTPUT_NOT_RESET";
            }

            if (!locker.IsLocked)
            {
                return "LOCK_NOT_CLOSED";
            }

            if (correction && !locker.HasCargo)
            {
                return "SLOT_OPERATION_CONFLICT";
            }
        }

        return null;
    }

    private static bool IsFinalState(LockerSnapshot locker, bool correction) =>
        locker.IsKnown
        && locker.IsLocked
        && locker.UnlockOutputRaw is false
        && locker.HasCargo == correction;

    private static LockerSnapshot GetLocker(IoSnapshot snapshot, int physicalSlot) =>
        TryGetLocker(snapshot, physicalSlot, out LockerSnapshot? locker)
            ? locker!
            : LockerSnapshot.Unknown(physicalSlot - 1, snapshot.ObservedAt);

    private static bool TryGetLocker(
        IoSnapshot snapshot,
        int physicalSlot,
        out LockerSnapshot? locker)
    {
        locker = snapshot.Lockers.FirstOrDefault(item => item.PhysicalNumber == physicalSlot);
        return locker is not null;
    }

    private static WireToGateSlotExecutionResult CreateSlotResult(
        LockerSnapshot locker,
        string outcome,
        IReadOnlyList<string> reasons) =>
        new(
            locker.PhysicalNumber,
            outcome,
            locker.IsKnown ? locker.HasCargo ? "OCCUPIED" : "EMPTY" : "UNKNOWN",
            locker.IsKnown ? locker.IsLocked ? "LOCKED" : "UNLOCKED" : "UNKNOWN",
            locker.IsKnown ? locker.UnlockOutputRaw is true ? "ACTIVE" : "RESET" : "UNKNOWN",
            reasons);

    private static void AddFailureResults(
        IReadOnlyList<int> slots,
        List<WireToGateSlotExecutionResult> results,
        string reason)
    {
        foreach (int slot in slots)
        {
            UpsertResult(
                results,
                new WireToGateSlotExecutionResult(
                    slot,
                    "NOT_STARTED",
                    "UNKNOWN",
                    "UNKNOWN",
                    "UNKNOWN",
                    [reason]));
        }
    }

    private static void AddUnknownResults(
        IReadOnlyList<int> slots,
        IReadOnlyList<int> affectedSlots,
        List<WireToGateSlotExecutionResult> results)
    {
        foreach (int slot in slots)
        {
            if (affectedSlots.Contains(slot))
            {
                UpsertResult(
                    results,
                    new WireToGateSlotExecutionResult(
                        slot,
                        "UNKNOWN",
                        "UNKNOWN",
                        "UNKNOWN",
                        "UNKNOWN",
                        ["SLOT_STATE_UNKNOWN"]));
            }
            else if (results.All(result => result.SlotNo != slot))
            {
                UpsertResult(
                    results,
                    new WireToGateSlotExecutionResult(
                        slot,
                        "NOT_STARTED",
                        "UNKNOWN",
                        "UNKNOWN",
                        "UNKNOWN",
                        ["SLOT_STATE_UNKNOWN"]));
            }
        }
    }

    private static void UpsertResult(
        List<WireToGateSlotExecutionResult> results,
        WireToGateSlotExecutionResult result)
    {
        int index = results.FindIndex(item => item.SlotNo == result.SlotNo);
        if (index >= 0)
        {
            results[index] = result;
        }
        else
        {
            results.Add(result);
        }
    }

    private static string MapFailureReason(Exception exception) => exception switch
    {
        TimeoutException => "ACTION_NOT_ALLOWED_IN_STATE",
        IOException => "SLOT_STATE_UNKNOWN",
        InvalidDataException invalid when invalid.Message.Contains("LOCK", StringComparison.OrdinalIgnoreCase)
            => "LOCK_NOT_CLOSED",
        _ => "SLOT_STATE_UNKNOWN"
    };

    private static async Task SendProgressAsync(
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, CancellationToken, Task>? progress,
        string phase,
        IReadOnlyList<int> active,
        IReadOnlyList<int> completed,
        CancellationToken cancellationToken)
    {
        if (progress is not null)
        {
            await progress(phase, active, completed, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureRemaining(DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        if (GetRemaining(deadline) <= TimeSpan.Zero)
        {
            throw new TimeoutException("WIRE_TO_GATE恢复操作超时。");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private TimeSpan GetRemaining(DateTimeOffset deadline) => deadline - _clock.Now;

    private static TimeSpan MinTimeout(TimeSpan configured, TimeSpan remaining) =>
        remaining <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : configured < remaining ? configured : remaining;

    private static bool SameContext(
        WireToGateRecoveryVectorContext left,
        WireToGateRecoveryVectorContext right) =>
        left.VectorType == right.VectorType
        && left.PrimaryId == right.PrimaryId
        && left.ExceptionRecoverySessionId == right.ExceptionRecoverySessionId
        && left.DemandId == right.DemandId
        && left.SlotOperationAttemptId == right.SlotOperationAttemptId
        && left.HandoffId == right.HandoffId
        && left.Slots.SequenceEqual(right.Slots)
        && (left.CommandContentSha256 is null
            || right.CommandContentSha256 is null
            || string.Equals(
                left.CommandContentSha256,
                right.CommandContentSha256,
                StringComparison.OrdinalIgnoreCase));

    private static void ValidateContext(
        WireToGateRecoveryVectorContext context,
        bool correction)
    {
        if (!WireToGateRecoveryVectorTypes.IsKnown(context.VectorType)
            || correction && context.VectorType != WireToGateRecoveryVectorTypes.LoadCorrection
            || !correction && context.VectorType == WireToGateRecoveryVectorTypes.LoadCorrection
            || !Guid.TryParseExact(context.PrimaryId, "D", out _)
            || !Guid.TryParseExact(context.DemandId, "D", out _)
            || context.ExceptionRecoverySessionId is not null
                && !Guid.TryParseExact(context.ExceptionRecoverySessionId, "D", out _)
            || context.SlotOperationAttemptId is not null
                && !Guid.TryParseExact(context.SlotOperationAttemptId, "D", out _)
            || context.HandoffId is not null
                && !Guid.TryParseExact(context.HandoffId, "D", out _)
            || context.Slots is null
            || context.Slots.Count is < 1 or > 8
            || context.Slots.Any(slot => slot is < 1 or > 8)
            || context.Slots.Distinct().Count() != context.Slots.Count
            || !context.Slots.SequenceEqual(context.Slots.Order()))
        {
            throw new InvalidDataException("RECOVERY_COMMAND_INVALID");
        }
    }

    private static void ValidateOptions(WireToGateSlotOperationExecutorOptions options)
    {
        if (options.UnlockFeedbackTimeout <= TimeSpan.Zero
            || options.UnlockOutputResetTimeout <= TimeSpan.Zero
            || options.OperationTimeout <= TimeSpan.Zero
            || options.FeedbackStableWindow < TimeSpan.Zero
            || options.IoSnapshotMaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}
