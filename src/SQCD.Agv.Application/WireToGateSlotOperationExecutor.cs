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
    private readonly Func<bool> _fatalFaultLatched;
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
    /// <param name="fatalFaultLatched">
    /// Asked immediately before every pulse, the first of a slot and each reopen: while a severe safety
    /// fault is latched this executor opens no door (8005-agv-onboard-hmi#191, which also settles #84).
    /// The latch lives on the onboard controller, which this executor does not reference, so it is
    /// handed in as a question, the way <paramref name="reopenPermitted"/> is. Omitted, nothing is ever
    /// latched -- only tests that do not exercise the latch omit it.
    /// </param>
    public WireToGateSlotOperationExecutor(
        IIoModuleClient ioModule,
        IWireToGateJournal journal,
        IClock clock,
        WireToGateSlotOperationExecutorOptions options,
        Func<bool> reopenPermitted,
        Func<bool>? fatalFaultLatched = null)
    {
        _ioModule = ioModule;
        _journal = journal;
        _clock = clock;
        _options = options;
        _reopenPermitted = reopenPermitted ?? throw new ArgumentNullException(nameof(reopenPermitted));
        _fatalFaultLatched = fatalFaultLatched ?? (() => false);
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

    /// <summary>
    /// True while this executor holds its own serialisation gate — that is, while a slot operation
    /// is actually running here (8005-agv-onboard-hmi#171).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Derived from the gate itself, never from a field kept alongside it.</b> A parallel "busy"
    /// flag drifts, and the way it drifts is that it reads "nothing running" while this executor is
    /// between <c>SendProgressAsync(UNLOCKING)</c> and <c>PulseUnlockAsync</c> — which is exactly the
    /// moment the fatal-fault clearance must not be allowed through.
    /// </para>
    /// <para>
    /// It reads true for the instant <see cref="AbortOperationAsync"/> takes the gate as a barrier.
    /// That direction is the safe one: a clearance refused a moment too often costs a second press,
    /// one allowed a moment too early opens a door beside the person who was just told to proceed.
    /// </para>
    /// </remarks>
    public bool HasOperationInFlight => _operationGate.CurrentCount == 0;

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

        // A resume is a new decision to open doors, so a latch refuses it whole, before anything is
        // written: the server closes the resume on the rejection and the operator asks again once the
        // latch is lifted (8005-agv-onboard-hmi#191). The check in front of each pulse still stands for a
        // latch that arrives after this one.
        if (_fatalFaultLatched())
        {
            throw new WireToGateResumeNotStartedException("FATAL_FAULT_LATCHED");
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
        if (!string.Equals(pending.BusinessId, slotOperationAttemptId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("SLOT_OPERATION_CONFLICT");
        }

        // Read, checked and written as one step under the journal's lock, as MarkResultRecordedAsync
        // does. A late acknowledgement can record this very attempt between the read and the write;
        // with them apart, the write puts the settled attempt back as unsettled and brings its pending
        // results back with it, and the next session reports a result for an operation the server has
        // already closed (onboard-hmi#123 review A, onboard-hmi#136 point 1).
        WireToGateRecoveryState? written = await _journal.UpdateRecoveryStateAsync(
            state => string.Equals(
                state.UnsettledSlotOperationAttemptId,
                slotOperationAttemptId,
                StringComparison.Ordinal)
                ? state with
                {
                    PendingResults = state.PendingResults
                        .Where(item =>
                            string.Equals(item.BusinessId, slotOperationAttemptId, StringComparison.Ordinal)
                            && !string.Equals(item.MessageId, pending.MessageId, StringComparison.Ordinal))
                        .Append(pending)
                        .ToArray()
                }
                : null,
            cancellationToken).ConfigureAwait(false);
        if (written is null)
        {
            throw new InvalidDataException("SLOT_OPERATION_CONFLICT");
        }
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
    /// Whether the journaled attempt opened <paramref name="physicalSlot"/>: the one definition a resume and the
    /// interrupted settlement both judge by (8005-agv-onboard-hmi#186).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three ways in. The active set holds the slot whose door may be open now; the completed set, the slots
    /// this attempt brought to their final state. The third is the slot that failed mid-execution and still
    /// reached a safe finish: <see cref="ExecuteRemainingSlotsAsync"/> journals it with an empty active set,
    /// outside the completed ones, and with an UNKNOWN result.
    /// </para>
    /// <para>
    /// What the third clause relies on is narrower than "NOT_STARTED means never pulsed", which does not hold:
    /// a resume's failure branch writes NOT_STARTED over every unfinished slot, including one an earlier run
    /// opened (review of PR #190, point 4). It relies on the other direction being safe. A result other than
    /// NOT_STARTED is written only for a slot some run of this attempt pulsed, or, in a resume, one refused by the
    /// one-door check before that run's pulse, which may never have been pulsed at all and is counted as opened
    /// on purpose: the journal cannot tell, and counting it keeps it UNKNOWN in a settlement. In the first run a
    /// slot refused before its first pulse is written NOT_STARTED, at the safe finish and outside the active set,
    /// whatever the snapshot read -- so the first clause does not count it either; with the IO unreadable it once
    /// went into the active set (second review of PR #190). The active set can still hold a slot not yet pulsed
    /// when a process dies between that checkpoint and the pulse; it counts as opened, the conservative side.
    /// Every slot of an occupancy conflict
    /// (8005-agv-onboard-hmi#172) is NOT_STARTED too, the conflict slot included, which reads final and was never
    /// opened; that is what keeps the third clause from counting cargo nobody loaded under this attempt.
    /// </para>
    /// <para>
    /// Until #186 the settlement used the first two clauses only, and a restart between the failure's journal
    /// write and the result reaching the outbox reported the failed slot NOT_STARTED -- the report decision 6
    /// of ADR-cross-0058 reserves for a slot never opened. Opened is not completed: the settlement still keeps
    /// that slot UNKNOWN whatever it reads (see <see cref="SettleInterruptedExclusiveAsync"/>), and only a
    /// resume an administrator authorized counts it.
    /// </para>
    /// </remarks>
    private static bool OpenedByThisAttempt(WireToGateRecoveryState state, int physicalSlot) =>
        state.ActiveUnlockSlots.Contains(physicalSlot)
        || state.CompletedSlots.Contains(physicalSlot)
        || state.SlotResults.Any(result =>
            result.SlotNo == physicalSlot
            && !string.Equals(result.Outcome, "NOT_STARTED", StringComparison.Ordinal));

    /// <summary>
    /// The reason codes the journal holds for <paramref name="physicalSlot"/>'s last result, or none.
    /// </summary>
    private static IReadOnlyList<string> JournaledReasonCodes(WireToGateRecoveryState state, int physicalSlot) =>
        state.SlotResults.LastOrDefault(result => result.SlotNo == physicalSlot)?.ReasonCodes ?? [];

    /// <summary>
    /// The reason codes of <paramref name="physicalSlot"/>'s journaled UNKNOWN result -- the slot this attempt
    /// failed on -- or null when the journal holds no failure for it.
    /// </summary>
    private static IReadOnlyList<string>? JournaledFailure(WireToGateRecoveryState state, int physicalSlot) =>
        state.SlotResults.LastOrDefault(result => result.SlotNo == physicalSlot) is { Outcome: "UNKNOWN" } failed
            ? failed.ReasonCodes
            : null;

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
    /// UNKNOWN, which the server turns into RecoveryRequired. A slot the journal holds as failed is
    /// never counted toward that COMPLETED, whatever it reads now: it failed on a hardware condition,
    /// and the same ADR keeps such an operation blocked until a person confirms it
    /// (8005-agv-onboard-hmi#186).
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
            if (!OpenedByThisAttempt(state, physicalSlot))
            {
                // Never opened: the door is shut and the lock closed, so report what is read (decision 6).
                // The reason is the one the journal holds for this slot, which is empty unless this slot
                // is what stopped the attempt -- an occupancy conflict journaled at the safe finish, whose
                // process exited before the refusal was sent (8005-agv-onboard-hmi#172 review). Without it
                // the settlement reported a refusal with no reason at all.
                UpsertResult(
                    results,
                    CreateSlotResult(locker, "NOT_STARTED", JournaledReasonCodes(state, physicalSlot)));
            }
            else if (JournaledFailure(state, physicalSlot) is null && IsFinalState(locker, command.ExpectedOccupied))
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
                //
                // A slot the journal holds as failed stays UNKNOWN whatever it reads now, under the reason it
                // failed with (8005-agv-onboard-hmi#186). It failed on one of ADR-cross-0058 decision 2's
                // conditions -- unreadable, lock feedback not valid, unlock output not reset -- and ADR-cross-0017
                // keeps an operation that went into recovery over such a fault blocked until a person confirms
                // it: a reading that looks final after a restart is not that confirmation. Counted COMPLETED
                // here, the server would commit it and nobody would look at the lock. A resume counts it, because
                // an administrator authorized that resume. The journaled reason is kept even when the IO is not
                // fresh now: the physical fields then read UNKNOWN, and the reason says why the slot is in doubt
                // at all, which the missing reading does not change.
                //
                // Also kept when the journaled UNKNOWN is a previous settlement's own conclusion rather than an
                // execution failure -- a process that died again before that result reached the outbox. The
                // journal then says the operation's state is UNKNOWN, and ADR-cross-0017 keeps it blocked for a
                // person; the two cannot be told apart by shape or reason code (review of PR #190, point 3).
                UpsertResult(
                    results,
                    CreateSlotResult(
                        locker,
                        "UNKNOWN",
                        JournaledFailure(state, physicalSlot)
                            ?? [fresh ? "RECOVERY_CHECKPOINT_NOT_UNIQUE" : "SLOT_STATE_UNKNOWN"]));
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
        await WriteCheckpointAsync(
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

        // A new operation starts from a clean journal, except for the device facts that outlive
        // every operation.
        WireToGateRecoveryState fresh = WireToGateRecoveryState.Empty with
        {
            ForcedIsolation = journaled.ForcedIsolation
        };
        WireToGateRecoveryOperationContext context =
            WireToGateRecoveryOperationContext.FromCommand(command);

        string? precheckFailure = ValidateBeforeOperation(initial, command, command.Slots);
        if (precheckFailure == "SLOT_OPERATION_CONFLICT")
        {
            // Every target is known, locked and reset -- ValidateBeforeOperation checks that of all of
            // them before it looks at occupancy, so a conflict is only ever reported over safe slots --
            // and one of them already holds what the command expects to find absent (or the reverse).
            // Nothing was opened, yet the demand cannot go on: the server puts it into RecoveryRequired,
            // and the recovery entries have to find this attempt to get the cargo out
            // (8005-agv-onboard-hmi#172). Journaled as the operation's own, at the safe finish, which is
            // the shape every other unsettled FAILED leaves behind; the result itself is the refusal's.
            WireToGateOperationExecutionResult conflict = CreateRejectedResult(command, initial, started);
            await WriteCheckpointAsync(
                context,
                WireToGateRecoveryCheckpoint.SafeFinishReached,
                [],
                [],
                conflict.SlotResults,
                fresh,
                cancellationToken).ConfigureAwait(false);
            return conflict with { JournalCheckpoint = "SAFE_FINISH_REACHED" };
        }

        if (precheckFailure is not null)
        {
            // Some target is not safe to call finished -- unknown, open or its unlock output not
            // reset. No safe finish is claimed and nothing is journaled, as before.
            return CreateRejectedResult(command, initial, started);
        }
        List<int> completed = [];
        List<WireToGateSlotExecutionResult> results = [];
        await WriteCheckpointAsync(
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
            firstRun: true,
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
            // Only a slot this attempt opened can have been brought to its final state by it. One never
            // opened that already reads final was that way before the command came: for a load, cargo
            // nobody put there under this attempt. Calling it COMPLETED reported a load that never
            // happened (8005-agv-onboard-hmi#172); it stays in the remaining set, where the precheck
            // below refuses it as SLOT_OPERATION_CONFLICT.
            //
            // The slot that failed mid-execution and still reached a safe finish counts here once it reads
            // final: an administrator authorized this resume, which the interrupted settlement does not have
            // and why it keeps that slot UNKNOWN (8005-agv-onboard-hmi#186).
            if (OpenedByThisAttempt(state, physicalSlot) && IsFinalState(locker, command.ExpectedOccupied))
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
        await WriteCheckpointAsync(
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
            firstRun: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WireToGateOperationExecutionResult> ExecuteRemainingSlotsAsync(
        WireToGateSlotOperationCommand command,
        WireToGateRecoveryOperationContext context,
        WireToGateRecoveryState existingState,
        List<int> completed,
        List<WireToGateSlotExecutionResult> results,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        bool firstRun,
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
            await WriteCheckpointAsync(
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
                await WriteCheckpointAsync(
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
            catch (FatalFaultLatchedException latched)
            {
                return await SettleRefusedByLatchAsync(
                    command,
                    context,
                    existingState,
                    completed,
                    results,
                    progress,
                    physicalSlot,
                    neverOpened: !latched.Reopen && firstRun).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException
                    or TimeoutException
                    or InvalidDataException
                    or RefusedBeforeFirstPulseException)
            {
                // Only the three conditions of ADR-cross-0058 decision 2 reach this branch: the slot
                // state cannot be read, the lock feedback is not valid, or the unlock output cannot be
                // confirmed reset -- of this slot, or, before a pulse, of another door (REQ-0357). An
                // operator who has not loaded, unloaded or shut the door is still inside
                // DriveSlotToTargetStateAsync. One snapshot serves the failed slot and the slots never
                // started, so the two cannot disagree about what was read.
                IoSnapshot failureSnapshot = _ioModule.CurrentSnapshot;
                string reason = MapFailureReason(exception);
                // Refused by another door before this attempt ever pulsed this slot: it was never opened,
                // so it is NOT_STARTED (decision 6), under the reason that stopped the attempt, which is
                // this slot's to carry (8005-agv-onboard-hmi#186, review of PR #190). Known for certain
                // only in the first run, where this process has driven every slot it has touched. A
                // resume re-drives slots an earlier run may have pulsed, and the journal cannot say which
                // (the loop below overwrites them as NOT_STARTED), so a refusal there stays UNKNOWN.
                bool neverOpened = exception is RefusedBeforeFirstPulseException && firstRun;
                string outcome = neverOpened ? "NOT_STARTED" : "UNKNOWN";
                UpsertResult(
                    results,
                    CreateSlotResult(ReadLocker(failureSnapshot, slotIndex), outcome, [reason]));
                foreach (int notStarted in command.Slots
                    .Where(slot => slot != physicalSlot && !completed.Contains(slot)))
                {
                    // Decision 6: a slot never opened has nothing uncertain about it. Its three
                    // fields are what the IO reads, and reasonCodes stay empty -- why the operation
                    // stopped belongs to the slot that stopped it, not to this one. In a resume this
                    // also covers a slot an earlier run opened and left unfinished: NOT_STARTED here
                    // says "not reached by this run", not "never pulsed by this attempt" (review of
                    // PR #190, point 4; so since #172).
                    UpsertResult(
                        results,
                        CreateSlotResult(ReadLocker(failureSnapshot, notStarted - 1), "NOT_STARTED", []));
                }

                // A slot this attempt never opened cannot be standing open because of it, whatever the snapshot
                // says: with the IO unreadable the one-door check refuses with SLOT_STATE_UNKNOWN, and reading the
                // safe finish off that same snapshot put a never-opened slot into the active set -- NOT_STARTED in
                // the result, a door that may be open in the journal, and opened again for a settlement or a
                // resume (second review of PR #190). The other door's state is not this attempt's to hold.
                bool safeFinish = neverOpened || IsSafeFinish(failureSnapshot, slotIndex);
                WireToGateRecoveryCheckpoint failureCheckpoint = safeFinish
                    ? WireToGateRecoveryCheckpoint.SafeFinishReached
                    : WireToGateRecoveryCheckpoint.ActiveUnlockSet;
                IReadOnlyList<int> failureActiveSlots = safeFinish ? [] : [physicalSlot];
                await WriteCheckpointAsync(
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

        await WriteCheckpointAsync(
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
    /// Ends the operation at a pulse the fatal-fault latch refused (8005-agv-onboard-hmi#191). The
    /// operation stops, it is not held: <c>ClearFatalFaultAsync</c> refuses while an operation is in
    /// flight, so an executor waiting here for the latch to lift would wait for ever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>FAILED, not UNKNOWN.</b> The door is shut and locked with the output reset -- a first pulse is
    /// only reached over a slot <see cref="ValidateBeforeOperation"/> found so, and a reopen only after
    /// the door was read shut -- so nothing about the slot is in doubt, and ADR-cross-0058 decision 2
    /// keeps UNKNOWN for readings that cannot be trusted. ADR-cross-0016 calls a refusal on a safety
    /// condition an execution failure. The server sends every result short of COMPLETED to
    /// RecoveryRequired except a FAILED carrying <c>OPERATOR_TIMEOUT</c> (<c>DeterminateLoadFailure</c>),
    /// which this never carries.
    /// </para>
    /// <para>
    /// The refused slot is <c>NOT_STARTED</c> when this run never pulsed it, otherwise <c>FAILED</c>; it
    /// carries the reason, the slots after it are <c>NOT_STARTED</c> with none (decision 6). The journal
    /// ends at the safe finish with nothing active, which keeps the attempt unsettled and resumable by
    /// <c>RESUME_AFTER_REPAIR</c> once the latch is lifted.
    /// </para>
    /// </remarks>
    private async Task<WireToGateOperationExecutionResult> SettleRefusedByLatchAsync(
        WireToGateSlotOperationCommand command,
        WireToGateRecoveryOperationContext context,
        WireToGateRecoveryState existingState,
        List<int> completed,
        List<WireToGateSlotExecutionResult> results,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        int physicalSlot,
        bool neverOpened)
    {
        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        UpsertResult(
            results,
            CreateSlotResult(
                ReadLocker(snapshot, physicalSlot - 1),
                neverOpened ? "NOT_STARTED" : "FAILED",
                [FatalFaultLatchedReason]));
        foreach (int notStarted in command.Slots
            .Where(slot => slot != physicalSlot && !completed.Contains(slot)))
        {
            UpsertResult(
                results,
                CreateSlotResult(ReadLocker(snapshot, notStarted - 1), "NOT_STARTED", []));
        }

        bool safeFinish = neverOpened || IsSafeFinish(snapshot, physicalSlot - 1);
        WireToGateRecoveryCheckpoint checkpoint = safeFinish
            ? WireToGateRecoveryCheckpoint.SafeFinishReached
            : WireToGateRecoveryCheckpoint.ActiveUnlockSet;
        IReadOnlyList<int> active = safeFinish ? [] : [physicalSlot];
        await WriteCheckpointAsync(
            context,
            checkpoint,
            active,
            completed,
            results,
            existingState,
            CancellationToken.None).ConfigureAwait(false);
        await SendProgressAsync(
                progress,
                new(safeFinish ? "SAFE_FINISH" : "PAUSED", active, completed),
                CancellationToken.None)
            .ConfigureAwait(false);
        return CreateResult(command, "FAILED", results, checkpoint);
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
        // Whether this call has asked for a pulse on this slot, read only by the one-door check before a reopen
        // in this same call. A pulse whose write throws ends the call as an IOException, reported UNKNOWN, so
        // where exactly the flag is set around the pulse does not change what is reported.
        bool pulseRequested = false;
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
                    // Before the first pulse the slot has not been opened by this call; on a reopen it has.
                    throw pulseRequested
                        ? new InvalidDataException(otherDoor)
                        : new RefusedBeforeFirstPulseException(otherDoor);
                }

                await SendProgressAsync(
                    progress,
                    new("UNLOCKING", [physicalSlot], completed, promptRound, cause),
                    cancellationToken).ConfigureAwait(false);
                // After the progress send, not before it: that send waits on the network, and a latch
                // arriving during it is exactly the window this check exists for. Nothing is awaited
                // between here and the pulse.
                ThrowIfFatalFaultLatched(reopen: pulseRequested);
                pulseRequested = true;
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

    private async Task WriteCheckpointAsync(
        WireToGateRecoveryOperationContext context,
        WireToGateRecoveryCheckpoint checkpoint,
        IReadOnlyList<int> activeSlots,
        IReadOnlyList<int> completedSlots,
        IReadOnlyList<WireToGateSlotExecutionResult> results,
        WireToGateRecoveryState existingState,
        CancellationToken cancellationToken)
    {
        // Merged under the journal's lock against the newest state, not against the copy this run read
        // when it started. This checkpoint owns six fields -- the attempt, the checkpoint itself, the
        // active unlock set, the operation context, the completed slots and the slot results -- and
        // every other field is taken as the journal holds it now. Written from the older copy instead,
        // a checkpoint silently undid whatever landed since: a recovery session released by a CLOSED
        // snapshot came back, a forced recovery generation went back down, a pending result recorded
        // in between disappeared (onboard-hmi#136 point 2). Only PendingLoadCancellation was read
        // afresh, and only because onboard-hmi#78 was bitten by it; that rule is kept below.
        await _journal.UpdateRecoveryStateAsync(
            current => new WireToGateRecoveryState(
                context.SlotOperationAttemptId,
                checkpoint,
                activeSlots.Order().ToArray(),
                current.ForcedRecoveryGeneration,
                current.PendingResults)
            {
                OperationContext = context,
                CompletedSlots = completedSlots.Distinct().Order().ToArray(),
                SlotResults = results
                    .GroupBy(result => result.SlotNo)
                    .Select(group => group.Last())
                    .OrderBy(result => result.SlotNo)
                    .ToArray(),
                ExceptionRecoverySessionId = current.ExceptionRecoverySessionId,
                RecoveryActionId = current.RecoveryActionId,
                RecoverySessionRequestId = current.RecoverySessionRequestId,
                RecoveryActionRequestId = current.RecoveryActionRequestId,
                RecoveryReason = current.RecoveryReason,
                RecoveryOperatorId = current.RecoveryOperatorId,
                RecoveryOperatorVerifiedAt = current.RecoveryOperatorVerifiedAt,
                RecoveryVector = current.RecoveryVector,
                RecoveryResultObservedAt = current.RecoveryResultObservedAt,
                LastCompletedLoadOperationContext = current.LastCompletedLoadOperationContext,
                // The operator's unanswered load cancellation for this attempt stays; one left over
                // from another attempt goes, as it always did (onboard-hmi#78).
                PendingLoadCancellation = string.Equals(
                    current.PendingLoadCancellation?.SlotOperationAttemptId,
                    context.SlotOperationAttemptId,
                    StringComparison.Ordinal)
                    ? current.PendingLoadCancellation
                    : existingState.PendingLoadCancellation,
                ForcedIsolation = current.ForcedIsolation
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

        // Two passes, safety of every slot first and occupancy second. The executor journals a
        // SLOT_OPERATION_CONFLICT as a safe finish (8005-agv-onboard-hmi#172), which is only true when
        // no slot has a safety problem; one pass stopping at the first problem reported a conflict on
        // slot 1 over an unreset unlock output on slot 2.
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
        }

        foreach (int physicalSlot in physicalSlots)
        {
            LockerSnapshot locker = snapshot.GetLocker(physicalSlot - 1);
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

    /// <summary>
    /// Another door was not proven shut before the first pulse of a slot (REQ-0357): the slot was not
    /// opened by the call that threw it. The message is the reason code, as for the other door checks.
    /// </summary>
    private sealed class RefusedBeforeFirstPulseException(string reasonCode) : Exception(reasonCode);

    /// <summary>
    /// The code a latch refusal carries on the wire. Protocol v2.0.0 has no code for "the onboard latched
    /// a severe safety fault"; <c>VEHICLE_NOT_READY</c> is the registered one the vehicle already uses for
    /// refusing door IO on its own state (onboard-hmi#123). A dedicated code is on the v3.0.0 list
    /// (program#115).
    /// </summary>
    public const string FatalFaultLatchedReason = "VEHICLE_NOT_READY";

    /// <summary>
    /// The latch refused the pulse about to be sent. <see cref="Reopen"/> says whether this call had
    /// already pulsed the slot.
    /// </summary>
    private sealed class FatalFaultLatchedException(bool reopen) : Exception("FATAL_FAULT_LATCHED")
    {
        public bool Reopen { get; } = reopen;
    }

    /// <summary>
    /// The latch check in front of every pulse (8005-agv-onboard-hmi#191). Named as the controller's own
    /// is, because <c>FatalFaultScopeArchitectureTests</c> looks for this name within a few lines above
    /// each registered-as-guarded pulse.
    /// </summary>
    private void ThrowIfFatalFaultLatched(bool reopen)
    {
        if (_fatalFaultLatched())
        {
            throw new FatalFaultLatchedException(reopen);
        }
    }

    private static string MapFailureReason(Exception exception) => exception switch
    {
        RefusedBeforeFirstPulseException refused => refused.Message,
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
