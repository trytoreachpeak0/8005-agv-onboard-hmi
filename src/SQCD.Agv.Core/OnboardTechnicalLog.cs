namespace SQCD.Agv.Core;

/// <summary>
/// 车载技术日志的七个覆盖面，逐字取自 REQ-0272：「OnboardTechnicalLog 覆盖 IO 通信、信号变化、安全判断、执行阶段、
/// 配置、重启恢复和本地异常」（8005-agv-program <c>requirements/baselines/current-requirements-v1.9.0.md:15764</c>，
/// v1.0.0 起未变）。
/// </summary>
/// <remarks>
/// 这里曾经是四类（IoCommunication、SignalChange、SafetyDecision、OperatorEvent），注释也写「四类（REQ-0272）」，
/// 但需求原文从来是七个面、没有「操作事件」（onboard-hmi#312 核对）。操作员按了什么、扫了什么归执行阶段。
/// 写进日志行的是枚举名，所以改名就是改日志格式。
/// </remarks>
public enum OnboardTechnicalLogCategory
{
    /// <summary>IO 通信：与 IO 模块连上、断开、读写往返。</summary>
    IoCommunication,

    /// <summary>信号变化：锁反馈、光幕、开锁输出的翻转。</summary>
    SignalChange,

    /// <summary>安全判断：冷启动基线、停稳信号、告警求值、出发前检查。</summary>
    SafetyDecision,

    /// <summary>执行阶段：装卸流程、操作员动作、服务端下发与结果上报。</summary>
    ExecutionPhase,

    /// <summary>配置：生效配置、配置被拒、仓位配置识别。</summary>
    Configuration,

    /// <summary>重启恢复：启动、单实例、会话重建、日志簿恢复。</summary>
    RestartRecovery,

    /// <summary>本地异常：未处理异常与按 Error 记下的本地失败。</summary>
    LocalFault
}

/// <summary>
/// 一条日志没有显式标类别时，按「来源类名＋等级」给它的类别（onboard-hmi#312，调度定的 B2 方案）。
/// </summary>
/// <remarks>
/// <para>
/// 约 190 个调用点不逐个改：大多数来源只写一类事，按来源给默认值就是对的。写不止一类事、而且那一类对排障要紧的
/// 少数几处，在调用点显式标（<see cref="IAppLogger.Write(LogSeverity, OnboardTechnicalLogCategory, string, string, Exception?)"/>）。
/// </para>
/// <para>
/// <b>每个来源都必须在这里登记。</b><c>TechnicalLogSourceRegistryArchitectureTests</c> 扫 <c>src/</c> 下所有日志调用，
/// 未登记的来源让它变红：新加一个类却不决定它记的是哪一面，那一面就悄悄掉出了需求的覆盖。未登记的来源在运行时
/// 归 <see cref="OnboardTechnicalLogCategory.LocalFault"/>，不抛异常——日志器绝不能因为分类挡住业务。
/// </para>
/// <para>
/// 等级为 <see cref="LogSeverity.Error"/> 的未标注条目一律归本地异常，压过来源的默认值。
/// </para>
/// </remarks>
public static class OnboardTechnicalLogCategories
{
    private static readonly Dictionary<string, OnboardTechnicalLogCategory> DefaultBySource =
        new(StringComparer.Ordinal)
        {
            ["App"] = OnboardTechnicalLogCategory.ExecutionPhase,
            ["MainViewModel"] = OnboardTechnicalLogCategory.ExecutionPhase,
            ["OnboardController"] = OnboardTechnicalLogCategory.ExecutionPhase,
            ["WireToGateBusinessService"] = OnboardTechnicalLogCategory.ExecutionPhase,
            // 与规则模块（服务端）的往返是结果上报与握手。需求七个面里没有「与服务端通信」，它服务于执行阶段。
            ["TcpJsonRuleGateway"] = OnboardTechnicalLogCategory.ExecutionPhase,
            ["ModbusTcpIoModuleClient"] = OnboardTechnicalLogCategory.IoCommunication,
            ["OnboardAlarmMonitor"] = OnboardTechnicalLogCategory.SafetyDecision,
            ["SlotGroupPresentation"] = OnboardTechnicalLogCategory.Configuration,
            // 会话建立、断开、重连与就绪变化，都是断线或重启之后怎么回来。
            ["WireToGateSessionService"] = OnboardTechnicalLogCategory.RestartRecovery,
            ["WireToGateSessionClient"] = OnboardTechnicalLogCategory.RestartRecovery,
            ["JournaledOperationsFeed"] = OnboardTechnicalLogCategory.RestartRecovery,
            ["SingleInstanceGuard"] = OnboardTechnicalLogCategory.RestartRecovery,
            ["RunningInstanceWindow"] = OnboardTechnicalLogCategory.RestartRecovery,
            ["FileAppLogger"] = OnboardTechnicalLogCategory.LocalFault,
            ["ProcessFailureLogging"] = OnboardTechnicalLogCategory.LocalFault
        };

    /// <summary>登记过的来源类名。</summary>
    public static IReadOnlyCollection<string> RegisteredSources => DefaultBySource.Keys;

    public static bool IsRegisteredSource(string source) => DefaultBySource.ContainsKey(source);

    /// <summary>未显式标类别的一条日志归哪一面。</summary>
    public static OnboardTechnicalLogCategory DefaultFor(string source, LogSeverity severity)
    {
        if (severity == LogSeverity.Error)
        {
            return OnboardTechnicalLogCategory.LocalFault;
        }

        return DefaultBySource.TryGetValue(source ?? string.Empty, out OnboardTechnicalLogCategory category)
            ? category
            : OnboardTechnicalLogCategory.LocalFault;
    }
}

/// <summary>
/// 技术日志保留多久。REQ-0272 的下限是 30 天。
/// </summary>
/// <remarks>
/// 下限是硬的：配置一个短于 30 天的值会被拒绝，而不是被静默接受。可配置指的是「可以更长」。
/// </remarks>
public sealed record OnboardTechnicalLogRetentionPolicy
{
    public OnboardTechnicalLogRetentionPolicy(TimeSpan retainFor)
    {
        if (retainFor < Minimum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retainFor),
                retainFor,
                $"REQ-0272 要求车载技术日志至少保留 {Minimum.TotalDays} 天。");
        }
        RetainFor = retainFor;
    }

    /// <summary>REQ-0272 的下限，同时也是默认值。</summary>
    public static TimeSpan Minimum { get; } = TimeSpan.FromDays(30);

    public static OnboardTechnicalLogRetentionPolicy Default { get; } = new(Minimum);

    public TimeSpan RetainFor { get; }

    public DateTimeOffset CutoffFor(DateTimeOffset now) => now - RetainFor;

    public bool IsWithinRetention(DateTimeOffset occurredAt, DateTimeOffset now) =>
        occurredAt > CutoffFor(now);

    /// <summary>
    /// 某一天的按天日志文件能不能删：那一天里最晚的一条也早于截止时刻才能删。
    /// </summary>
    /// <remarks>
    /// 文件里的条目最晚到当天 23:59:59.999，所以按「当天结束」而不是「当天开始」比，宁可多留一天，不早删一条。
    /// <paramref name="now"/> 用写文件名时同一个本地时区。
    /// </remarks>
    public bool IsDayExpired(DateOnly day, DateTimeOffset now)
    {
        DateTimeOffset endOfDay = new(day.AddDays(1).ToDateTime(TimeOnly.MinValue), now.Offset);
        return endOfDay <= CutoffFor(now);
    }
}

/// <summary>
/// 技术日志文件的命名：<c>agv-yyyyMMdd.log</c>。保留期清理只认这一个形状。
/// </summary>
/// <remarks>
/// L2 与 G3 把 <c>logging.directory</c> 指到证据目录，那里同时放着 <c>control.err.log</c>、<c>runner.ps1</c>、
/// hmi#308 的状态时间线 <c>hmi-state-*.ndjson</c> 等。清理认错一个文件就是删掉别人的证据，所以这里是精确匹配，
/// 不是 <c>agv-*.log</c> 通配。
/// </remarks>
public static class OnboardTechnicalLogFileName
{
    private const string Prefix = "agv-";
    private const string Suffix = ".log";
    private const string DayFormat = "yyyyMMdd";

    public static string For(DateOnly day) =>
        Prefix + day.ToString(DayFormat, System.Globalization.CultureInfo.InvariantCulture) + Suffix;

    /// <summary>文件名是不是一份按天技术日志；是则给出它是哪一天。</summary>
    public static bool TryParseDay(string fileName, out DateOnly day)
    {
        day = default;
        if (fileName is null
            || fileName.Length != Prefix.Length + DayFormat.Length + Suffix.Length
            || !fileName.StartsWith(Prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return false;
        }

        string digits = fileName.Substring(Prefix.Length, DayFormat.Length);
        return digits.All(char.IsAsciiDigit)
            && DateOnly.TryParseExact(
                digits,
                DayFormat,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out day);
    }
}
