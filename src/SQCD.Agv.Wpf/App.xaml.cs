using System.IO;
using System.Net.Http;
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
    private SlotIoModuleClientAdapter? _ioModule;
    private TcpJsonRuleGateway? _ruleGateway;
    private OnboardController? _controller;
    private SqliteOnboardExecutionJournal? _journal;
    private WireToGateSessionClient? _candidateSession;
    private MainViewModel? _viewModel;
    private bool _disposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            OnboardSettings settings = OnboardSettings.Load(settingsPath);
            _logger = new FileAppLogger(settings.Logging);
            HttpClient ioHttpClient = new()
            {
                BaseAddress = new Uri(settings.WireToGate.IoSimulatorBaseUrl),
                Timeout = TimeSpan.FromMilliseconds(settings.WireToGate.IoRequestTimeoutMs)
            };
            HttpSimulatorSlotIoProvider slotProvider = new(ioHttpClient);
            _ioModule = new SlotIoModuleClientAdapter(
                slotProvider,
                new SystemClock(),
                TimeSpan.FromMilliseconds(settings.IoModule.PollIntervalMs),
                _logger);
            _ruleGateway = new TcpJsonRuleGateway(
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
                workflowOptions);
            _ruleGateway.HeartbeatStatusProvider = () => new RuleHeartbeatStatus(
                _ioModule.IsConnected,
                _controller.Current.ActiveOperation?.OperationId,
                _controller.Current.DeparturePermitted);

            _journal = new SqliteOnboardExecutionJournal(settings.WireToGate.JournalDatabasePath);
            _candidateSession = new WireToGateSessionClient(
                new WireToGateSessionOptions(
                    settings.WireToGate.ControlServerHost,
                    settings.WireToGate.ControlServerPort,
                    settings.AgvId,
                    settings.OnboardInstanceId,
                    settings.WireToGate.CredentialEnvironmentVariable,
                    settings.WireToGate.UseTls,
                    settings.WireToGate.ServerCertificateSha256,
                    TimeSpan.FromMilliseconds(settings.WireToGate.ConnectTimeoutMs)),
                slotProvider,
                _journal);

            string version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown";
            _logger.Write(
                LogSeverity.Information,
                nameof(App),
                $"车载端启动：agvId={settings.AgvId}，environment={settings.Environment}，version={version}。");
            _viewModel = new MainViewModel(_controller, _logger, settings.AgvId, _candidateSession, _journal);
            WireToGateMainWindow window = new() { DataContext = _viewModel };
            MainWindow = window;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            window.Show();
            await _viewModel.InitializeAsync().ConfigureAwait(true);
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
            _viewModel?.Dispose();
            _candidateSession?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _journal?.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
