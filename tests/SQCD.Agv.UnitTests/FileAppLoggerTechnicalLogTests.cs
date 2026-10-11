using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// <see cref="FileAppLogger"/> 作为 REQ-0272 的车载技术日志：每行带类别，按保留期清理过期的按天文件，
/// 清理与写入的任何失败都不传到调用方（onboard-hmi#312）。
/// </summary>
/// <remarks>
/// <para>
/// 清理那一组断言的是「只删该删的」：L2 与 G3 把日志目录指到证据目录，同目录里有 <c>control.err.log</c>、
/// <c>runner.ps1</c>、hmi#308 的状态时间线 <c>hmi-state-*.ndjson</c>。删错一个就是删掉别人的证据，
/// 而那种错不会让任何东西报错，所以必须在这里红。
/// </para>
/// <para>不挂 <c>IntegrationSlice</c> trait，理由同 <see cref="OnboardAlarmSnapshotTests"/>。</para>
/// </remarks>
public sealed class FileAppLoggerTechnicalLogTests : IDisposable
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, Offset);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hmi312-log-" + Guid.NewGuid().ToString("N"));

    public FileAppLoggerTechnicalLogTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AnExplicitCategoryIsWrittenAsTheThirdColumn()
    {
        FileAppLogger logger = NewLogger(new ManualTime(Noon));

        logger.Write(LogSeverity.Information, OnboardTechnicalLogCategory.SignalChange, "ModbusTcpIoModuleClient", "1号仓IO变化");

        string[] columns = SingleLineOfToday().Split('\t');
        Assert.Equal(5, columns.Length);
        Assert.True(DateTimeOffset.TryParse(columns[0], out _), columns[0]);
        Assert.Equal("Information", columns[1]);
        Assert.Equal("SignalChange", columns[2]);
        Assert.Equal("ModbusTcpIoModuleClient", columns[3]);
        Assert.Equal("1号仓IO变化", columns[4]);
    }

    [Fact]
    public void AnUntaggedEntryIsWrittenWithItsSourcesDefaultCategory()
    {
        FileAppLogger logger = NewLogger(new ManualTime(Noon));

        logger.Write(LogSeverity.Warning, "WireToGateSessionService", "上层会话不可用");

        string[] columns = SingleLineOfToday().Split('\t');
        Assert.Equal("RestartRecovery", columns[2]);
        Assert.Equal("WireToGateSessionService", columns[3]);
    }

    /// <summary>
    /// 经接口调用（业务代码手里拿的就是 <see cref="IAppLogger"/>）也要落到带类别的那一份，
    /// 而不是接口的默认实现把类别丢掉。
    /// </summary>
    [Fact]
    public void AnExplicitCategoryThroughTheInterfaceIsNotDropped()
    {
#pragma warning disable CA1859 // The interface type is the point: business code holds an IAppLogger.
        IAppLogger logger = NewLogger(new ManualTime(Noon));
#pragma warning restore CA1859

        logger.Write(LogSeverity.Information, OnboardTechnicalLogCategory.SafetyDecision, "OnboardController", "启动IO安全快照验证通过");

        Assert.Equal("SafetyDecision", SingleLineOfToday().Split('\t')[2]);
    }

    /// <summary>
    /// 第一次写的时候清一次：31 天前那一天整天过期，删；30 天前那一天还有没满 30 天的条目，留；
    /// 同目录里一切不是 <c>agv-yyyyMMdd.log</c> 形状的东西，无论多旧，都不碰；子目录也不进。
    /// </summary>
    [Fact]
    public void ThePurgeDeletesOnlyExpiredDailyTechnicalLogs()
    {
        DateOnly today = DateOnly.FromDateTime(Noon.DateTime);
        string expired = Seed(OnboardTechnicalLogFileName.For(today.AddDays(-31)));
        string longExpired = Seed(OnboardTechnicalLogFileName.For(today.AddDays(-400)));
        string[] kept =
        [
            Seed(OnboardTechnicalLogFileName.For(today.AddDays(-30))),
            Seed(OnboardTechnicalLogFileName.For(today.AddDays(-1))),
            Seed("hmi-state-" + Digits(today.AddDays(-90)) + ".ndjson"),
            Seed("control.err.log"),
            Seed("onboard.out.log"),
            Seed("runner.ps1"),
            Seed("agv-" + Digits(today.AddDays(-90)) + ".log.bak"),
            Seed("AGV-" + Digits(today.AddDays(-90)) + ".log"),
            Seed("agv-notadate.log"),
            Seed(Path.Combine("onboard-app", OnboardTechnicalLogFileName.For(today.AddDays(-90))))
        ];
        foreach (string path in kept.Append(expired).Append(longExpired))
        {
            File.SetLastWriteTimeUtc(path, Noon.AddDays(-500).UtcDateTime);
        }

        NewLogger(new ManualTime(Noon)).Write(LogSeverity.Information, "App", "车载端启动");

        Assert.False(File.Exists(expired), expired);
        Assert.False(File.Exists(longExpired), longExpired);
        Assert.All(kept, path => Assert.True(File.Exists(path), $"不该删：{path}"));
    }

    [Fact]
    public void ALongerRetentionKeepsWhatTheDefaultWouldDelete()
    {
        DateOnly today = DateOnly.FromDateTime(Noon.DateTime);
        string fortyDaysAgo = Seed(OnboardTechnicalLogFileName.For(today.AddDays(-40)));

        NewLogger(new ManualTime(Noon), new OnboardTechnicalLogRetentionPolicy(TimeSpan.FromDays(90)))
            .Write(LogSeverity.Information, "App", "车载端启动");

        Assert.True(File.Exists(fortyDaysAgo));
    }

    /// <summary>车不重启也要清：跨过午夜后的第一条写入再清一次。</summary>
    [Fact]
    public void ThePurgeRunsAgainWhenTheDayRollsOver()
    {
        ManualTime time = new(Noon);
        DateOnly today = DateOnly.FromDateTime(Noon.DateTime);
        FileAppLogger logger = NewLogger(time);
        logger.Write(LogSeverity.Information, "App", "第一天");

        // 今天看还差一天才过期；明天看就过期了。
        string expiresTomorrow = Seed(OnboardTechnicalLogFileName.For(today.AddDays(-30)));
        logger.Write(LogSeverity.Information, "App", "同一天再写一条");
        Assert.True(File.Exists(expiresTomorrow));

        time.Now = Noon.AddDays(1);
        logger.Write(LogSeverity.Information, "App", "第二天");
        Assert.False(File.Exists(expiresTomorrow));
    }

    /// <summary>
    /// 一个过期文件删不掉（被别的进程独占打开），清理不抛、其余过期文件照删、这一条照写，
    /// 并留一行本地异常说明哪里没清掉。
    /// </summary>
    [Fact]
    public void APurgeFailureDoesNotReachTheCallerAndTheEntryIsStillWritten()
    {
        DateOnly today = DateOnly.FromDateTime(Noon.DateTime);
        string locked = Seed(OnboardTechnicalLogFileName.For(today.AddDays(-60)));
        string deletable = Seed(OnboardTechnicalLogFileName.For(today.AddDays(-61)));
        FileAppLogger logger = NewLogger(new ManualTime(Noon));

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Exception? thrown = Record.Exception(() => logger.Write(LogSeverity.Information, "App", "业务照常"));
            Assert.Null(thrown);
        }

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(deletable));
        string[] lines = LinesOfToday();
        Assert.Contains(lines, line => line.EndsWith("\tApp\t业务照常", StringComparison.Ordinal));
        Assert.Contains(
            lines,
            line => line.Contains("\tLocalFault\tFileAppLogger\t", StringComparison.Ordinal)
                && line.Contains(Path.GetFileName(locked), StringComparison.Ordinal));
    }

    /// <summary>
    /// 日志目录整个不可用（路径被一个同名文件占了）：清理与写入都失败，调用方什么都感觉不到。
    /// </summary>
    [Fact]
    public void AnUnusableDirectoryDoesNotReachTheCaller()
    {
        string blocked = Path.Combine(_directory, "blocked");
        File.WriteAllText(blocked, "not a directory");
        FileAppLogger logger = new(new LogSettings { Directory = blocked }, new ManualTime(Noon));

        Exception? thrown = Record.Exception(() =>
        {
            logger.Write(LogSeverity.Information, "App", "第一条");
            logger.Write(LogSeverity.Error, OnboardTechnicalLogCategory.LocalFault, "App", "第二条", new InvalidOperationException("x"));
        });

        Assert.Null(thrown);
    }

    /// <summary>
    /// 不只是 IO 与权限两类：任何异常都不出 <see cref="FileAppLogger.Write(LogSeverity, string, string, Exception?)"/>。
    /// 这里用订阅者抛出的异常代表「别的类型」，它此前会原样穿出去。
    /// </summary>
    [Fact]
    public void AnyExceptionInsideWriteStaysInsideIt()
    {
        FileAppLogger logger = NewLogger(new ManualTime(Noon));
        logger.EntryWritten += (_, _) => throw new InvalidOperationException("subscriber failed");

        Exception? thrown = Record.Exception(() => logger.Write(LogSeverity.Information, "App", "业务照常"));

        Assert.Null(thrown);
        Assert.Contains(LinesOfToday(), line => line.EndsWith("\tApp\t业务照常", StringComparison.Ordinal));
    }

    private FileAppLogger NewLogger(TimeProvider time, OnboardTechnicalLogRetentionPolicy? retention = null) =>
        new(new LogSettings { Directory = _directory }, time, retention);

    private static string Digits(DateOnly day) =>
        day.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

    private string Seed(string relativePath)
    {
        string path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "seed\n");
        return path;
    }

    private string[] LinesOfToday() =>
        File.ReadAllLines(Path.Combine(_directory, OnboardTechnicalLogFileName.For(DateOnly.FromDateTime(Noon.DateTime))));

    private string SingleLineOfToday() => Assert.Single(LinesOfToday());

    /// <summary>本地时区固定为 +08:00，与车上一致，也让文件名的「哪一天」不随跑测试的机器变。</summary>
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private static readonly TimeZoneInfo Zone =
            TimeZoneInfo.CreateCustomTimeZone("hmi312+08", Offset, "hmi312+08", "hmi312+08");

        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => Zone;
    }
}
