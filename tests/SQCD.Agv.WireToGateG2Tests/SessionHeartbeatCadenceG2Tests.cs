using System.Diagnostics;
using System.Globalization;
using System.Net;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 会话心跳的节拍，走真实计时器（onboard-hmi#142，ADR-cross-0027）：车载上位机每 2 秒发一次
/// <c>Heartbeat</c>，服务端连续 6 秒没有收到合法消息即判失联，所以单次心跳丢失不能构成失联。
/// </summary>
/// <remarks>
/// <para>
/// 这个类只有一条用例，其余节拍断言在 <c>SessionHeartbeatPacingG2Tests</c> 里走注入的时间源，
/// 零墙钟等待。分工：注入时间源证明「循环按它拿到的间隔推进」，这一条证明「产品真的走
/// <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c> 那条路，而且出厂默认值本身就是
/// ADR 要的 2 秒」——默认值被写成 5 秒这种事，只有它看得见。
/// </para>
/// <para>
/// <b>为什么带 ack 延迟，而不是分成两条用例。</b> 心跳循环要等 <c>HeartbeatAck</c> 回来才算这一拍
/// 走完。那段往返若被算进下一次等待，2 秒的节拍就变成 2 秒加往返。一条带延迟的用例同时钉住两件事：
/// 默认值是 2 秒，且 ack 的往返不累加。判别力比拆成两条更强，墙钟占用只有一半——在 CI 上这台机器
/// 还要同时跑服务端的 test 与 l2，xunit 又让各测试类并行，省下的每一秒都在给别人让路。
/// </para>
/// <para>
/// 没有 <c>IntegrationSlice</c> 与 <c>ProtocolVector</c> 标记：心跳不属于任何一条冻结向量，
/// <c>IntegrationSliceTraitArchitectureTests</c> 的等式要求切片标记恰好是本测试所声明向量的投影，
/// 声明不出向量就不该带切片。
/// </para>
/// </remarks>
public sealed class SessionHeartbeatCadenceG2Tests
{
    private const string CredentialVariable = "W2G_G2_HEARTBEAT_CREDENTIAL";

    /// <summary>
    /// 相邻两条心跳之间的上界，取 ADR-cross-0027 里那个唯一有意义的界：静默阈值的一半。超过它，
    /// 一次心跳丢失就会撞上服务端 6 秒的静默阈值。
    /// </summary>
    /// <remarks>
    /// 原本是手挑的 2.5 秒，2026-09-20 审查（S1）指出那样绿路径只剩 0.5 秒抗抖余量、判别也只剩
    /// 0.5 秒，两个余量反向共用一个门槛，收紧则易偶发红、放宽则丢判别力。改为把 <see cref="AckDelay"/>
    /// 拉大，门槛随之可以落在 ADR 的界上：绿路径实测 2.0 秒对 3.0 秒，余量 1.0；缺陷路径 4.5 秒对
    /// 3.0 秒，判别余量 1.5。
    /// </remarks>
    private static readonly TimeSpan MaximumGap = WireToGateSessionService.MaximumHeartbeatInterval;

    /// <summary>
    /// 服务端应答的往返。取 2.5 秒是为了让「把 ack 往返算进下一拍」的实现落在 2 + 2.5 = 4.5 秒，
    /// 离 <see cref="MaximumGap"/> 有 1.5 秒，CI 上并行几百条用例也不会把绿的抖成红的。
    /// </summary>
    private static readonly TimeSpan AckDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// 观察窗口的上限。2 秒节拍下第 2 条心跳在第 4 秒就到，用例等到它便收工，窗口只在红的时候用满：
    /// 写死 5 秒的实现在窗口里凑不齐两条，把 ack 往返算进下一拍的实现第 2 条要到 6.5 秒。
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(8);

    static SessionHeartbeatCadenceG2Tests()
    {
        Environment.SetEnvironmentVariable(CredentialVariable, "g2-heartbeat-credential");
    }

    /// <summary>
    /// 出厂默认下会话建立后心跳就按 2 秒的节拍来，而且服务端 ack 慢 2.5 秒也不把节拍往后挪：
    /// 首条与相邻两条都不超过 ADR-cross-0027 的那个界（静默阈值的一半，3 秒）。
    /// </summary>
    [Fact]
    public async Task TheSessionHeartbeatKeepsTheAdrCadenceOnTheRealTimer()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            HeartbeatAckDelay = AckDelay
        };
        FakeIoModuleClient io = new();
        await using WireToGateSessionService session = CreateSession(server, io);

        session.Start();
        // 等的是握手这件确定会发生的事实，所以按停顿判：进程被停住的那几秒里没机会去看，
        // 不该算进这 10 秒。下面等心跳到达那一处**不能**这样改——那个窗口是判据本身
        // （onboard-hmi#149）。
        await WaitUntilStallAwareAsync(
            () => session.Current.Readiness == WireToGateSessionReadiness.Ready,
            TimeSpan.FromSeconds(10),
            token);
        // 握手没完成就往下走，用例会红成「一条 Heartbeat 都没收到」，把诊断指向心跳，而真因在握手。
        Assert.True(
            session.Current.Readiness == WireToGateSessionReadiness.Ready,
            $"会话 10 秒内没有就绪（readiness={session.Current.Readiness}），红的不是心跳节拍。");
        long readyAt = Stopwatch.GetTimestamp();

        await WaitUntilAsync(() => server.HeartbeatArrivals.Count >= 2, Window, token);

        IReadOnlyList<long> arrivals = server.HeartbeatArrivals;
        Assert.True(
            arrivals.Count >= 2,
            $"{Window.TotalSeconds:0} 秒窗口里只收到 {arrivals.Count} 条 Heartbeat，{Describe(readyAt, arrivals)}；"
            + "ADR-cross-0027 要求 2 秒一条。");
        Assert.True(
            Stopwatch.GetElapsedTime(readyAt, arrivals[0]) <= MaximumGap,
            $"首条 Heartbeat 太晚，{Describe(readyAt, arrivals)}；ADR-cross-0027 要求 2 秒一条。");
        for (int index = 1; index < arrivals.Count; index++)
        {
            Assert.True(
                Stopwatch.GetElapsedTime(arrivals[index - 1], arrivals[index]) <= MaximumGap,
                $"第 {index} 与第 {index + 1} 条 Heartbeat 之间隔得太久，{Describe(readyAt, arrivals)}；"
                + "ack 的往返不该被算进下一次等待，单次心跳丢失就会撞上服务端 6 秒的静默阈值。");
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
                // ack 要等 AckDelay（2.5 秒）才回来，消息超时必须比它宽裕，否则红的是超时而不是节拍。
                // 这是用例本地的 WireToGateSessionOptions，不受 WireToGateSettings 那道「消息超时必须
                // 小于 3 秒」的约束——那道界管的是出厂配置，这里要的是把缺陷放大到看得见。
                TimeSpan.FromSeconds(5),
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
    /// <summary>
    /// 等一个确定会发生的事实，按停顿判而不是按墙钟判。
    /// </summary>
    /// <remarks>
    /// 只给「等事实」用。**量节拍用 <see cref="WaitUntilAsync"/>**：那里的窗口就是用例要守的界，
    /// 给它补偿等于把用例名说的上界悄悄放大（onboard-hmi#149）。
    /// </remarks>
    private static async Task WaitUntilStallAwareAsync(
        Func<bool> predicate,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        StallAwareDeadline deadline = new(window, TimeSpan.FromMilliseconds(20));
        while (!predicate() && !deadline.HasExpired)
        {
            await deadline.PollAsync(cancellationToken);
        }
    }

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
