using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private const int MaxLogEntries = 300;
    private readonly OnboardController _controller;
    private readonly IAppLogger _logger;
    private readonly WireToGateSessionClient _candidateSession;
    private readonly SqliteOnboardExecutionJournal _journal;
    private readonly CancellationTokenSource _candidateStopping = new();
    private readonly OperatorRecordFormatter _operatorRecordFormatter = new();
    private bool _candidateConnected;
    private bool _candidateReady;
    private long _candidateSessionGeneration;
    private string _candidateReason = "CANDIDATE_HANDSHAKE_REQUIRED";
    private bool _disposed;
    private string _scanText = string.Empty;
    private string _ruleConnectionText = "离线";
    private string _ioConnectionText = "离线";
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
    private string _recoverySlotName = "当前仓";
    private string? _lastLoggedErrorKey;

    public MainViewModel(
        OnboardController controller,
        IAppLogger logger,
        string agvId,
        WireToGateSessionClient candidateSession,
        SqliteOnboardExecutionJournal journal)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _candidateSession = candidateSession ?? throw new ArgumentNullException(nameof(candidateSession));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
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

    public IReadOnlyList<string> JourneySteps { get; } =
    [
        "连接与恢复", "前往机台", "机台扫码与装货", "等待发车 / 前往关卡", "关卡自动卸货", "8005 本地完成"
    ];

    public string CommunicationText => _candidateConnected ? "已连接" : "已断开";

    public string BusinessReadinessText =>
        _candidateConnected && _candidateReady ? "READY" : "RECOVERY_REQUIRED";

    public string MotionText => _controller.Current.State == OnboardState.Operating
        ? "停稳（站点作业）"
        : "运动状态 UNKNOWN";

    public string SafetyInterlockText => DepartureText;

    public string CurrentDemandText => _controller.Current.ActiveOperation?.TaskId ?? "等待 ControlServer 唯一 Demand";

    public string WorkTypeText => _controller.Current.ActiveOperation is null
        ? "WIRE_TO_GATE"
        : $"WIRE_TO_GATE / {_controller.Current.ActiveOperation.OperationType.ToString().ToUpperInvariant()}";

    public string CurrentStopText => VisitText;

    public string StationGuardText => _controller.Current.ActiveOperation is null ? "保持" : "有效（站点作业中）";

    public string CurrentSublotText => _controller.Current.ActiveOperation?.Sublot ?? "—";

    public string BasketAndSlotsText => _controller.Current.ActiveOperation is null
        ? "—"
        : $"服务端冻结 / [{_controller.Current.ActiveOperation.SlotIndex + 1}]";

    public string PrimaryActionText => _controller.Current.State switch
    {
        OnboardState.ReadyToScan => "提交 Sublot 最终核验",
        OnboardState.Operating => "等待目标仓位物理闭环",
        OnboardState.Reporting => "等待可靠结果确认",
        OnboardState.Faulted => "进入安全恢复",
        _ => "等待服务端下一步"
    };

    public bool HasBlockingNotice => HasError || HasWarning || BusinessReadinessText != "READY";

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

    public string RecoverySlotName
    {
        get => _recoverySlotName;
        private set => SetProperty(ref _recoverySlotName, value);
    }

    public async Task InitializeAsync()
    {
        await _journal.InitializeAsync(_candidateStopping.Token).ConfigureAwait(true);
        _controller.StateChanged += OnStateChanged;
        ApplySnapshot(_controller.Current);
        await _controller.StartAsync().ConfigureAwait(true);
        await EstablishCandidateSessionAsync().ConfigureAwait(true);
        _ = MaintainCandidateHeartbeatAsync(_candidateStopping.Token);
    }

    public void StopCandidateSession()
    {
        if (!_candidateStopping.IsCancellationRequested)
        {
            _candidateStopping.Cancel();
        }
        _controller.StateChanged -= OnStateChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        StopCandidateSession();
        _candidateStopping.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SubmitAsync(ScanInputMethod inputMethod)
    {
        string sublot = ScanText;
        ScanText = string.Empty;
        await _controller.SubmitScanAsync(sublot, inputMethod).ConfigureAwait(true);
    }

    public Task<bool> ConfirmSafeStartupStateAsync() => _controller.ConfirmSafeStartupStateAsync();

    public Task<bool> RequestReopenCurrentOperationAsync() =>
        Task.FromResult(_controller.RequestReopenCurrentOperation());

    public Task<bool> RequestCancelCurrentOperationAsync() =>
        Task.FromResult(_controller.RequestCancelCurrentOperation());

    public Task<bool> RetryPendingResultAsync() => _controller.RetryPendingResultAsync();

    private void OnStateChanged(object? sender, ValueChangedEventArgs<OnboardSnapshot> args)
    {
        RunOnUiThread(() => ApplySnapshot(args.Value));
    }

    private void ApplySnapshot(OnboardSnapshot snapshot)
    {
        RuleConnectionText = snapshot.RuleConnected ? "在线" : "离线";
        IoConnectionText = snapshot.IoConnected ? "在线" : "离线";
        VisitText = snapshot.Visit is null ? "未到站" : $"{snapshot.Visit.StationName} / {snapshot.Visit.VisitId}";
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
        bool candidateGateOpen = _candidateConnected && _candidateReady;
        Guidance = candidateGateOpen
            ? snapshot.Guidance
            : $"候选协议尚未 READY（{_candidateReason}）。禁止扫码、开仓和移动。";
        CanSubmit = snapshot.State == OnboardState.ReadyToScan && candidateGateOpen;
        HasWarning = hasWarning || !candidateGateOpen;
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

        OnPropertyChanged(nameof(CommunicationText));
        OnPropertyChanged(nameof(BusinessReadinessText));
        OnPropertyChanged(nameof(MotionText));
        OnPropertyChanged(nameof(SafetyInterlockText));
        OnPropertyChanged(nameof(CurrentDemandText));
        OnPropertyChanged(nameof(CurrentStopText));
        OnPropertyChanged(nameof(StationGuardText));
        OnPropertyChanged(nameof(CurrentSublotText));
        OnPropertyChanged(nameof(BasketAndSlotsText));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(HasBlockingNotice));

        foreach (LockerCardViewModel locker in Lockers)
        {
            LockerSnapshot state = snapshot.Io.GetLocker(locker.SlotIndex);
            locker.Update(state, snapshot.ActiveOperation);
        }

        AppendOperatorRecord(snapshot);
        LogOperatorVisibleError(snapshot);
    }

    private async Task EstablishCandidateSessionAsync()
    {
        try
        {
            WireToGateHandshakeResult result = await _candidateSession
                .ConnectAndRecoverAsync(_candidateStopping.Token)
                .ConfigureAwait(true);
            _candidateConnected = true;
            _candidateReady = result.Readiness == VehicleBusinessReadiness.Ready;
            _candidateSessionGeneration = result.SessionGeneration;
            _candidateReason = result.ReasonCode;
            ApplySnapshot(_controller.Current);
            _logger.Write(
                LogSeverity.Information,
                nameof(MainViewModel),
                $"候选协议五步恢复完成：generation={result.SessionGeneration}，readiness={result.Readiness}，reason={result.ReasonCode}。");
        }
        catch (Exception exception) when (exception is SocketException or IOException or InvalidDataException or
                                          InvalidOperationException or OperationCanceledException or HttpRequestException)
        {
            if (exception is OperationCanceledException && _candidateStopping.IsCancellationRequested)
            {
                return;
            }
            _candidateConnected = false;
            _candidateReady = false;
            _candidateReason = "CANDIDATE_HANDSHAKE_FAILED";
            ApplySnapshot(_controller.Current);
            _logger.Write(LogSeverity.Warning, nameof(MainViewModel), "候选协议恢复握手未完成，保持安全阻断。", exception);
        }
    }

    private async Task MaintainCandidateHeartbeatAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_candidateConnected)
                {
                    continue;
                }
                await _candidateSession.SendHeartbeatAsync(_candidateSessionGeneration, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Write(LogSeverity.Error, nameof(MainViewModel), "候选协议心跳丢失，已进入安全阻断。", exception);
            RunOnUiThread(() =>
            {
                _candidateConnected = false;
                _candidateReady = false;
                _candidateReason = "CONTROL_SERVER_HEARTBEAT_LOST";
                _controller.EnterFatalFault(
                    "CONTROL_SERVER_CONNECTION_LOST",
                    "ControlServer 连接已丢失，已禁止新操作。请确认仓门与开锁输出状态。");
                ApplySnapshot(_controller.Current);
            });
        }
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
        while (Logs.Count > MaxLogEntries)
        {
            Logs.RemoveAt(0);
        }
    }

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
        Dispatcher dispatcher = System.Windows.Application.Current.Dispatcher;
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
