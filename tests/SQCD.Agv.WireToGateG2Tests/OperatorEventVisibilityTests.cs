using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 钉住操作员提示的可见性：应答每次都要出，广播同一代内只出一次、换代要重新出。
///
/// 这条通道坏了不会发出任何噪音——去重把提示吞掉之后，界面上是彻底的沉默，没有报错、
/// 没有失败的测试，只有一个按下去没反应的按钮。所以它必须有测试替它出声。
/// </summary>
public sealed class OperatorEventVisibilityTests
{
    /// <summary>
    /// `recoveryResumeEnabled` 出厂为 false，操作员每按一次「申请恢复」都该被回答一次。
    /// 此前这句提示的去重键是常量 "recovery-disabled"，一个进程生命周期内只出现一次。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    public async Task DisabledRecoveryAnswersTheOperatorEveryTimeTheyAsk()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new();
        NullLogger logger = new();
        await using WireToGateSessionService session = CreateOfflineSession(io, logger);
        await using WireToGateBusinessService business = new(
            session,
            io,
            logger,
            new SystemClock(),
            () => true,
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(30)),
            "W2G_OPERATOR_EVENT_VISIBILITY_OPERATOR");

        List<WireToGateOperatorEvent> published = [];
        business.OperatorEventPublished += (_, args) => published.Add(args.Value);

        Assert.False(await business.RequestResumeAfterRepairAsync(cancellationToken: testToken));
        Assert.False(await business.RequestResumeAfterRepairAsync(cancellationToken: testToken));
        Assert.False(await business.RequestResumeAfterRepairAsync(cancellationToken: testToken));

        Assert.Equal(3, published.Count);
        Assert.All(published, item => Assert.Equal("RECOVERY_BLOCKED", item.Kind));
    }

    /// <summary>
    /// 广播照旧去重：服务端重放同一条命令时不该把同一句话推很多遍。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    public void BroadcastKeepsDeduplicatingWithinOneGeneration()
    {
        OperatorEventDeduplicator deduplicator = new();
        deduplicator.ResetOnNewGeneration(1);

        Assert.True(deduplicator.ShouldPublish("operation-replay:attempt-1"));
        Assert.False(deduplicator.ShouldPublish("operation-replay:attempt-1"));
        Assert.True(deduplicator.ShouldPublish("operation-replay:attempt-2"));
    }

    /// <summary>
    /// 换代之后同一个广播键要重新发得出去。服务端重启后的新会话会重放它认为车载端
    /// 可能没收到的命令，此时沉默是错的。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    public void BroadcastRepeatsAfterTheSessionGenerationChanges()
    {
        OperatorEventDeduplicator deduplicator = new();
        deduplicator.ResetOnNewGeneration(1);
        Assert.True(deduplicator.ShouldPublish("recovery-operation-restored:attempt-1"));

        deduplicator.ResetOnNewGeneration(1);
        Assert.False(deduplicator.ShouldPublish("recovery-operation-restored:attempt-1"));

        deduplicator.ResetOnNewGeneration(2);
        Assert.True(deduplicator.ShouldPublish("recovery-operation-restored:attempt-1"));
    }

    /// <summary>
    /// 会话未建立时代号是 null，那不是一次换代，不该清空。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    public void UnchangedGenerationLeavesTheBroadcastKeysAlone()
    {
        OperatorEventDeduplicator deduplicator = new();

        Assert.True(deduplicator.ShouldPublish("sublot-requested:message-1"));
        deduplicator.ResetOnNewGeneration(null);
        Assert.False(deduplicator.ShouldPublish("sublot-requested:message-1"));
    }

    private static WireToGateSessionService CreateOfflineSession(
        IIoModuleClient io,
        IAppLogger logger)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "w2g-operator-events",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        // 端口 1 上没有服务端，本用例也从不连接：`recoveryResumeEnabled` 为 false 的短路
        // 分支在读会话状态之前就返回了。
        return new WireToGateSessionService(
            new WireToGateSessionOptions(
                "127.0.0.1",
                1,
                "AGV-8005-01",
                Guid.NewGuid().ToString("D"),
                new string('a', 40),
                "W2G_OPERATOR_EVENT_VISIBILITY_CREDENTIAL",
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2),
                1,
                1,
                "eight-slot-v1",
                "eight-slot-modbus-v1",
                SupportsBatchUnlock: false),
            io,
            new SqliteWireToGateJournal(Path.Combine(directory, "journal.db")),
            logger,
            new SystemClock(),
            new AlwaysStoppedVehicle(),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));
    }

    private sealed class AlwaysStoppedVehicle : IVehicleSafetySignalProvider
    {
        public VehicleSafetySignal Read() => new(
            VehicleMotionState.Stopped,
            DateTimeOffset.UtcNow,
            "OPERATOR_EVENT_VISIBILITY_TEST");
    }

    private sealed class NullLogger : IAppLogger
    {
        public event EventHandler<LogEntryEventArgs>? EntryWritten;

        public void Write(
            LogSeverity severity,
            string source,
            string message,
            Exception? exception = null)
        {
            _ = severity;
            _ = source;
            _ = message;
            _ = exception;
            _ = EntryWritten;
        }
    }
}
