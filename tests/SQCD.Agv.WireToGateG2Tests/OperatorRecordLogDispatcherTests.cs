using System.Windows.Threading;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.OperatorRecordOutput;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;
using static SQCD.Agv.WireToGateG2Tests.HmiScreenStateMonitorDispatcherTests;
using static SQCD.Agv.WireToGateG2Tests.MultiDemandViewModelTests;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// Research 4.3 item 1 for the operator record log (onboard-hmi#317), on a real WPF dispatcher: a record log whose
/// logger or handler throws never raises <see cref="Dispatcher.UnhandledException"/> -- which the application turns into
/// <c>UNHANDLED_UI_ERROR</c> and a latched vehicle -- and the view-model method that added the line finishes exactly as
/// it does without the record log.
/// </summary>
/// <remarks>
/// <para>
/// G2 has no dispatcher (<c>RunOnUiThread</c> runs on the caller there), so the handler is driven here on an STA thread
/// running <see cref="Dispatcher.Run"/>, and the line is added through <c>MainViewModel.ReportOperatorRejection</c>,
/// whose banner write comes after <c>Logs.Add</c>: an exception out of the handler would leave the banner unwritten.
/// </para>
/// <para>No <c>IntegrationSlice</c>/<c>ProtocolVector</c> trait: a diagnostic output, it proves no protocol vector.</para>
/// </remarks>
public sealed class OperatorRecordLogDispatcherTests
{
    [Theory]
    [InlineData("logger")]
    [InlineData("OnLogsCollectionChanged")]
    public async Task AFailureNeverReachesTheDispatcherAndTheViewModelFinishesItsWork(string failing)
    {
        await using OnboardController observedController = Controller();
        await using OnboardController referenceController = Controller();
        MainViewModel observed = await ViewModel(observedController);
        MainViewModel reference = await ViewModel(referenceController);

        using DispatcherThread thread = new();
        int unhandled = 0;
        thread.Dispatcher.Invoke(() => thread.Dispatcher.UnhandledException += (_, args) =>
        {
            Interlocked.Increment(ref unhandled);
            args.Handled = true;
        });

        IAppLogger logger = failing == "logger" ? new ThrowingLogger() : new RecordingLogger();
        OperatorRecordLog? log = thread.Dispatcher.Invoke(() => OperatorRecordLog.TryStart(
            new LogSettings { OperatorRecords = true },
            observed.Logs,
            logger,
            entry =>
            {
                if (entry == failing)
                {
                    throw new InvalidOperationException(entry);
                }
            }));
        Assert.NotNull(log);

        // BeginInvoke, not Invoke: an exception escaping the queued work goes to UnhandledException, not to this thread.
        DispatcherOperation added = thread.Dispatcher.BeginInvoke(() => observed.ReportOperatorRejection("SUBLOT_NOT_IN_WORKLIST"));
        await added.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        reference.ReportOperatorRejection("SUBLOT_NOT_IN_WORKLIST");

        Assert.Equal(0, Volatile.Read(ref unhandled));
        Assert.True(log.IsDisabled);
        Assert.Equal(reference.Guidance, thread.Dispatcher.Invoke(() => observed.Guidance));
        Assert.Equal(reference.HasWarning, thread.Dispatcher.Invoke(() => observed.HasWarning));
        Assert.Equal(reference.Logs.Count, thread.Dispatcher.Invoke(() => observed.Logs.Count));
        Assert.Equal(reference.ClearLogsCommand.CanExecute(null), thread.Dispatcher.Invoke(() => observed.ClearLogsCommand.CanExecute(null)));
        thread.Dispatcher.Invoke(log.Dispose);
    }

    /// <summary>The working case on the same dispatcher: the record is written on the dispatcher thread, inside the add.</summary>
    [Fact]
    public async Task TheRecordIsWrittenOnTheDispatcherThreadInsideTheAdd()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        using DispatcherThread thread = new();
        ThreadRecordingLogger logger = new();

        OperatorRecordLog? log = thread.Dispatcher.Invoke(() => OperatorRecordLog.TryStart(
            new LogSettings { OperatorRecords = true },
            viewModel.Logs, // logs-reference: the record log subscribes to its CollectionChanged
            logger));
        Assert.NotNull(log);

        await thread.Dispatcher.BeginInvoke(() => viewModel.ReportOperatorRejection("SUBLOT_NOT_IN_WORKLIST"))
            .Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(thread.Dispatcher.Thread.ManagedThreadId, Assert.Single(logger.Threads));
        thread.Dispatcher.Invoke(log.Dispose);
    }

    private sealed class ThreadRecordingLogger : IAppLogger
    {
        private readonly List<int> _threads = [];

        public event EventHandler<LogEntryEventArgs>? EntryWritten;

        public IReadOnlyList<int> Threads
        {
            get
            {
                lock (_threads)
                {
                    return [.. _threads];
                }
            }
        }

        public void Write(LogSeverity severity, string source, string message, Exception? exception = null)
        {
            if (source == nameof(OperatorRecordLog))
            {
                lock (_threads)
                {
                    _threads.Add(Environment.CurrentManagedThreadId);
                }
            }
        }
    }
}
