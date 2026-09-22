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
    /// <summary>
    /// 单例守卫挡下本次启动时的退出码：本机已有车载端在运行，或判断不了有没有。
    /// </summary>
    /// <remarks>
    /// 取 3 而不是 1 或 2：那两个在脚本里是「泛指出错」的常用值，而这一次退出不是出错，是守卫按设计挡下了一次重复
    /// 启动。与现场线（03027de）用的是同一个值，运维脚本不必分版本判断。启动失败走的仍然是 <see cref="ExitCodeStartupFailed"/>（-1）。
    /// </remarks>
    internal const int ExitCodeNotStartedAnotherInstance = 3;

    /// <summary>
    /// 启动失败的退出码：配置被拒，或启动途中 IO／网络出错。
    /// </summary>
    /// <remarks>一直是 -1，运维脚本按它判断；onboard-hmi#14 改的是「能不能退出来」，不是退出码。</remarks>
    internal const int ExitCodeStartupFailed = -1;

    private FileAppLogger? _logger;
    private SingleInstanceGuard? _singleInstance;
    private ModbusTcpIoModuleClient? _ioModule;
    private IRuleGateway? _ruleGateway;
    private OnboardController? _controller;
    private WireToGateSessionService? _wireToGate;
    private WireToGateBusinessService? _wireToGateBusiness;
    private OnboardAutomationHttpServer? _automationServer;
    private ControlServerVehicleSafetySignalProvider? _vehicleSafetySignalProvider;
    private OnboardAlarmMonitor? _alarmMonitor;
    private MainViewModel? _viewModel;
    private bool _disposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 引导日志器在任何可能被拒的东西之前建（onboard-hmi#14）：它不读业务配置，写到固定的 <程序目录>/logs，
        // 与出厂配置的日志目录是同一个。修复之前读配置在建日志器之前，配置一被拒，catch 拿到的日志器是 null，
        // 一个字节都不写，再被一个没人点的模态框挡住退出——车上看到的就是「进程活着、没窗口、没日志」。
        FileAppLogger bootstrapLogger = new(new LogSettings());
        string settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        OnboardSettings? loadedSettings = StartupConfiguration.TryLoad(settingsPath, bootstrapLogger);
        if (loadedSettings is null)
        {
            await ExitAfterStartupFailureAsync().ConfigureAwait(true);
            return;
        }

        OnboardSettings settings = loadedSettings;
        try
        {
            _logger = new FileAppLogger(settings.Logging);
            // 单例守卫在任何副作用之前（onboard-hmi#173、#165）：下面的车辆安全投影一构造就开始轮询服务端，IO 客户端
            // 一构造就握着 Modbus 目标——第二个实例一步都不能走到那里。顺序是判据的一部分；日志器提前是为了让这一行
            // 落在部署实际在用的日志文件里。
            _singleInstance = SingleInstanceGuard.Acquire(settings.AgvId, _logger);
            if (!_singleInstance.ShouldStart)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                RunningInstanceWindow.ReportRefusal(_singleInstance, _logger);
                Shutdown(ExitCodeNotStartedAnotherInstance);
                return;
            }

            _vehicleSafetySignalProvider = new ControlServerVehicleSafetySignalProvider(
                settings.VehicleSafety,
                startPolling: settings.WireToGate.Enabled);
            ControlServerVehicleSafetySignalProvider vehicleSafetySignalProvider = _vehicleSafetySignalProvider;
            Func<bool> vehicleStoppedProvider = () =>
                vehicleSafetySignalProvider.Read().IsStoppedAndFresh(
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromMilliseconds(settings.VehicleSafety.MaximumEvidenceAgeMs),
                    TimeSpan.FromMilliseconds(settings.VehicleSafety.ClockSkewToleranceMs));
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
            : null,
                // 复位复核要问的「现在有没有在开门」。控制器自己的 _operationLock 只覆盖 MVP 的
                // SubmitScanAsync，在 v2 上永远拿得到，所以真实来源是那两个执行器各自的门
                // （8005-agv-onboard-hmi#171）。业务服务晚于控制器构造，故延迟求值。
                () => _wireToGateBusiness?.HasSlotWorkInFlight == true);
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
            // 操作员工号：两种模式配置的是同一个**变量名**，但只有 WIRE_TO_GATE 那条路校验它有**值**。
            // 所以旧模式下没设这个环境变量时，复位入口不会出现——「有这个机制」不等于「这个机制生效」
            // （8005-agv-onboard-hmi#171 审查，产品路发现 3）。读取放在每次用的时候，不在启动时定格。
            string operatorIdVariable = settings.WireToGate.OperatorIdEnvironmentVariable;
            MainViewModel viewModel = new(
                _controller,
                _logger,
                settings.AgvId,
                slotConfigurationStore?.Current ?? localSlotConfiguration,
                () => Environment.GetEnvironmentVariable(operatorIdVariable));
            _viewModel = viewModel;
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
                        settings.WireToGate.RecoveryVerificationMethod),
                    // 让「本界面已禁止扫码开门」成为真的：v2 的扫码不经过控制器，所以业务服务
                    // 自己读锁存（8005-agv-onboard-hmi#171）。
                    () => _controller?.IsFatalFaultLatched == true);
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
                    // 所选需求只在扫码前取消、本站多条需求时用得上（批次7-14）；其余情形业务服务忽略它。
                    (selectedDemandId, cancellationToken) =>
                        _wireToGateBusiness.RequestLoadCancellationAsync(
                            WireToGateBusinessService.LoadCancellationDefaultReason,
                            selectedDemandId,
                            cancellationToken),
                    () => _wireToGateBusiness.IsLoadCancellationDemandSelectionRequired,
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
                    () => _wireToGateBusiness.RecoveryReasonAlreadyGiven,
                    () => _wireToGateBusiness.RecoveryFallbackDemandId,
                    // 按名字传，不接着按位置排：按位置传的参数在调用点没有名字，grep 找不到它的用法
                    // （8005-agv-onboard-hmi#177 票面记下的那一次，就是这样把两个就绪委托判成了「v2 不传」）。
                    sublotEntryPausedUntilStopped: () => _wireToGateBusiness.IsSublotEntryPausedUntilStopped,
                    // 视图模型自己订阅停稳信号的变化，刷新入口、横幅与控制器提示（8005-agv-onboard-hmi#177）。
                    vehicleSafetySignal: vehicleSafetySignalProvider,
                    // 取消装货在严重安全故障锁存期间唯一还开着的那一半：扫码之前，不碰 IO（8005-agv-onboard-hmi#174）。
                    // 不接这一条，视图模型按 false 处理，锁存期间取消装货整个关着——安全，但操作员又只能干等站点超时。
                    canRequestLoadCancellationBeforeAnySublot: () =>
                        _wireToGateBusiness.CanRequestLoadCancellationBeforeAnySublot);
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
            // 第二次双击被挡下时，由本实例自己把窗口调出来（onboard-hmi#165 第 2 条）。
            RunningInstanceWindow.Listen(window);
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
            (_logger ?? bootstrapLogger).Write(LogSeverity.Error, nameof(App), "车载端启动失败。", exception);
            System.Diagnostics.Debug.WriteLine(exception);
            await ExitAfterStartupFailureAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 原因已经写进日志之后的退出：交互式桌面上给现场一个有时限的提示框，然后以 <see cref="ExitCodeStartupFailed"/> 退出。
    /// </summary>
    /// <remarks>取舍见 <see cref="StartupFailureNotice"/>：提示框不阻塞退出，最多挡 30 秒，不在交互式桌面上就不弹。</remarks>
    private async Task ExitAfterStartupFailureAsync()
    {
        // 提示框不是本应用的窗口，关掉它不该、也不会触发「最后一个窗口关闭即退出」；退出只由下面那次 Shutdown 决定。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (Environment.UserInteractive)
        {
            await StartupFailureNotice.ShowAsync(
                () => MessageBox.Show(
                    "软件无法启动，请联系维护人员检查程序配置。原因已写入程序目录下的 logs 日志。",
                    "软件启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error),
                StartupFailureNotice.Timeout).ConfigureAwait(true);
        }

        Shutdown(ExitCodeStartupFailed);
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

        // 最后才放开名字：IO 客户端停下之前，下一个实例不该拿到它。
        _singleInstance?.Dispose();
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

    /// <summary>
    /// 界面里没人接住的异常最后到这里。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这条路不只兜底：<c>MainWindow.xaml.cs</c> 的恢复类按钮全是 <c>async void</c> 的
    /// handler，自己不 catch，所以它们从业务服务里带出来的业务拒绝——「恢复会话还没开」
    /// 「原因还没填」——正是从这里出去的。原来它们一律 <c>EnterFatalFault</c>，于是按错一个
    /// 恢复按钮同样会把整车锁死（8005-agv-onboard-hmi#171）。
    /// </para>
    /// <para>
    /// 判据与 <c>MainViewModel.HandleCommandError</c> 共用同一张登记表，两条路不会各分各的。
    /// <see cref="OperationCanceledException"/> 排在前面：锁存之后控制器主动取消在途流程，
    /// 那是受控停止，不该被当成新的界面异常再报一次。
    /// </para>
    /// </remarks>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        switch (OnboardFailureClassification.Classify(e.Exception))
        {
            case OnboardCommandFailureKind.ControlledCancellation:
                _logger?.Write(LogSeverity.Information, nameof(App), "界面命令已被取消。", e.Exception);
                break;

            case OnboardCommandFailureKind.OperatorRejection:
                _logger?.Write(
                    LogSeverity.Warning,
                    nameof(App),
                    $"界面命令被业务规则拒绝：{e.Exception.Message}。 ");
                _viewModel?.ReportOperatorRejection(e.Exception.Message);
                break;

            default:
                _logger?.Write(LogSeverity.Error, nameof(App), "界面发生未处理异常。", e.Exception);
                _controller?.EnterFatalFault("UNHANDLED_UI_ERROR", OnboardFatalFaultBanner.UnhandledUiError);
                MessageBox.Show(
                    OnboardFatalFaultBanner.UnhandledUiErrorDialog,
                    "软件运行异常",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                break;
        }

        e.Handled = true;
    }
}
