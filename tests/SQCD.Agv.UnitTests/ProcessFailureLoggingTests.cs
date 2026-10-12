using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 调度线程以外的未处理失败进技术日志的本地异常，且只记日志、不改变进程的生死（REQ-0272，onboard-hmi#312）。
/// </summary>
/// <remarks>
/// 「不改变生死」在这里有两个可检查的形状：后台任务的未观察异常不被标成已观察（标了就等于替运行时决定吞掉它），
/// 处理器自己即使日志器抛异常也不往外抛（往外抛会在终结器线程上把进程带走）。
/// 不挂 <c>IntegrationSlice</c> trait，理由同 <see cref="OnboardAlarmSnapshotTests"/>。
/// </remarks>
public sealed class ProcessFailureLoggingTests
{
    [Fact]
    public void AnUnhandledExceptionIsLoggedAsALocalFault()
    {
        CategoryRecordingLogger logger = new();
        ProcessFailureLogging logging = new(logger);

        logging.OnUnhandledException(this, new UnhandledExceptionEventArgs(new InvalidOperationException("boom"), isTerminating: true));

        (LogSeverity severity, OnboardTechnicalLogCategory category, string source, string message, Exception? exception) =
            Assert.Single(logger.Entries);
        Assert.Equal(LogSeverity.Error, severity);
        Assert.Equal(OnboardTechnicalLogCategory.LocalFault, category);
        Assert.Equal(nameof(ProcessFailureLogging), source);
        Assert.Contains("进程即将退出", message, StringComparison.Ordinal);
        Assert.Equal("boom", exception?.Message);
    }

    [Fact]
    public void AnUnobservedTaskExceptionIsLoggedAndLeftUnobserved()
    {
        CategoryRecordingLogger logger = new();
        ProcessFailureLogging logging = new(logger);
        UnobservedTaskExceptionEventArgs args = new(new AggregateException(new TimeoutException("late")));

        logging.OnUnobservedTaskException(this, args);

        (LogSeverity severity, OnboardTechnicalLogCategory category, _, _, Exception? exception) = Assert.Single(logger.Entries);
        Assert.Equal(LogSeverity.Error, severity);
        Assert.Equal(OnboardTechnicalLogCategory.LocalFault, category);
        Assert.IsType<AggregateException>(exception);
        Assert.False(args.Observed);
    }

    [Fact]
    public void AFailingLoggerDoesNotEscapeEitherHandler()
    {
        ProcessFailureLogging logging = new(new ThrowingLogger());

        Assert.Null(Record.Exception(() => logging.OnUnhandledException(
            this,
            new UnhandledExceptionEventArgs(new InvalidOperationException("boom"), isTerminating: false))));
        Assert.Null(Record.Exception(() => logging.OnUnobservedTaskException(
            this,
            new UnobservedTaskExceptionEventArgs(new AggregateException()))));
    }

    private sealed class CategoryRecordingLogger : IAppLogger
    {
        public event EventHandler<LogEntryEventArgs>? EntryWritten;

        public List<(LogSeverity Severity, OnboardTechnicalLogCategory Category, string Source, string Message, Exception? Exception)> Entries { get; } = [];

        public void Write(LogSeverity severity, string source, string message, Exception? exception = null) =>
            Write(severity, OnboardTechnicalLogCategories.DefaultFor(source, severity), source, message, exception);

        public void Write(
            LogSeverity severity,
            OnboardTechnicalLogCategory category,
            string source,
            string message,
            Exception? exception = null)
        {
            Entries.Add((severity, category, source, message, exception));
            EntryWritten?.Invoke(this, new LogEntryEventArgs(new LogEntry(DateTimeOffset.Now, severity, source, message)));
        }
    }

    private sealed class ThrowingLogger : IAppLogger
    {
        public event EventHandler<LogEntryEventArgs>? EntryWritten
        {
            add { }
            remove { }
        }

        public void Write(LogSeverity severity, string source, string message, Exception? exception = null) =>
            throw new InvalidOperationException("logger failed");
    }
}
