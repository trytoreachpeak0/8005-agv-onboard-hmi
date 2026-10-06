using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 判故障应答被服务端以内容冲突码拒收、车载端已按 hmi#254 放弃那一行之后，服务端再发同一个 <c>declarationId</c> 的
/// <c>SlotFaultDeclarationCommand</c>：车载端不再发那份应答，改回一条关联到这条命令的
/// <c>ProtocolProblem(SLOT_OPERATION_CONFLICT)</c>，会话不断开（<c>trytoreachpeak0/8005-agv-onboard-hmi#266</c>）。
/// 服务端据此把声明转为 <c>UNRECONCILED</c>（control-server#481 第二部分）。
/// </summary>
public sealed partial class StationDeadlineExpiredG2Tests
{
    private const string SlotFaultDeclarationCommandFailure = "处理服务端业务消息失败：SlotFaultDeclarationCommand";

    /// <summary>
    /// 应答首发即被拒收：车载端放弃这一行，当场用手边这条命令回拒，不等下一次重连的重放（服务端只在会话开始时重放）。
    /// 判定已写进日志、执行器已中止，所以判定生效的日志与 <c>SLOT_FAULT_DECLARED</c> 照常出现，<c>OperationResult</c> 照常报
    /// 2 号仓 UNKNOWN；拒收不落进命令处理的兜底。
    /// </summary>
    /// <remarks>
    /// 先红：改前首发的拒收以 <c>InvalidDataException(BUSINESS_ID_CONTENT_CONFLICT)</c> 越过应答发送，落进
    /// <c>HandleCommandAsync</c> 的兜底，判定生效日志与事件都被跳过，命令也无人回答。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task AnAnswerRefusedOnItsFirstSendIsGivenUpAndItsCommandRefusedOnTheSpotWhileTheDeclarationApplies()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartForDeclarationAsync(token, configure: RefuseDeclarationAnswers);
        string command = await DeclareAndHaveTheAnswerGivenUpAsync(harness, token);

        Assert.Single(
            harness.Server.ReceivedEnvelopes,
            item => item.MessageType == "ProtocolProblem" && IsDeclarationRefusal(item.WireLine, command));
        // Written once DeclareSlotFaultAsync has seen the run stop, which the OperationResult does not wait for.
        await Harness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(
                entry => entry.Message.StartsWith("已执行服务端的人工判故障", StringComparison.Ordinal)),
            "the applied declaration to be logged",
            token,
            () => DescribeDeclarationHandling(harness));
        await harness.WaitForEventAsync("SLOT_FAULT_DECLARED", token);
        Assert.True(harness.HasEvent("DURABLE_MESSAGE_ABANDONED"));
        JsonElement result = harness.SingleResult("OperationResult");
        AssertWireSlot(result, 2, "UNKNOWN", ["SLOT_FAULT_DECLARED"]);
        AssertAnswerNotResentAndSessionKept(harness);
    }

    /// <summary>
    /// 路径一：会话就绪时重发。应答首发即被拒收、放弃；服务端重放命令时，改前走
    /// <c>ResendSlotFaultDeclarationResultAsync</c> → <c>StoreDurableAsync</c> 抛 <c>DURABLE_MESSAGE_ABANDONED</c>，落进兜底，不作回答。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task AReplayedDeclarationWhoseAnswerWasGivenUpIsRefusedWithoutResendingTheAnswer()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartForDeclarationAsync(token, configure: RefuseDeclarationAnswers);
        await DeclareAndHaveTheAnswerGivenUpAsync(harness, token);

        string replay = Guid.NewGuid().ToString("D");
        await SendDeclarationCommandAsync(harness, replay);
        await WaitForDeclarationRefusalAsync(harness, replay, 1, token);

        // Refused off the outbox mark, before any resend is tried (A): the resend path would log its intent first. Without
        // A, B and the given-up catch still refuse the replay, and only this tells the two apart (review of PR #268).
        Assert.DoesNotContain(
            harness.Logger.Entries,
            entry => entry.Message.StartsWith("收到重复的SlotFaultDeclarationCommand", StringComparison.Ordinal));
        AssertAnswerNotResentAndSessionKept(harness);
    }

    /// <summary>
    /// 路径二：会话没就绪时。重放的命令到了、处理到读发件箱那一步时连接断了。
    /// </summary>
    /// <remarks>
    /// 改前走 <c>SendDurableCoreAsync</c> 的未就绪分支，以 <c>rebind:false</c> 存盘时抛 <c>DURABLE_MESSAGE_ABANDONED</c>，落进兜底。
    /// 改后走不到那一支：读到的那一行已放弃，A 直接回拒；连接已经不在，回拒发不出去，只记日志（D）。重连后服务端再重放，
    /// 这一次回拒到达。所以这条用例测的是「A 拦下、回拒发不出去由 D 记日志、下一次会话再回」。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task AReplayHandledAfterTheConnectionWentIsRefusedOnTheNextSession()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        AnswerReadHoldJournal? hold = null;
        await using Harness harness = await StartForDeclarationAsync(
            token,
            configure: RefuseDeclarationAnswers,
            wrapJournal: inner => hold = new AnswerReadHoldJournal(
                inner,
                WireToGateSessionClient.SlotFaultDeclarationResultKey(FirstDeclarationId)));
        await DeclareAndHaveTheAnswerGivenUpAsync(harness, token);

        string replay = Guid.NewGuid().ToString("D");
        hold!.HoldNextRead();
        await SendDeclarationCommandAsync(harness, replay);
        await hold.Held.WaitAsync(token);
        await harness.Client.DisconnectAsync();
        hold.Release();

        await harness.Client.ConnectAndRecoverAsync(token);
        await SendDeclarationCommandAsync(harness, replay);
        await WaitForDeclarationRefusalAsync(harness, replay, 1, token);

        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Message.StartsWith("判故障应答已被服务端拒收，但这条命令的回拒未能发出", StringComparison.Ordinal));
        Assert.NotEqual(
            harness.Server.ReceivedEnvelopes.Single(item => item.MessageType == "SlotFaultDeclarationResult").Connection,
            harness.Server.ReceivedEnvelopes.Single(item => IsDeclarationRefusal(item.WireLine, replay)).Connection);
        AssertAnswerNotResentAndSessionKept(harness);
    }

    /// <summary>
    /// 路径三：应答先被确认、后被放弃（服务端库被替换后，对已确认应答的重放回内容冲突）。改前
    /// <c>ResendSlotFaultDeclarationResultAsync</c> 的已确认分支不看放弃标记，每次重放命令都把放弃的那一行再发一遍。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task AnAcknowledgedAnswerGivenUpLaterIsNeverResentAndTheReplayIsRefused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartForDeclarationAsync(token);
        await StartThreeSlotLoadAtSlotTwoAsync(harness, token);
        await SendDeclarationCommandAsync(harness, Guid.NewGuid().ToString("D"));
        await harness.WaitForInboundAsync("OperationResult", token);
        await WaitForAckedAsync(harness, FirstDeclarationId, token);

        RefuseDeclarationAnswers(harness.Server);
        string refusedReplay = Guid.NewGuid().ToString("D");
        await SendDeclarationCommandAsync(harness, refusedReplay);
        await WaitForAnswerGivenUpAsync(harness, token);
        WireToGateDurableMessage row = await ReadDeclarationAnswerAsync(harness, token);
        Assert.True(row.Acknowledged && row.Abandoned);
        // Given up without anything thrown -- the acknowledged answer's replay swallows the refusal -- and still refused
        // on the spot.
        await WaitForDeclarationRefusalAsync(harness, refusedReplay, 1, token);
        int answersOnTheWire = CountOnTheWire(harness, "SlotFaultDeclarationResult");
        Assert.Equal(2, answersOnTheWire);

        string replay = Guid.NewGuid().ToString("D");
        await SendDeclarationCommandAsync(harness, replay);
        await WaitForDeclarationRefusalAsync(harness, replay, 1, token);

        Assert.Equal(answersOnTheWire, CountOnTheWire(harness, "SlotFaultDeclarationResult"));
        Assert.True(harness.Client.Current.Connected);

        // B on its own: A refuses every replay before a resend is tried, so only a direct call reaches the check that keeps
        // an answer acknowledged and then given up off the wire (review of PR #268).
        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(
            () => harness.Client.ResendSlotFaultDeclarationResultAsync(FirstDeclarationId, token));
        Assert.Equal("DURABLE_MESSAGE_ABANDONED", refused.Message);
        Assert.Equal(answersOnTheWire, CountOnTheWire(harness, "SlotFaultDeclarationResult"));
    }

    /// <summary>
    /// 回拒丢了：服务端没处理那条 <c>ProtocolProblem</c>（不持久），声明仍待答。重连后服务端以同一个 messageId 再重放，
    /// 车载端再回一次，应答始终没有再发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task ALostRefusalIsGivenAgainWhenTheReplayComesBackAfterAReconnect()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartForDeclarationAsync(token, configure: RefuseDeclarationAnswers);
        await DeclareAndHaveTheAnswerGivenUpAsync(harness, token);

        string replay = Guid.NewGuid().ToString("D");
        await SendDeclarationCommandAsync(harness, replay);
        await WaitForDeclarationRefusalAsync(harness, replay, 1, token);

        await harness.Client.DisconnectAsync();
        await harness.Client.ConnectAndRecoverAsync(token);
        await SendDeclarationCommandAsync(harness, replay);
        await WaitForDeclarationRefusalAsync(harness, replay, 2, token);

        int[] connections =
        [
            .. harness.Server.ReceivedEnvelopes
                .Where(item => IsDeclarationRefusal(item.WireLine, replay))
                .Select(item => item.Connection)
                .Distinct()
        ];
        Assert.Equal(2, connections.Length);
        AssertAnswerNotResentAndSessionKept(harness);
    }

    private static void RefuseDeclarationAnswers(FakeControlServer server) =>
        server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SlotFaultDeclarationResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
        };

    /// <summary>
    /// The three-slot load waits on slot 2, the declaration arrives, its APPLIED answer is refused and given up, the
    /// command is refused on the spot, and the run it stopped reports its UNKNOWN result. Returns the command's messageId.
    /// </summary>
    private static async Task<string> DeclareAndHaveTheAnswerGivenUpAsync(
        Harness harness,
        CancellationToken cancellationToken)
    {
        await StartThreeSlotLoadAtSlotTwoAsync(harness, cancellationToken);
        string command = Guid.NewGuid().ToString("D");
        await SendDeclarationCommandAsync(harness, command);
        await WaitForAnswerGivenUpAsync(harness, cancellationToken);
        await WaitForDeclarationRefusalAsync(harness, command, 1, cancellationToken);
        await harness.WaitForInboundAsync("OperationResult", cancellationToken);
        Assert.Equal(1, CountOnTheWire(harness, "SlotFaultDeclarationResult"));
        return command;
    }

    private static Task SendDeclarationCommandAsync(Harness harness, string messageId) =>
        harness.Server.SendCommandAsync(
            "SlotFaultDeclarationCommand",
            messageId,
            DeclarationPayload(FirstDeclarationId, DeclaredAttemptId, 2));

    private static Task<WireToGateDurableMessage> ReadDeclarationAnswerAsync(
        Harness harness,
        CancellationToken cancellationToken) =>
        harness.Journal
            .ReadOutgoingByDeduplicationKeyAsync(
                WireToGateSessionClient.SlotFaultDeclarationResultKey(FirstDeclarationId),
                cancellationToken)
            .ContinueWith(read => read.Result ?? throw new InvalidOperationException("no answer on file"), cancellationToken);

    private static Task WaitForAnswerGivenUpAsync(Harness harness, CancellationToken cancellationToken) =>
        Harness.WaitUntilAsync(
            () => harness.Journal
                .ReadOutgoingByDeduplicationKeyAsync(
                    WireToGateSessionClient.SlotFaultDeclarationResultKey(FirstDeclarationId),
                    cancellationToken)
                .GetAwaiter()
                .GetResult()?.Abandoned == true,
            "the declaration's answer to be given up",
            cancellationToken,
            () => DescribeDeclarationHandling(harness));

    /// <summary>
    /// Waits for the <paramref name="count"/>th <c>ProtocolProblem</c> refusing <paramref name="commandMessageId"/>: correlated
    /// to it, naming it as rejected, with <c>SLOT_OPERATION_CONFLICT</c>.
    /// </summary>
    private static Task WaitForDeclarationRefusalAsync(
        Harness harness,
        string commandMessageId,
        int count,
        CancellationToken cancellationToken) =>
        Harness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Count(item => IsDeclarationRefusal(item.WireLine, commandMessageId))
                >= count,
            $"refusal {count} of SlotFaultDeclarationCommand {commandMessageId}",
            cancellationToken,
            () => DescribeDeclarationHandling(harness));

    private static bool IsDeclarationRefusal(string wireLine, string commandMessageId)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        JsonElement root = document.RootElement;
        if (root.GetProperty("messageType").GetString() != "ProtocolProblem")
        {
            return false;
        }

        JsonElement payload = root.GetProperty("payload");
        return root.GetProperty("correlationId").GetString() == commandMessageId
            && payload.GetProperty("rejectedMessageId").GetString() == commandMessageId
            && payload.GetProperty("rejectedMessageType").GetString() == "SlotFaultDeclarationCommand"
            && payload.GetProperty("problem").GetProperty("reasonCode").GetString() == "SLOT_OPERATION_CONFLICT";
    }

    private static void AssertAnswerNotResentAndSessionKept(Harness harness)
    {
        Assert.Equal(1, CountOnTheWire(harness, "SlotFaultDeclarationResult"));
        Assert.True(harness.Client.Current.Connected);
        Assert.DoesNotContain(
            harness.Logger.Entries,
            entry => entry.Message.StartsWith(SlotFaultDeclarationCommandFailure, StringComparison.Ordinal));
    }

    private static int CountOnTheWire(Harness harness, string messageType) =>
        harness.Server.Received.Count(item => item.MessageType == messageType);

    private static string DescribeDeclarationHandling(Harness harness) =>
        string.Join(
            Environment.NewLine,
            [
                harness.DescribeEvents(),
                "received: " + string.Join(", ", harness.Server.Received.Select(item => $"{item.Connection}:{item.MessageType}")),
                .. harness.Logger.Exceptions.Select(item => $"{item.Message}{Environment.NewLine}{item.Exception}")
            ]);

    /// <summary>
    /// Holds the next read of one outbox key -- the first thing the handling of a replayed declaration does -- until
    /// released: what lets a test take the connection away between the command arriving and its answer.
    /// </summary>
    private sealed class AnswerReadHoldJournal(IWireToGateJournal inner, string heldKey) : IWireToGateJournal
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task Held => _held.Task;

        public void HoldNextRead() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.TrySetResult();

        public async Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(deduplicationKey, heldKey, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _held.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return await inner.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);
        }

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, cancellationToken);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);

        public Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default) =>
            inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

        public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
            WireToGateDurableMessage expected,
            WireToGateDurableMessage replacement,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceOutgoingForReplayAsync(expected, replacement, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByMessageIdAsync(messageId, cancellationToken);

        public Task MarkOutgoingAcknowledgedAsync(
            string messageId,
            string acceptedContentSha256,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAcknowledgedAsync(messageId, acceptedContentSha256, cancellationToken);

        public Task<WireToGateDurableMessage> MarkOutgoingAbandonedAsync(
            string messageId,
            string contentSha256,
            string reasonCode,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAbandonedAsync(messageId, contentSha256, reasonCode, cancellationToken);

        public Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadUnacknowledgedOutgoingAsync(cancellationToken);

        public Task<IReadOnlyList<WireToGateAppliedJourneySnapshot>> ReadAppliedJourneySnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadAppliedJourneySnapshotsAsync(cancellationToken);

        public Task<WireToGateAppliedJourneySnapshot> SaveAppliedJourneySnapshotAsync(
            WireToGateAppliedJourneySnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            inner.SaveAppliedJourneySnapshotAsync(snapshot, cancellationToken);

        public Task<string> ComputeContentSha256Async(CancellationToken cancellationToken = default) =>
            inner.ComputeContentSha256Async(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
