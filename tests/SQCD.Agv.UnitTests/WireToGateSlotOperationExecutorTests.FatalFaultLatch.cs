using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 锁存检查的位置：在 <c>SendProgress(UNLOCKING)</c> 之后、开锁之前（8005-agv-onboard-hmi#191，PR #198 审查低项 1）。
/// </summary>
/// <remarks>
/// <para>
/// 那一次进度发送要走网络，锁存可能恰好在它进行时发生——票面第 1 条的窗口就是这种形状。检查放在它前面，这个窗口就又开了。
/// G2 的锁存用例都在命令到达之前或门开着的时候锁存，挪动检查的位置它们照样全绿（审查实测），所以这一条断这个窗口本身。
/// </para>
/// <para>
/// 窗口是确定构造的：进度回调收到 <c>UNLOCKING</c> 时置上锁存，执行器从回调返回后才轮到检查。
/// </para>
/// </remarks>
public sealed partial class WireToGateSlotOperationExecutorTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ALatchArrivingWhileTheUnlockingProgressIsSentStopsThePulse()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), "w2g-executor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SimulationIo io = new();
        bool latched = false;
        await using SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
        await journal.InitializeAsync(token);
        await using WireToGateSlotOperationExecutor executor = new(
            io,
            journal,
            new SystemClock(),
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(1)),
            () => true,
            () => Volatile.Read(ref latched));

        WireToGateOperationExecutionResult result = await executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            (progress, _) =>
            {
                if (progress.Phase == "UNLOCKING")
                {
                    Volatile.Write(ref latched, true);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal(0, io.UnlockCount);
        Assert.Equal("FAILED", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults);
        Assert.Equal("NOT_STARTED", slot.Outcome);
        Assert.Equal([WireToGateSlotOperationExecutor.FatalFaultLatchedReason], slot.ReasonCodes);
    }
}
