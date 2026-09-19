using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <param name="Text">清单那一行显示的字。</param>
/// <param name="Code">
/// 给 UIA 的 <c>ItemStatus</c>：<c>FRONT</c>／<c>REAR</c>／<c>BOTH</c>／<c>UNKNOWN</c>／<c>UNASSIGNED</c>。
/// </param>
public sealed record WorklistItemSide(string Text, string Code);

/// <summary>
/// 清单项的侧（批次7-13，<c>8005-agv-onboard-hmi#134</c>；方向取自 <c>8005-agv-onboard-hmi#66</c> 第 5 条）。
/// </summary>
/// <remarks>
/// <para>
/// <b>侧只来自带这条需求 <c>demandId</c> 的仓位命令的 <c>slots</c></b>，经本机生效仓位配置
/// （<see cref="SlotGroupLayout.SideOfSlot"/>）分到前侧或后侧。协议的清单项不带仓位与侧，分侧也不进协议
/// （规格第 5.1 节第 7 条），所以命令到来之前车载端不知道这条需求在哪一侧——显示「待分配」，不从清单顺序、站名
/// 或花篮数推。
/// </para>
/// <para>
/// 来源的先后：本次运行收到的 <c>SlotOperationCommand</c> 最新；其次是日志里的 <c>OperationContext</c>（在途那一次），
/// 再次是 <c>LastCompletedLoadOperationContext</c>（本站上一次完成的装货）。后两者只读，是重启之后仍在的那份命令。
/// </para>
/// <para>
/// 目标仓里只要有一个分组未知，整条就显示「分组未知」，不挑已知的那一侧来说：猜错一侧会让操作员对着错的一排找货。
/// </para>
/// </remarks>
public static class WorklistItemSides
{
    public static readonly WorklistItemSide Unassigned = new("待分配", "UNASSIGNED");

    public static WorklistItemSide Resolve(
        string demandId,
        IReadOnlyDictionary<string, IReadOnlyList<int>> commandSlotsByDemand,
        WireToGateRecoveryState? journaled,
        SlotGroupLayout layout)
    {
        ArgumentNullException.ThrowIfNull(demandId);
        ArgumentNullException.ThrowIfNull(commandSlotsByDemand);
        ArgumentNullException.ThrowIfNull(layout);

        IReadOnlyList<int>? slots = commandSlotsByDemand.GetValueOrDefault(demandId)
            ?? SlotsOf(journaled?.OperationContext, demandId)
            ?? SlotsOf(journaled?.LastCompletedLoadOperationContext, demandId);
        if (slots is not { Count: > 0 })
        {
            return Unassigned;
        }

        HashSet<SlotSide> sides = [.. slots.Select(layout.SideOfSlot)];
        if (sides.Contains(SlotSide.Unknown))
        {
            return new WorklistItemSide("分组未知", "UNKNOWN");
        }

        bool front = sides.Contains(SlotSide.Front);
        bool rear = sides.Contains(SlotSide.Rear);
        return front && rear
            ? new WorklistItemSide("前后两侧", "BOTH")
            : front
                ? new WorklistItemSide("前侧", "FRONT")
                : new WorklistItemSide("后侧", "REAR");
    }

    private static IReadOnlyList<int>? SlotsOf(WireToGateRecoveryOperationContext? context, string demandId) =>
        context is not null && string.Equals(context.DemandId, demandId, StringComparison.Ordinal)
            ? context.Slots
            : null;
}
