namespace SQCD.Agv.Application;

/// <summary>
/// 倒计时的四个显示档位。颜色与闪烁由界面按档位选，这里只判档。
/// </summary>
public enum StationDepartureCountdownTier
{
    /// <summary>服务端没有给出截止时间——纯卸货站、关卡站，或超时被配置为禁用。</summary>
    Absent,

    /// <summary>剩余超过 60 秒。</summary>
    Normal,

    /// <summary>最后 60 秒。</summary>
    Warning,

    /// <summary>最后 10 秒。</summary>
    Critical,

    /// <summary>已经到期。ADR-cross-0058 决策 4 之后，到期不必然结束本站，车辆可能长时间停在这一档上。</summary>
    Expired
}

/// <param name="Text">直接显示给操作员的一行文字。</param>
/// <param name="Tier">配色与闪烁的依据。</param>
/// <param name="Dimmed">最后 10 秒逐秒闪烁时的「灭」相位；其余档位恒为 false。</param>
public sealed record StationDepartureCountdownView(
    string Text,
    StationDepartureCountdownTier Tier,
    bool Dimmed);

/// <summary>
/// 把服务端下发的本站截止时间（ADR-cross-0055 的实现映射一节：
/// <c>SublotWaitStartedAt + SublotWaitTimeout</c>）算成界面上的一行倒计时。
///
/// 三条要求来自 ADR-cross-0055 的 Consequences，其中后两条是 ADR-cross-0058 决策 4 之后新增的边界态：
///
/// <list type="bullet">
/// <item>最后 60 秒转黄，最后 10 秒转红并逐秒闪烁，不依赖声音设备。</item>
/// <item>截止时间缺席时显示「无倒计时」，**不是** 00:00——两者含义完全不同。</item>
/// <item>剩余归零后不显示负数，改显示「已到期，等待服务端结算」。决策 4 使期限到期不必然结束本站：
/// 仓门未闭时服务端只发告警并继续等，车辆会带着一个已耗尽的倒计时停很久。</item>
/// </list>
///
/// 这是个纯函数，每次都从绝对时刻重算，不持有任何递减状态——**同一次停靠内截止时间会往后跳**
/// （每批 LoadBatch 闭环后服务端重置计时并随下一份清单重发，断联也会先清空再重填），
/// 自己递减的实现会在那一刻显示错误的剩余时间。
/// </summary>
public static class StationDepartureCountdownFormatter
{
    private const string AbsentText = "无倒计时";
    private const string ExpiredText = "已到期，等待服务端结算";

    /// <summary>最后 60 秒转黄。</summary>
    private static readonly TimeSpan WarningThreshold = TimeSpan.FromSeconds(60);

    /// <summary>最后 10 秒转红并逐秒闪烁。</summary>
    private static readonly TimeSpan CriticalThreshold = TimeSpan.FromSeconds(10);

    public static StationDepartureCountdownView Format(DateTimeOffset? deadlineAt, DateTimeOffset now)
    {
        if (deadlineAt is not { } deadline)
        {
            return new StationDepartureCountdownView(AbsentText, StationDepartureCountdownTier.Absent, Dimmed: false);
        }

        TimeSpan remaining = deadline - now;
        if (remaining <= TimeSpan.Zero)
        {
            return new StationDepartureCountdownView(ExpiredText, StationDepartureCountdownTier.Expired, Dimmed: false);
        }

        // 向上取整到秒：剩 0.4 秒时显示 00:01 而不是 00:00。00:00 是个会被读成「已经到了」的数字，
        // 而到期有它自己的文案，两者不能混。
        TimeSpan display = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        StationDepartureCountdownTier tier = display <= CriticalThreshold
            ? StationDepartureCountdownTier.Critical
            : display <= WarningThreshold
                ? StationDepartureCountdownTier.Warning
                : StationDepartureCountdownTier.Normal;

        // 闪烁相位取自绝对秒数而不是刷新次数：界面的刷新节拍与秒边界不对齐，按刷新次数取反会在
        // 边界附近出现两次相同相位，看上去是卡住而不是闪烁。
        bool dimmed = tier == StationDepartureCountdownTier.Critical
            && Math.Abs(now.ToUnixTimeSeconds() % 2) == 1;

        return new StationDepartureCountdownView(FormatRemaining(display), tier, dimmed);
    }

    // 分钟位不用 TimeSpan 的自定义格式串：那里的 "h" 是排除天数之后的小时分量，
    // 一个超过一天的期限会被显示成个位小时数。这里一律从总量算。
    private static string FormatRemaining(TimeSpan remaining) =>
        remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
            : $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
}
