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

    private sealed class FakeFacade : IOnboardAutomationFacade
    {
        private readonly OnboardAutomationSnapshot _snapshot = CreateSnapshot();

        public int SubmitCount { get; private set; }

        public OnboardAutomationSnapshot ReadSnapshot() => _snapshot;

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
                now);
        }
    }
}
