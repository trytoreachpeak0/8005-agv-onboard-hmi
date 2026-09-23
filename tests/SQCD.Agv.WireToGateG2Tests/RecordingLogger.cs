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

    private string? _holdPrefix;
    private readonly TaskCompletionSource _holdReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _holdEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Parks the next writer of an entry starting with <paramref name="prefix"/> right after the entry is kept, until
    /// <see cref="ReleaseHold"/>: a service that logs and then acts is held between the two, so a test can change the
    /// world in that gap instead of hoping a schedule opens it. One hold per logger.
    /// </summary>
    public void HoldNextEntryStartingWith(string prefix) => Volatile.Write(ref _holdPrefix, prefix);

    /// <summary>Completes once a writer is parked by <see cref="HoldNextEntryStartingWith"/>.</summary>
    public Task HoldEntered => _holdEntered.Task;

    public void ReleaseHold() => _holdReleased.TrySetResult();

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

        string? prefix = Volatile.Read(ref _holdPrefix);
        if (prefix is not null
            && message.StartsWith(prefix, StringComparison.Ordinal)
            && Interlocked.CompareExchange(ref _holdPrefix, null, prefix) == prefix)
        {
            _holdEntered.TrySetResult();
            _holdReleased.Task.Wait();
        }
    }
}
