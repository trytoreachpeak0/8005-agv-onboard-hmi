using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 占用冲突被拒的结果，在「记为待答」之前被下一次操作的 Prepared 盖掉日志，也仍然要发出去
/// （<c>trytoreachpeak0/8005-agv-onboard-hmi#172</c>）。
/// </summary>
/// <remarks>
/// <para>
/// #172 之前冲突不落日志、checkpoint 为 <c>NONE</c>，业务服务根本不调 <c>RecordPendingResultAsync</c>，结果一定发得出去。
/// #172 让冲突以 <c>SafeFinishReached</c> 落日志后，冲突也走「先释放显示闸、再记待答、再发送」这条路：同一站排在后面的
/// 命令可以在前两步之间拿到执行器、写下自己的 Prepared，记待答就在日志锁内被拒（<c>SLOT_OPERATION_CONFLICT</c>）。
/// 那个异常原先一路抛到 <c>HandleCommandAsync</c> 的兜底 catch，只落一条 Error 日志，结果没有写进发件箱，之后也没有
/// 任何东西会再发它——服务端永远收不到这次 attempt 的结论。
/// </para>
/// <para>
/// 窗口用 <see cref="NextOperationJournal"/> 确定性地造出来：它在 <c>RecordPendingResultAsync</c> 那一次日志写入之前
/// 先写下另一次 attempt 的 Prepared，正是排队命令恰好在那一刻开始时日志的样子。不靠时序碰运气。
/// 日志被盖掉这件事本身（前一张的恢复入口因此找不到它）是 onboard-hmi#182，不在这里断言。
/// </para>
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ARefusedLoadWhoseJournalIsOverwrittenBeforeItIsRecordedPendingStillSendsItsResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        NextOperationJournal? journal = null;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        // The one-slot load the double sends finds its slot already holding a basket.
        io.SetCargoPresent(0, true);
        await using Harness harness = await Harness.StartAsync(
            io,
            token,
            wrapJournal: inner => journal = new NextOperationJournal(inner, "RecordPendingResultAsync"));

        await Harness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(item => item.MessageType == "OperationResult"),
            "the refused load's OperationResult to reach the server",
            token,
            () => string.Join(Environment.NewLine, harness.Logger.Entries.Select(entry => entry.Message)));

        string sent = harness.Server.ReceivedEnvelopes.First(item => item.MessageType == "OperationResult").WireLine;
        using (System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(sent))
        {
            System.Text.Json.JsonElement payload = document.RootElement.GetProperty("payload");
            Assert.Equal(AttemptId, payload.GetProperty("slotOperationAttemptId").GetString());
            Assert.Equal("FAILED", payload.GetProperty("overallOutcome").GetString());
            Assert.Equal("SAFE_FINISH_REACHED", payload.GetProperty("journalCheckpoint").GetString());
        }

        // The window was really opened: the overwrite landed, and the journal names the other attempt.
        Assert.True(journal!.NextOperationWritten);
        Assert.Equal(NextAttemptId, harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId);
        Assert.Equal(0, io.UnlockCount);

        // And it is said for what it is: a warning naming both attempts, not the catch-all error.
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Severity == LogSeverity.Warning
                && entry.Message.Contains(AttemptId, StringComparison.Ordinal)
                && entry.Message.Contains(NextAttemptId, StringComparison.Ordinal));
        Assert.DoesNotContain(
            harness.Logger.Entries,
            entry => entry.Message.Contains("处理服务端业务消息失败", StringComparison.Ordinal));
    }
}
