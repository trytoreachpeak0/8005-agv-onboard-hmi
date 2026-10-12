using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ScreenState;
using SQCD.Agv.Wpf.ScreenStateOutput;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;
using static SQCD.Agv.WireToGateG2Tests.MultiDemandViewModelTests;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The screen-state snapshot on a real WPF dispatcher (onboard-hmi#315, T2 acceptance "调度线程隔离" and "线程模型").
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a real dispatcher.</b> G2 has no WPF application, so <c>RunOnUiThread</c> runs on the calling thread and
/// <c>App.OnDispatcherUnhandledException</c> is never reached: neither the thread model nor "a failure does not latch the
/// vehicle" can be seen there. These tests run a <see cref="Dispatcher"/> on an STA thread and assert on its
/// <see cref="Dispatcher.UnhandledException"/>, which is what the application turns into <c>UNHANDLED_UI_ERROR</c>.
/// </para>
/// <para>No <c>IntegrationSlice</c>/<c>ProtocolVector</c> trait: a diagnostic output, it proves no protocol vector.</para>
/// </remarks>
public sealed class HmiScreenStateMonitorDispatcherTests
{
    private static readonly TimeSpan Flush = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Research 4.3 item 1: each dispatcher entry point, made to throw, with a logger that throws a non-IO exception too
    /// (the logger used in the catch). Asserts: nothing reaches the dispatcher's unhandled-exception event; the entries
    /// that <c>RefreshWireToGateInputStateCore</c> writes, and the banner and operator record that
    /// <c>ReportOperatorRejection</c> writes, end up exactly as on a view model with no snapshot attached; the snapshot is
    /// disabled and the writer was told to write <c>DISABLED</c>, naming that entry point.
    /// </summary>
    [Theory]
    [InlineData("OnViewModelPropertyChanged", true)]
    [InlineData("OnViewModelPropertyChanged", false)]
    [InlineData("OnCollectionChanged", true)]
    [InlineData("OnCommandCanExecuteChanged", true)]
    [InlineData("OnFlushTick", true)]
    [InlineData("OnHeartbeatTick", true)]
    [InlineData("Build", true)]
    [InlineData("Build", false)]
    public async Task AFailingEntryPointNeverReachesTheDispatcherAndLeavesTheScreenAsWithoutASnapshot(
        string failingEntry,
        bool loggerThrows)
    {
        await using OnboardController observedController = Controller();
        await using OnboardController referenceController = Controller();
        MainViewModel observed = await ViewModel(observedController);
        MainViewModel reference = await ViewModel(referenceController);
        bool open = false;
        foreach (MainViewModel viewModel in new[] { observed, reference })
        {
            viewModel.ConfigureWireToGate(
                (_, _, _) => Task.CompletedTask,
                () => open,
                canRequestRecovery: () => open,
                canRequestLoadCompensation: () => open,
                canRequestFaultCargoHandoff: () => open);
        }

        IAppLogger logger = loggerThrows ? new ThrowingLogger() : new RecordingLogger();
        RecordingSink sink = new();
        using DispatcherThread thread = new();
        int unhandled = 0;
        thread.Dispatcher.Invoke(() => thread.Dispatcher.UnhandledException += (_, args) =>
        {
            Interlocked.Increment(ref unhandled);
            args.Handled = true;
        });
        // A heartbeat long enough not to fire before the trigger, unless the heartbeat is what is being made to fail.
        TimeSpan heartbeat = failingEntry == "OnHeartbeatTick" ? Heartbeat : TimeSpan.FromMinutes(5);
        HmiScreenStateMonitor? monitor = thread.Dispatcher.Invoke(() => HmiScreenStateMonitor.TryStart(
            observed,
            () => failingEntry == "Build"
                ? throw new InvalidOperationException("builder")
                : HmiScreenStateBuilder.Build(observed, new ModalDialogRegistry()),
            new ModalDialogRegistry(),
            sink,
            logger,
            TimeProvider.System,
            thread.Dispatcher,
            failingEntry == "OnHeartbeatTick" ? TimeSpan.FromMinutes(5) : Flush,
            heartbeat,
            entry =>
            {
                if (entry == failingEntry)
                {
                    throw new InvalidOperationException(entry);
                }
            }));
        Assert.NotNull(monitor);
        if (failingEntry is not ("OnFlushTick" or "Build" or "OnHeartbeatTick"))
        {
            await WaitAsync(() => sink.Submitted > 0, "the first snapshot");
        }

        open = true;
        _ = thread.Dispatcher.BeginInvoke(() =>
        {
            observed.RefreshWireToGateInputState();
            reference.RefreshWireToGateInputState();
            observed.ReportOperatorRejection("SUBLOT_NOT_IN_WORKLIST");
            reference.ReportOperatorRejection("SUBLOT_NOT_IN_WORKLIST");
        });

        await WaitAsync(() => monitor.IsDisabled, "the snapshot to disable itself");
        // Let anything queued behind the trigger run, so a late escape would still be counted.
        thread.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle, TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref unhandled));
        Assert.Equal([failingEntry == "Build" ? "OnFlushTick" : failingEntry], sink.DisabledEntries);
        Assert.Equal(nameof(InvalidOperationException), sink.DisabledReason);
        Dictionary<string, object?> expected = thread.Dispatcher.Invoke(() => Screen(reference));
        Dictionary<string, object?> actual = thread.Dispatcher.Invoke(() => Screen(observed));
        Assert.Equal(expected, actual);
        Assert.Equal(true, actual[nameof(MainViewModel.CanSubmit)]);
        Assert.Equal(true, actual[nameof(MainViewModel.CanRequestWireToGateRecovery)]);
        Assert.Equal(OnboardCommandRejectionText.DescribeWithCode("SUBLOT_NOT_IN_WORKLIST"), actual[nameof(MainViewModel.Guidance)]);
        if (logger is RecordingLogger recording)
        {
            Assert.Contains(recording.Entries, entry => entry.Source == nameof(HmiScreenStateMonitor));
        }
    }

    /// <summary>
    /// The thread model of research 4.3: the state is built on the dispatcher thread, and every file operation happens
    /// off it, on the writer's task.
    /// </summary>
    [Fact]
    public async Task TheStateIsBuiltOnTheDispatcherAndWrittenOffIt()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        using TemporaryDirectory directory = new();
        using DispatcherThread thread = new();
        int dispatcherThreadId = thread.Dispatcher.Invoke(() => Environment.CurrentManagedThreadId);
        ConcurrentBag<int> buildThreads = [];
        ThreadRecordingFileSystem files = new();
        await using HmiScreenStateFileWriter writer = new(Options(directory.Path), files, TimeProvider.System, new RecordingLogger());
        HmiScreenStateMonitor? monitor = thread.Dispatcher.Invoke(() => HmiScreenStateMonitor.TryStart(
            viewModel,
            () =>
            {
                buildThreads.Add(Environment.CurrentManagedThreadId);
                return HmiScreenStateBuilder.Build(viewModel, new ModalDialogRegistry());
            },
            new ModalDialogRegistry(),
            writer,
            new RecordingLogger(),
            TimeProvider.System,
            thread.Dispatcher,
            Flush,
            Heartbeat));
        Assert.NotNull(monitor);

        await WaitAsync(() => files.Threads.Count >= 3 && buildThreads.Count >= 3, "a few builds and writes");
        thread.Dispatcher.Invoke(monitor.Dispose);

        Assert.All(buildThreads, id => Assert.Equal(dispatcherThreadId, id));
        Assert.All(files.Threads, id => Assert.NotEqual(dispatcherThreadId, id));
        Assert.True(File.Exists(Path.Combine(directory.Path, HmiScreenStateFileWriter.CurrentFileName)));
    }

    /// <summary>
    /// The heartbeat runs on the dispatcher, so a blocked dispatcher stops <c>writtenAt</c>: the signal T3 reads as "the
    /// screen is not responding". It moves again once the dispatcher is free.
    /// </summary>
    [Fact]
    public async Task ABlockedDispatcherStopsWrittenAt()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        using TemporaryDirectory directory = new();
        using DispatcherThread thread = new();
        await using HmiScreenStateFileWriter writer = new(
            Options(directory.Path), HmiScreenStateFileSystem.Instance, TimeProvider.System, new RecordingLogger());
        HmiScreenStateMonitor? monitor = thread.Dispatcher.Invoke(() => HmiScreenStateMonitor.TryStart(
            viewModel,
            () => HmiScreenStateBuilder.Build(viewModel, new ModalDialogRegistry()),
            new ModalDialogRegistry(),
            writer,
            new RecordingLogger(),
            TimeProvider.System,
            thread.Dispatcher,
            Flush,
            Heartbeat));
        Assert.NotNull(monitor);
        string current = Path.Combine(directory.Path, HmiScreenStateFileWriter.CurrentFileName);
        await WaitAsync(() => ReadWrittenAt(current) is not null, "the first write");
        DateTimeOffset beforeBlock = await AdvancedPast(current, ReadWrittenAt(current)!.Value);

        using ManualResetEventSlim release = new();
        using ManualResetEventSlim blocked = new();
        _ = thread.Dispatcher.BeginInvoke(() =>
        {
            blocked.Set();
            release.Wait(Patience);
        });
        Assert.True(blocked.Wait(Patience, TestContext.Current.CancellationToken));
        // Whatever the writer had already been handed is allowed to land; after that nothing more may.
        Assert.True(await writer.DrainAsync());
        DateTimeOffset? whileBlockedFirst = ReadWrittenAt(current);
        await Task.Delay(Heartbeat * 6, TestContext.Current.CancellationToken);
        DateTimeOffset? whileBlockedLast = ReadWrittenAt(current);
        release.Set();
        DateTimeOffset afterRelease = await AdvancedPast(current, whileBlockedLast!.Value);
        thread.Dispatcher.Invoke(monitor.Dispose);

        Assert.True(whileBlockedFirst >= beforeBlock);
        Assert.Equal(whileBlockedFirst, whileBlockedLast);
        Assert.True(afterRelease > whileBlockedLast);
    }

    private static async Task<DateTimeOffset> AdvancedPast(string current, DateTimeOffset than)
    {
        DateTimeOffset? seen = null;
        await WaitAsync(() => (seen = ReadWrittenAt(current)) > than, "writtenAt to advance");
        return seen!.Value;
    }

    internal static DateTimeOffset? ReadWrittenAt(string path)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
                return document.RootElement.GetProperty("writtenAt").GetDateTimeOffset();
            }
            catch (IOException)
            {
                // The writer is replacing it this instant.
                Thread.Sleep(5);
            }
        }

        return null;
    }

    internal static HmiScreenStateOutputOptions Options(string directory) =>
        new(directory, Flush, Heartbeat, 20L * 1024 * 1024, 30);

    /// <summary>What the operator sees and can press, as plain values: every public <c>Can…</c> flag, banner and record.</summary>
    private static Dictionary<string, object?> Screen(MainViewModel viewModel)
    {
        Dictionary<string, object?> screen = typeof(MainViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(bool) && property.Name.StartsWith("Can", StringComparison.Ordinal))
            .ToDictionary(property => property.Name, property => property.GetValue(viewModel));
        screen[nameof(MainViewModel.Guidance)] = viewModel.Guidance;
        screen[nameof(MainViewModel.HasWarning)] = viewModel.HasWarning;
        screen[nameof(MainViewModel.StateText)] = viewModel.StateText;
        screen["Logs.Count"] = viewModel.Logs.Count;
        screen["ClearLogsCommand.CanExecute"] = viewModel.ClearLogsCommand.CanExecute(null);
        return screen;
    }

    internal static async Task WaitAsync(Func<bool> condition, string what)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.Elapsed < Patience, $"timed out waiting for {what}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A logger that throws a non-IO exception, the kind <c>FileAppLogger</c> did not catch before onboard-hmi#312.</summary>
    internal sealed class ThrowingLogger : IAppLogger
    {
        public event EventHandler<LogEntryEventArgs>? EntryWritten;

        public void Write(LogSeverity severity, string source, string message, Exception? exception = null)
        {
            throw new InvalidOperationException("logger");
        }
    }

    internal sealed class RecordingSink : IHmiScreenStateSink
    {
        private readonly ConcurrentQueue<string> _disabled = new();
        private int _submitted;

        public int Submitted => Volatile.Read(ref _submitted);

        public string[] DisabledEntries => [.. _disabled];

        public string? DisabledReason { get; private set; }

        public void Submit(HmiScreenState state, bool heartbeat) => Interlocked.Increment(ref _submitted);

        public void Disable(string entry, string reason, DateTimeOffset at)
        {
            DisabledReason = reason;
            _disabled.Enqueue(entry);
        }
    }

    private sealed class ThreadRecordingFileSystem : IHmiScreenStateFileSystem
    {
        private readonly ConcurrentQueue<int> _threads = new();

        public IReadOnlyCollection<int> Threads => _threads;

        public void CreateDirectory(string path) => Record(() => HmiScreenStateFileSystem.Instance.CreateDirectory(path));

        public void WriteAllBytes(string path, byte[] bytes) => Record(() => HmiScreenStateFileSystem.Instance.WriteAllBytes(path, bytes));

        public void Replace(string source, string destination) => Record(() => HmiScreenStateFileSystem.Instance.Replace(source, destination));

        public void AppendAllBytes(string path, byte[] bytes) => Record(() => HmiScreenStateFileSystem.Instance.AppendAllBytes(path, bytes));

        public long LengthOf(string path)
        {
            _threads.Enqueue(Environment.CurrentManagedThreadId);
            return HmiScreenStateFileSystem.Instance.LengthOf(path);
        }

        public IReadOnlyList<string> FileNamesIn(string directory)
        {
            _threads.Enqueue(Environment.CurrentManagedThreadId);
            return HmiScreenStateFileSystem.Instance.FileNamesIn(directory);
        }

        public void Delete(string path) => Record(() => HmiScreenStateFileSystem.Instance.Delete(path));

        private void Record(Action action)
        {
            _threads.Enqueue(Environment.CurrentManagedThreadId);
            action();
        }
    }

    internal sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hmi315-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A writer still finishing; the temp directory is cleaned by the OS eventually.
            }
        }
    }
}
