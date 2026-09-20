using System.Globalization;
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
    /// 已有实例在运行时，本进程的退出码。
    ///
    /// 取 3 而不是 1 或 2：那两个在脚本里是「泛指出错」的常用值，而这一次退出并不是出错——
    /// 是守卫按设计挡下了一次重复启动。启动失败走的仍然是原来的 <c>Shutdown(-1)</c>。
    /// </summary>
    internal const int ExitCodeAlreadyRunning = 3;

    private FileAppLogger? _logger;
    private SingleInstanceGuard? _singleInstance;
    private ModbusTcpIoModuleClient? _ioModule;
    private IRuleGateway? _ruleGateway;
    private OnboardController? _controller;
    private WireToGateSessionService? _wireToGate;
    private WireToGateBusinessService? _wireToGateBusiness;
    private OnboardAutomationHttpServer? _automationServer;
    private ControlServerVehicleSafetySignalProvider? _vehicleSafetySignalProvider;
    private bool _disposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Logging has to exist before anything that can throw, or a rejected
        // configuration leaves no trace at all: the catch below would run with
        // a null logger, write nothing, and park on a modal dialog nobody is
        // standing in front of.  The default directory matches the deployed
        // configuration, so the bootstrap logger and the configured one append
        // to the same file.
        _logger = new FileAppLogger(new LogSettings());

        try
        {
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            OnboardSettings settings = OnboardSettings.Load(settingsPath);

            // 配置一读出来就换成配置里的那个日志器。下面单例守卫写的那一行必须落在部署实际在用
            // 的日志文件里，否则事后追「第二个进程是什么时候被挡下来的」要去翻另一个目录。
            _logger = new FileAppLogger(settings.Logging);

            // 单例检查排在所有副作用之前。车辆安全投影一构造就开始轮询服务端，IO 客户端一构造
            // 就握着 Modbus 目标，会话服务更是直接建连接——而第二个实例的要求恰恰是不连服务端、
            // 不碰 IO、不建会话（onboard-hmi#157）。
            _singleInstance = SingleInstanceGuard.Acquire(settings.AgvId, _logger);
            if (!_singleInstance.ShouldStart)
            {
                int? runningPid = RunningInstanceWindow.BringToFront(_logger);
                string pidText = runningPid is int pid
                    ? pid.ToString(CultureInfo.InvariantCulture)
                    : "未知";
                _logger.Write(
                    LogSeverity.Warning,
                    nameof(App),
                    $"已有实例在运行，pid={pidText}，本次不启动。互斥体名字={_singleInstance.Name}。");
                Shutdown(ExitCodeAlreadyRunning);
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
            MainViewModel viewModel = new(_controller, _logger, new SystemClock(), settings.AgvId);
            MainWindow window = new() { DataContext = viewModel };
            MainWindow = window;
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
                    TimeSpan.FromMilliseconds(settings.Workflow.IoSnapshotMaxAgeMs),
                    TimeSpan.FromMilliseconds(settings.VehicleSafety.MaximumEvidenceAgeMs),
                    TimeSpan.FromMilliseconds(settings.VehicleSafety.ClockSkewToleranceMs));
                _wireToGate.StateChanged += (_, args) =>
                {
                    viewModel.UpdateWireToGateStatus(args.Value);
                    _controller.RefreshExternalSafetyState();
                };
                _wireToGate.JourneyChanged += (_, args) =>
                {
                    viewModel.UpdateWireToGateJourney(args.Value);
                    _controller.RefreshExternalSafetyState();
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
                        $"服务端请求录入Sublot：demandId={args.Value.DemandId}，revision={args.Value.WorklistRevision}。 ");
                    viewModel.RefreshWireToGateInputState();
                };
                // 界面在业务服务之前订阅了 JourneyChanged，清单刷新的那一刻录入请求还没撤，
                // 所以撤销之后要再刷一次按钮。
                _wireToGateBusiness.SublotEntryExpired += (_, _) =>
                    viewModel.RefreshWireToGateInputState();
                _wireToGateBusiness.OperatorEventPublished += (_, args) =>
                    viewModel.ApplyWireToGateOperatorEvent(args.Value);
                viewModel.ConfigureWireToGate(
                    (sublot, inputMethod, cancellationToken) => _wireToGateBusiness.SubmitSublotFromOperatorAsync(
                        sublot,
                        inputMethod == ScanInputMethod.Scanner ? "SCANNER" : "KEYBOARD",
                        cancellationToken),
                    () => _wireToGateBusiness.CanSubmitSublot,
                    () => _wireToGateBusiness.CanRequestResumeAfterRepair,
                    cancellationToken => _wireToGateBusiness.RequestResumeAfterRepairAsync(
                        cancellationToken: cancellationToken),
                    () => _wireToGateBusiness.CanRequestLoadCancellation,
                    cancellationToken => _wireToGateBusiness.RequestLoadCancellationAsync(
                        cancellationToken: cancellationToken),
                    () => _wireToGateBusiness.CanRequestLoadCompensation,
                    cancellationToken => _wireToGateBusiness.RequestLoadCompensationAsync(
                        cancellationToken: cancellationToken),
                    () => _wireToGateBusiness.CanRequestLoadCorrection,
                    cancellationToken => _wireToGateBusiness.RequestLoadCorrectionAsync(
                        cancellationToken: cancellationToken),
                    () => _wireToGateBusiness.CanRequestFaultCargoHandoff,
                    cancellationToken => _wireToGateBusiness.RequestFaultCargoHandoffAsync(
                        cancellationToken: cancellationToken),
                    // 按名字传：这一条是「取消装货」在故障态下唯一还开着的那半边判据，
                    // 请求走的仍是上面那个 RequestLoadCancellationAsync（onboard-hmi#85）。
                    canRequestLoadCancellationBeforeLoad: () =>
                        _wireToGateBusiness.CanRequestLoadCancellationBeforeLoad);
                _wireToGateBusiness.Start();
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
            _wireToGateBusiness?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _wireToGate?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _controller?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _ruleGateway?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _ioModule?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _vehicleSafetySignalProvider?.Dispose();
            // 名字最后放开。先放开它，下一个进程就可能在本进程还握着 Modbus 连接和会话时抢到，
            // 那正是这张票要消灭的那种重叠。
            _singleInstance?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            _logger?.Write(LogSeverity.Warning, nameof(App), "程序退出时停止后台服务失败。", exception);
        }

        GC.SuppressFinalize(this);
    }

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
