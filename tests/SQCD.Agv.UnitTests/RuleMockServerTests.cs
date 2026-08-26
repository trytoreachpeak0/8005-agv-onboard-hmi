using System.Globalization;
using System.Net;
using System.Net.Sockets;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.RuleMock;

namespace SQCD.Agv.UnitTests;

public sealed class RuleMockServerTests
{
    [Fact]
    public void DefaultSettingsProvideLoadAndUnloadRulesForAllEightSlots()
    {
        string settingsPath = Path.Combine(AppContext.BaseDirectory, "rulemock.settings.json");
        RuleMockSettings settings = RuleMockSettings.Load(settingsPath);

        Assert.Equal(16, settings.Rules.Count);
        for (int slotIndex = 0; slotIndex < 8; slotIndex++)
        {
            string physicalNumber = (slotIndex + 1).ToString("000", CultureInfo.InvariantCulture);
            MockSublotRule load = Assert.Single(settings.Rules,
                rule => rule.Sublot == $"LOAD-{physicalNumber}");
            MockSublotRule unload = Assert.Single(settings.Rules,
                rule => rule.Sublot == $"UNLOAD-{physicalNumber}");

            Assert.Equal(slotIndex, load.SlotIndex);
            Assert.Equal("Load", load.OperationType);
            Assert.Equal(slotIndex, unload.SlotIndex);
            Assert.Equal("Unload", unload.OperationType);
        }
    }

    [Fact]
    public async Task DefaultStateIsInTransitUntilArriveCommand()
    {
        RuleMockSettings settings = new() { AutoStartVisit = false };
        await using RuleMockServer server = new(settings);

        Assert.False(server.IsArrived);

        await server.ArriveAsync();

        Assert.True(server.IsArrived);
        Assert.Contains("已到站", server.GetStatusText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DepartCommandReturnsVehicleToTransitState()
    {
        RuleMockSettings settings = new() { AutoStartVisit = true };
        await using RuleMockServer server = new(settings);
        Assert.True(server.IsArrived);

        await server.DepartAsync("测试离站");

        Assert.False(server.IsArrived);
        Assert.Contains("在途", server.GetStatusText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectedOnboardRemainsWaitingUntilMockArrives()
    {
        int port = GetFreePort();
        RuleMockSettings mockSettings = new()
        {
            ListenIp = "127.0.0.1",
            ListenPort = port,
            AgvId = "AGV-TEST",
            AutoStartVisit = false
        };
        await using RuleMockServer server = new(mockSettings);
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));
        Task serverTask = server.RunAsync(shutdown.Token);

        RuleGatewaySettings gatewaySettings = new()
        {
            Host = "127.0.0.1",
            Port = port,
            ConnectTimeoutMs = 500,
            RequestTimeoutMs = 500,
            HeartbeatIntervalMs = 200,
            ReconnectDelaysMs = [50]
        };
        await using TcpJsonRuleGateway gateway = new(
            gatewaySettings,
            "AGV-TEST",
            "OBU-TEST",
            new NullLogger());
        await gateway.StartAsync(shutdown.Token);
        await WaitUntilAsync(() => gateway.IsConnected, shutdown.Token);
        Assert.Null(gateway.CurrentVisit);

        await server.ArriveAsync(shutdown.Token);
        await WaitUntilAsync(() => gateway.CurrentVisit is not null, shutdown.Token);
        Assert.Equal("VISIT-MOCK-001", gateway.CurrentVisit?.VisitId);

        await server.DepartAsync("测试完成", shutdown.Token);
        await WaitUntilAsync(() => gateway.CurrentVisit is null, shutdown.Token);

        await gateway.StopAsync(shutdown.Token);
        shutdown.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task ScanVerificationIgnoresLetterCaseAndEchoesSubmittedSublot()
    {
        int port = GetFreePort();
        RuleMockSettings mockSettings = new()
        {
            ListenIp = "127.0.0.1",
            ListenPort = port,
            AgvId = "AGV-TEST",
            AutoStartVisit = true,
            ResponseDelayMs = 0,
            Rules =
            [
                new MockSublotRule
                {
                    Sublot = "LOAD-001",
                    TaskId = "TASK-LOAD-001",
                    SlotIndex = 0,
                    OperationType = "Load"
                }
            ]
        };
        await using RuleMockServer server = new(mockSettings);
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));
        Task serverTask = server.RunAsync(shutdown.Token);

        RuleGatewaySettings gatewaySettings = new()
        {
            Host = "127.0.0.1",
            Port = port,
            ConnectTimeoutMs = 500,
            RequestTimeoutMs = 500,
            HeartbeatIntervalMs = 200,
            ReconnectDelaysMs = [50]
        };
        await using TcpJsonRuleGateway gateway = new(
            gatewaySettings,
            "AGV-TEST",
            "OBU-TEST",
            new NullLogger());
        await gateway.StartAsync(shutdown.Token);
        await WaitUntilAsync(
            () => gateway.IsConnected && gateway.CurrentVisit is not null,
            shutdown.Token);

        ScanAuthorization authorization = await gateway.VerifyScanAsync(
            new ScanVerificationRequest(
                "load-001",
                ScanInputMethod.Manual,
                gateway.CurrentVisit!.VisitId),
            shutdown.Token);

        Assert.True(authorization.Accepted);
        Assert.Equal("load-001", authorization.Sublot);
        Assert.Equal(0, authorization.SlotIndex);
        Assert.Equal(OperationType.Load, authorization.OperationType);

        await gateway.StopAsync(shutdown.Token);
        shutdown.Cancel();
        await serverTask;
    }

    private static int GetFreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class NullLogger : IAppLogger
    {
        public event EventHandler<LogEntryEventArgs>? EntryWritten;

        public void Write(LogSeverity severity, string source, string message, Exception? exception = null)
        {
            EntryWritten?.Invoke(this, new LogEntryEventArgs(new LogEntry(DateTimeOffset.Now, severity, source, message)));
        }
    }
}
