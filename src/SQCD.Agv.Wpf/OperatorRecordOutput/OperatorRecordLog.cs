using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf.OperatorRecordOutput;

/// <summary>
/// Writes every line added to the on-screen operator record (「操作记录」, <c>MainViewModel.Logs</c>), and every time the
/// operator clears it, as one structured line in the technical log (T4 of <c>docs/W2G_HMI_OBSERVABILITY_RESEARCH.md</c>,
/// onboard-hmi#317).
/// </summary>
/// <remarks>
/// <para>
/// <b>At the <c>Logs</c> layer, not at the sources.</b> It listens to the collection itself, so all four write sites
/// and any later one are covered by the same code; what each line says about its source and attempt comes from the
/// <see cref="LogLineViewModel"/> its write site built. Removals are not written: the only remover is the trim to the
/// last 300 lines, which the operator does not do and does not see as an action. A reset is the operator pressing
/// 「清空记录」 (<c>ClearLogsCommand</c>, the only <c>Logs.Clear()</c>), written with how many lines it took away.
/// </para>
/// <para>
/// <b>Line shape.</b> Message column <c>操作记录 {json}</c>, one JSON object with <c>action</c> (<c>APPENDED</c> or
/// <c>CLEARED</c>), and for an appended line <c>at</c>, <c>source</c>, <c>kind</c>, <c>eventKind</c>, <c>attemptId</c>,
/// <c>message</c>; for a clear, <c>cleared</c>. JSON escapes the tabs and line breaks a message could carry, so the line
/// stays one line with the logger's tab-separated columns intact.
/// </para>
/// <para>
/// <b>Content boundary.</b> The message is written as shown on screen. That differs from the screen-state snapshot,
/// which leaves operator record messages out because several name a sublot; the technical log already carries sublots
/// and operator ids in its own lines, and the coordinator decided on 2026-10-12 (onboard-hmi#317, option A) that this
/// line may too. It reads nothing but the <see cref="LogLineViewModel"/> -- no input box, no credential -- which
/// <c>OperatorRecordLogArchitectureTests</c> holds.
/// </para>
/// <para>
/// <b>Isolation (research 4.3, item 1).</b> The handler runs inside <c>Logs.Add</c>, inside a view-model method, on the
/// WPF dispatcher. An exception out of it would stop that method halfway and reach the dispatcher as
/// <c>UNHANDLED_UI_ERROR</c>, which latches the vehicle. So each entry point -- <see cref="OnLogsCollectionChanged"/>,
/// <see cref="TryStart"/>, <see cref="Dispose"/> -- is wrapped whole in <c>try/catch (Exception)</c>; on failure the
/// record log unsubscribes for the rest of the process and says so once, and that log write is wrapped again because a
/// logger can throw. The write itself is synchronous: operator records arrive at the rate of operator events, and the
/// view model already logs on the dispatcher (<c>LogOperatorVisibleError</c>).
/// </para>
/// </remarks>
internal sealed class OperatorRecordLog : IDisposable
{
    /// <summary>The fixed start of the message column, so the lines can be found with one search.</summary>
    internal const string Prefix = "操作记录 ";

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ObservableCollection<LogLineViewModel> _logs;
    private readonly IAppLogger _logger;
    private readonly Action<string>? _entryProbe;
    private readonly object _gate = new();
    private int _count;
    private int _disabled;

    private OperatorRecordLog(ObservableCollection<LogLineViewModel> logs, IAppLogger logger, Action<string>? entryProbe)
    {
        _logs = logs;
        _logger = logger;
        _entryProbe = entryProbe;
    }

    internal bool IsDisabled => Volatile.Read(ref _disabled) != 0;

    /// <summary>
    /// Starts it if <see cref="LogSettings.OperatorRecords"/> is on; null when it is off or could not start. Off means
    /// nothing is subscribed and nothing is written.
    /// </summary>
    /// <param name="entryProbe">Test seam: called first in every entry point with its name, so a test can make it throw.</param>
    internal static OperatorRecordLog? TryStart(
        LogSettings logging,
        ObservableCollection<LogLineViewModel> logs,
        IAppLogger logger,
        Action<string>? entryProbe = null)
    {
        OperatorRecordLog? started = null;
        try
        {
            if (!logging.OperatorRecords)
            {
                return null;
            }

            entryProbe?.Invoke(nameof(TryStart));
            started = new OperatorRecordLog(logs, logger, entryProbe);
            lock (started._gate)
            {
                started._count = logs.Count;
                logs.CollectionChanged += started.OnLogsCollectionChanged;
            }

            return started;
        }
        catch (Exception failure)
        {
            if (started is not null)
            {
                started.Disable(nameof(TryStart), failure);
            }
            else
            {
                try
                {
                    logger.Write(
                        LogSeverity.Warning,
                        OnboardTechnicalLogCategory.LocalFault,
                        nameof(OperatorRecordLog),
                        $"操作记录落盘停用（{nameof(TryStart)}），本次运行不再写：{failure.GetType().Name}: {failure.Message}");
                }
                catch (Exception)
                {
                    // The logger can throw (research 4.3, item 1); not starting is the whole answer anyway.
                }
            }

            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            Interlocked.Exchange(ref _disabled, 1);
            _logs.CollectionChanged -= OnLogsCollectionChanged;
        }
        catch (Exception)
        {
            // Shutting down: nothing may escape onto the dispatcher.
        }
    }

    /// <summary>The JSON written for one appended line; internal so the tests read the same shape the log gets.</summary>
    internal static string Appended(LogLineViewModel line) =>
        Prefix + JsonSerializer.Serialize(
            new AppendedRecord(
                "APPENDED",
                line.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                line.Source.ToString(),
                line.Kind.ToString(),
                line.EventKind,
                line.AttemptId,
                line.Message),
            Json);

    internal static string Cleared(int count) =>
        Prefix + JsonSerializer.Serialize(new ClearedRecord("CLEARED", count), Json);

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        try
        {
            if (IsDisabled)
            {
                return;
            }

            _entryProbe?.Invoke(nameof(OnLogsCollectionChanged));
            List<string> lines = [];
            lock (_gate)
            {
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        foreach (LogLineViewModel line in e.NewItems?.OfType<LogLineViewModel>() ?? [])
                        {
                            lines.Add(Appended(line));
                        }

                        break;
                    case NotifyCollectionChangedAction.Reset:
                        lines.Add(Cleared(_count));
                        break;
                }

                _count = _logs.Count;
            }

            foreach (string line in lines)
            {
                _logger.Write(LogSeverity.Information, nameof(OperatorRecordLog), line);
            }
        }
        catch (Exception failure)
        {
            Disable(nameof(OnLogsCollectionChanged), failure);
        }
    }

    private void Disable(string where, Exception failure)
    {
        if (Interlocked.Exchange(ref _disabled, 1) != 0)
        {
            return;
        }

        try
        {
            _logs.CollectionChanged -= OnLogsCollectionChanged;
        }
        catch (Exception)
        {
            // Already disabled above; a handler left subscribed returns at once.
        }

        try
        {
            _logger.Write(
                LogSeverity.Warning,
                OnboardTechnicalLogCategory.LocalFault,
                nameof(OperatorRecordLog),
                $"操作记录落盘停用（{where}），本次运行不再写：{failure.GetType().Name}: {failure.Message}");
        }
        catch (Exception)
        {
            // The logger can throw (research 4.3, item 1); the record log is off either way.
        }
    }

    private sealed record AppendedRecord(
        string Action,
        string At,
        string Source,
        string Kind,
        string? EventKind,
        string? AttemptId,
        string Message);

    private sealed record ClearedRecord(
        string Action,
        int Cleared);
}
