using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The operator-facing words of the door-open banner (onboard-hmi#354): which door stands open, what to do, and --
/// once it has stood open past <see cref="EscalationDelay"/> -- that the vehicle is waiting on it and for how long.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> On 2026-10-15 two vehicles stood at an unload station for 15 and 20 minutes because a door was
/// left open. The screen did say so, in a 14px line of the prompt area and a 12px line on the slot card; nothing grew
/// louder until the six-minute expected-action alarm, and that one tells the operator somebody else will come.
/// </para>
/// <para>
/// The wording and the two-minute escalation are the user's of 2026-10-16 (through the coordinator); the delay is a
/// constant on purpose, not a setting. No ticket number and no word for the dispatch side appear in it: the operator
/// has neither. No sound either -- the user ruled it out.
/// </para>
/// </remarks>
public static class WireToGateDoorOpenPromptText
{
    /// <summary>How long a door may stand open before the banner turns red and says the vehicle is waiting.</summary>
    public static readonly TimeSpan EscalationDelay = TimeSpan.FromSeconds(120);

    public static string Headline(OperationType operationType, IReadOnlyList<int> openSlots) =>
        $"{Doors(openSlots)}开着：{(operationType == OperationType.Load ? "请放入篮子" : "请取出篮子")}，然后关好仓门";

    public static string WaitingDetail(OperationType operationType) =>
        operationType == OperationType.Load
            ? "门关好、车确认有货后，才算这一仓装完。"
            : "门关好、车确认仓空后，才算这一仓卸完。";

    public static string OverdueDetail(IReadOnlyList<int> openSlots, TimeSpan openFor) =>
        $"车在等关门：{Doors(openSlots)}已开 {(int)openFor.TotalMinutes} 分 {openFor.Seconds:00} 秒。门不关好，车不会继续。";

    private static string Doors(IReadOnlyList<int> slots) => string.Join("、", slots) + "号仓门";
}
