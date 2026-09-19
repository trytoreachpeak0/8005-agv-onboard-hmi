using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// Executes the exact server-frozen slot set for a WIRE_TO_GATE operation.
/// It deliberately has no slot-selection API: every physical side effect is
/// driven by the command received from ControlServer and is journaled first.
/// </summary>
public sealed record WireToGateSlotOperationExecutorOptions(
    TimeSpan UnlockFeedbackTimeout,
    TimeSpan UnlockOutputResetTimeout,
    TimeSpan OperationTimeout,
    TimeSpan FeedbackStableWindow,
    TimeSpan IoSnapshotMaxAge);

/// <summary>
/// Why a prompt went out, so the HMI states it instead of inferring it from the round number.
/// </summary>
public enum WireToGatePromptCause
{
    /// <summary>Not a prompt round: preparing, verifying, finishing, pausing.</summary>
    None,

    /// <summary>The slot's first unlock and the first wait for the operator.</summary>
    FirstOpen,

    /// <summary>The door was shut over the opposite occupancy and the slot is opened again.</summary>
    OppositeReopen,

    /// <summary>OperationTimeout passed with the door still open; only the prompt repeats.</summary>
    PromptCadence,

    /// <summary>
    /// The slot is to be reopened, but the vehicle safety fact does not allow an unlock pulse (an
    /// emergency stop, or a safety projection that cannot be trusted). Nothing is pulsed until it does.
    /// </summary>
    ReopenHeldBySafety
}

/// <summary>
/// One progress report from an executor. <paramref name="PromptRound"/> counts the rounds of waiting
/// for the operator on the active slot, so a prompt deduplicated by key still differs from the round
/// before it.
/// </summary>
public sealed record WireToGateOperationProgress(
    string Phase,
    IReadOnlyList<int> Active,
    IReadOnlyList<int> Completed,
    int PromptRound = 0,
    WireToGatePromptCause Cause = WireToGatePromptCause.None);

public sealed class WireToGateSlotOperationExecutor : IAsyncDisposable
{
    private readonly IIoModuleClient _ioModule;
    private readonly IWireToGateJournal _journal;
    private readonly IClock _clock;
    private static readonly TimeSpan ReopenPermissionPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly WireToGateSlotOperationExecutorOptions _options;
    private readonly Func<bool> _reopenPermitted;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private ActiveOperation? _activeOperation;

    /// <param name="reopenPermitted">
    /// Asked before every automatic reopen pulse. Nothing on this vehicle reads the emergency stop
    /// directly, and nothing in this repository establishes that an emergency stop cuts the unlock
    /// output in hardware, so the executor asks the vehicle safety fact instead: false while the
    /// vehicle is not proven stopped -- RIoT reporting an emergency state that is not OK makes the
    /// control server's projection UNKNOWN (RIOT_EMERGENCY_NOT_OK) -- or while that projection is
    /// stale or unreadable. The first unlock of a slot is the server's command and is not gated here.
    /// </param>
    public WireToGateSlotOperationExecutor(
        IIoModuleClient ioModule,
        IWireToGateJournal journal,
        IClock clock,
        WireToGateSlotOperationExecutorOptions options,
        Func<bool> reopenPermitted)
    {
        _ioModule = ioModule;
        _journal = journal;
        _clock = clock;
        _options = options;
        _reopenPermitted = reopenPermitted ?? throw new ArgumentNullException(nameof(reopenPermitted));
        ValidateOptions(options);
    }

    /// <summary>
    /// Stops the closed loop on <paramref name="slotOperationAttemptId"/> if that attempt is the one
    /// running, and returns once it has stopped (8005-agv-onboard-hmi#78, <c>75d02de</c> redone).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An authorized load cancellation takes the attempt's slots over with a different executor. Left
    /// running, this one would keep pulsing a door shut empty while the other waits for the same slot to
    /// be emptied -- two executors on one lock. The aborted run ends in
    /// <see cref="OperationCanceledException"/> on its caller's side, writes no result and nothing more to
    /// the journal: the attempt stays unsettled with its last active unlock set, which is how the
    /// cancellation learns which door may be standing open.
    /// </para>
    /// <para>
    /// Returns once the operation gate is free again, so no pulse of the aborted run can follow. Returns
    /// false, and touches nothing, when no run or another attempt's run is in progress.
    /// </para>
    /// </remarks>
    public async Task<bool> AbortOperationAsync(
        string slotOperationAttemptId,
        CancellationToken cancellationToken = default)
    {
        RequireUuid(slotOperationAttemptId, nameof(slotOperationAttemptId));
        ActiveOperation? active = Volatile.Read(ref _activeOperation);
        if (active is null
            || !string.Equals(active.SlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            await active.Abort.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // It ended on its own between the read and the cancel, which is what was asked for.
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _operationGate.Release();
        return true;
    }

    public async Task<WireToGateOperationExecutionResult> ExecuteAsync(
        WireToGateSlotOperationCommand command,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Volatile.Write(ref _activeOperation, new ActiveOperation(command.SlotOperationAttemptId, abort));
        try
        {
            return await ExecuteExclusiveAsync(command, progress, abort.Token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _activeOperation, null);
            _operationGate.Release();
        }
    }

    /// <summary>The run <see cref="AbortOperationAsync"/> can stop.</summary>
    private sealed record ActiveOperation(string SlotOperationAttemptId, CancellationTokenSource Abort);

    /// <summary>
    /// Resumes the persisted operation context after an authenticated
    /// RESUME_AFTER_REPAIR action. The resume command can only select the exact
    /// persisted attempt/checkpoint/slot set; it cannot manufacture a new one.
    /// Slots already observed in their desired final state are recorded without
    /// another unlock pulse.
    /// </summary>
    public async Task<WireToGateOperationExecutionResult> ResumeAsync(
        WireToGateSlotOperationResumeCommand resume,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resume);
        // Every refusal up to the first journal write in ResumeExclusiveAsync is thrown as
        // WireToGateResumeNotStartedException: the caller answers those, and only those, with
        // SlotOperationCommandRejected (8005-agv-onboard-hmi#119).
        try
        {
            ValidateResumeCommand(resume);
        }
        catch (InvalidDataException exception)
        {
            throw new WireToGateResumeNotStartedException(exception.Message, exception);
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Volatile.Write(ref _activeOperation, new ActiveOperation(resume.SlotOperationAttemptId, abort));
        cancellationToken = abort.Token;
        try
        {
            WireToGateRecoveryState state = await _journal
                .ReadRecoveryStateAsync(cancellationToken)
                .ConfigureAwait(false);
            WireToGateRecoveryOperationContext context = state.OperationContext
                ?? throw new WireToGateResumeNotStartedException("RECOVERY_OPERATION_CONTEXT_MISSING");
            if (!WireToGateRecoverySafetyPolicy.MatchesPersistedResumeState(
                    state,
                    resume.SlotOperationAttemptId,
                    resume.ProvenRecoveryCheckpoint)
                || !string.Equals(
                    state.ExceptionRecoverySessionId,
                    resume.ExceptionRecoverySessionId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    state.RecoveryActionId,
                    resume.RecoveryActionId,
                    StringComparison.Ordinal)
                || !string.Equals(context.DemandId, resume.DemandId, StringComparison.Ordinal)
                || !string.Equals(
                    context.CommandContentSha256,
                    resume.CommandContentSha256,
                    StringComparison.OrdinalIgnoreCase)
                || !context.Slots.SequenceEqual(resume.Slots))
            {
                throw new WireToGateResumeNotStartedException("RECOVERY_STATE_MISMATCH");
            }

            WireToGateSlotOperationCommand command = context.ToCommand();
            try
            {
                ValidateCommand(command);
            }
            catch (InvalidDataException exception)
            {
                throw new WireToGateResumeNotStartedException(exception.Message, exception);
            }

            return await ResumeExclusiveAsync(
                command,
                state,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _activeOperation, null);
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Puts a result that leaves its operation unsettled on the journal's pending list, so the next
    /// session's RecoveryStateReport names it and the session client sends it again once that report
    /// is acknowledged (CV-OPERATION-RESULT-UNKNOWN-RECONCILE, REPLAY_RESULT_ON_RECONNECT).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recorded before the result is sent, alongside the durable send itself, so no restart can leave
    /// a sent result the journal does not know is pending.
    /// </para>
    /// <para>
    /// A result is pending exactly while its operation is the journal's unsettled one. Whatever settles
    /// the operation -- a completed resume, a cancellation or compensation proof -- clears or replaces
    /// that attempt, and the report only names entries for the attempt still unsettled; this write
    /// also drops entries left behind by attempts settled that way.
    /// </para>
    /// </remarks>
    public async Task RecordPendingResultAsync(
        string slotOperationAttemptId,
        WireToGatePendingResult pending,
        CancellationToken cancellationToken = default)
    {
        RequireUuid(slotOperationAttemptId, nameof(slotOperationAttemptId));
        ArgumentNullException.ThrowIfNull(pending);
        WireToGateRecoveryState state = await _journal.ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(state.UnsettledSlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal)
            || !string.Equals(pending.BusinessId, slotOperationAttemptId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("SLOT_OPERATION_CONFLICT");
        }

        WireToGatePendingResult[] kept = state.PendingResults
            .Where(item => string.Equals(item.BusinessId, slotOperationAttemptId, StringComparison.Ordinal)
                && !string.Equals(item.MessageId, pending.MessageId, StringComparison.Ordinal))
            .Append(pending)
            .ToArray();
        await _journal.WriteRecoveryStateAsync(state with { PendingResults = kept }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkResultRecordedAsync(
        string slotOperationAttemptId,
        CancellationToken cancellationToken = default)
    {
        RequireUuid(slotOperationAttemptId, nameof(slotOperationAttemptId));
        // Read, checked and written as one step under the journal's lock, as the CLOSED release does
        // (onboard-hmi#123). A late acknowledgement records its attempt while the next operation may already be
        // journaling its Prepared; with a separate read and write, that write could land in between and this one
        // would put the old state back over it, wiping the operation now at the doors (onboard-hmi#127).
        WireToGateRecoveryState? written = await _journal.UpdateRecoveryStateAsync(
            state => string.Equals(state.UnsettledSlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal)
                ? Recorded(state)
                : null,
            cancellationToken).ConfigureAwait(false);
        if (written is null)
        {
            throw new InvalidDataException("SLOT_OPERATION_CONFLICT");
        }
    }

    private static WireToGateRecoveryState Recorded(WireToGateRecoveryState state) =>
        state with
        {
            UnsettledSlotOperationAttemptId = null,
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ResultRecorded,
            ActiveUnlockSlots = [],
            PendingResults = [],
            OperationContext = null,
            CompletedSlots = [],
            SlotResults = [],
            ExceptionRecoverySessionId = null,
            RecoveryActionId = null,
            RecoverySessionRequestId = null,
            RecoveryActionRequestId = null,
            RecoveryReason = null,
            RecoveryOperatorId = null,
            RecoveryOperatorVerifiedAt = null,
            RecoveryVector = null,
            RecoveryResultObservedAt = null,
            PendingLoadCancellation = null,
            LastCompletedLoadOperationContext = state.OperationContext?.OperationType == OperationType.Load
                ? state.OperationContext
                : state.LastCompletedLoadOperationContext
        };

    /// <summary>
    /// Settles a slot operation whose executing process is gone (8005-agv-program#40). It reads the
    /// live IO only, emits no unlock pulse at all, and turns the journal's one unsettled attempt into
    /// a result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A process that exits after the unlock and before the result is written -- a closed window, a
    /// crash, a power cut, a servicing reboot -- leaves an unsettled attempt behind that nothing else
    /// ever settles: the vehicle only settles an attempt when a command or a recovery action arrives,
    /// the server does not judge a journey Blocked while no result has come, and the recovery entry
    /// requires the journey to be Blocked already. Both ends then wait for each other, as measured on
    /// the rig.
    /// </para>
    /// <para>
    /// The wrap-up follows ADR-cross-0017: after a restart no further unlock may go out, so the live
    /// physical state is all there is to go on and only two conclusions are available. Every target
    /// slot that was opened sits in its desired final state and none is left unopened -- the operator
    /// finished the job after the last process died -- makes the outcome a definite COMPLETED, and
    /// calling that unknown would turn a settled result into an unsettled one. Everything else is
    /// UNKNOWN, which the server turns into RecoveryRequired.
    /// </para>
    /// <para>
    /// A definite failure is never produced here: FAILED presupposes the station deadline has passed
    /// (ADR-cross-0058 decision 5), and a restart is not a deadline. The physical fields always carry
    /// the real readings (decision 6) -- what is unknown is how this operation should proceed, not
    /// what the slot looks like. A slot only reaches COMPLETED when <see cref="IsFinalState"/> holds,
    /// which includes the unlock output having fallen back, so this path and the executor's other two
    /// agree on what a completed slot is (onboard-hmi#49).
    /// </para>
    /// <para>
    /// The caller is responsible for establishing that nobody is executing this attempt: this method
    /// cannot tell. The executor outlives a connection, so during a reconnect it may still be running
    /// while the journal looks exactly as it does after a restart.
    /// </para>
    /// </remarks>
    public async Task<WireToGateOperationExecutionResult> SettleInterruptedAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SettleInterruptedExclusiveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<WireToGateOperationExecutionResult> SettleInterruptedExclusiveAsync(
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await _journal
            .ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        WireToGateRecoveryOperationContext context = state.OperationContext
            ?? throw new InvalidDataException("RECOVERY_OPERATION_CONTEXT_MISSING");
        // A recovery vector has its own journal entry and its own resume rules; not this path's job.
        // Nor is an attempt with an unanswered load cancellation on file (1acb018, onboard-hmi#78): the
        // server may already have authorized it, and the conclusion is that cancellation's.
        if (!string.Equals(
                state.UnsettledSlotOperationAttemptId,
                context.SlotOperationAttemptId,
                StringComparison.Ordinal)
            || state.RecoveryVector is not null
            || state.PendingLoadCancellation is not null)
        {
            throw new InvalidDataException("RECOVERY_STATE_MISMATCH");
        }

        WireToGateSlotOperationCommand command = context.ToCommand();
        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        bool fresh = snapshot.IsConnected
            && SafetyRules.IsSnapshotFresh(snapshot, _clock.Now, _options.IoSnapshotMaxAge);
        List<int> completed = [];
        List<int> stillActive = [];
        bool unsafeSlot = false;
        List<WireToGateSlotExecutionResult> results = [];
        foreach (int physicalSlot in command.Slots)
        {
            LockerSnapshot locker = fresh
                ? TryGetLocker(snapshot, physicalSlot - 1)
                : LockerSnapshot.Unknown(physicalSlot - 1, snapshot.ObservedAt);
            bool opened = state.ActiveUnlockSlots.Contains(physicalSlot)
                || state.CompletedSlots.Contains(physicalSlot);
            if (!opened)
            {
                // Never opened: the door is shut and the lock closed, so report what is read and
                // leave reasonCodes empty (decision 6).
                UpsertResult(results, CreateSlotResult(locker, "NOT_STARTED", []));
            }
            else if (IsFinalState(locker, command.ExpectedOccupied))
            {
                // A slot the journal calls completed is re-read too: a physical change since the
                // checkpoint stops it counting as completed.
                UpsertResult(results, CreateSlotResult(locker, "COMPLETED", []));
                completed.Add(physicalSlot);
            }
            else
            {
                // When the readings are there, the only unknown is the next step -- unlock again and
                // keep loading, or give this demand up; the journal and the IO do not imply a single
                // answer (the "single lawful next step" of ADR-cross-0017). When they are not there,
                // it is simply unreadable.
                UpsertResult(
                    results,
                    CreateSlotResult(
                        locker,
                        "UNKNOWN",
                        [fresh ? "RECOVERY_CHECKPOINT_NOT_UNIQUE" : "SLOT_STATE_UNKNOWN"]));
                if (!fresh || !IsSafeFinish(snapshot, physicalSlot - 1))
                {
                    unsafeSlot = true;
                    // Only the door that was opening can be standing open from this operation: a
                    // completed slot closed its loop, and without IO it is unreadable, not open. So
                    // the active set stays the journal's one slot (REQ-0357, ADR-cross-0061).
                    if (state.ActiveUnlockSlots.Contains(physicalSlot))
                    {
                        stillActive.Add(physicalSlot);
                    }
                }
            }
        }

        bool allCompleted = completed.Count == command.Slots.Count;
        WireToGateRecoveryCheckpoint checkpoint = !unsafeSlot
            ? WireToGateRecoveryCheckpoint.SafeFinishReached
            : WireToGateRecoveryCheckpoint.ActiveUnlockSet;
        // Written in the same shape as an UNKNOWN decided mid-execution: the journal keeps the
        // OperationContext and the unsettled attempt, which is exactly what compensation and the
        // recovery vectors recognise. CancellationToken.None for the same reason -- once the
        // conclusion has been read it has to reach the disk.
        await WriteRecoveryStateAsync(
            context,
            checkpoint,
            stillActive,
            completed,
            results,
            state,
            CancellationToken.None).ConfigureAwait(false);
        return CreateResult(command, allCompleted ? "COMPLETED" : "UNKNOWN", results, checkpoint);
    }

    public ValueTask DisposeAsync()
    {
        _operationGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<WireToGateOperationExecutionResult> ExecuteExclusiveAsync(
        WireToGateSlotOperationCommand command,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        // An attempt the journal says was started and is unsettled is not a new command, whoever sends
        // it again (1086c4a, onboard-hmi#78). Its conclusion is a cancellation's, a recovery action's or
        // the interrupted settlement's. Run again it would find the door open and make up a refusal, or
        // find it shut and unlock it without authorization (ADR-cross-0016, ADR-cross-0017).
        WireToGateRecoveryState journaled = await _journal.ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(
                journaled.UnsettledSlotOperationAttemptId,
                command.SlotOperationAttemptId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("SLOT_OPERATION_ALREADY_STARTED");
        }

        DateTimeOffset started = _clock.Now;
        IoSnapshot initial = _ioModule.CurrentSnapshot;
        IReadOnlyList<int> physicallyUnknown = journaled.ForcedIsolation?.PhysicallyUnknownSlots ?? [];
        if (command.Slots.Any(physicallyUnknown.Contains))
        {
            // A slot opened by hand after the power was cut (REQ-0241): whatever the IO reads now,
            // nothing proves what state it was left in, so it is not operated until a hardware
            // recovery record clears it (onboard-hmi#107).
            return CreateRejectedResult(command, initial, started, physicallyUnknown);
        }

        string? precheckFailure = ValidateBeforeOperation(initial, command, command.Slots);
        if (precheckFailure is not null)
        {
            return CreateRejectedResult(command, initial, started);
        }

        // A new operation starts from a clean journal, except for the device facts that outlive
        // every operation.
        WireToGateRecoveryState fresh = WireToGateRecoveryState.Empty with
        {
            ForcedIsolation = journaled.ForcedIsolation
        };
        WireToGateRecoveryOperationContext context =
            WireToGateRecoveryOperationContext.FromCommand(command);
        List<int> completed = [];
        List<WireToGateSlotExecutionResult> results = [];
        await WriteRecoveryStateAsync(
            context,
            WireToGateRecoveryCheckpoint.Prepared,
            [],
            completed,
            results,
            fresh,
            cancellationToken).ConfigureAwait(false);
        await SendProgressAsync(progress, new("PREPARING", [], []), cancellationToken).ConfigureAwait(false);

        return await ExecuteRemainingSlotsAsync(
            command,
            context,
            fresh,
            completed,
            results,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WireToGateOperationExecutionResult> ResumeExclusiveAsync(
        WireToGateSlotOperationCommand command,
        WireToGateRecoveryState state,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        if (!snapshot.IsConnected
            || !SafetyRules.IsSnapshotFresh(snapshot, _clock.Now, _options.IoSnapshotMaxAge))
        {
            throw new WireToGateResumeNotStartedException("SLOT_STATE_UNKNOWN");
        }

        List<int> completed = state.CompletedSlots
            .Where(command.Slots.Contains)
            .Distinct()
            .Order()
            .ToList();
        List<WireToGateSlotExecutionResult> results = state.SlotResults
            .Where(result => command.Slots.Contains(result.SlotNo))
            .GroupBy(result => result.SlotNo)
            .Select(group => group.Last())
            .OrderBy(result => result.SlotNo)
            .ToList();

        // Re-read every target before deciding whether a recorded slot can be
        // reused. A physical state change since the checkpoint makes it
        // unresolved and therefore requires a fresh safe operation.
        foreach (int physicalSlot in command.Slots)
        {
            LockerSnapshot locker = snapshot.GetLocker(physicalSlot - 1);
            if (IsFinalState(locker, command.ExpectedOccupied))
            {
                completed.Add(physicalSlot);
                UpsertResult(results, CreateSlotResult(locker, "COMPLETED", []));
            }
            else
            {
                completed.Remove(physicalSlot);
            }
        }

        int[] remaining = command.Slots.Where(slot => !completed.Contains(slot)).ToArray();
        string? precheckFailure = ValidateBeforeOperation(snapshot, command, remaining);
        if (precheckFailure is not null)
        {
            throw new WireToGateResumeNotStartedException(precheckFailure);
        }

        // The first journal write of this resume. From here on the executor has changed the
        // operation's recorded state and may pulse, so nothing below is "not started".
        completed = completed.Distinct().Order().ToList();
        WireToGateRecoveryOperationContext context =
            WireToGateRecoveryOperationContext.FromCommand(command);
        await WriteRecoveryStateAsync(
            context,
            WireToGateRecoveryCheckpoint.ActiveUnlockSet,
            [],
            completed,
            results,
            state,
            cancellationToken).ConfigureAwait(false);
        await SendProgressAsync(progress, new("PREPARING", [], completed), cancellationToken)
            .ConfigureAwait(false);

        return await ExecuteRemainingSlotsAsync(
            command,
            context,
            state,
            completed,
            results,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WireToGateOperationExecutionResult> ExecuteRemainingSlotsAsync(
        WireToGateSlotOperationCommand command,
        WireToGateRecoveryOperationContext context,
        WireToGateRecoveryState existingState,
        List<int> completed,
        List<WireToGateSlotExecutionResult> results,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        foreach (int physicalSlot in command.Slots)
        {
            if (completed.Contains(physicalSlot))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            int slotIndex = physicalSlot - 1;
            // Written once per slot, not once per reopen: the seventeenth reopen answers the same
            // recovery question as the first -- this slot is in the active set and its door may be
            // open -- so the journal bytes would not change.
            await WriteRecoveryStateAsync(
                context,
                WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                [physicalSlot],
                completed,
                results,
                existingState,
                cancellationToken).ConfigureAwait(false);

            try
            {
                LockerSnapshot completedLocker = await DriveSlotToTargetStateAsync(
                    command,
                    physicalSlot,
                    completed,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                WireToGateSlotExecutionResult result = CreateSlotResult(
                    completedLocker,
                    "COMPLETED",
                    []);
                UpsertResult(results, result);
                completed.Add(physicalSlot);
                await WriteRecoveryStateAsync(
                    context,
                    WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                    [],
                    completed,
                    results,
                    existingState,
                    cancellationToken).ConfigureAwait(false);
                await SendProgressAsync(progress, new("VERIFYING", [], completed), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException)
            {
                // Only the three conditions of ADR-cross-0058 decision 2 reach this branch: the slot
                // state cannot be read, the lock feedback is not valid, or the unlock output cannot be
                // confirmed reset. An operator who has not loaded, unloaded or shut the door is still
                // inside DriveSlotToTargetStateAsync. One snapshot serves the failed slot and the
                // slots never started, so the two cannot disagree about what was read.
                IoSnapshot failureSnapshot = _ioModule.CurrentSnapshot;
                string reason = MapFailureReason(exception);
                UpsertResult(
                    results,
                    CreateSlotResult(ReadLocker(failureSnapshot, slotIndex), "UNKNOWN", [reason]));
                foreach (int notStarted in command.Slots
                    .Where(slot => slot != physicalSlot && !completed.Contains(slot)))
                {
                    // Decision 6: a slot never opened has nothing uncertain about it. Its three
                    // fields are what the IO reads, and reasonCodes stay empty -- why the operation
                    // stopped belongs to the slot that stopped it, not to this one.
                    UpsertResult(
                        results,
                        CreateSlotResult(ReadLocker(failureSnapshot, notStarted - 1), "NOT_STARTED", []));
                }

                bool safeFinish = IsSafeFinish(failureSnapshot, slotIndex);
                WireToGateRecoveryCheckpoint failureCheckpoint = safeFinish
                    ? WireToGateRecoveryCheckpoint.SafeFinishReached
                    : WireToGateRecoveryCheckpoint.ActiveUnlockSet;
                IReadOnlyList<int> failureActiveSlots = safeFinish ? [] : [physicalSlot];
                await WriteRecoveryStateAsync(
                    context,
                    failureCheckpoint,
                    failureActiveSlots,
                    completed,
                    results,
                    existingState,
                    CancellationToken.None).ConfigureAwait(false);
                await SendProgressAsync(
                        progress,
                        new(safeFinish ? "SAFE_FINISH" : "PAUSED", failureActiveSlots, completed),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return CreateResult(command, "UNKNOWN", results, failureCheckpoint);
            }
        }

        await WriteRecoveryStateAsync(
            context,
            WireToGateRecoveryCheckpoint.SafeFinishReached,
            [],
            completed,
            results,
            existingState,
            cancellationToken).ConfigureAwait(false);
        await SendProgressAsync(progress, new("SAFE_FINISH", [], completed), cancellationToken)
            .ConfigureAwait(false);
        return CreateResult(command, "COMPLETED", results, WireToGateRecoveryCheckpoint.SafeFinishReached);
    }

    /// <summary>
    /// Drives one slot to its target state (ADR-cross-0058 decision 1). A door shut over the opposite
    /// occupancy -- loading without putting a basket in, unloading without taking it out -- gets a
    /// fresh unlock pulse and another prompt: not a failure, not recovery, and no retry limit
    /// (ADR-cross-0040). A door that stays open meets neither condition, and every
    /// <see cref="WireToGateSlotOperationExecutorOptions.OperationTimeout"/> it only prompts again
    /// (decision 3). Returns the reading that satisfied <see cref="IsFinalState"/>; throws only for
    /// the decision 2 conditions, which the caller turns into UNKNOWN.
    /// </summary>
    /// <remarks>
    /// With no deadline any more, a reopen can come long after the command did, so every reopen pulse
    /// first asks the vehicle safety fact. While it says no, the slot is held: the door is shut and
    /// locked, nothing is pulsed, and the operator is told why on the prompt cadence.
    /// </remarks>
    private async Task<LockerSnapshot> DriveSlotToTargetStateAsync(
        WireToGateSlotOperationCommand command,
        int physicalSlot,
        IReadOnlyList<int> completed,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        int slotIndex = physicalSlot - 1;
        bool unlockNeeded = true;
        WireToGatePromptCause cause = WireToGatePromptCause.FirstOpen;
        for (int promptRound = 0; ; promptRound++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (unlockNeeded && cause == WireToGatePromptCause.OppositeReopen && !_reopenPermitted())
            {
                promptRound = await HoldReopenUntilPermittedAsync(
                    physicalSlot,
                    completed,
                    promptRound,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            if (unlockNeeded)
            {
                // Every pulse, the first and each reopen, asks the whole vehicle first: another door not
                // proven shut stops the operation here, before this one opens beside it (REQ-0357).
                IoSnapshot beforePulse = _ioModule.CurrentSnapshot;
                if (WireToGateSingleDoorRule.OtherDoorNotShut(
                        beforePulse,
                        IsFresh(beforePulse),
                        physicalSlot) is { } otherDoor)
                {
                    throw new InvalidDataException(otherDoor);
                }

                await SendProgressAsync(
                    progress,
                    new("UNLOCKING", [physicalSlot], completed, promptRound, cause),
                    cancellationToken).ConfigureAwait(false);
                await _ioModule.PulseUnlockAsync(slotIndex, cancellationToken).ConfigureAwait(false);
                // Both are hardware responses in milliseconds. Missing either one means the lock
                // feedback or the unlock output cannot be trusted -- decision 2, not the operator.
                LockerSnapshot unlocked = await _ioModule.WaitForLockerAsync(
                    slotIndex,
                    locker => locker.IsKnown && !locker.IsLocked,
                    _options.UnlockFeedbackTimeout,
                    _options.FeedbackStableWindow,
                    cancellationToken).ConfigureAwait(false);
                await _ioModule.WaitForLockerAsync(
                    slotIndex,
                    locker => locker.IsKnown
                        && locker.ObservedAt >= unlocked.ObservedAt
                        && locker.UnlockOutputRaw is false,
                    _options.UnlockOutputResetTimeout,
                    _options.FeedbackStableWindow,
                    cancellationToken).ConfigureAwait(false);
            }

            await SendProgressAsync(
                progress,
                new("WAITING_OPERATOR", [physicalSlot], completed, promptRound, cause),
                cancellationToken).ConfigureAwait(false);
            (SlotWaitOutcome outcome, LockerSnapshot? closed) = await WaitForClosedDoorAsync(
                slotIndex,
                command.ExpectedOccupied,
                cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case SlotWaitOutcome.Unknown:
                    throw new InvalidDataException("SLOT_STATE_UNKNOWN");
                case SlotWaitOutcome.PromptCadence:
                    // The door is still open. Another pulse means nothing to a lock that is already
                    // released, so this round only prompts again.
                    unlockNeeded = false;
                    cause = WireToGatePromptCause.PromptCadence;
                    continue;
            }

            // The door is shut and the occupancy is known either way; an output still energised is
            // given its reset timeout, and one that does not fall back throws from here (decision 2).
            LockerSnapshot settled = closed!.UnlockOutputRaw is false
                ? closed
                : await _ioModule.WaitForLockerAsync(
                    slotIndex,
                    locker => locker.IsKnown
                        && locker.IsLocked
                        && locker.HasCargo == closed.HasCargo
                        && locker.UnlockOutputRaw is false,
                    _options.UnlockOutputResetTimeout,
                    _options.FeedbackStableWindow,
                    cancellationToken).ConfigureAwait(false);
            if (outcome == SlotWaitOutcome.Reached)
            {
                return settled;
            }

            unlockNeeded = true;
            cause = WireToGatePromptCause.OppositeReopen;
        }
    }

    /// <summary>
    /// Waits, without pulsing, until the vehicle safety fact allows the reopen. Prompts once at once
    /// and again every <see cref="WireToGateSlotOperationExecutorOptions.OperationTimeout"/>, each as a
    /// round of its own. Returns the round the reopen itself uses.
    /// </summary>
    private async Task<int> HoldReopenUntilPermittedAsync(
        int physicalSlot,
        IReadOnlyList<int> completed,
        int promptRound,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        await SendProgressAsync(
            progress,
            new("WAITING_OPERATOR", [physicalSlot], completed, promptRound, WireToGatePromptCause.ReopenHeldBySafety),
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset nextPrompt = _clock.Now + _options.OperationTimeout;
        while (!_reopenPermitted())
        {
            await Task.Delay(ReopenPermissionPollInterval, cancellationToken).ConfigureAwait(false);
            if (_clock.Now >= nextPrompt)
            {
                promptRound++;
                await SendProgressAsync(
                    progress,
                    new("WAITING_OPERATOR", [physicalSlot], completed, promptRound, WireToGatePromptCause.ReopenHeldBySafety),
                    cancellationToken).ConfigureAwait(false);
                nextPrompt = _clock.Now + _options.OperationTimeout;
            }
        }

        return promptRound + 1;
    }

    private enum SlotWaitOutcome
    {
        Reached,
        Opposite,
        Unknown,
        PromptCadence
    }

    /// <summary>
    /// Waits for the first of: the door shut over the expected occupancy, the door shut over the
    /// opposite one, the slot turning unreadable -- each held for
    /// <see cref="WireToGateSlotOperationExecutorOptions.FeedbackStableWindow"/> -- or one
    /// <see cref="WireToGateSlotOperationExecutorOptions.OperationTimeout"/> passing with none of them.
    /// </summary>
    /// <remarks>
    /// The closed-door predicates leave the unlock output out on purpose: a door shut with the output
    /// still energised is the case <see cref="DriveSlotToTargetStateAsync"/> gives its reset timeout,
    /// and folding the output in here would turn it into an open door that prompts forever.
    /// </remarks>
    private async Task<(SlotWaitOutcome Outcome, LockerSnapshot? Locker)> WaitForClosedDoorAsync(
        int slotIndex,
        bool expectedOccupied,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource waitCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<LockerSnapshot> reachedTask = _ioModule.WaitForLockerAsync(
            slotIndex,
            locker => locker.IsKnown && locker.IsLocked && locker.HasCargo == expectedOccupied,
            _options.OperationTimeout,
            _options.FeedbackStableWindow,
            waitCts.Token);
        Task<LockerSnapshot> oppositeTask = _ioModule.WaitForLockerAsync(
            slotIndex,
            locker => locker.IsKnown && locker.IsLocked && locker.HasCargo != expectedOccupied,
            _options.OperationTimeout,
            _options.FeedbackStableWindow,
            waitCts.Token);
        Task<LockerSnapshot> unknownTask = _ioModule.WaitForLockerAsync(
            slotIndex,
            locker => !locker.IsKnown,
            _options.OperationTimeout,
            _options.FeedbackStableWindow,
            waitCts.Token);
        try
        {
            Task<LockerSnapshot> winner = await Task.WhenAny(reachedTask, oppositeTask, unknownTask)
                .ConfigureAwait(false);
            LockerSnapshot locker = await winner.ConfigureAwait(false);
            return winner == reachedTask
                ? (SlotWaitOutcome.Reached, locker)
                : winner == oppositeTask
                    ? (SlotWaitOutcome.Opposite, locker)
                    : (SlotWaitOutcome.Unknown, locker);
        }
        catch (TimeoutException)
        {
            return (SlotWaitOutcome.PromptCadence, null);
        }
        finally
        {
            await waitCts.CancelAsync().ConfigureAwait(false);
            await ObserveAsync(reachedTask).ConfigureAwait(false);
            await ObserveAsync(oppositeTask).ConfigureAwait(false);
            await ObserveAsync(unknownTask).ConfigureAwait(false);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or TimeoutException or IOException)
        {
            // The round was decided by another wait; how the losing one ended changes nothing.
        }
    }

    private async Task WriteRecoveryStateAsync(
        WireToGateRecoveryOperationContext context,
        WireToGateRecoveryCheckpoint checkpoint,
        IReadOnlyList<int> activeSlots,
        IReadOnlyList<int> completedSlots,
        IReadOnlyList<WireToGateSlotExecutionResult> results,
        WireToGateRecoveryState existingState,
        CancellationToken cancellationToken)
    {
        // The one field of this record another writer owns while the operation runs: the operator's
        // unanswered load cancellation for this attempt, journaled by the business service after this
        // run read its existingState. It is read afresh so a checkpoint does not drop it; one left over
        // from another attempt goes, as it always did (onboard-hmi#78).
        WireToGatePendingLoadCancellation? pending = (await _journal
                .ReadRecoveryStateAsync(cancellationToken)
                .ConfigureAwait(false))
            .PendingLoadCancellation;
        if (!string.Equals(
                pending?.SlotOperationAttemptId,
                context.SlotOperationAttemptId,
                StringComparison.Ordinal))
        {
            pending = existingState.PendingLoadCancellation;
        }

        await _journal.WriteRecoveryStateAsync(
            new WireToGateRecoveryState(
                context.SlotOperationAttemptId,
                checkpoint,
                activeSlots.Order().ToArray(),
                existingState.ForcedRecoveryGeneration,
                existingState.PendingResults)
            {
                OperationContext = context,
                CompletedSlots = completedSlots.Distinct().Order().ToArray(),
                SlotResults = results
                    .GroupBy(result => result.SlotNo)
                    .Select(group => group.Last())
                    .OrderBy(result => result.SlotNo)
                    .ToArray(),
                ExceptionRecoverySessionId = existingState.ExceptionRecoverySessionId,
                RecoveryActionId = existingState.RecoveryActionId,
                RecoverySessionRequestId = existingState.RecoverySessionRequestId,
                RecoveryActionRequestId = existingState.RecoveryActionRequestId,
                RecoveryReason = existingState.RecoveryReason,
                RecoveryOperatorId = existingState.RecoveryOperatorId,
                RecoveryOperatorVerifiedAt = existingState.RecoveryOperatorVerifiedAt,
                RecoveryVector = existingState.RecoveryVector,
                RecoveryResultObservedAt = existingState.RecoveryResultObservedAt,
                LastCompletedLoadOperationContext = existingState.LastCompletedLoadOperationContext,
                PendingLoadCancellation = pending,
                ForcedIsolation = existingState.ForcedIsolation
            },
            cancellationToken).ConfigureAwait(false);
    }

    private string? ValidateBeforeOperation(
        IoSnapshot snapshot,
        WireToGateSlotOperationCommand command,
        IReadOnlyList<int> physicalSlots)
    {
        if (physicalSlots.Count == 0)
        {
            return null;
        }

        if (!snapshot.IsConnected || !SafetyRules.IsSnapshotFresh(snapshot, _clock.Now, _options.IoSnapshotMaxAge))
        {
            return "SLOT_STATE_UNKNOWN";
        }

        foreach (int physicalSlot in physicalSlots)
        {
            LockerSnapshot locker = snapshot.GetLocker(physicalSlot - 1);
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

            if (locker.HasCargo == command.ExpectedOccupied)
            {
                // Both occupancy mismatches are one protocol-level conflict:
                // the physical slot state disagrees with the requested
                // operation. Keep the legacy, more specific codes in the
                // non-WIRE_TO_GATE SafetyRules path.
                return "SLOT_OPERATION_CONFLICT";
            }
        }

        return null;
    }

    /// <remarks>
    /// Nothing was opened, so every slot is NOT_STARTED with the fields the IO reads (decision 6).
    /// Each slot that fails the precheck on its own carries its own refusal reason -- all of them when
    /// the snapshot itself cannot be trusted -- and a slot that was merely next to one carries none.
    /// </remarks>
    /// <param name="physicallyUnknown">
    /// Slots under a forced isolation. Each is refused as <c>SLOT_INOPERABLE</c> and reported
    /// <c>UNKNOWN</c> in every physical field: a reading taken now says nothing about a slot opened
    /// by hand.
    /// </param>
    private WireToGateOperationExecutionResult CreateRejectedResult(
        WireToGateSlotOperationCommand command,
        IoSnapshot snapshot,
        DateTimeOffset observedAt,
        IReadOnlyList<int>? physicallyUnknown = null)
    {
        IReadOnlyList<WireToGateSlotExecutionResult> results = command.Slots
            .Select(slot => physicallyUnknown?.Contains(slot) == true
                ? CreateSlotResult(
                    LockerSnapshot.Unknown(slot - 1, snapshot.ObservedAt),
                    "NOT_STARTED",
                    ["SLOT_INOPERABLE"])
                : CreateSlotResult(
                    ReadLocker(snapshot, slot - 1),
                    "NOT_STARTED",
                    ValidateBeforeOperation(snapshot, command, [slot]) is { } reason ? [reason] : []))
            .ToArray();
        return CreateResult(command, "FAILED", results, WireToGateRecoveryCheckpoint.None, observedAt);
    }

    private WireToGateOperationExecutionResult CreateResult(
        WireToGateSlotOperationCommand command,
        string outcome,
        IReadOnlyList<WireToGateSlotExecutionResult> results,
        WireToGateRecoveryCheckpoint checkpoint,
        DateTimeOffset? observedAt = null) =>
        new(
            command.DemandId,
            command.SlotOperationAttemptId,
            command.OperationType,
            outcome,
            results.OrderBy(result => result.SlotNo).ToArray(),
            observedAt ?? _clock.Now,
            checkpoint switch
            {
                WireToGateRecoveryCheckpoint.None => "NONE",
                WireToGateRecoveryCheckpoint.Prepared => "PREPARED",
                WireToGateRecoveryCheckpoint.ActiveUnlockSet => "ACTIVE_UNLOCK_SET",
                WireToGateRecoveryCheckpoint.SafeFinishReached => "SAFE_FINISH_REACHED",
                WireToGateRecoveryCheckpoint.ResultRecorded => "RESULT_RECORDED",
                _ => throw new ArgumentOutOfRangeException(nameof(checkpoint))
            },
            string.Empty);

    private static WireToGateSlotExecutionResult CreateSlotResult(
        LockerSnapshot locker,
        string outcome,
        IReadOnlyList<string> reasonCodes) =>
        new(
            locker.PhysicalNumber,
            outcome,
            locker.IsKnown ? locker.HasCargo ? "OCCUPIED" : "EMPTY" : "UNKNOWN",
            locker.IsKnown ? locker.IsLocked ? "LOCKED" : "UNLOCKED" : "UNKNOWN",
            locker.IsKnown ? locker.UnlockOutputRaw is true ? "ACTIVE" : "RESET" : "UNKNOWN",
            reasonCodes);

    private static bool IsFinalState(LockerSnapshot locker, bool expectedOccupied) =>
        locker.IsKnown
        && locker.IsLocked
        && locker.UnlockOutputRaw is false
        && locker.HasCargo == expectedOccupied;

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

    private static LockerSnapshot TryGetLocker(IoSnapshot snapshot, int slotIndex) =>
        snapshot.Lockers.FirstOrDefault(locker => locker.SlotIndex == slotIndex)
        ?? LockerSnapshot.Unknown(slotIndex, snapshot.ObservedAt);

    /// <summary>
    /// What a result may state about a slot: the reading itself when the snapshot is connected and
    /// fresh, otherwise UNKNOWN -- a stale reading is not a reading.
    /// </summary>
    private LockerSnapshot ReadLocker(IoSnapshot snapshot, int slotIndex) =>
        snapshot.IsConnected && SafetyRules.IsSnapshotFresh(snapshot, _clock.Now, _options.IoSnapshotMaxAge)
            ? TryGetLocker(snapshot, slotIndex)
            : LockerSnapshot.Unknown(slotIndex, snapshot.ObservedAt);

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

    /// <summary>
    /// Progress is advisory. Reporting it can fail -- the connection drops while the operator is
    /// still at the door -- and that says nothing about the slot, so a failure never reaches the
    /// IO failure branch: UNKNOWN comes from IO readings only. The caller logs its own failures; only
    /// cancellation of the operation itself passes through.
    /// </summary>
    private static async Task SendProgressAsync(
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        WireToGateOperationProgress report,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            await progress(report, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Deliberately swallowed; see the summary.
        }
    }

    private bool IsFresh(IoSnapshot snapshot) =>
        snapshot.IsConnected && SafetyRules.IsSnapshotFresh(snapshot, _clock.Now, _options.IoSnapshotMaxAge);

    private bool IsSafeFinish(IoSnapshot snapshot, int slotIndex)
    {
        if (!snapshot.IsConnected
            || !SafetyRules.IsSnapshotFresh(snapshot, _clock.Now, _options.IoSnapshotMaxAge))
        {
            return false;
        }

        LockerSnapshot locker = TryGetLocker(snapshot, slotIndex);
        return locker.IsKnown && locker.IsLocked && locker.UnlockOutputRaw is false;
    }

    private static void ValidateCommand(WireToGateSlotOperationCommand command)
    {
        RequireUuid(command.MessageId, nameof(command.MessageId));
        RequireUuid(command.DemandId, nameof(command.DemandId));
        RequireUuid(command.OperationSessionId, nameof(command.OperationSessionId));
        RequireUuid(command.SlotOperationAttemptId, nameof(command.SlotOperationAttemptId));
        RequireSha256(command.CommandContentSha256, nameof(command.CommandContentSha256));
        if (command.Slots is null
            || command.Slots.Count is < 1 or > 8
            || command.Slots.Any(slot => slot is < 1 or > 8)
            || command.Slots.Distinct().Count() != command.Slots.Count
            || !command.Slots.SequenceEqual(command.Slots.Order())
            || command.ExpectedBasketCount != command.Slots.Count
            || command.ExpectedOccupied != (command.OperationType == OperationType.Load))
        {
            throw new InvalidDataException("SLOT_SET_INVALID");
        }
    }

    private static void ValidateResumeCommand(WireToGateSlotOperationResumeCommand command)
    {
        RequireUuid(command.MessageId, nameof(command.MessageId));
        RequireUuid(command.ExceptionRecoverySessionId, nameof(command.ExceptionRecoverySessionId));
        RequireUuid(command.RecoveryActionId, nameof(command.RecoveryActionId));
        RequireUuid(command.DemandId, nameof(command.DemandId));
        RequireUuid(command.SlotOperationAttemptId, nameof(command.SlotOperationAttemptId));
        RequireSha256(command.CommandContentSha256, nameof(command.CommandContentSha256));
        if (command.Slots is null
            || command.Slots.Count is < 1 or > 8
            || command.Slots.Any(slot => slot is < 1 or > 8)
            || command.Slots.Distinct().Count() != command.Slots.Count
            || !command.Slots.SequenceEqual(command.Slots.Order())
            || command.ProvenRecoveryCheckpoint is not WireToGateRecoveryCheckpoint.Prepared
                and not WireToGateRecoveryCheckpoint.ActiveUnlockSet
                and not WireToGateRecoveryCheckpoint.SafeFinishReached)
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

    private static void RequireUuid(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out _))
        {
            throw new InvalidDataException($"{name}必须是标准UUID。");
        }
    }

    private static void RequireSha256(string value, string name)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{name}必须是64位SHA-256十六进制字符串。");
        }
    }
}
