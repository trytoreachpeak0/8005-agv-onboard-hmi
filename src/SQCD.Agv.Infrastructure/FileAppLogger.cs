using System.Diagnostics;
using System.Text;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

public sealed class FileAppLogger : IAppLogger
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly bool _writeToConsole;

    public FileAppLogger(LogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, settings.Directory));
        _writeToConsole = settings.WriteToConsole;
    }

    public event EventHandler<LogEntryEventArgs>? EntryWritten;

    public void Write(LogSeverity severity, string source, string message, Exception? exception = null)
    {
        string completeMessage = exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}";
        LogEntry entry = new(DateTimeOffset.Now, severity, source, completeMessage);
        string line = $"{entry.Timestamp:O}\t{entry.Severity}\t{entry.Source}\t{entry.Message}{Environment.NewLine}";

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                string path = Path.Combine(_directory, $"agv-{entry.Timestamp:yyyyMMdd}.log");
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch (IOException ioException)
        {
            Debug.WriteLine(ioException);
        }
        catch (UnauthorizedAccessException accessException)
        {
            Debug.WriteLine(accessException);
        }

        if (_writeToConsole)
        {
            Console.Write(line);
        }

        EntryWritten?.Invoke(this, new LogEntryEventArgs(entry));
    }
}
