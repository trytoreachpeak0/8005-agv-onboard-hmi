using System.Net;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 扫码前取消的车载端半边（批次5-27，onboard-hmi#76）：服务端的录入请求挂着、本需求还没发过仓位操作时，
/// 站点操作员可以取消；服务端以空仓位集合授权，车报 <c>ALL_EMPTY</c>、<c>slotResults</c> 为空，全程不开仓门。
/// </summary>
/// <remarks>
/// <para>
/// 按 ADR-cross-0046 第一种情形的原形做，四步是 <c>CV-LOAD-CANCELLATION-BEFORE-LOAD</c>：
/// <c>LoadCancellationStartRequested</c>（attempt 为 null）→ <c>LoadCancellationAuthorization</c>（slots
/// 为空）→ <c>LoadCancellationResult</c> → <c>DurableAck</c>。服务端半边是 control-server#83（PR #116），
/// 替身按它的形状应答。
/// </para>
/// <para>
/// 这里的 harness 用出厂配置（<c>recoveryResumeEnabled=false</c>）：这个入口不是维护权限，出厂配置下就得
/// 出现。业务服务在连接之前启动，与 <c>App</c> 的顺序一致，会话就绪时的恢复投影因此照常跑。
/// </para>
/// </remarks>
public sealed class LoadCancellationBeforeSublotG2Tests
{
    private const string OperatorVariable = "W2G_G2_BEFORE_SUBLOT_OPERATOR";
    private const string CredentialVariable = "W2G_G2_BEFORE_SUBLOT_CREDENTIAL";
    private const string OperationSessionId = "88888888-8888-4888-8888-888888888888";

    /// <summary><see cref="FakeControlServer"/> 工作清单里那唯一一条需求。</summary>
    private const string DemandId = "11111111-1111-1111-1111-111111111111";

    private const string AttemptId = "33333333-3333-4333-8333-333333333333";

    static LoadCancellationBeforeSublotG2Tests()
    {
        Environment.SetEnvironmentVariable(CredentialVariable, "g2-before-sublot-credential");
        Environment.SetEnvironmentVariable(OperatorVariable, "operator-001");
    }

    /// <summary>
    /// <c>CV-LOAD-CANCELLATION-BEFORE-LOAD</c> 四步走到 <c>DurableAck</c>：请求不带 attempt，授权没有仓位，
    /// 结果是 <c>ALL_EMPTY</c> 加空 <c>slotResults</c>；目标仓里有货也一次都不开锁。收到确认后清掉录入请求与
    /// 待答取消记录，本地不发任何仓位操作快照，任务状态留给服务端的下一份快照。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ACancellationBeforeAnySublotReportsAllEmptyWithoutOpeningADoor()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(token);
        Assert.True(harness.Business.CanRequestLoadCancellation);

        Assert.True(await harness.Business.RequestLoadCancellationAsync(
            "到站后现场确认本站没有要装的货。", token));

        JsonElement request = Assert.Single(harness.PayloadsReceived("LoadCancellationStartRequested"));
        Assert.Equal(DemandId, request.GetProperty("demandId").GetString());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("slotOperationAttemptId").ValueKind);

        JsonElement authorization = Assert.Single(harness.PayloadsSent("LoadCancellationAuthorization"));
        Assert.Equal("AUTHORIZED", authorization.GetProperty("decision").GetString());
        Assert.Equal(0, authorization.GetProperty("slots").GetArrayLength());

        (string resultMessageId, JsonElement result) = Assert.Single(harness.ResultsReceived());
        Assert.Equal(
            request.GetProperty("cancellationId").GetString(),
            result.GetProperty("cancellationId").GetString());
        Assert.Equal(DemandId, result.GetProperty("demandId").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("slotOperationAttemptId").ValueKind);
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(0, result.GetProperty("slotResults").GetArrayLength());

        string[] order =
        [
            .. harness.Server.Received
                .Select(item => item.MessageType)
                .Where(type => type is "LoadCancellationStartRequested" or "LoadCancellationResult")
        ];
        Assert.Equal(["LoadCancellationStartRequested", "LoadCancellationResult"], order);
        Assert.Contains(
            harness.PayloadsSent("DurableAck"),
            ack => ack.GetProperty("acceptedMessageId").GetString() == resultMessageId);

        Assert.Equal(0, harness.Io.UnlockCount);
        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(state.RecoveryVector);
        Assert.Null(state.PendingLoadCancellation);
        Assert.Null(state.UnsettledSlotOperationAttemptId);
        Assert.False(harness.Business.CanSubmitSublot);
        Assert.False(harness.Business.CanRequestLoadCancellation);
        Assert.Null(harness.Business.CurrentOperationSnapshot);
    }

    /// <summary>
    /// 本需求已经发过仓位操作（车上记着它的已结算装货）时，扫码前取消的入口不出现，按下也什么都不发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task TheEntryIsNotOfferedOnceALoadWasCommandedForTheDemand()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            seed: WireToGateRecoveryState.Empty with
            {
                ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ResultRecorded,
                LastCompletedLoadOperationContext = SettledLoad()
            });

        Assert.False(await harness.Business.RequestLoadCancellationAsync(
            "录入过的需求也按了一次取消。", token));

        Assert.False(harness.Business.CanRequestLoadCancellation);
        Assert.Empty(harness.PayloadsReceived("LoadCancellationStartRequested"));
    }

    /// <summary>
    /// 已经有一个在途取消（待答记录带着 attempt）时，扫码前取消的入口不出现，按下被判为冲突、什么都不发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task TheEntryIsNotOfferedWhileAnotherCancellationIsOpen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            seed: WireToGateRecoveryState.Empty with
            {
                PendingLoadCancellation = new WireToGatePendingLoadCancellation(
                    "c7b1f2a4-9d3e-4c8a-8f52-0a1b2c3d4e5f",
                    AttemptId,
                    "operator-000",
                    "SESSION",
                    DateTimeOffset.UtcNow,
                    "在途装货的取消，还没等到应答。")
            });

        Assert.False(await harness.Business.RequestLoadCancellationAsync(
            "又按了扫码前的取消。", token));

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_VECTOR_CONFLICT", token);
        Assert.False(harness.Business.CanRequestLoadCancellation);
        Assert.Empty(harness.PayloadsReceived("LoadCancellationStartRequested"));
    }

    /// <summary>
    /// 授权里带了仓位：车载端没发过任何仓位操作，没有服务端能指的仓，按范围不符拒绝，不上报结果、不开锁，
    /// 并提示操作员。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task AnAuthorizationNamingASlotIsRefusedWithoutSlotIo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server => server.LoadCancellationBeforeSublotAuthorizedSlots = [1]);

        Assert.False(await harness.Business.RequestLoadCancellationAsync(
            "到站后现场确认本站没有要装的货。", token));

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_RESPONSE_SCOPE_MISMATCH", token);
        await AssertNothingExecutedAsync(harness, token);
    }

    /// <summary>
    /// 授权里带了 attempt：与请求的 null 对不上，同样按范围不符拒绝、不执行，并提示操作员。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task AnAuthorizationNamingAnAttemptIsRefusedWithoutSlotIo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server => server.LoadCancellationBeforeSublotAuthorizedAttemptId = AttemptId);

        Assert.False(await harness.Business.RequestLoadCancellationAsync(
            "到站后现场确认本站没有要装的货。", token));

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_RESPONSE_SCOPE_MISMATCH", token);
        await AssertNothingExecutedAsync(harness, token);
    }

    /// <summary>
    /// 服务端拒绝：如实显示原因，录入请求留着（本地清单不变），待答记录清掉，下一次按下是新的请求。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ARefusalShowsItsReasonAndLeavesTheEntryRequestInPlace()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server => server.LoadCancellationDecision = "REJECTED");

        Assert.False(await harness.Business.RequestLoadCancellationAsync(
            "到站后现场确认本站没有要装的货。", token));

        await harness.WaitForRecoveryBlockedAsync("ACTION_NOT_ALLOWED_IN_STATE", token);
        Assert.True(harness.Business.CanSubmitSublot);
        Assert.True(harness.Business.CanRequestLoadCancellation);
        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(state.PendingLoadCancellation);
        Assert.Null(state.RecoveryVector);
        Assert.Empty(harness.ResultsReceived());
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// 授权应答丢了之后再按：沿用首发的操作员、理由与 <c>verifiedAt</c>，换一个 messageId，
    /// <c>cancellationId</c> 不变；替身按真服务端的规则比对，没有冲突。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ALostAuthorizationIsAskedForAgainWithTheFirstPressContent()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string? originalOperator = Environment.GetEnvironmentVariable(OperatorVariable);
        try
        {
            Environment.SetEnvironmentVariable(OperatorVariable, "operator-001");
            await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
                token,
                server => server.LoadCancellationAuthorizationsToDrop = 1);

            Assert.False(await harness.Business.RequestLoadCancellationAsync(
                "到站后现场确认本站没有要装的货。", token));
            Assert.NotNull((await harness.ReadRecoveryStateAsync(token)).PendingLoadCancellation);
            Assert.True(harness.Business.CanRequestLoadCancellation);

            Environment.SetEnvironmentVariable(OperatorVariable, "operator-002");
            Assert.True(await harness.Business.RequestLoadCancellationAsync(
                "换了个人又按了一次。", token));

            AssertRetriedWithTheFirstPressContent(harness.Server.ReceivedEnvelopes);
            Assert.Empty(harness.Server.RecoveryRequestConflicts);
            Assert.Equal(0, harness.Io.UnlockCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OperatorVariable, originalOperator);
        }
    }

    /// <summary>
    /// 同上，但两次按下之间车载端重启：首发内容只能从日志里来。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ALostAuthorizationIsAskedForAgainWithTheFirstPressContentAcrossARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = NewJournalPath();
        string? originalOperator = Environment.GetEnvironmentVariable(OperatorVariable);
        await using FakeControlServer server = BeforeSublotHarness.NewServer();
        server.LoadCancellationAuthorizationsToDrop = 1;
        try
        {
            Environment.SetEnvironmentVariable(OperatorVariable, "operator-001");
            await using (BeforeSublotHarness beforeRestart = await BeforeSublotHarness.StartAsync(
                token,
                existingServer: server,
                journalPath: journalPath))
            {
                Assert.False(await beforeRestart.Business.RequestLoadCancellationAsync(
                    "到站后现场确认本站没有要装的货。", token));
            }

            await using FakeControlServer serverAfterRestart = BeforeSublotHarness.NewServer();
            serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
            Environment.SetEnvironmentVariable(OperatorVariable, "operator-002");
            await using BeforeSublotHarness afterRestart = await BeforeSublotHarness.StartAsync(
                token,
                existingServer: serverAfterRestart,
                journalPath: journalPath,
                baselineRevision: 2);
            Assert.True(afterRestart.Business.CanRequestLoadCancellation);

            Assert.True(await afterRestart.Business.RequestLoadCancellationAsync(
                "重启之后换了个人再按一次。", token));

            AssertRetriedWithTheFirstPressContent(
                [.. server.ReceivedEnvelopes, .. serverAfterRestart.ReceivedEnvelopes]);
            Assert.Empty(server.RecoveryRequestConflicts);
            Assert.Empty(serverAfterRestart.RecoveryRequestConflicts);
            Assert.Equal(0, afterRestart.Io.UnlockCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OperatorVariable, originalOperator);
        }
    }

    /// <summary>
    /// 结果没等到 <c>DurableAck</c> 时，录入请求与待答取消记录都还在；再按一次补报的是同一条结果（同一个
    /// messageId），确认之后两者才一起清掉。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task TheEntryRequestAndThePendingCancellationGoOnlyOnceTheResultIsAcknowledged()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server => server.LoadCancellationResultAcksToDrop = 1);

        Assert.False(await harness.Business.RequestLoadCancellationAsync(
            "到站后现场确认本站没有要装的货。", token));

        WireToGateRecoveryState unacknowledged = await harness.ReadRecoveryStateAsync(token);
        Assert.NotNull(unacknowledged.PendingLoadCancellation);
        Assert.NotNull(unacknowledged.RecoveryVector);
        await AssertSublotEntryClosedAsync(harness, token);
        Assert.True(harness.Business.CanRequestLoadCancellation);
        Assert.Null(harness.Business.CurrentOperationSnapshot);

        Assert.True(await harness.Business.RequestLoadCancellationAsync(
            "再按一次补报结果。", token));

        (string MessageId, JsonElement Payload)[] results = [.. harness.ResultsReceived()];
        Assert.Equal(2, results.Length);
        Assert.Equal(results[0].MessageId, results[1].MessageId);
        Assert.Equal(results[0].Payload.GetRawText(), results[1].Payload.GetRawText());
        Assert.Single(harness.PayloadsReceived("LoadCancellationStartRequested"));

        WireToGateRecoveryState acknowledged = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(acknowledged.PendingLoadCancellation);
        Assert.Null(acknowledged.RecoveryVector);
        Assert.False(harness.Business.CanSubmitSublot);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// 结果没等到确认就重启：重连握手补发结果并得到确认，会话就绪后车载端自己把这次取消收尾，不留下一个
    /// 永远结不掉的恢复向量。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task AResultAcknowledgedOnReconnectSettlesTheCancellation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = NewJournalPath();
        await using FakeControlServer server = BeforeSublotHarness.NewServer();
        server.LoadCancellationResultAcksToDrop = 1;

        await using (BeforeSublotHarness beforeRestart = await BeforeSublotHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath))
        {
            Assert.False(await beforeRestart.Business.RequestLoadCancellationAsync(
                "到站后现场确认本站没有要装的货。", token));
            Assert.NotNull((await beforeRestart.ReadRecoveryStateAsync(token)).RecoveryVector);
        }

        await using BeforeSublotHarness afterRestart = await BeforeSublotHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            baselineRevision: 2,
            awaitEntryRequest: false);

        await BeforeSublotHarness.WaitUntilAsync(
            () => afterRestart.ReadRecoveryStateAsync(token).GetAwaiter().GetResult() is
            { RecoveryVector: null, PendingLoadCancellation: null },
            "the reconnect to settle the acknowledged cancellation",
            token);
        (string MessageId, JsonElement Payload)[] results = [.. afterRestart.ResultsReceived()];
        Assert.Equal(2, results.Length);
        Assert.Equal(results[0].MessageId, results[1].MessageId);
        Assert.Contains(
            afterRestart.PayloadsSent("DurableAck"),
            ack => ack.GetProperty("acceptedMessageId").GetString() == results[1].MessageId);
        Assert.Single(afterRestart.PayloadsReceived("LoadCancellationStartRequested"));
        Assert.Equal(0, afterRestart.Io.UnlockCount);
    }

    /// <summary>
    /// 扫码前取消挂着时离站期限到了：倒计时那一行说「已到期，正在取消本站装货」，不显示已过期时长；取消被拒、
    /// 回到可扫码之后再到期，回到通用的「已到期，等待本站结束」（onboard-hmi#78，调度会话 2026-09-18 定）。
    /// </summary>
    /// <remarks>
    /// 依据：服务端在取消记录开着时既不开始装货，也不按期限结束本站，本站改由车报的 <c>ALL_EMPTY</c> 结束，
    /// 所以此刻「等待本站结束」不准确。配色照 Expired 档不变，那一半由 <c>MainViewModel</c> 按期限算。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task PastTheDeadlineTheCountdownLineSaysTheLoadIsBeingCancelledOnlyWhileTheCancellationIsOpen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server =>
            {
                server.LoadCancellationAuthorizationsToDrop = 1;
                server.LoadCancellationDecision = "REJECTED";
            });
        DateTimeOffset deadline = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(20);
        StationDepartureCountdownContext expired = new(
            deadline,
            deadline + TimeSpan.FromSeconds(20),
            StationDepartureCountdownFormatter.Format(deadline, deadline + TimeSpan.FromSeconds(20)));

        Assert.False(await harness.Business.RequestLoadCancellationAsync(
            "到站后现场确认本站没有要装的货。", token));
        Assert.True(harness.Business.IsLoadCancellationBeforeSublotOpen);
        Assert.Equal("已到期，正在取消本站装货", harness.Business.DescribeExpiredStationDeadline(expired));

        Assert.False(await harness.Business.RequestLoadCancellationAsync(
            "没等到答复，又按了一次。", token));
        await harness.WaitForRecoveryBlockedAsync("ACTION_NOT_ALLOWED_IN_STATE", token);
        Assert.True(harness.Business.CanSubmitSublot);
        Assert.Null(harness.Business.DescribeExpiredStationDeadline(expired));
        Assert.Equal("已到期，等待本站结束", expired.Generic.Text);
    }

    private static async Task AssertNothingExecutedAsync(
        BeforeSublotHarness harness,
        CancellationToken cancellationToken)
    {
        Assert.Empty(harness.ResultsReceived());
        Assert.Equal(0, harness.Io.UnlockCount);
        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(cancellationToken);
        Assert.Null(state.RecoveryVector);
        Assert.Null(harness.Business.CurrentOperationSnapshot);
        // 服务端已按 cancellationId 落了授权记录、站点被它挂住，待答记录留着，扫码也就仍然关着。
        Assert.False(harness.Business.CanSubmitSublot);
    }

    /// <summary>
    /// 扫码前取消已发出、还没有答复时不能提交子批：服务端取消记录开着时不会开始装货，提交了只会让操作员
    /// 干等仓位操作。服务端拒绝之后待答记录清掉，提交子批恢复，并且真的发得出去。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task SublotEntryClosesWhileTheCancellationIsOutAndReopensOnceItIsRefused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server =>
            {
                server.LoadCancellationAuthorizationsToDrop = 1;
                server.LoadCancellationDecision = "REJECTED";
            });

        Assert.False(await harness.Business.RequestLoadCancellationAsync(
            "到站后现场确认本站没有要装的货。", token));
        Assert.True(harness.Business.IsLoadCancellationBeforeSublotOpen);
        await AssertSublotEntryClosedAsync(harness, token);

        Assert.False(await harness.Business.RequestLoadCancellationAsync(
            "再按一次，这次服务端拒绝。", token));
        await harness.WaitForRecoveryBlockedAsync("ACTION_NOT_ALLOWED_IN_STATE", token);

        Assert.False(harness.Business.IsLoadCancellationBeforeSublotOpen);
        Assert.True(harness.Business.CanSubmitSublot);
        await harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", token);
        await BeforeSublotHarness.WaitUntilAsync(
            () => harness.PayloadsReceived("SublotSubmitted").Count == 1,
            "the sublot submitted after the refusal to reach the server",
            token);
    }

    private static async Task AssertSublotEntryClosedAsync(
        BeforeSublotHarness harness,
        CancellationToken cancellationToken)
    {
        Assert.False(harness.Business.CanSubmitSublot);
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", cancellationToken));
        Assert.Equal("LOAD_CANCELLATION_IN_PROGRESS", refused.Message);
        Assert.Empty(harness.PayloadsReceived("SublotSubmitted"));
    }

    private static void AssertRetriedWithTheFirstPressContent(
        IEnumerable<(int Connection, string MessageType, string MessageId, string WireLine)> received)
    {
        var requests = received
            .Where(envelope => envelope.MessageType == "LoadCancellationStartRequested")
            .ToArray();
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(requests[0].MessageId, requests[1].MessageId);
        using JsonDocument first = JsonDocument.Parse(requests[0].WireLine);
        using JsonDocument retried = JsonDocument.Parse(requests[1].WireLine);
        JsonElement retriedPayload = retried.RootElement.GetProperty("payload");
        Assert.Equal(first.RootElement.GetProperty("payload").GetRawText(), retriedPayload.GetRawText());
        Assert.Equal(
            "operator-001",
            retriedPayload.GetProperty("operator").GetProperty("operatorId").GetString());
        Assert.Equal(JsonValueKind.Null, retriedPayload.GetProperty("slotOperationAttemptId").ValueKind);
    }

    /// <summary>
    /// 会话 Ready、车在动、手里有一条录入请求：本端的扫码入口是开着的（8005-agv-onboard-hmi#177）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这条钉的是一句文案的前提，不是一个期望的行为。</b>WIRE_TO_GATE_NOT_READY 在「会话 Ready 且车辆停稳」为假时
    /// 显示，所以这个状态下也显示；它原来说「本界面已禁止扫码与发车」，而本端 CanSubmitSublot、SubmitSublotAsync、
    /// 发送口三层都不看车动没动，扫码那一半就是假的。hmi#177 把那一句改成只说「已禁止发车」，理由就是这条断言。
    /// </para>
    /// <para>
    /// <b>它红了，要改的是那一句文案，不是这条。</b>扫码入口哪天自己看车动没动了（调度报给用户的方案 B），这条会红——
    /// 那时 OnboardCommandRejectionText 与 OnboardController 里 WIRE_TO_GATE_NOT_READY 那一句才可以重新说禁止扫码。
    /// </para>
    /// <para>
    /// <b>这不是一个一直敞开的洞。</b>假服务端不因车动降级会话（RequireSafeSafetyForReadiness 默认关）；它扮演的是真服务端
    /// 的降级回复到达之前那一段。真服务端收到 departureSafe=false 会把会话降出 Ready（control-server 的
    /// WireToGateStore.DecideReadinessAsync，豁免只给本车在途装卸造成的门锁原因码），扫码入口随之关闭——中间隔一次
    /// 上报往返，这条量的就是那段窗口里本端的样子。
    /// </para>
    /// <para>
    /// 等的是「车载端把车在动报出去了」，不是「车动了」：替身一改状态就读断言，读到的可能是本端还没来得及知道车在动的
    /// 那一刻，入口开着就什么也证明不了。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task TheEntryStaysOpenWhileTheSessionIsReadyAndTheVehicleMoves()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        PushableVehicle vehicle = new();
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(token, vehicle: vehicle);
        Assert.True(harness.Business.CanSubmitSublot);

        vehicle.StartMoving();
        await BeforeSublotHarness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(envelope =>
                envelope.MessageType == "SafetyStateChanged" && ReportsMoving(envelope.WireLine)),
            "the onboard to report the vehicle moving",
            token);

        Assert.True(harness.Business.CanSubmitSublot);
    }

    private static bool ReportsMoving(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        JsonElement safety = document.RootElement.GetProperty("payload").GetProperty("safety");
        return !safety.GetProperty("vehicleStopped").GetBoolean()
            && !safety.GetProperty("departureSafe").GetBoolean();
    }

    private static WireToGateRecoveryOperationContext SettledLoad() =>
        new(
            "44444444-4444-4444-8444-444444444444",
            null,
            1,
            DateTimeOffset.UtcNow,
            DemandId,
            OperationSessionId,
            AttemptId,
            OperationType.Load,
            [1, 2],
            2,
            true,
            new string('0', 64));

    private static string NewJournalPath() =>
        Path.Combine(Path.GetTempPath(), "w2g-before-sublot", Guid.NewGuid().ToString("N"), "journal.db");

    private sealed class BeforeSublotHarness : IAsyncDisposable
    {
        private readonly WireToGateSessionService _session;
        private readonly SqliteWireToGateJournal _journal;
        private readonly List<WireToGateOperatorEvent> _blocked;
        private readonly bool _ownsServer;

        private BeforeSublotHarness(
            FakeControlServer server,
            bool ownsServer,
            FakeIoModuleClient io,
            WireToGateSessionService session,
            WireToGateBusinessService business,
            SqliteWireToGateJournal journal,
            List<WireToGateOperatorEvent> blocked)
        {
            Server = server;
            _ownsServer = ownsServer;
            Io = io;
            _session = session;
            Business = business;
            _journal = journal;
            _blocked = blocked;
        }

        public FakeControlServer Server { get; }

        public FakeIoModuleClient Io { get; }

        public WireToGateBusinessService Business { get; }

        public static FakeControlServer NewServer() =>
            new(IPAddress.Loopback)
            {
                SendReadinessAfterRecoveryAck = true,
                SendJourneySnapshotsAfterRecovery = true,
                // 一次重启会让同一辆车再收一遍同修订号的快照；时间戳每次都新，车载端会判成同修订内容冲突。
                ReplayJourneySnapshotsWithStableIdentity = true,
                OperationSessionId = OperationSessionId,
                SublotEntryExpectedSublots = ["SUBLOT-001"],
                RespondToLoadCancellationRequests = true
            };

        /// <param name="awaitEntryRequest">
        /// 等录入请求与工作清单到了再返回。重连时车载端自己收尾一次取消会清掉录入请求，与替身重发的请求谁先谁后不定，
        /// 那种用例不等。
        /// </param>
        /// <param name="seed">写进一份新日志的恢复状态；重启（沿用 <paramref name="journalPath"/>）时不写。</param>
        public static async Task<BeforeSublotHarness> StartAsync(
            CancellationToken cancellationToken,
            Action<FakeControlServer>? configure = null,
            WireToGateRecoveryState? seed = null,
            FakeControlServer? existingServer = null,
            string? journalPath = null,
            long baselineRevision = 1,
            bool awaitEntryRequest = true,
            IVehicleSafetySignalProvider? vehicle = null)
        {
            bool ownsServer = existingServer is null;
            FakeControlServer server = existingServer ?? NewServer();
            configure?.Invoke(server);

            try
            {
                // 目标仓里有货：一次清空会去开锁，所以 UnlockCount == 0 才说明真的没开。
                FakeIoModuleClient io = new();
                io.SetCargoPresent(0, true);
                io.SetCargoPresent(1, true);
                RecordingLogger logger = new();
                IVehicleSafetySignalProvider safety = vehicle ?? new StoppedVehicle();

                string databasePath = journalPath ?? NewJournalPath();
                Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
                SqliteWireToGateJournal journal = new(databasePath);
                await journal.InitializeAsync(cancellationToken);
                if (seed is not null)
                {
                    await journal.UpdateRecoveryStateAsync(_ => seed, cancellationToken);
                }

                WireToGateSessionService session = new(
                    new WireToGateSessionOptions(
                        "127.0.0.1",
                        server.Port,
                        "AGV-8005-01",
                        Guid.NewGuid().ToString("D"),
                        new string('a', 40),
                        CredentialVariable,
                        G2SessionTimeouts.Connect,
                        TimeSpan.FromSeconds(2),
                        baselineRevision,
                        baselineRevision,
                        "eight-slot-v1",
                        "eight-slot-modbus-v1",
                        SupportsBatchUnlock: false),
                    io,
                    journal,
                    logger,
                    new SystemClock(),
                    safety,
                    new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
                    new SlotConfigurationActivationCoordinator(
                        new DocumentActiveSlotConfigurationStore(
                            new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                            G2SlotConfigurationFixtures.Approved()),
                        TimeProvider.System),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(500));
                // 不传 WireToGateRecoveryOptions：出厂默认 ResumeAfterRepairEnabled=false。
                WireToGateBusinessService business = new(
                    session,
                    io,
                    logger,
                    new SystemClock(),
                    () => safety.Read().MotionState == VehicleMotionState.Stopped,
                    new WireToGateSlotOperationExecutorOptions(
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromMilliseconds(10),
                        TimeSpan.FromSeconds(30)),
                    OperatorVariable,
                    safety,
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMilliseconds(500));

                List<WireToGateOperatorEvent> blocked = [];
                business.OperatorEventPublished += (_, args) =>
                {
                    if (args.Value.Kind == "RECOVERY_BLOCKED")
                    {
                        lock (blocked)
                        {
                            blocked.Add(args.Value);
                        }
                    }
                };
                business.Start();
                await session.Client.ConnectAndRecoverAsync(cancellationToken);

                BeforeSublotHarness harness = new(
                    server, ownsServer, io, session, business, journal, blocked);
                if (!awaitEntryRequest)
                {
                    return harness;
                }

                // 录入请求跟在工作清单后面，入口判定两样都读。看 ExpectedSublots 而不看 CanSubmitSublot：
                // 重启前留下的扫码前取消没结时，提交子批本来就关着。
                await WaitUntilAsync(
                    () => business.ExpectedSublots is not null
                        && session.CurrentJourney.CurrentStopWorklist is not null,
                    "the entry request and its worklist to arrive",
                    cancellationToken);

                // 入口读的是会话就绪时刷新的恢复状态缓存；等它与日志一致，入口判定才有意义。
                WireToGateRecoveryState journaled = await journal.ReadRecoveryStateAsync(cancellationToken);
                if (seed is null && journaled.RecoveryVector is null)
                {
                    await WaitUntilAsync(
                        () => business.CanRequestLoadCancellation,
                        "the cancellation entry to be offered",
                        cancellationToken);
                }

                return harness;
            }
            catch
            {
                if (ownsServer)
                {
                    await server.DisposeAsync();
                }

                throw;
            }
        }

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(
            CancellationToken cancellationToken) =>
            _journal.ReadRecoveryStateAsync(cancellationToken);

        public IReadOnlyList<JsonElement> PayloadsReceived(string messageType) =>
            Payloads(Server.ReceivedEnvelopes, messageType);

        public IReadOnlyList<JsonElement> PayloadsSent(string messageType) =>
            Payloads(Server.SentEnvelopes, messageType);

        public IReadOnlyList<(string MessageId, JsonElement Payload)> ResultsReceived() =>
        [
            .. Server.ReceivedEnvelopes
                .Where(envelope => envelope.MessageType == "LoadCancellationResult")
                .Select(envelope => (envelope.MessageId, Payload(envelope.WireLine)))
        ];

        public async Task WaitForRecoveryBlockedAsync(
            string reasonCode,
            CancellationToken cancellationToken) =>
            await WaitUntilAsync(
                () =>
                {
                    lock (_blocked)
                    {
                        return _blocked.Any(
                            item => item.Message.Contains(reasonCode, StringComparison.Ordinal));
                    }
                },
                $"a RECOVERY_BLOCKED event naming {reasonCode}",
                cancellationToken);

        public static async Task WaitUntilAsync(
            Func<bool> predicate,
            string expectation,
            CancellationToken cancellationToken)
        {
            StallAwareDeadline deadline = new(TimeSpan.FromSeconds(5));
            while (!predicate())
            {
                if (deadline.HasExpired)
                {
                    Assert.Fail($"Timed out after {deadline.Describe()} waiting for: {expectation}");
                }

                await deadline.PollAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Business.DisposeAsync();
            await _session.DisposeAsync();
            if (_ownsServer)
            {
                await Server.DisposeAsync();
            }
        }

        private static IReadOnlyList<JsonElement> Payloads(
            IEnumerable<(int Connection, string MessageType, string MessageId, string WireLine)> envelopes,
            string messageType) =>
        [
            .. envelopes
                .Where(envelope => envelope.MessageType == messageType)
                .Select(envelope => Payload(envelope.WireLine))
        ];

        private static JsonElement Payload(string wireLine)
        {
            using JsonDocument document = JsonDocument.Parse(wireLine);
            return document.RootElement.GetProperty("payload").Clone();
        }

        /// <summary>车一直停着、读数一直新鲜；除了下面那一条，这里没有东西取决于运动状态。</summary>
        private sealed class StoppedVehicle : IVehicleSafetySignalProvider
        {
            public VehicleSafetySignal Read() =>
                new(VehicleMotionState.Stopped, DateTimeOffset.UtcNow, "BEFORE_SUBLOT_TEST");
        }
    }

    /// <summary>
    /// 先停着（握手与录入请求都要车停稳），再被外力推动。读数一直新鲜，所以动起来就是 Moving 而不是 Unknown。
    /// </summary>
    /// <remarks>
    /// 可订阅，与产品里的 <c>ControlServerVehicleSafetySignalProvider</c> 一样：业务服务只在可订阅的提供者报变化时
    /// 重算安全状态并上报，不可订阅的只等仓门快照或会话变化顺带触发，那样「车动了」要等别的东西碰巧发生才传得出去。
    /// </remarks>
    private sealed class PushableVehicle : IObservableVehicleSafetySignalProvider
    {
        private volatile bool _moving;

        public event EventHandler<ValueChangedEventArgs<VehicleSafetySignal>>? SignalChanged;

        public void StartMoving()
        {
            _moving = true;
            SignalChanged?.Invoke(this, new ValueChangedEventArgs<VehicleSafetySignal>(Read()));
        }

        public VehicleSafetySignal Read() =>
            new(
                _moving ? VehicleMotionState.Moving : VehicleMotionState.Stopped,
                DateTimeOffset.UtcNow,
                "BEFORE_SUBLOT_TEST");
    }
}
