using System.Globalization;
using System.IO;
using System.Threading.Channels;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ScreenState;

namespace SQCD.Agv.Wpf.ScreenStateOutput;

/// <summary>What the dispatcher side hands the writer. Never blocks and never throws.</summary>
internal interface IHmiScreenStateSink
{
    /// <summary>A state built on the dispatcher; <paramref name="heartbeat"/> rewrites the current file even if unchanged.</summary>
    void Submit(HmiScreenState state, bool heartbeat);

    /// <summary>The snapshot stopped itself: write one <c>DISABLED</c> document, then nothing more.</summary>
    void Disable(string entry, string reason, DateTimeOffset at);
}

/// <summary>
/// Writes <c>hmi-state.json</c> and the daily timeline <c>hmi-state-yyyyMMdd.ndjson</c> (research section 4.1,
/// onboard-hmi#315) on one background task, one write at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread model.</b> <see cref="Submit"/> and <see cref="Disable"/> are called on the dispatcher thread and only post
/// to a bounded channel. Serialising, comparing and every file operation happen on the writer's own task, never on the
/// dispatcher. When the writer falls behind the oldest pending state is dropped: each state is complete, and the content
/// is compared with what was last written, so a drop loses an intermediate screen, never the latest one.
/// </para>
/// <para>
/// <b>Current file.</b> Written to <c>hmi-state.json.tmp</c> and moved over <c>hmi-state.json</c> in one step, so a reader
/// always gets a whole document. A reader holding the file open can make the move fail; it is retried briefly, and
/// otherwise the next write (at the latest the next heartbeat) tries again.
/// </para>
/// <para>
/// <b>Timeline.</b> One line per content change, never per heartbeat. One file per local day, capped in size: past the
/// cap that day's file stops growing and one log line says so. Files older than the retention are deleted by the writer
/// itself, matching <c>hmi-state-yyyyMMdd.ndjson</c> exactly: the directory is the technical log's, and in L2/G3 an
/// evidence directory, so a looser match would delete somebody else's file (<c>agv-*.log</c> included). The timeline is
/// diagnostic material and is not REQ-0272's technical log.
/// </para>
/// <para>
/// <b>Clock jumps.</b> A vehicle PC's clock can jump months ahead after a power loss (workspace <c>remote-ops/fleet.md</c>),
/// and by that clock every real timeline file has expired. The same two gates as the technical log's purge apply: the
/// "now" used to purge is never later than the newest timeline day found at the first purge plus this process's uptime on
/// the monotonic clock; and nothing is deleted if, between the newest file to delete and today, two adjacent days lie more
/// than <see cref="MaxContinuousGapDays"/> apart.
/// </para>
/// <para>
/// <b>Failures.</b> Nothing here may reach the business or the screen. A failed write is logged once, until a write
/// succeeds again; the logger is called inside its own <c>try/catch</c>, because a logger can throw too.
/// </para>
/// </remarks>
internal sealed class HmiScreenStateFileWriter : IHmiScreenStateSink, IAsyncDisposable
{
    internal const string CurrentFileName = "hmi-state.json";
    private const string TimelinePrefix = "hmi-state-";
    private const string TimelineSuffix = ".ndjson";
    private const string DayFormat = "yyyyMMdd";
    private const int MaxContinuousGapDays = 7;
    private const int ReplaceAttempts = 5;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(10);

    private readonly HmiScreenStateOutputOptions _options;
    private readonly IHmiScreenStateFileSystem _files;
    private readonly TimeProvider _time;
    private readonly IAppLogger _logger;
    private readonly string _processStartId;
    private readonly DateTimeOffset _processStartedAt;
    private readonly long _startTimestamp;
    private readonly Channel<Request> _requests;
    private readonly Task _loop;
    private string? _lastFingerprint;
    private long _revision;
    private bool _disabled;
    private bool _failing;
    private DateOnly? _purgedFor;
    private DateOnly? _cappedFor;
    private DateTimeOffset? _purgeAnchor;

    internal HmiScreenStateFileWriter(
        HmiScreenStateOutputOptions options,
        IHmiScreenStateFileSystem files,
        TimeProvider time,
        IAppLogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processStartId = Guid.NewGuid().ToString("N");
        _processStartedAt = time.GetLocalNow();
        _startTimestamp = time.GetTimestamp();
        _requests = Channel.CreateBounded<Request>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _loop = Task.Run(RunAsync);
    }

    internal string CurrentFilePath => Path.Combine(_options.Directory, CurrentFileName);

    internal static string TimelineFileName(DateOnly day) =>
        TimelinePrefix + day.ToString(DayFormat, CultureInfo.InvariantCulture) + TimelineSuffix;

    public void Submit(HmiScreenState state, bool heartbeat) =>
        _requests.Writer.TryWrite(new Request(state, heartbeat, null, null));

    public void Disable(string entry, string reason, DateTimeOffset at)
    {
        _requests.Writer.TryWrite(new Request(null, false, new DisableRequest(entry, reason, at), null));
        _requests.Writer.TryComplete();
    }

    /// <summary>
    /// Completes once everything submitted before it has been handled (tests). False if the writer no longer takes
    /// requests or the marker was dropped behind a backlog.
    /// </summary>
    internal async Task<bool> DrainAsync()
    {
        TaskCompletionSource<bool> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.Writer.TryWrite(new Request(null, false, null, done)))
        {
            return false;
        }

        Task finished = await Task.WhenAny(done.Task, _loop).ConfigureAwait(false);
        return finished == done.Task;
    }

    /// <summary>Lets the pending writes finish, for at most <paramref name="timeout"/>.</summary>
    internal async Task CompleteAsync(TimeSpan timeout)
    {
        _requests.Writer.TryComplete();
        await Task.WhenAny(_loop, Task.Delay(timeout)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await CompleteAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

    private async Task RunAsync()
    {
        await foreach (Request request in _requests.Reader.ReadAllAsync().ConfigureAwait(false))
        {
#pragma warning disable CA1031 // See the class remarks: no failure here may leave the writer.
            try
            {
                if (request.Drained is { } drained)
                {
                    drained.TrySetResult(true);
                }
                else if (request.Disable is { } disable)
                {
                    WriteDisabled(disable);
                }
                else if (!_disabled && request.State is { } state)
                {
                    WriteActive(state, request.Heartbeat);
                }
            }
            catch (Exception failure)
            {
                ReportFailure(failure);
            }
#pragma warning restore CA1031
        }
    }

    private void WriteActive(HmiScreenState state, bool heartbeat)
    {
        string fingerprint = HmiScreenStateDocument.Fingerprint(state);
        bool changed = !string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal);
        if (!changed && !heartbeat)
        {
            return;
        }

        if (changed)
        {
            _revision++;
            _lastFingerprint = fingerprint;
        }

        HmiScreenStateDocument document = Document(HmiScreenStateDocumentStatus.Active, state.ProgramVersion, null, state);
        byte[] json = document.ToUtf8Json();
        WriteCurrent(json);
        if (changed)
        {
            AppendTimeline(document.WrittenAt, json);
        }

        ReportRecovery();
    }

    private void WriteDisabled(DisableRequest disable)
    {
        if (_disabled)
        {
            return;
        }

        _disabled = true;
        _revision++;
        HmiScreenStateDocument document = Document(HmiScreenStateDocumentStatus.Disabled, ProgramVersion(), disable, null);
        byte[] json = document.ToUtf8Json();
        WriteCurrent(json);
        AppendTimeline(document.WrittenAt, json);
    }

    private HmiScreenStateDocument Document(
        HmiScreenStateDocumentStatus status,
        string programVersion,
        DisableRequest? disable,
        HmiScreenState? state) =>
        new(
            HmiScreenStateDocument.CurrentSchemaVersion,
            status,
            _revision,
            _time.GetLocalNow(),
            _processStartId,
            Environment.ProcessId,
            _processStartedAt,
            programVersion,
            disable?.Entry,
            disable?.Reason,
            disable?.At,
            state);

    private static string ProgramVersion() =>
        typeof(HmiScreenStateFileWriter).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
        ?? string.Empty;

    private void WriteCurrent(byte[] json)
    {
        _files.CreateDirectory(_options.Directory);
        string current = CurrentFilePath;
        string pending = current + ".tmp";
        _files.WriteAllBytes(pending, json);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                _files.Replace(pending, current);
                return;
            }
            catch (Exception failure) when (
                attempt < ReplaceAttempts && failure is IOException or UnauthorizedAccessException)
            {
                // A reader holding hmi-state.json without delete sharing blocks the move for as long as it reads.
                Thread.Sleep(ReplaceRetryDelay);
            }
        }
    }

    private void AppendTimeline(DateTimeOffset now, byte[] json)
    {
        DateOnly day = DateOnly.FromDateTime(now.DateTime);
        PurgeOnceADay(now, day);
        if (_cappedFor == day)
        {
            return;
        }

        string path = Path.Combine(_options.Directory, TimelineFileName(day));
        byte[] line = HmiScreenStateFileSystem.Utf8Line(json);
        if (_files.LengthOf(path) + line.Length > _options.TimelineMaxFileBytes)
        {
            _cappedFor = day;
            Log(
                LogSeverity.Warning,
                $"屏幕状态时间线 {TimelineFileName(day)} 达到大小上限 {_options.TimelineMaxFileBytes} 字节，当天不再追加；"
                + "当前快照文件照常更新。",
                null);
            return;
        }

        _files.AppendAllBytes(path, line);
    }

    private void PurgeOnceADay(DateTimeOffset now, DateOnly today)
    {
        if (_purgedFor == today)
        {
            return;
        }

        _purgedFor = today;
        List<(string Name, DateOnly Day)> timelines = [.. _files.FileNamesIn(_options.Directory)
            .Select(name => (Name: name, Ok: TryParseTimelineDay(name, out DateOnly day), Day: day))
            .Where(file => file.Ok)
            .Select(file => (file.Name, file.Day))
            .OrderBy(file => file.Day)];
        DateTimeOffset purgeNow = BoundedPurgeNow(now, today, timelines);
        DateTimeOffset cutoff = purgeNow - TimeSpan.FromDays(_options.TimelineRetentionDays);
        List<(string Name, DateOnly Day)> expired = [.. timelines.Where(file => EndOfDay(file.Day, now.Offset) <= cutoff)];
        if (expired.Count == 0)
        {
            return;
        }

        // Gate two: from the newest file to delete up to today, the files must run without a gap longer than a week.
        List<DateOnly> chain = [.. timelines.Where(file => file.Day >= expired[^1].Day && file.Day <= today).Select(file => file.Day)];
        chain.Add(today);
        for (int index = 1; index < chain.Count; index++)
        {
            if (chain[index].DayNumber - chain[index - 1].DayNumber > MaxContinuousGapDays)
            {
                Log(
                    LogSeverity.Warning,
                    $"屏幕状态时间线之间有超过 {MaxContinuousGapDays} 天的空档（{chain[index - 1]:yyyy-MM-dd} 到 {chain[index]:yyyy-MM-dd}），"
                    + "可能是时钟跳过，本次不清理。",
                    null);
                return;
            }
        }

        foreach ((string name, DateOnly _) in expired)
        {
            _files.Delete(Path.Combine(_options.Directory, name));
        }
    }

    /// <summary>
    /// Gate one: the purge's "now" is never later than the anchor plus this process's uptime. The anchor is the start of
    /// the newest timeline day (not after today) found at the first purge, or the wall clock when there is none.
    /// </summary>
    private DateTimeOffset BoundedPurgeNow(DateTimeOffset now, DateOnly today, List<(string Name, DateOnly Day)> timelines)
    {
        if (_purgeAnchor is null)
        {
            DateOnly? newest = timelines.Where(file => file.Day <= today).Select(file => (DateOnly?)file.Day).LastOrDefault();
            _purgeAnchor = newest is { } day ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), now.Offset) : now;
        }

        DateTimeOffset bound = _purgeAnchor.Value + _time.GetElapsedTime(_startTimestamp);
        return now < bound ? now : bound;
    }

    private static DateTimeOffset EndOfDay(DateOnly day, TimeSpan offset) =>
        day == DateOnly.MaxValue
            ? DateTimeOffset.MaxValue
            : new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), offset);

    internal static bool TryParseTimelineDay(string fileName, out DateOnly day)
    {
        day = default;
        return fileName.Length == TimelinePrefix.Length + DayFormat.Length + TimelineSuffix.Length
            && fileName.StartsWith(TimelinePrefix, StringComparison.Ordinal)
            && fileName.EndsWith(TimelineSuffix, StringComparison.Ordinal)
            && fileName.AsSpan(TimelinePrefix.Length, DayFormat.Length).ToString().All(char.IsAsciiDigit)
            && DateOnly.TryParseExact(
                fileName.Substring(TimelinePrefix.Length, DayFormat.Length),
                DayFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out day);
    }

    private void ReportFailure(Exception failure)
    {
        if (_failing)
        {
            return;
        }

        _failing = true;
        Log(LogSeverity.Warning, "屏幕状态快照写盘失败，界面与业务不受影响；恢复之前不再重复记录。", failure);
    }

    private void ReportRecovery()
    {
        if (!_failing)
        {
            return;
        }

        _failing = false;
        Log(LogSeverity.Information, "屏幕状态快照写盘已恢复。", null);
    }

    private void Log(LogSeverity severity, string message, Exception? exception)
    {
#pragma warning disable CA1031 // The logger can throw anything (research section 4.3, item 1); nothing may leave here.
        try
        {
            _logger.Write(severity, nameof(HmiScreenStateFileWriter), message, exception);
        }
        catch (Exception)
        {
            // Nowhere left to report it.
        }
#pragma warning restore CA1031
    }

    private sealed record Request(
        HmiScreenState? State,
        bool Heartbeat,
        DisableRequest? Disable,
        TaskCompletionSource<bool>? Drained);

    private sealed record DisableRequest(string Entry, string Reason, DateTimeOffset At);
}
