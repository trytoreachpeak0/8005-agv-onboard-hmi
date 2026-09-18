using SQCD.Agv.Application;

namespace SQCD.Agv.Wpf;

/// <summary>
/// What the vehicle says once the server's station departure deadline has passed while the stop is
/// still waiting on someone (8005-agv-onboard-hmi#78).
/// </summary>
/// <remarks>
/// <para>
/// After the deadline a load keeps reopening a door shut over an empty slot (8005-agv-program#55), so
/// the only way to give the load up is the operator pressing cancel. With a door open or shut empty
/// the prompt line therefore names the door and the way out on every prompt round, and the countdown
/// line shows how long the stop is overdue instead of #75's generic "expired, waiting for the stop to
/// end" -- nothing ends the stop while the operator does nothing. The two are split because the
/// countdown box has a fixed width and the sentence does not fit it, while the prompt line wraps; the
/// overdue time belongs on the line that is redrawn from the clock.
/// </para>
/// <para>
/// A load cancellation that is open outranks that prompt: the control server neither starts a load
/// nor ends the stop by its deadline while one is open, and asking the operator to press cancel
/// again would be wrong. That line carries no overdue time (coordinator decision, 2026-09-18).
/// </para>
/// <para>
/// Only the text changes; the tier and its colour stay the deadline's, so nothing here extends or
/// voids the server's deadline.
/// </para>
/// </remarks>
public static class WireToGateStationDeadlineText
{
    public const string CancellingText = "已到期，正在取消本站装货";

    /// <summary>The prompt for a load past its deadline whose doors are open or were shut empty.</summary>
    public static string LoadPrompt(IReadOnlyList<int> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return $"请放入货物并关闭{string.Join("、", slots.Order())}号仓门；不装了请按取消。";
    }

    /// <summary>How long the stop is past its deadline, rounded down to the second.</summary>
    public static string Overdue(TimeSpan overdue) => $"已过期 {FormatOverdue(overdue)}";

    /// <summary>
    /// The countdown line's replacement, or <c>null</c> to keep the generic one: before the deadline,
    /// with no deadline, and past it with nothing waiting on the operator.
    /// </summary>
    /// <param name="loadAwaitingOperatorSlots">
    /// The slots of a load in flight whose doors are open or being reopened, or <c>null</c>.
    /// </param>
    public static string? CountdownOverride(
        StationDepartureCountdownContext context,
        bool cancellationOpen,
        IReadOnlyList<int>? loadAwaitingOperatorSlots)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Generic.Tier != StationDepartureCountdownTier.Expired
            || context.DeadlineAt is not { } deadline)
        {
            return null;
        }

        if (cancellationOpen)
        {
            return CancellingText;
        }

        return loadAwaitingOperatorSlots is { Count: > 0 }
            ? Overdue(context.Now - deadline)
            : null;
    }

    // Rounded down: "00:00 overdue" at the instant of expiry, never a second the stop has not yet run
    // over. Hours from the total, for the same reason the countdown formatter gives.
    private static string FormatOverdue(TimeSpan overdue)
    {
        TimeSpan whole = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, overdue.TotalSeconds)));
        return whole.TotalHours >= 1
            ? $"{(int)whole.TotalHours}:{whole.Minutes:D2}:{whole.Seconds:D2}"
            : $"{(int)whole.TotalMinutes:D2}:{whole.Seconds:D2}";
    }
}
