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
/// 各在各的那一行，不合成一个（REQ-0355：结束本站不等于车辆离开）。只有「最迟几点离站」取两者中更晚的那个
/// （onboard-hmi#310）：服务端在两者都过了才离站，这是显示时的取大，不改、不延长任何一个期限。剩余时间的取整照站点离站倒计时：向上取整到
/// 秒，00:00 不出现，到期有它自己的文案。
/// </para>
/// </remarks>
public static class LoadingPhaseText
{
    public const string CargoHoldingText = "等待更多任务";

    public const string CargoHoldingExpiredText = "已到期，等待服务端";

    /// <summary>
    /// <c>LOADING</c> 带持货期限时那一句的开头（待送点由用户 10-15 定，机台由 C11 同日定为同一句，<c>8005-agv-onboard-hmi#352</c>）：
    /// 还有任务没装、期限在走，到点带已装的货走，没装的放回待派。
    /// </summary>
    public const string TasksToScanText = "还有任务待扫码";

    private const string Leaving = "离站";

    private const string TasksToScanLeaving = "带已装的货离站";

    public const string VehicleFullText = "已装满，装完已承诺的任务后离站";

    private static readonly IReadOnlyDictionary<string, string> ClosedReasonTexts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WAITING_STATION_YIELD"] = "另一辆车需要本站，本车结束等单，前往卸货",
            ["CARGO_HOLDING_TIMEOUT"] = "等单已到期，本车结束等单，前往卸货",
            ["VEHICLE_FULL"] = "已装满，本站装货结束",
            ["PLANNED_LOADING_COMPLETE"] = "本站计划装货已完成",
            // 协议 4.0.0 新增（CP-0012、CP-0013，8005-agv-onboard-hmi#349）：待送点上操作员按了「出发」，或缺电时服务端
            // 让车带着已装的货自动出发，先送货、旅程结束后再去充电（protocol#15，旅程里不插充电停靠）。
            ["OPERATOR_DEPARTED"] = OperatorDepartedText,
            ["CHARGING_REQUIRED"] = ChargingRequiredText
        };

    /// <summary>待送点上操作员按「出发」、服务端受理后装货阶段的结束原因。</summary>
    public const string OperatorDepartedText = "已按出发，本站装货结束，前往送货";

    /// <summary>
    /// 待送点缺电、车上有货时装货阶段的结束原因：服务端让车自动出发，先送货，送完再充电（CP-0013，protocol#15）。
    /// </summary>
    public const string ChargingRequiredText = "电量低，先送车上的货，送完再充电";

    /// <param name="zone">「最迟几点离站」用的时区，界面传车载端本地时区。</param>
    /// <param name="stationDepartureDeadlineAt">本站清单的站点离站期限，没有时为空。</param>
    public static LoadingPhaseView Describe(
        WireToGateLoadingPhase? phase,
        DateTimeOffset now,
        TimeZoneInfo zone,
        DateTimeOffset? stationDepartureDeadlineAt = null)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return phase?.State switch
        {
            // 8005-agv-onboard-hmi#352（C11 定）：期限给了、装货阶段还开着，就倒计时。待送点有追加进来、还没扫的需求时服务端发的
            // 是 LOADING，期限照样在走，到期没扫的会被放回；屏上没有倒计时，客户就看不到。装满与结束不在此列：机台的期限进入
            // CLOSED 之后照旧下发（program#94 语义表：不清空），那时这一行说的是结束原因。
            "LOADING" when phase.CargoHoldingDeadlineAt is not null => CargoHolding(
                LatestDeparture(phase.CargoHoldingDeadlineAt, stationDepartureDeadlineAt),
                now,
                zone,
                TasksToScanText,
                TasksToScanLeaving),
            "CARGO_HOLDING_WAIT" => CargoHolding(
                LatestDeparture(phase.CargoHoldingDeadlineAt, stationDepartureDeadlineAt), now, zone, CargoHoldingText, Leaving),
            "VEHICLE_FULL" => new LoadingPhaseView(LoadingPhaseLine.VehicleFull, VehicleFullText, "VEHICLE_FULL"),
            // 入站校验保证 CLOSED 必带原因；这里仍不假设，空原因显示空串而不是编一个。
            "CLOSED" => new LoadingPhaseView(
                LoadingPhaseLine.Closed,
                ClosedReasonTexts.GetValueOrDefault(phase.ClosedReason ?? string.Empty, phase.ClosedReason ?? string.Empty),
                phase.ClosedReason ?? string.Empty),
            _ => new LoadingPhaseView(LoadingPhaseLine.None, string.Empty, string.Empty)
        };
    }

    /// <summary>
    /// The vehicle leaves at the later of the holding deadline and the station's (onboard-hmi#310): the server departs
    /// once the station departure wait is over and holding has ended, and ADR-cross-0057 counts the holding deadline from
    /// the journey's first LoadBatch, so with several batches at one stop it can come first. Without a holding deadline
    /// the wait has no departure time to report, and the station's is not borrowed for one.
    /// </summary>
    /// <remarks>Public so the screen-state snapshot reports the time this line shows, not a second computation of it (onboard-hmi#313).</remarks>
    public static DateTimeOffset? LatestDeparture(DateTimeOffset? holding, DateTimeOffset? station) =>
        holding is { } h && station is { } s && s > h ? s : holding;

    private static LoadingPhaseView CargoHolding(
        DateTimeOffset? deadlineAt,
        DateTimeOffset now,
        TimeZoneInfo zone,
        string lead,
        string leaving)
    {
        if (deadlineAt is not { } deadline)
        {
            return new LoadingPhaseView(LoadingPhaseLine.CargoHolding, lead, "NO_DEADLINE");
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
            $"{lead}，最迟 {latest} {leaving}（剩 {FormatRemaining(display)}）",
            "ACTIVE");
    }

    // 与 StationDepartureCountdownFormatter 同一写法：小时从总量算，超过一天的期限也不会显示错。
    private static string FormatRemaining(TimeSpan remaining) =>
        remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
            : $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
}
