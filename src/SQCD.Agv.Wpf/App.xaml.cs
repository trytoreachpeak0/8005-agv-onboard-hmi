using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using SQCD.Agv.Application;
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
    private bool _disposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            OnboardSettings settings = OnboardSettings.Load(settingsPath);
            // 当前项目尚未接入可信的车速/驻车信号。未知必须按“未停稳”处理，
            // 禁止用“没有仓位操作”冒充车辆物理停稳。现场适配器只需替换此 provider。
            UnavailableVehicleSafetySignalProvider vehicleSafetySignalProvider =
                new UnavailableVehicleSafetySignalProvider();
            Func<bool> vehicleStoppedProvider = () =>
                vehicleSafetySignalProvider.Read().IsStoppedAndFresh(
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromMilliseconds(settings.Workflow.IoSnapshotMaxAgeMs));
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
            MainViewModel viewModel = new(_controller, _logger, settings.AgvId);
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
                    vehicleStoppedProvider);
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
                    vehicleSafetySignalProvider);
                _wireToGateBusiness.SublotEntryRequested += (_, args) =>
                {
                    _logger.Write(
                        LogSeverity.Information,
                        nameof(App),
                        $"服务端请求录入Sublot：demandId={args.Value.DemandId}，revision={args.Value.WorklistRevision}。 ");
                    viewModel.RefreshWireToGateInputState();
                };
                viewModel.ConfigureWireToGate(
                    (sublot, inputMethod, cancellationToken) => _wireToGateBusiness.SubmitSublotAsync(
                        sublot,
                        inputMethod == ScanInputMethod.Scanner ? "SCANNER" : "KEYBOARD",
                        cancellationToken),
                    () => _wireToGateBusiness.CanSubmitSublot);
                _wireToGateBusiness.Start();
            }

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            window.Show();
            await viewModel.InitializeAsync().ConfigureAwait(true);
            _wireToGate?.Start();
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException
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
            _wireToGateBusiness?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _wireToGate?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _controller?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _ruleGateway?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _ioModule?.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
