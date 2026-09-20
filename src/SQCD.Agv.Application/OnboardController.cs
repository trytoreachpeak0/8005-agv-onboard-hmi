using System.Collections.Concurrent;
using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

public sealed class OnboardController : IAsyncDisposable
{
    // 控制器不直接依赖康耐德 Modbus 类，也不直接依赖 TCP JSON 类；它只依赖 Core 中定义的接口。
    private readonly IIoModuleClient _ioModule;
    private readonly IRuleGateway _ruleGateway;
    private readonly IAppLogger _logger;
    private readonly IClock _clock;
    private readonly OnboardWorkflowOptions _options;
    private readonly Func<bool> _externalSafetyReadyProvider;
    private readonly Func<WireToGateJourneySnapshot?>? _journeyProvider;
    private readonly Func<bool> _peerSlotWorkInFlightProvider;
    // 操作锁
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    // 线程安全集合
    private readonly ConcurrentDictionary<string, byte> _startedOperationIds = new(StringComparer.Ordinal);
    // 状态锁,用于保护状态发布过程，避免多个线程同时生成、覆盖 OnboardSnapshot
    private readonly object _stateGate = new();
    private readonly object _pendingReportGate = new();
    private readonly SemaphoreSlim _pendingReportSendLock = new(1, 1);
    // 当前扫码/仓位流程的取消源，严重安全故障发生时用于立即阻止后续物理动作。
    private readonly object _operationCancellationGate = new();
    // 提前关门后的恢复操作由界面线程发出，通过这个锁和任务源交给当前操作流程。
    private readonly object _recoveryGate = new();
    private TaskCompletionSource<OperatorRecoveryAction>? _recoveryActionSource;
    private string? _recoveryOperationId;
    // 控制器生命周期的“停止开关”
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _activeOperationCts;
    //当前正在执行的仓位操作。
    private ActiveOperation? _activeOperation;
    private PendingOperationReport? _pendingReport;
    // 车载端当前完整状态快照。
    private OnboardSnapshot _current;
    // 表示是否已完成启动时的 IO 安全初始状态验证。
    private bool _startupValidated;
    // 是否已经调用过 StartAsync
    private bool _started;
    // 是否已经释放资源、不能继续使用
    private bool _disposed;
    // 一旦写入便保持到 ClearFatalFaultAsync 复核通过，普通状态发布不得覆盖严重安全故障。
    //
    // 这个锁存在 v2 上约束什么，是一张写下来并且被守住的表：
    // tests/SQCD.Agv.UnitTests/FatalFaultScopeArchitectureTests.cs。**答案是「只有本控制器发布的
    // 快照」——一个物理动作都不在里面。** 本文件里的四个读点有三个在 SubmitScanAsync 那条 MVP
    // 流程上，而 v2 下界面扫码走 WireToGateBusinessService，那条路一次都不会被调用；第四个是
    // PublishCore，只改快照。服务端下发的仓位命令与操作员按出来的恢复向量各自直接持
    // IIoModuleClient，不经过这里。
    //
    // 所以：**任何依赖「锁存 ⇒ 本机不会开门」的陈述，在 v2 上都要先去那张表里核一遍。**
    // 8005-agv-onboard-hmi#171 的第一版就是在这里推错了——写对了「执行器不经过控制器」这个
    // 一般命题，却只把它用在一处，于是横幅、扫码入口、复位的在途判据三处同时说了假话。
    private FatalFault? _fatalFault;

    // 创建控制器时，外部必须把五项依赖传进来。
    public OnboardController(
        IIoModuleClient ioModule,
        IRuleGateway ruleGateway,
        IAppLogger logger,
        IClock clock,
        OnboardWorkflowOptions options,
        Func<bool>? externalSafetyReadyProvider = null,
        Func<WireToGateJourneySnapshot?>? journeyProvider = null,
        Func<bool>? peerSlotWorkInFlightProvider = null)
    {
        _ioModule = ioModule ?? throw new ArgumentNullException(nameof(ioModule));
        _ruleGateway = ruleGateway ?? throw new ArgumentNullException(nameof(ruleGateway));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _externalSafetyReadyProvider = externalSafetyReadyProvider ?? (() => true);
        _journeyProvider = journeyProvider;
        // v2 的仓位操作与恢复向量都在本控制器之外执行，各有自己的串行化门。复位复核要问的
        // 「现在有没有在开门」只能从那一侧读——本控制器的 _operationLock 只覆盖 MVP 的
        // SubmitScanAsync，在 v2 上永远拿得到（8005-agv-onboard-hmi#171）。
        _peerSlotWorkInFlightProvider = peerSlotWorkInFlightProvider ?? (() => false);
        _current = new OnboardSnapshot(
            OnboardState.Starting,
            false,
            false,
            null,
            IoSnapshot.Unknown(_clock.Now),
            null,
            false,
            "系统正在启动…",
            null,
            _clock.Now);
    }

    // 安全读取当前状态
    public OnboardSnapshot Current => Volatile.Read(ref _current);

    public bool CanReopenCurrentOperation
    {
        get
        {
            lock (_recoveryGate)
            {
                return _activeOperation?.Stage == OperationStage.WaitingOperatorRecovery
                    && _activeOperation.ReopenAttempts < _options.MaxReopenAttempts
                    && IsExternalSafetyReady()
                    && _recoveryActionSource is not null;
            }
        }
    }

    public bool CanCancelCurrentOperation
    {
        get
        {
            lock (_recoveryGate)
            {
                return _activeOperation?.Stage == OperationStage.WaitingOperatorRecovery
                    && _recoveryActionSource is not null;
            }
        }
    }

    public bool CanRetryPendingResult
    {
        get
        {
            lock (_pendingReportGate)
            {
                return _pendingReport is not null && _ruleGateway.IsConnected;
            }
        }
    }

    // 状态变化事件
    public event EventHandler<ValueChangedEventArgs<OnboardSnapshot>>? StateChanged;

    /// <summary>
    /// 外部安全会话状态变化后重新计算扫码和发车门禁。
    /// 已经开始的操作仍可继续收敛到关门安全状态，但不会允许新的开锁或重新开门。
    /// </summary>
    public void RefreshExternalSafetyState() => ReevaluateIdleState();

    // 启动控制器,订阅 IO 和规则模块事件,启动两个后台通信客户端,让界面进入“连接中”状态
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // 禁止已释放对象再次启动
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 防止重复启动
        if (_started)
        {
            return;
        }

        // 创建生命周期取消令牌
        _started = true;
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 先订阅事件，再启动连接
        _ioModule.ConnectionChanged += OnIoConnectionChanged;
        _ioModule.SnapshotChanged += OnIoSnapshotChanged;
        _ruleGateway.ConnectionChanged += OnRuleConnectionChanged;
        _ruleGateway.VisitChanged += OnVisitChanged;

        // 立即通知界面“连接中”
        Publish(OnboardState.Connecting, "正在连接仓门控制设备和任务系统…");
        // 启动两个客户端
        await _ioModule.StartAsync(_lifetimeCts.Token).ConfigureAwait(false);
        await _ruleGateway.StartAsync(_lifetimeCts.Token).ConfigureAwait(false);
        // 重新评估空闲状态
        ReevaluateIdleState();
    }

    // 扫码核验与开锁前安全检查,由界面提交扫码时调用
    /// <summary>
    /// 检查输入
    ///    → 防止重复扫码
    ///    → 检查当前是否真的允许扫码
    ///    → 向规则模块请求授权
    ///    → 检查规则响应和 IO 现场状态
    ///    → 防止同一 operationId 重复开锁
    /// </summary>
    /// <param name="sublot"></param>
    /// <param name="inputMethod"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task SubmitScanAsync(
        string sublot,
        ScanInputMethod inputMethod,
        CancellationToken cancellationToken = default)
    {
        // 空输入直接拒绝
        if (string.IsNullOrWhiteSpace(sublot))
        {
            Publish(Current.State, "请扫描物料条码。", "SUBLOT_EMPTY");
            return;
        }

        string normalizedSublot = sublot.Trim();
        if (normalizedSublot.Length > _options.MaxSublotLength)
        {
            Publish(Current.State, "条码内容不正确，请重新扫描。", "SUBLOT_TOO_LONG");
            return;
        }

        // 防止重复扫码
        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            ActiveOperation? currentOperation = _activeOperation;
            if (currentOperation?.Stage == OperationStage.WaitingOperatorRecovery)
            {
                OnboardSnapshot current = Current;
                Publish(
                    OnboardState.Operating,
                    current.ErrorCode == "EARLY_DOOR_CLOSED"
                        ? current.Guidance
                        : GetRecoveryGuidance(currentOperation),
                    "EARLY_DOOR_CLOSED");
            }
            else
            {
                Publish(Current.State, "当前装卸操作尚未完成，请完成装卸并关好仓门。", "OPERATION_BUSY");
            }

            return;
        }

        // 保存规则模块返回的扫码授权结果
        ScanAuthorization? authorization = null;
        // 保存已经开始执行的仓位操作
        ActiveOperation? operation = null;
        CancellationToken lifetimeToken = _lifetimeCts?.Token ?? CancellationToken.None;
        using CancellationTokenSource operationCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        lock (_operationCancellationGate)
        {
            _activeOperationCts = operationCts;
            if (Volatile.Read(ref _fatalFault) is not null)
            {
                operationCts.Cancel();
            }
        }

        CancellationToken operationToken = operationCts.Token;
        try
        {
            // 先读取一次完整快照，再从中取出当前到站信息。
            OnboardSnapshot before = Current;
            VisitContext? visit = before.Visit;
            // 只有全部满足时才允许继续。
            if (before.State != OnboardState.ReadyToScan
                || !before.RuleConnected
                || !before.IoConnected
                || !IsExternalSafetyReady()
                || !IsAuthoritativeJourneyReady()
                || visit is null
                || !visit.IsActive(_clock.Now))
            {
                string errorCode = !IsExternalSafetyReady()
                    ? "WIRE_TO_GATE_NOT_READY"
                    : !IsAuthoritativeJourneyReady()
                        ? "WIRE_TO_GATE_JOURNEY_NOT_READY"
                        : "NOT_READY";
                ReevaluateIdleState(GetOperatorMessage(errorCode), errorCode);
                return;
            }

            string? journeySublotError = ValidateJourneySublot(normalizedSublot);
            if (journeySublotError is not null)
            {
                ReevaluateIdleState(GetOperatorMessage(journeySublotError), journeySublotError);
                return;
            }

            // 标准化 SUBLOT
            // 进入核验状态并记录日志
            Publish(OnboardState.Verifying, $"正在确认物料条码 {normalizedSublot}…");
            _logger.Write(LogSeverity.Information, nameof(OnboardController),
                $"提交扫码核验，visitId={visit.VisitId}，sublot={normalizedSublot}，inputMethod={inputMethod}。");

            // 请求规则模块核验
            authorization = await _ruleGateway.VerifyScanAsync(
                new ScanVerificationRequest(normalizedSublot, inputMethod, visit.VisitId),
                operationToken).ConfigureAwait(false);

            // 规则模块主动拒绝扫码
            if (!authorization.Accepted)
            {
                string code = authorization.ErrorCode ?? "SCAN_REJECTED";
                _logger.Write(LogSeverity.Warning, nameof(OnboardController),
                    $"扫码核验失败，code={code}，message={authorization.ErrorMessage}。");
                Publish(
                    OnboardState.ReadyToScan,
                    GetOperatorMessage(code, authorization.SlotIndex, authorization.OperationType),
                    code);
                return;
            }

            // 规则允许后，仍必须做本地安全检查
            string? validationError = ValidateAuthorization(authorization);
            if (validationError is not null)
            {
                _logger.Write(LogSeverity.Warning, nameof(OnboardController),
                    $"规则响应或开锁前置条件不满足，code={validationError}。");
                bool? precheckAcknowledged = await TryReportPrecheckFailureAsync(
                    authorization,
                    visit,
                    validationError,
                    operationToken)
                    .ConfigureAwait(false);
                if (precheckAcknowledged is false)
                {
                    Publish(
                        OnboardState.Faulted,
                        "仓门未打开，但任务系统尚未确认本次未执行结果。禁止继续扫码和发车；系统将在连接恢复后自动重报，也可点击“重新上报结果”。",
                        "PRECHECK_RESULT_ACK_TIMEOUT");
                    return;
                }

                Publish(
                    OnboardState.ReadyToScan,
                    GetOperatorMessage(validationError, authorization.SlotIndex, authorization.OperationType),
                    validationError);
                return;
            }

            // 取得已验证的授权字段
            string operationId = authorization.OperationId!;
            string taskId = authorization.TaskId!;
            int slotIndex = authorization.SlotIndex!.Value;
            OperationType operationType = authorization.OperationType!.Value;

            // 原子防重，最后一道重复开锁保护
            if (!_startedOperationIds.TryAdd(operationId, 0))
            {
                Publish(
                    OnboardState.ReadyToScan,
                    GetOperatorMessage("DUPLICATE_OPERATION", slotIndex, operationType),
                    "DUPLICATE_OPERATION");
                return;
            }

            // 创建进行中的操作
            operation = new ActiveOperation(
                visit.VisitId,
                operationId,
                taskId,
                authorization.Sublot,
                slotIndex,
                operationType,
                OperationStage.WritingUnlock,
                _clock.Now);
            SetActiveOperation(operation);
            Publish(OnboardState.Operating, $"正在开启{slotIndex + 1}号仓…");

            // 真正发出开锁信号
            ThrowIfFatalFaultLatched(operationToken);
            ThrowIfExternalSafetyNotReady();
            ThrowIfAuthoritativeJourneyNotReady(normalizedSublot);
            await _ioModule.PulseUnlockAsync(slotIndex, operationToken).ConfigureAwait(false);
            // 等待锁反馈确认“已解锁”
            operation = operation with { Stage = OperationStage.WaitingUnlockFeedback };
            SetActiveOperation(operation);
            LockerSnapshot unlockedLocker = await _ioModule.WaitForLockerAsync(
                slotIndex,
                locker => locker.IsKnown && !locker.IsLocked,
                _options.UnlockFeedbackTimeout,
                _options.FeedbackStableWindow,
                operationToken).ConfigureAwait(false);

            // 确认硬件脉冲已经自动结束。只接受开锁反馈之后的新鲜快照，
            // 避免误用写DO之前残留的DO=0快照。
            operation = operation with { Stage = OperationStage.WaitingUnlockOutputReset };
            SetActiveOperation(operation);
            await _ioModule.WaitForLockerAsync(
                slotIndex,
                locker => locker.IsKnown
                    && locker.ObservedAt >= unlockedLocker.ObservedAt
                    && locker.UnlockOutputRaw is false,
                _options.UnlockOutputResetTimeout,
                _options.FeedbackStableWindow,
                operationToken).ConfigureAwait(false);

            // 提示人工装卸和关门
            operation = operation with { Stage = OperationStage.WaitingCargoAndRelock };
            SetActiveOperation(operation);
            string action = operationType == OperationType.Load ? "放入货物" : "取出货物";
            Publish(OnboardState.Operating, $"{slotIndex + 1}号仓已打开，请{action}并手动关闭仓门。");

            // 等待关门；如果仓门提前关闭但装卸未完成，则进入人工恢复流程。
            OperationRecoveryOutcome recoveryOutcome = await WaitForCargoAndRelockWithRecoveryAsync(
                operation,
                operationToken).ConfigureAwait(false);
            operation = recoveryOutcome.Operation;
            if (recoveryOutcome.Cancelled)
            {
                await HandleOperatorCancellationAsync(
                    operation,
                    recoveryOutcome.Locker,
                    operationToken).ConfigureAwait(false);
                return;
            }

            LockerSnapshot completedLocker = recoveryOutcome.Locker;
            string? completionSafetyError = SafetyRules.ValidateAllDoorsSafe(
                _ioModule.CurrentSnapshot,
                _clock.Now,
                _options.IoSnapshotMaxAge);
            if (completionSafetyError is not null)
            {
                await HandleOperationFailureAsync(
                    operation,
                    completionSafetyError,
                    operationToken).ConfigureAwait(false);
                return;
            }

            completedLocker = _ioModule.CurrentSnapshot.GetLocker(operation.SlotIndex);
            bool expectedCargo = operation.OperationType == OperationType.Load;
            if (completedLocker.HasCargo != expectedCargo)
            {
                await HandleOperationFailureAsync(
                    operation,
                    "CARGO_EXPECTATION_MISMATCH",
                    operationToken).ConfigureAwait(false);
                return;
            }

            // 开始上报成功结果
            operation = operation with { Stage = OperationStage.ReportingResult };
            SetActiveOperation(operation);
            Publish(OnboardState.Reporting, "仓门和货物状态已确认，正在提交操作结果…");

            OperationResult successResult = CreateResult(operation, completedLocker, true, null, OperationStage.Completed);
            _logger.Write(LogSeverity.Information, nameof(OnboardController),
                $"上报成功结果，messageId={successResult.MessageId}，visitId={successResult.VisitId}，taskId={successResult.TaskId}，operationId={successResult.OperationId}，slot={successResult.SlotIndex}。");
            RegisterPendingReport(new PendingOperationReport(
                successResult,
                PendingReportKind.SuccessfulOperation,
                operation,
                "装卸操作完成，任务系统已确认，可以继续扫码。"));
            bool acknowledged = await TrySendPendingReportAsync(operationToken).ConfigureAwait(false);
            acknowledged |= !IsPendingResult(successResult.MessageId);
            // 物理成功，但结果 ACK 超时
            if (!acknowledged)
            {
                operation = operation with { Stage = OperationStage.Failed };
                SetActiveOperation(operation);
                Publish(
                    OnboardState.Faulted,
                    GetOperatorMessage(
                        "RESULT_ACK_TIMEOUT",
                        operation.SlotIndex,
                        operation.OperationType,
                        completedLocker),
                    "RESULT_ACK_TIMEOUT");
                return;
            }

            // 成功收 ACK，回到空闲状态
            _logger.Write(LogSeverity.Information, nameof(OnboardController),
                $"仓位操作完成并收到ACK，operationId={operation.OperationId}，slot={operation.SlotIndex}。");
        }
        // 调用方主动取消
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            bool applicationStopping = lifetimeToken.IsCancellationRequested;
            ActiveOperation? interruptedOperation = _activeOperation ?? operation;
            if (interruptedOperation is not null && !applicationStopping && !HasPendingResult())
            {
                await HandleOperationFailureAsync(interruptedOperation, "OPERATION_CANCELLED", cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
        // 可预期的运行异常
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException or InvalidOperationException)
        {
            ActiveOperation? failedOperation = _activeOperation ?? operation;
            string failureCode = GetFailureCode(failedOperation, exception);
            _logger.Write(LogSeverity.Error, nameof(OnboardController),
                $"仓位操作失败，code={failureCode}，operationId={failedOperation?.OperationId ?? "N/A"}。", exception);

            if (failedOperation is not null)
            {
                await HandleOperationFailureAsync(failedOperation, failureCode, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                ReevaluateIdleState(GetOperatorMessage(failureCode), failureCode);
            }
        }
        // 无论如何都释放操作锁
        finally
        {
            lock (_operationCancellationGate)
            {
                if (ReferenceEquals(_activeOperationCts, operationCts))
                {
                    _activeOperationCts = null;
                }
            }

            _operationLock.Release();
        }
    }

    /// <summary>
    /// 操作员确认重新打开当前仓门。这里只向正在等待的操作流程发送选择，
    /// 真正的开锁前仍会重新执行IO和任务安全检查。
    /// </summary>
    public bool RequestReopenCurrentOperation()
    {
        bool accepted = TrySetRecoveryAction(OperatorRecoveryAction.Reopen);
        if (accepted)
        {
            _logger.Write(LogSeverity.Information, nameof(OnboardController), "操作员请求重新打开当前仓门。");
        }

        return accepted;
    }

    /// <summary>
    /// 操作员确认取消当前未完成的装卸操作。
    /// </summary>
    public bool RequestCancelCurrentOperation()
    {
        bool accepted = TrySetRecoveryAction(OperatorRecoveryAction.Cancel);
        if (accepted)
        {
            _logger.Write(LogSeverity.Warning, nameof(OnboardController), "操作员请求取消当前装卸操作。");
        }

        return accepted;
    }

    /// <summary>
    /// 手动重试上报当前保留的结果。自动重连也会调用同一条幂等路径，
    /// 始终复用原messageId，不会再次执行物理开锁。
    /// </summary>
    public async Task<bool> RetryPendingResultAsync(CancellationToken cancellationToken = default)
    {
        CancellationToken lifetimeToken = _lifetimeCts?.Token ?? CancellationToken.None;
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        try
        {
            return await TrySendPendingReportAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            return false;
        }
    }

    // 程序退出或控制器释放时调用，目标是停止后台通信
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            return;
        }

        _lifetimeCts?.Cancel();
        await _ruleGateway.StopAsync(cancellationToken).ConfigureAwait(false);
        await _ioModule.StopAsync(cancellationToken).ConfigureAwait(false);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _operationLock.Release();
        await _pendingReportSendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _pendingReportSendLock.Release();
        _started = false;
        Publish(OnboardState.Connecting, "车载端服务已停止。");
    }

    public async Task<bool> ConfirmSafeStartupStateAsync(CancellationToken cancellationToken = default)
    {
        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            Publish(Current.State, "当前装卸操作尚未完成，暂时不能进行安全复核。", "OPERATION_BUSY");
            return false;
        }

        try
        {
            OnboardSnapshot current = Current;
            if (current.State != OnboardState.Faulted || current.ErrorCode != "STARTUP_STATE_UNSAFE")
            {
                Publish(current.State, "当前故障不允许通过启动安全复核清除。", current.ErrorCode);
                return false;
            }

            IoSnapshot io = _ioModule.CurrentSnapshot;
            string? unsafeReason = ValidateRecoverableStartupSnapshot(io);
            if (unsafeReason is not null)
            {
                Publish(OnboardState.Faulted, $"安全复核未通过：{unsafeReason}", "STARTUP_STATE_UNSAFE");
                return false;
            }

            _startupValidated = true;
            _logger.Write(LogSeverity.Warning, nameof(OnboardController),
                "维护人员已执行启动安全复核：DO全0、仓门全锁、快照有效；保留当前货物反馈。 ");
            Publish(OnboardState.Connecting, "启动安全复核通过，正在重新评估通信和到站状态。");
            ReevaluateIdleState("启动安全复核通过，已保留当前仓位货物状态。");
            return true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void EnterFatalFault(string errorCode, string guidance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(guidance);

        FatalFault requested = new(errorCode, guidance);
        FatalFault effective = Interlocked.CompareExchange(ref _fatalFault, requested, null) ?? requested;
        lock (_operationCancellationGate)
        {
            _activeOperationCts?.Cancel();
        }

        _logger.Write(
            LogSeverity.Error,
            nameof(OnboardController),
            $"严重安全故障已锁存，code={effective.ErrorCode}；当前活动流程已请求停止。");
        Publish(OnboardState.Faulted, effective.Banner, effective.ErrorCode);
    }

    /// <summary>
    /// True when a fatal fault is latched and its code is one maintenance may lift on this machine
    /// (8005-agv-onboard-hmi#171). False whenever nothing is latched, so a normal run never offers
    /// this entry.
    /// </summary>
    /// <summary>
    /// 现在是不是锁存着一个严重安全故障。v2 的业务路径不经过本控制器，所以那一侧要自己读这个，
    /// 才能让「本界面已禁止扫码开门」成为真的（8005-agv-onboard-hmi#171）。
    /// </summary>
    public bool IsFatalFaultLatched => Volatile.Read(ref _fatalFault) is not null;

    public bool CanClearFatalFault =>
        Volatile.Read(ref _fatalFault) is { } latched
        && OnboardFailureClassification.Clearance(latched.ErrorCode)
            == FatalFaultClearance.ClearableBySafetyReview;

    /// <summary>
    /// Lifts a latched fatal safety fault after maintenance has reviewed the doors, and records who
    /// did it. Returns false and leaves the latch standing when the review does not hold.
    /// </summary>
    /// <param name="operatorId">
    /// Who is lifting it. Required: the point of this entry is that afterwards somebody can ask who
    /// opened the vehicle back up and when, so a clearance nobody can be tied to is not one worth
    /// having.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Four conditions, and each one refuses out loud.</b> There is a latch; its code is
    /// clearable; no slot operation is running; and the IO snapshot says what
    /// <see cref="ValidateRecoverableStartupSnapshot"/> requires -- module connected, snapshot fresh,
    /// all eight slots readable, every unlock output reset, every door locked. The last is the same
    /// physical review <see cref="ConfirmSafeStartupStateAsync"/> runs, because it is the same
    /// question: are the doors where this process believes they are.
    /// </para>
    /// <para>
    /// <b>This is deliberately not folded into <see cref="ConfirmSafeStartupStateAsync"/>.</b> That
    /// method admits one code, <c>STARTUP_STATE_UNSAFE</c>, and it runs before the vehicle has ever
    /// been allowed to operate -- it sets <c>_startupValidated</c>. Giving it a second job would give
    /// a startup-time confirmation the power to clear a safety latch during a run, on a press whose
    /// operator cannot tell the two situations apart. Clearing the latch here leaves
    /// <c>_startupValidated</c> alone.
    /// </para>
    /// </remarks>
    public async Task<bool> ClearFatalFaultAsync(
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);

        FatalFault? latched = Volatile.Read(ref _fatalFault);
        if (latched is null)
        {
            return false;
        }

        if (OnboardFailureClassification.Clearance(latched.ErrorCode)
            != FatalFaultClearance.ClearableBySafetyReview)
        {
            RefuseClearance(latched, "该故障不能在车上复位，请重启车载端程序。", operatorId);
            return false;
        }

        // 两把锁都要问。本控制器的 _operationLock 只覆盖 MVP 的 SubmitScanAsync；v2 的仓位操作与
        // 恢复向量各在自己的执行器里跑，各有自己的门。只问前者，在 v2 上等于没问
        // （8005-agv-onboard-hmi#171 审查 S2）。
        if (_peerSlotWorkInFlightProvider())
        {
            RefuseClearance(latched, "当前装卸操作尚未结束，请等待其结束后再复位。", operatorId);
            return false;
        }

        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            RefuseClearance(latched, "当前装卸操作尚未结束，请等待其结束后再复位。", operatorId);
            return false;
        }

        try
        {
            string? unsafeReason = ValidateRecoverableStartupSnapshot(_ioModule.CurrentSnapshot);
            if (unsafeReason is not null)
            {
                RefuseClearance(latched, unsafeReason, operatorId);
                return false;
            }

            // Clear the exact latch that was reviewed. A different one arriving meanwhile was never
            // reviewed, and EnterFatalFault would not have overwritten this one, so it would be lost.
            if (Interlocked.CompareExchange(ref _fatalFault, null, latched) != latched)
            {
                return false;
            }

            _logger.Write(
                LogSeverity.Warning,
                nameof(OnboardController),
                $"严重安全故障已复位：code={latched.ErrorCode}，operator={operatorId}，"
                + $"at={_clock.Now.ToUniversalTime():O}；仓门全锁、开锁输出全0、快照有效已复核。 ");
            // Faulted has to be left before ReevaluateIdleState, which republishes the current state
            // unchanged while it is still Faulted.
            Publish(OnboardState.Connecting, "严重安全故障已复位，正在重新评估通信和到站状态。");
            ReevaluateIdleState("严重安全故障已复位，请确认仓位状态后继续作业。");
            return true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Keeps the latch and tells the operator why this attempt was refused. The reason goes into the
    /// latch first: <see cref="PublishCore"/> rewrites any publish back to the latch's banner while
    /// it stands, so a reason published beside it would never be seen.
    /// </summary>
    private void RefuseClearance(FatalFault latched, string reason, string operatorId)
    {
        FatalFault explained = latched with { ClearanceRefusal = reason };
        Interlocked.CompareExchange(ref _fatalFault, explained, latched);
        _logger.Write(
            LogSeverity.Warning,
            nameof(OnboardController),
            $"严重安全故障复位被拒：code={latched.ErrorCode}，operator={operatorId}，reason={reason}");
        Publish(OnboardState.Faulted, explained.Banner, latched.ErrorCode);
    }

    // 规则模块回复成功后、本地写 DO 前的最后检查
    private string? ValidateAuthorization(ScanAuthorization authorization)
    {
        if (!IsExternalSafetyReady())
        {
            return "WIRE_TO_GATE_NOT_READY";
        }

        if (!IsAuthoritativeJourneyReady())
        {
            return "WIRE_TO_GATE_JOURNEY_NOT_READY";
        }

        if (authorization.OperationType == OperationType.Load && authorization.ExpectedCargoAfter is not true
            || authorization.OperationType == OperationType.Unload && authorization.ExpectedCargoAfter is not false)
        {
            return "INVALID_RULE_RESPONSE";
        }

        return SafetyRules.ValidateBeforeUnlock(
            authorization,
            _ioModule.CurrentSnapshot,
            _startedOperationIds.Keys.ToHashSet(StringComparer.Ordinal),
            _clock.Now,
            _options.IoSnapshotMaxAge);
    }

    private async Task<OperationRecoveryOutcome> WaitForCargoAndRelockWithRecoveryAsync(
        ActiveOperation operation,
        CancellationToken cancellationToken)
    {
        bool expectedCargo = operation.OperationType == OperationType.Load;
        // 保持原有语义：120秒从首次确认仓门已经打开后开始计算，
        // 重新开门不会重置这段总时限。
        DateTimeOffset deadline = _clock.Now + _options.OperationTimeout;
        string? recoveryOverride = null;

        while (true)
        {
            TimeSpan remaining = GetRemainingOperationTime(deadline);
            LockerSnapshot closedLocker = await _ioModule.WaitForLockerAsync(
                operation.SlotIndex,
                locker => locker.IsKnown && locker.IsLocked,
                remaining,
                _options.FeedbackStableWindow,
                cancellationToken).ConfigureAwait(false);

            if (closedLocker.HasCargo == expectedCargo)
            {
                return new OperationRecoveryOutcome(operation, closedLocker, false);
            }

            operation = operation with { Stage = OperationStage.WaitingOperatorRecovery };
            SetActiveOperation(operation);
            RecoveryWaitResult recovery = await WaitForRecoveryActionOrCompletionAsync(
                operation,
                deadline,
                recoveryOverride,
                cancellationToken).ConfigureAwait(false);
            recoveryOverride = null;

            if (recovery.CompletedLocker is not null)
            {
                return new OperationRecoveryOutcome(operation, recovery.CompletedLocker, false);
            }

            LockerSnapshot latest = _ioModule.CurrentSnapshot.GetLocker(operation.SlotIndex);
            if (latest.IsKnown && latest.IsLocked && latest.HasCargo == expectedCargo)
            {
                return new OperationRecoveryOutcome(operation, latest, false);
            }

            if (recovery.Action == OperatorRecoveryAction.Cancel)
            {
                string? cancelIssue = ValidateRecoveryAction(operation, requireActiveVisit: false);
                if (cancelIssue is null)
                {
                    return new OperationRecoveryOutcome(operation, latest, true);
                }

                recoveryOverride = $"暂时不能取消本次操作：{cancelIssue}";
                continue;
            }

            if (operation.ReopenAttempts >= _options.MaxReopenAttempts)
            {
                recoveryOverride = "重新开门次数已经用完，请取消本次操作或联系维护人员。";
                continue;
            }

            string? reopenIssue = ValidateRecoveryAction(operation, requireActiveVisit: true);
            if (reopenIssue is not null)
            {
                recoveryOverride = $"暂时不能重新打开仓门：{reopenIssue}";
                continue;
            }

            int attempt = operation.ReopenAttempts + 1;
            operation = operation with
            {
                Stage = OperationStage.WritingUnlock,
                ReopenAttempts = attempt
            };
            SetActiveOperation(operation);
            Publish(
                OnboardState.Operating,
                $"正在重新打开{operation.SlotIndex + 1}号仓（第{attempt}次）…");
            ThrowIfFatalFaultLatched(cancellationToken);
            ThrowIfExternalSafetyNotReady();
            await _ioModule.PulseUnlockAsync(operation.SlotIndex, cancellationToken).ConfigureAwait(false);

            operation = operation with { Stage = OperationStage.WaitingUnlockFeedback };
            SetActiveOperation(operation);
            LockerSnapshot unlockedLocker = await _ioModule.WaitForLockerAsync(
                operation.SlotIndex,
                locker => locker.IsKnown && !locker.IsLocked,
                MinTimeout(_options.UnlockFeedbackTimeout, GetRemainingOperationTime(deadline)),
                _options.FeedbackStableWindow,
                cancellationToken).ConfigureAwait(false);

            operation = operation with { Stage = OperationStage.WaitingUnlockOutputReset };
            SetActiveOperation(operation);
            await _ioModule.WaitForLockerAsync(
                operation.SlotIndex,
                locker => locker.IsKnown
                    && locker.ObservedAt >= unlockedLocker.ObservedAt
                    && locker.UnlockOutputRaw is false,
                MinTimeout(_options.UnlockOutputResetTimeout, GetRemainingOperationTime(deadline)),
                _options.FeedbackStableWindow,
                cancellationToken).ConfigureAwait(false);

            operation = operation with { Stage = OperationStage.WaitingCargoAndRelock };
            SetActiveOperation(operation);
            string action = operation.OperationType == OperationType.Load ? "放入货物" : "取出货物";
            Publish(
                OnboardState.Operating,
                $"{operation.SlotIndex + 1}号仓已重新打开，请{action}并手动关闭仓门。");
        }
    }

    private async Task<RecoveryWaitResult> WaitForRecoveryActionOrCompletionAsync(
        ActiveOperation operation,
        DateTimeOffset deadline,
        string? guidanceOverride,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining = GetRemainingOperationTime(deadline);
        TaskCompletionSource<OperatorRecoveryAction> source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_recoveryGate)
        {
            _recoveryActionSource = source;
            _recoveryOperationId = operation.OperationId;
        }

        string guidance = guidanceOverride ?? GetRecoveryGuidance(operation);
        Publish(OnboardState.Operating, guidance, "EARLY_DOOR_CLOSED");

        using CancellationTokenSource monitorCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<LockerSnapshot> completionTask = _ioModule.WaitForLockerAsync(
            operation.SlotIndex,
            locker => locker.IsKnown
                && locker.IsLocked
                && locker.HasCargo == (operation.OperationType == OperationType.Load),
            remaining,
            _options.FeedbackStableWindow,
            monitorCts.Token);
        Task<OperatorRecoveryAction> actionTask =
            source.Task.WaitAsync(remaining, cancellationToken);

        try
        {
            Task winner = await Task.WhenAny(completionTask, actionTask).ConfigureAwait(false);
            if (winner == completionTask)
            {
                return new RecoveryWaitResult(
                    null,
                    await completionTask.ConfigureAwait(false));
            }

            OperatorRecoveryAction action = await actionTask.ConfigureAwait(false);
            monitorCts.Cancel();
            try
            {
                _ = await completionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (monitorCts.IsCancellationRequested)
            {
                // 操作员已经做出选择，停止并发的货物状态等待。
            }

            return new RecoveryWaitResult(action, null);
        }
        finally
        {
            monitorCts.Cancel();
            _ = source.TrySetCanceled(CancellationToken.None);
            if (!completionTask.IsCompleted)
            {
                try
                {
                    _ = await completionTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (monitorCts.IsCancellationRequested)
                {
                    // 恢复等待结束，确保并发的IO等待任务也已经退出。
                }
            }
            else if (completionTask.IsFaulted)
            {
                _ = completionTask.Exception;
            }

            lock (_recoveryGate)
            {
                if (ReferenceEquals(_recoveryActionSource, source))
                {
                    _recoveryActionSource = null;
                    _recoveryOperationId = null;
                }
            }
        }
    }

    private bool TrySetRecoveryAction(OperatorRecoveryAction action)
    {
        lock (_recoveryGate)
        {
            ActiveOperation? operation = _activeOperation;
            if (operation?.Stage != OperationStage.WaitingOperatorRecovery
                || _recoveryActionSource is null
                || _recoveryOperationId != operation.OperationId
                || action == OperatorRecoveryAction.Reopen
                    && (operation.ReopenAttempts >= _options.MaxReopenAttempts
                        || !IsExternalSafetyReady()))
            {
                return false;
            }

            return _recoveryActionSource.TrySetResult(action);
        }
    }

    private string? ValidateRecoveryAction(ActiveOperation operation, bool requireActiveVisit)
    {
        if (!IsExternalSafetyReady())
        {
            return "上层安全会话尚未就绪，请等待连接和恢复完成。";
        }

        if (!_ioModule.IsConnected || !_ioModule.CurrentSnapshot.IsConnected)
        {
            return "仓门控制设备连接中断，请等待连接恢复。";
        }

        if (!_ruleGateway.IsConnected)
        {
            return "任务系统连接中断，请等待连接恢复。";
        }

        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        if (!SafetyRules.IsSnapshotFresh(snapshot, _clock.Now, _options.IoSnapshotMaxAge))
        {
            return "仓位状态长时间未更新，请等待状态恢复。";
        }

        if (snapshot.Lockers.Count != 8 || snapshot.Lockers.Any(locker => !locker.IsKnown))
        {
            return "部分仓位状态无法确认，请等待状态恢复。";
        }

        if (snapshot.Lockers.Any(locker => locker.UnlockOutputRaw is not false))
        {
            return "开门控制尚未复位，请稍后重试。";
        }

        if (snapshot.Lockers.Any(locker => !locker.IsLocked))
        {
            return "请先关好所有仓门。";
        }

        bool originalCargo = operation.OperationType == OperationType.Unload;
        if (snapshot.GetLocker(operation.SlotIndex).HasCargo != originalCargo)
        {
            return "当前货物状态已经变化，请等待系统重新确认。";
        }

        if (requireActiveVisit)
        {
            VisitContext? visit = _ruleGateway.CurrentVisit;
            if (visit is null
                || visit.VisitId != operation.VisitId
                || !visit.IsActive(_clock.Now))
            {
                return "本次到站任务已经结束，请取消本次操作。";
            }
        }

        return null;
    }

    private string GetRecoveryGuidance(ActiveOperation operation)
    {
        string state = operation.OperationType == OperationType.Load
            ? "尚未检测到货物"
            : "仍检测到货物";
        if (operation.ReopenAttempts >= _options.MaxReopenAttempts)
        {
            return $"{operation.SlotIndex + 1}号仓门已关闭，但{state}。重新开门次数已经用完，请取消本次操作或联系维护人员。";
        }

        return $"{operation.SlotIndex + 1}号仓门已关闭，但{state}。请选择“重新打开仓门”继续操作，或取消本次操作。";
    }

    private TimeSpan GetRemainingOperationTime(DateTimeOffset deadline)
    {
        TimeSpan remaining = deadline - _clock.Now;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException("装卸操作总等待时间已到。");
        }

        return remaining;
    }

    private static TimeSpan MinTimeout(TimeSpan first, TimeSpan second) =>
        first <= second ? first : second;

    private async Task HandleOperatorCancellationAsync(
        ActiveOperation operation,
        LockerSnapshot finalLocker,
        CancellationToken cancellationToken)
    {
        const string failureCode = "OPERATION_CANCELLED_BY_OPERATOR";
        OperationStage cancellationStage = operation.Stage;
        ActiveOperation cancelledOperation = operation with { Stage = OperationStage.Failed };
        SetActiveOperation(cancelledOperation);
        OperationResult result = CreateResult(
            cancelledOperation,
            finalLocker,
            false,
            failureCode,
            cancellationStage);

        RegisterPendingReport(new PendingOperationReport(
            result,
            PendingReportKind.CancelledOperation,
            cancelledOperation,
            $"{operation.SlotIndex + 1}号仓操作已取消，任务系统已确认，可以继续扫码。"));
        bool acknowledged = await TrySendPendingReportAsync(cancellationToken).ConfigureAwait(false);
        acknowledged |= !IsPendingResult(result.MessageId);

        if (!acknowledged)
        {
            Publish(
                OnboardState.Faulted,
                "本次操作已在车上取消，但任务系统尚未确认。禁止继续扫码和发车；系统将在连接恢复后自动重报，也可点击“重新上报结果”。",
                "CANCEL_RESULT_ACK_TIMEOUT");
            return;
        }

        _logger.Write(
            LogSeverity.Warning,
            nameof(OnboardController),
            $"操作员已取消装卸操作，operationId={operation.OperationId}，slot={operation.SlotIndex}。");
    }

    // 已经开始物理仓位操作后失败
    private async Task HandleOperationFailureAsync(
        ActiveOperation operation,
        string failureCode,
        CancellationToken cancellationToken)
    {
        LockerSnapshot finalLocker = _ioModule.CurrentSnapshot.GetLocker(operation.SlotIndex);
        ActiveOperation failedOperation = operation with { Stage = OperationStage.Failed };
        SetActiveOperation(failedOperation);

        OperationResult failureResult = CreateResult(
            failedOperation,
            finalLocker,
            false,
            failureCode,
            operation.Stage);

        _logger.Write(LogSeverity.Warning, nameof(OnboardController),
            $"上报失败结果，messageId={failureResult.MessageId}，visitId={failureResult.VisitId}，taskId={failureResult.TaskId}，operationId={failureResult.OperationId}，slot={failureResult.SlotIndex}，code={failureCode}。");

        string operatorMessage = GetOperatorMessage(
            failureCode,
            operation.SlotIndex,
            operation.OperationType,
            finalLocker);
        RegisterPendingReport(new PendingOperationReport(
            failureResult,
            PendingReportKind.FailedOperation,
            failedOperation,
            operatorMessage));
        bool acknowledged = await TrySendPendingReportAsync(cancellationToken).ConfigureAwait(false);
        acknowledged |= !IsPendingResult(failureResult.MessageId);
        string resultStatus = acknowledged
            ? string.Empty
            : " 任务状态尚未确认；系统将在连接恢复后自动重报，也可点击“重新上报结果”。";
        Publish(
            OnboardState.Faulted,
            $"{operatorMessage}{resultStatus}",
            failureCode);
    }

    private async Task<bool?> TryReportPrecheckFailureAsync(
        ScanAuthorization authorization,
        VisitContext visit,
        string failureCode,
        CancellationToken cancellationToken)
    {
        if (failureCode is "INVALID_RULE_RESPONSE" or "DUPLICATE_OPERATION"
            || string.IsNullOrWhiteSpace(authorization.OperationId)
            || string.IsNullOrWhiteSpace(authorization.TaskId)
            || authorization.SlotIndex is not (>= 0 and <= 7)
            || authorization.OperationType is null
            || !_startedOperationIds.TryAdd(authorization.OperationId, 0))
        {
            return null;
        }

        ActiveOperation rejected = new(
            visit.VisitId,
            authorization.OperationId,
            authorization.TaskId,
            authorization.Sublot,
            authorization.SlotIndex.Value,
            authorization.OperationType.Value,
            OperationStage.Precheck,
            _clock.Now);
        OperationResult result = CreateResult(
            rejected,
            _ioModule.CurrentSnapshot.GetLocker(rejected.SlotIndex),
            success: false,
            failureCode,
            OperationStage.Precheck);
        RegisterPendingReport(new PendingOperationReport(
            result,
            PendingReportKind.PrecheckFailure,
            rejected,
            GetOperatorMessage(
                failureCode,
                rejected.SlotIndex,
                rejected.OperationType,
                result.FinalLocker)));
        bool acknowledged = await TrySendPendingReportAsync(cancellationToken).ConfigureAwait(false);
        acknowledged |= !IsPendingResult(result.MessageId);
        _logger.Write(
            acknowledged ? LogSeverity.Information : LogSeverity.Warning,
            nameof(OnboardController),
            $"开锁前检查失败结果{(acknowledged ? "已确认" : "未收到ACK")}，messageId={result.MessageId}，operationId={result.OperationId}，code={failureCode}。");
        return acknowledged;
    }

    // 只负责把当前信息组装成 OperationResult，不发送网络消息。
    private OperationResult CreateResult(
        ActiveOperation operation,
        LockerSnapshot finalLocker,
        bool success,
        string? failureCode,
        OperationStage failureStage)
    {
        IoSnapshot io = _ioModule.CurrentSnapshot;
        bool departurePermitted = SafetyRules.IsDeparturePermitted(
            io,
            success ? null : operation,
            hasBlockingFault: !success,
            _clock.Now,
            _options.IoSnapshotMaxAge)
            && IsExternalSafetyReady();
        return new OperationResult(
            $"MSG-{Guid.NewGuid():N}",
            operation.VisitId,
            operation.OperationId,
            operation.TaskId,
            operation.Sublot,
            operation.SlotIndex,
            operation.OperationType,
            success,
            failureCode,
            failureStage,
            finalLocker,
            departurePermitted,
            operation.StartedAt,
            _clock.Now);
    }

    private void RegisterPendingReport(PendingOperationReport pending)
    {
        lock (_pendingReportGate)
        {
            if (_pendingReport is not null
                && _pendingReport.Result.MessageId != pending.Result.MessageId)
            {
                throw new InvalidOperationException("已有尚未确认的操作结果，不能覆盖。");
            }

            _pendingReport = pending;
        }
    }

    private bool HasPendingResult()
    {
        lock (_pendingReportGate)
        {
            return _pendingReport is not null;
        }
    }

    private bool IsPendingResult(string messageId)
    {
        lock (_pendingReportGate)
        {
            return _pendingReport?.Result.MessageId == messageId;
        }
    }

    private async Task<bool> TrySendPendingReportAsync(CancellationToken cancellationToken)
    {
        await _pendingReportSendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        PendingOperationReport? acknowledgedReport = null;
        try
        {
            PendingOperationReport? pending;
            lock (_pendingReportGate)
            {
                pending = _pendingReport;
            }

            if (pending is null || !_ruleGateway.IsConnected)
            {
                return false;
            }

            bool acknowledged;
            try
            {
                acknowledged = await _ruleGateway.ReportOperationAsync(
                    pending.Result,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
            {
                _logger.Write(
                    LogSeverity.Warning,
                    nameof(OnboardController),
                    $"保留结果上报未确认，messageId={pending.Result.MessageId}，稍后重试。",
                    exception);
                return false;
            }

            if (!acknowledged)
            {
                return false;
            }

            lock (_pendingReportGate)
            {
                if (_pendingReport?.Result.MessageId == pending.Result.MessageId)
                {
                    acknowledgedReport = _pendingReport;
                    _pendingReport = null;
                }
            }
        }
        finally
        {
            _pendingReportSendLock.Release();
        }

        if (acknowledgedReport is null)
        {
            return false;
        }

        ResolveAcknowledgedPendingReport(acknowledgedReport);
        return true;
    }

    private void ResolveAcknowledgedPendingReport(PendingOperationReport pending)
    {
        _logger.Write(
            LogSeverity.Information,
            nameof(OnboardController),
            $"保留结果已收到任务系统确认，messageId={pending.Result.MessageId}，operationId={pending.Result.OperationId}。");

        switch (pending.Kind)
        {
            case PendingReportKind.SuccessfulOperation:
            case PendingReportKind.CancelledOperation:
                SetActiveOperation(null);
                ReturnToIdleAfterConfirmedReport(pending.AcknowledgedGuidance);
                break;

            case PendingReportKind.PrecheckFailure:
                if (Current.State == OnboardState.Faulted
                    && Current.ErrorCode == "PRECHECK_RESULT_ACK_TIMEOUT")
                {
                    ReturnToIdleAfterConfirmedReport(pending.AcknowledgedGuidance);
                }
                break;

            case PendingReportKind.FailedOperation:
                if (Current.State == OnboardState.Faulted)
                {
                    Publish(
                        OnboardState.Faulted,
                        pending.AcknowledgedGuidance,
                        pending.Result.FailureCode);
                }

                break;
        }
    }

    private void ReturnToIdleAfterConfirmedReport(string guidance)
    {
        if (Volatile.Read(ref _fatalFault) is not null)
        {
            Publish(OnboardState.Faulted, guidance);
            return;
        }

        if (Current.State == OnboardState.Faulted)
        {
            Publish(OnboardState.Connecting, guidance);
        }

        ReevaluateIdleState(guidance);
    }

    private async Task RetryPendingReportAfterReconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            if (!HasPendingResult() || !_ruleGateway.IsConnected)
            {
                return;
            }

            _logger.Write(
                LogSeverity.Information,
                nameof(OnboardController),
                "任务系统连接已恢复，正在自动重新上报保留结果。");
            _ = await TrySendPendingReportAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(OnboardController),
                "自动重新上报保留结果失败，等待下次重试。",
                exception);
        }
    }

    // IO 连接状态变化
    private void OnIoConnectionChanged(object? sender, ValueChangedEventArgs<bool> args)
    {
        _logger.Write(args.Value ? LogSeverity.Information : LogSeverity.Warning, nameof(OnboardController),
            args.Value ? "IO模块在线。" : "IO模块离线。");
        ReevaluateIdleState();
    }

    // 收到新的 IO 快照
    private void OnIoSnapshotChanged(object? sender, ValueChangedEventArgs<IoSnapshot> args)
    {
        if (!_startupValidated && args.Value.IsConnected)
        {
            bool safeColdStart = args.Value.Lockers.Count == 8
                && args.Value.Lockers.All(locker => locker.IsKnown
                    && locker.UnlockOutputRaw is false
                    && locker.IsLocked
                    && !locker.HasCargo);
            if (!safeColdStart)
            {
                _logger.Write(
                    LogSeverity.Warning,
                    nameof(OnboardController),
                    $"启动IO快照不符合冷启动基线：{DescribeStartupSnapshot(args.Value)}。");
                Publish(OnboardState.Faulted,
                    DescribeStartupOperatorMessage(args.Value),
                    "STARTUP_STATE_UNSAFE");
                return;
            }

            _startupValidated = true;
            _logger.Write(LogSeverity.Information, nameof(OnboardController), "启动IO安全快照验证通过。");
        }

        OnboardSnapshot current = Current;
        bool departure = CalculateDeparture(args.Value, _activeOperation, current.State);
        Publish(current.State, current.Guidance, current.ErrorCode, ioOverride: args.Value, departureOverride: departure);
        if (_activeOperation is null && current.State != OnboardState.Faulted)
        {
            // IO会高频发布快照。保留最近一次非致命业务错误，避免错误提示
            // 刚显示就被下一次轮询恢复成“已到站，请扫描SUBLOT”。
            ReevaluateIdleState(
                string.IsNullOrWhiteSpace(current.ErrorCode) ? null : current.Guidance,
                current.ErrorCode);
        }
    }

    // 规则模块连接变化
    private void OnRuleConnectionChanged(object? sender, ValueChangedEventArgs<bool> args)
    {
        _logger.Write(args.Value ? LogSeverity.Information : LogSeverity.Warning, nameof(OnboardController),
            args.Value ? "规则模块在线。" : "规则模块离线。");
        ReevaluateIdleState();
        if (args.Value && HasPendingResult())
        {
            CancellationToken token = _lifetimeCts?.Token ?? CancellationToken.None;
            _ = RetryPendingReportAfterReconnectAsync(token);
        }
    }

    // 到站上下文变化
    private void OnVisitChanged(object? sender, ValueChangedEventArgs<VisitContext?> args)
    {
        ReevaluateIdleState();
    }

    //空闲状态机 ReevaluateIdleState,这是决定“连接中、等待到站、可扫码”的核心方法。
    private void ReevaluateIdleState(string? preferredGuidance = null, string? errorCode = null)
    {
        lock (_stateGate)
        {
            OnboardSnapshot current = Current;
            if (_activeOperation is not null || current.State == OnboardState.Faulted)
            {
                PublishCore(current.State, current.Guidance, current.ErrorCode, null, null);
                return;
            }

            VisitContext? visit = _ruleGateway.CurrentVisit;
            if (!_startupValidated || !_ioModule.IsConnected || !_ruleGateway.IsConnected)
            {
                PublishCore(OnboardState.Connecting, preferredGuidance ?? "正在等待仓门控制设备和任务系统连接…", errorCode, null, null);
            }
            else if (!IsExternalSafetyReady())
            {
                PublishCore(
                    OnboardState.Connecting,
                    preferredGuidance ?? GetOperatorMessage("WIRE_TO_GATE_NOT_READY"),
                    errorCode ?? "WIRE_TO_GATE_NOT_READY",
                    null,
                    false);
            }
            else if (!IsAuthoritativeJourneyReady())
            {
                PublishCore(
                    OnboardState.Connecting,
                    preferredGuidance ?? GetOperatorMessage("WIRE_TO_GATE_JOURNEY_NOT_READY"),
                    errorCode ?? "WIRE_TO_GATE_JOURNEY_NOT_READY",
                    null,
                    false);
            }
            else if (visit is null || !visit.IsActive(_clock.Now))
            {
                PublishCore(OnboardState.WaitingArrival, preferredGuidance ?? "设备已连接，等待到站通知…", errorCode, null, null);
            }
            else
            {
                PublishCore(OnboardState.ReadyToScan, preferredGuidance ?? "已到站，请扫描物料条码。", errorCode, null, null);
            }
        }
    }

    // 设置当前操作
    private void SetActiveOperation(ActiveOperation? operation)
    {
        Volatile.Write(ref _activeOperation, operation);
    }

    // 发布完整状态快照
    private void Publish(
        OnboardState state,
        string guidance,
        string? errorCode = null,
        IoSnapshot? ioOverride = null,
        bool? departureOverride = null)
    {
        lock (_stateGate)
        {
            PublishCore(state, guidance, errorCode, ioOverride, departureOverride);
        }
    }

    private void PublishCore(
        OnboardState state,
        string guidance,
        string? errorCode,
        IoSnapshot? ioOverride,
        bool? departureOverride)
    {
        FatalFault? fatalFault = Volatile.Read(ref _fatalFault);
        if (fatalFault is not null)
        {
            state = OnboardState.Faulted;
            guidance = fatalFault.Banner;
            errorCode = fatalFault.ErrorCode;
            departureOverride = false;
        }
        else if (state == OnboardState.ReadyToScan && !IsExternalSafetyReady())
        {
            state = OnboardState.Connecting;
            guidance = GetOperatorMessage("WIRE_TO_GATE_NOT_READY");
            errorCode = "WIRE_TO_GATE_NOT_READY";
            departureOverride = false;
        }
        else if (state == OnboardState.ReadyToScan && !IsAuthoritativeJourneyReady())
        {
            state = OnboardState.Connecting;
            guidance = GetOperatorMessage("WIRE_TO_GATE_JOURNEY_NOT_READY");
            errorCode = "WIRE_TO_GATE_JOURNEY_NOT_READY";
            departureOverride = false;
        }

        IoSnapshot io = ioOverride ?? _ioModule.CurrentSnapshot;
        ActiveOperation? active = _activeOperation;
        OnboardSnapshot snapshot = new(
            state,
            _ruleGateway.IsConnected,
            _ioModule.IsConnected,
            _ruleGateway.CurrentVisit,
            io,
            active,
            departureOverride ?? CalculateDeparture(io, active, state),
            guidance,
            errorCode,
            _clock.Now);
        Volatile.Write(ref _current, snapshot);
        StateChanged?.Invoke(this, new ValueChangedEventArgs<OnboardSnapshot>(snapshot));
    }

    // 技术异常转业务错误码
    private static string GetFailureCode(ActiveOperation? operation, Exception exception)
    {
        return exception switch
        {
            TimeoutException when operation is null => "REQUEST_TIMEOUT",
            IOException when operation is null => "RULE_OFFLINE",
            TimeoutException when operation?.Stage == OperationStage.WritingUnlock => "IO_WRITE_FAILED",
            TimeoutException when operation?.Stage == OperationStage.WaitingUnlockFeedback => "UNLOCK_FEEDBACK_TIMEOUT",
            TimeoutException when operation?.Stage == OperationStage.WaitingUnlockOutputReset => "UNLOCK_OUTPUT_RESET_TIMEOUT",
            TimeoutException when operation?.Stage == OperationStage.WaitingCargoAndRelock => "RELOCK_OR_CARGO_TIMEOUT",
            TimeoutException when operation?.Stage == OperationStage.WaitingOperatorRecovery => "RELOCK_OR_CARGO_TIMEOUT",
            IOException when operation?.Stage == OperationStage.WritingUnlock => "IO_WRITE_FAILED",
            IOException => "IO_OFFLINE",
            InvalidDataException => "INVALID_RULE_RESPONSE",
            InvalidOperationException when exception.Message is
                "WIRE_TO_GATE_NOT_READY"
                or "WIRE_TO_GATE_JOURNEY_NOT_READY"
                or "SUBLOT_NOT_IN_WORKLIST" => exception.Message,
            InvalidOperationException => "RULE_OFFLINE",
            _ => "OPERATION_FAILED"
        };
    }

    // 错误码转成操作员可以直接理解并执行的处理提示。
    // 英文错误码和底层异常仍由调用方写入技术日志，不在主界面显示。
    private string GetOperatorMessage(
        string code,
        int? slotIndex = null,
        OperationType? operationType = null,
        LockerSnapshot? locker = null)
    {
        int? physicalNumber = slotIndex is >= 0 and <= 7 ? slotIndex.Value + 1 : locker?.PhysicalNumber;
        string slotName = physicalNumber.HasValue ? $"{physicalNumber.Value}号仓" : "目标仓";

        return code switch
        {
            "SUBLOT_EMPTY" => "请扫描物料条码。",
            "SUBLOT_TOO_LONG" => "条码内容不正确，请重新扫描。",
            "OPERATION_BUSY" => "当前装卸操作尚未完成，请完成装卸并关好仓门。",
            "NOT_READY" => "车辆尚未准备好，请等待界面显示“可扫码”。",
            "WIRE_TO_GATE_NOT_READY" =>
                "上层安全会话尚未就绪，已禁止扫码、开门和发车。请等待连接及恢复完成。",
            "WIRE_TO_GATE_JOURNEY_NOT_READY" =>
                "服务端旅程或当前站点任务尚未同步，已禁止扫码和开门。请等待任务恢复。",
            "SUBLOT_NOT_IN_WORKLIST" =>
                "当前条码不属于服务端下发的站点任务，请核对条码或等待任务刷新。",
            "VISIT_NOT_ACTIVE" => "车辆尚未到站或本次作业已经结束，请等待新的到站任务。",
            "SUBLOT_NOT_FOUND" => "未找到该条码对应的任务，请核对条码或联系班组长。",
            "SCAN_REJECTED" => "该条码当前不能操作，请核对任务或联系班组长。",
            "RULE_OFFLINE" => "任务系统连接中断，正在自动恢复，请稍候。",
            "REQUEST_TIMEOUT" => "任务确认超时，请稍后重新扫描；持续出现请联系维护人员。",
            "INVALID_RULE_RESPONSE" => "任务信息异常，仓门未打开，请联系维护人员。",
            "DUPLICATE_OPERATION" => "该任务已经处理过，仓门不会再次打开，请勿重复扫描。",
            "IO_OFFLINE" =>
                "仓门控制设备连接中断，所有仓位状态无法确认。已禁止开门和发车，请等待恢复或联系维护人员。",
            "IO_STATE_UNKNOWN" => GetUnknownIoStateOperatorMessage(),
            "IO_SNAPSHOT_STALE" =>
                "仓门控制设备状态长时间未更新，所有仓位状态无法确认。已禁止开门和发车，请等待恢复或联系维护人员。",
            "UNLOCK_OUTPUT_ACTIVE" => GetActiveUnlockOutputOperatorMessage(),
            "SLOT_NOT_LOCKED" => GetUnlockedDoorOperatorMessage(),
            "SLOT_NOT_EMPTY" => $"{slotName}已有货物，不能继续装货，请核对任务。",
            "SLOT_HAS_NO_CARGO" => $"{slotName}当前为空，不能执行取货，请核对任务。",
            "IO_WRITE_FAILED" => $"未能确认是否已向{slotName}发出开门指令，请勿重复扫描或拉动仓门，并联系维护人员。",
            "UNLOCK_FEEDBACK_TIMEOUT" => $"未确认{slotName}已经打开，请勿重复扫描，检查仓门后联系维护人员。",
            "UNLOCK_OUTPUT_RESET_TIMEOUT" =>
                $"{slotName}开门控制未按时复位，禁止继续操作和发车。请勿触碰仓门，并联系维护人员。",
            "RELOCK_OR_CARGO_TIMEOUT" => GetOperationTimeoutOperatorMessage(slotName, operationType, locker),
            "CARGO_EXPECTATION_MISMATCH" => operationType == OperationType.Load
                ? $"未稳定检测到{slotName}内的货物，操作未完成，请联系维护人员。"
                : $"{slotName}内仍检测到货物，操作未完成，请联系维护人员。",
            "RESULT_ACK_TIMEOUT" =>
                "装卸已经完成，但任务系统尚未确认。禁止继续扫码和发车；系统将在连接恢复后自动重报，也可点击“重新上报结果”。",
            "PRECHECK_RESULT_ACK_TIMEOUT" =>
                "仓门未打开，但任务系统尚未确认本次未执行结果。禁止继续扫码和发车，请等待自动重报或点击“重新上报结果”。",
            "OPERATION_CANCELLED_BY_OPERATOR" => $"{slotName}操作已取消，仓门保持锁闭，可以继续扫码。",
            "CANCEL_RESULT_ACK_TIMEOUT" =>
                "本次操作已在车上取消，但任务系统尚未确认。禁止继续扫码和发车；系统将在连接恢复后自动重报，也可点击“重新上报结果”。",
            "OPERATION_CANCELLED" => "本次操作已中断，请确认仓门和货物状态，并联系维护人员。",
            "OPERATION_FAILED" => "本次操作未完成，已禁止发车，请联系维护人员。",
            _ => "该条码当前不能操作，请核对任务或联系班组长。"
        };
    }

    private string GetUnknownIoStateOperatorMessage()
    {
        IoSnapshot snapshot = _ioModule.CurrentSnapshot;
        if (snapshot.Lockers.Count != 8)
        {
            return "仓位状态数据不完整，已禁止开门和发车，请等待恢复或联系维护人员。";
        }

        int[] unknownNumbers = snapshot.Lockers
            .Where(locker => !locker.IsKnown)
            .Select(locker => locker.PhysicalNumber)
            .Order()
            .ToArray();
        return unknownNumbers.Length switch
        {
            0 => "仓位状态无法确认，已禁止开门和发车，请等待恢复或联系维护人员。",
            1 => $"{unknownNumbers[0]}号仓状态无法确认，已禁止开门，请等待恢复或联系维护人员。",
            8 => "所有仓位状态无法确认，已禁止开门和发车，请等待恢复或联系维护人员。",
            _ => $"多个仓位状态无法确认（{FormatPhysicalNumbers(unknownNumbers)}），已禁止开门和发车，请等待恢复或联系维护人员。"
        };
    }

    private string GetActiveUnlockOutputOperatorMessage()
    {
        int[] activeNumbers = _ioModule.CurrentSnapshot.Lockers
            .Where(locker => locker.UnlockOutputRaw is not false)
            .Select(locker => locker.PhysicalNumber)
            .Order()
            .ToArray();
        return activeNumbers.Length switch
        {
            0 => "开门控制尚未复位，请勿重复扫描；持续不恢复请联系维护人员。",
            1 => $"{activeNumbers[0]}号仓开门控制尚未复位，请勿重复扫描；持续不恢复请联系维护人员。",
            _ => $"多个仓位的开门控制尚未复位（{FormatPhysicalNumbers(activeNumbers)}），请勿重复扫描并联系维护人员。"
        };
    }

    private string GetUnlockedDoorOperatorMessage()
    {
        int[] unlockedNumbers = _ioModule.CurrentSnapshot.Lockers
            .Where(locker => locker.IsKnown && !locker.IsLocked)
            .Select(locker => locker.PhysicalNumber)
            .Order()
            .ToArray();
        return unlockedNumbers.Length switch
        {
            0 => "检测到仓门未关好，请关好所有仓门后重新扫描。",
            1 => $"{unlockedNumbers[0]}号仓门未关好，请关紧仓门后重新扫描。",
            _ => $"多个仓门未关好（{FormatPhysicalNumbers(unlockedNumbers)}），请全部关紧后重新扫描。"
        };
    }

    private static string FormatPhysicalNumbers(IEnumerable<int> physicalNumbers) =>
        string.Join("、", physicalNumbers.Select(number => $"{number}号"));

    private static string GetOperationTimeoutOperatorMessage(
        string slotName,
        OperationType? operationType,
        LockerSnapshot? locker)
    {
        if (locker is null || !locker.IsKnown)
        {
            return $"未读取到{slotName}的状态，请确认仓门并联系维护人员。";
        }

        if (!locker.IsLocked)
        {
            return $"{slotName}门尚未关好，请关紧仓门；系统已禁止发车。";
        }

        if (operationType == OperationType.Load && !locker.HasCargo)
        {
            return $"未检测到{slotName}内的货物，请检查货物是否放置到位。";
        }

        if (operationType == OperationType.Unload && locker.HasCargo)
        {
            return $"{slotName}内仍检测到货物，请确认货物已经全部取出。";
        }

        return $"{slotName}操作尚未完成，请检查仓门和货物状态。";
    }

    private bool CalculateDeparture(IoSnapshot io, ActiveOperation? active, OnboardState state)
    {
        return IsExternalSafetyReady()
            && SafetyRules.IsDeparturePermitted(
                io,
                active,
                state == OnboardState.Faulted,
                _clock.Now,
                _options.IoSnapshotMaxAge);
    }

    private bool IsExternalSafetyReady()
    {
        try
        {
            return _externalSafetyReadyProvider();
        }
        catch (Exception exception)
        {
            _logger.Write(
                LogSeverity.Error,
                nameof(OnboardController),
                "读取上层安全会话门禁失败，已按未就绪处理。",
                exception);
            return false;
        }
    }

    private bool IsAuthoritativeJourneyReady()
    {
        if (_journeyProvider is null)
        {
            return true;
        }

        try
        {
            return _journeyProvider()?.CanAcceptSublot == true;
        }
        catch (Exception exception)
        {
            _logger.Write(
                LogSeverity.Error,
                nameof(OnboardController),
                "读取服务端旅程投影失败，已按未就绪处理。",
                exception);
            return false;
        }
    }

    private string? ValidateJourneySublot(string sublot)
    {
        if (_journeyProvider is null)
        {
            return null;
        }

        // Membership of the items' sublots, not equality with "the" item: a stop carries up to eight
        // demands since batch 7-13 (8005-agv-onboard-hmi#134). Which demand the sublot belongs to is
        // the control server's to bind; this end never picks one.
        IReadOnlyList<WireToGateWorklistItem> items = _journeyProvider()?.CurrentStopWorklist?.Items ?? [];
        return items.Count == 0
            ? "WIRE_TO_GATE_JOURNEY_NOT_READY"
            : items.Any(item => string.Equals(item.Sublot, sublot, StringComparison.Ordinal))
                ? null
                : "SUBLOT_NOT_IN_WORKLIST";
    }

    private void ThrowIfExternalSafetyNotReady()
    {
        if (!IsExternalSafetyReady())
        {
            throw new InvalidOperationException("WIRE_TO_GATE_NOT_READY");
        }
    }

    private void ThrowIfAuthoritativeJourneyNotReady(string sublot)
    {
        if (_journeyProvider is null)
        {
            return;
        }

        if (!IsAuthoritativeJourneyReady())
        {
            throw new InvalidOperationException("WIRE_TO_GATE_JOURNEY_NOT_READY");
        }

        string? journeySublotError = ValidateJourneySublot(sublot);
        if (journeySublotError is not null)
        {
            throw new InvalidOperationException(journeySublotError);
        }
    }

    private void ThrowIfFatalFaultLatched(CancellationToken cancellationToken)
    {
        FatalFault? fatalFault = Volatile.Read(ref _fatalFault);
        if (fatalFault is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        throw new OperationCanceledException(
            $"严重安全故障已锁存：{fatalFault.ErrorCode}",
            cancellationToken);
    }

    private string? ValidateRecoverableStartupSnapshot(IoSnapshot snapshot)
    {
        if (!snapshot.IsConnected)
        {
            return "仓门控制设备连接中断，请等待恢复。";
        }

        if (!SafetyRules.IsSnapshotFresh(snapshot, _clock.Now, _options.IoSnapshotMaxAge))
        {
            return "仓位状态长时间没有更新，请等待恢复。";
        }

        if (snapshot.Lockers.Count != 8 || snapshot.Lockers.Any(locker => !locker.IsKnown))
        {
            return "部分仓位状态未读取到，请等待恢复或联系维护人员。";
        }

        LockerSnapshot? outputActive = snapshot.Lockers.FirstOrDefault(locker => locker.UnlockOutputRaw is not false);
        if (outputActive is not null)
        {
            return $"{outputActive.PhysicalNumber}号仓开门控制尚未复位，请勿操作仓门。";
        }

        LockerSnapshot? unlocked = snapshot.Lockers.FirstOrDefault(locker => !locker.IsLocked);
        if (unlocked is not null)
        {
            return $"{unlocked.PhysicalNumber}号仓门未关好，请关紧仓门。";
        }

        return null;
    }

    private static string DescribeStartupOperatorMessage(IoSnapshot snapshot)
    {
        if (snapshot.Lockers.Count != 8)
        {
            return "启动检查未通过：仓位状态数据不完整。请等待恢复；如持续异常，请联系维护人员。";
        }

        int[] unknownNumbers = snapshot.Lockers
            .Where(locker => !locker.IsKnown)
            .Select(locker => locker.PhysicalNumber)
            .Order()
            .ToArray();
        if (unknownNumbers.Length == 8)
        {
            return "启动检查未通过：所有仓位状态无法确认。请等待恢复；如持续异常，请联系维护人员。";
        }

        if (unknownNumbers.Length > 1)
        {
            return $"启动检查未通过：多个仓位状态无法确认（{FormatPhysicalNumbers(unknownNumbers)}）。请等待恢复；如持续异常，请联系维护人员。";
        }

        bool cargoOnly = snapshot.Lockers.Count == 8
            && snapshot.Lockers.All(locker => locker.IsKnown
                && locker.UnlockOutputRaw is false
                && locker.IsLocked)
            && snapshot.Lockers.Any(locker => locker.HasCargo);
        if (cargoOnly)
        {
            return "车辆启动时检测到仓内已有货物，请核对界面与现场状态，确认无误后点击“启动安全复核”。";
        }

        List<string> issues = [];
        foreach (LockerSnapshot locker in snapshot.Lockers)
        {
            if (!locker.IsKnown)
            {
                issues.Add($"{locker.PhysicalNumber}号仓状态读取失败");
                continue;
            }

            if (locker.UnlockOutputRaw is not false)
            {
                issues.Add($"{locker.PhysicalNumber}号仓开门控制尚未复位");
            }

            if (!locker.IsLocked)
            {
                issues.Add($"{locker.PhysicalNumber}号仓门未关好");
            }

            if (locker.HasCargo)
            {
                issues.Add($"{locker.PhysicalNumber}号仓内已有货物");
            }
        }

        string detail = string.Join("；", issues.Take(3));
        if (issues.Count > 3)
        {
            detail += $"；另有{issues.Count - 3}项异常";
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = "部分仓位状态异常";
        }

        return $"启动检查未通过：{detail}。请处理后点击“启动安全复核”；如无法恢复，请联系维护人员。";
    }

    private static string DescribeStartupSnapshot(IoSnapshot snapshot)
    {
        IEnumerable<string> abnormal = snapshot.Lockers
            .Where(locker => !locker.IsKnown
                || locker.UnlockOutputRaw is not false
                || !locker.IsLocked
                || locker.HasCargo)
            .Select(locker => $"{locker.PhysicalNumber}号仓(DO={ToRaw(locker.UnlockOutputRaw)},锁DI={ToRaw(locker.LockFeedbackRaw)},光幕DI={ToRaw(locker.LightCurtainRaw)})");
        string detail = string.Join("；", abnormal);
        return string.IsNullOrEmpty(detail) ? "未找到具体异常点" : detail;
    }

    private static string ToRaw(bool? value) => value.HasValue ? (value.Value ? "1" : "0") : "?";

    private enum OperatorRecoveryAction
    {
        Reopen,
        Cancel
    }

    private sealed record RecoveryWaitResult(
        OperatorRecoveryAction? Action,
        LockerSnapshot? CompletedLocker);

    private sealed record OperationRecoveryOutcome(
        ActiveOperation Operation,
        LockerSnapshot Locker,
        bool Cancelled);

    /// <param name="Guidance">
    /// The banner the latch itself carries. It never changes once latched, so a refused clearance
    /// attempt cannot erase it (8005-agv-onboard-hmi#171).
    /// </param>
    private sealed record FatalFault(string ErrorCode, string Guidance)
    {
        /// <summary>Why the most recent clearance attempt was refused, or null when none was.</summary>
        public string? ClearanceRefusal { get; init; }

        /// <summary>
        /// What the operator reads. <see cref="PublishCore"/> rewrites every publish back to this
        /// while the latch stands, so a refusal has to be inside the latch to survive -- publishing
        /// it as an ordinary guidance string would be overwritten by the very next line of that
        /// method.
        /// </summary>
        public string Banner => ClearanceRefusal is null
            ? Guidance
            : $"{Guidance} 复位未通过：{ClearanceRefusal}";
    }

    private enum PendingReportKind
    {
        SuccessfulOperation,
        CancelledOperation,
        FailedOperation,
        PrecheckFailure
    }

    private sealed record PendingOperationReport(
        OperationResult Result,
        PendingReportKind Kind,
        ActiveOperation Operation,
        string AcknowledgedGuidance);

    // 异步释放资源
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _ioModule.ConnectionChanged -= OnIoConnectionChanged;
        _ioModule.SnapshotChanged -= OnIoSnapshotChanged;
        _ruleGateway.ConnectionChanged -= OnRuleConnectionChanged;
        _ruleGateway.VisitChanged -= OnVisitChanged;
        _lifetimeCts?.Dispose();
        _operationLock.Dispose();
        _pendingReportSendLock.Dispose();
    }
}
