using System.Globalization;
using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// 门未证明扣车的文案与被扣仓位，唯一的一处（CP-0009、REQ-0364，<c>8005-agv-onboard-hmi#219</c>；向量
/// <c>CV-LOAD-CANCELLATION-EMPTY-DOOR-UNPROVEN</c>、<c>CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN</c> 的
/// <c>DISPLAY_REPAIR_REQUIRED_NOTICE</c>，<c>CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE</c> 的 <c>DISPLAY_HOLD_FROM_BLOCKING_FACTS</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>扣车是服务端的，车载端只照着显示。</b>被扣仓位只从车辆业务快照的 <c>blockingFacts</c> 读：原因码
/// <c>SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY</c>、<c>subjectType</c> 为 <c>SLOT</c>、<c>subjectId</c> 是仓号（协议 wire-notes
/// 与服务端 <c>WireToGateStore</c> 同一个写法）。车载端日志不另记一份：两份会分叉，而解除扣车的只有服务端。
/// </para>
/// <para>
/// 断线时旅程投影被清空，这句话跟着消失；重连或重启后服务端重推业务快照，它又回来。这是「持久提示」的来源，
/// 不靠车载端自己记住。
/// </para>
/// </remarks>
public static class WireToGateDoorHoldText
{
    /// <summary>扣车的原因码，也是清空结果里门未证明那一仓带的原因码。</summary>
    public const string DoorLockUnprovenAfterEmptyReason = WireToGateRecoveryVectorExecutor.DoorLockUnprovenAfterEmptyReason;

    /// <summary>用户给的那一句（票面措辞）：仓空了，门锁没锁上，车要修好才能继续。</summary>
    public const string RepairRequiredNotice = "仓已确认无货，门锁未锁闭，本车需维修后才能继续。";

    /// <summary>服务端扣着的仓位，升序去重；没有业务状态或没有扣车时为空。</summary>
    public static IReadOnlyList<int> HeldSlots(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        return journey.VehicleBusinessState?.BlockingFacts is not { } facts
            ? []
            : facts
                .Where(fact => string.Equals(fact.ReasonCode, DoorLockUnprovenAfterEmptyReason, StringComparison.Ordinal)
                    && string.Equals(fact.SubjectType, "SLOT", StringComparison.Ordinal))
                .Select(fact => int.TryParse(
                    fact.SubjectId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int slot) && slot is >= 1 and <= 8
                        ? slot
                        : 0)
                .Where(slot => slot != 0)
                .Distinct()
                .Order()
                .ToArray();
    }

    /// <summary>
    /// 扣车提示；没有扣车时是空串。带这个原因码的事实只要有一条就提示，哪怕它的主体读不出仓号：扣车在服务端是真的，
    /// 说不出是哪几仓也不能因此不说。
    /// </summary>
    public static string Notice(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        if (journey.VehicleBusinessState?.BlockingFacts is not { } facts
            || !facts.Any(fact => string.Equals(fact.ReasonCode, DoorLockUnprovenAfterEmptyReason, StringComparison.Ordinal)))
        {
            return string.Empty;
        }

        IReadOnlyList<int> held = HeldSlots(journey);
        return held.Count == 0
            ? RepairRequiredNotice
            : $"{string.Join("、", held)}号仓：{RepairRequiredNotice}";
    }
}
