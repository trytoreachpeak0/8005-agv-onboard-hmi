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
    private readonly List<(OnboardTechnicalLogCategory Category, string Message)> _categories = [];

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

    /// <summary>
    /// The technical-log category each entry was filed under, explicit or the source's default — what
    /// <c>FileAppLogger</c> writes into its third column (REQ-0272, onboard-hmi#312). Without the explicit overload
    /// here the interface's default implementation would drop the category before a test could see it.
    /// </summary>
    public IReadOnlyList<(OnboardTechnicalLogCategory Category, string Message)> Categories
    {
        get
        {
            lock (_entries)
            {
                return _categories.ToArray();
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

    /// <summary>
    /// The last <paramref name="count"/> entries with the time each was written, oldest first: for the message of a wait
    /// that ran out, which has to show whether the vehicle was still doing anything (8005-agv-onboard-hmi#383).
    /// </summary>
    public IReadOnlyList<string> Tail(int count)
    {
        lock (_entries)
        {
            int start = Math.Max(0, _entries.Count - count);
            return
            [
                .. Enumerable.Range(start, _entries.Count - start).Select(index =>
                    $"{_writtenAt[index]:HH:mm:ss.fff} {_entries[index].Severity} {_entries[index].Source}: {_entries[index].Message}")
            ];
        }
    }

    private readonly List<DateTimeOffset> _writtenAt = [];

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
        Exception? exception = null) =>
        Write(severity, OnboardTechnicalLogCategories.DefaultFor(source, severity), source, message, exception);

    public void Write(
        LogSeverity severity,
        OnboardTechnicalLogCategory category,
        string source,
        string message,
        Exception? exception = null)
    {
        lock (_entries)
        {
            _entries.Add((severity, source, message));
            _writtenAt.Add(DateTimeOffset.UtcNow);
            _categories.Add((category, message));
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
