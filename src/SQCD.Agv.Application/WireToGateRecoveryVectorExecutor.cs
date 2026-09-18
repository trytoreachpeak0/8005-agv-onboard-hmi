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

        if (context.Slots.Count == 0)
        {
            // The cancellation before any sublot is entered: nothing was commanded, so there is no
            // slot to read, open or prove, and the IO module is not consulted at all -- not even for
            // freshness, because a stale snapshot has nothing here to be wrong about. Journaled the
            // same way as a clear that reached its safe finish, so a retry reports the same
            // observedAt.
            await WriteVectorStateAsync(
                context,
                WireToGateRecoveryCheckpoint.SafeFinishReached,
                [],
                [],
                [],
                state,
                cancellationToken).ConfigureAwait(false);
            DateTimeOffset emptyObservedAt = await EnsureResultObservedAtAsync(
                context,
                CancellationToken.None).ConfigureAwait(false);
            return CreateResult(
                context,
                "COMPLETED",
                [],
                WireToGateRecoveryCheckpoint.SafeFinishReached,
                emptyObservedAt);
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

        int[] handedOver = HandedOverOpenSlots(state, context);
        if (resuming && state.ActiveUnlockSlots.Count > 0 && handedOver.Length == 0)
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
                AddUnknownResults(replaySnapshot, context.Slots, state.ActiveUnlockSlots, results);
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
        // A door handed over open is expected to be open; it only has to be readable.
        string? precheckFailure = ValidateInitialSnapshot(
                initial,
                context.Slots.Where(slot => !handedOver.Contains(slot)).ToArray(),
                correction)
            ?? (handedOver.Any(slot => !GetLocker(initial, slot).IsKnown) ? "SLOT_STATE_UNKNOWN" : null);
        if (precheckFailure is not null)
        {
            AddRejectedResults(initial, context.Slots, completed, results, correction);
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
                if (!correction && IsAlreadyEmpty(locker, slot, handedOver))
                {
                    completed.Add(slot);
                    UpsertResult(results, CreateSlotResult(locker, "COMPLETED", []));
                }
            }

            await WriteVectorStateAsync(
                context,
                WireToGateRecoveryCheckpoint.Prepared,
                handedOver.Where(slot => !completed.Contains(slot)).ToArray(),
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
                    AddUnknownResults(initial, context.Slots, [slot], results);
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
                    if (IsAlreadyEmpty(locker, slot, handedOver))
                    {
                        completed.Add(slot);
                        UpsertResult(results, CreateSlotResult(locker, "COMPLETED", []));
                    }
                }

                await WriteVectorStateAsync(
                    context,
                    WireToGateRecoveryCheckpoint.Prepared,
                    handedOver.Where(slot => !completed.Contains(slot)).ToArray(),
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
            // A door the aborted load left open is not pulsed: the operator is already at it. Shut empty
            // since the vector started, it is cleared as it stands; shut over a basket, it is an ordinary
            // target again and is unlocked to be emptied.
            if (handedOver.Contains(physicalSlot)
                && IsFresh(beforePulse)
                && IsFinalState(GetLocker(beforePulse, physicalSlot), correction))
            {
                UpsertResult(
                    results,
                    CreateSlotResult(GetLocker(beforePulse, physicalSlot), "COMPLETED", []));
                completed.Add(physicalSlot);
                await WriteVectorStateAsync(
                    context,
                    WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                    [],
                    completed,
                    results,
                    state,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            bool openHandedOver = handedOver.Contains(physicalSlot)
                && !IsShut(GetLocker(beforePulse, physicalSlot));
            string? slotPrecheckFailure = openHandedOver
                ? null
                : ValidateInitialSnapshot(beforePulse, [physicalSlot], correction);
            if (slotPrecheckFailure is not null)
            {
                AddRejectedResults(beforePulse, context.Slots, completed, results, correction);
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
            if (!openHandedOver)
            {
                await SendProgressAsync(
                    progress,
                    "UNLOCKING",
                    [physicalSlot],
                    completed,
                    cancellationToken).ConfigureAwait(false);
            }

            try
            {
                EnsureRemaining(deadline, cancellationToken);
                if (!openHandedOver)
                {
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
                }

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
                // One snapshot for the failed slot and the slots never started. The failed slot's
                // own UNKNOWN is not overwritten, and a slot never opened reports what the IO reads
                // with no reason code (ADR-cross-0058 decision 6).
                IoSnapshot failureSnapshot = _ioModule.CurrentSnapshot;
                string reason = MapFailureReason(exception);
                UpsertResult(
                    results,
                    CreateSlotResult(ReadPhysicalSlot(failureSnapshot, physicalSlot), "UNKNOWN", [reason]));
                foreach (int notStarted in context.Slots
                    .Where(slot => slot != physicalSlot && !completed.Contains(slot)))
                {
                    UpsertResult(
                        results,
                        CreateSlotResult(ReadPhysicalSlot(failureSnapshot, notStarted), "NOT_STARTED", []));
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

    /// <summary>
    /// What a result may state about a slot: the reading when the snapshot is fresh, otherwise UNKNOWN.
    /// </summary>
    private LockerSnapshot ReadPhysicalSlot(IoSnapshot snapshot, int physicalSlot) =>
        IsFresh(snapshot)
            ? GetLocker(snapshot, physicalSlot)
            : LockerSnapshot.Unknown(physicalSlot - 1, snapshot.ObservedAt);

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

    /// <summary>
    /// The doors a load cancelled in flight left open, which this vector takes over without pulsing them
    /// (ADR-cross-0046, onboard-hmi#78).
    /// </summary>
    /// <remarks>
    /// The business service writes them as the prepared vector's active unlock set once it has aborted
    /// the load: an active set at <see cref="WireToGateRecoveryCheckpoint.Prepared"/> has no other
    /// source, because this executor only fences a slot at
    /// <see cref="WireToGateRecoveryCheckpoint.ActiveUnlockSet"/>. Nothing but a load cancellation hands
    /// doors over, so for any other vector the same shape stays a fence.
    /// </remarks>
    private static int[] HandedOverOpenSlots(
        WireToGateRecoveryState state,
        WireToGateRecoveryVectorContext context) =>
        context.VectorType == WireToGateRecoveryVectorTypes.LoadCancellation
            && state.RecoveryVector is not null
            && state.ProvenRecoveryCheckpoint == WireToGateRecoveryCheckpoint.Prepared
                ? state.ActiveUnlockSlots.Where(context.Slots.Contains).ToArray()
                : [];

    /// <summary>
    /// A slot a clear has nothing to do for: empty -- and, if it was handed over open, also shut, locked
    /// and output reset, since an open empty door is not yet cleared.
    /// </summary>
    private static bool IsAlreadyEmpty(LockerSnapshot locker, int slot, int[] handedOver) =>
        !locker.HasCargo && (!handedOver.Contains(slot) || IsFinalState(locker, correction: false));

    private static bool IsShut(LockerSnapshot locker) =>
        locker.IsKnown && locker.IsLocked && locker.UnlockOutputRaw is false;

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

    /// <summary>
    /// A precheck refused the rest of the vector. Slots already completed keep their result; every
    /// other slot is NOT_STARTED with the fields the IO reads, and only a slot that fails the precheck
    /// on its own carries its reason -- all of them when the snapshot itself cannot be trusted.
    /// </summary>
    private void AddRejectedResults(
        IoSnapshot snapshot,
        IReadOnlyList<int> slots,
        IReadOnlyList<int> completed,
        List<WireToGateSlotExecutionResult> results,
        bool correction)
    {
        foreach (int slot in slots.Where(slot => !completed.Contains(slot)))
        {
            UpsertResult(
                results,
                CreateSlotResult(
                    ReadPhysicalSlot(snapshot, slot),
                    "NOT_STARTED",
                    ValidateInitialSnapshot(snapshot, [slot], correction) is { } reason ? [reason] : []));
        }
    }

    private void AddUnknownResults(
        IoSnapshot snapshot,
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
                    CreateSlotResult(ReadPhysicalSlot(snapshot, slot), "NOT_STARTED", []));
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
            || context.Slots.Count > 8
            || context.Slots.Count == 0
                && (correction
                    || !WireToGateRecoveryVectorTypes.IsLoadCancellationBeforeSublot(context))
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
            || options.FeedbackStableWindow <= TimeSpan.Zero
            || options.IoSnapshotMaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}
