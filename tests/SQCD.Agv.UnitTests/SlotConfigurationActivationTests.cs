using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 车载端仓位配置的原子激活、结果补报与能力快照指纹（<c>FP-IS-14</c> 车载半边的业务语义）。
/// </summary>
/// <remarks>
/// 这里固化的是语义，不是消息面。<c>SlotConfigurationActivationCommand</c> /
/// <c>SlotConfigurationActivationResult</c> 的传输与序列化属批次 2 轨 A，尚未落地；本类覆盖的是
/// 轨 A 到位后要接上的那一层，接线本身不在本票范围。
///
/// 不挂 <c>IntegrationSlice</c> trait：<c>FP-IS-14</c> 不在 vendored 的
/// <c>integration-slices/index.json</c> 里，标上去会让
/// <see cref="IntegrationSliceCoverageArchitectureTests"/> 的双向相等直接红。
/// </remarks>
public sealed class SlotConfigurationActivationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 指纹相等时激活成功，记下服务端对这一版的命名；硬件事实一个字节没动，所以指纹不变。
    /// </summary>
    /// <remarks>
    /// 消息 7 不带配置内容，所以「切换」不是把一份新配置装上去——车手上那份已经是对的，激活确认的是
    /// 「服务端批准的那一版就是你手上这份」。因此指纹不变是**正确**的：它变了才说明车换了硬件事实，
    /// 而车没有权力换。
    /// </remarks>
    [Fact]
    public void ActivationConfirmsTheHeldConfigurationAndRecordsTheServersVersionNameWithoutChangingTheFingerprint()
    {
        Fixture fixture = new();
        string before = fixture.Store.Current.Fingerprint;

        SlotConfigurationActivationResult result = fixture.Coordinator.Activate(
            new SlotConfigurationActivationRequest("ACT-1", "v2", before));

        Assert.Equal(SlotConfigurationActivationStatus.Activated, result.Status);
        Assert.Equal("v2", fixture.Store.Current.ConfigurationVersion);
        Assert.Equal(before, fixture.Store.Current.Fingerprint);
        // 能力快照带的就是这个值：激活后立刻反映当下的生效配置，不是下一次会话才更新。
        Assert.Equal(fixture.Store.Current.Fingerprint, result.ResultingFingerprint);
        Assert.Equal(1, fixture.Coordinator.ActivationsPerformed);
    }

    /// <summary>
    /// 指纹对不上就拒绝，生效配置一个字段不动。
    /// </summary>
    /// <remarks>
    /// 服务端批准的那一版与车手上这份不是同一份硬件事实。双方都不该让步：服务端改口就丢了权威，车改口
    /// 就是宣称自己装着从没收到过的东西——协议里根本没有一条消息能把配置内容送过来。拒绝，报向量点名
    /// 的那个稳定错误码，让人去查。
    /// </remarks>
    [Fact]
    public void AFingerprintThatDisagreesIsRejectedWithTheStableCodeAndTheLiveConfigurationIsUntouched()
    {
        Fixture fixture = new();
        ActiveSlotConfiguration original = fixture.Store.Current;

        SlotConfigurationActivationResult result = fixture.Coordinator.Activate(
            new SlotConfigurationActivationRequest("ACT-1", "v2", new string('b', 64)));

        Assert.Equal(SlotConfigurationActivationStatus.Rejected, result.Status);
        Assert.Equal(
            SlotConfigurationActivationCoordinator.FingerprintMismatchReasonCode,
            result.ReasonCode);
        // 报的是车此刻真正装着的那个指纹，不是服务端刚才说的那个——补报的价值就在于说出实情。
        Assert.Equal(original.Fingerprint, result.ResultingFingerprint);
        Assert.Equal(original, fixture.Store.Current);
        Assert.Equal(0, fixture.Coordinator.ActivationsPerformed);
    }

    [Fact]
    public void SlotStatesStayEightAndAConfigurationThatIsNotEightSlotsIsRefusedWithoutTouchingTheLiveOne()
    {
        Fixture fixture = new();
        ActiveSlotConfiguration original = fixture.Store.Current;
        Assert.Equal(8, original.Slots.Count);
        Assert.Equal(8, ActiveSlotConfiguration.RequiredSlotCount);

        // 本机手上那份就只有七个仓：不合法的配置绝不允许被认作生效版本，这条判断不需要服务端参与，
        // 也排在指纹核对之前——先说清「你手上这份本身就不成立」，比说「和我批准的那份对不上」准确。
        ActiveSlotConfiguration sevenSlots = original with { Slots = [.. original.Slots.Take(7)] };
        Fixture broken = new(initial: sevenSlots);
        SlotConfigurationActivationResult result = broken.Coordinator.Activate(
            new SlotConfigurationActivationRequest("ACT-1", "v2", sevenSlots.Fingerprint));

        Assert.Equal(SlotConfigurationActivationStatus.Rejected, result.Status);
        Assert.Equal("SLOT_COUNT_INVALID", result.ReasonCode);
        Assert.Equal(sevenSlots, broken.Store.Current);
        Assert.Equal(0, broken.Coordinator.ActivationsPerformed);
        // 那台正常的车不受影响。
        Assert.Equal(original, fixture.Store.Current);
        Assert.Equal(8, fixture.Store.Current.Slots.Count);
    }

    [Fact]
    public void AResultThatNeverReachedTheServerIsReplayedUnchangedAndTheActivationHappensOnlyOnce()
    {
        Fixture fixture = new();
        SlotConfigurationActivationRequest request =
            new("ACT-1", "v2", Configuration("v1", pulseMs: 500).Fingerprint);

        SlotConfigurationActivationResult first = fixture.Coordinator.Activate(request);
        // 结果没送达，服务端重连后重发同一条命令：PENDING_RESULT_REPLAY 补报同一个结果。
        SlotConfigurationActivationResult replayed = fixture.Coordinator.Activate(request);

        Assert.Equal(first, replayed);
        Assert.Equal(1, fixture.Coordinator.ActivationsPerformed);
    }

    [Fact]
    public void ReplayAfterARestartStillReportsTheSameResultRatherThanActivatingAgain()
    {
        FaultyDocument document = new();
        Fixture first = new(document);
        SlotConfigurationActivationRequest request =
            new("ACT-1", "v2", Configuration("v1", pulseMs: 500).Fingerprint);
        SlotConfigurationActivationResult original = first.Coordinator.Activate(request);

        // 进程重启：重新从同一份文档建起来。
        Fixture restarted = new(document);
        SlotConfigurationActivationResult replayed = restarted.Coordinator.Activate(request);

        Assert.Equal(original, replayed);
        Assert.Equal(0, restarted.Coordinator.ActivationsPerformed);
        Assert.Equal("v2", restarted.Store.Current.ConfigurationVersion);
    }

    [Theory]
    [InlineData(FaultyDocument.FaultPoint.BeforeWrite)]   // 断电：还没开始写
    [InlineData(FaultyDocument.FaultPoint.DuringWrite)]   // 崩溃：写了一半
    public void AFailureMidActivationLeavesTheOldConfigurationCompleteWithNoHalfSwitchedState(
        FaultyDocument.FaultPoint faultPoint)
    {
        FaultyDocument document = new();
        Fixture fixture = new(document);
        ActiveSlotConfiguration original = fixture.Store.Current;
        document.FailAt = faultPoint;

        Assert.Throws<IOException>(() => fixture.Coordinator.Activate(
            new SlotConfigurationActivationRequest("ACT-1", "v2", Configuration("v1", pulseMs: 500).Fingerprint)));

        // 进程内与磁盘上都是完整的旧版本，没有中间态。
        Assert.Equal(original, fixture.Store.Current);
        document.FailAt = FaultyDocument.FaultPoint.None;
        Fixture restarted = new(document);
        Assert.Equal(original, restarted.Store.Current);
        Assert.Null(restarted.Store.FindResult("ACT-1"));
    }

    [Fact]
    public void ADisconnectAfterActivationLosesNeitherTheConfigurationNorTheResult()
    {
        FaultyDocument document = new();
        Fixture fixture = new(document);
        SlotConfigurationActivationResult result = fixture.Coordinator.Activate(
            new SlotConfigurationActivationRequest("ACT-1", "v2", Configuration("v1", pulseMs: 500).Fingerprint));

        // 断线只是结果没送出去；本机状态已经落盘，重启后两半都在。
        Fixture restarted = new(document);
        Assert.Equal("v2", restarted.Store.Current.ConfigurationVersion);
        Assert.Equal(result, restarted.Store.FindResult("ACT-1"));
    }

    [Fact]
    public void TheRealFileSwapLeavesEitherTheOldOrTheNewDocumentAndNeverAHalfWrittenOne()
    {
        string root = Path.Combine(Path.GetTempPath(), "w2g-slotcfg-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "active-slot-configuration.json");
            AtomicJsonFile file = new(path);
            file.Commit("""{"first":true}""");

            // 上一次断电留下的临时文件：它从未被提交，重开时直接丢掉，正式文件不受影响。
            File.WriteAllText(path + ".pending", "{ truncated");
            AtomicJsonFile reopened = new(path);

            Assert.Equal("""{"first":true}""", reopened.ReadCommitted());
            Assert.False(File.Exists(path + ".pending"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TheRecoveryRoleIsTheNewSlotConfigurationOne()
    {
        Assert.Equal("SLOT_CONFIGURATION", SlotConfigurationActivationCoordinator.RecoveryRole);
    }

    /// <summary>
    /// 规范化摘要钉在一个固定值上，两个仓各钉一份同样的。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 消息 7 不带配置内容，激活是一次核验：服务端发它批准的那一版的指纹，车算自己手上那份的指纹，
    /// 相等才切换。两端的实现互相看不见——车载端在
    /// <see cref="ActiveSlotConfiguration"/>，控制服务端在
    /// <c>ControlServer.Domain.SlotConfigurationFingerprint</c>——所以「两边算法一致」这句话在任何
    /// 一个仓里都不可能靠对比来证。
    /// </para>
    /// <para>
    /// 固定值是唯一能证的形式：同一批输入，同一个字面量，两个仓各断言一次。哪一边改了规范化形式，
    /// 那一边当场变红，而不是等到现场那台车拒收激活的时候才发现。控制服务端那一份在
    /// <c>SlotConfigurationActivationTests.TheCanonicalFingerprintOfTheSharedExampleIsTheValueTheOnboardSideAlsoComputes</c>。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFingerprintMatchesTheValueTheControlServerComputes()
    {
        // 与控制服务端那份测试逐字段相同的一批输入：八个仓，DO{n}／DI{n}／DI{n+8}，ACTIVE_HIGH，500ms。
        Assert.Equal(
            "de93ca3d9eda7b619dd3ea2e8824f8592a3471b11ff723eba3dbc12ea6f69da9",
            Configuration("v1", pulseMs: 500).Fingerprint);
    }

    /// <summary>
    /// 指纹取自硬件事实，与版本名无关。
    /// </summary>
    /// <remarks>
    /// 协议 v2 的消息 7 不带配置内容，那次激活是一次核验：服务端发它批准的那一版的指纹，车算自己手上
    /// 那份的指纹，相等才切换。所以摘要只能取两端都有的东西——版本名是服务端自己的命名，车不知道；
    /// <c>SlotPosition</c> 是车本机的位置名，服务端没有这个概念。摘要把版本名算进去，两端就永远对不上。
    /// </remarks>
    [Fact]
    public void TheFingerprintIsDerivedFromTheHardwareFactsAloneAndNotFromTheVersionNames()
    {
        Assert.Equal(
            Configuration("v1", pulseMs: 500).Fingerprint,
            Configuration("v1", pulseMs: 500).Fingerprint);
        Assert.NotEqual(
            Configuration("v1", pulseMs: 500).Fingerprint,
            Configuration("v1", pulseMs: 400).Fingerprint);
        // 只有版本名不同：同一份硬件事实，同一个指纹。
        Assert.Equal(
            Configuration("v1", pulseMs: 500).Fingerprint,
            Configuration("v2", pulseMs: 500).Fingerprint);
        // 仓位位置名同理：它只在车上有，服务端算不出带着它的那个值。
        Assert.Equal(
            Configuration("v1", pulseMs: 500).Fingerprint,
            Configuration("v1", pulseMs: 500, slotPosition: "REAR").Fingerprint);
    }

    private static ActiveSlotConfiguration Configuration(
        string version,
        int pulseMs,
        string? slotPosition = null) => new(
        "eight-slot-v1",
        version,
        [.. Enumerable.Range(1, 8).Select(number => new SlotConfigurationEntry(
            number,
            slotPosition ?? (number <= 4 ? "LEFT" : "RIGHT"),
            $"DO{number}",
            $"DI{number}",
            $"DI{number + 8}",
            "ACTIVE_HIGH",
            pulseMs))]);

    private sealed class Fixture
    {
        public Fixture(IAtomicDocument? document = null, ActiveSlotConfiguration? initial = null)
        {
            Store = new DocumentActiveSlotConfigurationStore(
                document ?? new FaultyDocument(), initial ?? Configuration("v1", pulseMs: 500));
            Coordinator = new SlotConfigurationActivationCoordinator(
                Store, new FixedTimeProvider(Now));
        }

        public DocumentActiveSlotConfigurationStore Store { get; }
        public SlotConfigurationActivationCoordinator Coordinator { get; }
    }

    /// <summary>把断电与崩溃搬进单元测试的那份文档。</summary>
    public sealed class FaultyDocument : IAtomicDocument
    {
        public enum FaultPoint
        {
            None,
            BeforeWrite,
            DuringWrite
        }

        private string? _committed;

        public FaultPoint FailAt { get; set; }

        public string? ReadCommitted() => _committed;

        public void Commit(string content)
        {
            if (FailAt == FaultPoint.BeforeWrite)
            {
                throw new IOException("断电：还没开始写。");
            }
            if (FailAt == FaultPoint.DuringWrite)
            {
                // 真实实现写的是临时文件，正式文件此刻仍是完整的旧内容——这里同样不碰 _committed。
                throw new IOException("崩溃：临时文件写了一半。");
            }
            _committed = content;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
