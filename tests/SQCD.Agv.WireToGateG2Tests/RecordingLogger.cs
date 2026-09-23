using SQCD.Agv.Core;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// An <see cref="IAppLogger"/> that keeps what it was told and tells no one.
/// </summary>
/// <remarks>
/// The G2 suite needs a logger only because the services under test require one; nothing here
/// asserts on log text, and writing to the console would bury the assertion failures that matter.
/// Entries are kept rather than dropped so a test that does want to see why a guard fired can,
/// without a second logger type existing for that one case.
/// </remarks>
internal sealed class RecordingLogger : IAppLogger
{
    private readonly List<(LogSeverity Severity, string Source, string Message)> _entries = [];
    private readonly List<(string Message, Exception Exception)> _exceptions = [];

    public event EventHandler<LogEntryEventArgs>? EntryWritten;

    /// <summary>
    /// Each entry written with an exception, with that exception: for a test that has to tell which failure a
    /// guard caught, not only that one was logged.
    /// </summary>
    public IReadOnlyList<(string Message, Exception Exception)> Exceptions
    {
        get
        {
            lock (_entries)
            {
                return _exceptions.ToArray();
            }
        }
    }

    public IReadOnlyList<(LogSeverity Severity, string Source, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Write(
        LogSeverity severity,
        string source,
        string message,
        Exception? exception = null)
    {
        lock (_entries)
        {
            _entries.Add((severity, source, message));
            if (exception is not null)
            {
                _exceptions.Add((message, exception));
            }
        }
    }
}
