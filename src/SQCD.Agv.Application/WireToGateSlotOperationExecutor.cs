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

    /// <summary>The operator pressed 「重新开门」 over a door shut on the opposite occupancy, and the slot is opened again.</summary>
    OppositeReopen,

    /// <summary>OperationTimeout passed with the door still open; only the prompt repeats.</summary>
    PromptCadence,

    /// <summary>
    /// The slot is to be reopened, but the vehicle safety fact does not allow an unlock pulse (an
    /// emergency stop, or a safety projection that cannot be trusted). Nothing is pulsed until it does.
    /// </summary>
    ReopenHeldBySafety,

    /// <summary>
    /// The door was shut over the opposite occupancy -- a load with nothing put in, an unload with the basket left in.
    /// The slot stays shut and locked, and nothing is pulsed until the operator presses 「重新开门」
    /// (<see cref="WireToGateOperatorSlotRequest.Reopen"/>) or a load is cancelled (8005-agv-onboard-hmi#385, CP-0019).
    /// </summary>
    ShutOverOpposite,

    /// <summary>
    /// A pressed reopen's lock did not release, or released and fell back inside the stable window, while the slot read
    /// shut, locked, its output reset and its occupancy known: the door was held or shut again as it opened. Back to
    /// waiting for the press, as <see cref="ShutOverOpposite"/> (8005-agv-onboard-hmi#385).
    /// </summary>
    ReopenNotReleased,

    /// <summary>
    /// The operator pressed 「剩下的不装」 over a slot held shut for 「重新开门」, and the run holds every slot shut until the
    /// server's <c>RemainingBasketsSkipResult</c> decides it (8005-agv-onboard-hmi#388, CP-0021).
    /// </summary>
    RemainingBasketsSkipPending
}

/// <summary>
/// What the operator can ask of the slot an executor holds shut over the opposite occupancy
/// (<see cref="WireToGatePromptCause.ShutOverOpposite"/>, <see cref="WireToGatePromptCause.ReopenNotReleased"/>).
/// </summary>
/// <remarks>
/// One channel for every such request, so a later one -- the multi-basket load's 「剩下的不装」
/// (8005-agv-onboard-hmi#388) -- is another value here and another branch where the wait returns, not another channel.
/// </remarks>
public enum WireToGateOperatorSlotRequest
{
    /// <summary>「重新开门」: one more unlock pulse on the same slot of the same operation, with no limit on how many.</summary>
    Reopen,

    /// <summary>
    /// 「剩下的不装」: hold every slot shut until the server decides the skip. Handed over only by
    /// <see cref="WireToGateSlotOperationExecutor.HoldForRemainingBasketsSkip"/>, which checks what the skip needs first; a
    /// <see cref="WireToGateSlotOperationExecutor.SubmitOperatorRequest"/> of this value is refused (8005-agv-onboard-hmi#388).
    /// </summary>
    RemainingBasketsSkip,

    /// <summary>
    /// 「不装了」 in a resume over empty slots: the run ends with what the slots read, opening nothing. Handed over only by
    /// <see cref="WireToGateSlotOperationExecutor.AbandonResume"/>, which checks what it needs first; a
    /// <see cref="WireToGateSlotOperationExecutor.SubmitOperatorRequest"/> of this value is refused (8005-agv-onboard-hmi#410).
    /// </summary>
    AbandonResume
}

/// <summary>
/// The slot a running operation is holding shut for an operator request, and why; see
/// <see cref="WireToGateSlotOperationExecutor.OperatorRequestWait"/>.
/// </summary>
public sealed record WireToGateOperatorRequestWait(
    string SlotOperationAttemptId,
    int PhysicalSlot,
    WireToGatePromptCause Cause);

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

/// <summary>
/// What <see cref="WireToGateSlotOperationExecutor.DeclareSlotFaultAsync"/> did with a slot fault declaration.
/// </summary>
public enum WireToGateSlotFaultDeclarationOutcome
{
    /// <summary>
    /// Journaled and applied: no unlock pulse of the attempt follows, and the run ends in an UNKNOWN result
    /// its own caller reports.
    /// </summary>
    Applied,

    /// <summary>The declared attempt is not the one running here -- none is, or another one is.</summary>
    AttemptNotExecuting,

    /// <summary>
    /// The attempt is running, but the declared slot is not the one it is waiting on the operator for: the
    /// slot has closed its loop, ended UNKNOWN, or the run is at another slot.
    /// </summary>
    SlotNotAwaiting,

    /// <summary>Another declaration has already been applied to this attempt.</summary>
    AlreadyDeclared,

    /// <summary>
    /// A load cancellation over this attempt is waiting for its answer or already running: the attempt's conclusion
    /// is that cancellation's.
    /// </summary>
    TakenOverByLoadCancellation
}

/// <summary>What <see cref="WireToGateSlotOperationExecutor.MarkResultRecordedAsync"/> did with the journal.</summary>
public enum WireToGateResultRecording
{
    /// <summary>The attempt is settled: the journal records its result and holds nothing of it any more.</summary>
    Settled,

    /// <summary>
    /// A recovery vector prepared over the same attempt holds the journal entry; only the result's own share was
    /// closed, and the attempt settles with the vector (8005-agv-onboard-hmi#259).
    /// </summary>
    TakenOverByRecoveryVector
}

public sealed class WireToGateSlotOperationExecutor : IAsyncDisposable
{
    /// <summary>
    /// The code a slot fault declaration puts in the declared slot's <c>reasonCodes</c> (REQ-0359, ADR-cross-0062):
    /// the UNKNOWN says by itself that a person declared it, not that a reading failed.
    /// </summary>
    public const string SlotFaultDeclaredReason = "SLOT_FAULT_DECLARED";

    private readonly IIoModuleClient _ioModule;
    private readonly IWireToGateJournal _journal;
    private readonly IClock _clock;
    private static readonly TimeSpan ReopenPermissionPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly WireToGateSlotOperationExecutorOptions _options;
    private readonly Func<bool> _reopenPermitted;
    private readonly Func<bool> _fatalFaultLatched;
    private readonly Action<WireToGateJournalOverwrite> _journalOverwritten;

    /// <summary>
    /// The outbox key of the result the vector on file produced itself, not given up, or <c>null</c> when it has none
    /// (8005-agv-onboard-hmi#267). The outbox keys and the tie between a result and one preparation of its vector are the
    /// business service's; without it every vector on file counts as having no result.
    /// </summary>
    private readonly Func<WireToGateRecoveryVectorContext, WireToGateRecoveryState, CancellationToken, Task<string?>>
        _vectorResultKeyOnFile;

    /// <summary>The journey on screen, read when a result is recorded (8005-agv-onboard-hmi#352 review L1).</summary>
    private readonly Func<WireToGateJourneySnapshot> _currentJourney;

    private readonly SemaphoreSlim _operationGate = new(1, 1);

    /// <summary>
    /// Serialises a slot fault declaration against the three moments of a run it must not interleave with: an
    /// unlock pulse, a slot closing its loop, and a slot ending in a failure (8005-agv-onboard-hmi#215). Whichever
    /// takes it first decides, so a declaration is never applied to a slot already counted COMPLETED, and no
    /// pulse follows one that was applied.
    /// </summary>
    private readonly SemaphoreSlim _declarationGate = new(1, 1);
    private ActiveOperation? _activeOperation;

    /// <param name="reopenPermitted">
    /// Asked before every reopen pulse. Nothing on this vehicle reads the emergency stop
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
        Func<bool>? fatalFaultLatched = null,
        Action<WireToGateJournalOverwrite>? journalOverwritten = null,
        Func<WireToGateRecoveryVectorContext, WireToGateRecoveryState, CancellationToken, Task<string?>>?
            vectorResultKeyOnFile = null,
        Func<WireToGateJourneySnapshot>? currentJourney = null)
    {
        _ioModule = ioModule;
        _journal = journal;
        _clock = clock;
        _options = options;
        _reopenPermitted = reopenPermitted ?? throw new ArgumentNullException(nameof(reopenPermitted));
        _fatalFaultLatched = fatalFaultLatched ?? (() => false);
        _journalOverwritten = journalOverwritten ?? (_ => { });
        _vectorResultKeyOnFile = vectorResultKeyOnFile ?? ((_, _, _) => Task.FromResult<string?>(null));
        _currentJourney = currentJourney ?? (() => WireToGateJourneySnapshot.Empty);
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

    /// <summary>
    /// The run <see cref="AbortOperationAsync"/> can stop, and where it stands for a slot fault declaration.
    /// </summary>
    private sealed class ActiveOperation(string slotOperationAttemptId, CancellationTokenSource abort)
    {
        public string SlotOperationAttemptId { get; } = slotOperationAttemptId;

        public CancellationTokenSource Abort { get; } = abort;

        /// <summary>Whether this run is a <c>RESUME_AFTER_REPAIR</c>, the one run 「不装了」 is offered in (8005-agv-onboard-hmi#410).</summary>
        public bool IsResume { get; init; }

        /// <summary>
        /// The slot this run is driving and has not yet closed or failed -- the one a declaration may name.
        /// Read and written only under <see cref="_declarationGate"/>.
        /// </summary>
        public int? AwaitingSlot { get; set; }

        /// <summary>The slot a declaration was applied to. Written only under <see cref="_declarationGate"/>.</summary>
        public int? DeclaredSlot { get; set; }

        /// <summary>
        /// The slot held shut for an operator request and the request's answer, while the run waits for it; read and
        /// written only under a lock on this object.
        /// </summary>
        public PendingOperatorRequest? OperatorRequest { get; set; }

        /// <summary>
        /// The 「剩下的不装」 the run holds every slot shut for, until its decision; read and written only under a lock on this
        /// object (8005-agv-onboard-hmi#388).
        /// </summary>
        public RemainingBasketsSkipHold? SkipHold { get; set; }
    }

    /// <summary>
    /// A slot held shut for an operator request, and the answer the request completes. <paramref name="Command"/> and
    /// <paramref name="Completed"/> are the run's at the moment it began to wait: what 「剩下的不装」 is judged on.
    /// </summary>
    private sealed record PendingOperatorRequest(
        WireToGateOperatorRequestWait Wait,
        TaskCompletionSource<WireToGateOperatorSlotRequest> Answer,
        WireToGateSlotOperationCommand Command,
        IReadOnlyList<int> Completed);

    /// <summary>
    /// A press of 「剩下的不装」 the run is held for: the request and the slots it named, and the decision -- the authorized slots,
    /// or <c>null</c> when the request was rejected or dropped.
    /// </summary>
    private sealed record RemainingBasketsSkipHold(
        string SkipRequestId,
        IReadOnlyList<int> LoadedSlots,
        TaskCompletionSource<IReadOnlyList<int>?> Decision);

    /// <summary>
    /// The slot the running operation holds shut over the opposite occupancy, waiting for the operator's
    /// 「重新开门」, or <c>null</c> when it waits for no request (8005-agv-onboard-hmi#385).
    /// </summary>
    public WireToGateOperatorRequestWait? OperatorRequestWait
    {
        get
        {
            ActiveOperation? active = Volatile.Read(ref _activeOperation);
            if (active is null)
            {
                return null;
            }

            lock (active)
            {
                return active.OperatorRequest?.Wait;
            }
        }
    }

    /// <summary>
    /// Hands the operator's <paramref name="request"/> to the run holding <paramref name="physicalSlot"/> of
    /// <paramref name="slotOperationAttemptId"/> shut for one. True when that run took it; false, and nothing
    /// happens, when no run waits for a request on that slot -- including a second press of the same wait, which the
    /// first one has already answered.
    /// </summary>
    /// <remarks>
    /// Only hands it over. Whatever the request leads to -- the safety hold, the one-door check, the latch and the
    /// declaration gate in front of a pulse -- is the run's, as for every other pulse. <see cref="WireToGateOperatorSlotRequest.RemainingBasketsSkip"/>
    /// is refused here: it goes through <see cref="HoldForRemainingBasketsSkip"/>.
    /// </remarks>
    public bool SubmitOperatorRequest(
        string slotOperationAttemptId,
        int physicalSlot,
        WireToGateOperatorSlotRequest request)
    {
        ActiveOperation? active = Volatile.Read(ref _activeOperation);
        if (request is WireToGateOperatorSlotRequest.RemainingBasketsSkip or WireToGateOperatorSlotRequest.AbandonResume
            || active is null
            || !string.Equals(active.SlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal))
        {
            return false;
        }

        lock (active)
        {
            if (active.OperatorRequest is not { } pending || pending.Wait.PhysicalSlot != physicalSlot)
            {
                return false;
            }

            active.OperatorRequest = null;
            return pending.Answer.TrySetResult(request);
        }
    }

    /// <summary>
    /// What 「剩下的不装」 would be asked over now, or <c>null</c> when it is not offered (8005-agv-onboard-hmi#388, CP-0021
    /// REQ-0367): a load of N &gt; 1 baskets held shut for 「重新开门」, with k slots closed safely <c>OCCUPIED</c>,
    /// 1 &lt;= k &lt; N, every target door read locked with its unlock output reset and none unreadable, and no skip held
    /// already.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only while the run waits for 「重新开门」.</b> That is the one wait in which every target door is shut by
    /// construction: the run opens one slot at a time, and the slot it waits on is held shut over the opposite occupancy.
    /// The slot that is held -- opened, shut empty and locked -- counts among the remaining slots, as the 10-21 agv03 @160
    /// shape needs. A door open, a lock that does not read locked, an output not reset, a slot that cannot be read: not
    /// offered, read off the IO as it is now, not off the run's memory.
    /// </para>
    /// <para>
    /// Manual recovery is the business service's to rule out; once an exception recovery session or a slot fault
    /// declaration takes the run, this wait has ended anyway.
    /// </para>
    /// </remarks>
    public WireToGateRemainingBasketsSkipOffer? RemainingBasketsSkipOffer
    {
        get
        {
            ActiveOperation? active = Volatile.Read(ref _activeOperation);
            if (active is null)
            {
                return null;
            }

            PendingOperatorRequest? pending;
            lock (active)
            {
                pending = active.SkipHold is null ? active.OperatorRequest : null;
            }

            return pending is null ? null : SkipOfferOver(pending, _ioModule.CurrentSnapshot);
        }
    }

    /// <summary>The <c>skipRequestId</c> the running operation is held for, or <c>null</c> when none is pending in it.</summary>
    public string? HeldRemainingBasketsSkipRequestId
    {
        get
        {
            ActiveOperation? active = Volatile.Read(ref _activeOperation);
            if (active is null)
            {
                return null;
            }

            lock (active)
            {
                return active.SkipHold?.SkipRequestId;
            }
        }
    }

    /// <summary>
    /// The operator pressed 「剩下的不装」: holds the run -- no slot opens, 「重新开门」 is refused -- until
    /// <see cref="DecideRemainingBasketsSkip"/> decides <paramref name="skipRequestId"/>. True when the run took the hold;
    /// false, and nothing changes, when the skip is not offered now (<see cref="RemainingBasketsSkipOffer"/>), when it is
    /// another attempt's, or when <paramref name="loadedSlots"/> are not exactly the slots the run has loaded.
    /// </summary>
    /// <remarks>
    /// Taken in the same lock that takes a 「重新开门」, so the two presses of one wait cannot both win: the hold answers the
    /// wait, and the reopen then finds nothing to answer. The caller journals the request and sends it after this returns
    /// true, and decides it with <c>null</c> should either fail, so a hold is never left without a request behind it.
    /// </remarks>
    public bool HoldForRemainingBasketsSkip(
        string slotOperationAttemptId,
        string skipRequestId,
        IReadOnlyList<int> loadedSlots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skipRequestId);
        ArgumentNullException.ThrowIfNull(loadedSlots);
        ActiveOperation? active = Volatile.Read(ref _activeOperation);
        if (active is null
            || !string.Equals(active.SlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal))
        {
            return false;
        }

        lock (active)
        {
            if (active.SkipHold is not null
                || active.OperatorRequest is not { } pending
                || SkipOfferOver(pending, _ioModule.CurrentSnapshot) is not { } offer
                || !offer.LoadedSlots.SequenceEqual(loadedSlots.Order()))
            {
                return false;
            }

            active.OperatorRequest = null;
            active.SkipHold = new RemainingBasketsSkipHold(
                skipRequestId,
                offer.LoadedSlots,
                new TaskCompletionSource<IReadOnlyList<int>?>(TaskCreationOptions.RunContinuationsAsynchronously));
            return pending.Answer.TrySetResult(WireToGateOperatorSlotRequest.RemainingBasketsSkip);
        }
    }

    /// <summary>
    /// Decides the skip the run is held for: <paramref name="authorizedSlots"/> are the slots the server's <c>AUTHORIZED</c>
    /// names, and the run ends the load with them; <c>null</c> lets go of the hold -- rejected, or dropped -- and the run goes
    /// back to waiting for 「重新开门」. True when the run held <paramref name="skipRequestId"/> and took the decision; false
    /// when it holds no such request any more -- the operation ended another way, which is the late authorization the wire
    /// notes say is never acted on -- or the authorization names other slots than the request did.
    /// </summary>
    public bool DecideRemainingBasketsSkip(
        string slotOperationAttemptId,
        string skipRequestId,
        IReadOnlyList<int>? authorizedSlots)
    {
        ActiveOperation? active = Volatile.Read(ref _activeOperation);
        if (active is null
            || !string.Equals(active.SlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal))
        {
            return false;
        }

        lock (active)
        {
            // The hold stays published until the run's wait takes it off: a decision that comes before the run has reached
            // that wait -- a server that answers within milliseconds -- is still found there (found by the G2 injection of
            // 8005-agv-onboard-hmi#388). A decided hold takes no second decision.
            if (active.SkipHold is not { } hold
                || hold.Decision.Task.IsCompleted
                || !string.Equals(hold.SkipRequestId, skipRequestId, StringComparison.Ordinal)
                || authorizedSlots is not null && !hold.LoadedSlots.SequenceEqual(authorizedSlots.Order()))
            {
                return false;
            }

            return hold.Decision.TrySetResult(authorizedSlots is null ? null : [.. authorizedSlots.Order()]);
        }
    }

    /// <summary>
    /// The attempt 「不装了」 would end now, or <c>null</c> when it is not offered (8005-agv-onboard-hmi#410): a resumed load held
    /// shut for 「重新开门」, nothing loaded in this run, and every target slot read fresh, known, locked, its unlock output
    /// reset and empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only in a resume.</b> The first run of a load has 「取消装货」, which the server authorizes. A resume runs over a load
    /// whose own result is with the server, which refuses that cancellation and a 「剩下的不装」 alike; without this, a resume
    /// with nothing to load had no way to end but the door reopened on every press (agv01 2026-10-22).
    /// </para>
    /// <para>
    /// <b>Only over empty slots, read off the IO as it is now.</b> Ended here, the load is settled by 「补偿清空」, which proves
    /// every slot empty; a basket in a slot would be cargo this load no longer claims. A slot completed earlier in the run is
    /// a basket loaded, so that rules it out as well.
    /// </para>
    /// </remarks>
    public string? ResumeAbandonableAttempt
    {
        get
        {
            ActiveOperation? active = Volatile.Read(ref _activeOperation);
            if (active is null)
            {
                return null;
            }

            lock (active)
            {
                return AbandonableOver(active, _ioModule.CurrentSnapshot) ? active.SlotOperationAttemptId : null;
            }
        }
    }

    /// <summary>
    /// The operator pressed 「不装了」 over <paramref name="slotOperationAttemptId"/>: the run ends with what the slots read and
    /// opens nothing more. True when the run took the press; false, and nothing changes, when it is not offered now
    /// (<see cref="ResumeAbandonableAttempt"/>) or is another attempt's.
    /// </summary>
    /// <remarks>Taken in the same lock that takes a 「重新开门」, so the two presses of one wait cannot both win.</remarks>
    public bool AbandonResume(string slotOperationAttemptId)
    {
        ActiveOperation? active = Volatile.Read(ref _activeOperation);
        if (active is null
            || !string.Equals(active.SlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal))
        {
            return false;
        }

        lock (active)
        {
            if (!AbandonableOver(active, _ioModule.CurrentSnapshot) || active.OperatorRequest is not { } pending)
            {
                return false;
            }

            active.OperatorRequest = null;
            return pending.Answer.TrySetResult(WireToGateOperatorSlotRequest.AbandonResume);
        }
    }

    /// <summary>Whether 「不装了」 is offered over <paramref name="active"/> as <paramref name="snapshot"/> reads. Under its lock.</summary>
    private bool AbandonableOver(ActiveOperation active, IoSnapshot snapshot) =>
        active.IsResume
        && active.SkipHold is null
        && active.OperatorRequest is { Command.OperationType: OperationType.Load, Completed.Count: 0 } pending
        && AllTargetSlotsReadEmptyAndShut(pending.Command, snapshot);

    private bool AllTargetSlotsReadEmptyAndShut(WireToGateSlotOperationCommand command, IoSnapshot snapshot) =>
        IsFresh(snapshot)
        && command.Slots.All(slot => TryGetLocker(snapshot, slot - 1) is
        {
            IsKnown: true,
            IsLocked: true,
            UnlockOutputRaw: false,
            HasCargo: false
        });

    /// <summary>The skip offered over <paramref name="pending"/> as <paramref name="snapshot"/> reads, or <c>null</c>.</summary>
    private WireToGateRemainingBasketsSkipOffer? SkipOfferOver(PendingOperatorRequest pending, IoSnapshot snapshot)
    {
        WireToGateSlotOperationCommand command = pending.Command;
        if (command.OperationType != OperationType.Load
            || command.Slots.Count < 2
            || pending.Completed.Count == 0
            || pending.Completed.Count >= command.Slots.Count
            || !IsFresh(snapshot))
        {
            return null;
        }

        foreach (int slot in command.Slots)
        {
            LockerSnapshot locker = TryGetLocker(snapshot, slot - 1);
            if (!locker.IsKnown
                || !locker.IsLocked
                || locker.UnlockOutputRaw is not false
                || pending.Completed.Contains(slot) && !locker.HasCargo)
            {
                return null;
            }
        }

        return new WireToGateRemainingBasketsSkipOffer(
            command.SlotOperationAttemptId,
            command.DemandId,
            command.OperationSessionId,
            command.ExpectedBasketCount,
            [.. pending.Completed.Order()],
            pending.Wait.PhysicalSlot);
    }

    /// <summary>
    /// Applies an administrator's slot fault declaration to the run in progress, if it names that run's attempt
    /// and the slot the run is waiting on the operator for (REQ-0359, CP-0005 item 4.2, 8005-agv-onboard-hmi#215).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the load cancellation's abort.</b> <see cref="AbortOperationAsync"/> ends its run in silence because
    /// the cancellation reports the attempt's conclusion itself. Nobody reports for a declaration, so the run this
    /// stops does not end in <see cref="OperationCanceledException"/>: it settles the attempt -- the declared slot
    /// UNKNOWN with <see cref="SlotFaultDeclaredReason"/>, the slots after it NOT_STARTED, the completed ones as
    /// the live IO reads them -- and returns that result to the caller of <see cref="ExecuteAsync"/> or
    /// <see cref="ResumeAsync"/>, which reports it as it reports any other. The run tells the two cancellations
    /// apart by <see cref="ActiveOperation.DeclaredSlot"/>, which only this method sets.
    /// </para>
    /// <para>
    /// <b>Order.</b> Checked and journaled under <see cref="_declarationGate"/>, which the run also takes before
    /// every pulse, when a slot closes its loop and when it fails: whichever comes first decides. The journal
    /// write comes before anything is said, so a restart after it reports the declared slot UNKNOWN rather than
    /// judging it again from the live IO. <paramref name="announceApplied"/> runs next, still under the gate, so
    /// the <c>APPLIED</c> answer is on its way before the run is stopped and its <c>OperationResult</c> can
    /// follow; if it throws, the run is still stopped and the exception reaches the caller. Returns once the run
    /// has stopped.
    /// </para>
    /// <para>
    /// The precondition "not yet UNKNOWN" is this run's own: a slot that failed has been taken off
    /// <see cref="ActiveOperation.AwaitingSlot"/> under the gate before its UNKNOWN is written.
    /// </para>
    /// </remarks>
    public async Task<WireToGateSlotFaultDeclarationOutcome> DeclareSlotFaultAsync(
        WireToGateSlotFaultDeclaration declaration,
        Func<Task> announceApplied,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(announceApplied);
        RequireUuid(declaration.DeclarationId, nameof(declaration.DeclarationId));
        RequireUuid(declaration.SlotOperationAttemptId, nameof(declaration.SlotOperationAttemptId));

        ActiveOperation? active;
        await _declarationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            active = Volatile.Read(ref _activeOperation);
            if (active is null
                || !string.Equals(
                    active.SlotOperationAttemptId,
                    declaration.SlotOperationAttemptId,
                    StringComparison.Ordinal))
            {
                return WireToGateSlotFaultDeclarationOutcome.AttemptNotExecuting;
            }

            if (active.DeclaredSlot is not null)
            {
                return WireToGateSlotFaultDeclarationOutcome.AlreadyDeclared;
            }

            if (active.AwaitingSlot != declaration.SlotNo)
            {
                return WireToGateSlotFaultDeclarationOutcome.SlotNotAwaiting;
            }

            // CancellationToken.None: from the check above to the write the gate keeps the run from moving on, and
            // a declaration abandoned halfway would leave the operator's slot in neither state.
            //
            // A load cancellation over this attempt -- pressed and waiting for its answer, or already authorized and
            // running -- owns the attempt's conclusion, and the declaration is refused in the same journal step that
            // would have written it (review S1 of PR #247). The cancellation refuses the other order itself, before
            // it stops this run: WireToGateBusinessService.RequestLoadCancellationCoreAsync. Its pending entry is
            // journaled before its request goes out, so one of the two always sees the other.
            WireToGateSlotFaultDeclarationOutcome refusal = WireToGateSlotFaultDeclarationOutcome.AlreadyDeclared;
            WireToGateRecoveryState? written = await _journal.UpdateRecoveryStateAsync(
                state =>
                {
                    if (string.Equals(
                            state.PendingLoadCancellation?.SlotOperationAttemptId,
                            declaration.SlotOperationAttemptId,
                            StringComparison.Ordinal)
                        || string.Equals(
                            state.RecoveryVector?.SlotOperationAttemptId,
                            declaration.SlotOperationAttemptId,
                            StringComparison.Ordinal))
                    {
                        refusal = WireToGateSlotFaultDeclarationOutcome.TakenOverByLoadCancellation;
                        return null;
                    }

                    return string.Equals(
                            state.UnsettledSlotOperationAttemptId,
                            declaration.SlotOperationAttemptId,
                            StringComparison.Ordinal)
                        && !string.Equals(
                            state.SlotFaultDeclaration?.SlotOperationAttemptId,
                            declaration.SlotOperationAttemptId,
                            StringComparison.Ordinal)
                        ? state with { SlotFaultDeclaration = declaration }
                        : null;
                },
                CancellationToken.None).ConfigureAwait(false);
            if (written is null)
            {
                // A load cancellation owns the attempt, or the journal no longer names this attempt as the one in
                // progress, or already names a declaration on it: not this run's to decide any more.
                return refusal;
            }

            active.DeclaredSlot = declaration.SlotNo;
            try
            {
                await announceApplied().ConfigureAwait(false);
            }
            finally
            {
                // Journaled means applied, whatever became of the announcement: the run is stopped either way.
                // Still under the gate, so a run waiting for it at a pulse or at a closing step finds the declaration
                // and its own token already cancelled, and nothing it does after the gate gets ahead of the stop.
                try
                {
                    await active.Abort.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // Only a shutdown ends the run on this slot without the gate; there is nothing left to stop.
                }
            }
        }
        finally
        {
            _declarationGate.Release();
        }

        await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _operationGate.Release();
        return WireToGateSlotFaultDeclarationOutcome.Applied;
    }

    /// <summary>
    /// Marks <paramref name="physicalSlot"/> as the slot <paramref name="active"/> is waiting on the operator for.
    /// </summary>
    private async Task BeginAwaitingAsync(ActiveOperation? active, int physicalSlot)
    {
        if (active is null)
        {
            return;
        }

        await _declarationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            active.AwaitingSlot = physicalSlot;
        }
        finally
        {
            _declarationGate.Release();
        }
    }

    /// <summary>
    /// Ends the wait on <paramref name="physicalSlot"/> -- it closed its loop, or failed -- unless a declaration got
    /// there first. Returns true when one did: the slot is then the declaration's, whatever the run saw.
    /// </summary>
    private async Task<bool> EndAwaitingAsync(ActiveOperation? active, int physicalSlot)
    {
        if (active is null)
        {
            return false;
        }

        await _declarationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (active.DeclaredSlot == physicalSlot)
            {
                return true;
            }

            active.AwaitingSlot = null;
            return false;
        }
        finally
        {
            _declarationGate.Release();
        }
    }

    private static bool IsDeclared(ActiveOperation? active, int physicalSlot) =>
        active is not null && active.DeclaredSlot == physicalSlot;

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
        Volatile.Write(ref _activeOperation, new ActiveOperation(resume.SlotOperationAttemptId, abort) { IsResume = true });
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

    /// <summary>
    /// Records that the server acknowledged the COMPLETED result of <paramref name="slotOperationAttemptId"/>, the journal's
    /// unsettled attempt, and says how: settled, or left to the recovery vector that has taken the attempt over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A recovery vector prepared over the same attempt keeps its entry</b> (8005-agv-onboard-hmi#259). An in-flight
    /// load cancellation is prepared under the load's own attempt id, and the load's acknowledgement can land after that
    /// prepare write: the server answers the cancellation request and then the result, and the two continuations race on
    /// the vehicle. Settled here, the write would drop the running cancellation and the door it holds open from the
    /// journal -- a restart in that window found no vector and no open door. So only the result's own share is closed:
    /// its pending entry goes, so no handshake replays it, and the load becomes the last completed one, as it would had
    /// the acknowledgement come first. The outbox row is not this method's: the send that got the acknowledgement marked
    /// it already. Everything the prepare write took over -- the unsettled attempt, the checkpoint, the active unlock set,
    /// the vector, the operation context, the slot results and the recovery session -- stays, and the attempt is
    /// settled with the vector when the vector settles.
    /// </para>
    /// <para>
    /// <b>So does an attempt a vector held when it was forgotten</b> (8005-agv-onboard-hmi#278): a fault cargo handoff or a
    /// compensation over a load whose result went out unacknowledged, ended UNKNOWN or FAILED. The vector is gone, its
    /// doors in doubt are not, and <see cref="WireToGateRecoveryState.TakenOverSlotOperationAttemptId"/> is what says so --
    /// the acknowledgement may come while the vector runs, after it was forgotten, or with a replay after a reconnect.
    /// </para>
    /// <para>
    /// Answered as a value rather than thrown: every caller has a result the server acknowledged in hand, and a refusal
    /// here used to end the formal load path with nothing caught.
    /// </para>
    /// </remarks>
    public async Task<WireToGateResultRecording> MarkResultRecordedAsync(
        string slotOperationAttemptId,
        CancellationToken cancellationToken = default)
    {
        RequireUuid(slotOperationAttemptId, nameof(slotOperationAttemptId));
        // Read, checked and written as one step under the journal's lock, as the CLOSED release does
        // (onboard-hmi#123). A late acknowledgement records its attempt while the next operation may already be
        // journaling its Prepared; with a separate read and write, that write could land in between and this one
        // would put the old state back over it, wiping the operation now at the doors (onboard-hmi#127).
        WireToGateResultRecording recording = WireToGateResultRecording.Settled;
        // The stop a recorded load was made at is stamped from the journey on screen in the same write (8005-agv-onboard-hmi#352
        // review L1), not only from the next journey snapshot: one that ended the stop without an operation session would
        // otherwise leave the load unstamped.
        WireToGateJourneySnapshot journey = _currentJourney();
        WireToGateRecoveryState? written = await _journal.UpdateRecoveryStateAsync(
            state =>
            {
                if (!string.Equals(state.UnsettledSlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal))
                {
                    return null;
                }

                if (state.RecoveryVector is { } vector
                        && string.Equals(vector.SlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal)
                    || string.Equals(state.TakenOverSlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal))
                {
                    recording = WireToGateResultRecording.TakenOverByRecoveryVector;
                    return RecordedUnderRecoveryVector(state, slotOperationAttemptId).WithLoadStopsStampedFrom(journey);
                }

                recording = WireToGateResultRecording.Settled;
                return Recorded(state).WithLoadStopsStampedFrom(journey);
            },
            cancellationToken).ConfigureAwait(false);
        if (written is null)
        {
            throw new InvalidDataException("SLOT_OPERATION_CONFLICT");
        }

        return recording;
    }

    /// <summary>
    /// The result's own share of <see cref="Recorded"/>, for an attempt a recovery vector has taken over: the two fields
    /// that belong to the acknowledged result and not to the vector (8005-agv-onboard-hmi#259).
    /// </summary>
    private static WireToGateRecoveryState RecordedUnderRecoveryVector(
        WireToGateRecoveryState state,
        string slotOperationAttemptId) =>
        WithLoadOnBoardRecorded(state, slotOperationAttemptId) with
        {
            PendingResults = state.PendingResults
                .Where(item => !(string.Equals(item.MessageType, "OperationResult", StringComparison.Ordinal)
                    && string.Equals(item.BusinessId, slotOperationAttemptId, StringComparison.Ordinal)))
                .ToArray(),
            LastCompletedLoadOperationContext = state.OperationContext is { OperationType: OperationType.Load } load
                && string.Equals(load.SlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal)
                    ? LoadedContext(state)
                    : state.LastCompletedLoadOperationContext
        };

    /// <summary>
    /// The journal's operation context as the load it put on board: the context itself, or, for a load ended on an authorized
    /// skip -- its slot results carry <see cref="RemainingBasketsSkippedReason"/> -- the context narrowed to the slots it
    /// completed, with <c>expectedBasketCount</c> k (8005-agv-onboard-hmi#388). Its unload names only those slots.
    /// </summary>
    private static WireToGateRecoveryOperationContext? LoadedContext(WireToGateRecoveryState state)
    {
        if (state.OperationContext is not { OperationType: OperationType.Load } context
            || !state.SlotResults.Any(result => result.ReasonCodes.Contains(RemainingBasketsSkippedReason)))
        {
            return state.OperationContext;
        }

        int[] loaded = [.. state.SlotResults
            .Where(result => string.Equals(result.Outcome, "COMPLETED", StringComparison.Ordinal))
            .Select(result => result.SlotNo)
            .Order()];
        return context with { Slots = loaded, ExpectedBasketCount = loaded.Length };
    }

    /// <summary>
    /// The settled-load list's share of recording <paramref name="slotOperationAttemptId"/> (8005-agv-onboard-hmi#209): its
    /// load is on board, its unload takes the demand's load off. Anything else -- no context, another attempt's -- leaves
    /// the list as it is.
    /// </summary>
    private static WireToGateRecoveryState WithLoadOnBoardRecorded(
        WireToGateRecoveryState state,
        string? slotOperationAttemptId) =>
        state.OperationContext is { } context
            && string.Equals(context.SlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal)
                ? context.OperationType == OperationType.Load
                    ? state.WithLoadOnBoard(LoadedContext(state)!)
                    : state.WithoutLoadOnBoard(context.DemandId)
                : state;

    private static WireToGateRecoveryState Recorded(WireToGateRecoveryState state) =>
        WithLoadOnBoardRecorded(state, state.OperationContext?.SlotOperationAttemptId) with
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
            SlotFaultDeclaration = null,
            TakenOverSlotOperationAttemptId = null,
            RemainingBasketsSkip = null,
            LastCompletedLoadOperationContext = state.OperationContext?.OperationType == OperationType.Load
                ? LoadedContext(state)
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
        // A slot fault declaration applied to this attempt and journaled before the process went away
        // (8005-agv-onboard-hmi#215). Its slot is UNKNOWN for the declaration's reason whatever it reads now:
        // judged from the live IO it could come out COMPLETED -- a reading stuck at the target state is one of
        // the faults an administrator declares -- and the APPLIED the server may already hold would then be
        // contradicted by the result.
        int? declaredSlot = string.Equals(
            state.SlotFaultDeclaration?.SlotOperationAttemptId,
            context.SlotOperationAttemptId,
            StringComparison.Ordinal)
            ? state.SlotFaultDeclaration!.SlotNo
            : null;
        // The operator's 「剩下的不装」 authorized and journaled before the process went away (8005-agv-onboard-hmi#388): the load
        // ends as the authorization says when the slots still read so, without a pulse. Otherwise, below, as it always has.
        if (declaredSlot is null
            && state.RemainingBasketsSkip is { IsAuthorized: true } skip
            && string.Equals(skip.SlotOperationAttemptId, context.SlotOperationAttemptId, StringComparison.Ordinal)
            && SkippedResults(command, skip.Result!.LoadedSlots, state.CompletedSlots, snapshot, fresh) is { } skipped)
        {
            await WriteCheckpointAsync(
                context,
                WireToGateRecoveryCheckpoint.SafeFinishReached,
                [],
                skip.Result.LoadedSlots,
                skipped,
                state,
                CancellationToken.None).ConfigureAwait(false);
            return CreateResult(
                command,
                RemainingBasketsSkippedOutcome,
                skipped,
                WireToGateRecoveryCheckpoint.SafeFinishReached);
        }

        foreach (int physicalSlot in command.Slots)
        {
            LockerSnapshot locker = fresh
                ? TryGetLocker(snapshot, physicalSlot - 1)
                : LockerSnapshot.Unknown(physicalSlot - 1, snapshot.ObservedAt);
            if (physicalSlot == declaredSlot)
            {
                UpsertResult(results, CreateSlotResult(locker, "UNKNOWN", [SlotFaultDeclaredReason]));
                if (!fresh || !IsSafeFinish(snapshot, physicalSlot - 1))
                {
                    unsafeSlot = true;
                    stillActive.Add(physicalSlot);
                }
            }
            else if (!OpenedByThisAttempt(state, physicalSlot))
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

        // The journal's active unlock set is the record of a door that may still be open -- left by an UNKNOWN result,
        // a recovery vector the server has concluded, or one a maintainer ended after the manual check -- and the
        // clean journal below would drop it from every later handshake. So the whole set is asked of a fresh reading
        // first, not only this command's targets: a door it does not prove shut refuses the command before anything is
        // written, and the record stays (8005-agv-onboard-hmi#267). The single-door rule in front of each pulse would
        // stop the pulse too, but only after the clean journal had been written and an UNKNOWN result made up for an
        // operation that opened nothing.
        bool initialFresh = IsFresh(initial);
        int[] doorsInDoubt = WireToGateSingleDoorRule.DoorsNotProvenShut(
            initial,
            initialFresh,
            journaled.ActiveUnlockSlots);
        if (doorsInDoubt.Length > 0)
        {
            throw new WireToGateDoorNotProvenShutException(
                doorsInDoubt,
                initial,
                initialFresh,
                journaled.UnsettledSlotOperationAttemptId);
        }

        // A recovery vector on file is settled before the operation starts, or the operation is refused
        // (8005-agv-onboard-hmi#267). Its fields are not the checkpoint's: written beside a new attempt they would stay,
        // and the late acknowledgement of the vector's result would then settle the new attempt as the vector's. When
        // the vector's own result is already in the outbox the server has it -- and a server still holding that
        // operation for recovery would not have called the vehicle ready and sent this command -- so the first write
        // below takes the vector and its recovery session off. A vector with no result yet, prepared and not run or
        // running, refuses; so does a forced mechanical recovery, whose isolation only its acknowledged result records.
        string? vectorResultKey = null;
        if (journaled.RecoveryVector is { } vectorOnFile)
        {
            vectorResultKey = vectorOnFile.VectorType == WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery
                ? null
                : await _vectorResultKeyOnFile(vectorOnFile, journaled, cancellationToken).ConfigureAwait(false);
            if (vectorResultKey is null)
            {
                throw new WireToGateRecoveryVectorUnsettledException(vectorOnFile);
            }
        }

        WireToGateRecoveryVectorContext? vectorToSettle = journaled.RecoveryVector;
        IReadOnlyList<int> physicallyUnknown = journaled.ForcedIsolation?.PhysicallyUnknownSlots ?? [];
        if (command.Slots.Any(physicallyUnknown.Contains))
        {
            // A slot opened by hand after the power was cut (REQ-0241): whatever the IO reads now,
            // nothing proves what state it was left in, so it is not operated until a hardware
            // recovery record clears it (onboard-hmi#107).
            return CreateRejectedResult(command, initial, started, physicallyUnknown);
        }

        // A new operation starts from a clean journal, except for the device facts that outlive
        // every operation. Only the fields a checkpoint owns are replaced (WriteCheckpointAsync); a recovery vector and
        // its session stay as the journal holds them. Every door of the active unlock set has just been proven shut, so
        // replacing the set drops no door in doubt; the earlier attempt it replaces, and a vector it writes beside, are
        // told to the caller to log once the journal is written (OnJournalOverwritten).
        WireToGateRecoveryState fresh = WireToGateRecoveryState.Empty with
        {
            ForcedIsolation = journaled.ForcedIsolation,
            RepairRelease = journaled.RepairRelease
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
                cancellationToken,
                vectorToSettle).ConfigureAwait(false);
            OnJournalOverwritten(journaled, command, vectorResultKey);
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
            cancellationToken,
            vectorToSettle).ConfigureAwait(false);
        OnJournalOverwritten(journaled, command, vectorResultKey);
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

    /// <summary>
    /// Tells the caller what the first write of <paramref name="command"/> replaced, when it replaced anything an operator
    /// or a maintainer may later ask about: an unsettled attempt, an active unlock set or a recovery vector it settled.
    /// </summary>
    private void OnJournalOverwritten(
        WireToGateRecoveryState replaced,
        WireToGateSlotOperationCommand command,
        string? vectorResultKey)
    {
        if (replaced.UnsettledSlotOperationAttemptId is null
            && replaced.ActiveUnlockSlots.Count == 0
            && replaced.RecoveryVector is null)
        {
            return;
        }

        _journalOverwritten(new WireToGateJournalOverwrite(
            command.SlotOperationAttemptId,
            replaced.UnsettledSlotOperationAttemptId,
            [.. replaced.ActiveUnlockSlots.Order()],
            replaced.RecoveryVector,
            vectorResultKey));
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
        // Both callers publish the run before they get here.
        ActiveOperation? active = Volatile.Read(ref _activeOperation);
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
                await BeginAwaitingAsync(active, physicalSlot).ConfigureAwait(false);
                LockerSnapshot completedLocker = await DriveSlotToTargetStateAsync(
                    command,
                    physicalSlot,
                    completed,
                    progress,
                    active,
                    cancellationToken).ConfigureAwait(false);
                if (await EndAwaitingAsync(active, physicalSlot).ConfigureAwait(false))
                {
                    // The door closed on the target state, but a declaration was applied to this slot first and
                    // its APPLIED is already on its way: the slot is the declaration's (8005-agv-onboard-hmi#215).
                    return await SettleDeclaredAsync(
                        command,
                        context,
                        existingState,
                        completed,
                        results,
                        progress,
                        physicalSlot).ConfigureAwait(false);
                }

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
            catch (OperationCanceledException) when (IsDeclared(active, physicalSlot))
            {
                // Stopped by a slot fault declaration, not by a load cancellation taking the slots over: nobody
                // else reports this attempt, so the run settles it and returns the result (8005-agv-onboard-hmi#215).
                // The load cancellation's abort, and a shutdown, still end in silence below.
                return await SettleDeclaredAsync(
                    command,
                    context,
                    existingState,
                    completed,
                    results,
                    progress,
                    physicalSlot).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RemainingBasketsSkipAuthorizedException authorized)
            {
                // The server authorized the operator's 「剩下的不装」 while the run held the slots for it
                // (8005-agv-onboard-hmi#388). A declaration applied first still owns its slot.
                if (await EndAwaitingAsync(active, physicalSlot).ConfigureAwait(false))
                {
                    return await SettleDeclaredAsync(
                        command,
                        context,
                        existingState,
                        completed,
                        results,
                        progress,
                        physicalSlot).ConfigureAwait(false);
                }

                return await SettleRemainingBasketsSkippedAsync(
                    command,
                    context,
                    existingState,
                    completed,
                    results,
                    progress,
                    physicalSlot,
                    authorized.AuthorizedSlots).ConfigureAwait(false);
            }
            catch (ResumeAbandonedException)
            {
                // The operator's 「不装了」 in a resume over empty slots (8005-agv-onboard-hmi#410). A declaration applied
                // first still owns its slot.
                if (await EndAwaitingAsync(active, physicalSlot).ConfigureAwait(false))
                {
                    return await SettleDeclaredAsync(
                        command,
                        context,
                        existingState,
                        completed,
                        results,
                        progress,
                        physicalSlot).ConfigureAwait(false);
                }

                return await SettleResumeAbandonedAsync(
                    command,
                    context,
                    existingState,
                    completed,
                    results,
                    progress,
                    physicalSlot).ConfigureAwait(false);
            }
            catch (FatalFaultLatchedException latched)
            {
                if (await EndAwaitingAsync(active, physicalSlot).ConfigureAwait(false))
                {
                    return await SettleDeclaredAsync(
                        command,
                        context,
                        existingState,
                        completed,
                        results,
                        progress,
                        physicalSlot).ConfigureAwait(false);
                }

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
                    or RefusedBeforeFirstPulseException
                    or SlotFeedbackFailedException)
            {
                // Only the three conditions of ADR-cross-0058 decision 2 reach this branch: the slot
                // state cannot be read, the lock feedback is not valid, or the unlock output cannot be
                // confirmed reset -- of this slot, or, before a pulse, of another door (REQ-0357). An
                // operator who has not loaded, unloaded or shut the door is still inside
                // DriveSlotToTargetStateAsync. One snapshot serves the failed slot and the slots never
                // started, so the two cannot disagree about what was read.
                //
                // A declaration applied to this slot before the failure was decided owns it: its APPLIED is
                // already on its way, and the OperationResult has to say the same (8005-agv-onboard-hmi#215).
                // Otherwise the slot leaves the awaiting state here, before its UNKNOWN is written, so a
                // declaration arriving from now on finds it no longer waiting.
                if (await EndAwaitingAsync(active, physicalSlot).ConfigureAwait(false))
                {
                    return await SettleDeclaredAsync(
                        command,
                        context,
                        existingState,
                        completed,
                        results,
                        progress,
                        physicalSlot).ConfigureAwait(false);
                }

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
    /// Ends a resume on the operator's 「不装了」 (8005-agv-onboard-hmi#410) with what the slots read now: <c>FAILED</c> -- the
    /// slot held shut <c>FAILED</c>, the slots the run had not reached <c>NOT_STARTED</c>, none with a reason code -- at the
    /// safe finish, with no door opened on the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>FAILED, not UNKNOWN</b>, for the reason a latch refusal is FAILED (<see cref="SettleRefusedByLatchAsync"/>): every
    /// slot is read, shut, locked and reset, so nothing about it is in doubt. It carries no <c>OPERATOR_TIMEOUT</c>, so the
    /// server never settles it as a determinate failure: it judges the replacement not reconciled, keeps the load
    /// RecoveryRequired and closes the session, and 「补偿清空」 settles the load next. No protocol reason code says "the
    /// operator gave up", and none is borrowed.
    /// </para>
    /// <para>
    /// <b>When the slots no longer read so</b> -- changed in the milliseconds since the press was taken -- the held slot is
    /// reported <c>UNKNOWN</c> under the reason its reading gives, as any other reading that cannot be trusted is.
    /// </para>
    /// </remarks>
    private async Task<WireToGateOperationExecutionResult> SettleResumeAbandonedAsync(
        WireToGateSlotOperationCommand command,
        WireToGateRecoveryOperationContext context,
        WireToGateRecoveryState existingState,
        List<int> completed,
        List<WireToGateSlotExecutionResult> results,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        int physicalSlot)
    {
        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        bool proven = AllTargetSlotsReadEmptyAndShut(command, snapshot);
        LockerSnapshot held = ReadLocker(snapshot, physicalSlot - 1);
        UpsertResult(
            results,
            proven
                ? CreateSlotResult(held, "FAILED", [])
                : CreateSlotResult(held, "UNKNOWN", [UnprovenReason(held)]));
        foreach (int notStarted in command.Slots
            .Where(slot => slot != physicalSlot && !completed.Contains(slot)))
        {
            UpsertResult(results, CreateSlotResult(ReadLocker(snapshot, notStarted - 1), "NOT_STARTED", []));
        }

        bool safeFinish = IsSafeFinish(snapshot, physicalSlot - 1);
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
        return CreateResult(command, proven ? "FAILED" : "UNKNOWN", results, checkpoint);
    }

    /// <summary>Why a slot read for a settlement is not proven shut and reset, in the wire's reason codes.</summary>
    private static string UnprovenReason(LockerSnapshot locker) =>
        !locker.IsKnown ? "SLOT_STATE_UNKNOWN"
        : !locker.IsLocked ? "LOCK_NOT_CLOSED"
        : locker.UnlockOutputRaw is not false ? "UNLOCK_OUTPUT_NOT_RESET"
        : "SLOT_STATE_UNKNOWN";

    /// <summary>
    /// Ends the load on the server's authorization of 「剩下的不装」 (8005-agv-onboard-hmi#388, CP-0021 REQ-0367): with the slots
    /// reading as the authorization says, <c>COMPLETED_REMAINING_SKIPPED</c> -- the authorized slots <c>COMPLETED</c>, every
    /// other target slot <c>NOT_STARTED</c> with <c>REMAINING_BASKETS_SKIPPED</c> -- at the safe finish, with the authorized
    /// slots completed. No slot is opened on the way.
    /// </summary>
    /// <remarks>
    /// When the slots no longer read so -- a slot unreadable, a door not locked, a basket where none is to be or none where
    /// one is -- the vehicle reports what it would without the skip (wire notes): <c>UNKNOWN</c>, the slot in doubt
    /// <c>UNKNOWN</c> and every untouched slot as read, never <c>REMAINING_BASKETS_SKIPPED</c>. The authorization is then
    /// spent on the server's side when that result settles.
    /// </remarks>
    private async Task<WireToGateOperationExecutionResult> SettleRemainingBasketsSkippedAsync(
        WireToGateSlotOperationCommand command,
        WireToGateRecoveryOperationContext context,
        WireToGateRecoveryState existingState,
        List<int> completed,
        List<WireToGateSlotExecutionResult> results,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        int heldSlot,
        IReadOnlyList<int> authorized)
    {
        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        bool fresh = IsFresh(snapshot);
        if (SkippedResults(command, authorized, completed, snapshot, fresh) is { } skipped)
        {
            foreach (WireToGateSlotExecutionResult result in skipped)
            {
                UpsertResult(results, result);
            }

            await WriteCheckpointAsync(
                context,
                WireToGateRecoveryCheckpoint.SafeFinishReached,
                [],
                authorized,
                results,
                existingState,
                CancellationToken.None).ConfigureAwait(false);
            await SendProgressAsync(progress, new("SAFE_FINISH", [], authorized), CancellationToken.None)
                .ConfigureAwait(false);
            return CreateResult(
                command,
                RemainingBasketsSkippedOutcome,
                results,
                WireToGateRecoveryCheckpoint.SafeFinishReached);
        }

        List<int> stillCompleted = [];
        bool safeFinish = true;
        foreach (int physicalSlot in command.Slots)
        {
            LockerSnapshot locker = ReadLocker(snapshot, physicalSlot - 1);
            if (completed.Contains(physicalSlot) && IsFinalState(locker, command.ExpectedOccupied))
            {
                UpsertResult(results, CreateSlotResult(locker, "COMPLETED", []));
                stillCompleted.Add(physicalSlot);
            }
            else if (!completed.Contains(physicalSlot)
                && physicalSlot != heldSlot
                && IsFinalState(locker, !command.ExpectedOccupied))
            {
                // Never opened by this run, and as it was: decision 6.
                UpsertResult(results, CreateSlotResult(locker, "NOT_STARTED", []));
            }
            else
            {
                UpsertResult(
                    results,
                    CreateSlotResult(
                        locker,
                        "UNKNOWN",
                        [locker.IsKnown ? "RECOVERY_CHECKPOINT_NOT_UNIQUE" : "SLOT_STATE_UNKNOWN"]));
                safeFinish &= IsSafeFinish(snapshot, physicalSlot - 1);
            }
        }

        WireToGateRecoveryCheckpoint checkpoint = safeFinish
            ? WireToGateRecoveryCheckpoint.SafeFinishReached
            : WireToGateRecoveryCheckpoint.ActiveUnlockSet;
        IReadOnlyList<int> activeSlots = safeFinish ? [] : [heldSlot];
        await WriteCheckpointAsync(
            context,
            checkpoint,
            activeSlots,
            stillCompleted,
            results,
            existingState,
            CancellationToken.None).ConfigureAwait(false);
        await SendProgressAsync(
                progress,
                new(safeFinish ? "SAFE_FINISH" : "PAUSED", activeSlots, stillCompleted),
                CancellationToken.None)
            .ConfigureAwait(false);
        return CreateResult(command, "UNKNOWN", results, checkpoint);
    }

    /// <summary>The <c>OperationResult.overallOutcome</c> of a load ended on an authorized skip (protocol 6.0.0 candidate).</summary>
    public const string RemainingBasketsSkippedOutcome = "COMPLETED_REMAINING_SKIPPED";

    /// <summary>The slot reason every skipped slot carries, and no other slot of any result (protocol 6.0.0 candidate).</summary>
    public const string RemainingBasketsSkippedReason = "REMAINING_BASKETS_SKIPPED";

    /// <summary>
    /// The slot results of a skip of <paramref name="command"/> over <paramref name="authorized"/>, read off
    /// <paramref name="snapshot"/>, or <c>null</c> when the slots do not read as the skip needs: the authorized slots are
    /// exactly the completed ones and each reads its final state, every other target slot reads known, locked, empty and
    /// reset. Shared by the run and the interrupted settlement, so both report a skip on the same facts.
    /// </summary>
    private static List<WireToGateSlotExecutionResult>? SkippedResults(
        WireToGateSlotOperationCommand command,
        IReadOnlyList<int> authorized,
        IReadOnlyCollection<int> completed,
        IoSnapshot snapshot,
        bool fresh)
    {
        if (!fresh
            || command.OperationType != OperationType.Load
            || authorized.Count == 0
            || authorized.Count >= command.Slots.Count
            || !authorized.Order().SequenceEqual(completed.Distinct().Order()))
        {
            return null;
        }

        List<WireToGateSlotExecutionResult> results = [];
        foreach (int physicalSlot in command.Slots)
        {
            LockerSnapshot locker = TryGetLocker(snapshot, physicalSlot - 1);
            bool loaded = authorized.Contains(physicalSlot);
            if (!IsFinalState(locker, expectedOccupied: loaded))
            {
                return null;
            }

            results.Add(loaded
                ? CreateSlotResult(locker, "COMPLETED", [])
                : CreateSlotResult(locker, "NOT_STARTED", [RemainingBasketsSkippedReason]));
        }

        return results;
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
    /// Ends the run on the slot a fault declaration was applied to (REQ-0359, 8005-agv-onboard-hmi#215): that slot
    /// UNKNOWN with <see cref="SlotFaultDeclaredReason"/>, every slot not reached NOT_STARTED, and each completed slot
    /// as the live IO reads it now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The declared slot is never judged from its reading.</b> A reading that looks final -- a light curtain stuck
    /// at "empty" on an unload, say -- would make the interrupted settlement call it COMPLETED, which is exactly what
    /// an administrator declared it faulty to prevent. Its physical fields still carry the live readings
    /// (ADR-cross-0058 decision 6).
    /// </para>
    /// <para>
    /// A completed slot that no longer reads final is UNKNOWN, as the interrupted settlement would have it; one that
    /// does stays COMPLETED. The journal keeps the attempt unsettled, with the declared slot in the active set while
    /// its door may be open -- the shape every UNKNOWN decided mid-execution leaves for the recovery entries. Written
    /// with <see cref="CancellationToken.None"/>: the run was cancelled to get here.
    /// </para>
    /// </remarks>
    private async Task<WireToGateOperationExecutionResult> SettleDeclaredAsync(
        WireToGateSlotOperationCommand command,
        WireToGateRecoveryOperationContext context,
        WireToGateRecoveryState existingState,
        List<int> completed,
        List<WireToGateSlotExecutionResult> results,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        int declaredSlot)
    {
        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        bool fresh = IsFresh(snapshot);
        List<int> stillCompleted = [];
        foreach (int physicalSlot in command.Slots)
        {
            LockerSnapshot locker = ReadLocker(snapshot, physicalSlot - 1);
            if (physicalSlot == declaredSlot)
            {
                UpsertResult(results, CreateSlotResult(locker, "UNKNOWN", [SlotFaultDeclaredReason]));
            }
            else if (!completed.Contains(physicalSlot))
            {
                UpsertResult(results, CreateSlotResult(locker, "NOT_STARTED", []));
            }
            else if (IsFinalState(locker, command.ExpectedOccupied))
            {
                UpsertResult(results, CreateSlotResult(locker, "COMPLETED", []));
                stillCompleted.Add(physicalSlot);
            }
            else
            {
                UpsertResult(
                    results,
                    CreateSlotResult(
                        locker,
                        "UNKNOWN",
                        [fresh ? "RECOVERY_CHECKPOINT_NOT_UNIQUE" : "SLOT_STATE_UNKNOWN"]));
            }
        }

        bool safeFinish = IsSafeFinish(snapshot, declaredSlot - 1);
        WireToGateRecoveryCheckpoint checkpoint = safeFinish
            ? WireToGateRecoveryCheckpoint.SafeFinishReached
            : WireToGateRecoveryCheckpoint.ActiveUnlockSet;
        IReadOnlyList<int> activeSlots = safeFinish ? [] : [declaredSlot];
        await WriteCheckpointAsync(
            context,
            checkpoint,
            activeSlots,
            stillCompleted,
            results,
            existingState,
            CancellationToken.None).ConfigureAwait(false);
        await SendProgressAsync(
                progress,
                new(safeFinish ? "SAFE_FINISH" : "PAUSED", activeSlots, stillCompleted),
                CancellationToken.None)
            .ConfigureAwait(false);
        return CreateResult(command, "UNKNOWN", results, checkpoint);
    }

    /// <summary>
    /// Refuses the pulse about to go out on a slot a fault declaration was applied to. Called under
    /// <see cref="_declarationGate"/>; the cancellation it throws is the one the run settles as declared.
    /// </summary>
    private static void ThrowIfDeclared(ActiveOperation? active, int physicalSlot)
    {
        if (IsDeclared(active, physicalSlot))
        {
            throw new OperationCanceledException();
        }
    }

    /// <summary>
    /// Drives one slot to its target state (ADR-cross-0058 decision 1, as CP-0019 rewrote it). A door shut over the
    /// opposite occupancy -- loading without putting a basket in, unloading without taking it out -- is not a failure
    /// and not recovery: the slot stays shut and locked, nothing is pulsed, and the operator is asked to press
    /// 「重新开门」 (<see cref="WireToGatePromptCause.ShutOverOpposite"/>), or to cancel a load. Each press is one more
    /// pulse, with no limit on how many (ADR-cross-0040). A door that stays open meets neither condition, and every
    /// <see cref="WireToGateSlotOperationExecutorOptions.OperationTimeout"/> it only prompts again (decision 3); so
    /// does the wait for the press. Returns the reading that satisfied <see cref="IsFinalState"/>; throws only for the
    /// decision 2 conditions, which the caller turns into UNKNOWN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it no longer reopens by itself</b> (8005-agv-onboard-hmi#385). The automatic reopen came about 0.4 s
    /// after the door was read shut, while the person who shut it was often still at it: on 2026-10-19 (agv03) the lock
    /// released for 0.24 s and caught again, on 2026-10-21 (agv02) it did not release at all. Either way the wait for the
    /// lock to read released ran out, and that was read as feedback that cannot be trusted -- UNKNOWN, and the vehicle
    /// stood until a recovery, once for five hours.
    /// </para>
    /// <para>
    /// <b>A pressed reopen that does not open is not decision 2 either.</b> When the lock does not hold released for the
    /// stable window, the slot is asked once more: read, locked, output reset, held for the stable window within
    /// <see cref="WireToGateSlotOperationExecutorOptions.UnlockOutputResetTimeout"/>. Then the door is simply shut again
    /// over a known occupancy, and the run waits for the press (<see cref="WireToGatePromptCause.ReopenNotReleased"/>).
    /// Only a slot that cannot be read, an output that does not reset, or a lock that will not settle either way is
    /// decision 2. The first pulse of a slot keeps decision 2 as it was: it is the server's command over a slot the
    /// precheck found shut, and nobody has been at that door yet.
    /// </para>
    /// <para>
    /// With no deadline, a press can come long after the command did, so every reopen pulse first asks the vehicle
    /// safety fact. While it says no, the slot is held: the door is shut and locked, nothing is pulsed, and the operator
    /// is told why on the prompt cadence. When it allows a pulse again, the run goes back to waiting for a press rather
    /// than pulsing on the one it held: an emergency stop can last long, and the person who pressed may have gone, so a
    /// door that sprang open by itself when the stop cleared would be the automatic reopen CP-0019 removed (review M3 of
    /// PR #390).
    /// </para>
    /// </remarks>
    private async Task<LockerSnapshot> DriveSlotToTargetStateAsync(
        WireToGateSlotOperationCommand command,
        int physicalSlot,
        IReadOnlyList<int> completed,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        ActiveOperation? active,
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
            if (cause is WireToGatePromptCause.ShutOverOpposite or WireToGatePromptCause.ReopenNotReleased)
            {
                (OperatorWaitOutcome waited, promptRound) = await WaitForOperatorRequestAsync(
                    command,
                    physicalSlot,
                    completed,
                    promptRound,
                    cause,
                    progress,
                    active,
                    cancellationToken).ConfigureAwait(false);
                if (waited == OperatorWaitOutcome.RemainingBasketsSkipHeld)
                {
                    // Held for the server's decision; an authorization ends the load from inside the wait. Let go -- rejected
                    // or dropped -- the run goes on as the slot now stands: held shut for the press again, or, should the
                    // lock have let go by itself meanwhile, an open door to be shut (8005-agv-onboard-hmi#388).
                    promptRound = await WaitForRemainingBasketsSkipDecisionAsync(
                        physicalSlot,
                        completed,
                        promptRound,
                        progress,
                        active,
                        cancellationToken).ConfigureAwait(false);
                    LockerSnapshot now = ReadLocker(_ioModule.CurrentSnapshot, slotIndex);
                    if (!(now.IsKnown && !now.IsLocked))
                    {
                        continue;
                    }

                    promptRound++;
                    unlockNeeded = false;
                    cause = WireToGatePromptCause.PromptCadence;
                }
                else if (waited == OperatorWaitOutcome.ResumeAbandoned)
                {
                    // Nothing pulsed: the run ends with what the slots read (8005-agv-onboard-hmi#410).
                    throw new ResumeAbandonedException();
                }
                else if (waited == OperatorWaitOutcome.DoorReleased)
                {
                    // The lock reads released with nobody having pressed: the door was not latched after all, and it
                    // is open now. Nothing to pulse; the operator is at an open door, as after any pulse.
                    unlockNeeded = false;
                    cause = WireToGatePromptCause.PromptCadence;
                }
                else
                {
                    unlockNeeded = true;
                    cause = WireToGatePromptCause.OppositeReopen;
                }
            }

            if (unlockNeeded && cause == WireToGatePromptCause.OppositeReopen && !_reopenPermitted())
            {
                // The press is spent on the hold. Once a pulse is allowed again the operator is asked anew; nothing opens
                // by itself (see the remarks).
                promptRound = await HoldReopenUntilPermittedAsync(
                    physicalSlot,
                    completed,
                    promptRound,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                cause = WireToGatePromptCause.ShutOverOpposite;
                continue;
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
                // The pulse is taken under the declaration gate, so a slot fault declaration is applied either
                // before it -- and then none goes out -- or after it, never beside it (8005-agv-onboard-hmi#215).
                await _declarationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    ThrowIfDeclared(active, physicalSlot);
                    // After the progress send, not before it: that send waits on the network, and a latch
                    // arriving during it is exactly the window this check exists for. Nothing is awaited
                    // between here and the pulse.
                    ThrowIfFatalFaultLatched(reopen: pulseRequested);
                    pulseRequested = true;
                    await _ioModule.PulseUnlockAsync(slotIndex, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _declarationGate.Release();
                }

                // Both are hardware responses in milliseconds. Missing either one means the lock
                // feedback or the unlock output cannot be trusted -- decision 2, not the operator --
                // except a pressed reopen whose door reads shut again (see the remarks).
                LockerSnapshot unlocked;
                try
                {
                    unlocked = await _ioModule.WaitForLockerAsync(
                        slotIndex,
                        locker => locker.IsKnown && !locker.IsLocked,
                        _options.UnlockFeedbackTimeout,
                        _options.FeedbackStableWindow,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException) when (cause == WireToGatePromptCause.OppositeReopen)
                {
                    await WaitForShutAndResetAsync(slotIndex, cancellationToken).ConfigureAwait(false);
                    cause = WireToGatePromptCause.ReopenNotReleased;
                    continue;
                }
                catch (TimeoutException)
                {
                    throw new SlotFeedbackFailedException(UnlockNotConfirmedReason(slotIndex));
                }

                await WaitForOutputResetAsync(
                    slotIndex,
                    locker => locker.IsKnown
                        && locker.ObservedAt >= unlocked.ObservedAt
                        && locker.UnlockOutputRaw is false,
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
                : await WaitForOutputResetAsync(
                    slotIndex,
                    locker => locker.IsKnown
                        && locker.IsLocked
                        && locker.HasCargo == closed.HasCargo
                        && locker.UnlockOutputRaw is false,
                    cancellationToken).ConfigureAwait(false);
            if (outcome == SlotWaitOutcome.Reached)
            {
                return settled;
            }

            cause = WireToGatePromptCause.ShutOverOpposite;
        }
    }

    private enum OperatorWaitOutcome
    {
        /// <summary>The operator pressed 「重新开门」.</summary>
        ReopenPressed,

        /// <summary>The lock read released, held for the stable window, without a pulse.</summary>
        DoorReleased,

        /// <summary>The operator pressed 「剩下的不装」, and the run is held for the server's decision.</summary>
        RemainingBasketsSkipHeld,

        /// <summary>The operator pressed 「不装了」 in a resume over empty slots (8005-agv-onboard-hmi#410).</summary>
        ResumeAbandoned
    }

    /// <summary>
    /// Holds the slot shut over the opposite occupancy until the operator asks for it
    /// (<see cref="SubmitOperatorRequest"/>), and prompts once at once and again every
    /// <see cref="WireToGateSlotOperationExecutorOptions.OperationTimeout"/>, each a round of its own -- the prompt cadence
    /// of decision 3, which also keeps the expected-action clock of REQ-0358 running from the slot's first unlock. Returns
    /// how the wait ended and the round that follows it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What else ends it.</b> A slot that turns unreadable for the stable window is decision 2, as in the wait for a
    /// shut door. A lock that reads released for the stable window without a pulse is a door that was never latched: it
    /// is open, and the run goes back to waiting for it to be shut. A fatal fault latched while the run waits ends it at
    /// once as the latch's refusal of the reopen, rather than leaving it waiting: <c>ClearFatalFaultAsync</c> refuses
    /// while an operation is in flight, so nobody could lift the latch the wait would be waiting on. A slot fault
    /// declaration or a load cancellation cancels the run's token, which ends the wait with it.
    /// </para>
    /// <para>
    /// The request is published before the first prompt, so the screen offers the button no earlier than the executor
    /// can take it, and taken off in the same lock that takes an answer, so a second press of the same wait finds
    /// nothing.
    /// </para>
    /// </remarks>
    private async Task<(OperatorWaitOutcome Outcome, int NextRound)> WaitForOperatorRequestAsync(
        WireToGateSlotOperationCommand command,
        int physicalSlot,
        IReadOnlyList<int> completed,
        int promptRound,
        WireToGatePromptCause cause,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        ActiveOperation? active,
        CancellationToken cancellationToken)
    {
        int slotIndex = physicalSlot - 1;
        // Both callers publish the run before they get here; nothing else drives a slot.
        ArgumentNullException.ThrowIfNull(active);
        TaskCompletionSource<WireToGateOperatorSlotRequest> answer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (active)
        {
            active.OperatorRequest = new(
                new WireToGateOperatorRequestWait(active.SlotOperationAttemptId, physicalSlot, cause),
                answer,
                command,
                [.. completed]);
        }

        try
        {
            for (; ; promptRound++)
            {
                await SendProgressAsync(
                    progress,
                    new("WAITING_OPERATOR", [physicalSlot], completed, promptRound, cause),
                    cancellationToken).ConfigureAwait(false);
                using CancellationTokenSource waitCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task<WireToGateOperatorSlotRequest> pressed = answer.Task.WaitAsync(waitCts.Token);
                Task<LockerSnapshot> released = _ioModule.WaitForLockerAsync(
                    slotIndex,
                    locker => locker.IsKnown && !locker.IsLocked,
                    _options.OperationTimeout,
                    _options.FeedbackStableWindow,
                    waitCts.Token);
                Task<LockerSnapshot> unknown = _ioModule.WaitForLockerAsync(
                    slotIndex,
                    locker => !locker.IsKnown,
                    _options.OperationTimeout,
                    _options.FeedbackStableWindow,
                    waitCts.Token);
                Task latched = WaitForFatalFaultLatchAsync(waitCts.Token);
                try
                {
                    Task winner = await Task.WhenAny(pressed, released, unknown, latched).ConfigureAwait(false);
                    if (winner == pressed)
                    {
                        WireToGateOperatorSlotRequest request = await pressed.ConfigureAwait(false);
                        OperatorWaitOutcome outcome = request switch
                        {
                            WireToGateOperatorSlotRequest.RemainingBasketsSkip => OperatorWaitOutcome.RemainingBasketsSkipHeld,
                            WireToGateOperatorSlotRequest.AbandonResume => OperatorWaitOutcome.ResumeAbandoned,
                            _ => OperatorWaitOutcome.ReopenPressed
                        };
                        return (outcome, promptRound + 1);
                    }

                    if (winner == latched)
                    {
                        await latched.ConfigureAwait(false);
                        throw new FatalFaultLatchedException(reopen: true);
                    }

                    try
                    {
                        _ = await ((Task<LockerSnapshot>)winner).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // One prompt cadence passed with the door shut and locked: prompt again, same cause.
                        continue;
                    }

                    if (winner == unknown)
                    {
                        throw new InvalidDataException("SLOT_STATE_UNKNOWN");
                    }

                    return (OperatorWaitOutcome.DoorReleased, promptRound + 1);
                }
                finally
                {
                    await waitCts.CancelAsync().ConfigureAwait(false);
                    await ObserveAsync(pressed).ConfigureAwait(false);
                    await ObserveAsync(released).ConfigureAwait(false);
                    await ObserveAsync(unknown).ConfigureAwait(false);
                    await ObserveAsync(latched).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            lock (active)
            {
                if (active.OperatorRequest?.Answer == answer)
                {
                    active.OperatorRequest = null;
                }
            }
        }
    }

    /// <summary>
    /// Holds every slot shut while a press of 「剩下的不装」 waits for the server (8005-agv-onboard-hmi#388): nothing is pulsed,
    /// and the operator is told it is pending at once and again every
    /// <see cref="WireToGateSlotOperationExecutorOptions.OperationTimeout"/>, each a round of its own. Returns the last round
    /// prompted when the hold is let go; an authorization throws <see cref="RemainingBasketsSkipAuthorizedException"/>, which
    /// the run settles.
    /// </summary>
    /// <remarks>
    /// The held slot turning unreadable is decision 2 and a fatal fault latched is the latch's refusal, as in the wait for the
    /// press; a cancellation of the run's token ends this wait too. The lock letting go by itself does not end it -- nothing
    /// is pulsed either way, and the authorization, when it comes, is checked against the slots as they read then. Whatever
    /// ends the wait other than a decision takes the hold off with it, so a late decision finds nothing to act on.
    /// </remarks>
    private async Task<int> WaitForRemainingBasketsSkipDecisionAsync(
        int physicalSlot,
        IReadOnlyList<int> completed,
        int promptRound,
        Func<WireToGateOperationProgress, CancellationToken, Task>? progress,
        ActiveOperation? active,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(active);
        RemainingBasketsSkipHold hold;
        lock (active)
        {
            hold = active.SkipHold ?? throw new InvalidOperationException("执行器没有持有这次「剩下的不装」。");
        }

        int slotIndex = physicalSlot - 1;
        try
        {
            for (; ; promptRound++)
            {
                await SendProgressAsync(
                    progress,
                    new(
                        "WAITING_OPERATOR",
                        [physicalSlot],
                        completed,
                        promptRound,
                        WireToGatePromptCause.RemainingBasketsSkipPending),
                    cancellationToken).ConfigureAwait(false);
                using CancellationTokenSource waitCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task<IReadOnlyList<int>?> decided = hold.Decision.Task.WaitAsync(_options.OperationTimeout, waitCts.Token);
                Task<LockerSnapshot> unknown = _ioModule.WaitForLockerAsync(
                    slotIndex,
                    locker => !locker.IsKnown,
                    _options.OperationTimeout,
                    _options.FeedbackStableWindow,
                    waitCts.Token);
                Task latched = WaitForFatalFaultLatchAsync(waitCts.Token);
                try
                {
                    Task winner = await Task.WhenAny(decided, unknown, latched).ConfigureAwait(false);
                    if (winner == latched)
                    {
                        await latched.ConfigureAwait(false);
                        throw new FatalFaultLatchedException(reopen: true);
                    }

                    if (winner == unknown)
                    {
                        try
                        {
                            _ = await unknown.ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            continue;
                        }

                        throw new InvalidDataException("SLOT_STATE_UNKNOWN");
                    }

                    IReadOnlyList<int>? authorized;
                    try
                    {
                        authorized = await decided.ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // One prompt cadence with no answer: say again that it is pending.
                        continue;
                    }

                    if (authorized is null)
                    {
                        return promptRound;
                    }

                    throw new RemainingBasketsSkipAuthorizedException(authorized);
                }
                finally
                {
                    await waitCts.CancelAsync().ConfigureAwait(false);
                    await ObserveAsync(decided).ConfigureAwait(false);
                    await ObserveAsync(unknown).ConfigureAwait(false);
                    await ObserveAsync(latched).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            lock (active)
            {
                if (ReferenceEquals(active.SkipHold, hold))
                {
                    active.SkipHold = null;
                }
            }
        }
    }

    /// <summary>Completes once the fatal-fault latch is set, polling it as the reopen permission is polled.</summary>
    private async Task WaitForFatalFaultLatchAsync(CancellationToken cancellationToken)
    {
        while (!_fatalFaultLatched())
        {
            await Task.Delay(ReopenPermissionPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After a pressed reopen whose lock did not hold released: the slot read, locked and its output reset, held for the
    /// stable window, within <see cref="WireToGateSlotOperationExecutorOptions.UnlockOutputResetTimeout"/> -- the door
    /// shut again over an occupancy that can be read. Anything else is decision 2, under the reason the reading at that
    /// moment gives.
    /// </summary>
    private async Task WaitForShutAndResetAsync(int slotIndex, CancellationToken cancellationToken)
    {
        try
        {
            await _ioModule.WaitForLockerAsync(
                slotIndex,
                locker => locker.IsKnown && locker.IsLocked && locker.UnlockOutputRaw is false,
                _options.UnlockOutputResetTimeout,
                _options.FeedbackStableWindow,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new SlotFeedbackFailedException(UnlockNotConfirmedReason(slotIndex));
        }
    }

    /// <summary>
    /// The wait for the unlock output to fall back, which is decision 2 when it does not: <c>UNLOCK_OUTPUT_NOT_RESET</c>,
    /// or <c>SLOT_STATE_UNKNOWN</c> when the slot cannot be read at that moment.
    /// </summary>
    private async Task<LockerSnapshot> WaitForOutputResetAsync(
        int slotIndex,
        Func<LockerSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _ioModule.WaitForLockerAsync(
                slotIndex,
                predicate,
                _options.UnlockOutputResetTimeout,
                _options.FeedbackStableWindow,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LockerSnapshot now = ReadLocker(_ioModule.CurrentSnapshot, slotIndex);
            throw new SlotFeedbackFailedException(now.IsKnown ? "UNLOCK_OUTPUT_NOT_RESET" : "SLOT_STATE_UNKNOWN");
        }
    }

    /// <summary>
    /// Which decision 2 condition a lock that did not settle after a pulse is, read off the slot as it is now: unreadable
    /// is <c>SLOT_STATE_UNKNOWN</c>, an output still energised is <c>UNLOCK_OUTPUT_NOT_RESET</c>, and a readable slot
    /// whose output has fallen back but whose lock would not hold one state -- or would not release on the first pulse --
    /// is <c>SLOT_INOPERABLE</c>. None of them is <c>ACTION_NOT_ALLOWED_IN_STATE</c>, which is what every timeout was
    /// reported as until 8005-agv-onboard-hmi#385 and which read as "a state that does not allow it" on both field
    /// occasions.
    /// </summary>
    private string UnlockNotConfirmedReason(int slotIndex)
    {
        LockerSnapshot now = ReadLocker(_ioModule.CurrentSnapshot, slotIndex);
        return !now.IsKnown
            ? "SLOT_STATE_UNKNOWN"
            : now.UnlockOutputRaw is not false
                ? "UNLOCK_OUTPUT_NOT_RESET"
                : "SLOT_INOPERABLE";
    }

    /// <summary>
    /// Waits, without pulsing, until the vehicle safety fact allows the reopen. Prompts once at once
    /// and again every <see cref="WireToGateSlotOperationExecutorOptions.OperationTimeout"/>, each as a
    /// round of its own. Returns the last round it prompted, the one the wait for the next press follows.
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

        return promptRound;
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
        CancellationToken cancellationToken,
        WireToGateRecoveryVectorContext? settleVector = null)
    {
        // Merged under the journal's lock against the newest state, not against the copy this run read
        // when it started. This checkpoint owns six fields -- the attempt, the checkpoint itself, the
        // active unlock set, the operation context, the completed slots and the slot results -- and
        // every other field is taken as the journal holds it now. Written from the older copy instead,
        // a checkpoint silently undid whatever landed since: a recovery session released by a CLOSED
        // snapshot came back, a forced recovery generation went back down, a pending result recorded
        // in between disappeared (onboard-hmi#136 point 2). Only PendingLoadCancellation was read
        // afresh, and only because onboard-hmi#78 was bitten by it; that rule is kept below.
        //
        // The one exception is a new operation's first write over a vector it settles first
        // (<paramref name="settleVector"/>, 8005-agv-onboard-hmi#267): that vector and its recovery session are taken off
        // in the same step, so they are never written beside the new attempt.
        await _journal.UpdateRecoveryStateAsync(
            journalled =>
            {
                WireToGateRecoveryState current = WithoutSettledVector(journalled, settleVector);
                return new WireToGateRecoveryState(
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
                    LoadsOnBoard = current.LoadsOnBoard,
                    // The operator's unanswered load cancellation for this attempt stays; one left over
                    // from another attempt goes, as it always did (onboard-hmi#78).
                    PendingLoadCancellation = string.Equals(
                        current.PendingLoadCancellation?.SlotOperationAttemptId,
                        context.SlotOperationAttemptId,
                        StringComparison.Ordinal)
                        ? current.PendingLoadCancellation
                        : existingState.PendingLoadCancellation,
                    ForcedIsolation = current.ForcedIsolation,
                    RepairRelease = current.RepairRelease,
                    // A declaration belongs to its attempt: kept while this attempt is the one checkpointed, never
                    // carried into another (8005-agv-onboard-hmi#215).
                    SlotFaultDeclaration = string.Equals(
                        current.SlotFaultDeclaration?.SlotOperationAttemptId,
                        context.SlotOperationAttemptId,
                        StringComparison.Ordinal)
                        ? current.SlotFaultDeclaration
                        : null,
                    // The operator's 「剩下的不装」 belongs to its attempt, like a declaration (8005-agv-onboard-hmi#388).
                    RemainingBasketsSkip = string.Equals(
                        current.RemainingBasketsSkip?.SlotOperationAttemptId,
                        context.SlotOperationAttemptId,
                        StringComparison.Ordinal)
                        ? current.RemainingBasketsSkip
                        : null,
                    // Written as null on purpose, not left out by accident (8005-agv-onboard-hmi#278): a checkpoint of
                    // this attempt means this executor is running it again -- a new operation, or a resume of the very
                    // attempt a forgotten vector held -- and the result it reaches is this run's own account, to be
                    // recorded as a settlement. A marker carried over would leave that result recorded as a share only.
                    TakenOverSlotOperationAttemptId = null
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="state"/> without <paramref name="vector"/>, and without its recovery session when the journal still
    /// names that session -- the fields an acknowledged non-<c>COMPLETED</c> result forgets (the business service's
    /// <c>ForgetSettledVector</c>). Unchanged when <paramref name="vector"/> is <c>null</c> or no longer the one on file.
    /// </summary>
    private static WireToGateRecoveryState WithoutSettledVector(
        WireToGateRecoveryState state,
        WireToGateRecoveryVectorContext? vector)
    {
        if (vector is null
            || state.RecoveryVector is not { } onFile
            || !string.Equals(onFile.VectorType, vector.VectorType, StringComparison.Ordinal)
            || !string.Equals(onFile.PrimaryId, vector.PrimaryId, StringComparison.Ordinal))
        {
            return state;
        }

        WireToGateRecoveryState cleared = state with
        {
            RecoveryVector = null,
            RecoveryResultObservedAt = null
        };
        return vector.ExceptionRecoverySessionId is { } session
            && string.Equals(state.ExceptionRecoverySessionId, session, StringComparison.Ordinal)
                ? cleared with
                {
                    ExceptionRecoverySessionId = null,
                    RecoveryActionId = null,
                    RecoverySessionRequestId = null,
                    RecoveryActionRequestId = null,
                    RecoveryReason = null,
                    RecoveryOperatorId = null,
                    RecoveryOperatorVerifiedAt = null
                }
                : cleared;
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

    /// <summary>
    /// An UNKNOWN result for <paramref name="command"/> read off the live IO alone, for a caller whose execution
    /// broke off where it cannot tell what was done -- an exception out of the journal, say -- and that still owes
    /// the server an answer (onboard-hmi#233). Reads no journal and writes nothing: the journal may be the very
    /// thing that failed.
    /// </summary>
    /// <remarks>
    /// Every target slot is UNKNOWN with <c>SLOT_STATE_UNKNOWN</c>, and the checkpoint is
    /// <c>ACTIVE_UNLOCK_SET</c>: a door of this attempt may be open, which is the most a caller that does not know
    /// how far it got can say. The three physical fields are what the IO reads, or UNKNOWN when it cannot be read.
    /// </remarks>
    public WireToGateOperationExecutionResult CreateUnknownResultFromLiveIo(WireToGateSlotOperationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        WireToGateSlotExecutionResult[] results =
        [
            .. command.Slots.Select(slot =>
                CreateSlotResult(ReadLocker(snapshot, slot - 1), "UNKNOWN", ["SLOT_STATE_UNKNOWN"]))
        ];
        return CreateResult(command, "UNKNOWN", results, WireToGateRecoveryCheckpoint.ActiveUnlockSet);
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
    /// A hardware response after a pulse did not come -- the lock did not settle, or the output did not reset -- and the
    /// slot's reading says which decision 2 condition that is. The message is the reason code (8005-agv-onboard-hmi#385).
    /// </summary>
    private sealed class SlotFeedbackFailedException(string reasonCode) : Exception(reasonCode);

    /// <summary>
    /// The server authorized the 「剩下的不装」 the run was held for, naming <see cref="AuthorizedSlots"/>
    /// (8005-agv-onboard-hmi#388); the run settles it in <see cref="SettleRemainingBasketsSkippedAsync"/>.
    /// </summary>
    private sealed class RemainingBasketsSkipAuthorizedException(IReadOnlyList<int> authorizedSlots)
        : Exception("REMAINING_BASKETS_SKIP_AUTHORIZED")
    {
        public IReadOnlyList<int> AuthorizedSlots { get; } = authorizedSlots;
    }

    /// <summary>The operator's 「不装了」 ended the resume's wait (8005-agv-onboard-hmi#410); see <see cref="SettleResumeAbandonedAsync"/>.</summary>
    private sealed class ResumeAbandonedException() : Exception("RESUME_ABANDONED");

    /// <summary>
    /// The code a latch refusal carries on the wire, in a slot result's <c>reasonCodes</c>.
    /// </summary>
    /// <remarks>
    /// Protocol 3.0.0 registered <c>ONBOARD_FATAL_FAULT_LATCHED</c>, and its <c>allowedMessageTypes</c> name the
    /// five results that carry <c>slotResults</c> -- <c>OperationResult</c>, <c>LoadCorrectionResult</c>,
    /// <c>LoadCancellationResult</c>, <c>LoadCompensationResult</c>, <c>FaultCargoRecoveryResult</c> -- beside the
    /// three safety messages (8005-agv-onboard-hmi#214). Until then this borrowed <c>VEHICLE_NOT_READY</c>
    /// (onboard-hmi#191). It has the same value as <c>WireToGateSafetyEvaluator.FatalFaultLatchedReason</c> but is a
    /// separate one on purpose: that one says "do not depart" in a safety summary, this one says "this slot was
    /// not pulsed" in a slot result, and the business service recognizes a latch refusal by this one alone.
    /// </remarks>
    public const string FatalFaultLatchedReason = "ONBOARD_FATAL_FAULT_LATCHED";

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
        SlotFeedbackFailedException feedback => feedback.Message,
        // Every wait on a hardware response turns its own timeout into SlotFeedbackFailedException with the reason the
        // reading gives, so none should arrive here. One that does is a slot this run could not read in time; it is not
        // ACTION_NOT_ALLOWED_IN_STATE, which is what it used to be called (8005-agv-onboard-hmi#385).
        TimeoutException => "SLOT_STATE_UNKNOWN",
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
