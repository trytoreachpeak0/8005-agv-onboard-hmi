using System.Text.Json;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 服务端用同一个校验编号再问出发前安全检查（8005-agv-onboard-hmi#386）。
/// </summary>
/// <remarks>
/// <para>
/// <b>现场：</b>10-21 agv02 时钟比服务端快约 0.12 秒，回答的 <c>observedAt</c> 落在服务端的「未来」，被判无效；服务端发件箱
/// 按同一个 messageId、同一个 <c>preDepartureSafetyCheckId</c> 把这条检查再发一遍（真服务端 <c>DepartureCheckMessageId</c>
/// 每个停靠固定）。车载端再答时 <c>observedAt</c>、<c>validUntil</c> 现取，去重键 <c>predeparture:{check}:{version}:{outcome}</c>
/// 却不变，撞上 <c>BUSINESS_ID_CONTENT_CONFLICT</c>，不再作答，两边循环，最长 22 分钟。
/// </para>
/// <para>
/// <b>这里断的是：</b>再问一次就有一份新回答——新的 messageId、现取的 <c>observedAt</c> 与 <c>validUntil</c>，服务端收件箱
/// 按 messageId 收下它当作新的一行；车载端不报冲突。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 同一条检查（同 messageId、同编号、同版本）再到一次：第二份回答发出，内容是新的读数，不是第一份的原样重发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task TheSameCheckSentAgainIsAnsweredAgainWithAFreshReading()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartPreDepartureStopAsync(token);
        // As the SAFE case of APreDepartureCheckNamingTheFirstDemandIsAnsweredAsForOneItem: the handshake's safety
        // resend settles first, so the version read below is still the accepted one when the check arrives.
        await Task.Delay(300, token);

        long version = harness.Session.Current.SafetyStateVersion;
        string checkId = Guid.NewGuid().ToString("D");
        string requestId = Guid.NewGuid().ToString("D");
        await SendDepartureCheckAsync(harness, requestId, checkId, version);
        await WaitForPreDepartureResultsAsync(harness, 1, token);
        // The real server asks again a few seconds later; a short pause is enough for a fresh clock reading.
        await Task.Delay(50, token);
        await SendDepartureCheckAsync(harness, requestId, checkId, version);

        (string MessageId, string? CorrelationId, JsonElement Payload)[] results =
            await WaitForPreDepartureResultsAsync(harness, 2, token);
        (string firstId, string? firstCorrelation, JsonElement first) = results[0];
        (string secondId, string? secondCorrelation, JsonElement second) = results[1];
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(checkId, firstCorrelation);
        Assert.Equal(checkId, secondCorrelation);
        foreach (JsonElement answer in new[] { first, second })
        {
            Assert.Equal(checkId, answer.GetProperty("preDepartureSafetyCheckId").GetString());
            Assert.Equal("SAFE", answer.GetProperty("outcome").GetString());
            Assert.Equal(version, answer.GetProperty("safetyStateVersion").GetInt64());
            Assert.Equal(
                answer.GetProperty("observedAt").GetDateTimeOffset().AddSeconds(2),
                answer.GetProperty("validUntil").GetDateTimeOffset());
        }

        // Fresh, not the first answer replayed: the server judged the first one by exactly these times.
        Assert.True(
            second.GetProperty("observedAt").GetDateTimeOffset() > first.GetProperty("observedAt").GetDateTimeOffset(),
            $"The second answer was observed at {second.GetProperty("observedAt")}, not after the first at {first.GetProperty("observedAt")}.");
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.DoesNotContain(
            harness.Logger.Exceptions,
            entry => entry.Exception.Message == "BUSINESS_ID_CONTENT_CONFLICT");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 同一个编号再问时安全状态已经变了（一个仓读不到），服务端按新版本问：按新版本、新结论作答。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task TheSameCheckAskedAgainUnderANewSafetyVersionIsAnsweredUnderThatVersion()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartPreDepartureStopAsync(token, io);
        await Task.Delay(300, token);

        long version = harness.Session.Current.SafetyStateVersion;
        string checkId = Guid.NewGuid().ToString("D");
        await SendDepartureCheckAsync(harness, Guid.NewGuid().ToString("D"), checkId, version);
        await WaitForPreDepartureResultsAsync(harness, 1, token);

        io.SetUnreadable(3);
        io.PublishSnapshot();
        await WaitForReportedSafetyAcceptedAsync(
            harness,
            safety => safety.GetProperty("unknownPresent").GetBoolean(),
            "the unreadable slot",
            token);
        long newVersion = harness.Session.Current.SafetyStateVersion;
        Assert.True(newVersion > version);
        await SendDepartureCheckAsync(harness, Guid.NewGuid().ToString("D"), checkId, newVersion);

        (string MessageId, string? CorrelationId, JsonElement Payload)[] results =
            await WaitForPreDepartureResultsAsync(harness, 2, token);
        JsonElement second = results[1].Payload;
        Assert.Equal(checkId, second.GetProperty("preDepartureSafetyCheckId").GetString());
        Assert.Equal("UNKNOWN", second.GetProperty("outcome").GetString());
        Assert.Equal(newVersion, second.GetProperty("safetyStateVersion").GetInt64());
        Assert.Equal("SAFE", results[0].Payload.GetProperty("outcome").GetString());
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 一条持久消息在已登记的去重键下换了内容：照旧拒绝（异常信息仍是 <c>BUSINESS_ID_CONTENT_CONFLICT</c>，调用方按它分类），
    /// 技术日志里多一行，写清是哪一个键、哪几个字段不同、两份各是什么——10-21 的日志只有那一个原因码。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task AConflictUnderAKnownKeyIsLoggedWithTheKeyAndWhatDiffers()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartPreDepartureStopAsync(token);
        await Task.Delay(300, token);

        string checkId = Guid.NewGuid().ToString("D");
        long version = harness.Session.Current.SafetyStateVersion;
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        WireToGateSafetySummaryPayload safety = new(true, true, true, true, false, []);
        await harness.Session.Client.SendPreDepartureSafetyCheckResultAsync(
            checkId, "DEPARTURE", "SAFE", observedAt, version, observedAt.AddSeconds(2), safety, token);

        InvalidDataException conflict = await Assert.ThrowsAsync<InvalidDataException>(() =>
            harness.Session.Client.SendPreDepartureSafetyCheckResultAsync(
                checkId, "NON_BUSINESS_MOVE", "SAFE", observedAt, version, observedAt.AddSeconds(2), safety, token));

        Assert.Equal("BUSINESS_ID_CONTENT_CONFLICT", conflict.Message);
        (LogSeverity severity, _, string line) = Assert.Single(
            harness.Logger.Entries,
            entry => entry.Source == nameof(WireToGateSessionClient)
                && entry.Message.Contains("BUSINESS_ID_CONTENT_CONFLICT", StringComparison.Ordinal));
        // A refusal, filed apart from the Information-level publication diagnostics.
        Assert.Equal(LogSeverity.Warning, severity);
        Assert.Contains($"predeparture:{checkId}:", line, StringComparison.Ordinal);
        Assert.Contains("payload.checkPurpose", line, StringComparison.Ordinal);
        Assert.Contains("\"DEPARTURE\"", line, StringComparison.Ordinal);
        Assert.Contains("\"NON_BUSINESS_MOVE\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("payload.safety", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// 日志簿那一侧（审查 L2）：同一条检查答了两次而服务端都还没确认，两份各占发件箱一行、messageId 不同；车载端重启后
    /// 握手按原 messageId、原内容（<c>observedAt</c> 不重取）把两份都重放一遍，服务端确认后两行都记为已确认。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task TwoUnacknowledgedAnswersToOneCheckAreReplayedAsTheyWereAfterARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness first = await StartPreDepartureStopAsync(
            token,
            io,
            server => server.WithholdPreDepartureSafetyCheckResultAck = true);
        await Task.Delay(300, token);

        long version = first.Session.Current.SafetyStateVersion;
        string checkId = Guid.NewGuid().ToString("D");
        string requestId = Guid.NewGuid().ToString("D");
        await SendDepartureCheckAsync(first, requestId, checkId, version);
        await WaitForPreDepartureResultsAsync(first, 1, token);
        await Task.Delay(50, token);
        await SendDepartureCheckAsync(first, requestId, checkId, version);
        (string MessageId, string? CorrelationId, JsonElement Payload)[] answered =
            await WaitForPreDepartureResultsAsync(first, 2, token);
        Assert.NotEqual(answered[0].MessageId, answered[1].MessageId);

        await first.StopVehicleAsync();
        WireToGateDurableMessage[] owed;
        await using (SqliteWireToGateJournal journal = new(first.JournalPath))
        {
            owed =
            [
                .. (await journal.ReadUnacknowledgedOutgoingAsync(token))
                    .Where(row => row.MessageType == "PreDepartureSafetyCheckResult")
            ];
        }

        Assert.Equal(
            answered.Select(answer => answer.MessageId).Order(StringComparer.Ordinal),
            owed.Select(row => row.MessageId).Order(StringComparer.Ordinal));
        Assert.All(owed, row => Assert.StartsWith($"predeparture:{checkId}:", row.DeduplicationKey, StringComparison.Ordinal));

        int deliveredBefore = first.Server.ReceivedEnvelopes.Count(item => item.MessageType == "PreDepartureSafetyCheckResult");
        first.Server.WithholdPreDepartureSafetyCheckResultAck = false;
        first.Server.SimulateOnboardProcessRestart();
        await using Harness second = await Harness.StartAgainstAsync(first.Server, token, first.JournalPath, io);
        await second.WaitUntilAsync(
            () => second.Server.ReceivedEnvelopes.Count(item => item.MessageType == "PreDepartureSafetyCheckResult")
                >= deliveredBefore + 2,
            "both owed answers to be replayed after the restart",
            token);

        (string MessageId, string? CorrelationId, JsonElement Payload)[] all =
            await WaitForPreDepartureResultsAsync(second, deliveredBefore + 2, token);
        foreach ((string messageId, _, JsonElement payload) in answered)
        {
            (_, _, JsonElement replayed) = Assert.Single(all.Skip(deliveredBefore), item => item.MessageId == messageId);
            Assert.Equal(
                payload.GetProperty("observedAt").GetDateTimeOffset(),
                replayed.GetProperty("observedAt").GetDateTimeOffset());
            Assert.Equal(
                payload.GetProperty("validUntil").GetDateTimeOffset(),
                replayed.GetProperty("validUntil").GetDateTimeOffset());
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            bool acknowledged = true;
            await using (SqliteWireToGateJournal journal = new(first.JournalPath))
            {
                foreach (WireToGateDurableMessage row in owed)
                {
                    acknowledged &= await journal.ReadOutgoingByDeduplicationKeyAsync(row.DeduplicationKey, token)
                        is { Acknowledged: true };
                }
            }

            if (acknowledged)
            {
                break;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "Timed out waiting for both replayed answers to be acknowledged on file.");
            await Task.Delay(100, token);
        }
        Assert.DoesNotContain(second.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Empty(second.UiErrors);
    }

    private static async Task<Harness> StartPreDepartureStopAsync(
        CancellationToken token,
        FakeIoModuleClient? io = null,
        Action<FakeControlServer>? configure = null)
    {
        Harness harness = await Harness.StartAsync(
            server =>
            {
                configure?.Invoke(server);
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
            },
            token,
            io: io ?? new FakeIoModuleClient { OperatorNeverActs = true });
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null && harness.Session.Current.SafetyStateVersion > 0,
            "the worklist and an accepted safety state",
            token);
        return harness;
    }

    private static Task SendDepartureCheckAsync(Harness harness, string requestId, string checkId, long version) =>
        harness.Server.SendCommandAsync(
            "PreDepartureSafetyCheck",
            requestId,
            new
            {
                preDepartureSafetyCheckId = checkId,
                checkPurpose = "DEPARTURE",
                demandId = DemandA,
                movementLegId = "22222222-2222-4222-8222-000000000002",
                expectedSafetyStateVersion = version,
                targetStationId = "ST-GATE"
            });

    /// <summary>Waits for at least <paramref name="count"/> answers and returns them in the order received.</summary>
    private static async Task<(string MessageId, string? CorrelationId, JsonElement Payload)[]> WaitForPreDepartureResultsAsync(
        Harness harness,
        int count,
        CancellationToken token)
    {
        await harness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Count(item => item.MessageType == "PreDepartureSafetyCheckResult") >= count,
            $"the control server to receive {count} PreDepartureSafetyCheckResult",
            token);
        return
        [
            .. harness.Server.ReceivedEnvelopes
                .Where(item => item.MessageType == "PreDepartureSafetyCheckResult")
                .Select(item =>
                {
                    using JsonDocument document = JsonDocument.Parse(item.WireLine);
                    JsonElement root = document.RootElement;
                    return (
                        root.GetProperty("messageId").GetString()!,
                        root.TryGetProperty("correlationId", out JsonElement correlation) ? correlation.GetString() : null,
                        root.GetProperty("payload").Clone());
                })
        ];
    }
}
