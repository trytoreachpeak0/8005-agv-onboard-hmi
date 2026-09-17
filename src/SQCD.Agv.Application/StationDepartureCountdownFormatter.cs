namespace SQCD.Agv.Application;

/// <summary>
/// 离站期限倒计时的五个显示档位。颜色与闪烁由界面按档位选，这里只判档。
/// </summary>
public enum StationDepartureCountdownTier
{
    /// <summary>服务端没有给出截止时间——纯卸货站、关卡站，或超时被配置为禁用。</summary>
    Absent,

    /// <summary>剩余超过 60 秒。</summary>
    Normal,

    /// <summary>最后 60 秒，转黄。</summary>
    Warning,

    /// <summary>最后 10 秒，转红并逐秒闪烁。</summary>
    Critical,

    /// <summary>已经到期。到期后由服务端结束本站，车载端只等。</summary>
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
/// 交给覆盖文案的输入：服务端的截止时刻、算倒计时用的当前时刻，以及本类给出的通用显示。
/// </summary>
/// <remarks>
/// 覆盖入口留给 <c>8005-agv-onboard-hmi#78</c>（在途装货时期限到期的文案，要显示已过期多久），
/// 所以把截止时刻与当前时刻原样带上，而不是只给一段已经格式化好的文字。
/// </remarks>
public sealed record StationDepartureCountdownContext(
    DateTimeOffset? DeadlineAt,
    DateTimeOffset Now,
    StationDepartureCountdownView Generic);

/// <summary>
/// 把服务端快照里的 <c>stationDepartureDeadlineAt</c> 算成界面上的一行倒计时。
/// </summary>
/// <remarks>
/// <para>
/// 规则来自 ADR-cross-0055 的 Consequences：服务端给出截止时间时全程显示剩余时间，最后 60 秒转黄、
/// 最后 10 秒转红并逐秒闪烁，不依赖声音；截止时间为空显示「无倒计时」而不是 00:00；归零后不显示负数，
/// 显示「已到期，等待本站结束」，不说由谁结算。
/// </para>
/// <para>
/// 期限归服务端（批次5-10 算、批次5-22 下发），车载端不自算、不延长、不作废。这是个纯函数，每次都从
/// 绝对时刻重算，不持有递减状态：同一次停靠内期限会整体换掉（装货提交后重新计满、断联恢复后重新计满），
/// 自己递减的实现会在那一刻显示错误的剩余时间。
/// </para>
/// <para>
/// 剩余时间是「服务端时钟下的截止时刻」减「车载端时钟下的当前时刻」，所以显示的准确度依赖两端时钟的偏差。
/// v2 已有有界容差（<c>vehicleSafety.clockSkewToleranceMs</c>，默认 500 ms、上限 1000 ms，由 L2
/// <c>real-onboard-clock-skew</c> 钉住）；偏差超出容差时会话本身先进恢复。本类不改容差，也不做补偿——
/// 在容差内，显示最多差一秒左右。
/// </para>
/// </remarks>
public static class StationDepartureCountdownFormatter
{
    public const string AbsentText = "无倒计时";
    public const string ExpiredText = "已到期，等待本站结束";

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

        // 向上取整到秒：剩 0.4 秒时显示 00:01 而不是 00:00。00:00 会被读成「已经到了」，而到期有它自己的文案。
        TimeSpan display = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        StationDepartureCountdownTier tier = display <= CriticalThreshold
            ? StationDepartureCountdownTier.Critical
            : display <= WarningThreshold
                ? StationDepartureCountdownTier.Warning
                : StationDepartureCountdownTier.Normal;

        // 闪烁相位取自当前时刻的绝对秒数而不是刷新次数：刷新节拍与秒边界不对齐，按次数取反会在边界附近
        // 连着两次落在同一相位上，看着像卡住。
        bool dimmed = tier == StationDepartureCountdownTier.Critical
            && Math.Abs(now.ToUnixTimeSeconds() % 2) == 1;

        return new StationDepartureCountdownView(FormatRemaining(display), tier, dimmed);
    }

    // 不用 TimeSpan 的自定义格式串：那里的 "h" 是去掉天数后的小时分量，超过一天的期限会显示错。一律从总量算。
    private static string FormatRemaining(TimeSpan remaining) =>
        remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
            : $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
}
