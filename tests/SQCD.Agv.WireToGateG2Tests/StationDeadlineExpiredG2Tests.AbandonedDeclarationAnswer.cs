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

        AssertAnswerNotResentAndSessionKept(harness);
    }

    /// <summary>
    /// 路径二：会话没就绪时。重放的命令到了、处理到读发件箱那一步时连接断了；改前走
    /// <c>SendDurableCoreAsync</c> 的未就绪分支，以 <c>rebind:false</c> 存盘时抛 <c>DURABLE_MESSAGE_ABANDONED</c>，落进兜底。
    /// 改后回拒发不出去只记日志；重连后服务端再重放，这一次回拒到达。
    /// </summary>
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

        Assert.NotEqual(
            harness.Server.ReceivedEnvelopes.Single(item => item.MessageType == "SlotFaultDeclarationResult").Connection,
            harness.Server.ReceivedEnvelopes.Single(item => item.MessageType == "ProtocolProblem").Connection);
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
        await SendDeclarationCommandAsync(harness, Guid.NewGuid().ToString("D"));
        await WaitForAnswerGivenUpAsync(harness, token);
        WireToGateDurableMessage row = await ReadDeclarationAnswerAsync(harness, token);
        Assert.True(row.Acknowledged && row.Abandoned);
        int answersOnTheWire = CountOnTheWire(harness, "SlotFaultDeclarationResult");

        string replay = Guid.NewGuid().ToString("D");
        await SendDeclarationCommandAsync(harness, replay);
        await WaitForDeclarationRefusalAsync(harness, replay, 1, token);

        Assert.Equal(answersOnTheWire, CountOnTheWire(harness, "SlotFaultDeclarationResult"));
        Assert.True(harness.Client.Current.Connected);
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
                .Where(item => item.MessageType == "ProtocolProblem")
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
    /// The three-slot load waits on slot 2, the declaration arrives, its APPLIED answer is refused and given up, and the
    /// run it stopped reports its UNKNOWN result.
    /// </summary>
    private static async Task DeclareAndHaveTheAnswerGivenUpAsync(Harness harness, CancellationToken cancellationToken)
    {
        await StartThreeSlotLoadAtSlotTwoAsync(harness, cancellationToken);
        await SendDeclarationCommandAsync(harness, Guid.NewGuid().ToString("D"));
        await WaitForAnswerGivenUpAsync(harness, cancellationToken);
        await harness.WaitForInboundAsync("OperationResult", cancellationToken);
        Assert.Equal(1, CountOnTheWire(harness, "SlotFaultDeclarationResult"));
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
