using System.Diagnostics;
using System.Net;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 会话心跳节拍的精确断言（onboard-hmi#142，ADR-cross-0027）：间隔由配置说了算，不早发也不漏发，
/// 业务在忙的时候照发，链路断了重连之后在新会话上继续。
/// </summary>
/// <remarks>
/// <para>
/// 时间源是注入的 <see cref="ManualTimeProvider"/>，每条用例的墙钟开销只有握手和几次轮询。
/// <c>SessionHeartbeatCadenceG2Tests</c> 那条走真实计时器，负责证明出厂默认值本身是 2 秒；
/// 这里证明的是循环按拿到的间隔推进。
/// </para>
/// <para>
/// 没有 <c>IntegrationSlice</c> 与 <c>ProtocolVector</c> 标记：心跳不属于任何一条冻结向量，
/// 而切片标记必须恰好是所声明向量的投影。
/// </para>
/// </remarks>
public sealed class SessionHeartbeatPacingG2Tests
{
    private const string CredentialVariable = "W2G_G2_HEARTBEAT_PACING_CREDENTIAL";
    private const string OperatorVariable = "W2G_G2_HEARTBEAT_PACING_OPERATOR";

    /// <summary>
    /// 等一条心跳到达最多等这么久。推完时钟之后心跳是毫秒级到的，这个界存在只是为了让用例守的界
    /// 与它名字里说的界一致：ADR-cross-0027 的 2 秒节拍加半秒余量。
    /// </summary>
    private static readonly TimeSpan HeartbeatArrival = TimeSpan.FromSeconds(2.5);

    static SessionHeartbeatPacingG2Tests()
    {
        Environment.SetEnvironmentVariable(CredentialVariable, "g2-heartbeat-pacing-credential");
        Environment.SetEnvironmentVariable(OperatorVariable, "operator-142");
    }

    /// <summary>
    /// 默认间隔就是 ADR 的 2 秒：差 0.1 秒时一条都没发，补满就发一条，再过一拍再发一条。
    /// </summary>
    [Fact]
    public async Task TheDefaultCadenceIsTwoSecondsAndNotAMillisecondEarlier()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ManualTimeProvider time = new();
        await using FakeControlServer server = NewServer();
        await using WireToGateSessionService session = CreateSession(server, new FakeIoModuleClient(), time);

        await StartAndWaitForReadyAsync(session, token);

        await AdvanceAsync(time, TimeSpan.FromSeconds(1.9), token);
        await Task.Delay(150, token);
        Assert.Empty(server.HeartbeatArrivals);

        time.Advance(TimeSpan.FromSeconds(0.1));
        await WaitForAsync(() => server.HeartbeatArrivals.Count == 1, "第一条心跳", token, HeartbeatArrival);

        await AdvanceAsync(time, TimeSpan.FromSeconds(2), token);
        await WaitForAsync(() => server.HeartbeatArrivals.Count == 2, "第二条心跳", token, HeartbeatArrival);
    }

    /// <summary>
    /// 配置值取代默认值：按 400 毫秒配，推三拍就有三条。
    /// </summary>
    [Fact]
    public async Task AConfiguredIntervalReplacesTheDefault()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TimeSpan interval = TimeSpan.FromMilliseconds(400);
        ManualTimeProvider time = new();
        await using FakeControlServer server = NewServer();
        await using WireToGateSessionService session =
            CreateSession(server, new FakeIoModuleClient(), time, interval);

        await StartAndWaitForReadyAsync(session, token);

        for (int beat = 1; beat <= 3; beat++)
        {
            await AdvanceAsync(time, interval, token);
            await WaitForAsync(() => server.HeartbeatArrivals.Count == beat, $"第 {beat} 条心跳", token, HeartbeatArrival);
        }
    }

    /// <summary>
    /// 验收第 2 条：一次仓位操作正开着门等操作员（执行器占着这条会话在发进度、结果还没得报），
    /// 心跳仍然一拍不落。
    /// </summary>
    /// <remarks>
    /// ADR-cross-0027 的后果里写得很直白：心跳必须独立于仓位操作和结果补报持续运行。这里用
    /// <see cref="FakeIoModuleClient.OperatorNeverActs"/> 造出一次永远结束不了的装货，业务侧在那期间
    /// 一直占着发送闸报进度，心跳走的是另一条任务，三拍就该有三条。
    /// </remarks>
    [Fact]
    public async Task TheCadenceHoldsWhileASlotOperationIsInFlight()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ManualTimeProvider time = new();
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using FakeControlServer server = NewServer();
        server.SendSlotOperationCommandAfterRecovery = true;
        server.SendJourneySnapshotsAfterRecovery = true;
        await using WireToGateSessionService session = CreateSession(server, io, time);
        await using WireToGateBusinessService business = CreateBusiness(session, io);

        business.Start();
        await StartAndWaitForReadyAsync(session, token);
        await WaitForAsync(() => io.UnlockCount >= 1, "服务端下发的装货命令开到仓门", token);

        int before = server.HeartbeatArrivals.Count;
        for (int beat = 1; beat <= 3; beat++)
        {
            await AdvanceAsync(time, TimeSpan.FromSeconds(2), token);
            await WaitForAsync(
                () => server.HeartbeatArrivals.Count == before + beat,
                $"装货进行中的第 {beat} 条心跳",
                token,
                HeartbeatArrival);
        }

        // 操作真的还开着：谁都没关门，也没有结果发出去。
        Assert.Equal(1, io.UnlockCount);
        Assert.DoesNotContain(server.ReceivedEnvelopes, envelope => envelope.MessageType == "OperationResult");
    }

    /// <summary>
    /// 验收第 1 条的重连那一半：心跳发送失败不会把会话留在原地——链路断了，那次心跳抛出来，
    /// 服务重连，心跳在新一代会话上从头按拍发。
    /// </summary>
    [Fact]
    public async Task AFailedHeartbeatReconnectsAndTheCadenceResumesOnTheNewSession()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ManualTimeProvider time = new();
        await using FakeControlServer server = NewServer();
        await using WireToGateSessionService session = CreateSession(server, new FakeIoModuleClient(), time);

        await StartAndWaitForReadyAsync(session, token);
        await AdvanceAsync(time, TimeSpan.FromSeconds(2), token);
        await WaitForAsync(() => server.HeartbeatArrivals.Count == 1, "第一代会话上的心跳", token, HeartbeatArrival);

        await session.Client.DisconnectAsync();

        // 这一拍发不出去：连接没了。异常把循环推到重连分支，重连延迟同样走注入的时间源。
        await AdvanceAsync(time, TimeSpan.FromSeconds(2), token);
        await AdvanceAsync(time, TimeSpan.FromSeconds(2), token);
        await WaitForAsync(
            () => session.Current.Readiness == WireToGateSessionReadiness.Ready
                && session.Current.SessionGeneration > 1,
            "第二代会话就绪",
            token);

        await AdvanceAsync(time, TimeSpan.FromSeconds(2), token);
        await WaitForAsync(() => server.HeartbeatArrivals.Count >= 2, "第二代会话上的心跳", token, HeartbeatArrival);

        long generation = session.Current.SessionGeneration!.Value;
        var latest = server.ReceivedEnvelopes.Last(envelope => envelope.MessageType == "Heartbeat");
        using JsonDocument document = JsonDocument.Parse(latest.WireLine);
        Assert.Equal(generation, document.RootElement.GetProperty("sessionGeneration").GetInt64());
    }

    /// <summary>
    /// 0、负数、以及大到会撞上失联阈值的间隔，构造时就拒——间隔到了 3 秒，丢一条心跳就是 6 秒静默，
    /// 正好踩在 ADR-cross-0027 判失联的线上。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3_000)]
    [InlineData(int.MaxValue)]
    public async Task AnIntervalOutsideTheAdrBoundsIsRefused(int milliseconds)
    {
        await using FakeControlServer server = NewServer();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateSession(
                server,
                new FakeIoModuleClient(),
                new ManualTimeProvider(),
                TimeSpan.FromMilliseconds(milliseconds)));

        Assert.Equal("heartbeatInterval", exception.ParamName);
    }

    private static FakeControlServer NewServer() =>
        new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };

    private static async Task StartAndWaitForReadyAsync(
        WireToGateSessionService session,
        CancellationToken cancellationToken)
    {
        session.Start();
        await WaitForAsync(
            () => session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "会话就绪",
            cancellationToken);
    }

    /// <summary>
    /// 先等被测代码真的开始等下一拍，再推时钟。反过来推的那一次会落空，而用例红的样子是「心跳没来」，
    /// 看不出错在测试自己身上。
    /// </summary>
    private static async Task AdvanceAsync(
        ManualTimeProvider time,
        TimeSpan delta,
        CancellationToken cancellationToken)
    {
        await WaitForAsync(() => time.ArmedTimers >= 1, "会话开始等下一拍", cancellationToken);
        time.Advance(delta);
    }

    /// <summary>
    /// 轮询到条件成立为止，超时即红并说清等的是什么。
    /// </summary>
    /// <remarks>
    /// 默认 10 秒给的是握手、IO 这类真实往返。**等心跳到达要用 <see cref="HeartbeatArrival"/>**：
    /// 时钟一推，心跳是毫秒级到的，等它 10 秒等于把「节拍不超过 2.5 秒」这句话的实际上界放成 10 秒
    /// ——用例名说的界与它真正守的界对不上（2026-09-20 审查 S3）。
    /// </remarks>
    private static async Task WaitForAsync(
        Func<bool> predicate,
        string what,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(10);
        long start = Stopwatch.GetTimestamp();
        while (!predicate())
        {
            if (Stopwatch.GetElapsedTime(start) > limit)
            {
                Assert.Fail($"等不到{what}（{limit.TotalSeconds:0.#} 秒内）。");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private static WireToGateSessionService CreateSession(
        FakeControlServer server,
        FakeIoModuleClient io,
        ManualTimeProvider time,
        TimeSpan? heartbeatInterval = null) =>
        new(
            new WireToGateSessionOptions(
                "127.0.0.1",
                server.Port,
                "AGV-8005-01",
                Guid.NewGuid().ToString("D"),
                new string('a', 40),
                CredentialVariable,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2),
                1,
                1,
                "eight-slot-v1",
                "eight-slot-modbus-v1",
                SupportsBatchUnlock: false),
            io,
            new SqliteWireToGateJournal(NewJournalPath()),
            new RecordingLogger(),
            new SystemClock(),
            new StoppedVehicle(),
            new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500),
            heartbeatInterval,
            time);

    private static WireToGateBusinessService CreateBusiness(
        WireToGateSessionService session,
        FakeIoModuleClient io) =>
        new(
            session,
            io,
            new RecordingLogger(),
            new SystemClock(),
            () => true,
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(30)),
            OperatorVariable,
            new StoppedVehicle(),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(500),
            new WireToGateRecoveryOptions(
                false,
                "W2G_G2_HEARTBEAT_PACING_PROOF",
                "MAINTENANCE_ADMINISTRATOR",
                "CONFIGURED_PROOF"));

    private static string NewJournalPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "w2g-heartbeat", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "journal.db");
    }

    private sealed class StoppedVehicle : IVehicleSafetySignalProvider
    {
        public VehicleSafetySignal Read() => new(VehicleMotionState.Stopped, DateTimeOffset.UtcNow, "G2_TEST");
    }
}
