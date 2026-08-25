using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private const int MaxLogEntries = 300;
    private readonly OnboardController _controller;
    private readonly IAppLogger _logger;
    private readonly OperatorRecordFormatter _operatorRecordFormatter = new();
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
        string agvId)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        _controller.StateChanged += OnStateChanged;
        ApplySnapshot(_controller.Current);
        await _controller.StartAsync().ConfigureAwait(true);
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
        Guidance = snapshot.Guidance;
        CanSubmit = snapshot.State == OnboardState.ReadyToScan;
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
            locker.Update(state, snapshot.ActiveOperation);
        }

        AppendOperatorRecord(snapshot);
        LogOperatorVisibleError(snapshot);
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
