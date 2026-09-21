namespace SQCD.Agv.Core;

/// <summary>What a failed operator-initiated UI command means for the vehicle.</summary>
public enum OnboardCommandFailureKind
{
    /// <summary>
    /// Not a failure. The controller cancels the running flow when it latches a fault or the process
    /// is shutting down; reporting that as a new UI error would latch a second time over the first.
    /// </summary>
    ControlledCancellation,

    /// <summary>
    /// A rule refused this press. The operator is told why and can act again; nothing is latched.
    /// </summary>
    OperatorRejection,

    /// <summary>
    /// The press failed for a reason that leaves this machine's own state in doubt. The vehicle
    /// latches a fatal safety fault.
    /// </summary>
    FatalFault
}

/// <summary>Whether a latched fatal fault can be lifted on this machine, or only by a restart.</summary>
public enum FatalFaultClearance
{
    /// <summary>
    /// Maintenance can lift it here, after the physical safety review in
    /// <c>OnboardController.ClearFatalFaultAsync</c> passes.
    /// </summary>
    ClearableBySafetyReview,

    /// <summary>
    /// Nothing on this HMI lifts it. The process must be restarted, because the fault means this
    /// process's own state can no longer be trusted to judge that review.
    /// </summary>
    TerminalUntilRestart
}

/// <summary>
/// The two registries behind 8005-agv-onboard-hmi#171. Both answer a question whose wrong default
/// stopped a vehicle for three hours on <c>agv01</c> on 2026-09-16: an operator scanned a sublot that
/// was not on the station worklist, and the HMI latched a fatal safety fault that nothing could lift.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registry one</b> (<see cref="Classify"/>) answers: <i>an operator-initiated UI command failed --
/// does the vehicle latch a fatal safety fault?</i> It keys off the <b>error code</b> the failure
/// carries, never the exception type. Exception type cannot carry this decision here:
/// <c>RECOVERY_OPERATION_CONTEXT_MISSING</c> is thrown from eight sites as three different exception
/// types, and <c>SUBLOT_NOT_IN_WORKLIST</c> shares <see cref="InvalidOperationException"/> with
/// <c>RECOVERY_SCOPE_MISMATCH</c>. A type-based rule would also go silently wrong the next time
/// somebody adds a business refusal -- which is exactly the failure this ticket is about.
/// </para>
/// <para>
/// <b>Registry two</b> (<see cref="Clearance"/>) answers: <i>a fatal fault is latched -- can
/// maintenance lift it here?</i> Before this ticket the answer was "never": <c>_fatalFault</c> was
/// written once and never cleared, so the only way out was restarting the process, and the HMI has no
/// entry for that.
/// </para>
/// <para>
/// <b>Both registries are guarded at the source level</b> by
/// <c>LocalFailureCodeRegistryArchitectureTests</c>: every <c>throw new …Exception("CODE")</c> under
/// <c>src/</c> must appear in registry one, and every <c>EnterFatalFault("CODE", …)</c> call site must
/// appear in registry two. Adding either without registering it fails that test. That is the whole
/// point -- the author of the next business refusal has to state which kind it is, rather than
/// inheriting "latch the vehicle" by default.
/// </para>
/// <para>
/// <b>They are total over literal throw sites, which is narrower than total.</b> A code carried in a
/// variable is invisible to a source scan, and <b>there are six such sites under <c>src/</c></b>, not
/// one as this paragraph first said (审查，判据路条目 15):
/// <c>OnboardController.ThrowIfAuthoritativeJourneyNotReady</c> (the journey sublot error),
/// <c>WireToGateRecoveryVectorExecutor</c> and <c>WireToGateSlotOperationExecutor</c>'s
/// <c>otherDoor</c> refusals, <c>WireToGateSlotOperationExecutor</c>'s precheck failure, and
/// <c>WireToGateSessionClient</c>'s two <c>ProtocolProblem</c>/<c>SessionRejected</c> rethrows.
/// (Two further sites forward an inner exception's message, so their codes were already scanned at
/// the site that threw them.)
/// <para>
/// Today every code they carry happens to be registered, so there is no live consequence -- but
/// nothing reports it when one of them drifts. Say "every literal throw site", never "every code".
/// </para>
/// </para>
/// <para>
/// <b>Unregistered still means latch.</b> <see cref="Classify"/> falls back to
/// <see cref="OnboardCommandFailureKind.FatalFault"/> so a code that somehow escapes the gate fails
/// safe rather than silently becoming a dismissible message.
/// </para>
/// <para>
/// <b>This is not the same table as <see cref="OnboardCommandRejectionText"/>, and conflating the two
/// is what went wrong in this ticket's first version.</b> This one decides <i>latch or not</i> and is
/// consulted only where a failure reaches the UI command error handler -- on v2 that is the sublot
/// scan and nothing else, because every recovery entry catches its own exceptions inside
/// <c>WireToGateBusinessService</c>. The wording table decides <i>what the operator reads</i> and is
/// consulted from both places. So a code can be live in one table and unreachable in the other; the
/// per-group comments below say which.
/// </para>
/// </remarks>
public static class OnboardFailureClassification
{
    /// <summary>
    /// Codes that mean "a rule said no to this press". Every one of them leaves the doors, the IO
    /// module and this process exactly as they were: nothing was written, so there is nothing to
    /// distrust afterwards. They are shown to the operator and the vehicle keeps running.
    /// </summary>
    private static readonly HashSet<string> OperatorRejections = new(StringComparer.Ordinal)
    {
        // The station worklist refused the scan. This is the defect's origin: one operator mis-scan.
        "SUBLOT_NOT_IN_WORKLIST",
        "LOAD_CANCELLATION_IN_PROGRESS",

        // Scanning while a fatal fault stands. An operator rejection on purpose: the vehicle is
        // already latched, and latching it a second time would only overwrite the banner that says
        // why. What the operator needs here is the sentence telling them the entry is off until
        // maintenance clears it.
        "FATAL_FAULT_LATCHED",

        // Not ready yet -- the session, the journey, the journal or the vehicle. Waiting fixes these,
        // and the controller already publishes its own operator wording for the first two.
        "WIRE_TO_GATE_NOT_READY",
        "WIRE_TO_GATE_JOURNEY_NOT_READY",
        "WIRE_TO_GATE_JOURNAL_NOT_READY",
        "VEHICLE_NOT_READY",
        // The vehicle is not stopped, so scanning waits (8005-agv-onboard-hmi#177). Nothing was sent and the
        // entry request stays open; a stop brings the entry back.
        "VEHICLE_NOT_STOPPED",

        // ---------------------------------------------------------------------------------------
        // The recovery-path codes below are NOT reachable from Classify today, and saying otherwise
        // is what the first version of this registry got wrong. Every recovery entry
        // (RunRecoveryRequestAsync, RequestResumeAfterRepairAsync, RunManualChargingReturnAsync)
        // catches IOException / TimeoutException / InvalidOperationException / InvalidDataException
        // itself and publishes RECOVERY_BLOCKED, so none of them ever reaches the UI command error
        // handler. They are registered here as the answer to "if that catch is ever narrowed or
        // removed, what should happen" -- a business refusal, not a latched vehicle.
        //
        // They ARE live in OnboardCommandRejectionText: since 8005-agv-onboard-hmi#171 those catches
        // render the code through it instead of pasting the bare code into a sentence.
        // ---------------------------------------------------------------------------------------

        // Missing or mismatched operator identity and recovery credentials. A configuration or
        // staffing answer, not a safety one.
        "WIRE_TO_GATE_OPERATOR_NOT_READY",
        "RECOVERY_AUTHENTICATION_REQUIRED",
        "RECOVERY_OPERATOR_MISMATCH",
        "FORCED_RECOVERY_NOT_AUTHORIZED",

        // The recovery entry was pressed at a moment that does not accept it. The press did nothing.
        "RECOVERY_SESSION_NOT_READY",
        "RECOVERY_SESSION_STATE_PENDING",
        "RECOVERY_ACTION_ALREADY_SELECTED",
        "RECOVERY_REASON_REQUIRED",
        "RECOVERY_OPERATION_CONTEXT_MISSING",
        "LOAD_CORRECTION_OPERATION_NOT_AVAILABLE",

        // The hardware-recovery record is incomplete, not required, or cannot be judged because slot
        // state is unreadable right now. Unreadable IO has its own alarm path (IO_STATE_UNKNOWN,
        // IO_SNAPSHOT_STALE) which already blocks scanning and departure; refusing this press does
        // not need a second, permanent one.
        "HARDWARE_RECOVERY_NOT_REQUIRED",
        "HARDWARE_RECOVERY_OBSERVATIONS_REQUIRED",
        "HARDWARE_RECOVERY_RECORD_REQUIRED",
        "SLOT_STATE_UNKNOWN"
    };

    /// <summary>
    /// Every other local code. Each one means some piece of state disagrees with another piece --
    /// this machine's journal against the server's snapshot, a persisted revision against the one on
    /// the wire, an envelope against its schema. After one of those, what this process believes about
    /// the doors is exactly what is in question, so it latches.
    /// </summary>
    /// <remarks>
    /// Codes in the second group below are thrown from paths no UI command reaches today (the durable
    /// outbox, the protocol reader, the slot executor's own preflight). They are registered as
    /// <see cref="OnboardCommandFailureKind.FatalFault"/> as the safe default, not because each was
    /// weighed as an operator-facing refusal. If one of them ever does surface on a button, judge it
    /// then rather than reading this entry as that judgement already made.
    /// </remarks>
    private static readonly HashSet<string> FatalFaults = new(StringComparer.Ordinal)
    {
        // Recovery state that does not line up with what the server or the journal says.
        "RECOVERY_COMMAND_HASH_MISMATCH",
        "RECOVERY_COMMAND_INVALID",
        "RECOVERY_OPERATOR_CONTEXT_MISSING",
        "RECOVERY_RESPONSE_SCOPE_MISMATCH",
        "RECOVERY_SCOPE_MISMATCH",
        "RECOVERY_SESSION_REQUEST_MISSING",
        "RECOVERY_SESSION_SCOPE_MISMATCH",
        "RECOVERY_STATE_MISMATCH",
        "RECOVERY_STATE_NOT_READ",
        "RECOVERY_VECTOR_CONFLICT",
        "RECOVERY_VECTOR_CONTEXT_MISSING",
        "RECOVERY_VECTOR_TYPE_INVALID",

        // Not on a UI command path today; registered as the safe default (see remarks).
        "ACTIVE_UNLOCK_SET_MORE_THAN_ONE_SLOT",
        "AGV_ID_MISMATCH",
        "BUSINESS_ID_CONTENT_CONFLICT",
        "CONTENT_HASH_MISMATCH",
        "CORRELATION_INVALID",
        "DURABLE_OUTBOX_CONTENT_MISMATCH",
        "DURABLE_OUTBOX_MISSING",
        "DURABLE_OUTBOX_REBIND_CONFLICT",
        "DURABLE_OUTBOX_REBIND_IDENTITY_CONFLICT",
        "DURABLE_OUTBOX_ROW_MISSING",
        "HANDSHAKE_SEQUENCE_INVALID",
        "MESSAGE_ID_CONTENT_CONFLICT",
        "PERSISTED_SNAPSHOT_REVISION_MISMATCH",
        "PROTOCOL_ENVELOPE_INVALID",
        "PROTOCOL_PAYLOAD_INVALID",
        "PROTOCOL_RELEASE_IDENTITY_MISMATCH",
        "PROTOCOL_SCHEMA_INVALID",
        "SLOT_INOPERABLE",
        "SLOT_OPERATION_ALREADY_STARTED",
        "SLOT_OPERATION_CONFLICT",
        "SLOT_SET_INVALID",
        "SNAPSHOT_REVISION_CONTENT_CONFLICT",
        "SNAPSHOT_REVISION_REGRESSION",
        "STALE_SESSION_GENERATION"
    };

    /// <summary>
    /// Every code <c>OnboardController.EnterFatalFault</c> is called with, and whether maintenance can
    /// lift it on this machine.
    /// </summary>
    private static readonly Dictionary<string, FatalFaultClearance> FatalFaultCodes =
        new(StringComparer.Ordinal)
        {
            // One UI command failed for a reason this process understands. The process itself is
            // intact, so the physical review in ClearFatalFaultAsync is a review it can be trusted to
            // run: doors all locked, every unlock output reset, snapshot fresh.
            ["UI_COMMAND_FAILED"] = FatalFaultClearance.ClearableBySafetyReview,

            // An exception nobody caught reached the dispatcher. Where it came from is unknown, so
            // the state this process would review the doors against is unknown too -- including
            // whether the latch itself is still being honoured. Only a restart clears this.
            ["UNHANDLED_UI_ERROR"] = FatalFaultClearance.TerminalUntilRestart
        };

    /// <summary>Registry one, as data, for the architecture gate.</summary>
    public static IReadOnlyCollection<string> RegisteredLocalFailureCodes =>
        [.. OperatorRejections, .. FatalFaults];

    /// <summary>Registry two, as data, for the architecture gate.</summary>
    public static IReadOnlyCollection<string> RegisteredFatalFaultCodes => [.. FatalFaultCodes.Keys];

    /// <summary>True when <paramref name="code"/> is registered in registry one.</summary>
    public static bool IsRegisteredLocalFailureCode(string code) =>
        OperatorRejections.Contains(code) || FatalFaults.Contains(code);

    /// <summary>True when <paramref name="code"/> is registered in registry two.</summary>
    public static bool IsRegisteredFatalFaultCode(string code) => FatalFaultCodes.ContainsKey(code);

    /// <summary>
    /// Whether a failed operator-initiated UI command latches a fatal safety fault. Reads the error
    /// code the failure carries -- the exception's message, which every local refusal here uses as its
    /// code -- and never the exception type.
    /// </summary>
    /// <remarks>
    /// <b><see cref="OperationCanceledException"/> is the one thing read by type here, and that is not
    /// a hole in "never by type".</b> A cancellation carries no error code to look up — its message is
    /// framework text or this codebase's own Chinese sentence — because it is not a refusal at all:
    /// the controller cancels the running flow when it latches a fault or the process shuts down.
    /// Classifying it by code would mean falling through to <see cref="OnboardCommandFailureKind.FatalFault"/>
    /// and latching a second time over the first, overwriting the banner that says why.
    /// The rule this registry exists to enforce is "the kind of a refusal is not inferable from its
    /// exception type"; a cancellation is not a refusal, so it is outside that rule rather than an
    /// exception to it.
    /// </remarks>
    public static OnboardCommandFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            // TaskCanceledException 派生自 OperationCanceledException，而 HttpClient 超时抛的正是
            // 它（ControlServerVehicleSafetySignalProvider 走 HttpClient）。一次网络超时不是本控制器
            // 发出的受控取消，把它静默吞掉是过松的一侧——改前会锁存（过严），不能换成完全没痕迹
            // （8005-agv-onboard-hmi#171 审查，产品路发现 6）。
            //
            // 判据是「这次取消是不是我们自己要求的」：带着一个已被请求取消的 token 才算。
            OperationCanceledException cancelled
                when cancelled.CancellationToken.IsCancellationRequested =>
                OnboardCommandFailureKind.ControlledCancellation,
            TaskCanceledException => OnboardCommandFailureKind.FatalFault,
            _ when OperatorRejections.Contains(exception.Message) =>
                OnboardCommandFailureKind.OperatorRejection,
            _ => OnboardCommandFailureKind.FatalFault
        };
    }

    /// <summary>
    /// Whether a latched fatal fault can be lifted here. An unregistered code cannot: a fault whose
    /// origin nobody wrote down is not one to let an operator dismiss.
    /// </summary>
    public static FatalFaultClearance Clearance(string fatalFaultCode) =>
        FatalFaultCodes.TryGetValue(fatalFaultCode, out FatalFaultClearance clearance)
            ? clearance
            : FatalFaultClearance.TerminalUntilRestart;
}
