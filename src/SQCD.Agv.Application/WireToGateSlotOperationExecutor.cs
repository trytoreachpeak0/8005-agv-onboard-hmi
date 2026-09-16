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

public sealed class WireToGateSlotOperationExecutor : IAsyncDisposable
{
    private readonly IIoModuleClient _ioModule;
    private readonly IWireToGateJournal _journal;
    private readonly IClock _clock;
    private readonly WireToGateSlotOperationExecutorOptions _options;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public WireToGateSlotOperationExecutor(
        IIoModuleClient ioModule,
        IWireToGateJournal journal,
        IClock clock,
        WireToGateSlotOperationExecutorOptions options)
    {
        _ioModule = ioModule;
        _journal = journal;
        _clock = clock;
        _options = options;
        ValidateOptions(options);
    }

    public async Task<WireToGateOperationExecutionResult> ExecuteAsync(
        WireToGateSlotOperationCommand command,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, CancellationToken, Task>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteExclusiveAsync(command, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Resumes the persisted operation context after an authenticated
    /// RESUME_AFTER_REPAIR action. The resume command can only select the exact
    /// persisted attempt/checkpoint/slot set; it cannot manufacture a new one.
    /// Slots already observed in their desired final state are recorded without
    /// another unlock pulse.
    /// </summary>
    public async Task<WireToGateOperationExecutionResult> ResumeAsync(
        WireToGateSlotOperationResumeCommand resume,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, CancellationToken, Task>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resume);
        ValidateResumeCommand(resume);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WireToGateRecoveryState state = await _journal
                .ReadRecoveryStateAsync(cancellationToken)
                .ConfigureAwait(false);
            WireToGateRecoveryOperationContext context = state.OperationContext
                ?? throw new InvalidDataException("RECOVERY_OPERATION_CONTEXT_MISSING");
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
                throw new InvalidDataException("RECOVERY_STATE_MISMATCH");
            }

            WireToGateSlotOperationCommand command = context.ToCommand();
            ValidateCommand(command);
            return await ResumeExclusiveAsync(
                command,
                state,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
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
        WireToGateRecoveryState state = await _journal.ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(state.UnsettledSlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("SLOT_OPERATION_CONFLICT");
        }

        await _journal.WriteRecoveryStateAsync(
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
            },
            cancellationToken).ConfigureAwait(false);
    }

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
        if (!string.Equals(
                state.UnsettledSlotOperationAttemptId,
                context.SlotOperationAttemptId,
                StringComparison.Ordinal)
            || state.RecoveryVector is not null)
        {
            throw new InvalidDataException("RECOVERY_STATE_MISMATCH");
        }

        WireToGateSlotOperationCommand command = context.ToCommand();
        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        bool fresh = snapshot.IsConnected
            && SafetyRules.IsSnapshotFresh(snapshot, _clock.Now, _options.IoSnapshotMaxAge);
        List<int> completed = [];
        List<int> stillActive = [];
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
                    stillActive.Add(physicalSlot);
                }
            }
        }

        bool allCompleted = completed.Count == command.Slots.Count;
        WireToGateRecoveryCheckpoint checkpoint = stillActive.Count == 0
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
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset started = _clock.Now;
        IoSnapshot initial = _ioModule.CurrentSnapshot;
        string? precheckFailure = ValidateBeforeOperation(initial, command, command.Slots);
        if (precheckFailure is not null)
        {
            return CreateRejectedResult(command, precheckFailure, started);
        }

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
            WireToGateRecoveryState.Empty,
            cancellationToken).ConfigureAwait(false);
        await SendProgressAsync(progress, "PREPARING", [], [], cancellationToken).ConfigureAwait(false);

        return await ExecuteRemainingSlotsAsync(
            command,
            context,
            WireToGateRecoveryState.Empty,
            completed,
            results,
            started,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WireToGateOperationExecutionResult> ResumeExclusiveAsync(
        WireToGateSlotOperationCommand command,
        WireToGateRecoveryState state,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset started = _clock.Now;
        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        if (!snapshot.IsConnected
            || !SafetyRules.IsSnapshotFresh(snapshot, _clock.Now, _options.IoSnapshotMaxAge))
        {
            throw new InvalidDataException("SLOT_STATE_UNKNOWN");
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
            throw new InvalidDataException(precheckFailure);
        }

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
        await SendProgressAsync(progress, "PREPARING", [], completed, cancellationToken).ConfigureAwait(false);

        return await ExecuteRemainingSlotsAsync(
            command,
            context,
            state,
            completed,
            results,
            started,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WireToGateOperationExecutionResult> ExecuteRemainingSlotsAsync(
        WireToGateSlotOperationCommand command,
        WireToGateRecoveryOperationContext context,
        WireToGateRecoveryState existingState,
        List<int> completed,
        List<WireToGateSlotExecutionResult> results,
        DateTimeOffset started,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = started + _options.OperationTimeout;
        foreach (int physicalSlot in command.Slots)
        {
            if (completed.Contains(physicalSlot))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            int slotIndex = physicalSlot - 1;
            await WriteRecoveryStateAsync(
                context,
                WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                [physicalSlot],
                completed,
                results,
                existingState,
                cancellationToken).ConfigureAwait(false);
            await SendProgressAsync(progress, "UNLOCKING", [physicalSlot], completed, cancellationToken)
                .ConfigureAwait(false);

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
                LockerSnapshot completedLocker = await _ioModule.WaitForLockerAsync(
                    slotIndex,
                    locker => locker.IsKnown
                        && locker.IsLocked
                        && locker.HasCargo == command.ExpectedOccupied,
                    MinTimeout(_options.OperationTimeout, GetRemaining(deadline)),
                    _options.FeedbackStableWindow,
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
                await SendProgressAsync(progress, "VERIFYING", [], completed, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException)
            {
                LockerSnapshot latest = TryGetLocker(_ioModule.CurrentSnapshot, slotIndex);
                string reason = MapFailureReason(exception);
                UpsertResult(results, CreateSlotResult(latest, "UNKNOWN", [reason]));
                foreach (int notStarted in command.Slots.Where(slot => !completed.Contains(slot)))
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

                IoSnapshot failureSnapshot = _ioModule.CurrentSnapshot;
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
                        safeFinish ? "SAFE_FINISH" : "PAUSED",
                        failureActiveSlots,
                        completed,
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
        await SendProgressAsync(progress, "SAFE_FINISH", [], completed, cancellationToken).ConfigureAwait(false);
        return CreateResult(command, "COMPLETED", results, WireToGateRecoveryCheckpoint.SafeFinishReached);
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
                PendingLoadCancellation = existingState.PendingLoadCancellation
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

    private WireToGateOperationExecutionResult CreateRejectedResult(
        WireToGateSlotOperationCommand command,
        string reason,
        DateTimeOffset observedAt)
    {
        IReadOnlyList<WireToGateSlotExecutionResult> results = command.Slots
            .Select(slot => new WireToGateSlotExecutionResult(
                slot,
                "NOT_STARTED",
                "UNKNOWN",
                "UNKNOWN",
                "UNKNOWN",
                [reason]))
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
            throw new TimeoutException("WIRE_TO_GATE操作超时。");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private TimeSpan GetRemaining(DateTimeOffset deadline) => deadline - _clock.Now;

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

    private static TimeSpan MinTimeout(TimeSpan configured, TimeSpan remaining) =>
        remaining <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : configured < remaining ? configured : remaining;

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
            || options.FeedbackStableWindow < TimeSpan.Zero
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
