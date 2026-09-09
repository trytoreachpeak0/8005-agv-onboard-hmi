namespace SQCD.Agv.Core;

/// <summary>
/// 技术日志覆盖的四类（REQ-0272）。
/// </summary>
public enum OnboardTechnicalLogCategory
{
    /// <summary>与 IO 模块的通信：连上、断开、读写往返。</summary>
    IoCommunication,

    /// <summary>信号变化：锁反馈、光幕、开锁输出。</summary>
    SignalChange,

    /// <summary>安全判定：出发前安全检查、仓位前置条件、急停。</summary>
    SafetyDecision,

    /// <summary>操作事件：操作员按了什么、扫了什么、服务端下发了什么。</summary>
    OperatorEvent
}

/// <summary>一条技术日志。</summary>
public sealed record OnboardTechnicalLogEntry(
    DateTimeOffset OccurredAt,
    OnboardTechnicalLogCategory Category,
    string Message);

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
}

/// <summary>
/// 车载技术日志：四类事件都记，过期之前一条都不清。
/// </summary>
public sealed class OnboardTechnicalLog(OnboardTechnicalLogRetentionPolicy? retentionPolicy = null)
{
    private readonly List<OnboardTechnicalLogEntry> _entries = [];

    public OnboardTechnicalLogRetentionPolicy RetentionPolicy { get; } =
        retentionPolicy ?? OnboardTechnicalLogRetentionPolicy.Default;

    public IReadOnlyList<OnboardTechnicalLogEntry> Entries => _entries;

    public void Append(OnboardTechnicalLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _entries.Add(entry);
    }

    public void Append(DateTimeOffset occurredAt, OnboardTechnicalLogCategory category, string message) =>
        Append(new OnboardTechnicalLogEntry(occurredAt, category, message));

    /// <summary>清掉已过保留期的条目，返回清掉几条。未过期的一条都不动。</summary>
    public int PurgeExpired(DateTimeOffset now)
    {
        DateTimeOffset cutoff = RetentionPolicy.CutoffFor(now);
        return _entries.RemoveAll(entry => entry.OccurredAt <= cutoff);
    }
}
