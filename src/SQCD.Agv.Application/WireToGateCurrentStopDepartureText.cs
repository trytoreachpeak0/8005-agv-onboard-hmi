using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// 「出发」的文案，唯一的一处（协议 4.0.0 候选，待送点 CP-0012、<c>8005-agv-onboard-hmi#349</c>，机台取货 CP-0014、
/// <c>8005-agv-onboard-hmi#352</c>）：确认框、按不了的原因、
/// 待处理、受理与拒绝，以及待送点摘要那一行。
/// </summary>
/// <remarks>
/// <para>
/// <b>屏上不写票号，不写「调度」。</b>解决不了的情况一律写「联系系统维护人员」，票号与原始码进日志（onboard-hmi#254 定的规矩）。
/// </para>
/// <para>
/// <b>拒绝原因表外的码原样显示</b>，与 <see cref="WireToGateStopEndedReasonText"/> 同一规矩：猜出来的意思比没有更糟，原始码至少能念给
/// 电话那头的人听。
/// </para>
/// <para>
/// <b>按下之后、结果回来之前，一直显示「出发请求待处理」</b>（protocol#13 审查 L4）：请求不带按键时刻也不带有效期，断线后补发的
/// 那一份仍是操作员这一次按键，服务端按它到达时本站的样子判。所以屏上不倒计时、不说「已失效」，只说在等。
/// </para>
/// </remarks>
public static class WireToGateCurrentStopDepartureText
{
    public const string ButtonText = "出发";

    public const string PendingText = "出发请求待处理，等待服务端答复";

    public const string AcceptedText = "服务端已受理出发请求，车辆按服务端安排离站";

    /// <summary>请求写到了链路上，但没等到服务端确认；车载端会按同一个编号自动重发。</summary>
    public const string NotAcknowledgedText = "出发请求已发出，还没收到服务端确认，系统会自动重发，请勿重复按。";

    /// <summary>请求已记下，但当前没有能送出去的连接；连接恢复后按同一个编号补发。</summary>
    public const string NotSentText = "出发请求已记下，连接恢复后自动补发，请勿重复按。";

    private const string ContactMaintainer = "如无法解决，请联系系统维护人员。";

    private static readonly IReadOnlyDictionary<string, string> RejectionTexts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["STATION_MISMATCH"] = "出发请求未被受理：车辆已不在按下「出发」时的那一站。请核对车辆位置与屏上清单。" + ContactMaintainer,
            ["WORKLIST_REVISION_STALE"] = "出发请求未被受理：按下时屏上的任务清单已经更新。请核对最新清单，仍需出发时重新按「出发」。",
            ["ACTION_NOT_ALLOWED_IN_STATE"] =
                "出发请求未被受理：当前状态不允许出发（本站还没装完、车辆已在离站、仓门操作进行中，或有纠错、取消未完成）。"
                + "请等当前操作结束后再按「出发」。" + ContactMaintainer
        };

    /// <summary>服务端拒绝时屏上那一句。表外的码原样带出，不编意思。</summary>
    public static string RejectionText(string? reasonCode) =>
        RejectionTexts.GetValueOrDefault(reasonCode ?? string.Empty)
            ?? $"出发请求未被受理（{reasonCode ?? "未说明原因"}）。" + ContactMaintainer;

    /// <summary>上一次按键现在的状况；没有时是空串。</summary>
    public static string StatusText(WireToGateCurrentStopDeparture? last) =>
        last switch
        {
            null => string.Empty,
            { Result: null } => PendingText,
            { Result.Outcome: WireToGateCurrentStopDepartureOutcome.Accepted } => AcceptedText,
            { Result: { } result } => RejectionText(result.ReasonCode)
        };

    /// <summary>给 UIA 的 <c>ItemStatus</c>：<c>PENDING</c>／<c>ACCEPTED</c>／拒绝原始码；没有时是空串。判据读它，不比中文全文。</summary>
    public static string Status(WireToGateCurrentStopDeparture? last) =>
        last switch
        {
            null => string.Empty,
            { Result: null } => "PENDING",
            { Result.Outcome: WireToGateCurrentStopDepartureOutcome.Accepted } => "ACCEPTED",
            { Result: { } result } => result.ReasonCode ?? WireToGateCurrentStopDepartureOutcome.Rejected
        };

    /// <summary>入口出现但按不了时旁边那一句；按得了或入口不出现时是空串。</summary>
    public static string UnavailableText(WireToGateCurrentStopDepartureUnavailability unavailability) =>
        unavailability switch
        {
            WireToGateCurrentStopDepartureUnavailability.SafetyFaultLatched => "严重安全故障锁存中，不能出发",
            WireToGateCurrentStopDepartureUnavailability.SessionNotReady => "与服务端的连接未就绪，暂不能出发",
            WireToGateCurrentStopDepartureUnavailability.NoOperator => "未登录操作员，不能出发",
            WireToGateCurrentStopDepartureUnavailability.EntryInProgress => "扫码已提交，等服务端答复后才能出发",
            WireToGateCurrentStopDepartureUnavailability.SlotOperationInProgress => "仓门操作进行中，结束后才能出发",
            WireToGateCurrentStopDepartureUnavailability.CorrectionOrCancellationOpen => "纠错或取消进行中，结束后才能出发",
            _ => string.Empty
        };

    /// <summary>
    /// 确认框正文：「已装 N 篮将出发送货；未装的 M 条退回待派」，一篮没装时写「车将留在待送点」（票面第 2 条）。
    /// </summary>
    /// <param name="loadedBasketCount">车上有货的仓位数，读自仓位读数。</param>
    /// <param name="unloadedItemCount">屏上清单还剩的待装条数。</param>
    public static string ConfirmationText(int loadedBasketCount, int unloadedItemCount)
    {
        string loaded = loadedBasketCount > 0
            ? $"已装 {loadedBasketCount} 篮将出发送货"
            : "本站一篮未装，车将留在待送点";
        string unloaded = unloadedItemCount > 0 ? $"；未装的 {unloadedItemCount} 条退回待派" : string.Empty;
        return $"{loaded}{unloaded}。\n\n退回待派的任务不会被取消，之后会重新派车。确认现在出发？";
    }

    /// <summary>
    /// 机台取货停靠的确认框正文（CP-0014，<c>8005-agv-onboard-hmi#352</c>）：本站已装完、按了立即离站、不再等「离站剩余」走完，
    /// 出发后不能再在本站纠错；后面有没装货的取货站点时说出个数，它们退回待派。机台上没有待送点摘要，篮数不在这里重复。
    /// </summary>
    /// <param name="pickupStopsAfter">后面还没装货的取货站点数，读自计划。</param>
    public static string MachineConfirmationText(int pickupStopsAfter)
    {
        string released = pickupStopsAfter > 0
            ? $"；后面还没装货的 {pickupStopsAfter} 个取货站点退回待派，不会取消，之后会重新派车"
            : string.Empty;
        return $"本站已装完。现在出发，车辆会立即离站送货，不再等「离站剩余」的时间走完{released}。\n\n"
            + "出发后不能再在本站纠错。确认现在出发？";
    }

    /// <summary>
    /// 待送点摘要那一行：已装篮数、前后仓剩余空仓、待装条数（票面第 5 条）。只显示，数据车上都有。仓位读数未知的不算空仓，
    /// 有未知时另写一句，免得把读不到的仓说成空仓。
    /// </summary>
    /// <summary>给 UIA 与屏幕状态快照的摘要原始值：同样五个数，不带中文（审查 L4）。</summary>
    public static string SummaryCode(
        int loadedBasketCount,
        int frontEmptyCount,
        int rearEmptyCount,
        int unknownSlotCount,
        int unloadedItemCount) =>
        $"LOADED={loadedBasketCount};FRONT_EMPTY={frontEmptyCount};REAR_EMPTY={rearEmptyCount};"
        + $"UNKNOWN={unknownSlotCount};PENDING={unloadedItemCount}";

    public static string SummaryText(
        int loadedBasketCount,
        int frontEmptyCount,
        int rearEmptyCount,
        int unknownSlotCount,
        int unloadedItemCount)
    {
        string unknown = unknownSlotCount > 0 ? $"，另有 {unknownSlotCount} 个仓状态未知" : string.Empty;
        return $"待送点：已装 {loadedBasketCount} 篮；空仓 前仓 {frontEmptyCount} 个、后仓 {rearEmptyCount} 个{unknown}；"
            + $"待装 {unloadedItemCount} 条";
    }
}
