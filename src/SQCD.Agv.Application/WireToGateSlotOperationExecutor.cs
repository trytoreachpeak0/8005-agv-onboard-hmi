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
                ActiveUnlockSlots = []
            },
            cancellationToken).ConfigureAwait(false);
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
        string? precheckFailure = ValidateBeforeOperation(initial, command);
        if (precheckFailure is not null)
        {
            return CreateRejectedResult(command, precheckFailure, started);
        }

        await _journal.WriteRecoveryStateAsync(
            new WireToGateRecoveryState(
                command.SlotOperationAttemptId,
                WireToGateRecoveryCheckpoint.Prepared,
                [],
                0,
                []),
            cancellationToken).ConfigureAwait(false);
        await SendProgressAsync(progress, "PREPARING", [], [], cancellationToken).ConfigureAwait(false);

        List<int> completed = [];
        List<WireToGateSlotExecutionResult> results = [];
        DateTimeOffset deadline = started + _options.OperationTimeout;
        foreach (int physicalSlot in command.Slots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int slotIndex = physicalSlot - 1;
            await _journal.WriteRecoveryStateAsync(
                new WireToGateRecoveryState(
                    command.SlotOperationAttemptId,
                    WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                    [physicalSlot],
                    0,
                    []),
                cancellationToken).ConfigureAwait(false);
            await SendProgressAsync(progress, "UNLOCKING", [physicalSlot], completed, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await EnsureRemainingAsync(deadline, cancellationToken).ConfigureAwait(false);
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
                    [],
                    completed,
                    cancellationToken).ConfigureAwait(false);
                bool expectedOccupied = command.ExpectedOccupied;
                LockerSnapshot completedLocker = await _ioModule.WaitForLockerAsync(
                    slotIndex,
                    locker => locker.IsKnown
                        && locker.IsLocked
                        && locker.HasCargo == expectedOccupied,
                    MinTimeout(_options.OperationTimeout, GetRemaining(deadline)),
                    _options.FeedbackStableWindow,
                    cancellationToken).ConfigureAwait(false);

                WireToGateSlotExecutionResult result = CreateSlotResult(
                    completedLocker,
                    "COMPLETED",
                    []);
                results.Add(result);
                completed.Add(physicalSlot);
                await _journal.WriteRecoveryStateAsync(
                    new WireToGateRecoveryState(
                        command.SlotOperationAttemptId,
                        WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                        [],
                        0,
                        []),
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
                results.Add(CreateSlotResult(latest, "UNKNOWN", [reason]));
                foreach (int notStarted in command.Slots.Where(slot => !results.Any(result => result.SlotNo == slot)))
                {
                    results.Add(new WireToGateSlotExecutionResult(
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
                await _journal.WriteRecoveryStateAsync(
                    new WireToGateRecoveryState(
                        command.SlotOperationAttemptId,
                        failureCheckpoint,
                        failureActiveSlots,
                        0,
                        []),
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

        await _journal.WriteRecoveryStateAsync(
            new WireToGateRecoveryState(
                command.SlotOperationAttemptId,
                WireToGateRecoveryCheckpoint.SafeFinishReached,
                [],
                0,
                []),
            cancellationToken).ConfigureAwait(false);
        await SendProgressAsync(progress, "SAFE_FINISH", [], completed, cancellationToken).ConfigureAwait(false);
        return CreateResult(command, "COMPLETED", results, WireToGateRecoveryCheckpoint.SafeFinishReached);
    }

    private string? ValidateBeforeOperation(IoSnapshot snapshot, WireToGateSlotOperationCommand command)
    {
        if (!snapshot.IsConnected || !SafetyRules.IsSnapshotFresh(snapshot, _clock.Now, _options.IoSnapshotMaxAge))
        {
            return "SLOT_STATE_UNKNOWN";
        }

        foreach (int physicalSlot in command.Slots)
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
                return command.ExpectedOccupied ? "SLOT_NOT_EMPTY" : "SLOT_HAS_NO_CARGO";
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

    private async Task EnsureRemainingAsync(DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        if (GetRemaining(deadline) <= TimeSpan.Zero)
        {
            throw new TimeoutException("WIRE_TO_GATE操作超时。");
        }

        await Task.CompletedTask.ConfigureAwait(false);
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
}
