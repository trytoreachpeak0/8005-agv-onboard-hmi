using System.Globalization;
using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>持货等单那一行显示哪一种；<see cref="None"/> 时整行不出现。</summary>
public enum LoadingPhaseLine
{
    None,
    CargoHolding,
    VehicleFull,
    Closed
}

/// <param name="Line">显示哪一种。</param>
/// <param name="Text">给操作员的一句话。</param>
/// <param name="Code">
/// 给 UIA 的 <c>ItemStatus</c> 的原始值：等单时是 <c>ACTIVE</c>／<c>NO_DEADLINE</c>／<c>EXPIRED</c>，装满时是
/// <c>VEHICLE_FULL</c>，结束时是服务端的 <c>closedReason</c> 原始码。判据读它，不比中文全文。
/// </param>
public sealed record LoadingPhaseView(LoadingPhaseLine Line, string Text, string Code);

/// <summary>
/// 持货等单、装满与装货结束原因的文案，唯一的一处（批次7-13，<c>8005-agv-onboard-hmi#134</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>只显示，不判断。</b>按侧装满、持货等单与让站都在服务端判（规格第 5.1 节第 7、8、10 条），车载端读
/// <c>VehicleBusinessStateSnapshot.loadingPhase</c> 的整值：期限不推算、不延长，过了期限也不在本地判接下来发生
/// 什么，只说「已到期，等待服务端」。下一份快照的 <c>loadingPhase</c> 变了，这一行就整值跟着变。
/// </para>
/// <para>
/// 等单期限（<c>cargoHoldingDeadlineAt</c>）与站点离站期限（<c>stationDepartureDeadlineAt</c>）是两个期限，
/// 各在各的那一行，不合成一个（REQ-0355：结束本站不等于车辆离开）。剩余时间的取整照站点离站倒计时：向上取整到
/// 秒，00:00 不出现，到期有它自己的文案。
/// </para>
/// </remarks>
public static class LoadingPhaseText
{
    public const string CargoHoldingText = "等待更多任务";

    public const string CargoHoldingExpiredText = "已到期，等待服务端";

    public const string VehicleFullText = "已装满，装完已承诺的任务后离站";

    private static readonly IReadOnlyDictionary<string, string> ClosedReasonTexts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WAITING_STATION_YIELD"] = "另一辆车需要本站，本车结束等单，前往卸货",
            ["CARGO_HOLDING_TIMEOUT"] = "等单已到期，本车结束等单，前往卸货",
            ["VEHICLE_FULL"] = "已装满，本站装货结束",
            ["PLANNED_LOADING_COMPLETE"] = "本站计划装货已完成"
        };

    /// <param name="zone">「最迟几点离站」用的时区，界面传车载端本地时区。</param>
    public static LoadingPhaseView Describe(WireToGateLoadingPhase? phase, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return phase?.State switch
        {
            "CARGO_HOLDING_WAIT" => CargoHolding(phase.CargoHoldingDeadlineAt, now, zone),
            "VEHICLE_FULL" => new LoadingPhaseView(LoadingPhaseLine.VehicleFull, VehicleFullText, "VEHICLE_FULL"),
            // 入站校验保证 CLOSED 必带原因；这里仍不假设，空原因显示空串而不是编一个。
            "CLOSED" => new LoadingPhaseView(
                LoadingPhaseLine.Closed,
                ClosedReasonTexts.GetValueOrDefault(phase.ClosedReason ?? string.Empty, phase.ClosedReason ?? string.Empty),
                phase.ClosedReason ?? string.Empty),
            _ => new LoadingPhaseView(LoadingPhaseLine.None, string.Empty, string.Empty)
        };
    }

    private static LoadingPhaseView CargoHolding(DateTimeOffset? deadlineAt, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (deadlineAt is not { } deadline)
        {
            return new LoadingPhaseView(LoadingPhaseLine.CargoHolding, CargoHoldingText, "NO_DEADLINE");
        }

        TimeSpan remaining = deadline - now;
        if (remaining <= TimeSpan.Zero)
        {
            return new LoadingPhaseView(LoadingPhaseLine.CargoHolding, CargoHoldingExpiredText, "EXPIRED");
        }

        TimeSpan display = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        string latest = TimeZoneInfo.ConvertTime(deadline, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        return new LoadingPhaseView(
            LoadingPhaseLine.CargoHolding,
            $"{CargoHoldingText}，最迟 {latest} 离站（剩 {FormatRemaining(display)}）",
            "ACTIVE");
    }

    // 与 StationDepartureCountdownFormatter 同一写法：小时从总量算，超过一天的期限也不会显示错。
    private static string FormatRemaining(TimeSpan remaining) =>
        remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
            : $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
}
