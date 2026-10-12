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
/// <b>保留期边界两侧的按天日志不连续时一份都不删。</b>车上 RTC 断电后会跳到约 5 个月以后（workspace
/// <c>remote-ops/fleet.md</c>），校时开机后才跑、还可能失败；按那个时钟算，目录里全部真实日志都「过期」。
/// 只比较「今天与最新一份」挡得住开机第一行，挡不住错时钟跑过午夜之后（那时最新一份就是昨天按错时钟写的）；
/// 比较边界两侧两种都挡得住。代价是停机几天后的清理推迟几天，只会多留，不会少留。
/// </para>
/// <para>
/// <b>写日志与清理的任何失败都不出 <c>Write</c>。</b>业务代码在锁里、在 catch 里、在调度线程上调它，日志器抛出的
/// 任何东西都会变成一次业务失败（hmi#308 调研第 2 节）。此前只接 IO 与权限两类。
/// </para>
/// </remarks>
public sealed class FileAppLogger : IAppLogger
{
    /// <summary>边界两侧相隔不超过这么多天算连续：周末停机不该让清理停下。</summary>
    private const int MaxContinuousGapDays = 2;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly bool _writeToConsole;
    private readonly TimeProvider _time;
    private readonly OnboardTechnicalLogRetentionPolicy _retention;
    private bool _directoryReported;
    private DateOnly? _purgedFor;

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

            List<(string Path, DateOnly Day, bool Expired)> daily = [];
            foreach (string path in Directory.EnumerateFiles(_directory, "agv-*.log", SearchOption.TopDirectoryOnly))
            {
                if (OnboardTechnicalLogFileName.TryParseDay(Path.GetFileName(path), out DateOnly day))
                {
                    daily.Add((path, day, _retention.IsDayExpired(day, now)));
                }
            }

            if (!daily.Any(file => file.Expired))
            {
                return;
            }

            // 今天这一份马上就要写，算在留下的一侧。
            DateOnly newestExpired = daily.Where(file => file.Expired).Max(file => file.Day);
            DateOnly oldestKept = daily.Where(file => !file.Expired).Select(file => file.Day).Append(today).Min();
            int gap = oldestKept.DayNumber - newestExpired.DayNumber;
            if (gap > MaxContinuousGapDays)
            {
                AppendOwnWarning(
                    now,
                    $"保留期边界两侧的按天日志不连续：要删的最新一份是 {newestExpired:yyyy-MM-dd}，要留的最早一份是 "
                    + $"{oldestKept:yyyy-MM-dd}，相隔 {gap} 天。可能是停机，也可能是时钟前跳（车上 RTC 断电会快约 5 个月），"
                    + "本次一份都不删，等边界两侧连续后再清。");
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
