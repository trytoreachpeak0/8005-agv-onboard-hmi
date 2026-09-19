using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private const int MaxLogEntries = 300;
    private readonly OnboardController _controller;
    private readonly IAppLogger _logger;
    private readonly OperatorRecordFormatter _operatorRecordFormatter = new();
    private readonly SlotGroupLayout _slotGroupLayout;
    private string _scanText = string.Empty;
    private string _ruleConnectionText = "离线";
    private string _ioConnectionText = "离线";
    private string _wireToGateText = "未启用";
    private string _visitText = "未到站";
    private string _stopDirectionText = string.Empty;
    private string _taskTypeText = string.Empty;
    private string _departureText = "禁止发车";
    private string _stateText = "启动中";
    private string _guidance = "系统正在启动…";
    private string _alarmText = string.Empty;
    private string _openingSideText = string.Empty;
    private bool _hasAlarms;
    private AlarmEntry? _expectedActionOverdue;
    private bool _hasExpectedActionOverdue;
    private string _expectedActionOverdueText = string.Empty;
    private string _recoveryReason = string.Empty;
    private bool _recoveryReasonAlreadyGiven;
    private Func<bool>? _wireToGateRecoveryReasonAlreadyGiven;
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
    private bool _canRequestForcedMechanicalRecovery;
    private bool _canConfirmForcedMechanicalRecovery;
    private bool _canSubmitHardwareRecoveryRecord;
    private bool _hasPhysicallyUnknownSlots;
    private string _physicallyUnknownSlotsText = string.Empty;
    private string _hardwareRecoveryObservations = string.Empty;
    private Func<bool>? _wireToGateCanConfirmForcedMechanicalRecovery;
    private Func<CancellationToken, Task<bool>>? _wireToGateForcedMechanicalRecoveryConfirmer;
    private Func<IReadOnlyList<int>>? _wireToGatePhysicallyUnknownSlots;
    private Func<bool>? _wireToGateCanSubmitHardwareRecoveryRecord;
    private Func<string, CancellationToken, Task<bool>>? _wireToGateHardwareRecoveryRecordSubmitter;
    private bool _canRequestManualChargingReturn;
    private bool _hasWireToGateJourney;
    private bool _wireToGateEnabled;
    private bool _hasStationDepartureCountdown;
    private DateTimeOffset? _stationDepartureDeadlineAt;
    private string _stationDepartureCountdownText = StationDepartureCountdownFormatter.AbsentText;
    private StationDepartureCountdownTier _stationDepartureCountdownTier = StationDepartureCountdownTier.Absent;
    private bool _stationDepartureCountdownDimmed;
    private Func<StationDepartureCountdownContext, string?>? _stationDepartureCountdownTextOverride;
    private DispatcherTimer? _stationDepartureCountdownTimer;
    private bool _stationDepartureCountdownStopped;
    private WireToGateSessionSnapshot? _wireToGateSession;
    private WireToGateHmiOperationSnapshot? _wireToGateOperation;
    private OnboardSnapshot? _lastControllerSnapshot;
    private string _recoverySlotName = "当前仓";
    private string? _lastLoggedErrorKey;
    private Func<string, ScanInputMethod, CancellationToken, Task>? _wireToGateSubmitter;
    private Func<bool>? _wireToGateCanSubmit;
    private Func<bool>? _wireToGateCanRequestRecovery;
    private Func<string?, CancellationToken, Task<bool>>? _wireToGateRecoveryRequester;
    private Func<bool>? _wireToGateCanRequestLoadCancellation;
    private Func<bool>? _wireToGateCanRequestLoadCompensation;
    private Func<bool>? _wireToGateCanRequestLoadCorrection;
    private Func<bool>? _wireToGateCanRequestFaultCargoHandoff;
    private Func<CancellationToken, Task<bool>>? _wireToGateLoadCancellationRequester;
    private Func<string?, CancellationToken, Task<bool>>? _wireToGateLoadCompensationRequester;
    private Func<CancellationToken, Task<bool>>? _wireToGateLoadCorrectionRequester;
    private Func<string?, CancellationToken, Task<bool>>? _wireToGateFaultCargoHandoffRequester;
    private Func<bool>? _wireToGateCanRequestForcedMechanicalRecovery;
    private Func<string?, CancellationToken, Task<bool>>? _wireToGateForcedMechanicalRecoveryRequester;
    private Func<bool>? _wireToGateCanRequestManualChargingReturn;
    private Func<bool>? _wireToGateLoadCancellationPending;
    private Func<WireToGateSublotRejection?>? _wireToGateSublotRejection;
    private bool _hasSublotRejection;
    private string _sublotRejectionText = string.Empty;
    private string _sublotRejectionReasonCode = string.Empty;
    private Func<CancellationToken, Task<bool>>? _wireToGateManualChargingReturnRequester;

    /// <param name="slotConfiguration">
    /// 本机生效仓位配置，仓位区按它的 <c>SlotPosition</c> 分前后两组。启动时读一次就够：激活只改版本名，
    /// 位置名不在指纹里、激活也不动它。
    /// </param>
    public MainViewModel(
        OnboardController controller,
        IAppLogger logger,
        string agvId,
        ActiveSlotConfiguration slotConfiguration)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        AgvId = agvId;
        Lockers = new ObservableCollection<LockerCardViewModel>(
            Enumerable.Range(0, 8).Select(index => new LockerCardViewModel(index)));
        _slotGroupLayout = SlotGroupPresentation.Create(slotConfiguration, _logger);
        SlotGroups = new ObservableCollection<SlotGroupViewModel>(
            _slotGroupLayout.Groups.Select(group => new SlotGroupViewModel(
                group,
                [.. group.PhysicalSlotNumbers.Select(number => Lockers[number - 1])])));
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

    /// <summary>仓位区的前后两侧分组；卡片与 <see cref="Lockers"/> 是同一批实例。</summary>
    public ObservableCollection<SlotGroupViewModel> SlotGroups { get; }

    /// <summary>仓位区顶部的「本次开门：前侧／后侧／前后两侧」。没有目标仓时为空。</summary>
    public string OpeningSideText
    {
        get => _openingSideText;
        private set
        {
            if (SetProperty(ref _openingSideText, value))
            {
                OnPropertyChanged(nameof(HasOpeningSide));
            }
        }
    }

    public bool HasOpeningSide => OpeningSideText.Length > 0;

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

    public string WireToGateText
    {
        get => _wireToGateText;
        private set => SetProperty(ref _wireToGateText, value);
    }

    public string AlarmText
    {
        get => _alarmText;
        private set => SetProperty(ref _alarmText, value);
    }

    public bool HasAlarms
    {
        get => _hasAlarms;
        private set => SetProperty(ref _hasAlarms, value);
    }

    /// <summary>
    /// 本机界面上的告警。只显示与这台车、当前停靠、当前操作直接相关的那些（REQ-0270），由调用方收敛好传进来。
    /// </summary>
    internal void UpdateOnboardAlarms(IReadOnlyList<AlarmEntry> alarms) => RunOnUiThread(() =>
    {
        ArgumentNullException.ThrowIfNull(alarms);
        // 期待动作超时单独一行（REQ-0358）：它的消息只是期待的动作，要套进规定的整句、并随会话在线与否换说法。
        _expectedActionOverdue = alarms.FirstOrDefault(
            alarm => alarm.AlarmCode == OnboardAlarmCodes.SlotExpectedActionOverdue);
        AlarmEntry[] others = [.. alarms.Where(alarm => alarm.AlarmCode != OnboardAlarmCodes.SlotExpectedActionOverdue)];
        HasAlarms = others.Length > 0;
        AlarmText = string.Join("；", others.Select(alarm => alarm.Message));
        RefreshExpectedActionOverdueCore();
    });

    /// <summary>提示区是否显示期待动作超时那一行（REQ-0358）。</summary>
    public bool HasExpectedActionOverdue
    {
        get => _hasExpectedActionOverdue;
        private set => SetProperty(ref _hasExpectedActionOverdue, value);
    }

    /// <summary>那一行字，文案集中在 <c>WireToGateExpectedActionOverdueText</c>。只是告知，没有任何按钮跟着它。</summary>
    public string ExpectedActionOverdueText
    {
        get => _expectedActionOverdueText;
        private set => SetProperty(ref _expectedActionOverdueText, value);
    }

    private void RefreshExpectedActionOverdueCore()
    {
        HasExpectedActionOverdue = _expectedActionOverdue is not null;
        ExpectedActionOverdueText = _expectedActionOverdue is null
            ? string.Empty
            : WireToGateExpectedActionOverdueText.Describe(
                _expectedActionOverdue.Message,
                _wireToGateSession?.Connected == true);
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
        RefreshExpectedActionOverdueCore();
    });

    internal void UpdateWireToGateJourney(WireToGateJourneySnapshot snapshot) => RunOnUiThread(() =>
    {
        _hasWireToGateJourney = true;
        // 期限以最新快照为准，整值替换：重新计满、恢复后的新期限、变为空，都照收，车载端不自己推算。
        _stationDepartureDeadlineAt = snapshot.CurrentStopWorklist?.StationDepartureDeadlineAt;
        HasStationDepartureCountdown = snapshot.CurrentStopWorklist is not null;
        RefreshStationDepartureCountdownCore();
        SyncStationDepartureCountdownTimerCore();
        if (snapshot.CurrentStopWorklist is { } worklist)
        {
            WireToGateWorklistItem? item = worklist.Items.SingleOrDefault();
            VisitText = item is null
                ? $"{worklist.StationId} / 无待处理任务"
                : $"{worklist.StationId} / {item.Sublot}";
        }
        else
        {
            VisitText = "旅程未同步";
        }
        // 方向只随服务端的 stopRole／legType，任务类型只随清单项的 workType；都不推断（批次6-03）。
        StopDirectionText = WireToGateStopFacts.DirectionText(snapshot);
        TaskTypeText = WireToGateStopFacts.TaskTypeText(snapshot);
        RefreshWireToGateInputStateCore();
        ApplyWireToGatePresentationCore();
    });

    internal void ConfigureWireToGate(
        Func<string, ScanInputMethod, CancellationToken, Task> submitter,
        Func<bool> canSubmit,
        Func<bool>? canRequestRecovery = null,
        Func<string?, CancellationToken, Task<bool>>? recoveryRequester = null,
        Func<bool>? canRequestLoadCancellation = null,
        Func<CancellationToken, Task<bool>>? loadCancellationRequester = null,
        Func<bool>? canRequestLoadCompensation = null,
        Func<string?, CancellationToken, Task<bool>>? loadCompensationRequester = null,
        Func<bool>? canRequestLoadCorrection = null,
        Func<CancellationToken, Task<bool>>? loadCorrectionRequester = null,
        Func<bool>? canRequestFaultCargoHandoff = null,
        Func<string?, CancellationToken, Task<bool>>? faultCargoHandoffRequester = null,
        Func<bool>? canRequestForcedMechanicalRecovery = null,
        Func<string?, CancellationToken, Task<bool>>? forcedMechanicalRecoveryRequester = null,
        Func<bool>? canRequestManualChargingReturn = null,
        Func<CancellationToken, Task<bool>>? manualChargingReturnRequester = null,
        Func<bool>? loadCancellationPending = null,
        Func<WireToGateSublotRejection?>? sublotRejection = null,
        Func<bool>? recoveryReasonAlreadyGiven = null)
    {
        _wireToGateRecoveryReasonAlreadyGiven = recoveryReasonAlreadyGiven;
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
        _wireToGateCanRequestForcedMechanicalRecovery = canRequestForcedMechanicalRecovery;
        _wireToGateForcedMechanicalRecoveryRequester = forcedMechanicalRecoveryRequester;
        _wireToGateCanRequestManualChargingReturn = canRequestManualChargingReturn;
        _wireToGateManualChargingReturnRequester = manualChargingReturnRequester;
        _wireToGateLoadCancellationPending = loadCancellationPending;
        _wireToGateSublotRejection = sublotRejection;
        _wireToGateEnabled = true;
        RefreshWireToGateInputStateCore();
        ApplyWireToGatePresentationCore();
    }

    /// <summary>
    /// The two steps of a forced mechanical recovery that follow its authorization (onboard-hmi#107):
    /// the operator's confirmation of the isolation and the manual extraction, and the hardware
    /// recovery record that clears the slots it left physically unknown.
    /// </summary>
    internal void ConfigureForcedIsolation(
        Func<bool> canConfirm,
        Func<CancellationToken, Task<bool>> confirmer,
        Func<IReadOnlyList<int>> physicallyUnknownSlots,
        Func<bool> canSubmitRecord,
        Func<string, CancellationToken, Task<bool>> recordSubmitter)
    {
        _wireToGateCanConfirmForcedMechanicalRecovery = canConfirm;
        _wireToGateForcedMechanicalRecoveryConfirmer = confirmer;
        _wireToGatePhysicallyUnknownSlots = physicallyUnknownSlots;
        _wireToGateCanSubmitHardwareRecoveryRecord = canSubmitRecord;
        _wireToGateHardwareRecoveryRecordSubmitter = recordSubmitter;
        RefreshWireToGateInputStateCore();
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
            // An event can open or close sublot entry without any snapshot arriving -- a cancellation
            // before any sublot being sent, refused or settled -- so the input gates are read again.
            RefreshWireToGateInputStateCore();
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
        CanRequestForcedMechanicalRecovery = _wireToGateCanRequestForcedMechanicalRecovery?.Invoke() == true;
        CanRequestManualChargingReturn = _wireToGateCanRequestManualChargingReturn?.Invoke() == true;
        RefreshForcedIsolationCore();
        RefreshRecoveryReasonLockCore();
    }

    public string VisitText
    {
        get => _visitText;
        private set => SetProperty(ref _visitText, value);
    }

    public string StopDirectionText
    {
        get => _stopDirectionText;
        private set => SetProperty(ref _stopDirectionText, value);
    }

    public string TaskTypeText
    {
        get => _taskTypeText;
        private set => SetProperty(ref _taskTypeText, value);
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
        private set => SetRecoveryEntry(ref _canRequestWireToGateRecovery, value);
    }

    public bool CanRequestLoadCancellation
    {
        get => _canRequestLoadCancellation;
        private set => SetProperty(ref _canRequestLoadCancellation, value);
    }

    public bool CanRequestLoadCompensation
    {
        get => _canRequestLoadCompensation;
        private set => SetRecoveryEntry(ref _canRequestLoadCompensation, value);
    }

    public bool CanRequestLoadCorrection
    {
        get => _canRequestLoadCorrection;
        private set => SetProperty(ref _canRequestLoadCorrection, value);
    }

    public bool CanRequestFaultCargoHandoff
    {
        get => _canRequestFaultCargoHandoff;
        private set => SetRecoveryEntry(ref _canRequestFaultCargoHandoff, value);
    }

    public bool CanRequestForcedMechanicalRecovery
    {
        get => _canRequestForcedMechanicalRecovery;
        private set => SetRecoveryEntry(ref _canRequestForcedMechanicalRecovery, value);
    }

    /// <summary>
    /// 异常处置会话的原因（CP-0005 第五节，onboard-hmi#109）：判定人、故障类别、现场说明，写进
    /// <c>ExceptionRecoverySessionRequested.reason</c>。留空时业务服务用按恢复动作写死的缺省文字。
    /// </summary>
    public string RecoveryReason
    {
        get => _recoveryReason;
        set => SetProperty(ref _recoveryReason, value ?? string.Empty);
    }

    /// <summary>
    /// 原因输入框只跟着会开异常处置会话的四个入口出现。它们都要求管理员工号与恢复凭据，操作工的界面上没有；
    /// 取消装货与修正装货不开会话，不算。
    /// </summary>
    public bool HasRecoveryReasonInput =>
        CanRequestWireToGateRecovery
        || CanRequestLoadCompensation
        || CanRequestFaultCargoHandoff
        || CanRequestForcedMechanicalRecovery;

    /// <summary>
    /// 原因框能不能填。会话已开时发出去的是开会话时那句原因，这次填的会被丢掉，所以锁住，不让人白填。
    /// </summary>
    public bool IsRecoveryReasonEditable => !_recoveryReasonAlreadyGiven;

    /// <summary>原因框被锁住时，旁边那句说明（「会话已开，原因沿用开会话时填写的」，写在 XAML 里）是否显示。</summary>
    public bool HasRecoveryReasonCarriedOver => HasRecoveryReasonInput && _recoveryReasonAlreadyGiven;

    // The entry's own name has to be passed on: SetProperty's [CallerMemberName] would otherwise name this
    // helper, and the window's IsEnabled/Visibility bindings would never hear of the entry (onboard-hmi#112).
    private void SetRecoveryEntry(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            OnPropertyChanged(nameof(HasRecoveryReasonInput));
            OnPropertyChanged(nameof(HasRecoveryReasonCarriedOver));
        }
    }

    private void RefreshRecoveryReasonLockCore()
    {
        bool alreadyGiven = _wireToGateRecoveryReasonAlreadyGiven?.Invoke() == true;
        if (alreadyGiven == _recoveryReasonAlreadyGiven)
        {
            return;
        }

        _recoveryReasonAlreadyGiven = alreadyGiven;
        OnPropertyChanged(nameof(IsRecoveryReasonEditable));
        OnPropertyChanged(nameof(HasRecoveryReasonCarriedOver));
    }

    public bool CanConfirmForcedMechanicalRecovery
    {
        get => _canConfirmForcedMechanicalRecovery;
        private set => SetProperty(ref _canConfirmForcedMechanicalRecovery, value);
    }

    public bool CanSubmitHardwareRecoveryRecord
    {
        get => _canSubmitHardwareRecoveryRecord;
        private set => SetProperty(ref _canSubmitHardwareRecoveryRecord, value);
    }

    public bool HasPhysicallyUnknownSlots
    {
        get => _hasPhysicallyUnknownSlots;
        private set => SetProperty(ref _hasPhysicallyUnknownSlots, value);
    }

    public string PhysicallyUnknownSlotsText
    {
        get => _physicallyUnknownSlotsText;
        private set => SetProperty(ref _physicallyUnknownSlotsText, value);
    }

    /// <summary>The administrator's account of the repair, sent as the record's observations.</summary>
    public string HardwareRecoveryObservations
    {
        get => _hardwareRecoveryObservations;
        set => SetProperty(ref _hardwareRecoveryObservations, value ?? string.Empty);
    }

    public bool CanRequestManualChargingReturn
    {
        get => _canRequestManualChargingReturn;
        private set => SetProperty(ref _canRequestManualChargingReturn, value);
    }

    public string RecoverySlotName
    {
        get => _recoverySlotName;
        private set => SetProperty(ref _recoverySlotName, value);
    }

    /// <summary>
    /// 离站期限倒计时用的车载端时钟，默认系统时钟。测试靠它控制「现在」。
    /// </summary>
    internal IClock Clock { get; init; } = new SystemClock();

    /// <summary>
    /// 提示区是否显示子批拒收原因（onboard-hmi#77）。操作员再录入、停靠换了会话时撤下。
    /// </summary>
    public bool HasSublotRejection
    {
        get => _hasSublotRejection;
        private set => SetProperty(ref _hasSublotRejection, value);
    }

    /// <summary>拒收原因那一行字：被拒的子批与中文原因，文案集中在 <c>WireToGateSublotRejectionText</c>。</summary>
    public string SublotRejectionText
    {
        get => _sublotRejectionText;
        private set => SetProperty(ref _sublotRejectionText, value);
    }

    /// <summary>服务端给的原始原因码。UIA 的 ItemStatus 读它，服务端 G3 场景据此判「显示了原因」，不比文案全文。</summary>
    public string SublotRejectionReasonCode
    {
        get => _sublotRejectionReasonCode;
        private set => SetProperty(ref _sublotRejectionReasonCode, value);
    }

    /// <summary>
    /// 倒计时定时器挂在哪个 Dispatcher 上，默认是 WPF 应用的 UI 线程；没有 WPF 应用（单元测试）时为空，不起定时器。
    /// 测试给它一个自己开的 Dispatcher 线程，才能看到定时器的启停与真实刷新。
    /// </summary>
    internal Dispatcher? StationDepartureCountdownDispatcher { get; init; } = System.Windows.Application.Current?.Dispatcher;

    /// <summary>倒计时定时器此刻是否在跑。只在有期限时跑，期限变为空或窗口关闭后停。</summary>
    internal bool IsStationDepartureCountdownTicking => _stationDepartureCountdownTimer?.IsEnabled == true;

    /// <summary>
    /// 提示区是否显示离站期限倒计时。本站作业清单同步了就显示——没有截止时间也显示，写「无倒计时」；
    /// 整块藏起来会让「服务端没给期限」和「清单还没到」看起来一样。
    /// </summary>
    public bool HasStationDepartureCountdown
    {
        get => _hasStationDepartureCountdown;
        private set => SetProperty(ref _hasStationDepartureCountdown, value);
    }

    /// <summary>倒计时那一行字：覆盖文案有值时用它，否则是格式化类的通用文案。</summary>
    public string StationDepartureCountdownText
    {
        get => _stationDepartureCountdownText;
        private set => SetProperty(ref _stationDepartureCountdownText, value);
    }

    /// <summary>配色档位，XAML 的 DataTrigger 按它选颜色，UIA 的 ItemStatus 也读它。</summary>
    public StationDepartureCountdownTier StationDepartureCountdownTier
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

    /// <summary>
    /// 倒计时文案的覆盖入口：<c>App</c> 把它接到 <c>WireToGateBusinessService.DescribeExpiredStationDeadline</c>
    /// （<c>8005-agv-onboard-hmi#78</c>，期限过后在途装货与装货取消的文案）。
    /// </summary>
    /// <remarks>
    /// 每次重算都调用；返回 <c>null</c> 或不设置时显示通用文案。只换文字，档位与闪烁仍按服务端期限算——
    /// 覆盖方不能借它延长或作废期限。
    /// </remarks>
    internal Func<StationDepartureCountdownContext, string?>? StationDepartureCountdownTextOverride
    {
        get => _stationDepartureCountdownTextOverride;
        set => RunOnUiThread(() =>
        {
            _stationDepartureCountdownTextOverride = value;
            RefreshStationDepartureCountdownCore();
        });
    }

    public async Task InitializeAsync()
    {
        _controller.StateChanged += OnStateChanged;
        ApplySnapshot(_controller.Current);
        await _controller.StartAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 按当前时钟重算倒计时。定时器调的就是它；截止时刻是绝对值，这里不保留任何递减状态。
    /// </summary>
    internal void RefreshStationDepartureCountdown() => RunOnUiThread(RefreshStationDepartureCountdownCore);

    /// <summary>
    /// 窗口关闭时停掉倒计时定时器，之后的快照也不再启动它。
    /// </summary>
    internal void StopStationDepartureCountdown() => RunOnUiThread(() =>
    {
        _stationDepartureCountdownStopped = true;
        _stationDepartureCountdownTimer?.Stop();
    });

    /// <summary>
    /// 有期限才需要按时钟重算：期限为空时显示「无倒计时」，不随时间变化，定时器停掉。到期之后仍然有期限，
    /// 定时器继续跑——那一档的文字本身不变，但 #78 的覆盖文案要显示已过期多久。
    /// </summary>
    private void SyncStationDepartureCountdownTimerCore()
    {
        if (_stationDepartureDeadlineAt is null || _stationDepartureCountdownStopped)
        {
            _stationDepartureCountdownTimer?.Stop();
            return;
        }

        if (_stationDepartureCountdownTimer is null)
        {
            if (StationDepartureCountdownDispatcher is not { } dispatcher)
            {
                return;
            }

            // 250 ms 而不是 1 s：最后 10 秒要逐秒闪烁，相位取自绝对秒数。节拍等于秒长时它与秒边界的相对位置会漂移，
            // 跨边界那一下会连着两次落在同一相位上，看着像卡住。
            _stationDepartureCountdownTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _stationDepartureCountdownTimer.Tick += (_, _) => RefreshStationDepartureCountdownCore();
        }

        if (!_stationDepartureCountdownTimer.IsEnabled)
        {
            _stationDepartureCountdownTimer.Start();
        }
    }

    private void RefreshStationDepartureCountdownCore()
    {
        DateTimeOffset now = Clock.Now;
        StationDepartureCountdownView view = StationDepartureCountdownFormatter.Format(_stationDepartureDeadlineAt, now);
        string? overrideText = _stationDepartureCountdownTextOverride?.Invoke(
            new StationDepartureCountdownContext(_stationDepartureDeadlineAt, now, view));
        StationDepartureCountdownText = overrideText ?? view.Text;
        StationDepartureCountdownTier = view.Tier;
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
        RequestWithReasonAsync(_wireToGateRecoveryRequester, cancellationToken);

    public Task<bool> RequestLoadCancellationAsync(CancellationToken cancellationToken = default) =>
        _wireToGateLoadCancellationRequester is null
            ? Task.FromResult(false)
            : _wireToGateLoadCancellationRequester(cancellationToken);

    public Task<bool> RequestLoadCompensationAsync(CancellationToken cancellationToken = default) =>
        RequestWithReasonAsync(_wireToGateLoadCompensationRequester, cancellationToken);

    public Task<bool> RequestLoadCorrectionAsync(CancellationToken cancellationToken = default) =>
        _wireToGateLoadCorrectionRequester is null
            ? Task.FromResult(false)
            : _wireToGateLoadCorrectionRequester(cancellationToken);

    public Task<bool> RequestFaultCargoHandoffAsync(CancellationToken cancellationToken = default) =>
        RequestWithReasonAsync(_wireToGateFaultCargoHandoffRequester, cancellationToken);

    public Task<bool> RequestForcedMechanicalRecoveryAsync(CancellationToken cancellationToken = default) =>
        RequestWithReasonAsync(_wireToGateForcedMechanicalRecoveryRequester, cancellationToken);

    /// <summary>
    /// Sends the administrator's entered reason, trimmed, or <c>null</c> when nothing was entered, and clears
    /// the box once the request was taken so the next session does not go out with this one's reason.
    /// </summary>
    private async Task<bool> RequestWithReasonAsync(
        Func<string?, CancellationToken, Task<bool>>? requester,
        CancellationToken cancellationToken)
    {
        if (requester is null)
        {
            return false;
        }

        // A session already open keeps the reason it was opened with: nothing entered now is sent, and the
        // box keeps its text rather than looking as if it had been.
        RefreshRecoveryReasonLockCore();
        bool locked = _recoveryReasonAlreadyGiven;
        string? reason = locked || string.IsNullOrWhiteSpace(RecoveryReason) ? null : RecoveryReason.Trim();
        bool accepted = await requester(reason, cancellationToken).ConfigureAwait(true);
        if (accepted && !locked)
        {
            RecoveryReason = string.Empty;
        }

        return accepted;
    }

    public Task<bool> ConfirmForcedMechanicalRecoveryAsync(CancellationToken cancellationToken = default) =>
        _wireToGateForcedMechanicalRecoveryConfirmer is null
            ? Task.FromResult(false)
            : _wireToGateForcedMechanicalRecoveryConfirmer(cancellationToken);

    public Task<bool> SubmitHardwareRecoveryRecordAsync(CancellationToken cancellationToken = default) =>
        _wireToGateHardwareRecoveryRecordSubmitter is null
            ? Task.FromResult(false)
            : _wireToGateHardwareRecoveryRecordSubmitter(HardwareRecoveryObservations, cancellationToken);

    public Task<bool> RequestManualChargingReturnAsync(CancellationToken cancellationToken = default) =>
        _wireToGateManualChargingReturnRequester is null
            ? Task.FromResult(false)
            : _wireToGateManualChargingReturnRequester(cancellationToken);

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
        RefreshSlotGroupsCore();

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
            CanRequestForcedMechanicalRecovery = false;
            CanRequestManualChargingReturn = false;
            CanConfirmForcedMechanicalRecovery = false;
            CanSubmitHardwareRecoveryRecord = false;
            return;
        }

        WireToGateSublotRejection? sublotRejection = _wireToGateSublotRejection?.Invoke();
        HasSublotRejection = sublotRejection is not null;
        SublotRejectionText = sublotRejection is null
            ? string.Empty
            : WireToGateSublotRejectionText.Describe(sublotRejection);
        SublotRejectionReasonCode = sublotRejection?.ReasonCode ?? string.Empty;
        WireToGateHmiBanner banner = WireToGateHmiPresentation.Create(
            _wireToGateSession,
            _wireToGateOperation,
            _wireToGateCanSubmit?.Invoke() == true,
            _wireToGateLoadCancellationPending?.Invoke() == true,
            sublotRejection is not null);
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
        CanRequestForcedMechanicalRecovery = _wireToGateCanRequestForcedMechanicalRecovery?.Invoke() == true;
        CanRequestManualChargingReturn = _wireToGateCanRequestManualChargingReturn?.Invoke() == true;
        RefreshForcedIsolationCore();
        RefreshRecoveryReasonLockCore();
    }

    private void RefreshForcedIsolationCore()
    {
        CanConfirmForcedMechanicalRecovery = _wireToGateCanConfirmForcedMechanicalRecovery?.Invoke() == true;
        CanSubmitHardwareRecoveryRecord = _wireToGateCanSubmitHardwareRecoveryRecord?.Invoke() == true;
        IReadOnlyList<int> unknown = _wireToGatePhysicallyUnknownSlots?.Invoke() ?? [];
        HasPhysicallyUnknownSlots = unknown.Count > 0;
        PhysicallyUnknownSlotsText = unknown.Count > 0
            ? $"{string.Join("、", unknown)} 号仓经强制机械取出，物理状态未知，禁止操作；修复后提交硬件恢复记录。"
            : string.Empty;
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
        RefreshSlotGroupsCore();
    }

    private void RefreshSlotGroupsCore()
    {
        int[] targetSlots = [.. Lockers.Where(locker => locker.IsTarget).Select(locker => locker.PhysicalNumber)];
        foreach (SlotGroupViewModel group in SlotGroups)
        {
            group.IsTarget = group.Group.ContainsAnyOf(targetSlots);
        }
        OpeningSideText = SlotGroupPresentation.OpeningSideText(_slotGroupLayout, targetSlots);
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
        "OPERATION_COMPLETED" or "MANUAL_CHARGING_RETURN_ACCEPTED" => OperatorRecordKind.Success,
        "OPERATION_RECOVERY_REQUIRED" or "RECOVERY_BLOCKED" => OperatorRecordKind.Error,
        "RESULT_ACK_PENDING" or "RECOVERY_AUTHORIZED" or "SUBLOT_REJECTED" => OperatorRecordKind.Warning,
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
