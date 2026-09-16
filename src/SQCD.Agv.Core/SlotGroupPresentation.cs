using System.Globalization;

namespace SQCD.Agv.Core;

/// <summary>
/// 仓位在整车上的哪一侧。只有 <see cref="Front"/> 与 <see cref="Rear"/> 是配置里认得的值。
/// </summary>
public enum SlotSide
{
    Front,
    Rear,
    Unknown
}

/// <summary>
/// 仓位区里的一组：组标题、组内仓号（升序）。
/// </summary>
public sealed record SlotGroup(SlotSide Side, string Title, IReadOnlyList<int> PhysicalSlotNumbers)
{
    /// <summary>这一组里有没有本次操作的目标仓。有就高亮组标题。</summary>
    public bool ContainsAnyOf(IEnumerable<int> targetSlotNumbers)
    {
        ArgumentNullException.ThrowIfNull(targetSlotNumbers);

        return targetSlotNumbers.Any(PhysicalSlotNumbers.Contains);
    }
}

/// <summary>
/// 一个没能归入前侧或后侧的仓。<see cref="SlotPosition"/> 为 <c>null</c> 表示生效配置里根本没有这个仓号。
/// </summary>
public sealed record UnrecognizedSlot(int PhysicalSlotNumber, string? SlotPosition);

/// <summary>
/// 一份生效配置分出来的全部组，以及进了「分组未知」组的那些仓。
/// </summary>
public sealed record SlotGroupLayout(
    IReadOnlyList<SlotGroup> Groups,
    IReadOnlyList<UnrecognizedSlot> UnrecognizedSlots)
{
    public SlotSide SideOfSlot(int physicalSlotNumber) =>
        Groups.FirstOrDefault(group => group.PhysicalSlotNumbers.Contains(physicalSlotNumber))?.Side
        ?? SlotSide.Unknown;
}

/// <summary>
/// 车载界面仓位区的前后两侧分组（批次4（第二版）-02，REQ-0349）。
/// </summary>
/// <remarks>
/// <para>
/// <b>分组只来自本机生效仓位配置的 <see cref="SlotConfigurationEntry.SlotPosition"/>，不按仓号或卡片下标推。</b>
/// 协议不带位置，服务端也不下发分组；车上能说清「哪几个仓在前侧」的只有这份配置。
/// </para>
/// <para>
/// 不认得的位置值（包括历史文档里残留的 <c>LEFT</c>／<c>RIGHT</c>）一律进「分组未知」组，不映射成已知组：
/// 猜错一侧会让操作员对着错的一排找货，比明说「不知道」更糟。
/// </para>
/// </remarks>
public static class SlotGroupPresentation
{
    public const string FrontPosition = "FRONT";

    public const string RearPosition = "REAR";

    private static readonly SlotSide[] DisplayOrder = [SlotSide.Front, SlotSide.Rear, SlotSide.Unknown];

    /// <summary>
    /// 按生效配置分组。组的顺序固定为前侧、后侧、分组未知；空组不出现。
    /// </summary>
    /// <remarks>
    /// 分的是界面上那 1～8 号八张卡，一张都不能丢：配置里缺了某个仓号，那个仓照样显示，放进「分组未知」。
    /// </remarks>
    public static SlotGroupLayout Create(ActiveSlotConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        (int Number, string? Position, SlotSide Side)[] slots = [.. Enumerable
            .Range(1, ActiveSlotConfiguration.RequiredSlotCount)
            .Select(number =>
            {
                string? position = configuration.Slots
                    .FirstOrDefault(slot => slot.PhysicalSlotNumber == number)?.SlotPosition;
                return (number, position, SideOfPosition(position));
            })];
        List<SlotGroup> groups = [];
        foreach (SlotSide side in DisplayOrder)
        {
            int[] numbers = [.. slots.Where(slot => slot.Side == side).Select(slot => slot.Number)];
            if (numbers.Length > 0)
            {
                groups.Add(new SlotGroup(side, GroupTitle(side, numbers), numbers));
            }
        }

        return new SlotGroupLayout(
            groups,
            [.. slots
                .Where(slot => slot.Side == SlotSide.Unknown)
                .Select(slot => new UnrecognizedSlot(slot.Number, slot.Position))]);
    }

    /// <summary>
    /// 分组，并为每个位置值不认得的仓写一条技术日志。
    /// </summary>
    public static SlotGroupLayout Create(ActiveSlotConfiguration configuration, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        SlotGroupLayout layout = Create(configuration);
        foreach (UnrecognizedSlot slot in layout.UnrecognizedSlots)
        {
            logger.Write(
                LogSeverity.Warning,
                nameof(SlotGroupPresentation),
                $"仓位分组未知：physicalSlotNumber={slot.PhysicalSlotNumber}，"
                + $"slotPosition={slot.SlotPosition ?? "（生效配置里没有这个仓号）"}，"
                + $"configurationVersion={configuration.ConfigurationVersion}。只认 {FrontPosition}／{RearPosition}，"
                + "该仓显示在「分组未知」组。");
        }
        return layout;
    }

    /// <summary>
    /// 仓位区顶部的「本次开门」标注。没有目标仓时返回空串。
    /// </summary>
    /// <remarks>
    /// 目标仓里有分组未知的仓时如实标出来，不把它算进任何一侧。
    /// </remarks>
    public static string OpeningSideText(SlotGroupLayout layout, IEnumerable<int> targetSlotNumbers)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(targetSlotNumbers);

        HashSet<SlotSide> sides = [.. targetSlotNumbers.Select(layout.SideOfSlot)];
        if (sides.Count == 0)
        {
            return string.Empty;
        }

        bool front = sides.Contains(SlotSide.Front);
        bool rear = sides.Contains(SlotSide.Rear);
        bool unknown = sides.Contains(SlotSide.Unknown);
        string known = front && rear ? "前后两侧" : front ? "前侧" : rear ? "后侧" : string.Empty;
        return known.Length == 0
            ? "本次开门：分组未知"
            : unknown
                ? $"本次开门：{known}（另有分组未知的仓位）"
                : $"本次开门：{known}";
    }

    /// <summary>
    /// 仓号号段：连续的写成「1～4」，不连续的用顿号隔开，例如「1～2、5 号」。
    /// </summary>
    public static string NumberRangeText(IReadOnlyList<int> physicalSlotNumbers)
    {
        ArgumentNullException.ThrowIfNull(physicalSlotNumbers);

        int[] numbers = [.. physicalSlotNumbers.Distinct().Order()];
        List<string> runs = [];
        int index = 0;
        while (index < numbers.Length)
        {
            int end = index;
            while (end + 1 < numbers.Length && numbers[end + 1] == numbers[end] + 1)
            {
                end++;
            }
            runs.Add(end == index
                ? numbers[index].ToString(CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{numbers[index]}～{numbers[end]}"));
            index = end + 1;
        }
        return $"{string.Join("、", runs)} 号";
    }

    private static SlotSide SideOfPosition(string? slotPosition) => slotPosition switch
    {
        FrontPosition => SlotSide.Front,
        RearPosition => SlotSide.Rear,
        _ => SlotSide.Unknown
    };

    private static string GroupTitle(SlotSide side, IReadOnlyList<int> numbers) => side switch
    {
        SlotSide.Front => $"前侧仓门（{NumberRangeText(numbers)}）",
        SlotSide.Rear => $"后侧仓门（{NumberRangeText(numbers)}）",
        _ => $"分组未知（{NumberRangeText(numbers)}）"
    };
}
