using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.OperatorRecordOutput;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;
using static SQCD.Agv.WireToGateG2Tests.HmiScreenStateMonitorDispatcherTests;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The operator record log on its own collection (onboard-hmi#317, T4): what it writes for an added line and a clear,
/// what it does not write, the switch, and that its failures stay inside it.
/// </summary>
/// <remarks>
/// These run on the test thread with no dispatcher; that the same failures never reach a real dispatcher, and that the
/// view-model method around <c>Logs.Add</c> completes, is <see cref="OperatorRecordLogDispatcherTests"/>. No
/// <c>IntegrationSlice</c>/<c>ProtocolVector</c> trait: a diagnostic output, it proves no protocol vector.
/// </remarks>
public sealed class OperatorRecordLogTests
{
    private static readonly LogSettings On = new() { OperatorRecords = true };

    [Fact]
    public void SwitchedOffNothingIsSubscribedOrWritten()
    {
        ObservableCollection<LogLineViewModel> logs = [];
        RecordingLogger logger = new();

        Assert.Null(OperatorRecordLog.TryStart(new LogSettings(), logs, logger));
        logs.Add(Line("一条"));
        logs.Clear();

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void EachAddedLineIsOneStructuredLineInTheExecutionPhase()
    {
        ObservableCollection<LogLineViewModel> logs = [];
        RecordingLogger logger = new();
        using OperatorRecordLog? log = OperatorRecordLog.TryStart(On, logs, logger);
        Assert.NotNull(log);

        LogLineViewModel withAttempt = new(
            new DateTimeOffset(2026, 10, 12, 9, 30, 15, TimeSpan.FromHours(8)),
            OperatorRecordKind.Warning,
            "第一行\t带制表符\n第二行",
            OperatorRecordSource.OperatorEvent,
            "attempt-17",
            "SUBLOT_REJECTED");
        LogLineViewModel plain = Line("修正窗口已关闭");
        logs.Add(withAttempt);
        logs.Add(plain);

        (LogSeverity Severity, string Source, string Message)[] entries = [.. logger.Entries];
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry =>
        {
            Assert.Equal(LogSeverity.Information, entry.Severity);
            Assert.Equal(nameof(OperatorRecordLog), entry.Source);
            Assert.StartsWith(OperatorRecordLog.Prefix, entry.Message, StringComparison.Ordinal);
            // One line in the logger's tab-separated file.
            Assert.DoesNotContain('\n', entry.Message);
            Assert.DoesNotContain('\r', entry.Message);
            Assert.DoesNotContain('\t', entry.Message);
        });
        Assert.All(logger.Categories, category => Assert.Equal(OnboardTechnicalLogCategory.ExecutionPhase, category.Category));

        using JsonDocument first = Parse(entries[0].Message);
        Assert.Equal("APPENDED", first.RootElement.GetProperty("action").GetString());
        Assert.Equal("2026-10-12T09:30:15.0000000+08:00", first.RootElement.GetProperty("at").GetString());
        Assert.Equal("OperatorEvent", first.RootElement.GetProperty("source").GetString());
        Assert.Equal("Warning", first.RootElement.GetProperty("kind").GetString());
        Assert.Equal("SUBLOT_REJECTED", first.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal("attempt-17", first.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal(withAttempt.Message, first.RootElement.GetProperty("message").GetString());

        using JsonDocument second = Parse(entries[1].Message);
        Assert.Equal("Unknown", second.RootElement.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("attemptId").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("eventKind").ValueKind);
        Assert.Equal("修正窗口已关闭", second.RootElement.GetProperty("message").GetString());
    }

    /// <summary>
    /// The trim to the last 300 lines removes from the front; the operator neither does nor sees it, so nothing is
    /// written. A clear is written once, with how many lines were on screen when it was pressed.
    /// </summary>
    [Fact]
    public void ATrimWritesNothingAndAClearWritesHowManyLinesItTook()
    {
        ObservableCollection<LogLineViewModel> logs = [Line("启动前已有")];
        RecordingLogger logger = new();
        using OperatorRecordLog? log = OperatorRecordLog.TryStart(On, logs, logger);

        for (int index = 0; index < 4; index++)
        {
            logs.Add(Line($"第 {index} 条"));
        }

        logs.RemoveAt(0);
        logs.RemoveAt(0);
        Assert.Equal(4, logger.Entries.Count);

        logs.Clear();

        Assert.Equal(5, logger.Entries.Count);
        using JsonDocument cleared = Parse(logger.Entries[^1].Message);
        Assert.Equal("CLEARED", cleared.RootElement.GetProperty("action").GetString());
        Assert.Equal(3, cleared.RootElement.GetProperty("cleared").GetInt32());

        logs.Add(Line("清空之后"));
        logs.Clear();
        using JsonDocument again = Parse(logger.Entries[^1].Message);
        Assert.Equal(1, again.RootElement.GetProperty("cleared").GetInt32());
    }

    /// <summary>A replaced line puts a new line on screen, so it is written like an added one (review L2; nothing replaces today).</summary>
    [Fact]
    public void AReplacedLineIsWrittenAsTheNewLine()
    {
        ObservableCollection<LogLineViewModel> logs = [Line("原来那条")];
        RecordingLogger logger = new();
        using OperatorRecordLog? log = OperatorRecordLog.TryStart(On, logs, logger);

        logs[0] = Line("换上的那条");

        using JsonDocument line = Parse(Assert.Single(logger.Entries).Message);
        Assert.Equal("APPENDED", line.RootElement.GetProperty("action").GetString());
        Assert.Equal("换上的那条", line.RootElement.GetProperty("message").GetString());
    }

    /// <summary>
    /// Research 4.3 item 1: a logger that throws something other than an IO failure does not get out of
    /// <c>Logs.Add</c>; the record log turns itself off and does not try again.
    /// </summary>
    [Fact]
    public void AThrowingLoggerNeverEscapesTheAddAndTurnsTheLogOff()
    {
        ObservableCollection<LogLineViewModel> logs = [];
        CountingThrowingLogger logger = new();
        using OperatorRecordLog? log = OperatorRecordLog.TryStart(On, logs, logger);
        Assert.NotNull(log);

        logs.Add(Line("第一条"));
        logs.Add(Line("第二条"));
        logs.Clear();

        Assert.True(log.IsDisabled);
        // The record write and the one "turned off" write, both thrown; nothing after.
        Assert.Equal(2, logger.Calls);
        Assert.Empty(logs);
    }

    [Fact]
    public void AFailingHandlerTurnsTheLogOffAndSaysWhereAsALocalFault()
    {
        ObservableCollection<LogLineViewModel> logs = [];
        RecordingLogger logger = new();
        using OperatorRecordLog? log = OperatorRecordLog.TryStart(On, logs, logger, entry =>
        {
            if (entry == "OnLogsCollectionChanged")
            {
                throw new InvalidOperationException("probe");
            }
        });
        Assert.NotNull(log);

        logs.Add(Line("第一条"));
        logs.Add(Line("第二条"));

        Assert.True(log.IsDisabled);
        (LogSeverity severity, string source, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogSeverity.Warning, severity);
        Assert.Equal(nameof(OperatorRecordLog), source);
        Assert.Contains("OnLogsCollectionChanged", message, StringComparison.Ordinal);
        Assert.Equal(OnboardTechnicalLogCategory.LocalFault, Assert.Single(logger.Categories).Category);
    }

    [Fact]
    public void AFailingStartReturnsNullAndSaysSo()
    {
        ObservableCollection<LogLineViewModel> logs = [];
        RecordingLogger logger = new();

        OperatorRecordLog? log = OperatorRecordLog.TryStart(On, logs, logger, entry =>
        {
            if (entry == "TryStart")
            {
                throw new InvalidOperationException("probe");
            }
        });
        logs.Add(Line("没有人听"));

        Assert.Null(log);
        (_, _, string message) = Assert.Single(logger.Entries);
        Assert.Contains("TryStart", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailingStartWithAThrowingLoggerStillReturns()
    {
        Assert.Null(OperatorRecordLog.TryStart(On, [], new ThrowingLogger(), _ => throw new InvalidOperationException("probe")));
    }

    [Fact]
    public void DisposedNothingMoreIsWritten()
    {
        ObservableCollection<LogLineViewModel> logs = [];
        RecordingLogger logger = new();
        OperatorRecordLog? log = OperatorRecordLog.TryStart(On, logs, logger);
        Assert.NotNull(log);

        log.Dispose();
        logs.Add(Line("停了之后"));

        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// The view model keeps the last 300 lines (<c>MainViewModel.TrimLogs</c>, called from <c>AppendLogLine</c>); every
    /// line still reaches the technical log, and the trim itself writes nothing. Nothing pinned the 300 before
    /// onboard-hmi#317 moved the trim into the helper.
    /// </summary>
    [Fact]
    public async Task TheScreenKeepsTheLast300LinesAndTheLogKeepsEveryOne()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);
        RecordingLogger logger = new();
        using OperatorRecordLog? log = OperatorRecordLog.TryStart(On, viewModel.Logs, logger);
        Assert.NotNull(log);

        for (int index = 0; index < 305; index++)
        {
            viewModel.ReportOperatorRejection("SUBLOT_NOT_IN_WORKLIST");
        }

        Assert.Equal(300, viewModel.Logs.Count);
        Assert.Equal(305, logger.Entries.Count(entry => entry.Source == nameof(OperatorRecordLog)));
        Assert.All(logger.Entries, entry => Assert.Contains("\"APPENDED\"", entry.Message, StringComparison.Ordinal));
    }

    /// <summary>
    /// The operator-event write site (<c>MainViewModel.ApplyWireToGateOperatorEvent</c>) names its source, the event's own
    /// kind, and the slot operation attempt the event carries (T4 acceptance, first item; review S1).
    /// </summary>
    [Fact]
    public async Task AnOperatorEventLineCarriesItsAttemptAndKind()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);
        RecordingLogger logger = new();
        using OperatorRecordLog? log = OperatorRecordLog.TryStart(On, viewModel.Logs, logger);
        DateTimeOffset now = DateTimeOffset.Now;

        viewModel.ApplyWireToGateOperatorEvent(new WireToGateOperatorEvent(
            now,
            "OPERATION_PROGRESS",
            "1号仓开锁中。",
            new WireToGateHmiOperationSnapshot("attempt-T4-9", OperationType.Load, [1], WireToGateHmiOperationStage.Unlocking, "开锁中", now)));

        using JsonDocument line = Parse(Assert.Single(logger.Entries).Message);
        Assert.Equal("OperatorEvent", line.RootElement.GetProperty("source").GetString());
        Assert.Equal("OPERATION_PROGRESS", line.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal("attempt-T4-9", line.RootElement.GetProperty("attemptId").GetString());
    }

    /// <summary>
    /// The controller-record write site (<c>MainViewModel.AppendOperatorRecord</c>) names its source and puts the
    /// controller's <c>OperationId</c> in <c>attemptId</c> -- the legacy controller's operation number, not a W2G slot
    /// operation attempt; the two share the field (review S1). The snapshot reaches the view model through its
    /// <c>StateChanged</c> handler, invoked here by reflection because a disabled rule gateway never lets the real
    /// controller reach <c>Operating</c>.
    /// </summary>
    [Fact]
    public async Task AControllerRecordLineCarriesTheControllersOperationId()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);
        RecordingLogger logger = new();
        using OperatorRecordLog? log = OperatorRecordLog.TryStart(On, viewModel.Logs, logger);
        DateTimeOffset now = DateTimeOffset.Now;
        OnboardSnapshot operating = new(
            OnboardState.Operating,
            RuleConnected: true,
            IoConnected: true,
            new VisitContext("VISIT-T4", "ST-T4", "T4 站", true, now.AddMinutes(10)),
            IoSnapshot.Unknown(now),
            new ActiveOperation("VISIT-T4", "OP-T4-7", "TASK-T4", "SUBLOT-T4", 0, OperationType.Load, OperationStage.WritingUnlock, now),
            DeparturePermitted: false,
            "1号仓开锁中",
            ErrorCode: null,
            now);

        MethodInfo handler = typeof(MainViewModel).GetMethod("OnStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainViewModel.OnStateChanged not found.");
        handler.Invoke(viewModel, [controller, new ValueChangedEventArgs<OnboardSnapshot>(operating)]);

        JsonDocument[] lines = [.. logger.Entries.Select(entry => Parse(entry.Message))];
        try
        {
            JsonDocument record = Assert.Single(lines, line => line.RootElement.GetProperty("source").GetString() == "ControllerRecord");
            Assert.Equal("OP-T4-7", record.RootElement.GetProperty("attemptId").GetString());
            Assert.Equal(JsonValueKind.Null, record.RootElement.GetProperty("eventKind").ValueKind);
        }
        finally
        {
            foreach (JsonDocument line in lines)
            {
                line.Dispose();
            }
        }
    }

    private static LogLineViewModel Line(string message) =>
        new(DateTimeOffset.Now, OperatorRecordKind.System, message);

    private static JsonDocument Parse(string message) =>
        JsonDocument.Parse(message[OperatorRecordLog.Prefix.Length..]);

    private sealed class CountingThrowingLogger : IAppLogger
    {
        private int _calls;

        public event EventHandler<LogEntryEventArgs>? EntryWritten;

        public int Calls => Volatile.Read(ref _calls);

        public void Write(LogSeverity severity, string source, string message, Exception? exception = null)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("logger");
        }
    }
}
