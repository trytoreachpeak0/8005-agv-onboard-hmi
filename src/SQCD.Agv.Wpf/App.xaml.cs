using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using SQCD.Agv.Application;
using SQCD.Agv.AutomationHost;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

public partial class App : System.Windows.Application, IDisposable
{
    private FileAppLogger? _logger;
    private ModbusTcpIoModuleClient? _ioModule;
    private IRuleGateway? _ruleGateway;
    private OnboardController? _controller;
    private WireToGateSessionService? _wireToGate;
    private WireToGateBusinessService? _wireToGateBusiness;
    private OnboardAutomationHttpServer? _automationServer;
    private ControlServerVehicleSafetySignalProvider? _vehicleSafetySignalProvider;
    private OnboardAlarmMonitor? _alarmMonitor;
    private bool _disposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            OnboardSettings settings = OnboardSettings.Load(settingsPath);
            _vehicleSafetySignalProvider = new ControlServerVehicleSafetySignalProvider(
                settings.VehicleSafety,
                startPolling: settings.WireToGate.Enabled);
            ControlServerVehicleSafetySignalProvider vehicleSafetySignalProvider = _vehicleSafetySignalProvider;
            Func<bool> vehicleStoppedProvider = () =>
                vehicleSafetySignalProvider.Read().IsStoppedAndFresh(
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromMilliseconds(settings.VehicleSafety.MaximumEvidenceAgeMs),
                    TimeSpan.FromMilliseconds(settings.VehicleSafety.ClockSkewToleranceMs));
            _logger = new FileAppLogger(settings.Logging);
            _ioModule = new ModbusTcpIoModuleClient(settings.IoModule, _logger);
            _ruleGateway = settings.WireToGate.Enabled
                ? new DisabledRuleGateway()
                : new TcpJsonRuleGateway(
                    settings.RuleGateway,
                    settings.AgvId,
                    settings.OnboardInstanceId,
                    _logger);
            OnboardWorkflowOptions workflowOptions = new(
                TimeSpan.FromMilliseconds(settings.Workflow.UnlockFeedbackTimeoutMs),
                TimeSpan.FromMilliseconds(settings.Workflow.UnlockOutputResetTimeoutMs),
                TimeSpan.FromMilliseconds(settings.Workflow.OperationTimeoutMs),
                TimeSpan.FromMilliseconds(settings.Workflow.FeedbackStableMs),
                TimeSpan.FromMilliseconds(settings.Workflow.IoSnapshotMaxAgeMs),
                settings.Workflow.MaxSublotLength,
                settings.Workflow.MaxReopenAttempts);
            _controller = new OnboardController(
                _ioModule,
                _ruleGateway,
                _logger,
                new SystemClock(),
                workflowOptions,
                () => !settings.WireToGate.Enabled
                    || (_wireToGate?.Current.Readiness == WireToGateSessionReadiness.Ready
                        && vehicleStoppedProvider()),
                settings.WireToGate.Enabled
                    ? () =>
                    {
                        WireToGateJourneySnapshot? journey = _wireToGate?.CurrentJourney;
                        return journey is not null
                            && journey.CanAcceptSublotAt(
                                DateTimeOffset.Now,
                                TimeSpan.FromMilliseconds(settings.WireToGate.JourneySnapshotMaxAgeMs))
                            ? journey
                            : null;
                    }
            : null);
            if (_ruleGateway is TcpJsonRuleGateway legacyRuleGateway)
            {
                legacyRuleGateway.HeartbeatStatusProvider = () => new RuleHeartbeatStatus(
                    _ioModule.IsConnected,
                    _controller.Current.ActiveOperation?.OperationId,
                    _controller.Current.DeparturePermitted);
            }

            string version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown";
            _logger.Write(
                LogSeverity.Information,
                nameof(App),
                $"车载端启动：agvId={settings.AgvId}，environment={settings.Environment}，version={version}。");
            // 生效配置与激活结果一起落在同一份原子文档里（#27）：分两次写会留下一个窗口——配置已切、结果没记下，
            // 重连补报时车会以为自己没激活过，于是再激活一次。初始那一份是本机自述的配置，由设置渲染出来；文档里
            // 已有内容时以文档为准。仓位区的前后分组读的也是它，所以放在界面之前建。
            ActiveSlotConfiguration localSlotConfiguration =
                OnboardActiveSlotConfigurationFactory.Create(settings.WireToGate, settings.IoModule);
            DocumentActiveSlotConfigurationStore? slotConfigurationStore = settings.WireToGate.Enabled
                ? new DocumentActiveSlotConfigurationStore(
                    new AtomicJsonFile(settings.WireToGate.ActiveSlotConfigurationPath),
                    localSlotConfiguration)
                : null;
            MainViewModel viewModel = new(
                _controller,
                _logger,
                settings.AgvId,
                slotConfigurationStore?.Current ?? localSlotConfiguration);
            MainWindow window = new() { DataContext = viewModel };
            MainWindow = window;
            // 告警板两种模式下都有：旧模式没有会话可以报，本机界面照样要显示。
            OnboardAlarmBoard alarmBoard = new(settings.AgvId, TimeProvider.System);
            if (settings.WireToGate.Enabled)
            {
                SqliteWireToGateJournal journal = new(settings.WireToGate.JournalPath);
                _wireToGate = new WireToGateSessionService(
                    settings.WireToGate.CreateSessionOptions(settings.AgvId),
                    _ioModule,
                    journal,
                    _logger,
                    new SystemClock(),
                    vehicleSafetySignalProvider,
                    // 告警板的生产者是下面的 OnboardAlarmMonitor。握手报的是那一刻告警板上的全量，空的也报：
                    // 一份空快照说的是「这台车此刻没有告警」，与「这台车从没报过」在看板上是两种显示。
                    alarmBoard,
                    new SlotConfigurationActivationCoordinator(slotConfigurationStore!, TimeProvider.System),
                    TimeSpan.FromMilliseconds(settings.Workflow.IoSnapshotMaxAgeMs),
                    TimeSpan.FromMilliseconds(settings.VehicleSafety.MaximumEvidenceAgeMs),
                    TimeSpan.FromMilliseconds(settings.VehicleSafety.ClockSkewToleranceMs),
                    TimeSpan.FromMilliseconds(settings.WireToGate.SessionHeartbeatIntervalMs));
                _wireToGate.StateChanged += (_, args) =>
                {
                    viewModel.UpdateWireToGateStatus(args.Value);
                    _controller.RefreshExternalSafetyState();
                };
                // 清单项的侧与修正对象（批次7-13，onboard-hmi#134）：本次运行收到的仓位命令，加上日志里现成的操作
                // 上下文（只读）。日志在每份行程快照（含重启后从日志恢复的那几份）与每个操作员事件之后重读一次。
                JournaledOperationsFeed journaledOperations = new(
                    journal,
                    viewModel.UpdateJournaledOperations,
                    _logger);
                _wireToGate.ServerCommandReceived += (_, args) =>
                {
                    if (args.Value is WireToGateSlotOperationCommand command)
                    {
                        viewModel.RecordSlotOperationCommand(command);
                    }
                };
                _wireToGate.JourneyChanged += (_, args) =>
                {
                    viewModel.UpdateWireToGateJourney(args.Value);
                    _controller.RefreshExternalSafetyState();
                    _ = journaledOperations.RefreshAsync();
                };
                _wireToGateBusiness = new WireToGateBusinessService(
                    _wireToGate,
                    _ioModule,
                    _logger,
                    new SystemClock(),
                    vehicleStoppedProvider,
                    new WireToGateSlotOperationExecutorOptions(
                        TimeSpan.FromMilliseconds(settings.Workflow.UnlockFeedbackTimeoutMs),
                        TimeSpan.FromMilliseconds(settings.Workflow.UnlockOutputResetTimeoutMs),
                        TimeSpan.FromMilliseconds(settings.Workflow.OperationTimeoutMs),
                        TimeSpan.FromMilliseconds(settings.Workflow.FeedbackStableMs),
                        TimeSpan.FromMilliseconds(settings.Workflow.IoSnapshotMaxAgeMs)),
                    settings.WireToGate.OperatorIdEnvironmentVariable,
                    vehicleSafetySignalProvider,
                    TimeSpan.FromMilliseconds(settings.VehicleSafety.MaximumEvidenceAgeMs),
                    TimeSpan.FromMilliseconds(settings.VehicleSafety.ClockSkewToleranceMs),
                    new WireToGateRecoveryOptions(
                        settings.WireToGate.RecoveryResumeEnabled,
                        settings.WireToGate.RecoveryAuthenticationProofEnvironmentVariable,
                        settings.WireToGate.RecoveryAdministratorRole,
                        settings.WireToGate.RecoveryVerificationMethod));
                _wireToGateBusiness.SublotEntryRequested += (_, args) =>
                {
                    _logger.Write(
                        LogSeverity.Information,
                        nameof(App),
                        $"服务端请求录入Sublot：station={args.Value.StationId}，"
                        + $"revision={args.Value.WorklistRevision}，"
                        + $"expectedSublots={string.Join("、", args.Value.ExpectedSublots)}。 ");
                    viewModel.RefreshWireToGateInputState();
                };
                _wireToGateBusiness.OperatorEventPublished += (_, args) =>
                {
                    viewModel.ApplyWireToGateOperatorEvent(args.Value);
                    _ = journaledOperations.RefreshAsync();
                };
                viewModel.ConfigureWireToGate(
                    (sublot, inputMethod, cancellationToken) => _wireToGateBusiness.SubmitSublotAsync(
                        sublot,
                        inputMethod == ScanInputMethod.Scanner ? "SCANNER" : "KEYBOARD",
                        cancellationToken),
                    () => _wireToGateBusiness.CanSubmitSublot,
                    () => _wireToGateBusiness.CanRequestResumeAfterRepair,
                    // 四个会开异常处置会话的入口带上管理员填写的原因；null 时业务服务用该动作的缺省文字。
                    (reason, cancellationToken) => _wireToGateBusiness.RequestResumeAfterRepairAsync(
                        reason,
                        cancellationToken),
                    () => _wireToGateBusiness.CanRequestLoadCancellation,
                    cancellationToken => _wireToGateBusiness.RequestLoadCancellationAsync(
                        cancellationToken: cancellationToken),
                    () => _wireToGateBusiness.CanRequestLoadCompensation,
                    (reason, cancellationToken) => _wireToGateBusiness.RequestLoadCompensationAsync(
                        reason,
                        cancellationToken),
                    () => _wireToGateBusiness.CanRequestLoadCorrection,
                    cancellationToken => _wireToGateBusiness.RequestLoadCorrectionAsync(
                        cancellationToken: cancellationToken),
                    () => _wireToGateBusiness.CanRequestFaultCargoHandoff,
                    (reason, cancellationToken) => _wireToGateBusiness.RequestFaultCargoHandoffAsync(
                        reason,
                        cancellationToken),
                    () => _wireToGateBusiness.CanRequestForcedMechanicalRecovery,
                    (reason, cancellationToken) => _wireToGateBusiness.RequestForcedMechanicalRecoveryAsync(
                        reason,
                        cancellationToken),
                    () => _wireToGateBusiness.CanRequestManualChargingReturnToService,
                    cancellationToken => _wireToGateBusiness.RequestManualChargingReturnToServiceAsync(
                        cancellationToken: cancellationToken),
                    () => _wireToGateBusiness.IsLoadCancellationBeforeSublotOpen,
                    () => _wireToGateBusiness.CurrentSublotRejection,
                    () => _wireToGateBusiness.RecoveryReasonAlreadyGiven);
                viewModel.ConfigureForcedIsolation(
                    () => _wireToGateBusiness.CanConfirmForcedMechanicalRecovery,
                    cancellationToken => _wireToGateBusiness.ConfirmForcedMechanicalRecoveryAsync(
                        cancellationToken),
                    () => _wireToGateBusiness.PhysicallyUnknownSlots,
                    () => _wireToGateBusiness.CanSubmitHardwareRecoveryRecord,
                    (observations, cancellationToken) => _wireToGateBusiness.SubmitHardwareRecoveryRecordAsync(
                        observations,
                        cancellationToken));
                viewModel.StationDepartureCountdownTextOverride =
                    _wireToGateBusiness.DescribeExpiredStationDeadline;
                _wireToGateBusiness.Start();
            }

            // 告警的生产者（REQ-0269／REQ-0270）：一秒一轮，相关事件发生时提前一轮。
            _alarmMonitor = new OnboardAlarmMonitor(
                alarmBoard,
                () => ReadAlarmInputs(settings, vehicleSafetySignalProvider),
                _wireToGate?.Client,
                _logger,
                TimeSpan.FromSeconds(1));
            _alarmMonitor.AlarmsChanged += (_, args) => viewModel.UpdateOnboardAlarms(
                OnboardAlarmVisibility.ForLocalDisplay(args.Value, CurrentAlarmContext(settings.AgvId)));
            _ioModule.ConnectionChanged += (_, _) => _alarmMonitor?.RequestEvaluation();
            _ioModule.SnapshotChanged += (_, _) => _alarmMonitor?.RequestEvaluation();
            _controller.StateChanged += (_, _) => _alarmMonitor?.RequestEvaluation();
            if (_wireToGate is not null)
            {
                _wireToGate.StateChanged += (_, _) => _alarmMonitor?.RequestEvaluation();
            }
            if (_wireToGateBusiness is not null)
            {
                _wireToGateBusiness.OperatorEventPublished += (_, _) => _alarmMonitor?.RequestEvaluation();
            }

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            window.Show();
            await viewModel.InitializeAsync().ConfigureAwait(true);
            if (settings.WireToGate.Enabled)
            {
                // Await the first asynchronous projection attempt before the
                // handshake snapshot.  A failed attempt still completes with
                // UNKNOWN and therefore remains fail-closed; it is never
                // promoted to STOPPED merely to make startup succeed.
                await vehicleSafetySignalProvider.WaitForFirstRefreshAsync().ConfigureAwait(true);
            }
            // 握手之前先求值一轮：握手报的是告警板上的全量，不先算，第一份就是空的，要等下一轮才补上。
            await _alarmMonitor.EvaluateOnceAsync().ConfigureAwait(true);
            _alarmMonitor.Start();
            _wireToGate?.Start();
            if (settings.Automation.Enabled)
            {
                _automationServer = new OnboardAutomationHttpServer(
                    new OnboardAutomationHostOptions(
                        settings.Automation.ListenAddress,
                        settings.Automation.Port),
                    new WpfOnboardAutomationFacade(
                        settings.AgvId,
                        _controller,
                        _wireToGate ?? throw new InvalidOperationException("WIRE_TO_GATE未初始化。"),
                        _wireToGateBusiness ?? throw new InvalidOperationException("WIRE_TO_GATE业务未初始化。"),
                        new SystemClock()));
                await _automationServer.StartAsync().ConfigureAwait(true);
                _logger.Write(
                    LogSeverity.Information,
                    nameof(App),
                    $"车载端自动化接口已启动：{_automationServer.Endpoint}。 ");
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or System.Net.Sockets.SocketException
                or System.Text.Json.JsonException)
        {
            _logger?.Write(LogSeverity.Error, nameof(App), "车载端启动失败。", exception);
            System.Diagnostics.Debug.WriteLine(exception);
            MessageBox.Show(
                "软件无法启动，请联系维护人员检查程序配置。",
                "软件启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _automationServer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _alarmMonitor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _wireToGateBusiness?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _wireToGate?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _controller?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _ruleGateway?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _ioModule?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _vehicleSafetySignalProvider?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            _logger?.Write(LogSeverity.Warning, nameof(App), "程序退出时停止后台服务失败。", exception);
        }

        GC.SuppressFinalize(this);
    }

    private OnboardAlarmInputs ReadAlarmInputs(
        OnboardSettings settings,
        ControlServerVehicleSafetySignalProvider vehicleSafetySignalProvider)
    {
        ModbusTcpIoModuleClient io = _ioModule ?? throw new InvalidOperationException("IO模块未初始化。");
        OnboardController controller = _controller ?? throw new InvalidOperationException("控制器未初始化。");
        bool ioConnected = io.IsConnected;
        IoSnapshot ioSnapshot = io.CurrentSnapshot;
        // 旧任务系统网关在 WIRE_TO_GATE 模式下是空实现，连接状态恒为断开，不适用就不判。
        bool? legacyRuleGatewayConnected = settings.WireToGate.Enabled ? null : _ruleGateway?.IsConnected == true;
        // 出发安全信号只在 WIRE_TO_GATE 模式下轮询。
        VehicleSafetySignal? vehicleSafety = settings.WireToGate.Enabled ? vehicleSafetySignalProvider.Read() : null;
        // 时刻最后取：先取时刻再读 IO，一次恰好落在两者之间的轮询会让快照「来自未来」，被判成陈旧。
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new OnboardAlarmInputs(
            now,
            ioConnected,
            ioSnapshot,
            TimeSpan.FromMilliseconds(settings.Workflow.IoSnapshotMaxAgeMs),
            legacyRuleGatewayConnected,
            controller.Current,
            vehicleSafety,
            TimeSpan.FromMilliseconds(settings.VehicleSafety.MaximumEvidenceAgeMs),
            TimeSpan.FromMilliseconds(settings.VehicleSafety.ClockSkewToleranceMs),
            _wireToGate?.Current.ReasonCodes ?? [],
            _wireToGateBusiness?.CurrentOperationSnapshot)
        {
            // 期待动作超时（REQ-0358）只在 WIRE_TO_GATE 模式下有；旧模式没有业务服务，这一项为空，求值器不判。
            ExpectedActionWait = _wireToGateBusiness?.CurrentExpectedActionWait,
            ExpectedActionOverdueThreshold = settings.Workflow.ExpectedActionOverdueThreshold
        };
    }

    // 本端的求值器不产出与停靠相关的告警，停靠不参与收敛。
    private OnboardAlarmContext CurrentAlarmContext(string agvId) => new(
        agvId,
        null,
        _wireToGateBusiness?.CurrentOperationSnapshot?.SlotOperationAttemptId
            ?? _controller?.Current.ActiveOperation?.OperationId);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Write(LogSeverity.Error, nameof(App), "界面发生未处理异常。", e.Exception);
        _controller?.EnterFatalFault(
            "UNHANDLED_UI_ERROR",
            "软件运行异常，已禁止继续操作。请确认仓门状态并联系维护人员。");
        MessageBox.Show(
            "软件运行异常，已禁止继续操作。\n请确认仓门状态并联系维护人员。",
            "软件运行异常",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
