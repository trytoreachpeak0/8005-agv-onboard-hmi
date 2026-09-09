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
    /// <summary>
    /// 一轮操作员等待的结论。Reached 非空表示已达成目标态；为空且 OppositeObserved
    /// 为真表示门已关好但货物状态与预期相反，需要重新开锁；两者都为空/假表示这一轮
    /// 只是提示节拍到期，仓门还开着。
    /// </summary>
    private readonly record struct SlotWaitResult(LockerSnapshot? Reached, bool OppositeObserved);

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
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken, Task>? progress,
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
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken, Task>? progress,
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
                LastCompletedLoadOperationContext = state.OperationContext?.OperationType == OperationType.Load
                    ? state.OperationContext
                    : state.LastCompletedLoadOperationContext
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
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken, Task>? progress,
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
        await SendProgressAsync(progress, "PREPARING", [], [], 0, cancellationToken).ConfigureAwait(false);

        return await ExecuteRemainingSlotsAsync(
            command,
            context,
            WireToGateRecoveryState.Empty,
            completed,
            results,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WireToGateOperationExecutionResult> ResumeExclusiveAsync(
        WireToGateSlotOperationCommand command,
        WireToGateRecoveryState state,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
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
        await SendProgressAsync(progress, "PREPARING", [], completed, 0, cancellationToken)
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
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken, Task>? progress,
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
            // 恢复日志在这里写一次就够。重开第 1 次与第 17 次要回答的问题完全相同
            // ——该仓在活动集合里、门可能开着——写进去的字节一个都不会变，
            // 所以重开循环内部不再重写（ADR-cross-0058 决策 1）。
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
                await SendProgressAsync(progress, "VERIFYING", [], completed, 0, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException)
            {
                // 能走到这里的只剩 ADR-cross-0058 决策 2 认定的三件事：仓位状态未知、
                // 锁闭反馈无效、开锁输出无法确认复位。操作员不放料、不取料、不关门
                // 都到不了这里——那三种在 DriveSlotToTargetStateAsync 里循环等着。
                // 一次读快照，失败仓与未开始仓共用同一份读数，避免两次读到不同状态。
                IoSnapshot failureSnapshot = _ioModule.CurrentSnapshot;
                string reason = MapFailureReason(exception);
                UpsertResult(
                    results,
                    CreateSlotResult(TryGetLocker(failureSnapshot, slotIndex), "UNKNOWN", [reason]));
                foreach (int notStarted in command.Slots
                    .Where(slot => slot != physicalSlot && !completed.Contains(slot)))
                {
                    // 决策 6：从未开启的仓位门是关的、锁是闭的、开锁 DO 从未置位，
                    // 三个字段都读得到，填 UNKNOWN 恰恰是在陈述不存在的不确定性。
                    // reasonCodes 留空——「为什么没开始」由 overallOutcome 承载，不属于仓位。
                    UpsertResult(
                        results,
                        CreateSlotResult(TryGetLocker(failureSnapshot, notStarted - 1), "NOT_STARTED", []));
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
                        safeFinish ? "SAFE_FINISH" : "PAUSED",
                        failureActiveSlots,
                        completed,
                        0,
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
        await SendProgressAsync(progress, "SAFE_FINISH", [], completed, 0, cancellationToken)
            .ConfigureAwait(false);
        return CreateResult(command, "COMPLETED", results, WireToGateRecoveryCheckpoint.SafeFinishReached);
    }

    /// <summary>
    /// 把一个仓位开到目标态。光幕稳定读到与预期相反的状态，说明操作员没有放入或
    /// 取出货物——自动重新输出开锁脉冲并提示，不判失败、不进恢复、不设次数上限
    /// （ADR-cross-0058 决策 1）。仓门根本没关时两个条件都不成立，等待继续，
    /// 这是第三条出路，不需要为它单独写判据。
    /// </summary>
    private async Task<LockerSnapshot> DriveSlotToTargetStateAsync(
        WireToGateSlotOperationCommand command,
        int physicalSlot,
        IReadOnlyList<int> completed,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        int slotIndex = physicalSlot - 1;
        bool unlockNeeded = true;
        for (int promptRound = 0; ; promptRound++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (unlockNeeded)
            {
                await SendProgressAsync(
                    progress,
                    "UNLOCKING",
                    [physicalSlot],
                    completed,
                    promptRound,
                    cancellationToken).ConfigureAwait(false);
                await _ioModule.PulseUnlockAsync(slotIndex, cancellationToken).ConfigureAwait(false);
                // 这两个等的是硬件毫秒级响应，等不到就是 IO 真的不可信，
                // 属决策 2 的进恢复路径，不受提示节拍影响。
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
                "WAITING_OPERATOR",
                [physicalSlot],
                completed,
                promptRound,
                cancellationToken).ConfigureAwait(false);
            SlotWaitResult wait = await WaitForTargetOrOppositeAsync(
                slotIndex,
                command.ExpectedOccupied,
                cancellationToken).ConfigureAwait(false);
            if (wait.Reached is { } reached)
            {
                return reached;
            }

            // 相反状态说明门已关好、货物没动，要重新开锁；提示节拍到期时门还开着，
            // 只是再提示一次，重复脉冲对一把已经开着的锁没有意义。
            unlockNeeded = wait.OppositeObserved;
        }
    }

    /// <summary>
    /// 并发等待两个互斥条件：达成态与相反态。两者都要求锁已闭，所以「仓门根本没关」
    /// 时两个都不成立，自然落到超时那条出路——那不是失败，是该再提示一次操作员
    /// （ADR-cross-0058 决策 5：OperationTimeout 退化成提示节拍，不再判死）。
    /// </summary>
    private async Task<SlotWaitResult> WaitForTargetOrOppositeAsync(
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
        try
        {
            Task<LockerSnapshot> winner = await Task.WhenAny(reachedTask, oppositeTask)
                .ConfigureAwait(false);
            LockerSnapshot locker = await winner.ConfigureAwait(false);
            return ReferenceEquals(winner, reachedTask)
                ? new SlotWaitResult(locker, false)
                : new SlotWaitResult(null, true);
        }
        catch (TimeoutException)
        {
            // 一个 OperationTimeout 之内两个条件都没有稳定成立：仓门还开着，
            // 操作员还没有动作。不判失败，回去再提示一次。
            return new SlotWaitResult(null, false);
        }
        finally
        {
            waitCts.Cancel();
            await ObserveAsync(reachedTask).ConfigureAwait(false);
            await ObserveAsync(oppositeTask).ConfigureAwait(false);
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
            // 本轮的结论已由另一个等待给出，落败的那个怎么退出都不改变判定。
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
                LastCompletedLoadOperationContext = existingState.LastCompletedLoadOperationContext
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
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken, Task>? progress,
        string phase,
        IReadOnlyList<int> active,
        IReadOnlyList<int> completed,
        int promptRound,
        CancellationToken cancellationToken)
    {
        if (progress is not null)
        {
            await progress(phase, active, completed, promptRound, cancellationToken)
                .ConfigureAwait(false);
        }
    }

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
