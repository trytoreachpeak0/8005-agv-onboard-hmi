using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using CountdownTier = SQCD.Agv.Application.StationDepartureCountdownTier;

namespace SQCD.Agv.Wpf.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private const int MaxLogEntries = 300;
    private readonly OnboardController _controller;
    private readonly IAppLogger _logger;
    private readonly IClock _clock;
    private readonly OperatorRecordFormatter _operatorRecordFormatter = new();
    private DispatcherTimer? _stationDepartureCountdownTimer;
    private string _scanText = string.Empty;
    private string _ruleConnectionText = "离线";
    private string _ioConnectionText = "离线";
    private string _wireToGateText = "未启用";
    private string _visitText = "未到站";
    private string _departureText = "禁止发车";
    private string _stateText = "启动中";
    private string _guidance = "系统正在启动…";
    private bool _canSubmit;
    private bool _hasError;
    private bool _hasWarning;
    private bool _canSafetyReview;
    private bool _canReopenOperation;
    private bool _canCancelOperation;
    private bool _canRetryPendingResult;
    private bool _canRequestWireToGateRecovery;
    private bool _canRequestLoadCancellation;
    private bool _canRequestLoadCompensation;
    private bool _canRequestLoadCorrection;
    private bool _canRequestFaultCargoHandoff;
    private bool _hasWireToGateJourney;
    private bool _hasUpcomingPlan;
    private bool _hasStationDepartureCountdown;
    private DateTimeOffset? _stationDepartureDeadlineAt;
    private string _stationDepartureCountdownText = string.Empty;
    private string _stationDepartureCountdownTier = nameof(CountdownTier.Absent);
    private bool _stationDepartureCountdownDimmed;
    private bool _wireToGateEnabled;
    private WireToGateSessionSnapshot? _wireToGateSession;
    private WireToGateHmiOperationSnapshot? _wireToGateOperation;
    private OnboardSnapshot? _lastControllerSnapshot;
    private string _recoverySlotName = "当前仓";
    private string? _lastLoggedErrorKey;
    private Func<string, ScanInputMethod, CancellationToken, Task>? _wireToGateSubmitter;
    private Func<bool>? _wireToGateCanSubmit;
    private Func<bool>? _wireToGateCanRequestRecovery;
    private Func<CancellationToken, Task<bool>>? _wireToGateRecoveryRequester;
    private Func<bool>? _wireToGateCanRequestLoadCancellation;
    private Func<bool>? _wireToGateCanRequestLoadCompensation;
    private Func<bool>? _wireToGateCanRequestLoadCorrection;
    private Func<bool>? _wireToGateCanRequestFaultCargoHandoff;
    private Func<CancellationToken, Task<bool>>? _wireToGateLoadCancellationRequester;
    private Func<CancellationToken, Task<bool>>? _wireToGateLoadCompensationRequester;
    private Func<CancellationToken, Task<bool>>? _wireToGateLoadCorrectionRequester;
    private Func<CancellationToken, Task<bool>>? _wireToGateFaultCargoHandoffRequester;

    public MainViewModel(
        OnboardController controller,
        IAppLogger logger,
        IClock clock,
        string agvId)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        AgvId = agvId;
        Lockers = new ObservableCollection<LockerCardViewModel>(
            Enumerable.Range(0, 8).Select(index => new LockerCardViewModel(index)));
        ScannerSubmitCommand = new AsyncCommand(
            () => SubmitAsync(ScanInputMethod.Scanner),
            () => CanSubmit && !string.IsNullOrWhiteSpace(ScanText),
            HandleCommandError);
        ManualSubmitCommand = new AsyncCommand(
            () => SubmitAsync(ScanInputMethod.Manual),
            () => CanSubmit && !string.IsNullOrWhiteSpace(ScanText),
            HandleCommandError);
        ClearLogsCommand = new AsyncCommand(
            () =>
            {
                Logs.Clear();
                return Task.CompletedTask;
            },
            () => Logs.Count > 0,
            HandleCommandError);
    }

    public string AgvId { get; }

    public ObservableCollection<LockerCardViewModel> Lockers { get; }

    public ObservableCollection<LogLineViewModel> Logs { get; } = [];

    /// <summary>
    /// 后续停靠序列，按 sequence 升序。协议保证 sequence 从 1 起连续，但**不保证数组本身有序**
    /// ——校验那边也是 OrderBy 之后才检查连续性的，所以这里要自己排。
    /// </summary>
    public ObservableCollection<StopLegViewModel> UpcomingLegs { get; } = [];

    public bool HasUpcomingPlan
    {
        get => _hasUpcomingPlan;
        private set => SetProperty(ref _hasUpcomingPlan, value);
    }

    /// <summary>
    /// 是否显示本站倒计时。只要这一站的作业清单已经同步就显示——**没有截止时间也要显示**，
    /// 那时写「无倒计时」。整块藏起来会让「服务端没给期限」和「清单还没到」看起来一模一样。
    /// </summary>
    public bool HasStationDepartureCountdown
    {
        get => _hasStationDepartureCountdown;
        private set => SetProperty(ref _hasStationDepartureCountdown, value);
    }

    public string StationDepartureCountdownText
    {
        get => _stationDepartureCountdownText;
        private set => SetProperty(ref _stationDepartureCountdownText, value);
    }

    /// <summary>
    /// <c>StationDepartureCountdownTier</c> 枚举值的名字，供 XAML 的 DataTrigger 选配色。
    /// </summary>
    public string StationDepartureCountdownTier
    {
        get => _stationDepartureCountdownTier;
        private set => SetProperty(ref _stationDepartureCountdownTier, value);
    }

    /// <summary>最后 10 秒逐秒闪烁时的「灭」相位。</summary>
    public bool StationDepartureCountdownDimmed
    {
        get => _stationDepartureCountdownDimmed;
        private set => SetProperty(ref _stationDepartureCountdownDimmed, value);
    }

    public AsyncCommand ScannerSubmitCommand { get; }

    public AsyncCommand ManualSubmitCommand { get; }

    public AsyncCommand ClearLogsCommand { get; }

    public string ScanText
    {
        get => _scanText;
        set
        {
            if (SetProperty(ref _scanText, value))
            {
                ScannerSubmitCommand.RaiseCanExecuteChanged();
                ManualSubmitCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RuleConnectionText
    {
        get => _ruleConnectionText;
        private set => SetProperty(ref _ruleConnectionText, value);
    }

    public string IoConnectionText
    {
        get => _ioConnectionText;
        private set => SetProperty(ref _ioConnectionText, value);
    }

    public string WireToGateText
    {
        get => _wireToGateText;
        private set => SetProperty(ref _wireToGateText, value);
    }

    internal void UpdateWireToGateStatus(WireToGateSessionSnapshot snapshot) => RunOnUiThread(() =>
    {
        _wireToGateSession = snapshot;
        WireToGateText = !snapshot.Connected
            ? "离线"
            : snapshot.Readiness switch
            {
                WireToGateSessionReadiness.Recovering => "恢复中",
                WireToGateSessionReadiness.RecoveryRequired => "需恢复",
                WireToGateSessionReadiness.Ready => "就绪",
                _ => "连接中"
            };
        RuleConnectionText = snapshot.Connected ? "在线" : "离线";
        RefreshWireToGateInputStateCore();
        ApplyWireToGatePresentationCore();
    });

    internal void UpdateWireToGateJourney(WireToGateJourneySnapshot snapshot) => RunOnUiThread(() =>
    {
        _hasWireToGateJourney = true;
        _stationDepartureDeadlineAt = snapshot.CurrentStopWorklist?.StationDepartureDeadlineAt;
        HasStationDepartureCountdown = snapshot.CurrentStopWorklist is not null;
        RefreshStationDepartureCountdownCore();
        if (snapshot.CurrentStopWorklist is { } worklist)
        {
            // 一次停靠可以有多项。原先这里取 SingleOrDefault()，两项就抛，而这条路径跑在 UI 线程
            // 的更新回调里——多单的第一份清单会让界面停在上一次的文字上，看不出发生了什么。
            VisitText = worklist.Items.Count switch
            {
                0 => $"{worklist.StationId} / 无待处理任务",
                1 => $"{worklist.StationId} / {worklist.Items[0].Sublot}",
                _ => $"{worklist.StationId} / {worklist.Items.Count} 项：" +
                     string.Join('、', worklist.Items.Select(item => item.Sublot))
            };
        }
        else
        {
            VisitText = "旅程未同步";
        }
        UpdateUpcomingLegsCore(snapshot.UpcomingStopPlan);
        RefreshWireToGateInputStateCore();
        ApplyWireToGatePresentationCore();
    });

    private void UpdateUpcomingLegsCore(WireToGateUpcomingStopPlan? plan)
    {
        UpcomingLegs.Clear();
        foreach (WireToGateMovementLeg leg in (plan?.Legs ?? [])
            .OrderBy(leg => leg.Sequence))
        {
            UpcomingLegs.Add(new StopLegViewModel(leg));
        }

        HasUpcomingPlan = UpcomingLegs.Count > 0;
    }

    internal void ConfigureWireToGate(
        Func<string, ScanInputMethod, CancellationToken, Task> submitter,
        Func<bool> canSubmit,
        Func<bool>? canRequestRecovery = null,
        Func<CancellationToken, Task<bool>>? recoveryRequester = null,
        Func<bool>? canRequestLoadCancellation = null,
        Func<CancellationToken, Task<bool>>? loadCancellationRequester = null,
        Func<bool>? canRequestLoadCompensation = null,
        Func<CancellationToken, Task<bool>>? loadCompensationRequester = null,
        Func<bool>? canRequestLoadCorrection = null,
        Func<CancellationToken, Task<bool>>? loadCorrectionRequester = null,
        Func<bool>? canRequestFaultCargoHandoff = null,
        Func<CancellationToken, Task<bool>>? faultCargoHandoffRequester = null)
    {
        _wireToGateSubmitter = submitter ?? throw new ArgumentNullException(nameof(submitter));
        _wireToGateCanSubmit = canSubmit ?? throw new ArgumentNullException(nameof(canSubmit));
        _wireToGateCanRequestRecovery = canRequestRecovery;
        _wireToGateRecoveryRequester = recoveryRequester;
        _wireToGateCanRequestLoadCancellation = canRequestLoadCancellation;
        _wireToGateLoadCancellationRequester = loadCancellationRequester;
        _wireToGateCanRequestLoadCompensation = canRequestLoadCompensation;
        _wireToGateLoadCompensationRequester = loadCompensationRequester;
        _wireToGateCanRequestLoadCorrection = canRequestLoadCorrection;
        _wireToGateLoadCorrectionRequester = loadCorrectionRequester;
        _wireToGateCanRequestFaultCargoHandoff = canRequestFaultCargoHandoff;
        _wireToGateFaultCargoHandoffRequester = faultCargoHandoffRequester;
        _wireToGateEnabled = true;
        RefreshWireToGateInputStateCore();
        ApplyWireToGatePresentationCore();
    }

    internal void ApplyWireToGateOperatorEvent(WireToGateOperatorEvent operatorEvent) =>
        RunOnUiThread(() =>
        {
            ArgumentNullException.ThrowIfNull(operatorEvent);
            if (operatorEvent.Operation is not null)
            {
                _wireToGateOperation = operatorEvent.Operation.Stage == WireToGateHmiOperationStage.Completed
                    ? null
                    : operatorEvent.Operation;
            }

            Logs.Add(new LogLineViewModel(
                operatorEvent.Timestamp,
                MapOperatorEventKind(operatorEvent.Kind),
                operatorEvent.Message));
            ClearLogsCommand.RaiseCanExecuteChanged();
            TrimLogs();
            ApplyWireToGatePresentationCore();
            RefreshLockerCardsCore();
        });

    internal void RefreshWireToGateInputState() => RunOnUiThread(RefreshWireToGateInputStateCore);

    private void RefreshWireToGateInputStateCore()
    {
        CanSubmit = _wireToGateCanSubmit?.Invoke() ?? CanSubmit;
        CanRequestWireToGateRecovery = _wireToGateCanRequestRecovery?.Invoke() == true;
        CanRequestLoadCancellation = _wireToGateCanRequestLoadCancellation?.Invoke() == true;
        CanRequestLoadCompensation = _wireToGateCanRequestLoadCompensation?.Invoke() == true;
        CanRequestLoadCorrection = _wireToGateCanRequestLoadCorrection?.Invoke() == true;
        CanRequestFaultCargoHandoff = _wireToGateCanRequestFaultCargoHandoff?.Invoke() == true;
    }

    public string VisitText
    {
        get => _visitText;
        private set => SetProperty(ref _visitText, value);
    }

    public string DepartureText
    {
        get => _departureText;
        private set => SetProperty(ref _departureText, value);
    }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public string Guidance
    {
        get => _guidance;
        private set => SetProperty(ref _guidance, value);
    }

    public bool CanSubmit
    {
        get => _canSubmit;
        private set
        {
            if (SetProperty(ref _canSubmit, value))
            {
                ScannerSubmitCommand.RaiseCanExecuteChanged();
                ManualSubmitCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public bool HasWarning
    {
        get => _hasWarning;
        private set => SetProperty(ref _hasWarning, value);
    }

    public bool CanSafetyReview
    {
        get => _canSafetyReview;
        private set => SetProperty(ref _canSafetyReview, value);
    }

    public bool CanReopenOperation
    {
        get => _canReopenOperation;
        private set => SetProperty(ref _canReopenOperation, value);
    }

    public bool CanCancelOperation
    {
        get => _canCancelOperation;
        private set => SetProperty(ref _canCancelOperation, value);
    }

    public bool CanRetryPendingResult
    {
        get => _canRetryPendingResult;
        private set => SetProperty(ref _canRetryPendingResult, value);
    }

    public bool CanRequestWireToGateRecovery
    {
        get => _canRequestWireToGateRecovery;
        private set => SetProperty(ref _canRequestWireToGateRecovery, value);
    }

    public bool CanRequestLoadCancellation
    {
        get => _canRequestLoadCancellation;
        private set => SetProperty(ref _canRequestLoadCancellation, value);
    }

    public bool CanRequestLoadCompensation
    {
        get => _canRequestLoadCompensation;
        private set => SetProperty(ref _canRequestLoadCompensation, value);
    }

    public bool CanRequestLoadCorrection
    {
        get => _canRequestLoadCorrection;
        private set => SetProperty(ref _canRequestLoadCorrection, value);
    }

    public bool CanRequestFaultCargoHandoff
    {
        get => _canRequestFaultCargoHandoff;
        private set => SetProperty(ref _canRequestFaultCargoHandoff, value);
    }

    public string RecoverySlotName
    {
        get => _recoverySlotName;
        private set => SetProperty(ref _recoverySlotName, value);
    }

    public async Task InitializeAsync()
    {
        _controller.StateChanged += OnStateChanged;
        StartStationDepartureCountdownTimer();
        ApplySnapshot(_controller.Current);
        await _controller.StartAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 重算本站倒计时。截止时间是个绝对时刻，剩余时间只能由它和当前时间算出来——期限本身会在
    /// 同一次停靠内往后跳，所以这里不保留任何递减状态。
    /// </summary>
    internal void RefreshStationDepartureCountdown() =>
        RunOnUiThread(RefreshStationDepartureCountdownCore);

    private void StartStationDepartureCountdownTimer()
    {
        if (_stationDepartureCountdownTimer is not null)
        {
            return;
        }

        // 250 ms 而不是 1 s：最后 10 秒要逐秒闪烁，而闪烁相位取自绝对秒数。刷新节拍等于秒长时，
        // 它与秒边界的相对位置会漂移，跨过边界那一下会连着两次落在同一相位上，看着像卡住。
        _stationDepartureCountdownTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _stationDepartureCountdownTimer.Tick += (_, _) => RefreshStationDepartureCountdownCore();
        _stationDepartureCountdownTimer.Start();
    }

    private void RefreshStationDepartureCountdownCore()
    {
        StationDepartureCountdownView view =
            StationDepartureCountdownFormatter.Format(_stationDepartureDeadlineAt, _clock.Now);
        StationDepartureCountdownText = view.Text;
        StationDepartureCountdownTier = view.Tier.ToString();
        StationDepartureCountdownDimmed = view.Dimmed;
    }

    private async Task SubmitAsync(ScanInputMethod inputMethod)
    {
        string sublot = ScanText;
        ScanText = string.Empty;
        if (_wireToGateSubmitter is not null)
        {
            await _wireToGateSubmitter(sublot, inputMethod, CancellationToken.None).ConfigureAwait(true);
            return;
        }

        await _controller.SubmitScanAsync(sublot, inputMethod).ConfigureAwait(true);
    }

    public Task<bool> ConfirmSafeStartupStateAsync() => _controller.ConfirmSafeStartupStateAsync();

    public Task<bool> RequestReopenCurrentOperationAsync() =>
        Task.FromResult(_controller.RequestReopenCurrentOperation());

    public Task<bool> RequestCancelCurrentOperationAsync() =>
        Task.FromResult(_controller.RequestCancelCurrentOperation());

    public Task<bool> RetryPendingResultAsync() => _controller.RetryPendingResultAsync();

    public Task<bool> RequestWireToGateRecoveryAsync(CancellationToken cancellationToken = default) =>
        _wireToGateRecoveryRequester is null
            ? Task.FromResult(false)
            : _wireToGateRecoveryRequester(cancellationToken);

    public Task<bool> RequestLoadCancellationAsync(CancellationToken cancellationToken = default) =>
        _wireToGateLoadCancellationRequester is null
            ? Task.FromResult(false)
            : _wireToGateLoadCancellationRequester(cancellationToken);

    public Task<bool> RequestLoadCompensationAsync(CancellationToken cancellationToken = default) =>
        _wireToGateLoadCompensationRequester is null
            ? Task.FromResult(false)
            : _wireToGateLoadCompensationRequester(cancellationToken);

    public Task<bool> RequestLoadCorrectionAsync(CancellationToken cancellationToken = default) =>
        _wireToGateLoadCorrectionRequester is null
            ? Task.FromResult(false)
            : _wireToGateLoadCorrectionRequester(cancellationToken);

    public Task<bool> RequestFaultCargoHandoffAsync(CancellationToken cancellationToken = default) =>
        _wireToGateFaultCargoHandoffRequester is null
            ? Task.FromResult(false)
            : _wireToGateFaultCargoHandoffRequester(cancellationToken);

    private void OnStateChanged(object? sender, ValueChangedEventArgs<OnboardSnapshot> args)
    {
        RunOnUiThread(() => ApplySnapshot(args.Value));
    }

    private void ApplySnapshot(OnboardSnapshot snapshot)
    {
        _lastControllerSnapshot = snapshot;
        RuleConnectionText = snapshot.RuleConnected ? "在线" : "离线";
        IoConnectionText = snapshot.IoConnected ? "在线" : "离线";
        if (!_hasWireToGateJourney)
        {
            VisitText = snapshot.Visit is null ? "未到站" : $"{snapshot.Visit.StationName} / {snapshot.Visit.VisitId}";
        }
        DepartureText = snapshot.DeparturePermitted ? "允许发车" : "禁止发车";
        bool hasBlockingError = snapshot.State == OnboardState.Faulted;
        bool hasWarning = !hasBlockingError && !string.IsNullOrWhiteSpace(snapshot.ErrorCode);
        StateText = snapshot.ErrorCode == "STARTUP_STATE_UNSAFE"
            ? "启动检查未通过"
            : hasBlockingError
                ? "设备异常"
                : hasWarning
                    ? "请处理"
                    : GetStateText(snapshot.State);
        Guidance = snapshot.Guidance;
        CanSubmit = _wireToGateCanSubmit?.Invoke() ?? snapshot.State == OnboardState.ReadyToScan;
        HasWarning = hasWarning;
        HasError = hasBlockingError;
        CanSafetyReview = snapshot.State == OnboardState.Faulted
            && snapshot.ErrorCode == "STARTUP_STATE_UNSAFE"
            && snapshot.ActiveOperation is null;
        CanReopenOperation = _controller.CanReopenCurrentOperation;
        CanCancelOperation = _controller.CanCancelCurrentOperation;
        CanRetryPendingResult = _controller.CanRetryPendingResult;
        RecoverySlotName = snapshot.ActiveOperation is null
            ? "当前仓"
            : $"{snapshot.ActiveOperation.SlotIndex + 1}号仓";

        foreach (LockerCardViewModel locker in Lockers)
        {
            LockerSnapshot state = snapshot.Io.GetLocker(locker.SlotIndex);
            locker.Update(state, snapshot.ActiveOperation, _wireToGateOperation);
        }

        AppendOperatorRecord(snapshot);
        LogOperatorVisibleError(snapshot);
        ApplyWireToGatePresentationCore();
    }

    private void HandleCommandError(Exception exception)
    {
        _logger.Write(LogSeverity.Error, nameof(MainViewModel), "界面命令执行失败。", exception);
        _controller.EnterFatalFault(
            "UI_COMMAND_FAILED",
            "操作界面出现异常，已停止开门。请确认仓门状态并联系维护人员。");
        Guidance = "操作界面出现异常，已停止开门。请确认仓门状态并联系维护人员。";
        HasError = true;
    }

    private void AppendOperatorRecord(OnboardSnapshot snapshot)
    {
        OperatorRecord? record = _operatorRecordFormatter.Format(snapshot);
        if (record is null)
        {
            return;
        }

        Logs.Add(new LogLineViewModel(record.Timestamp, record.Kind, record.Message));
        ClearLogsCommand.RaiseCanExecuteChanged();
        TrimLogs();
    }

    private void ApplyWireToGatePresentationCore()
    {
        if (!_wireToGateEnabled || _wireToGateSession is null)
        {
            return;
        }

        if (_lastControllerSnapshot?.State == OnboardState.Faulted)
        {
            CanRequestWireToGateRecovery = false;
            CanRequestLoadCancellation = false;
            CanRequestLoadCompensation = false;
            CanRequestLoadCorrection = false;
            CanRequestFaultCargoHandoff = false;
            return;
        }

        WireToGateHmiBanner banner = WireToGateHmiPresentation.Create(
            _wireToGateSession,
            _wireToGateOperation,
            _wireToGateCanSubmit?.Invoke() == true);
        RuleConnectionText = _wireToGateSession.Connected ? "在线" : "离线";
        StateText = banner.StateText;
        Guidance = banner.Guidance;
        HasWarning = banner.HasWarning;
        HasError = banner.HasError;
        CanReopenOperation = false;
        CanCancelOperation = false;
        CanRetryPendingResult = false;
        CanRequestWireToGateRecovery = _wireToGateCanRequestRecovery?.Invoke() == true
            && _wireToGateOperation?.Stage == WireToGateHmiOperationStage.RecoveryRequired;
        CanRequestLoadCancellation = _wireToGateCanRequestLoadCancellation?.Invoke() == true;
        CanRequestLoadCompensation = _wireToGateCanRequestLoadCompensation?.Invoke() == true;
        CanRequestLoadCorrection = _wireToGateCanRequestLoadCorrection?.Invoke() == true;
        CanRequestFaultCargoHandoff = _wireToGateCanRequestFaultCargoHandoff?.Invoke() == true;
    }

    private void RefreshLockerCardsCore()
    {
        OnboardSnapshot? snapshot = _lastControllerSnapshot;
        if (snapshot is null)
        {
            return;
        }

        foreach (LockerCardViewModel locker in Lockers)
        {
            locker.Update(
                snapshot.Io.GetLocker(locker.SlotIndex),
                snapshot.ActiveOperation,
                _wireToGateOperation);
        }
    }

    private void TrimLogs()
    {
        while (Logs.Count > MaxLogEntries)
        {
            Logs.RemoveAt(0);
        }
    }

    private static OperatorRecordKind MapOperatorEventKind(string kind) => kind switch
    {
        "OPERATION_COMPLETED" => OperatorRecordKind.Success,
        "OPERATION_RECOVERY_REQUIRED" or "RECOVERY_BLOCKED" => OperatorRecordKind.Error,
        "RESULT_ACK_PENDING" or "RECOVERY_AUTHORIZED" => OperatorRecordKind.Warning,
        "SUBLOT_ENTRY_REQUESTED" or "SUBLOT_SUBMITTED" or "OPERATION_PROGRESS" or "OPERATION_REPLAY" =>
            OperatorRecordKind.Operation,
        _ => OperatorRecordKind.System
    };

    private void LogOperatorVisibleError(OnboardSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ErrorCode))
        {
            _lastLoggedErrorKey = null;
            return;
        }

        string errorKey = $"{snapshot.ErrorCode}|{snapshot.Guidance}|{snapshot.ActiveOperation?.OperationId}";
        if (errorKey == _lastLoggedErrorKey)
        {
            return;
        }

        _lastLoggedErrorKey = errorKey;
        _logger.Write(
            snapshot.State == OnboardState.Faulted ? LogSeverity.Error : LogSeverity.Warning,
            nameof(MainViewModel),
            $"操作员提示：code={snapshot.ErrorCode}，guidance={snapshot.Guidance}。");
    }

    private static string GetStateText(OnboardState state)
    {
        return state switch
        {
            OnboardState.Starting => "启动中",
            OnboardState.Connecting => "连接中",
            OnboardState.WaitingArrival => "等待到站",
            OnboardState.ReadyToScan => "可扫码",
            OnboardState.Verifying => "核验中",
            OnboardState.Operating => "仓位操作中",
            OnboardState.Reporting => "结果上报中",
            OnboardState.Faulted => "设备异常",
            _ => state.ToString()
        };
    }

    private static void RunOnUiThread(Action action)
    {
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.BeginInvoke(action);
        }
    }
}
