using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 在途取消「首发」的两条规则：重发带第一次按下的内容；会话未就绪时按下不算第一次。
/// </summary>
/// <remarks>
/// <para>
/// <b>这两条原先在 <c>RecoveryVectorG2Tests</c>，断言未变，场景换了</b>（8005-agv-onboard-hmi#188）。原先的场景是
/// <c>RecoveryVectorHarness</c> 预置的「Prepared 的装货、没有进程在跑」：连上之后中断结算把它报成 UNKNOWN，服务端判
/// RecoveryRequired，而真服务端 <c>AuthorizeLoadCancellationAsync</c> 对 RecoveryRequired 的 operation 一律拒绝。那两条
/// 能绿，靠的是替身不判 RecoveryRequired，以及重启前那一次按下时入口缓存还没读到结算写下的待答结果——测的是一个真服务端下
/// 不存在的世界。现在的场景是一张真在途的装货：替身下发 SlotOperationCommand，门开着等操作员，结果还没发。
/// </para>
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>
    /// 服务端已经授权、车辆却没收下应答（超时、应答途中断线），之后车载端还重启了。重发必须拿到同一个授权：服务端按
    /// <c>cancellationId</c> 找回之前那次授权，但拿整个 payload 与首次比对（<c>UpsertSimpleWorkflowAsync</c>），所以重试得带
    /// 首发的操作员与理由——连同那一次的 <c>verifiedAt</c>——而不是重启之后在岗的那个人的。那份内容只能从日志里来，进程内存
    /// 撑不过重启（onboard-hmi#39，移植自 MVP 线 <c>297dd81</c>）。
    /// </summary>
    /// <remarks>
    /// 重启之后日志里是一次开过锁、没结算的装货，带着待答取消记录。中断结算对这种状态拒绝（<c>RECOVERY_STATE_MISMATCH</c>），
    /// 不发 UNKNOWN，所以服务端那边仍是在途、会授权；车载端照待答记录自动重发。原先那一版在重启后由另一个操作员再按一次，
    /// 现在重发是自动的——断的仍然是「重发带的是谁的内容」。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ALostCancellationAuthorizationIsAskedForAgainWithTheFirstPressContentAcrossARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        string? originalOperator = Environment.GetEnvironmentVariable(OperatorVariable);
        FakeControlServer before;
        try
        {
            Environment.SetEnvironmentVariable(OperatorVariable, "maintenance-001");
            await using (Harness beforeRestart = await Harness.StartAsync(
                new FakeIoModuleClient { OperatorNeverActs = true },
                token,
                server =>
                {
                    server.RespondToLoadCancellationRequests = true;
                    server.LoadCancellationAuthorizedSlots = [1];
                    server.LoadCancellationAuthorizationsToDrop = 1;
                    server.ReplayJourneySnapshotsWithStableIdentity = true;
                },
                journalPath: journalPath))
            {
                before = beforeRestart.Server;
                await beforeRestart.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
                await Harness.WaitUntilAsync(
                    () => beforeRestart.Business.CanRequestLoadCancellation,
                    "the in-flight load cancellation entry to be offered",
                    token);
                Assert.False(await beforeRestart.Business.RequestLoadCancellationAsync(
                    "装载结果未知，现场申请取消。", token));
                Assert.NotNull(beforeRestart.ReadRecoveryState(token).PendingLoadCancellation);
            }

            // 重启之后在岗的是另一个人；门在停机期间被空着关上。
            Environment.SetEnvironmentVariable(OperatorVariable, "maintenance-002");
            await using Harness afterRestart = await Harness.StartAsync(
                new FakeIoModuleClient(),
                token,
                server =>
                {
                    server.ReplayJourneySnapshotsWithStableIdentity = true;
                    server.RespondToLoadCancellationRequests = true;
                    server.LoadCancellationAuthorizedSlots = [1];
                    server.AdoptDurableRecoveryMemoryFrom(before);
                },
                journalPath: journalPath,
                baselineRevision: 2);
            await afterRestart.WaitForInboundAsync("LoadCancellationResult", token);

            Assert.Empty(before.RecoveryRequestConflicts);
            Assert.Empty(afterRestart.Server.RecoveryRequestConflicts);
            Assert.DoesNotContain(afterRestart.Server.Received, item => item.MessageType == "OperationResult");
            var requests = before.ReceivedEnvelopes
                .Concat(afterRestart.Server.ReceivedEnvelopes)
                .Where(envelope => envelope.MessageType == "LoadCancellationStartRequested")
                .ToArray();
            Assert.Equal(2, requests.Length);
            Assert.NotEqual(requests[0].MessageId, requests[1].MessageId);
            using JsonDocument first = JsonDocument.Parse(requests[0].WireLine);
            using JsonDocument retried = JsonDocument.Parse(requests[1].WireLine);
            JsonElement retriedPayload = retried.RootElement.GetProperty("payload");
            Assert.Equal(
                first.RootElement.GetProperty("payload").GetRawText(),
                retriedPayload.GetRawText());
            Assert.Equal(
                "maintenance-001",
                retriedPayload.GetProperty("operator").GetProperty("operatorId").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(OperatorVariable, originalOperator);
        }
    }

    /// <summary>
    /// 会话没就绪时按下的取消，一个字节都没发出去，就不算「首发」：不能把这次的操作员与 <c>verifiedAt</c> 记成待答内容。
    /// 否则就绪之后再按，沿用的是一份服务端从没见过、而且可能已经过时的核验——服务端也无从察觉，因为它比对的「首次」本来
    /// 就是那次重发。
    /// </summary>
    /// <remarks>
    /// 「未就绪」用断开连接来造：断开后会话客户端在发送之前就判 <c>WIRE_TO_GATE_NOT_READY</c>。「就绪后再按」用同一个进程
    /// 重连来造——执行器比连接活得长，装货仍在途、结果还没发，服务端会授权；换一个操作员就能看出发出去的是哪一次的核验。
    /// 原先那一版用一次重启来造「就绪后」，而重启之后那张装货会被中断结算报成 UNKNOWN，真服务端不会再授权取消。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ACancellationPressedWhileTheSessionIsNotReadyIsNotRememberedAsTheFirstPress()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string? originalOperator = Environment.GetEnvironmentVariable(OperatorVariable);
        try
        {
            Environment.SetEnvironmentVariable(OperatorVariable, "maintenance-001");
            await using Harness harness = await Harness.StartAsync(
                new FakeIoModuleClient { OperatorNeverActs = true },
                token,
                server =>
                {
                    server.RespondToLoadCancellationRequests = true;
                    server.LoadCancellationAuthorizedSlots = [1];
                    server.ReplayJourneySnapshotsWithStableIdentity = true;
                });
            await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
            await Harness.WaitUntilAsync(
                () => harness.Business.CanRequestLoadCancellation,
                "the in-flight load cancellation entry to be offered",
                token);

            await harness.Client.DisconnectAsync();
            await Harness.WaitUntilAsync(
                () => !harness.Business.CanRequestLoadCancellation,
                "the session to drop once the connection is gone",
                token);
            Assert.False(await harness.Business.RequestLoadCancellationAsync(
                "连接断了还是按了一次取消。", token));
            Assert.Null(harness.ReadRecoveryState(token).PendingLoadCancellation);
            Assert.DoesNotContain(
                harness.Server.ReceivedEnvelopes,
                envelope => envelope.MessageType == "LoadCancellationStartRequested");

            Environment.SetEnvironmentVariable(OperatorVariable, "maintenance-002");
            await harness.Client.ConnectAndRecoverAsync(token);
            await Harness.WaitUntilAsync(
                () => harness.Business.CanRequestLoadCancellation,
                "the load cancellation entry to be offered once the session is ready",
                token);

            Task<bool> cancellation = harness.Business.RequestLoadCancellationAsync("会话就绪之后再按一次。", token);
            await Harness.WaitUntilAsync(
                () => harness.ReadRecoveryState(token).RecoveryVector is not null,
                "the authorized cancellation to be journaled as a vector",
                token);
            harness.Io.CloseDoor(0, cargo: false);
            Assert.True(await cancellation, harness.DescribeEvents());

            Assert.Empty(harness.Server.RecoveryRequestConflicts);
            string request = Assert.Single(
                harness.Server.ReceivedEnvelopes,
                envelope => envelope.MessageType == "LoadCancellationStartRequested").WireLine;
            using JsonDocument document = JsonDocument.Parse(request);
            JsonElement payload = document.RootElement.GetProperty("payload");
            Assert.Equal(
                "maintenance-002",
                payload.GetProperty("operator").GetProperty("operatorId").GetString());
            Assert.Equal("会话就绪之后再按一次。", payload.GetProperty("reason").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(OperatorVariable, originalOperator);
        }
    }
}
