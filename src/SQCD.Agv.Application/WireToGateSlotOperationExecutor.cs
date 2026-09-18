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
    /// 一轮操作员等待的结论。Reached 非空表示已达成目标态；Opposite 非空表示门已关好
    /// 但货物状态与预期相反，需要重新开锁——它同时是这一刻的读数，本站期限已过时要靠
    /// 它填出三个明确的物理字段；两者都为空表示这一轮只是提示节拍到期，仓门还开着。
    /// </summary>
    private readonly record struct SlotWaitResult(LockerSnapshot? Reached, LockerSnapshot? Opposite)
    {
        public bool OppositeObserved => Opposite is not null;
    }

    /// <summary>
    /// 一个仓位驱动到底的结论。<see cref="HandedOver"/> 为真表示达成了目标态；为假表示
    /// 本站期限已过、宽限的那一轮也用掉了，货物始终没有交接——门已闭、开锁输出已复位，
    /// 三个物理字段都读得到，这是 ADR-cross-0058 决策 5 要的那种确定失败。
    /// </summary>
    private readonly record struct SlotDriveResult(LockerSnapshot Locker, bool HandedOver);

    private readonly IIoModuleClient _ioModule;
    private readonly IWireToGateJournal _journal;
    private readonly IClock _clock;
    private readonly WireToGateSlotOperationExecutorOptions _options;
    private readonly Func<DateTimeOffset?> _stationDepartureDeadline;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CancellationTokenSource? _activeOperation;

    /// <param name="stationDepartureDeadline">
    /// 服务端给本站的离站期限，随 CurrentStopWorklistSnapshot 到达（ADR-cross-0058 决策 3：
    /// 期限归服务端）。每一轮重新读，因为一份新的作业清单可以改写它。返回 null 表示本站
    /// 没有期限可倒数，那时目标态闭环没有上限，与决策 1 的原始形态一致。
    /// </param>
    public WireToGateSlotOperationExecutor(
        IIoModuleClient ioModule,
        IWireToGateJournal journal,
        IClock clock,
        WireToGateSlotOperationExecutorOptions options,
        Func<DateTimeOffset?>? stationDepartureDeadline = null)
    {
        _ioModule = ioModule;
        _journal = journal;
        _clock = clock;
        _options = options;
        _stationDepartureDeadline = stationDepartureDeadline ?? (static () => null);
        ValidateOptions(options);
    }

    /// <summary>
    /// 中止正在执行的仓位操作。目标态闭环没有自然终点（决策 1 不设次数上限），所以
    /// 操作员发起的装货取消必须先把它停下来，否则两个执行器会同时驱动同一个 IO 模块：
    /// 一个还在循环脉冲开锁，另一个在验证仓位清空。没有在途操作时什么都不做。
    /// </summary>
    public void AbortActiveOperation()
    {
        CancellationTokenSource? active = Volatile.Read(ref _activeOperation);
        if (active is null)
        {
            return;
        }

        try
        {
            active.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 操作在这两行之间自己结束了，正是想要的结果。
        }
    }

    public async Task<WireToGateOperationExecutionResult> ExecuteAsync(
        WireToGateSlotOperationCommand command,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken, Task>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);
        return await RunExclusiveAsync(
            token => ExecuteExclusiveAsync(command, progress, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 拿操作锁，并把这一次操作的取消源登记为「在途操作」，让 <see cref="AbortActiveOperation"/>
    /// 能停下它。取消源链接调用方的 token，所以关进程与外部中止走同一条路。
    /// </summary>
    private async Task<WireToGateOperationExecutionResult> RunExclusiveAsync(
        Func<CancellationToken, Task<WireToGateOperationExecutionResult>> body,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource active =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Volatile.Write(ref _activeOperation, active);
        try
        {
            return await body(active.Token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _activeOperation, null);
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
        return await RunExclusiveAsync(
            token => ResumeCoreAsync(resume, progress, token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WireToGateOperationExecutionResult> ResumeCoreAsync(
        WireToGateSlotOperationResumeCommand resume,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
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
                || !context.Slots.SequenceEqual(resume.Slots)
                || !string.Equals(
                    ExpectedResumeCommandSha256(state, context),
                    resume.CommandContentSha256,
                    StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// 恢复命令携带的是「这一次恢复动作」的内容哈希，不是原仓位操作命令的哈希：服务端按
    /// 恢复动作号、需求、attempt、仓位集合与强制恢复代际算出它，收到替换结果时再按同一公式
    /// 复算（控制端 <c>RecoveryCommandHash.ForRecoveryAction</c>）。拿原命令的哈希去比，
    /// 两者永远不等，维修后继续就永远被拒（onboard-hmi#99）。
    /// </summary>
    private static string ExpectedResumeCommandSha256(
        WireToGateRecoveryState state,
        WireToGateRecoveryOperationContext context) =>
        WireToGateRecoveryCommandHash.ForRecoveryAction(
            state.RecoveryActionId ?? string.Empty,
            context.DemandId,
            context.SlotOperationAttemptId,
            context.Slots,
            state.ForcedRecoveryGeneration);

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
                PendingLoadCancellation = null,
                LastCompletedLoadOperationContext = state.OperationContext?.OperationType == OperationType.Load
                    ? state.OperationContext
                    : state.LastCompletedLoadOperationContext
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 结算一次执行到一半、执行它的进程就没了的仓位操作（8005-agv-program#40）。只读实时 IO，
    /// 不输出任何开锁脉冲，把日志里那一次未结算的 attempt 交成一份结果。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 进程在开锁之后、结果写出之前退出——关窗口、崩溃、断电、系统更新重启都一样——日志里留下
    /// 一个未结算的 attempt，之后没有任何东西会结算它：车辆只在收到命令或恢复动作时才结算，
    /// 服务端没有结果就不判 RecoveryRequired、不让旅程停摆，而恢复入口要求旅程已经停摆。两端
    /// 就此互相等对方，现场实测过。
    /// </para>
    /// <para>
    /// 收尾照 ADR-cross-0017：重启后不得再输出开锁，只按实时物理状态收掉当前开锁集合，后面的
    /// 仓位要服务端授权才能继续。所以结论只有两种。每个开过的目标仓都已处在最终态、而且没有
    /// 未开始的仓位——上一个进程没了之后操作员把活干完了——结论是确定的 COMPLETED，说成不知道
    /// 就是把一次已经确定的结果误判成未知。其余一律 UNKNOWN，由服务端判 RecoveryRequired 走恢复。
    /// </para>
    /// <para>
    /// 不给确定失败：FAILED 的前提是本站期限已过（ADR-cross-0058 决策 5），进程重启不是期限。
    /// 物理字段一律填读到的真实读数（决策 6）——不知道的是这次操作该怎么往下走，不是仓位长什么样。
    /// </para>
    /// <para>
    /// 调用方负责确认这一次 attempt 确实没有人在执行：本方法看不出来，断网重连时执行器还在跑，
    /// 而日志的样子与进程重启后一模一样。
    /// </para>
    /// </remarks>
    public async Task<WireToGateOperationExecutionResult> SettleInterruptedAsync(
        CancellationToken cancellationToken = default) =>
        await RunExclusiveAsync(SettleInterruptedCoreAsync, cancellationToken).ConfigureAwait(false);

    private async Task<WireToGateOperationExecutionResult> SettleInterruptedCoreAsync(
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await _journal
            .ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        WireToGateRecoveryOperationContext context = state.OperationContext
            ?? throw new InvalidDataException("RECOVERY_OPERATION_CONTEXT_MISSING");
        // 恢复向量有自己的日志与自己的续做规则，不归这里。
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
                // 从未开过：门是关的、锁是闭的，读得到就照实填，reasonCodes 留空（决策 6）。
                UpsertResult(results, CreateSlotResult(locker, "NOT_STARTED", []));
            }
            else if (IsFinalState(locker, command.ExpectedOccupied))
            {
                // 日志说完成过的仓位也要重读：检查点之后物理状态变了，它就不再算完成。
                UpsertResult(results, CreateSlotResult(locker, "COMPLETED", []));
                completed.Add(physicalSlot);
            }
            else
            {
                // 读得到时，不确定的只是下一步：重新开锁接着装，还是放弃这一单，日志与 IO
                // 推不出唯一答案（ADR-cross-0017 的「唯一合法下一步」）。读不到时就是读不到。
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
        // 与执行中途判 UNKNOWN 的那条路写成同一个形状：日志保留 OperationContext 与未结 attempt，
        // 补偿与恢复向量认的正是它们。CancellationToken.None 同理——结论读完了就要落盘。
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
                SlotDriveResult drive = await DriveSlotToTargetStateAsync(
                    command,
                    physicalSlot,
                    completed,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                if (!drive.HandedOver)
                {
                    // 本站期限已过而货物始终没有交接。整站就此收场——后面的仓位不再开，
                    // 期限是站的不是仓位的，接着开下一个仓位只会让车停得更久。
                    return await SettleStationDeadlineFailureAsync(
                        command,
                        context,
                        existingState,
                        completed,
                        results,
                        physicalSlot,
                        drive.Locker,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }

                WireToGateSlotExecutionResult result = CreateSlotResult(
                    drive.Locker,
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
    /// 本站期限过后货物仍未交接，就地结算成 ADR-cross-0058 决策 5 的确定失败：
    /// overallOutcome 是 FAILED，每一个仓位都报得出已知的占用状态、已闭的门与已复位的
    /// 开锁输出——服务端的 determinateFailure 判据要的正是这三样。它既不阻塞旅程也不
    /// 开恢复会话，因为没有任何一件事是不确定的：车辆说得清现场长什么样，答案是没人
    /// 把货交过来。
    /// </summary>
    private async Task<WireToGateOperationExecutionResult> SettleStationDeadlineFailureAsync(
        WireToGateSlotOperationCommand command,
        WireToGateRecoveryOperationContext context,
        WireToGateRecoveryState existingState,
        IReadOnlyList<int> completed,
        List<WireToGateSlotExecutionResult> results,
        int timedOutSlot,
        LockerSnapshot timedOutLocker,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        UpsertResult(results, CreateSlotResult(timedOutLocker, "FAILED", ["OPERATOR_TIMEOUT"]));

        // 一次读快照，剩下的仓位共用同一份读数，避免两次读到不同状态。它们的门从未开过，
        // 三个字段都读得到——决策 6：填 UNKNOWN 才是在陈述不存在的不确定性。reasonCodes
        // 留空，「为什么没开始」由 overallOutcome 承载，不属于仓位。
        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        foreach (int notStarted in command.Slots
            .Where(slot => slot != timedOutSlot && !completed.Contains(slot)))
        {
            UpsertResult(
                results,
                CreateSlotResult(TryGetLocker(snapshot, notStarted - 1), "NOT_STARTED", []));
        }

        // 门已闭、开锁输出已复位，这就是安全收尾——与达成目标态时同一个检查点。
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
        return CreateResult(command, "FAILED", results, WireToGateRecoveryCheckpoint.SafeFinishReached);
    }

    /// <summary>
    /// 把一个仓位开到目标态。光幕稳定读到与预期相反的状态，说明操作员没有放入或
    /// 取出货物——自动重新输出开锁脉冲并提示，不判失败、不进恢复、不设次数上限
    /// （ADR-cross-0058 决策 1）。仓门根本没关时两个条件都不成立，等待继续，
    /// 这是第三条出路，不需要为它单独写判据。
    /// </summary>
    /// <remarks>
    /// 上限仍然不是次数，是服务端给的时刻：本站期限过了以后再读到相反态，就在那里
    /// 结算成确定失败。期限只在**相反态**上生效——门开着时不结算，那是决策 4 的
    /// 「仓门未闭超时转告警并持续等待」，服务端挂 STATION_TIMEOUT_DOOR_NOT_CLOSED，
    /// 车这边照旧提示。而且门开着时 determinateFailure 的三个条件本来就不成立。
    /// </remarks>
    private async Task<SlotDriveResult> DriveSlotToTargetStateAsync(
        WireToGateSlotOperationCommand command,
        int physicalSlot,
        IReadOnlyList<int> completed,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        int slotIndex = physicalSlot - 1;
        bool unlockNeeded = true;
        bool graceUsed = false;
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
                return new SlotDriveResult(reached, HandedOver: true);
            }

            if (wait.Opposite is { } opposite && IsPastStationDeadline())
            {
                // 期限已过，而且门已闭、货没动——这是唯一产得出确定失败的一刻。先花掉
                // 宽限的那一轮：再开一次门、再提示一次。它吸收两端时钟的偏差，也给操作员
                // 最后一次机会，因为决策 1 的立场始终是先提示、别判死。宽限用掉之后
                // 再读到相反态，本站就结算在这里。
                if (graceUsed)
                {
                    return new SlotDriveResult(opposite, HandedOver: false);
                }

                graceUsed = true;
            }

            // 相反状态说明门已关好、货物没动，要重新开锁；提示节拍到期时门还开着，
            // 只是再提示一次，重复脉冲对一把已经开着的锁没有意义。
            unlockNeeded = wait.OppositeObserved;
        }
    }

    /// <summary>
    /// 本站离站期限是否已经过去。期限由服务端随作业清单发来，每一轮重新读——新的一份
    /// 作业清单可以改写它。没有期限时永远返回 false，目标态闭环就退回决策 1 的原始
    /// 形态：没有上限。
    /// </summary>
    private bool IsPastStationDeadline() =>
        _stationDepartureDeadline() is { } deadline && _clock.Now >= deadline;

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
                ? new SlotWaitResult(locker, null)
                : new SlotWaitResult(null, locker);
        }
        catch (TimeoutException)
        {
            // 一个 OperationTimeout 之内两个条件都没有稳定成立：仓门还开着，
            // 操作员还没有动作。不判失败，回去再提示一次。
            return new SlotWaitResult(null, null);
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
