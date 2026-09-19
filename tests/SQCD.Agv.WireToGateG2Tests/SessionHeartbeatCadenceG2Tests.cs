using System.Diagnostics;
using System.Globalization;
using System.Net;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 会话心跳的节拍（onboard-hmi#142，ADR-cross-0027）：车载上位机每 2 秒发一次 <c>Heartbeat</c>，
/// 服务端连续 6 秒没有收到合法消息即判失联，所以单次心跳丢失不能构成失联。
/// </summary>
/// <remarks>
/// <para>
/// 这个类里只有一条用真实计时器的用例，其余节拍断言在 <c>SessionHeartbeatPacingG2Tests</c>
/// 里走注入的时间源，零墙钟等待。两者分工不同：注入时间源证明的是「循环按它拿到的间隔推进」，
/// 这一条证明的是「产品真的用 <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c>
/// 那条路，并且默认间隔就是 ADR 要的 2 秒」——把默认值写错成 5 秒这种事，只有它看得见。
/// </para>
/// <para>
/// 没有 <c>IntegrationSlice</c> 与 <c>ProtocolVector</c> 标记：心跳不属于任何一条冻结向量，
/// <c>IntegrationSliceTraitArchitectureTests</c> 的等式要求切片标记恰好是
/// 本测试所声明向量的投影，声明不出向量就不该带切片。
/// </para>
/// </remarks>
public sealed class SessionHeartbeatCadenceG2Tests
{
    private const string CredentialVariable = "W2G_G2_HEARTBEAT_CREDENTIAL";

    /// <summary>
    /// ADR-cross-0027 的 2 秒节拍加上一点余量。相邻两条心跳之间超过这个数，一次丢失就会撞上服务端
    /// 6 秒的静默阈值。
    /// </summary>
    private static readonly TimeSpan MaximumGap = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// 观察窗口。2 秒节拍下窗口内至少有 3 条心跳；写死 5 秒的旧实现一条都凑不齐。
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(7);

    static SessionHeartbeatCadenceG2Tests()
    {
        Environment.SetEnvironmentVariable(CredentialVariable, "g2-heartbeat-credential");
    }

    /// <summary>
    /// 出厂默认下会话建立后心跳就按 2 秒的节拍来：首条不晚于 2.5 秒，7 秒窗口里不少于 3 条，
    /// 相邻两条不超过 2.5 秒。
    /// </summary>
    [Fact]
    public async Task TheSessionHeartbeatKeepsTheAdrCadenceOnTheRealTimer()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback) { SendReadinessAfterRecoveryAck = true };
        FakeIoModuleClient io = new();
        await using WireToGateSessionService session = CreateSession(server, io);

        session.Start();
        await WaitUntilAsync(
            () => session.Current.Readiness == WireToGateSessionReadiness.Ready,
            TimeSpan.FromSeconds(10),
            token);
        long readyAt = Stopwatch.GetTimestamp();

        await WaitUntilAsync(() => server.HeartbeatArrivals.Count >= 3, Window, token);

        IReadOnlyList<long> arrivals = server.HeartbeatArrivals;
        Assert.True(
            arrivals.Count >= 3,
            $"7 秒窗口里只收到 {arrivals.Count} 条 Heartbeat，{Describe(readyAt, arrivals)}；"
            + "ADR-cross-0027 要求 2 秒一条。");
        Assert.True(
            Stopwatch.GetElapsedTime(readyAt, arrivals[0]) <= MaximumGap,
            $"首条 Heartbeat 太晚，{Describe(readyAt, arrivals)}；ADR-cross-0027 要求 2 秒一条。");
        for (int index = 1; index < arrivals.Count; index++)
        {
            Assert.True(
                Stopwatch.GetElapsedTime(arrivals[index - 1], arrivals[index]) <= MaximumGap,
                $"第 {index} 与第 {index + 1} 条 Heartbeat 之间隔得太久，{Describe(readyAt, arrivals)}；"
                + "单次心跳丢失就会撞上服务端 6 秒的静默阈值。");
        }
    }

    /// <summary>
    /// 服务端的 <c>HeartbeatAck</c> 慢 1 秒回，节拍不跟着往后挪：心跳循环要等 ack 才算这一拍走完，
    /// 那段往返若算进下一次等待，2 秒就成了 3 秒。
    /// </summary>
    /// <remarks>
    /// 这是票面第 2 点要核实的那件事的实测面。写锁（<c>_sendGate</c>）只罩着一次写行，不会长时间占住；
    /// 真正会把心跳往后拖的是等 <c>HeartbeatAck</c>，最长可以拖到 <c>MessageTimeout</c>。
    /// </remarks>
    [Fact]
    public async Task ASlowHeartbeatAckDoesNotPushTheNextHeartbeatLate()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            HeartbeatAckDelay = TimeSpan.FromSeconds(1)
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionService session = CreateSession(server, io);

        session.Start();
        await WaitUntilAsync(
            () => session.Current.Readiness == WireToGateSessionReadiness.Ready,
            TimeSpan.FromSeconds(10),
            token);
        long readyAt = Stopwatch.GetTimestamp();

        await WaitUntilAsync(() => server.HeartbeatArrivals.Count >= 3, Window, token);

        IReadOnlyList<long> arrivals = server.HeartbeatArrivals;
        Assert.True(
            arrivals.Count >= 3,
            $"ack 慢 1 秒时 7 秒窗口里只收到 {arrivals.Count} 条 Heartbeat，{Describe(readyAt, arrivals)}。");
        for (int index = 1; index < arrivals.Count; index++)
        {
            Assert.True(
                Stopwatch.GetElapsedTime(arrivals[index - 1], arrivals[index]) <= MaximumGap,
                $"ack 的往返被算进了下一次等待，{Describe(readyAt, arrivals)}；节拍应当按发出时刻推进。");
        }
    }

    /// <summary>
    /// 失败消息里把「会话就绪到首条心跳」和随后每一段间隔都写成秒，红的时候一眼看得出节拍是几秒。
    /// </summary>
    private static string Describe(long readyAt, IReadOnlyList<long> arrivals)
    {
        if (arrivals.Count == 0)
        {
            return "一条 Heartbeat 都没有收到";
        }

        List<string> gaps = [Format(Stopwatch.GetElapsedTime(readyAt, arrivals[0]))];
        for (int index = 1; index < arrivals.Count; index++)
        {
            gaps.Add(Format(Stopwatch.GetElapsedTime(arrivals[index - 1], arrivals[index])));
        }

        return $"就绪后各段间隔为 {string.Join("、", gaps)}";
    }

    private static string Format(TimeSpan gap) =>
        gap.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " 秒";

    private static WireToGateSessionService CreateSession(FakeControlServer server, FakeIoModuleClient io) =>
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
            TimeSpan.FromMilliseconds(500));

    private static string NewJournalPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "w2g-g2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "journal.db");
    }

    /// <summary>
    /// 轮询到条件成立或窗口用完为止，不抛：断言留给调用方，好让失败消息带上实测的节拍。
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan window, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        while (!predicate() && Stopwatch.GetElapsedTime(start) < window)
        {
            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class StoppedVehicle : IVehicleSafetySignalProvider
    {
        public VehicleSafetySignal Read() => new(VehicleMotionState.Stopped, DateTimeOffset.UtcNow, "G2_TEST");
    }
}
