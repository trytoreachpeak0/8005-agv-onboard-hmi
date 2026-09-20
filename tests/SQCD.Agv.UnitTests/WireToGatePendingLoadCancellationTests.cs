using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 待答取消记录（<see cref="WireToGatePendingLoadCancellation"/>）在日志里的形状。
/// </summary>
/// <remarks>
/// <para>
/// 这条记录存在的理由是：取消请求发出去却没等到应答时，重试必须逐字节重复首发的操作员与理由，
/// 否则服务端按 <c>cancellationId</c> 比对整个 payload 会判成另一个请求。进程内存撑不过重启，
/// 所以它得落到日志里。
/// </para>
/// <para>
/// <c>slotOperationAttemptId</c> 可空，是 批次5-27（onboard-hmi#76）的扫码前取消用的：那条路上
/// 还没有任何仓位操作，服务端自己的 payload 也允许这个字段为 null。
/// </para>
/// </remarks>
public sealed class WireToGatePendingLoadCancellationTests
{
    private const string CancellationId = "c7b1f2a4-9d3e-4c8a-8f52-0a1b2c3d4e5f";
    private const string AttemptId = "33333333-3333-4333-8333-333333333333";

    [Fact]
    public async Task APendingCancellationWithoutASlotOperationAttemptSurvivesTheJournal()
    {
        await using SqliteWireToGateJournal journal = await OpenJournalAsync();
        WireToGatePendingLoadCancellation pending = new(
            CancellationId,
            null,
            "operator-001",
            "SESSION",
            new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero),
            "扫码之前现场确认本站没有要装的货。");

        await journal.UpdateRecoveryStateAsync(
            _ => WireToGateRecoveryState.Empty with { PendingLoadCancellation = pending },
            TestContext.Current.CancellationToken);
        WireToGateRecoveryState read = await journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(pending, read.PendingLoadCancellation);
        Assert.Null(read.PendingLoadCancellation!.SlotOperationAttemptId);
    }

    /// <summary>
    /// 带 attempt 的那一半照旧：在途取消记的就是它，而这个字段仍按 UUID 校验。
    /// </summary>
    [Fact]
    public async Task APendingCancellationNamingASlotOperationAttemptKeepsIt()
    {
        await using SqliteWireToGateJournal journal = await OpenJournalAsync();
        WireToGatePendingLoadCancellation pending = new(
            CancellationId,
            AttemptId,
            "operator-001",
            "SESSION",
            new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero),
            "装载结果未知，现场申请取消。");

        await journal.UpdateRecoveryStateAsync(
            _ => WireToGateRecoveryState.Empty with { PendingLoadCancellation = pending },
            TestContext.Current.CancellationToken);
        WireToGateRecoveryState read = await journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(AttemptId, read.PendingLoadCancellation!.SlotOperationAttemptId);
    }

    /// <summary>
    /// 扫码前取消获得授权后落成的恢复向量没有仓位、没有 attempt，日志要能存下它，并连同待答记录一起读回：
    /// 待答记录留到结果得到确认才清。
    /// </summary>
    [Fact]
    public async Task AnAuthorizedCancellationBeforeAnySublotSurvivesTheJournalWithNoSlots()
    {
        await using SqliteWireToGateJournal journal = await OpenJournalAsync();
        WireToGateRecoveryVectorContext vector = BeforeSublotVector();
        WireToGatePendingLoadCancellation pending = new(
            CancellationId,
            null,
            "operator-001",
            "SESSION",
            new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero),
            "扫码之前现场确认本站没有要装的货。");

        await journal.UpdateRecoveryStateAsync(
            _ => WireToGateRecoveryState.Empty with
            {
                RecoveryVector = vector,
                PendingLoadCancellation = pending
            },
            TestContext.Current.CancellationToken);
        WireToGateRecoveryState read = await journal.ReadRecoveryStateAsync(
            TestContext.Current.CancellationToken);

        Assert.True(WireToGateRecoveryVectorTypes.IsLoadCancellationBeforeSublot(read.RecoveryVector!));
        Assert.Empty(read.RecoveryVector!.Slots);
        Assert.Equal(pending, read.PendingLoadCancellation);
    }

    /// <summary>
    /// 空仓位集合只放给扫码前取消：一个带 attempt 的取消向量没有仓位，日志照旧拒收。
    /// </summary>
    [Fact]
    public async Task AVectorNamingAnAttemptWithNoSlotsIsRefusedByTheJournal()
    {
        await using SqliteWireToGateJournal journal = await OpenJournalAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => journal.UpdateRecoveryStateAsync(
            _ => WireToGateRecoveryState.Empty with
            {
                RecoveryVector = BeforeSublotVector() with { SlotOperationAttemptId = AttemptId }
            },
            TestContext.Current.CancellationToken));
    }

    private static WireToGateRecoveryVectorContext BeforeSublotVector() =>
        new(
            WireToGateRecoveryVectorTypes.LoadCancellation,
            CancellationId,
            null,
            "11111111-1111-4111-8111-111111111111",
            null,
            null,
            [],
            null,
            "operator-001",
            "SESSION",
            new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero));

    private static async Task<SqliteWireToGateJournal> OpenJournalAsync()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "w2g-pending-cancellation",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        return journal;
    }
}
