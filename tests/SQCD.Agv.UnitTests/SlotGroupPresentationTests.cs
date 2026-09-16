using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 车载界面仓位区的前后两侧分组（批次4（第二版）-02，<c>trytoreachpeak0/8005-agv-onboard-hmi#66</c>）。
/// </summary>
/// <remarks>
/// 不挂 <c>IntegrationSlice</c> trait：这是纯界面呈现，不证明任何协议向量。
/// </remarks>
public sealed class SlotGroupPresentationTests
{
    [Fact]
    public void TheDefaultConfigurationShowsSlotsOneToFourAsFrontAboveFiveToEightAsRear()
    {
        SlotGroupLayout layout = SlotGroupPresentation.Create(Configuration(DefaultPositions));

        Assert.Collection(
            layout.Groups,
            front =>
            {
                Assert.Equal(SlotSide.Front, front.Side);
                Assert.Equal("前侧仓门（1～4 号）", front.Title);
                Assert.Equal([1, 2, 3, 4], front.PhysicalSlotNumbers);
            },
            rear =>
            {
                Assert.Equal(SlotSide.Rear, rear.Side);
                Assert.Equal("后侧仓门（5～8 号）", rear.Title);
                Assert.Equal([5, 6, 7, 8], rear.PhysicalSlotNumbers);
            });
        Assert.Empty(layout.UnrecognizedSlots);
    }

    /// <summary>
    /// 分组跟着配置走，不跟着仓号或卡片下标走：配置改成 1～2 前侧、3～8 后侧，组与号段随之变。
    /// </summary>
    [Fact]
    public void GroupsAndTheirNumberRangesFollowTheConfigurationRatherThanTheSlotIndex()
    {
        SlotGroupLayout layout = SlotGroupPresentation.Create(
            Configuration(number => number <= 2 ? "FRONT" : "REAR"));

        Assert.Equal(["前侧仓门（1～2 号）", "后侧仓门（3～8 号）"], layout.Groups.Select(group => group.Title));
        Assert.Equal([1, 2], layout.Groups[0].PhysicalSlotNumbers);
        Assert.Equal([3, 4, 5, 6, 7, 8], layout.Groups[1].PhysicalSlotNumbers);
    }

    [Fact]
    public void GroupOrderIsFrontThenRearEvenWhenTheConfigurationPutsRearFirst()
    {
        SlotGroupLayout layout = SlotGroupPresentation.Create(
            Configuration(number => number <= 4 ? "REAR" : "FRONT"));

        Assert.Equal(["前侧仓门（5～8 号）", "后侧仓门（1～4 号）"], layout.Groups.Select(group => group.Title));
    }

    [Theory]
    [InlineData("1,2,3,4", "1～4 号")]
    [InlineData("3", "3 号")]
    [InlineData("1,2,5", "1～2、5 号")]
    [InlineData("8,1,3,2", "1～3、8 号")]
    [InlineData("2,4,6", "2、4、6 号")]
    public void TheNumberRangeIsBuiltFromTheActualSlotNumbers(string numbers, string expected) =>
        Assert.Equal(expected, SlotGroupPresentation.NumberRangeText(Numbers(numbers)));

    /// <summary>
    /// 历史文档里残留的 <c>LEFT</c>／<c>RIGHT</c> 与任何别的值都不映射成已知组：进「分组未知」，并各记一条技术日志。
    /// </summary>
    [Theory]
    [InlineData("LEFT")]
    [InlineData("RIGHT")]
    [InlineData("front")]
    [InlineData("")]
    public void AnUnrecognizedPositionGoesToTheUnknownGroupAndIsLogged(string position)
    {
        RecordingLogger logger = new();

        SlotGroupLayout layout = SlotGroupPresentation.Create(
            Configuration(number => number == 3 ? position : DefaultPositions(number)),
            logger);

        Assert.Equal(["前侧仓门（1～2、4 号）", "后侧仓门（5～8 号）", "分组未知（3 号）"],
            layout.Groups.Select(group => group.Title));
        Assert.Equal(SlotSide.Unknown, layout.SideOfSlot(3));
        Assert.Equal([new UnrecognizedSlot(3, position)], layout.UnrecognizedSlots);
        LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogSeverity.Warning, entry.Severity);
        Assert.Contains("physicalSlotNumber=3", entry.Message, StringComparison.Ordinal);
        Assert.Contains($"slotPosition={position}，", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHistoricalLeftRightDocumentPutsEverySlotInTheUnknownGroup()
    {
        RecordingLogger logger = new();

        SlotGroupLayout layout = SlotGroupPresentation.Create(
            Configuration(number => number <= 4 ? "LEFT" : "RIGHT"),
            logger);

        SlotGroup group = Assert.Single(layout.Groups);
        Assert.Equal(SlotSide.Unknown, group.Side);
        Assert.Equal("分组未知（1～8 号）", group.Title);
        Assert.Equal(8, logger.Entries.Count);
    }

    /// <summary>
    /// 界面上八张卡一张都不能丢：配置里缺了的仓号照样显示，放进「分组未知」并记日志。
    /// </summary>
    [Fact]
    public void ASlotMissingFromTheConfigurationStillShowsInTheUnknownGroup()
    {
        RecordingLogger logger = new();
        ActiveSlotConfiguration configuration = Configuration(DefaultPositions);
        configuration = configuration with
        {
            Slots = [.. configuration.Slots.Where(slot => slot.PhysicalSlotNumber != 6)]
        };

        SlotGroupLayout layout = SlotGroupPresentation.Create(configuration, logger);

        Assert.Equal(Enumerable.Range(1, 8), layout.Groups.SelectMany(group => group.PhysicalSlotNumbers).Order());
        Assert.Equal([new UnrecognizedSlot(6, null)], layout.UnrecognizedSlots);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void ARecognizedConfigurationWritesNoTechnicalLog()
    {
        RecordingLogger logger = new();

        SlotGroupPresentation.Create(Configuration(DefaultPositions), logger);

        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData("2", "本次开门：前侧")]
    [InlineData("1,4", "本次开门：前侧")]
    [InlineData("7", "本次开门：后侧")]
    [InlineData("3,6", "本次开门：前后两侧")]
    [InlineData("", "")]
    public void TheOpeningSideIsDerivedFromTheTargetSlotsPositions(string targets, string expected)
    {
        SlotGroupLayout layout = SlotGroupPresentation.Create(Configuration(DefaultPositions));

        Assert.Equal(expected, SlotGroupPresentation.OpeningSideText(layout, Numbers(targets)));
    }

    [Fact]
    public void TheOpeningSideFollowsTheConfigurationNotTheSlotNumber()
    {
        SlotGroupLayout layout = SlotGroupPresentation.Create(
            Configuration(number => number <= 2 ? "FRONT" : "REAR"));

        Assert.Equal("本次开门：后侧", SlotGroupPresentation.OpeningSideText(layout, [3]));
    }

    [Fact]
    public void AnOpeningThatTouchesAnUnknownSlotSaysSoInsteadOfGuessing()
    {
        SlotGroupLayout layout = SlotGroupPresentation.Create(
            Configuration(number => number == 3 ? "LEFT" : DefaultPositions(number)));

        Assert.Equal("本次开门：分组未知", SlotGroupPresentation.OpeningSideText(layout, [3]));
        Assert.Equal("本次开门：前侧（另有分组未知的仓位）", SlotGroupPresentation.OpeningSideText(layout, [1, 3]));
    }

    [Fact]
    public void OnlyTheGroupsHoldingATargetSlotAreHighlighted()
    {
        SlotGroupLayout layout = SlotGroupPresentation.Create(Configuration(DefaultPositions));

        Assert.Equal([true, false], layout.Groups.Select(group => group.ContainsAnyOf([2])));
        Assert.Equal([false, true], layout.Groups.Select(group => group.ContainsAnyOf([5, 8])));
        Assert.Equal([true, true], layout.Groups.Select(group => group.ContainsAnyOf([4, 5])));
        Assert.Equal([false, false], layout.Groups.Select(group => group.ContainsAnyOf([])));
    }

    [Fact]
    public void TheLocalConfigurationFactoryProducesFrontForOneToFourAndRearForFiveToEight()
    {
        ActiveSlotConfiguration configuration =
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings());

        Assert.Equal(
            Enumerable.Range(1, 8).Select(DefaultPositions),
            configuration.Slots.OrderBy(slot => slot.PhysicalSlotNumber).Select(slot => slot.SlotPosition));
    }

    /// <summary>
    /// <c>LEFT</c>／<c>RIGHT</c> 改名为 <c>FRONT</c>／<c>REAR</c> 不动指纹：位置名不在摘要里，激活握手与
    /// <c>ONBOARD_HMI_G2</c> 证据都不受影响。
    /// </summary>
    [Fact]
    public void RenamingThePositionsLeavesTheFingerprintUnchanged()
    {
        ActiveSlotConfiguration renamed =
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings());
        // 另起一个实例而不是 with：with 会把已缓存的指纹一并拷过去，那样比的就不是两次独立计算。
        ActiveSlotConfiguration beforeRename = new(
            renamed.SlotModelVersion,
            renamed.ConfigurationVersion,
            [.. renamed.Slots.Select(slot => slot with
            {
                SlotPosition = slot.PhysicalSlotNumber <= 4 ? "LEFT" : "RIGHT"
            })]);

        Assert.Equal(beforeRename.Fingerprint, renamed.Fingerprint);
    }

    private static int[] Numbers(string commaSeparated) =>
        [.. commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];

    private static string DefaultPositions(int number) => number <= 4 ? "FRONT" : "REAR";

    private static ActiveSlotConfiguration Configuration(Func<int, string> position) => new(
        "eight-slot-v1",
        "v1",
        [.. Enumerable.Range(1, 8).Select(number => new SlotConfigurationEntry(
            number,
            position(number),
            $"DO{number}",
            $"DI{number}",
            $"DI{number + 8}",
            "ACTIVE_HIGH",
            500))]);

    private sealed class RecordingLogger : IAppLogger
    {
        public event EventHandler<LogEntryEventArgs>? EntryWritten;

        public List<LogEntry> Entries { get; } = [];

        public void Write(LogSeverity severity, string source, string message, Exception? exception = null)
        {
            LogEntry entry = new(DateTimeOffset.UnixEpoch, severity, source, message);
            Entries.Add(entry);
            EntryWritten?.Invoke(this, new LogEntryEventArgs(entry));
        }
    }
}
