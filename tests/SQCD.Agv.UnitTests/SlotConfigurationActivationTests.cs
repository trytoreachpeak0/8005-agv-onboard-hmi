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

    [Fact]
    public void ActivationSwitchesTheWholeConfigurationAndTheFingerprintFollowsItImmediately()
    {
        Fixture fixture = new();
        string before = fixture.Store.Current.Fingerprint;

        SlotConfigurationActivationResult result = fixture.Coordinator.Activate(
            new SlotConfigurationActivationRequest("ACT-1", Configuration("v2", pulseMs: 500)));

        Assert.Equal(SlotConfigurationActivationStatus.Activated, result.Status);
        Assert.Equal("v2", fixture.Store.Current.ConfigurationVersion);
        Assert.NotEqual(before, fixture.Store.Current.Fingerprint);
        // 能力快照带的就是这个值：激活后立刻反映新配置，不是下一次会话才更新。
        Assert.Equal(fixture.Store.Current.Fingerprint, result.ResultingFingerprint);
        Assert.Equal(1, fixture.Coordinator.ActivationsPerformed);
    }

    [Fact]
    public void SlotStatesStayEightAndAConfigurationThatIsNotEightSlotsIsRefusedWithoutTouchingTheLiveOne()
    {
        Fixture fixture = new();
        ActiveSlotConfiguration original = fixture.Store.Current;
        Assert.Equal(8, original.Slots.Count);
        Assert.Equal(8, ActiveSlotConfiguration.RequiredSlotCount);

        ActiveSlotConfiguration sevenSlots = original with
        {
            ConfigurationVersion = "v2",
            Slots = [.. original.Slots.Take(7)]
        };
        SlotConfigurationActivationResult result = fixture.Coordinator.Activate(
            new SlotConfigurationActivationRequest("ACT-1", sevenSlots));

        Assert.Equal(SlotConfigurationActivationStatus.Rejected, result.Status);
        Assert.Equal("SLOT_COUNT_INVALID", result.ReasonCode);
        Assert.Equal(original, fixture.Store.Current);
        Assert.Equal(8, fixture.Store.Current.Slots.Count);
        Assert.Equal(0, fixture.Coordinator.ActivationsPerformed);
    }

    [Fact]
    public void AResultThatNeverReachedTheServerIsReplayedUnchangedAndTheActivationHappensOnlyOnce()
    {
        Fixture fixture = new();
        SlotConfigurationActivationRequest request =
            new("ACT-1", Configuration("v2", pulseMs: 500));

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
            new("ACT-1", Configuration("v2", pulseMs: 500));
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
            new SlotConfigurationActivationRequest("ACT-1", Configuration("v2", pulseMs: 500))));

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
            new SlotConfigurationActivationRequest("ACT-1", Configuration("v2", pulseMs: 500)));

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

    [Fact]
    public void TheFingerprintIsDerivedFromContentSoTwoIdenticalConfigurationsAgreeAndAnyChangeShows()
    {
        Assert.Equal(
            Configuration("v1", pulseMs: 500).Fingerprint,
            Configuration("v1", pulseMs: 500).Fingerprint);
        Assert.NotEqual(
            Configuration("v1", pulseMs: 500).Fingerprint,
            Configuration("v1", pulseMs: 400).Fingerprint);
        Assert.NotEqual(
            Configuration("v1", pulseMs: 500).Fingerprint,
            Configuration("v2", pulseMs: 500).Fingerprint);
    }

    private static ActiveSlotConfiguration Configuration(string version, int pulseMs) => new(
        "eight-slot-v1",
        version,
        [.. Enumerable.Range(1, 8).Select(number => new SlotConfigurationEntry(
            number,
            number <= 4 ? "LEFT" : "RIGHT",
            $"DO{number}",
            $"DI{number}",
            $"DI{number + 8}",
            "ACTIVE_HIGH",
            pulseMs))]);

    private sealed class Fixture
    {
        public Fixture(IAtomicDocument? document = null)
        {
            Store = new DocumentActiveSlotConfigurationStore(
                document ?? new FaultyDocument(), Configuration("v1", pulseMs: 500));
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
