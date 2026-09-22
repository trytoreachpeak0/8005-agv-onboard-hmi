using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 严重安全故障锁存期间，车载端对服务端报「不能出发」（8005-agv-onboard-hmi#197）。
/// </summary>
/// <remarks>
/// <para>
/// <b>缺陷：</b>#191 之前与之后，<c>WireToGateSafetyEvaluator</c> 都不读锁存，锁存中的车照常报 <c>departureSafe=true</c>，
/// 服务端照常认为它可以发车、派往下一站，到站开门时才被 #191 拒绝。
/// </para>
/// <para>
/// <b>断在车载端发出去的那份安全摘要上</b>（假服务端收到的报文原文），再断假服务端据此作出的就绪判定：
/// <c>RequireSafeSafetyForReadiness</c> 打开时，它按真服务端 <c>DecideReadinessAsync</c> 的规则，出发不安全就答
/// <c>DEPARTURE_UNSAFE</c>（<c>DEPARTURE_SAFETY_NOT_READY</c> 的线上码）。锁存与复位走真控制器
/// （<c>EnterFatalFault</c> / <c>ClearFatalFaultAsync</c>），夹具与 App 一样把控制器的 <c>StateChanged</c> 接到业务服务。
/// </para>
/// <para>
/// <b>原因码：</b>协议 v2.0.0 没有「车载端锁存」的码，借用 <c>DEPARTURE_UNSAFE</c>，理由与不能用 <c>VEHICLE_NOT_READY</c>
/// 的原因写在 <see cref="WireToGateSafetyEvaluator.FatalFaultLatchedReason"/>；专用码在 program#115（v3.0.0 待办）。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    private const string DepartureUnsafe = "DEPARTURE_UNSAFE";

    /// <summary>
    /// 锁存：下一份安全摘要出发不安全，原因只有锁存这一项，四个物理字段照旧（门都锁着、输出复位、车停着、没有未知）；
    /// 服务端把会话降出 Ready。复位：出发安全与就绪都回来。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task ALatchReportsDepartureUnsafeAndTheServerStopsCallingTheSessionReadyUntilItIsCleared()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(io, token, server =>
        {
            server.RequireSafeSafetyForReadiness = true;
            server.SendReadinessAfterSafetyStateChangedAck = true;
        });
        JsonElement before = LatestReportedSafety(harness);
        Assert.True(before.GetProperty("departureSafe").GetBoolean());
        Assert.Empty(SafetyReasons(before));

        await WaitForSafetyReportsToSettleAsync(harness, token);
        LatchNow(harness);

        JsonElement latched = await WaitForReportedSafetyAsync(harness, departureSafe: false, token);
        Assert.Equal([DepartureUnsafe], SafetyReasons(latched));
        Assert.True(latched.GetProperty("vehicleStopped").GetBoolean());
        Assert.True(latched.GetProperty("allTargetSlotsLocked").GetBoolean());
        Assert.True(latched.GetProperty("allUnlockOutputsReset").GetBoolean());
        Assert.False(latched.GetProperty("unknownPresent").GetBoolean());
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired,
            "the server to take the latched vehicle's session out of Ready",
            token);
        Assert.Contains(DepartureUnsafe, harness.Session.Current.ReasonCodes);

        Assert.True(await harness.Controller.ClearFatalFaultAsync("operator-134", token));

        JsonElement cleared = await WaitForReportedSafetyAsync(harness, departureSafe: true, token);
        Assert.Empty(SafetyReasons(cleared));
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the session to be Ready again after the latch is cleared",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 出发前安全检查是另一条路（<c>HandlePreDepartureSafetyCheckAsync</c> 现场再判一次），服务端据它授权移动
    /// （<c>FindSafeDepartureResultAsync</c> 只认 SAFE）。锁存期间答 UNSAFE——不是 UNKNOWN：锁存是确知的状态。
    /// </summary>
    /// <remarks>
    /// 假服务端这里不按安全判就绪，会话留在 Ready，检查才会被作答：真服务端在会话不 Ready 时不会发这条检查，
    /// 这条用例证的是「万一问到，答案也对」。先等锁存那份 <c>SafetyStateChanged</c> 被接受，再按当前版本发问，
    /// 否则检查可能与那一份交错而被判过期。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task ALatchedVehicleAnswersThePreDepartureSafetyCheckUnsafe()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(io, token);

        await WaitForSafetyReportsToSettleAsync(harness, token);
        LatchNow(harness);
        await WaitForReportedSafetyAsync(harness, departureSafe: false, token);
        // Tied to the latched change's own version, not to "some change was accepted": the business service resends
        // the full safety state right after the handshake, so a looser wait can pass before the latched one is
        // acknowledged, and a check asked under the older version is then refused as PREDEPARTURE_CHECK_EXPIRED.
        long latchedVersion = harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "SafetyStateChanged")
            .Select(envelope =>
            {
                using JsonDocument document = JsonDocument.Parse(envelope.WireLine);
                JsonElement payload = document.RootElement.GetProperty("payload");
                return (Safe: payload.GetProperty("safety").GetProperty("departureSafe").GetBoolean(),
                    Version: payload.GetProperty("safetyStateVersion").GetInt64());
            })
            .Last(change => !change.Safe)
            .Version;
        await harness.WaitUntilAsync(
            () => harness.Session.Current.SafetyStateVersion >= latchedVersion,
            $"the latched safety state (version {latchedVersion}) to be accepted",
            token);

        string checkId = Guid.NewGuid().ToString("D");
        await harness.Server.SendCommandAsync(
            "PreDepartureSafetyCheck",
            Guid.NewGuid().ToString("D"),
            new
            {
                preDepartureSafetyCheckId = checkId,
                demandId = DemandA,
                movementLegId = "22222222-2222-4222-8222-000000000002",
                expectedSafetyStateVersion = harness.Session.Current.SafetyStateVersion,
                targetStationId = "ST-GATE"
            });

        JsonElement result = await WaitForPayloadAsync(harness, "PreDepartureSafetyCheckResult", token);
        Assert.Equal(checkId, result.GetProperty("preDepartureSafetyCheckId").GetString());
        Assert.Equal("UNSAFE", result.GetProperty("outcome").GetString());
        JsonElement safety = result.GetProperty("safety");
        Assert.False(safety.GetProperty("departureSafe").GetBoolean());
        Assert.Equal([DepartureUnsafe], SafetyReasons(safety));
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
    }

    /// <summary>
    /// 锁存中的车断线重连：新会话握手里那份全量 <c>SafetyStateSnapshot</c> 由会话客户端自己算，不经业务服务——它也必须
    /// 带上锁存。否则服务端在握手那一刻就判它 Ready，直到业务服务补发的那一份才降下来，中间正是派车的窗口。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task ALatchedVehicleThatReconnectsReportsDepartureUnsafeInTheHandshake()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(io, token);
        await WaitForSafetyReportsToSettleAsync(harness, token);
        LatchNow(harness);
        await WaitForReportedSafetyAsync(harness, departureSafe: false, token);
        int firstConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);

        // The harness connects the client itself rather than running the session service's reconnect loop, so the
        // test reconnects it, as the other reconnect cases do.
        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);

        JsonElement[] handshake = SafetyPayloads(harness, "SafetyStateSnapshot", afterConnection: firstConnection);
        JsonElement safety = Assert.Single(handshake);
        Assert.False(safety.GetProperty("departureSafe").GetBoolean());
        Assert.Equal([DepartureUnsafe], SafetyReasons(safety));
    }

    /// <summary>
    /// 启动之后业务服务按「换代必须全量补报」补发的那一份 <c>SafetyStateChanged</c> 已被服务端接受，此后没有正在发的。
    /// </summary>
    /// <remarks>
    /// 锁存之前必须等它：那一份若还在算，就会顺手把锁存带出去，用例便证不到「锁存本身会触发重报」——反向验证去掉
    /// <c>controller.StateChanged</c> 那根线时，锁存方向照样绿，就是这个原因。
    /// <para>
    /// 等到这里仍不够让第一条用例守住那根线：它打开了 <c>SendReadinessAfterSafetyStateChangedAck</c>，确认后面紧跟一条
    /// 就绪通知，会话状态一变业务服务就再算一次安全（<c>OnSessionStateChanged</c>），也会把锁存带出去。那根线由另外两条
    /// 用例守着（去掉它时只红那两条），第一条守的是就绪那一层。
    /// </para>
    /// </remarks>
    private static Task WaitForSafetyReportsToSettleAsync(Harness harness, CancellationToken token) =>
        harness.WaitUntilAsync(
            () =>
            {
                long[] sent = [.. harness.Server.ReceivedEnvelopes
                    .Where(envelope => envelope.MessageType == "SafetyStateChanged")
                    .Select(envelope =>
                    {
                        using JsonDocument document = JsonDocument.Parse(envelope.WireLine);
                        return document.RootElement.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64();
                    })];
                return sent.Length > 0 && harness.Session.Current.SafetyStateVersion >= sent.Max();
            },
            "the full safety state resent after the handshake to be accepted",
            token);

    /// <summary>车载端最近一次报给服务端的安全摘要，全量快照与增量变化都算。</summary>
    private static JsonElement LatestReportedSafety(Harness harness) =>
        harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType is "SafetyStateSnapshot" or "SafetyStateChanged")
            .Select(envelope => SafetyOf(envelope.WireLine))
            .Last();

    private static async Task<JsonElement> WaitForReportedSafetyAsync(
        Harness harness,
        bool departureSafe,
        CancellationToken token)
    {
        JsonElement latest = default;
        await harness.WaitUntilAsync(
            () =>
            {
                latest = LatestReportedSafety(harness);
                return latest.GetProperty("departureSafe").GetBoolean() == departureSafe;
            },
            $"a reported safety summary with departureSafe={departureSafe}",
            token);
        return latest;
    }

    private static JsonElement[] SafetyPayloads(Harness harness, string messageType, int afterConnection) =>
    [
        .. harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == messageType && envelope.Connection > afterConnection)
            .Select(envelope => SafetyOf(envelope.WireLine))
    ];

    private static JsonElement SafetyOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("payload").GetProperty("safety").Clone();
    }

    private static string[] SafetyReasons(JsonElement safety) =>
        [.. safety.GetProperty("reasonCodes").EnumerateArray().Select(code => code.GetString()!)];
}
