using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SQCD.Agv.AutomationHost;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

public sealed class OnboardAutomationHostTests
{
    [Fact]
    public async Task HostExposesReadOnlyHealthSnapshotOpenApiAndIdempotentSubmit()
    {
        int port = GetFreePort();
        FakeFacade facade = new();
        await using OnboardAutomationHttpServer server = new(
            new OnboardAutomationHostOptions("127.0.0.1", port),
            facade);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = new()
        {
            BaseAddress = new Uri(server.Endpoint)
        };

        HttpResponseMessage health = await client.GetAsync(
            "/api/v1/health",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        JsonDocument healthJson = (await health.Content.ReadFromJsonAsync<JsonDocument>(
            cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal(server.RunId, healthJson.RootElement.GetProperty("runId").GetString());

        HttpResponseMessage snapshot = await client.GetAsync(
            "/api/v1/snapshot",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        JsonDocument snapshotJson = (await snapshot.Content.ReadFromJsonAsync<JsonDocument>(
            cancellationToken: TestContext.Current.CancellationToken))!;
        long revision = snapshotJson.RootElement.GetProperty("revision").GetInt64();
        JsonElement snapshotState = snapshotJson.RootElement.GetProperty("state");
        Assert.Equal(JsonValueKind.Null, snapshotState.GetProperty("currentOperationPhase").ValueKind);

        string commandId = "11111111-1111-4111-8111-111111111111";
        var request = new
        {
            runId = server.RunId,
            commandId,
            expectedRevision = revision,
            sublot = "SUBLOT-001"
        };
        HttpResponseMessage first = await client.PostAsJsonAsync(
            "/api/v1/sublots/submit",
            request,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        HttpResponseMessage replay = await client.PostAsJsonAsync(
            "/api/v1/sublots/submit",
            request,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        JsonDocument replayJson = (await replay.Content.ReadFromJsonAsync<JsonDocument>(
            cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.True(replayJson.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, facade.SubmitCount);

        HttpResponseMessage unsafeEndpoint = await client.PostAsync(
            "/api/v1/slots/1/unlock",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unsafeEndpoint.StatusCode);

        HttpResponseMessage openApi = await client.GetAsync(
            "/openapi/v1.json",
            TestContext.Current.CancellationToken);
        string openApiText = await openApi.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("/api/v1/sublots/submit", openApiText, StringComparison.Ordinal);
        Assert.Contains("/api/v1/recovery/requests", openApiText, StringComparison.Ordinal);
        Assert.DoesNotContain("/unlock", openApiText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostRejectsNonLoopbackBinding()
    {
        Assert.Throws<InvalidDataException>(() => new OnboardAutomationHttpServer(
            new OnboardAutomationHostOptions("0.0.0.0", 58007),
            new FakeFacade()));
    }

    [Fact]
    public void HmiOperationStagesMapToTheFormalProgressVocabulary()
    {
        Assert.Equal("PREPARING", WireToGateHmiOperationStage.Preparing.ToProtocolPhase());
        Assert.Equal("UNLOCKING", WireToGateHmiOperationStage.Unlocking.ToProtocolPhase());
        Assert.Equal("WAITING_OPERATOR", WireToGateHmiOperationStage.WaitingOperator.ToProtocolPhase());
        Assert.Equal("VERIFYING", WireToGateHmiOperationStage.Verifying.ToProtocolPhase());
        Assert.Equal("SAFE_FINISH", WireToGateHmiOperationStage.Reporting.ToProtocolPhase());
        Assert.Equal("PAUSED", WireToGateHmiOperationStage.RecoveryRequired.ToProtocolPhase());
        Assert.Null(WireToGateHmiOperationStage.Completed.ToProtocolPhase());
    }

    private static int GetFreePort()
    {
        using System.Net.Sockets.TcpListener listener =
            new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// A recovery goes out once per commandId, reaches the facade with the host's provenance mark on
    /// its reason, and a refusal comes back carrying the refusal's own code rather than a generic one.
    /// </summary>
    [Fact]
    public async Task RecoveryRequestIsIdempotentAndReturnsTheRefusalCodeVerbatim()
    {
        FakeFacade facade = new()
        {
            RecoveryResponder = (action, _) => Task.FromResult(action == OnboardAutomationRecoveryActions.CompensateLoadAllEmpty
                ? WireToGateRecoveryRequestOutcome.Succeeded
                : WireToGateRecoveryRequestOutcome.Refused("RECOVERY_DEMAND_NOT_BLOCKED"))
        };
        await using OnboardAutomationHttpServer server = new(
            new OnboardAutomationHostOptions("127.0.0.1", GetFreePort()),
            facade);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = new() { BaseAddress = new Uri(server.Endpoint) };

        var accepted = new
        {
            runId = server.RunId,
            commandId = "33333333-3333-4333-8333-333333333333",
            expectedRevision = 1,
            action = OnboardAutomationRecoveryActions.CompensateLoadAllEmpty,
            reason = "  无人验收：补偿清空。 "
        };
        JsonElement first = await PostRecoveryAsync(client, accepted, HttpStatusCode.OK);
        Assert.Equal("ACCEPTED", first.GetProperty("status").GetString());
        Assert.False(first.GetProperty("replayed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("reasonCode").ValueKind);
        JsonElement replay = await PostRecoveryAsync(client, accepted, HttpStatusCode.OK);
        Assert.Equal("ACCEPTED", replay.GetProperty("status").GetString());
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        (string action, string reason, bool cancellable) = Assert.Single(facade.RecoveryRequests);
        Assert.Equal(OnboardAutomationRecoveryActions.CompensateLoadAllEmpty, action);
        Assert.Equal(OnboardAutomationApi.RecoveryReasonPrefix + "无人验收：补偿清空。", reason);
        // A caller that hangs up must not abort a recovery halfway through its slot IO.
        Assert.False(cancellable);

        JsonElement refused = await PostRecoveryAsync(
            client,
            accepted with
            {
                commandId = "44444444-4444-4444-8444-444444444444",
                action = OnboardAutomationRecoveryActions.ResumeAfterRepair
            },
            HttpStatusCode.Conflict);
        Assert.Equal("REJECTED", refused.GetProperty("status").GetString());
        Assert.Equal("RECOVERY_DEMAND_NOT_BLOCKED", refused.GetProperty("reasonCode").GetString());

        JsonElement conflict = await PostRecoveryAsync(
            client,
            accepted with { reason = "换了理由" },
            HttpStatusCode.Conflict);
        Assert.Equal("COMMAND_ID_CONFLICT", conflict.GetProperty("reasonCode").GetString());
        Assert.Equal(server.RunId, conflict.GetProperty("runId").GetString());
        Assert.Equal(2, facade.RecoveryRequests.Count);
    }

    /// <summary>
    /// Cancelling a load that is underway runs the whole clearing vector inside the request and waits
    /// on an operator. The host answers IN_PROGRESS instead of holding the connection, refuses a second
    /// recovery while the first is still running, and hands the final outcome to a replay.
    /// </summary>
    [Fact]
    public async Task RecoveryStillRunningAnswersInProgressAndItsReplayReadsTheOutcome()
    {
        TaskCompletionSource<WireToGateRecoveryRequestOutcome> running =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeFacade facade = new() { RecoveryResponder = (_, _) => running.Task };
        await using OnboardAutomationHttpServer server = new(
            new OnboardAutomationHostOptions(
                "127.0.0.1",
                GetFreePort(),
                TimeSpan.FromMilliseconds(200)),
            facade);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = new() { BaseAddress = new Uri(server.Endpoint) };

        var request = new
        {
            runId = server.RunId,
            commandId = "55555555-5555-4555-8555-555555555555",
            expectedRevision = 1,
            action = OnboardAutomationRecoveryActions.LoadCancellation,
            reason = "在途取消。"
        };
        JsonElement pending = await PostRecoveryAsync(client, request, HttpStatusCode.Accepted);
        Assert.Equal("IN_PROGRESS", pending.GetProperty("status").GetString());

        JsonElement blocked = await PostRecoveryAsync(
            client,
            request with { commandId = "66666666-6666-4666-8666-666666666666" },
            HttpStatusCode.Conflict);
        Assert.Equal("RECOVERY_REQUEST_IN_PROGRESS", blocked.GetProperty("reasonCode").GetString());

        running.SetResult(WireToGateRecoveryRequestOutcome.Refused("OPERATION_RECOVERY_REQUIRED"));
        JsonElement settled = await PostRecoveryAsync(client, request, HttpStatusCode.Conflict);
        Assert.Equal("REJECTED", settled.GetProperty("status").GetString());
        Assert.True(settled.GetProperty("replayed").GetBoolean());
        Assert.Equal("OPERATION_RECOVERY_REQUIRED", settled.GetProperty("reasonCode").GetString());
        Assert.Single(facade.RecoveryRequests);
    }

    [Fact]
    public async Task RecoveryRequestRequiresAKnownActionAndAReason()
    {
        FakeFacade facade = new();
        await using OnboardAutomationHttpServer server = new(
            new OnboardAutomationHostOptions("127.0.0.1", GetFreePort()),
            facade);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = new() { BaseAddress = new Uri(server.Endpoint) };
        var request = new
        {
            runId = server.RunId,
            commandId = "77777777-7777-4777-8777-777777777777",
            expectedRevision = 1,
            action = "UNLOCK_SLOT",
            reason = "想直接开锁。"
        };

        JsonElement unknown = await PostRecoveryAsync(client, request, HttpStatusCode.BadRequest);
        Assert.Equal("RECOVERY_ACTION_UNKNOWN", unknown.GetProperty("reasonCode").GetString());
        JsonElement noReason = await PostRecoveryAsync(
            client,
            request with { action = OnboardAutomationRecoveryActions.CompensateLoadAllEmpty, reason = " " },
            HttpStatusCode.BadRequest);
        Assert.Equal("INVALID_HTTP_REQUEST", noReason.GetProperty("reasonCode").GetString());
        Assert.Empty(facade.RecoveryRequests);
    }

    private static async Task<JsonElement> PostRecoveryAsync(
        HttpClient client,
        object request,
        HttpStatusCode expectedStatus)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/recovery/requests",
            request,
            TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(expectedStatus == response.StatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private sealed class FakeFacade : IOnboardAutomationFacade
    {
        private readonly OnboardAutomationSnapshot _snapshot = CreateSnapshot();
        private readonly List<(string Action, string Reason, bool Cancellable)> _recoveryRequests = [];

        public int SubmitCount { get; private set; }

        public Func<string, string, Task<WireToGateRecoveryRequestOutcome>> RecoveryResponder { get; init; } =
            (_, _) => Task.FromResult(WireToGateRecoveryRequestOutcome.Succeeded);

        public IReadOnlyList<(string Action, string Reason, bool Cancellable)> RecoveryRequests
        {
            get
            {
                lock (_recoveryRequests)
                {
                    return _recoveryRequests.ToArray();
                }
            }
        }

        public OnboardAutomationSnapshot ReadSnapshot() => _snapshot;

        public async Task<OnboardAutomationRecoveryOutcome> RequestRecoveryAsync(
            string action,
            string reason,
            CancellationToken cancellationToken = default)
        {
            lock (_recoveryRequests)
            {
                _recoveryRequests.Add((action, reason, cancellationToken.CanBeCanceled));
            }

            WireToGateRecoveryRequestOutcome outcome = await RecoveryResponder(action, reason);
            return new OnboardAutomationRecoveryOutcome(outcome.Accepted, outcome.ReasonCode, _snapshot);
        }

        public Task<OnboardAutomationSubmitOutcome> SubmitSublotAsync(
            string sublot,
            CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            return Task.FromResult(new OnboardAutomationSubmitOutcome(
                true,
                "22222222-2222-4222-8222-222222222222",
                null,
                _snapshot));
        }

        private static OnboardAutomationSnapshot CreateSnapshot()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            IoSnapshot io = IoSnapshot.Unknown(now);
            OnboardSnapshot onboard = new(
                OnboardState.ReadyToScan,
                true,
                true,
                null,
                io,
                null,
                false,
                "等待子批录入。",
                null,
                now);
            return new OnboardAutomationSnapshot(
                "AGV-8005-01",
                onboard,
                new WireToGateSessionSnapshot(
                    true,
                    1,
                    WireToGateSessionReadiness.Ready,
                    [],
                    1,
                    1,
                    now),
                WireToGateJourneySnapshot.Empty,
                WireToGateRecoveryState.Empty,
                true,
                ["SUBLOT-001"],
                null,
                null,
                [],
                now);
        }
    }
}
