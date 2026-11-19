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
        Assert.DoesNotContain("/unlock", openApiText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunIdMismatchErrorCarriesTheProcessRunIdNotTheAgvId()
    {
        await using AutomationHostHarness harness = await AutomationHostHarness.StartAsync();
        string foreignRunId = "33333333-3333-4333-8333-333333333333";
        Assert.NotEqual(harness.Server.RunId, foreignRunId);

        JsonDocument error = await harness.SubmitExpectingConflictAsync(new
        {
            runId = foreignRunId,
            commandId = "44444444-4444-4444-8444-444444444444",
            expectedRevision = await harness.ReadRevisionAsync(),
            sublot = "SUBLOT-001"
        });

        Assert.Equal("RUN_ID_MISMATCH", error.RootElement.GetProperty("reasonCode").GetString());
        string? responseRunId = error.RootElement.GetProperty("runId").GetString();
        Assert.Equal(harness.Server.RunId, responseRunId);
        Assert.NotEqual(FakeFacade.AgvId, responseRunId);
    }

    [Fact]
    public async Task CommandIdConflictErrorCarriesTheProcessRunId()
    {
        await using AutomationHostHarness harness = await AutomationHostHarness.StartAsync();
        string commandId = "55555555-5555-4555-8555-555555555555";
        long revision = await harness.ReadRevisionAsync();
        HttpResponseMessage first = await harness.Client.PostAsJsonAsync(
            "/api/v1/sublots/submit",
            new { runId = harness.Server.RunId, commandId, expectedRevision = revision, sublot = "SUBLOT-001" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        JsonDocument error = await harness.SubmitExpectingConflictAsync(new
        {
            runId = harness.Server.RunId,
            commandId,
            expectedRevision = revision,
            sublot = "SUBLOT-002"
        });

        Assert.Equal("COMMAND_ID_CONFLICT", error.RootElement.GetProperty("reasonCode").GetString());
        Assert.Equal(harness.Server.RunId, error.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task RevisionConflictErrorCarriesTheProcessRunId()
    {
        await using AutomationHostHarness harness = await AutomationHostHarness.StartAsync();
        long revision = await harness.ReadRevisionAsync();

        JsonDocument error = await harness.SubmitExpectingConflictAsync(new
        {
            runId = harness.Server.RunId,
            commandId = "66666666-6666-4666-8666-666666666666",
            expectedRevision = revision + 1,
            sublot = "SUBLOT-001"
        });

        Assert.Equal("REVISION_CONFLICT", error.RootElement.GetProperty("reasonCode").GetString());
        Assert.Equal(harness.Server.RunId, error.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task PathNotFoundErrorCarriesTheProcessRunId()
    {
        await using AutomationHostHarness harness = await AutomationHostHarness.StartAsync();

        HttpResponseMessage response = await harness.Client.PostAsync(
            "/api/v1/slots/1/unlock",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        JsonDocument error = (await response.Content.ReadFromJsonAsync<JsonDocument>(
            cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal("PATH_NOT_FOUND", error.RootElement.GetProperty("reasonCode").GetString());
        Assert.Equal(harness.Server.RunId, error.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task InvalidRequestErrorCarriesTheProcessRunId()
    {
        await using AutomationHostHarness harness = await AutomationHostHarness.StartAsync();

        HttpResponseMessage response = await harness.Client.PostAsJsonAsync(
            "/api/v1/sublots/submit",
            new
            {
                runId = harness.Server.RunId,
                commandId = "77777777-7777-4777-8777-777777777777",
                expectedRevision = await harness.ReadRevisionAsync(),
                sublot = ""
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonDocument error = (await response.Content.ReadFromJsonAsync<JsonDocument>(
            cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal("INVALID_HTTP_REQUEST", error.RootElement.GetProperty("reasonCode").GetString());
        Assert.Equal(harness.Server.RunId, error.RootElement.GetProperty("runId").GetString());
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

    [Fact]
    public async Task ANewPollOfTheSameIoReadingsKeepsTheRevision()
    {
        // PR #412 review L1 (8005-agv-onboard-hmi#404): every poll of the IO module stamps a new ObservedAt and a new
        // monotonic ObservedTimestamp. The fingerprint drops observedAt; the monotonic stamp must not reach it either, or
        // the revision moves every 100 ms against a real module and /sublots/submit answers REVISION_CONFLICT.
        FakeFacade facade = new();
        await using AutomationHostHarness harness = await AutomationHostHarness.StartAsync(facade);
        DateTimeOffset firstAt = DateTimeOffset.UtcNow;
        facade.ShowIo(ConnectedIo(firstAt, timestamp: 1_000_000, lockFeedback: true));
        long revision = await harness.ReadRevisionAsync();

        facade.ShowIo(ConnectedIo(firstAt.AddMilliseconds(100), timestamp: 2_000_000, lockFeedback: true));
        Assert.Equal(revision, await harness.ReadRevisionAsync());

        // Control: a reading that did change still moves it.
        facade.ShowIo(ConnectedIo(firstAt.AddMilliseconds(200), timestamp: 3_000_000, lockFeedback: false));
        Assert.Equal(revision + 1, await harness.ReadRevisionAsync());
    }

    private static IoSnapshot ConnectedIo(DateTimeOffset observedAt, long timestamp, bool lockFeedback) =>
        new(
            true,
            Enumerable.Range(0, 8)
                .Select(index => new LockerSnapshot(index, index + 1, false, lockFeedback, true, observedAt)
                {
                    ObservedTimestamp = timestamp
                })
                .ToArray(),
            observedAt)
        {
            ObservedTimestamp = timestamp
        };

    private static int GetFreePort()
    {
        using System.Net.Sockets.TcpListener listener =
            new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class AutomationHostHarness : IAsyncDisposable
    {
        private AutomationHostHarness(OnboardAutomationHttpServer server)
        {
            Server = server;
            Client = new HttpClient { BaseAddress = new Uri(server.Endpoint) };
        }

        public OnboardAutomationHttpServer Server { get; }

        public HttpClient Client { get; }

        public static async Task<AutomationHostHarness> StartAsync(FakeFacade? facade = null)
        {
            OnboardAutomationHttpServer server = new(
                new OnboardAutomationHostOptions("127.0.0.1", GetFreePort()),
                facade ?? new FakeFacade());
            await server.StartAsync(TestContext.Current.CancellationToken);
            return new AutomationHostHarness(server);
        }

        public async Task<long> ReadRevisionAsync()
        {
            JsonDocument snapshot = (await Client.GetFromJsonAsync<JsonDocument>(
                "/api/v1/snapshot",
                TestContext.Current.CancellationToken))!;
            return snapshot.RootElement.GetProperty("revision").GetInt64();
        }

        public async Task<JsonDocument> SubmitExpectingConflictAsync(object request)
        {
            HttpResponseMessage response = await Client.PostAsJsonAsync(
                "/api/v1/sublots/submit",
                request,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonDocument>(
                cancellationToken: TestContext.Current.CancellationToken))!;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
        }
    }

    private sealed class FakeFacade : IOnboardAutomationFacade
    {
        public const string AgvId = "AGV-8005-01";

        private OnboardAutomationSnapshot _snapshot = CreateSnapshot(IoSnapshot.Unknown(DateTimeOffset.UtcNow));

        public int SubmitCount { get; private set; }

        public OnboardAutomationSnapshot ReadSnapshot() => _snapshot;

        public void ShowIo(IoSnapshot io) => _snapshot = CreateSnapshot(io);

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

        private static OnboardAutomationSnapshot CreateSnapshot(IoSnapshot io)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
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
                AgvId,
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
                now);
        }
    }
}
