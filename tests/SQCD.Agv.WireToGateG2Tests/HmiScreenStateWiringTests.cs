using System.IO;
using System.Reflection;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ScreenStateOutput;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;
using static SQCD.Agv.WireToGateG2Tests.HmiScreenStateMonitorDispatcherTests;
using static SQCD.Agv.WireToGateG2Tests.MultiDemandViewModelTests;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The switch and the parameter check in front of the screen-state snapshot (onboard-hmi#315, review S2 and S4), on the
/// same <see cref="HmiScreenStateWiring.Start"/> that <c>App.StartScreenState</c> calls.
/// </summary>
/// <remarks>
/// "Nothing is built" is read off the view model: the number of handlers on its <c>PropertyChanged</c>, its collections'
/// <c>CollectionChanged</c> and its commands' <c>CanExecuteChanged</c> does not change, and no file appears in the output
/// directory, even after several flush and heartbeat periods. No <c>IntegrationSlice</c>/<c>ProtocolVector</c> trait: a
/// diagnostic output, it proves no protocol vector.
/// </remarks>
public sealed class HmiScreenStateWiringTests
{
    [Fact]
    public async Task SwitchedOffNothingIsBuiltSubscribedOrWritten()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        using TemporaryDirectory directory = new();
        RecordingLogger logger = new();
        using DispatcherThread thread = new();
        int[] before = Subscribers(viewModel);

        HmiScreenStateWiring.Started? started = thread.Dispatcher.Invoke(() => HmiScreenStateWiring.Start(
            new ScreenStateSettings { Directory = directory.Path },
            new LogSettings(),
            viewModel,
            new ModalDialogRegistry(),
            logger,
            thread.Dispatcher));
        await Task.Delay(TimeSpan.FromMilliseconds(1_500), TestContext.Current.CancellationToken);

        Assert.Null(started);
        Assert.Equal(before, Subscribers(viewModel));
        Assert.Empty(Directory.GetFiles(directory.Path));
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// Review S4: switched on with a heartbeat no longer than the flush interval, the HMI still starts (the settings pass
    /// <c>Validate</c>, <c>Start</c> returns normally), the snapshot does not start, and one log line says why.
    /// </summary>
    [Fact]
    public async Task SwitchedOnWithInvalidParametersTheHmiStartsWithoutTheSnapshotAndSaysWhy()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        using TemporaryDirectory directory = new();
        RecordingLogger logger = new();
        using DispatcherThread thread = new();
        int[] before = Subscribers(viewModel);
        OnboardSettings settings = new()
        {
            ScreenState = new ScreenStateSettings
            {
                Enabled = true,
                Directory = directory.Path,
                FlushIntervalMs = 500,
                HeartbeatIntervalMs = 500
            }
        };
        settings.Validate();

        HmiScreenStateWiring.Started? started = thread.Dispatcher.Invoke(() => HmiScreenStateWiring.Start(
            settings.ScreenState,
            settings.Logging,
            viewModel,
            new ModalDialogRegistry(),
            logger,
            thread.Dispatcher));
        await Task.Delay(TimeSpan.FromMilliseconds(1_500), TestContext.Current.CancellationToken);

        Assert.Null(started);
        Assert.Equal(before, Subscribers(viewModel));
        Assert.Empty(Directory.GetFiles(directory.Path));
        (LogSeverity Severity, string Source, string Message) line = Assert.Single(logger.Entries);
        Assert.Equal(LogSeverity.Warning, line.Severity);
        Assert.Equal(nameof(HmiScreenStateWiring), line.Source);
        Assert.Contains("参数无效，本次运行不启动快照", line.Message, StringComparison.Ordinal);
        Assert.Contains("heartbeatIntervalMs=500", line.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: switched on with valid parameters it does subscribe and write, so the two above are not vacuous.</summary>
    [Fact]
    public async Task SwitchedOnItSubscribesAndWrites()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        using TemporaryDirectory directory = new();
        using DispatcherThread thread = new();
        int[] before = Subscribers(viewModel);

        HmiScreenStateWiring.Started? started = thread.Dispatcher.Invoke(() => HmiScreenStateWiring.Start(
            new ScreenStateSettings { Enabled = true, Directory = directory.Path, FlushIntervalMs = 50, HeartbeatIntervalMs = 1_000 },
            new LogSettings(),
            viewModel,
            new ModalDialogRegistry(),
            new RecordingLogger(),
            thread.Dispatcher));
        Assert.NotNull(started);
        Assert.NotNull(started.Monitor);
        await WaitAsync(
            () => File.Exists(Path.Combine(directory.Path, HmiScreenStateFileWriter.CurrentFileName)),
            "the first write");
        int[] after = Subscribers(viewModel);
        thread.Dispatcher.Invoke(started.Monitor.Dispose);
        await started.Writer.DisposeAsync();

        Assert.All(before.Zip(after), pair => Assert.Equal(pair.First + 1, pair.Second));
    }

    /// <summary>
    /// With a session (onboard-hmi#314): the monitor subscribes to it and lets go on dispose, the written state carries
    /// its accepted safety state, and a change of that alone -- no view-model property moves -- is written before the next
    /// heartbeat, which is set to the longest the settings allow so it cannot be what wrote it.
    /// </summary>
    [Fact]
    public async Task SwitchedOnWithASessionItWritesTheAcceptedSafetyAndASessionChangeAloneIsWritten()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        using TemporaryDirectory directory = new();
        using DispatcherThread thread = new();
        FakeScreenStateSession session = new(new WireToGateAcceptedSafety(
            4,
            1,
            new WireToGateSafetySummaryPayload(false, true, false, true, false, ["LOCK_NOT_CLOSED"]),
            WireToGateAcceptedSafetySource.SafetyStateChanged));

        HmiScreenStateWiring.Started? started = thread.Dispatcher.Invoke(() => HmiScreenStateWiring.Start(
            new ScreenStateSettings { Enabled = true, Directory = directory.Path, FlushIntervalMs = 50, HeartbeatIntervalMs = 60_000 },
            new LogSettings(),
            viewModel,
            new ModalDialogRegistry(),
            new RecordingLogger(),
            thread.Dispatcher,
            session: session));
        Assert.NotNull(started);
        Assert.NotNull(started.Monitor);
        string current = Path.Combine(directory.Path, HmiScreenStateFileWriter.CurrentFileName);
        await WaitAsync(() => AcceptedVersionIn(current) == 4, "the first write, carrying version 4");
        Assert.Equal(1, session.Subscribers);

        session.Move(new WireToGateAcceptedSafety(
            5,
            1,
            new WireToGateSafetySummaryPayload(true, true, true, true, false, []),
            WireToGateAcceptedSafetySource.SessionReadiness));
        await Task.Run(session.Raise, TestContext.Current.CancellationToken);
        await WaitAsync(() => AcceptedVersionIn(current) == 5, "the session change alone to be written");

        thread.Dispatcher.Invoke(started.Monitor.Dispose);
        await started.Writer.DisposeAsync();
        Assert.Equal(0, session.Subscribers);
    }

    /// <summary>The accepted safety version in the current file, or null while it is absent or being replaced.</summary>
    private static long? AcceptedVersionIn(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.GetProperty("state").GetProperty("basis").GetProperty("acceptedSafety")
                .GetProperty("safetyStateVersion").GetInt64();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A session as the snapshot sees it: an accepted safety state the test moves, and the event it raises.</summary>
    internal sealed class FakeScreenStateSession(WireToGateAcceptedSafety accepted) : IHmiScreenStateSessionSource
    {
        private WireToGateAcceptedSafety _accepted = accepted;
        private EventHandler<ValueChangedEventArgs<WireToGateSessionSnapshot>>? _stateChanged;

        public event EventHandler<ValueChangedEventArgs<WireToGateSessionSnapshot>>? StateChanged
        {
            add => _stateChanged += value;
            remove => _stateChanged -= value;
        }

        public WireToGateAcceptedSafety AcceptedSafety => Volatile.Read(ref _accepted);

        public int Subscribers => _stateChanged?.GetInvocationList().Length ?? 0;

        public void Move(WireToGateAcceptedSafety accepted) => Volatile.Write(ref _accepted, accepted);

        public void Raise() => _stateChanged?.Invoke(
            this,
            new ValueChangedEventArgs<WireToGateSessionSnapshot>(new WireToGateSessionSnapshot(
                true,
                1,
                WireToGateSessionReadiness.Ready,
                [],
                1,
                AcceptedSafety.SafetyStateVersion,
                DateTimeOffset.UtcNow)));
    }

    /// <summary>Handlers on the view model's <c>PropertyChanged</c>, then each watched collection and command, in order.</summary>
    private static int[] Subscribers(MainViewModel viewModel) =>
    [
        Handlers(viewModel, "PropertyChanged"),
        Handlers(viewModel.Logs, "CollectionChanged"), // logs-reference: counts its CollectionChanged handlers, reads no line
        Handlers(viewModel.Lockers, "CollectionChanged"),
        Handlers(viewModel.WorklistItems, "CollectionChanged"),
        Handlers(viewModel.JourneyPlanLegs, "CollectionChanged"),
        Handlers(viewModel.RecoveryDemandChoices, "CollectionChanged"),
        Handlers(viewModel.ScannerSubmitCommand, "CanExecuteChanged"),
        Handlers(viewModel.ManualSubmitCommand, "CanExecuteChanged"),
        Handlers(viewModel.ClearLogsCommand, "CanExecuteChanged"),
        .. viewModel.Lockers.Select(locker => Handlers(locker, "PropertyChanged")),
    ];

    private static int Handlers(object source, string eventName)
    {
        for (Type? type = source.GetType(); type is not null; type = type.BaseType)
        {
            FieldInfo? field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null)
            {
                return (field.GetValue(source) as Delegate)?.GetInvocationList().Length ?? 0;
            }
        }

        throw new InvalidOperationException($"{source.GetType().Name} has no backing field for {eventName}");
    }
}
