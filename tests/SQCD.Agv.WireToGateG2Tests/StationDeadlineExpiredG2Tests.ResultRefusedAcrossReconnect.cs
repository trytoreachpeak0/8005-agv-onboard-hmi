using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 装货结果在旧会话上判定可发，落盘时新握手已经读过发件箱，于是被连接核对拒掉（8005-agv-onboard-hmi#204 审查 M-B）：
/// 新会话就绪后它以同一 messageId 送达并被确认，attempt 恰好结算一次。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="AResultPutOnFileInsideTheHandshakeWindowIsSentAfterTheReadinessAndSettledOnce"/>（onboard-hmi#132）
/// 不同：那里的结果在握手窗口里才判定，被「未就绪」拒掉，行记在新一代下；这里的结果在旧会话上判定，行记在旧一代下，
/// 被拒是因为它的连接已经不在了。
/// </para>
/// <para>
/// 送达有两条路，各自够用：业务层 onboard-hmi#127 的恢复投影（发送方放手后，会话已就绪就立刻重发，否则等下一次就绪），
/// 与会话客户端补发握手没看到的旧代次行（<c>RequestStaleResend</c>）。这里只断送达本身；哪条路起作用由反向验证分辨，
/// 结果写在 <c>evidence/hmi-204</c>。夹具默认的第三条路（假服务端每次握手后重发仓位命令，车载端据此跑 #124 的恢复）
/// 在装货开始后关掉，见用例里的注释。
/// </para>
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>When the held result is let go, and so when its connection check refuses it.</summary>
    public enum ResultRefusalMoment
    {
        /// <summary>The next handshake has read the outbox and is held there.</summary>
        DuringHandshake,

        /// <summary>The next session is already Ready.</summary>
        AfterReady
    }

    [Theory]
    [InlineData(ResultRefusalMoment.DuringHandshake)]
    [InlineData(ResultRefusalMoment.AfterReady)]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-CONNECTION-LOSS-SAFE-FINISH")]
    public async Task AResultRefusedAcrossAReconnectIsDeliveredOnTheNextSessionAndSettledOnce(
        ResultRefusalMoment moment)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        HandshakeWindowJournal window = null!;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.ReplayJourneySnapshotsWithStableIdentity = true;
            },
            wrapJournal: inner => window = new HandshakeWindowJournal(inner));
        try
        {
            window.Observe(harness.Client);
            await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
            // From here on the server does not send the SlotOperationCommand again after each handshake. This fixture
            // does by default, and the vehicle takes that duplicate as its cue to run the #124 restore, which sends the
            // result too -- a third way that would carry these cells whatever the two under test do. Whether the real
            // server resends the command after a reconnect is not what this test is about, so the harder case it is.
            harness.Server.SendSlotOperationCommandAfterRecovery = false;
            long firstGeneration = harness.Client.Current.SessionGeneration
                ?? throw new InvalidOperationException("The first session has no generation.");
            int firstConnection = harness.Server.ReceivedEnvelopes.Max(item => item.Connection);

            // The load ends on the first session; its result is judged sendable there and held in front of its outbox
            // write.
            window.HoldNextResultSave();
            harness.Io.CloseDoor(0, cargo: true);
            await window.ResultSaveHeld.WaitAsync(TimeSpan.FromSeconds(10), token);

            await harness.Client.DisconnectAsync();
            Task<WireToGateSessionSnapshot> reconnect;
            if (moment == ResultRefusalMoment.DuringHandshake)
            {
                window.HoldAt(HandshakeWindowEnd.AfterOutboxRead);
                reconnect = harness.Client.ConnectAndRecoverAsync(token);
                await window.Entered.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
            else
            {
                reconnect = harness.Client.ConnectAndRecoverAsync(token);
                await reconnect;
                Assert.True(harness.Client.Current.Readiness
                    is WireToGateSessionReadiness.Ready or WireToGateSessionReadiness.RecoveryRequired);
            }

            int secondConnection = harness.Server.ReceivedEnvelopes.Max(item => item.Connection);
            long secondGeneration = harness.Client.Current.SessionGeneration
                ?? throw new InvalidOperationException("The next session has no generation.");
            Assert.NotEqual(firstConnection, secondConnection);

            // Let go: it lands in the outbox after the handshake read it, and its connection check refuses it.
            window.ReleaseResultSave();
            await Harness.WaitUntilAsync(
                () => harness.HasEvent("RESULT_ACK_PENDING"),
                "the result refused and waiting for its acknowledgement",
                token,
                harness.DescribeEvents);

            // The premise: written under the first generation -- the handshake read the outbox before it existed. Read as
            // written, not from the journal now: after a Ready session a resend can rebind the row before this line runs
            // (1 run in 10 did, the product doing its job while the premise read the outcome instead of the cause).
            Assert.Equal(firstGeneration, SessionGenerationOf(window.ResultWireLineAsSaved!));
            if (moment == ResultRefusalMoment.DuringHandshake)
            {
                // Nothing can send it while the handshake is held: nothing sends a durable message before Ready.
                Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, item => item.MessageType == "OperationResult");
            }

            window.Release();
            await reconnect;
            await Harness.WaitUntilAsync(
                () => harness.Client.Current.Readiness == WireToGateSessionReadiness.Ready
                    && harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId is null,
                "the result delivered and the load settled",
                token,
                harness.DescribeEvents);
            // Room for a late second settlement to show itself.
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);

            // Delivered on the next session under its own generation, with the same messageId, and acknowledged. More
            // than one copy may arrive -- both ways named above can send it -- and every copy is this same message.
            var delivered = harness.Server.ReceivedEnvelopes.Where(item => item.MessageType == "OperationResult").ToArray();
            Assert.NotEmpty(delivered);
            Assert.All(delivered, item =>
            {
                Assert.Equal(secondConnection, item.Connection);
                Assert.Equal(AttemptId, item.MessageId);
                Assert.Equal(secondGeneration, SessionGenerationOf(item.WireLine));
            });
            Assert.True((await harness.Journal.ReadOutgoingByDeduplicationKeyAsync(ResultKey, token))!.Acknowledged);

            // Settled once, and the lock opened once.
            Assert.Equal(1, window.RecordingsThatClearedTheAttempt);
            WireToGateRecoveryState state = harness.ReadRecoveryState(token);
            Assert.Null(state.UnsettledSlotOperationAttemptId);
            Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, state.ProvenRecoveryCheckpoint);
            Assert.Equal(1, harness.CountEvents("OPERATION_COMPLETED"));
            Assert.Equal(1, harness.Io.UnlockCount);
        }
        finally
        {
            window.ReleaseResultSave();
            window.Release();
        }
    }

    private static long? SessionGenerationOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.TryGetProperty("sessionGeneration", out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;
    }
}
