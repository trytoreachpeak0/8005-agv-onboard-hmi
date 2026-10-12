using System.Diagnostics;
using System.Text;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// 车载技术日志（REQ-0272）：按天一个 <c>agv-yyyyMMdd.log</c>，每行
/// <c>时间戳\t等级\t类别\t来源\t消息</c>，过了保留期的按天文件由它自己清掉。
/// </summary>
/// <remarks>
/// <para>
/// <b>类别插在第三列</b>，不在行尾：L2 只按 Tab 取第 0 列的时间戳、再对整行做正则
/// （control-server <c>scripts/l2/scenarios/MultiStopRigCommon.ps1</c>），插列不打断它。
/// </para>
/// <para>
/// <b>清理只认 <see cref="OnboardTechnicalLogFileName"/> 的精确形状，只在本目录、不递归。</b>L2 与 G3 把日志目录指到
/// 证据目录，同目录里有别人的文件。时机是第一次写入与跨过午夜后的第一次写入：车可以几周不重启。
/// </para>
/// <para>
/// <b>时钟前跳时不删真实日志，两道闸。</b>车上 RTC 断电后会跳到约 5 个月以后（workspace <c>remote-ops/fleet.md</c>
/// 硬约束 2），校时开机后才跑、还可能失败；按那个时钟算，目录里全部真实日志都「过期」。
/// </para>
/// <list type="number">
/// <item>清理用的「现在」不超过「锚点＋本进程运行时长」：锚点是第一次清理时目录里最新一份（不晚于今天的）按天日志
/// 那一天的零点，运行时长取单调时钟。真实时间不早于那份日志里任何一行，所以这个上界不会超过真实时间；墙钟跳得再远，
/// 删的也只是按真实时间确已过期的。代价是重启后清理晚一两天，只多留。</item>
/// <item>从要删的最新一份到今天，任意相邻两份按天日志相隔超过 <see cref="MaxContinuousGapDays"/> 天就一份不删。
/// 锚点本身可能来自上一次按错时钟写出的文件（错时钟跨过一次重启），这一道挡的就是那种情况。比今天还晚的文件不算，
/// 否则时钟纠正之后，按错时钟写出的那份会一直挡着。门槛取 7 天：按周断续使用的备用车照常清理；比它更稀疏的车
/// 不清理，但那种车每天只有几 KB。</item>
/// </list>
/// <para>两道都挡不住的是：错时钟跨过一次重启、且跳变不足 7 天。那时会删掉按真实时间在 23～30 天之间的日志。
/// 这一条写在 onboard-hmi#320 的剩余风险里。</para>
/// <para>
/// <b>写日志与清理的任何失败都不出 <c>Write</c>。</b>业务代码在锁里、在 catch 里、在调度线程上调它，日志器抛出的
/// 任何东西都会变成一次业务失败（hmi#308 调研第 2 节）。此前只接 IO 与权限两类。
/// </para>
/// </remarks>
public sealed class FileAppLogger : IAppLogger
{
    /// <summary>相邻两份按天日志相隔不超过这么多天算连续：周末、按周断续使用都不该让清理停下。</summary>
    private const int MaxContinuousGapDays = 7;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly bool _writeToConsole;
    private readonly TimeProvider _time;
    private readonly OnboardTechnicalLogRetentionPolicy _retention;
    private bool _directoryReported;
    private DateOnly? _purgedFor;
    private DateTimeOffset? _anchor;
    private long _anchorTimestamp;

    public FileAppLogger(
        LogSettings settings,
        TimeProvider? timeProvider = null,
        OnboardTechnicalLogRetentionPolicy? retentionPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            Environment.ExpandEnvironmentVariables(settings.Directory)));
        _writeToConsole = settings.WriteToConsole;
        _time = timeProvider ?? TimeProvider.System;
        _retention = retentionPolicy ?? OnboardTechnicalLogRetentionPolicy.Default;
    }

    public event EventHandler<LogEntryEventArgs>? EntryWritten;

    public void Write(LogSeverity severity, string source, string message, Exception? exception = null) =>
        Write(severity, OnboardTechnicalLogCategories.DefaultFor(source, severity), source, message, exception);

    public void Write(
        LogSeverity severity,
        OnboardTechnicalLogCategory category,
        string source,
        string message,
        Exception? exception = null)
    {
#pragma warning disable CA1031 // 见类注释：日志器的任何失败都不能变成调用方的失败。
        try
        {
            string completeMessage = exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}";
            LogEntry entry = new(_time.GetLocalNow(), severity, source, completeMessage);
            string line = Format(entry.Timestamp, severity, category, source, completeMessage);

            lock (_gate)
            {
                ReportDirectoryOnce(entry.Timestamp);
                PurgeOnceADay(entry.Timestamp);
                Append(entry.Timestamp, line);
            }

            if (_writeToConsole)
            {
                TryConsole(line);
            }

            EntryWritten?.Invoke(this, new LogEntryEventArgs(entry));
        }
        catch (Exception failure)
        {
            Debug.WriteLine(failure);
        }
#pragma warning restore CA1031
    }

    private static string Format(
        DateTimeOffset timestamp,
        LogSeverity severity,
        OnboardTechnicalLogCategory category,
        string source,
        string message) =>
        $"{timestamp:O}\t{severity}\t{category}\t{source}\t{message}{Environment.NewLine}";

    private void Append(DateTimeOffset timestamp, string line)
    {
#pragma warning disable CA1031
        try
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, OnboardTechnicalLogFileName.For(DateOnly.FromDateTime(timestamp.DateTime)));
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
        catch (Exception failure)
        {
            Debug.WriteLine(failure);
        }
#pragma warning restore CA1031
    }

    private void AppendOwnWarning(DateTimeOffset now, string message) =>
        Append(now, Format(now, LogSeverity.Warning, OnboardTechnicalLogCategory.LocalFault, nameof(FileAppLogger), message));

    /// <summary>
    /// 展开后路径里还留着 <c>%</c>，多半是那个环境变量不存在：日志会落进一个字面叫 <c>%LOCALAPPDATA%</c> 的目录。
    /// 照写不误，只在第一次写时记一行。在锁里调。
    /// </summary>
    private void ReportDirectoryOnce(DateTimeOffset now)
    {
        if (_directoryReported)
        {
            return;
        }

        _directoryReported = true;
        if (_directory.Contains('%', StringComparison.Ordinal))
        {
            AppendOwnWarning(now, $"技术日志目录里的环境变量没有展开：{_directory}。日志照写在这里，请核对 logging.directory 与运行账号。");
        }
    }

    /// <summary>每个本地日历日清一次。在锁里调。</summary>
    private void PurgeOnceADay(DateTimeOffset now)
    {
        DateOnly today = DateOnly.FromDateTime(now.DateTime);
        if (_purgedFor == today)
        {
            return;
        }

        // 先记下再清：清理失败也不在同一天里每写一条就重试一次。
        _purgedFor = today;
        List<string> failures = [];
#pragma warning disable CA1031
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            List<(string Path, DateOnly Day)> found = [];
            foreach (string path in Directory.EnumerateFiles(_directory, "agv-*.log", SearchOption.TopDirectoryOnly))
            {
                if (OnboardTechnicalLogFileName.TryParseDay(Path.GetFileName(path), out DateOnly day))
                {
                    found.Add((path, day));
                }
            }

            DateTimeOffset retentionNow = BoundedNow(now, today, found.Select(file => file.Day));
            List<(string Path, DateOnly Day, bool Expired)> daily = found
                .Select(file => (file.Path, file.Day, _retention.IsDayExpired(file.Day, retentionNow)))
                .ToList();
            if (!daily.Any(file => file.Expired))
            {
                return;
            }

            // 从要删的最新一份到今天（今天这一份马上就要写），比今天晚的不算。
            DateOnly newestExpired = daily.Where(file => file.Expired).Max(file => file.Day);
            DateOnly[] window = daily
                .Select(file => file.Day)
                .Where(day => day >= newestExpired && day <= today)
                .Append(today)
                .Distinct()
                .Order()
                .ToArray();
            (DateOnly before, DateOnly after) = window.Zip(window.Skip(1)).MaxBy(pair => pair.Second.DayNumber - pair.First.DayNumber);
            int gap = after.DayNumber - before.DayNumber;
            if (gap > MaxContinuousGapDays)
            {
                AppendOwnWarning(
                    now,
                    $"按天日志在 {before:yyyy-MM-dd} 与 {after:yyyy-MM-dd} 之间相隔 {gap} 天（要删的最新一份是 {newestExpired:yyyy-MM-dd}）。"
                    + "可能是停机，也可能是时钟前跳（车上 RTC 断电会快约 5 个月），本次一份都不删，等这段连续后再清。");
                return;
            }

            foreach ((string path, _, bool expired) in daily)
            {
                if (!expired)
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch (Exception failure)
                {
                    failures.Add($"{Path.GetFileName(path)}（{failure.GetType().Name}: {failure.Message}）");
                }
            }
        }
        catch (Exception failure)
        {
            failures.Add($"{_directory}（{failure.GetType().Name}: {failure.Message}）");
        }
#pragma warning restore CA1031

        if (failures.Count > 0)
        {
            AppendOwnWarning(now, $"技术日志保留期清理没有删掉：{string.Join("；", failures)}。明天再试，业务不受影响。");
        }
    }

    /// <summary>
    /// 清理用的「现在」：墙钟与「锚点＋运行时长」中较小者。锚点在第一次清理时定，取不晚于今天的最新一份按天日志那一天的
    /// 零点；目录里还没有日志时取墙钟本身（没有可删的，错了也无害）。在锁里调。
    /// </summary>
    private DateTimeOffset BoundedNow(DateTimeOffset now, DateOnly today, IEnumerable<DateOnly> days)
    {
        if (_anchor is null)
        {
            DateOnly[] past = days.Where(day => day <= today).ToArray();
            _anchor = past.Length == 0
                ? now
                : new DateTimeOffset(past.Max().ToDateTime(TimeOnly.MinValue), now.Offset);
            _anchorTimestamp = _time.GetTimestamp();
        }

        DateTimeOffset bound = _anchor.Value + _time.GetElapsedTime(_anchorTimestamp);
        return bound < now ? bound : now;
    }

    private static void TryConsole(string line)
    {
#pragma warning disable CA1031
        try
        {
            Console.Write(line);
        }
        catch (Exception failure)
        {
            Debug.WriteLine(failure);
        }
#pragma warning restore CA1031
    }
}
