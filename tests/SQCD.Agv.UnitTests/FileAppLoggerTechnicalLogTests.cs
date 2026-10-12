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
        SeedDays(-29, -1);
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
        SeedDays(-59, -1);
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
    /// 车上 RTC 断电后会跳到约 5 个月以后（workspace <c>remote-ops/fleet.md</c>「镜像自带一个时钟故障」），
    /// 校时任务开机后才跑、还可能失败。车载端在校时之前写出第一行时，按那个时钟算，目录里全部真实日志都「过期」了。
    /// 保留期边界两侧的按天日志不连续时一份都不删，只记一行。
    /// </summary>
    [Fact]
    public void AClockThatJumpedForwardBeforeTimeSyncPurgesNothing()
    {
        string[] real = SeedDays(-40, -1);
        DateTimeOffset jumped = Noon.AddDays(150);

        NewLogger(new ManualTime(jumped)).Write(LogSeverity.Information, "App", "校时之前的第一行");

        Assert.All(real, path => Assert.True(File.Exists(path), $"时钟前跳时删掉了真实日志：{path}"));
        Assert.Contains(
            LinesOf(jumped),
            line => line.Contains("	Warning	LocalFault	FileAppLogger	", StringComparison.Ordinal)
                && line.Contains("不连续", StringComparison.Ordinal));
    }

    /// <summary>
    /// 校时一直失败、错的时钟跑过了午夜：昨天按错时钟写的那份成了「最新」，只比较「今天与最新一份」挡不住这一步。
    /// 比较的是保留期边界两侧，所以仍然一份不删；时钟纠正回来后照常清理，按错时钟写出的那份留着。
    /// </summary>
    [Fact]
    public void AClockThatStaysWrongPastMidnightStillPurgesNothingAndThePurgeResumesOnceItIsCorrected()
    {
        string[] real = SeedDays(-40, -1);
        ManualTime time = new(Noon.AddDays(150));
        FileAppLogger logger = NewLogger(time);
        logger.Write(LogSeverity.Information, "App", "错时钟第一天");

        time.Now = Noon.AddDays(151);
        logger.Write(LogSeverity.Information, "App", "错时钟第二天");
        Assert.All(real, path => Assert.True(File.Exists(path), $"错时钟跨天后删掉了真实日志：{path}"));

        time.Now = Noon;
        logger.Write(LogSeverity.Information, "App", "时钟已纠正");
        Assert.All(real.Take(10), path => Assert.False(File.Exists(path), $"纠正后该删没删：{path}"));
        Assert.All(real.Skip(10), path => Assert.True(File.Exists(path), $"纠正后不该删：{path}"));
        Assert.True(File.Exists(Path.Combine(_directory, OnboardTechnicalLogFileName.For(DateOnly.FromDateTime(Noon.AddDays(150).DateTime)))));
    }

    /// <summary>边界两侧连续时照常清理：相隔两天（周末停机）仍算连续。</summary>
    [Fact]
    public void ATwoDayGapAcrossTheBoundaryStillPurges()
    {
        string[] old = SeedDays(-40, -31);
        string[] recent = SeedDays(-29, -1);

        NewLogger(new ManualTime(Noon)).Write(LogSeverity.Information, "App", "周末之后");

        Assert.All(old, path => Assert.False(File.Exists(path), path));
        Assert.All(recent, path => Assert.True(File.Exists(path), path));
    }

    /// <summary>
    /// 文件名是远未来的一天（<c>9999-12-31</c>）也是合法的按天文件；它既不过期，也不能让「那一天结束」的计算溢出、
    /// 把整轮清理中断。
    /// </summary>
    [Fact]
    public void AFarFutureDailyFileNeitherExpiresNorStopsThePurge()
    {
        string[] old = SeedDays(-40, -31);
        string[] recent = SeedDays(-30, -1);
        string farFuture = Seed("agv-99991231.log");

        NewLogger(new ManualTime(Noon)).Write(LogSeverity.Information, "App", "远未来文件");

        Assert.All(old, path => Assert.False(File.Exists(path), path));
        Assert.All(recent, path => Assert.True(File.Exists(path), path));
        Assert.True(File.Exists(farFuture));
        // 目录按名字列，9999 排在最后，前面的照样删得掉；溢出是否中断了这一轮，看有没有记失败。
        Assert.DoesNotContain(LinesOfToday(), line => line.Contains("\tFileAppLogger\t", StringComparison.Ordinal));
    }

    /// <summary>
    /// 环境变量没展开（变量不存在）时路径里还留着 <c>%</c>：照写，但记一行，否则日志悄悄落进一个字面叫
    /// <c>%LOCALAPPDATA%</c> 的目录。
    /// </summary>
    [Fact]
    public void AnUnexpandedVariableInTheDirectoryIsReported()
    {
        string unexpanded = Path.Combine(_directory, "%HMI312_NOT_SET_" + Guid.NewGuid().ToString("N") + "%");
        FileAppLogger logger = new(new LogSettings { Directory = unexpanded }, new ManualTime(Noon));

        logger.Write(LogSeverity.Information, "App", "第一条");
        logger.Write(LogSeverity.Information, "App", "第二条");

        string[] lines = File.ReadAllLines(Path.Combine(unexpanded, OnboardTechnicalLogFileName.For(DateOnly.FromDateTime(Noon.DateTime))));
        Assert.Single(lines, line => line.Contains("	LocalFault	FileAppLogger	", StringComparison.Ordinal)
            && line.Contains('%', StringComparison.Ordinal));
        Assert.Contains(lines, line => line.EndsWith("	App	第一条", StringComparison.Ordinal));
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

    /// <summary>
    /// 默认目录在 %LOCALAPPDATA%，不在程序目录：部署把程序目录整个换成新包、旧包只留一代，第二次部署就把
    /// 两代以前的日志一并删掉，30 天无从谈起。
    /// </summary>
    [Fact]
    public void TheDefaultDirectoryIsUnderLocalAppDataNotBesideTheProgram()
    {
        Assert.Equal(@"%LOCALAPPDATA%\SQCD\8005AGV\logs", new LogSettings().Directory);
    }

    [Fact]
    public void AnEnvironmentVariableInTheDirectoryIsExpanded()
    {
        string variable = "HMI312_LOG_ROOT_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, _directory);
        try
        {
            FileAppLogger logger = new(new LogSettings { Directory = $"%{variable}%\\expanded" }, new ManualTime(Noon));

            logger.Write(LogSeverity.Information, "App", "展开环境变量");

            Assert.True(File.Exists(Path.Combine(
                _directory,
                "expanded",
                OnboardTechnicalLogFileName.For(DateOnly.FromDateTime(Noon.DateTime)))));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
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

    /// <summary>相对 <see cref="Noon"/> 连续若干天的按天日志，从早到晚。</summary>
    private string[] SeedDays(int fromOffset, int toOffset)
    {
        DateOnly today = DateOnly.FromDateTime(Noon.DateTime);
        return Enumerable.Range(fromOffset, toOffset - fromOffset + 1)
            .Select(offset => Seed(OnboardTechnicalLogFileName.For(today.AddDays(offset))))
            .ToArray();
    }

    private string[] LinesOf(DateTimeOffset at) =>
        File.ReadAllLines(Path.Combine(_directory, OnboardTechnicalLogFileName.For(DateOnly.FromDateTime(at.DateTime))));

    private string[] LinesOfToday() => LinesOf(Noon);

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
