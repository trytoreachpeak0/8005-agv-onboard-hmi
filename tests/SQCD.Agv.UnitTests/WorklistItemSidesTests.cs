using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 清单项标侧（批次7-13，<c>8005-agv-onboard-hmi#134</c>；方向取自 <c>8005-agv-onboard-hmi#66</c> 第 5 条）：侧只来自
/// 带这条需求 <c>demandId</c> 的仓位命令的 <c>slots</c>，经本机生效仓位配置分到前侧或后侧。
/// </summary>
/// <remarks>
/// 来源依次是本次运行收到的 <c>SlotOperationCommand</c>、日志里的 <c>OperationContext</c>、日志里的
/// <c>LastCompletedLoadOperationContext</c>——后两者是重启之后仍在的那份命令。都没有时是「待分配」：侧不从清单顺序、
/// 站名或花篮数推，推错一侧会让操作员对着错的一排找货。
/// </remarks>
public sealed class WorklistItemSidesTests
{
    private const string DemandA = "aaaaaaaa-0000-4000-8000-00000000000a";
    private const string DemandB = "bbbbbbbb-0000-4000-8000-00000000000b";

    // Slots 1-4 front, 5-8 rear; slot 8 missing from the configuration, so its side is unknown.
    private static readonly SlotGroupLayout Layout = SlotGroupPresentation.Create(new ActiveSlotConfiguration(
        "eight-slot-v1",
        "test",
        [
            .. Enumerable.Range(1, 7).Select(number => new SlotConfigurationEntry(
                number,
                number <= 4 ? SlotGroupPresentation.FrontPosition : SlotGroupPresentation.RearPosition,
                $"DO{number}",
                $"DI{number}",
                $"DI{number + 8}",
                "ACTIVE_HIGH",
                500))
        ]));

    [Theory]
    [InlineData("1,2", "前侧", "FRONT")]
    [InlineData("5", "后侧", "REAR")]
    [InlineData("4,5", "前后两侧", "BOTH")]
    [InlineData("8", "分组未知", "UNKNOWN")]
    [InlineData("1,8", "分组未知", "UNKNOWN")]
    public void TheSideComesFromTheCommandsSlots(string slots, string text, string code)
    {
        WorklistItemSide side = WorklistItemSides.Resolve(
            DemandA,
            new Dictionary<string, IReadOnlyList<int>>
            {
                [DemandA] = [.. slots.Split(',').Select(slot => int.Parse(slot, System.Globalization.CultureInfo.InvariantCulture))]
            },
            null,
            Layout);

        Assert.Equal((text, code), (side.Text, side.Code));
    }

    /// <summary>
    /// 没有命令也没有日志时是「待分配」；另一条需求的命令不算这一条的。
    /// </summary>
    [Fact]
    public void WithoutACommandForThisDemandTheSideIsUnassigned()
    {
        WorklistItemSide side = WorklistItemSides.Resolve(
            DemandA,
            new Dictionary<string, IReadOnlyList<int>> { [DemandB] = [5] },
            WireToGateRecoveryState.Empty with { OperationContext = Context(DemandB, [6]) },
            Layout);

        Assert.Equal(("待分配", "UNASSIGNED"), (side.Text, side.Code));
    }

    [Fact]
    public void TheJournaledOperationContextGivesTheSideAfterARestart()
    {
        WorklistItemSide side = WorklistItemSides.Resolve(
            DemandA,
            new Dictionary<string, IReadOnlyList<int>>(),
            WireToGateRecoveryState.Empty with { OperationContext = Context(DemandA, [6, 7]) },
            Layout);

        Assert.Equal(("后侧", "REAR"), (side.Text, side.Code));
    }

    [Fact]
    public void TheJournaledLastCompletedLoadGivesTheSideAfterARestart()
    {
        WorklistItemSide side = WorklistItemSides.Resolve(
            DemandA,
            new Dictionary<string, IReadOnlyList<int>>(),
            WireToGateRecoveryState.Empty with { LastCompletedLoadOperationContext = Context(DemandA, [3]) },
            Layout);

        Assert.Equal(("前侧", "FRONT"), (side.Text, side.Code));
    }

    /// <summary>
    /// 本次运行收到的命令比日志里的旧命令新，先用它。
    /// </summary>
    [Fact]
    public void ACommandReceivedThisRunWinsOverTheJournal()
    {
        WorklistItemSide side = WorklistItemSides.Resolve(
            DemandA,
            new Dictionary<string, IReadOnlyList<int>> { [DemandA] = [2] },
            WireToGateRecoveryState.Empty with
            {
                OperationContext = Context(DemandA, [6]),
                LastCompletedLoadOperationContext = Context(DemandA, [7])
            },
            Layout);

        Assert.Equal(("前侧", "FRONT"), (side.Text, side.Code));
    }

    private static WireToGateRecoveryOperationContext Context(string demandId, int[] slots) => new(
        "00000000-0000-4000-8000-000000000001",
        null,
        1,
        DateTimeOffset.UnixEpoch,
        demandId,
        "77777777-7777-4777-8777-777777777777",
        "44444444-4444-4444-4444-444444444444",
        OperationType.Load,
        slots,
        1,
        true,
        new string('0', 64));
}
