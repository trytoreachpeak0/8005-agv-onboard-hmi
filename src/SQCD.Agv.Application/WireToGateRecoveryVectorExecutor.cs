using System.Diagnostics;
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
    /// <summary>
    /// The outcome of a load cancellation or compensation clear that the light curtain settled as empty
    /// while some door's lock or unlock output could not be proven (CP-0009, REQ-0357, REQ-0364). Already the
    /// protocol's own <c>overallOutcome</c> value, so the business service sends it as it stands.
    /// </summary>
    public const string AllEmptyDoorUnprovenOutcome = "ALL_EMPTY_DOOR_UNPROVEN";

    /// <summary>
    /// The reason on each slot of such a clear whose door is not proven. Its registry entry allows it on
    /// <c>LoadCancellationResult</c>, <c>LoadCompensationResult</c> and <c>VehicleBusinessStateSnapshot</c>.
    /// </summary>
    public const string DoorLockUnprovenAfterEmptyReason = "SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY";

    private readonly IIoModuleClient _ioModule;
    private readonly IWireToGateJournal _journal;
    private readonly IClock _clock;
    private readonly WireToGateSlotOperationExecutorOptions _options;
    private readonly Func<bool> _fatalFaultLatched;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    /// <param name="fatalFaultLatched">
    /// Asked immediately before every pulse: while a severe safety fault is latched no vector opens a
    /// door (8005-agv-onboard-hmi#191). A door already standing open -- one an aborted load handed over --
    /// is still waited on, since waiting opens nothing. Omitted, nothing is ever latched.
    /// </param>
    public WireToGateRecoveryVectorExecutor(
        IIoModuleClient ioModule,
        IWireToGateJournal journal,
        IClock clock,
        WireToGateSlotOperationExecutorOptions options,
        Func<bool>? fatalFaultLatched = null)
    {
        _ioModule = ioModule ?? throw new ArgumentNullException(nameof(ioModule));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options;
        _fatalFaultLatched = fatalFaultLatched ?? (() => false);
        ValidateOptions(options);
    }

    /// <summary>
    /// True while this executor holds its own serialisation gate — a recovery vector is running here
    /// (8005-agv-onboard-hmi#171). Derived from the gate, never from a field kept beside it; see
    /// <see cref="WireToGateSlotOperationExecutor.HasOperationInFlight"/> for why that matters.
    /// </summary>
    /// <remarks>
    /// This executor opens doors too — compensation clearing, load correction and forced mechanical
    /// recovery all pulse the lock at <c>ExecuteExclusiveAsync</c>. It is the fourth of the four
    /// unlock sites in <c>FatalFaultScopeArchitectureTests</c>, and the one the fatal-fault latch
    /// cannot reach, so a clearance must ask it as well as the slot executor.
    /// </remarks>
    public bool HasOperationInFlight => _operationGate.CurrentCount == 0;

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

    /// <summary>
    /// Records and returns the <c>FAILED</c> result of a vector the vehicle refused before any
    /// unlock, or <c>null</c> when the journal cannot show that nothing was done (onboard-hmi#123).
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Nothing was done" is read from the journal alone, never from the caller's word: the vector is
    /// at <see cref="WireToGateRecoveryCheckpoint.Prepared"/> with no active unlock set and no slot
    /// counted complete -- the state the business service writes when it prepares a vector, and the
    /// state this executor's own precheck refusal leaves. Every pulse is preceded by a write of the
    /// active unlock set, and every slot the executor finishes or finds already empty is written as
    /// completed, so a journal in that state proves no slot of this vector was touched. Anything
    /// else may have acted, and the answer is <c>null</c>: the caller must not claim a refusal, and
    /// keeps whatever settlement it already had.
    /// </para>
    /// <para>
    /// The result is the one the precheck refusal in <see cref="ExecuteExclusiveAsync"/> gives --
    /// <c>FAILED</c>, every slot <c>NOT_STARTED</c> with what the IO reads -- here carrying
    /// <paramref name="reasonCode"/> on every slot, because the refusal is about the vehicle and not
    /// any one slot. It is written before it is returned, and asked again it is returned as written:
    /// a retry repeats the first answer byte for byte instead of reading the slots a second time.
    /// </para>
    /// </remarks>
    public async Task<WireToGateRecoveryVectorExecutionResult?> RefuseBeforeUnlockAsync(
        WireToGateRecoveryVectorContext context,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WireToGateRecoveryState state = await _journal
                .ReadRecoveryStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (state.RecoveryVector is not { } persisted || !SameContext(persisted, context))
            {
                throw new InvalidDataException("RECOVERY_STATE_MISMATCH");
            }

            if (state.ProvenRecoveryCheckpoint != WireToGateRecoveryCheckpoint.Prepared
                || state.ActiveUnlockSlots.Count > 0
                || state.CompletedSlots.Count > 0
                || context.Slots.Count == 0)
            {
                return null;
            }

            if (state.RecoveryResultObservedAt is { } recordedAt)
            {
                WireToGateSlotExecutionResult[] recorded = state.SlotResults
                    .Where(result => context.Slots.Contains(result.SlotNo))
                    .ToArray();
                return recorded.Length == context.Slots.Count
                    && recorded.All(result => result.Outcome == "NOT_STARTED")
                        ? CreateResult(
                            context,
                            "FAILED",
                            recorded,
                            WireToGateRecoveryCheckpoint.Prepared,
                            recordedAt)
                        : null;
            }

            IoSnapshot snapshot = _ioModule.CurrentSnapshot;
            WireToGateSlotExecutionResult[] results = context.Slots
                .Select(slot => CreateSlotResult(ReadPhysicalSlot(snapshot, slot), "NOT_STARTED", [reasonCode]))
                .ToArray();
            await WriteVectorStateAsync(
                context,
                WireToGateRecoveryCheckpoint.Prepared,
                [],
                [],
                results,
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
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Records and returns the result of a vector the operator chose not to carry on with, touching no IO
    /// (onboard-hmi#239): what the journal shows was done, and <c>UNKNOWN</c> for what it cannot show.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the vector <see cref="RefuseBeforeUnlockAsync"/> answers <c>null</c> for: one the journal shows may
    /// have acted -- a process that died mid-execution, its command replayed after the restart and held for
    /// someone at the vehicle. Carrying it on would pulse the slots it had not reached yet; declining it has to
    /// answer the server all the same, or its workflow waits for good, since only a result closes the session.
    /// </para>
    /// <para>
    /// Slots counted complete keep their recorded results. Slots in the active unlock set may have been pulsed
    /// and are <c>UNKNOWN</c>, as the replay path reports them when it cannot prove them safe. The rest were never
    /// reached and are <c>NOT_STARTED</c> with what the IO reads. The overall outcome is <c>COMPLETED</c> only when
    /// the journal already shows the safe finish, and <c>UNKNOWN</c> otherwise: the vehicle does not claim a
    /// failure over doors that may have opened. A result already recorded is returned as recorded.
    /// </para>
    /// </remarks>
    public async Task<WireToGateRecoveryVectorExecutionResult> SettleWithoutUnlockAsync(
        WireToGateRecoveryVectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WireToGateRecoveryState state = await _journal
                .ReadRecoveryStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (state.RecoveryVector is not { } persisted || !SameContext(persisted, context))
            {
                throw new InvalidDataException("RECOVERY_STATE_MISMATCH");
            }

            string outcome = state.ProvenRecoveryCheckpoint switch
            {
                WireToGateRecoveryCheckpoint.SafeFinishReached
                    or WireToGateRecoveryCheckpoint.ResultRecorded => "COMPLETED",
                WireToGateRecoveryCheckpoint.Prepared
                    when state.RecoveryResultObservedAt is not null => "FAILED",
                _ when state.RecoveryResultObservedAt is not null && IsDoorUnprovenSettlement(state, context)
                    => AllEmptyDoorUnprovenOutcome,
                _ => "UNKNOWN"
            };
            if (state.RecoveryResultObservedAt is { } recordedAt)
            {
                return CreateResult(
                    context,
                    outcome,
                    state.SlotResults.Where(result => context.Slots.Contains(result.SlotNo)).ToArray(),
                    state.ProvenRecoveryCheckpoint,
                    recordedAt);
            }

            List<WireToGateSlotExecutionResult> results = state.SlotResults
                .Where(result => context.Slots.Contains(result.SlotNo))
                .GroupBy(result => result.SlotNo)
                .Select(group => group.Last())
                .OrderBy(result => result.SlotNo)
                .ToList();
            AddUnknownResults(_ioModule.CurrentSnapshot, context.Slots, state.ActiveUnlockSlots, results);
            await WriteVectorStateAsync(
                context,
                state.ProvenRecoveryCheckpoint,
                state.ActiveUnlockSlots,
                state.CompletedSlots.Where(context.Slots.Contains).ToArray(),
                results,
                CancellationToken.None).ConfigureAwait(false);
            DateTimeOffset observedAt = await EnsureResultObservedAtAsync(
                context,
                CancellationToken.None).ConfigureAwait(false);
            return CreateResult(context, outcome, results, state.ProvenRecoveryCheckpoint, observedAt);
        }
        finally
        {
            _operationGate.Release();
        }
    }

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
        WireToGateRecoveryState state = await _journal
            .ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        // A slot a forced mechanical recovery left physically unknown is not opened for any vector
        // (REQ-0241, onboard-hmi#107): nothing proves the state it was left in. Refused before
        // anything is journaled, so the attempt leaves no trace to settle.
        if (state.ForcedIsolation is { } isolation
            && context.Slots.Any(isolation.PhysicallyUnknownSlots.Contains))
        {
            throw new InvalidDataException("SLOT_INOPERABLE");
        }

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
                _ when IsDoorUnprovenSettlement(state, context) => AllEmptyDoorUnprovenOutcome,
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
                cancellationToken).ConfigureAwait(false);
        }

        IoSnapshot initial = _ioModule.CurrentSnapshot;
        // A slot already empty whose door cannot be proven is not reopened: opening it again proves the lock
        // no better and opens one more door (CP-0009). When it is the only kind of slot left besides slots
        // proven empty and shut, the clear settles here without a pulse; with a loaded slot still to open,
        // the precheck below refuses the clear as it always has.
        if (!correction
            && SettlesDoorUnproven(context)
            && IsFresh(initial)
            && context.Slots.All(slot => IsFinalState(GetLocker(initial, slot), correction: false)
                || !handedOver.Contains(slot) && IsEmptyWithDoorUnproven(initial, slot))
            && context.Slots.Any(slot => IsEmptyWithDoorUnproven(initial, slot)))
        {
            foreach (int slot in context.Slots)
            {
                LockerSnapshot locker = GetLocker(initial, slot);
                if (IsEmptyWithDoorUnproven(initial, slot))
                {
                    UpsertResult(results, CreateDoorUnprovenResult(locker));
                }
                else if (!completed.Contains(slot))
                {
                    completed.Add(slot);
                    UpsertResult(results, CreateSlotResult(locker, "COMPLETED", []));
                }
            }

            DateTimeOffset settledAt = await RecordDoorUnprovenSettlementAsync(context, [], completed, results)
                .ConfigureAwait(false);
            await SendProgressAsync(progress, "PAUSED", [], completed, CancellationToken.None).ConfigureAwait(false);
            return CreateResult(
                context,
                AllEmptyDoorUnprovenOutcome,
                results,
                WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                settledAt);
        }

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
                    cancellationToken).ConfigureAwait(false);
            }

            await SendProgressAsync(progress, "PREPARING", [], completed, cancellationToken)
                .ConfigureAwait(false);
        }

        // One door at a time (REQ-0357, ADR-cross-0061). A door the aborted load left open is the one
        // open door on the vehicle, so it is brought to its end -- or to UNKNOWN, which stops here --
        // before any other slot is unlocked, and it stays the active unlock set until then. In
        // ascending order a loaded slot numbered below it would be opened beside it, and the journal
        // would lose the open door to that slot's checkpoint.
        foreach (int physicalSlot in handedOver.Concat(context.Slots.Except(handedOver)))
        {
            if (completed.Contains(physicalSlot))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            int slotIndex = physicalSlot - 1;
            // Each slot's own OperationTimeout from its turn: the slots are emptied one after another,
            // so a deadline shared from the vector's start would leave the later ones only what the
            // earlier ones did not use.
            DateTimeOffset deadline = _clock.Now + _options.OperationTimeout;
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
                // Refused before this slot's unlock was written, so no door is in doubt: recorded at
                // Prepared, which a replay reads back as FAILED (8005-agv-onboard-hmi#249).
                AddRejectedResults(beforePulse, context.Slots, completed, results, correction);
                DateTimeOffset observedAt = await RecordFailedResultAsync(context, completed, results)
                    .ConfigureAwait(false);
                return CreateResult(
                    context,
                    "FAILED",
                    results,
                    WireToGateRecoveryCheckpoint.Prepared,
                    observedAt);
            }

            await WriteVectorStateAsync(
                context,
                WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                [physicalSlot],
                completed,
                results,
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

            bool pulseSent = false;
            // Set once the door is the operator's: only the wait for it to be emptied and relocked can end in
            // the door-unproven settlement. A door that never unlocked or whose output never fell back after
            // the pulse is still UNKNOWN.
            bool awaitingOperator = false;
            try
            {
                EnsureRemaining(deadline, cancellationToken);
                if (!openHandedOver)
                {
                    // The whole vehicle, not only this vector's slots: another door not proven shut
                    // stops the vector before this one opens beside it (REQ-0357).
                    IoSnapshot atPulse = _ioModule.CurrentSnapshot;
                    if (WireToGateSingleDoorRule.OtherDoorNotShut(atPulse, IsFresh(atPulse), physicalSlot)
                        is { } otherDoor)
                    {
                        throw new InvalidDataException(otherDoor);
                    }

                    // Nothing is awaited between this check and the pulse (8005-agv-onboard-hmi#191).
                    ThrowIfFatalFaultLatched();
                    pulseSent = true;
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
                awaitingOperator = true;
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
                    cancellationToken).ConfigureAwait(false);
                await SendProgressAsync(progress, "VERIFYING", [], completed, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FatalFaultLatchedException)
            {
                // The latch refused this slot's pulse (8005-agv-onboard-hmi#191): it was not opened by
                // this vector and nothing about it is in doubt, so it is NOT_STARTED with the reason, as
                // the refusal before any unlock reports it (onboard-hmi#123), and nothing stays active.
                // The vector stops here and reports FAILED -- never ALL_EMPTY, HANDED_OFF or COMPLETED --
                // which the server takes to RecoveryRequired; stopping rather than waiting for the latch
                // to lift is what lets ClearFatalFaultAsync, which refuses while a vector runs, lift it.
                IoSnapshot refusalSnapshot = _ioModule.CurrentSnapshot;
                UpsertResult(
                    results,
                    CreateSlotResult(
                        ReadPhysicalSlot(refusalSnapshot, physicalSlot),
                        "NOT_STARTED",
                        [FatalFaultLatchedReason]));
                foreach (int notStarted in context.Slots
                    .Where(slot => slot != physicalSlot && !completed.Contains(slot)))
                {
                    UpsertResult(
                        results,
                        CreateSlotResult(ReadPhysicalSlot(refusalSnapshot, notStarted), "NOT_STARTED", []));
                }

                // Recorded at Prepared, as the precheck refusals are, and before the progress goes out
                // (8005-agv-onboard-hmi#249).
                DateTimeOffset refusedAt = await RecordFailedResultAsync(context, completed, results)
                    .ConfigureAwait(false);
                await SendProgressAsync(progress, "PAUSED", [], completed, CancellationToken.None)
                    .ConfigureAwait(false);
                return CreateResult(
                    context,
                    "FAILED",
                    results,
                    WireToGateRecoveryCheckpoint.Prepared,
                    refusedAt);
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or InvalidDataException)
            {
                // One snapshot for the failed slot and the slots never started. The failed slot's
                // own UNKNOWN is not overwritten, and a slot never opened reports what the IO reads
                // with no reason code (ADR-cross-0058 decision 6).
                IoSnapshot failureSnapshot = _ioModule.CurrentSnapshot;
                if (exception is TimeoutException
                    && awaitingOperator
                    && !correction
                    && SettlesDoorUnproven(context)
                    && context.Slots.All(slot => slot == physicalSlot || completed.Contains(slot))
                    && IsEmptyWithDoorUnproven(failureSnapshot, physicalSlot))
                {
                    // The operator emptied the last slot still to clear and its lock never reported closed,
                    // or its output never reported reset: the light curtain settles the slot EMPTY, the door
                    // is not proven, and no slot is left to open (CP-0009). The door may still stand open, so
                    // it stays the active unlock set. With a slot still loaded after this one, the condition
                    // above fails and the clear pauses UNKNOWN below, as before -- that slot is never opened
                    // beside an unproven door (REQ-0357).
                    UpsertResult(results, CreateDoorUnprovenResult(GetLocker(failureSnapshot, physicalSlot)));
                    DateTimeOffset settledAt = await RecordDoorUnprovenSettlementAsync(
                        context,
                        [physicalSlot],
                        completed,
                        results).ConfigureAwait(false);
                    await SendProgressAsync(progress, "PAUSED", [physicalSlot], completed, CancellationToken.None)
                        .ConfigureAwait(false);
                    return CreateResult(
                        context,
                        AllEmptyDoorUnprovenOutcome,
                        results,
                        WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                        settledAt);
                }

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

                // The slot stays the active unlock set only if its door may be open: pulsed (or the
                // pulse was attempted), or handed over open. Refused before the pulse, it never opened.
                IReadOnlyList<int> stillActive = pulseSent || openHandedOver ? [physicalSlot] : [];
                await WriteVectorStateAsync(
                    context,
                    WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                    stillActive,
                    completed,
                    results,
                    CancellationToken.None).ConfigureAwait(false);
                await SendProgressAsync(
                        progress,
                        "PAUSED",
                        stillActive,
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
        // The scope check and the write are one step under the journal's lock: this write owns
        // RecoveryResultObservedAt and nothing else, so every other field is taken as the journal
        // holds it now. Written from a copy read outside the lock, it put back whatever landed in
        // between -- a released recovery session came back and the entry answered
        // RECOVERY_SESSION_STATE_PENDING for good (onboard-hmi#136 point 3).
        DateTimeOffset? alreadyObserved = null;
        bool mismatch = false;
        WireToGateRecoveryState? written = await _journal.UpdateRecoveryStateAsync(
                current =>
                {
                    // Reset on entry: a change function may be evaluated more than once for the same
                    // step (a test double predicts what a step will write by running it against a state
                    // read outside the lock -- MultiDemandJourneyG2Tests.RestoreWindowJournal). Left set
                    // by an earlier evaluation, these would make this method throw, or report an older
                    // stamp, over a write that actually succeeded.
                    mismatch = false;
                    alreadyObserved = null;
                    if (current.RecoveryVector is not { } persisted || !SameContext(persisted, context))
                    {
                        mismatch = true;
                        return null;
                    }

                    if (current.RecoveryResultObservedAt is { } observedAt)
                    {
                        alreadyObserved = observedAt;
                        return null;
                    }

                    return current with { RecoveryResultObservedAt = _clock.Now.ToUniversalTime() };
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (mismatch)
        {
            throw new InvalidDataException("RECOVERY_STATE_MISMATCH");
        }

        // One of the two is always set by now: the change function either wrote a stamp or reported
        // one already on file. A reason code here could never be emitted.
        return alreadyObserved
            ?? written?.RecoveryResultObservedAt
            ?? throw new UnreachableException();
    }

    private async Task WriteVectorStateAsync(
        WireToGateRecoveryVectorContext context,
        WireToGateRecoveryCheckpoint checkpoint,
        IReadOnlyList<int> activeSlots,
        IReadOnlyList<int> completedSlots,
        IReadOnlyList<WireToGateSlotExecutionResult> results,
        CancellationToken cancellationToken)
    {
        // Merged under the journal's lock against the newest state. This checkpoint owns the attempt,
        // the checkpoint, the active unlock set, the completed slots, the slot results and the vector,
        // and it fills four more from the vector's own context where that context carries them. Every
        // other field, and the fallback for those four, is what the journal holds now -- not what
        // existingState held when the vector started, which is how a checkpoint used to drop a pending
        // result recorded between two of them (onboard-hmi#136 point 4).
        await _journal.UpdateRecoveryStateAsync(
            current => WithVectorState(current, context, checkpoint, activeSlots, completedSlots, results),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the door-unproven settlement and its observation stamp in one journal write, and returns the
    /// stamp.
    /// </summary>
    /// <remarks>
    /// The UNKNOWN and COMPLETED results are written as a checkpoint and then stamped in a second write
    /// (<see cref="EnsureResultObservedAtAsync"/>). Here the pair must not be split: a process that died
    /// between them would come back to an unstamped vector whose active slot no fresh reading proves safe,
    /// and the replay fence would report UNKNOWN over a slot the light curtain had already settled. In one
    /// write, a restart finds the stamp and replays the settlement as recorded, reading no IO
    /// (REPLAY_SAME_DOOR_UNPROVEN_RESULT_AFTER_RESTART). A stamp already on file is kept.
    /// </remarks>
    private async Task<DateTimeOffset> RecordDoorUnprovenSettlementAsync(
        WireToGateRecoveryVectorContext context,
        IReadOnlyList<int> activeSlots,
        IReadOnlyList<int> completedSlots,
        IReadOnlyList<WireToGateSlotExecutionResult> results)
    {
        WireToGateRecoveryState? written = await _journal.UpdateRecoveryStateAsync(
            current => WithVectorState(
                current,
                context,
                WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                activeSlots,
                completedSlots,
                results) with
            {
                RecoveryResultObservedAt = current.RecoveryResultObservedAt ?? _clock.Now.ToUniversalTime()
            },
            CancellationToken.None).ConfigureAwait(false);
        // The change function never returns null, so neither does the journal.
        return written?.RecoveryResultObservedAt ?? throw new UnreachableException();
    }

    /// <summary>
    /// Records a vector that stopped <c>FAILED</c> with no door in doubt, and its observation stamp, in one journal
    /// write, and returns the stamp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The checkpoint is <see cref="WireToGateRecoveryCheckpoint.Prepared"/> with an empty active unlock set: the
    /// one checkpoint whose stamped result <see cref="ExecuteExclusiveAsync"/> and
    /// <see cref="SettleWithoutUnlockAsync"/> replay as <c>FAILED</c>. Written at
    /// <see cref="WireToGateRecoveryCheckpoint.ActiveUnlockSet"/>, the same result came back <c>UNKNOWN</c> when it was
    /// asked for again, to go out under the deduplication key and messageId the first answer already holds
    /// (8005-agv-onboard-hmi#249). Slots the vector finished stay counted complete, so
    /// <see cref="RefuseBeforeUnlockAsync"/> still sees that it acted.
    /// </para>
    /// <para>
    /// One write, not a checkpoint and then a stamp: a process that died between the two would come back to an
    /// unstamped vector and run it again instead of answering what it had already decided. A stamp already on file
    /// is kept.
    /// </para>
    /// </remarks>
    private async Task<DateTimeOffset> RecordFailedResultAsync(
        WireToGateRecoveryVectorContext context,
        IReadOnlyList<int> completedSlots,
        IReadOnlyList<WireToGateSlotExecutionResult> results)
    {
        WireToGateRecoveryState? written = await _journal.UpdateRecoveryStateAsync(
            current => WithVectorState(
                current,
                context,
                WireToGateRecoveryCheckpoint.Prepared,
                [],
                completedSlots,
                results) with
            {
                RecoveryResultObservedAt = current.RecoveryResultObservedAt ?? _clock.Now.ToUniversalTime()
            },
            CancellationToken.None).ConfigureAwait(false);
        // The change function never returns null, so neither does the journal.
        return written?.RecoveryResultObservedAt ?? throw new UnreachableException();
    }

    private static WireToGateRecoveryState WithVectorState(
        WireToGateRecoveryState current,
        WireToGateRecoveryVectorContext context,
        WireToGateRecoveryCheckpoint checkpoint,
        IReadOnlyList<int> activeSlots,
        IReadOnlyList<int> completedSlots,
        IReadOnlyList<WireToGateSlotExecutionResult> results) =>
        current with
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
                ?? current.ExceptionRecoverySessionId,
            RecoveryActionId = context.VectorType is
                WireToGateRecoveryVectorTypes.LoadCompensation
                or WireToGateRecoveryVectorTypes.FaultCargoHandoff
                ? context.PrimaryId
                : current.RecoveryActionId,
            RecoveryOperatorId = context.OperatorId ?? current.RecoveryOperatorId,
            RecoveryOperatorVerifiedAt = context.OperatorVerifiedAt
                ?? current.RecoveryOperatorVerifiedAt
        };

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

    /// <summary>
    /// The two clears CP-0009 lets settle an empty slot whose door is not proven. A fault cargo handoff, a
    /// correction and a forced recovery are not among them.
    /// </summary>
    private static bool SettlesDoorUnproven(WireToGateRecoveryVectorContext context) =>
        context.VectorType is WireToGateRecoveryVectorTypes.LoadCancellation
            or WireToGateRecoveryVectorTypes.LoadCompensation;

    /// <summary>
    /// The light curtain proves the slot EMPTY in a fresh snapshot, and its door is not proven: the lock does
    /// not read locked or the unlock output does not read reset. A light curtain that cannot be read proves
    /// nothing, so such a slot is never this.
    /// </summary>
    private bool IsEmptyWithDoorUnproven(IoSnapshot snapshot, int physicalSlot) =>
        IsFresh(snapshot)
        && GetLocker(snapshot, physicalSlot) is { LightCurtainRaw: true } locker
        && !(locker.LockFeedbackRaw is true && locker.UnlockOutputRaw is false);

    /// <summary>
    /// The slot settled EMPTY with its door unproven: lock and output as read, the reason on it, and
    /// <c>FAILED</c> -- its clearing did not reach the closed loop, which is what the per-slot
    /// <c>COMPLETED</c> of an <c>ALL_EMPTY</c> means and keeps meaning.
    /// </summary>
    private static WireToGateSlotExecutionResult CreateDoorUnprovenResult(LockerSnapshot locker) =>
        new(
            locker.PhysicalNumber,
            "FAILED",
            "EMPTY",
            locker.LockFeedbackRaw switch { true => "LOCKED", false => "UNLOCKED", null => "UNKNOWN" },
            locker.UnlockOutputRaw switch { false => "RESET", true => "ACTIVE", null => "UNKNOWN" },
            [DoorLockUnprovenAfterEmptyReason]);

    /// <summary>
    /// Whether the journal records a door-unproven settlement of <paramref name="context"/>: one of the two
    /// clears, every target slot recorded EMPTY, and at least one carrying the reason. Only this executor
    /// writes that reason, and only in that settlement, so the recorded results are enough to replay it.
    /// </summary>
    private static bool IsDoorUnprovenSettlement(
        WireToGateRecoveryState state,
        WireToGateRecoveryVectorContext context)
    {
        if (!SettlesDoorUnproven(context) || context.Slots.Count == 0)
        {
            return false;
        }

        WireToGateSlotExecutionResult[] recorded = state.SlotResults
            .Where(result => context.Slots.Contains(result.SlotNo))
            .ToArray();
        return recorded.Length == context.Slots.Count
            && recorded.All(result => result.FinalPhysicalState == "EMPTY")
            && recorded.Any(result => result.ReasonCodes.Contains(
                DoorLockUnprovenAfterEmptyReason,
                StringComparer.Ordinal));
    }

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

    /// <summary>The code a latch refusal carries; see <see cref="WireToGateSlotOperationExecutor"/>.</summary>
    private const string FatalFaultLatchedReason = WireToGateSlotOperationExecutor.FatalFaultLatchedReason;

    private sealed class FatalFaultLatchedException() : Exception("FATAL_FAULT_LATCHED");

    /// <summary>
    /// The latch check in front of the pulse (8005-agv-onboard-hmi#191), under the name
    /// <c>FatalFaultScopeArchitectureTests</c> looks for above each registered-as-guarded pulse.
    /// </summary>
    private void ThrowIfFatalFaultLatched()
    {
        if (_fatalFaultLatched())
        {
            throw new FatalFaultLatchedException();
        }
    }

    private static string MapFailureReason(Exception exception) => exception switch
    {
        TimeoutException => "ACTION_NOT_ALLOWED_IN_STATE",
        IOException => "SLOT_STATE_UNKNOWN",
        // Raised with the reason code itself, by the single-door check before a pulse.
        InvalidDataException { Message: "UNLOCK_OUTPUT_NOT_RESET" or "LOCK_NOT_CLOSED" } invalid
            => invalid.Message,
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
