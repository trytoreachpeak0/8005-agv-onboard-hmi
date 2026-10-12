using System.Collections.ObjectModel;
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
