using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 一条持久报文在旧连接上判定可发、写进发件箱时新握手已经读过发件箱，于是被连接核对拒掉：它不能只等「下一次握手」，
/// 新会话就绪后就要以同一 messageId 送达并被确认（8005-agv-onboard-hmi#204 审查 M-B）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要单独管：</b>握手按 RELIABLE 补发的是它读发件箱那一刻未确认的行。这一行落盘晚于那一刻，这次握手看不到它；
/// 下一次握手要等下一次不相干的断线，间隔没有上限。修 #204 之前这条报文会写进新连接、把握手打断，重连后反而补发得出去；
/// 连接核对把「断一次再送达」变成了「等到下一次断线」，所以要由新会话自己把它送出去。
/// </para>
/// <para>
/// 用 <c>OperationProgress</c>：它在业务层没有自己的重发路径（安全上报有会话就绪与 #197 两条，<c>OperationResult</c>
/// 有 onboard-hmi#127 的恢复投影），这里送不送得到只看会话客户端。两格对应被拒的两个时刻：新握手还卡着时
/// （就绪之后才能补），与新会话已经就绪之后（就绪那一刻这一行还不存在）。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>When the held message is let go, and so when its connection check refuses it.</summary>
    public enum RefusalMoment
    {
        /// <summary>The next handshake has read the outbox and is held there.</summary>
        DuringHandshake,

        /// <summary>The next session is already Ready.</summary>
        AfterReady
    }

    [Theory]
    [InlineData(RefusalMoment.DuringHandshake)]
    [InlineData(RefusalMoment.AfterReady)]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    public async Task ADurableMessageRefusedAcrossAReconnectIsDeliveredOnTheNextSession(RefusalMoment moment)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ReconnectRaceJournal race = null!;
        await using Harness harness = await Harness.StartAsync(
            _ => { },
            token,
            wrapJournal: inner => race = new ReconnectRaceJournal(inner));
        try
        {
            await WaitForSafetyReportsToSettleAsync(harness, token);
            long firstGeneration = harness.Session.Current.SessionGeneration
                ?? throw new InvalidOperationException("The first session has no generation.");
            int firstConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);

            // Judged sendable on the first session, held in front of its outbox write.
            race.HoldNextSave("OperationProgress");
            Task<string> send = harness.Session.Client.SendOperationProgressAsync(
                "55555555-5555-5555-5555-555555555555",
                "UNLOCKING",
                [1],
                [],
                cancellationToken: token);
            await race.SafetyChangeHeld.WaitAsync(TimeSpan.FromSeconds(10), token);

            await harness.Session.Client.DisconnectAsync();
            Task<WireToGateSessionSnapshot> reconnect;
            if (moment == RefusalMoment.DuringHandshake)
            {
                race.HoldNextHandshakeAfterOutboxRead();
                reconnect = harness.Session.Client.ConnectAndRecoverAsync(token);
                await race.HandshakeHeld.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
            else
            {
                reconnect = harness.Session.Client.ConnectAndRecoverAsync(token);
                await reconnect;
                Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
            }

            int secondConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
            long secondGeneration = harness.Session.Current.SessionGeneration
                ?? throw new InvalidOperationException("The next session has no generation.");
            Assert.NotEqual(firstConnection, secondConnection);

            // Let go: it lands in the outbox after this handshake read it, and its connection check refuses it.
            race.ReleaseSafetyChange();
            await Assert.ThrowsAsync<WireToGateConnectionGoneException>(() => send);

            // The premise: written under the first generation -- the handshake read the outbox before it existed. Read as
            // written, not from the journal now: once a session is Ready the resend under test may already have rebound
            // and acknowledged it, and the premise would be reading the outcome instead of the cause.
            WireToGateDurableMessage refused = race.HeldRowAsSaved
                ?? throw new InvalidOperationException("The held message was never written.");
            Assert.Equal(firstGeneration, GenerationOf(refused.WireLine));
            if (moment == RefusalMoment.DuringHandshake)
            {
                // Nothing can send it while the handshake is held: nothing sends a durable message before Ready.
                Assert.False((await race.ReadOutgoingByMessageIdAsync(refused.MessageId, token))!.Acknowledged);
                Assert.DoesNotContain(
                    harness.Server.ReceivedEnvelopes,
                    envelope => envelope.Connection == secondConnection && envelope.MessageId == refused.MessageId);
            }

            race.ReleaseHandshake();
            await reconnect;
            await harness.WaitUntilAsync(
                () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
                "the next session to become Ready",
                token);

            // Delivered by the next session, not left for the next disconnect: the same messageId under the new
            // generation, on the new connection, and acknowledged.
            await harness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                    envelope.Connection == secondConnection && envelope.MessageId == refused.MessageId),
                $"the refused {refused.MessageType} {refused.MessageId} to be sent on the next session",
                token);
            // Once: passes never overlap, and a pass marks the row acknowledged before it ends, so a pass asked for again
            // no longer finds it. OperationProgress has no other sender.
            var delivered = Assert.Single(
                harness.Server.ReceivedEnvelopes,
                envelope => envelope.Connection == secondConnection && envelope.MessageId == refused.MessageId);
            Assert.Equal("OperationProgress", delivered.MessageType);
            Assert.Equal(secondGeneration, GenerationOf(delivered.WireLine));
            await harness.WaitUntilAsync(
                () => race.ReadOutgoingByMessageIdAsync(refused.MessageId, token).GetAwaiter().GetResult()
                    is { Acknowledged: true },
                $"the refused {refused.MessageType} {refused.MessageId} to be acknowledged",
                token);
            Assert.Equal(secondConnection, harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection));
            Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
            Assert.Empty(harness.UiErrors);
        }
        finally
        {
            race.ReleaseSafetyChange();
            race.ReleaseHandshake();
        }
    }

    /// <summary>
    /// 回绝一条上一代会话的服务端命令，这时新握手正在进行：那条 <c>ProtocolProblem</c> 不写进新连接，调用方得到
    /// 「连接已不在」（8005-agv-onboard-hmi#204 审查第 5 条）。
    /// </summary>
    /// <remarks>
    /// 命令属于它进来的那条连接。回绝原先在发送时才读连接序号、也不看命令是哪一代的，所以新握手读完发件箱、还在等下一步时
    /// 调用，它就带着上一代的代号插进新握手——与缺陷一同形。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    public async Task ARejectionOfAPreviousSessionsCommandStaysOutOfTheNextHandshake()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ReconnectRaceJournal race = null!;
        await using Harness harness = await Harness.StartAsync(
            _ => { },
            token,
            wrapJournal: inner => race = new ReconnectRaceJournal(inner));
        try
        {
            await WaitForSafetyReportsToSettleAsync(harness, token);
            long firstGeneration = harness.Session.Current.SessionGeneration
                ?? throw new InvalidOperationException("The first session has no generation.");
            PreviousSessionCommand command = new(
                "PreDepartureSafetyCheckRequested",
                "77777777-7777-7777-7777-777777777777",
                null,
                firstGeneration,
                DateTimeOffset.UtcNow);

            await harness.Session.Client.DisconnectAsync();
            race.HoldNextHandshakeAfterOutboxRead();
            Task<WireToGateSessionSnapshot> reconnect = harness.Session.Client.ConnectAndRecoverAsync(token);
            await race.HandshakeHeld.WaitAsync(TimeSpan.FromSeconds(10), token);
            int secondConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
            Assert.Equal(["SessionHello"], MessageTypesOn(harness, secondConnection));

            Exception? refusal = await Record.ExceptionAsync(() =>
                harness.Session.Client.RejectServerCommandAsync(command, "PREDEPARTURE_CHECK_EXPIRED", token));
            // Room for a line written anyway to reach the server.
            await Task.Delay(TimeSpan.FromMilliseconds(300), token);

            Assert.Equal(["SessionHello"], MessageTypesOn(harness, secondConnection));
            Assert.IsType<WireToGateConnectionGoneException>(refusal);

            race.ReleaseHandshake();
            await reconnect;
            Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
        }
        finally
        {
            race.ReleaseHandshake();
        }
    }

    private sealed record PreviousSessionCommand(
        string MessageType,
        string MessageId,
        string? CorrelationId,
        long SessionGeneration,
        DateTimeOffset SentAt)
        : WireToGateServerCommand(MessageType, MessageId, CorrelationId, SessionGeneration, SentAt);

    /// <summary>
    /// 一次写还挂着的时候连接被关（现场那种连接卡住）：关连接照常做完，挂着的写以「连接已不在」失败
    /// （8005-agv-onboard-hmi#204 审查 M-A）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CloseConnectionAsync</c> 不拿发送锁，所以它会在一次写还没写完时释放同一个写入口。<c>StreamWriter</c> 在异步写
    /// 进行中被释放会抛 <c>InvalidOperationException</c>；这一步若把关连接中止在释放读端、流与 socket 之前，挂着的写就
    /// 没有东西能结束它，或者之后以 <c>ObjectDisposedException</c> 结束——那不是 <see cref="WireToGateConnectionGoneException"/>，
    /// 业务服务会把它当成当前会话的失败去断开会话，缺陷二就回来了。
    /// </para>
    /// <para>
    /// <b>写是怎么挂住的，不靠时序：</b>假服务端停止读取、接收缓冲调小，车载端写一行 16 MB 的报文。写不完是按构造的：
    /// 对端一个字节都不再收，而两端缓冲加起来远小于这一行。用例先断言写确实开始了（对端 socket 上有数据在等）且还没写完，
    /// 再关连接。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    public async Task AWriteStillPendingWhenItsConnectionIsClosedFailsAsConnectionGoneAndTheCloseCompletes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server => server.ListenerReceiveBufferSize = 4_096,
            token);
        try
        {
            await WaitForSafetyReportsToSettleAsync(harness, token);

            harness.Server.PauseReading = true;
            Task<string> send = harness.Session.Client.SendDurableAsync(
                "OperationProgress",
                "hmi204:hung-write",
                "66666666-6666-6666-6666-666666666666",
                null,
                new { padding = new string('x', 16 * 1024 * 1024) },
                token);
            await harness.WaitUntilAsync(
                () => harness.Server.DataWaitingWhilePaused,
                "the large line to start arriving at the paused server",
                token);
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            // The premise: the write has started and cannot finish.
            Assert.False(send.IsCompleted, $"The write finished while the server read nothing: {send.Status}.");

            Exception? closeFailure = await Record.ExceptionAsync(() => harness.Session.Client.DisconnectAsync());
            Exception? sendFailure = await Record.ExceptionAsync(() => send.WaitAsync(TimeSpan.FromSeconds(10), token));

            // The close went all the way -- it did not stop at the writer it could not dispose -- and so ended the write,
            // which failed for what it is: its connection gone.
            Assert.Null(closeFailure);
            Assert.IsType<WireToGateConnectionGoneException>(sendFailure);
            Assert.Equal(WireToGateSessionReadiness.Disconnected, harness.Session.Current.Readiness);
        }
        finally
        {
            harness.Server.PauseReading = false;
        }
    }
}
