using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 期待动作超时的线上那一半（REQ-0358，CP-0005 实现票 1，<c>trytoreachpeak0/8005-agv-onboard-hmi#109</c>）：告警真的
/// 以 <c>subjectType=SLOT</c> 报上服务端、执行器照旧等；以及服务端看板取读数用的会话中途 <c>SafetyStateSnapshotRequested</c>
/// 得到一份按实时 IO 填的 <c>SafetyStateSnapshot</c>（与 <c>8005-agv-control-server#142</c> 配对）。
/// </summary>
/// <remarks>
/// 放进 <see cref="StationDeadlineExpiredG2Tests"/> 是为了用它的夹具：一个真的业务服务、一条只含 1 号仓的装货命令、
/// 一个可以一直不动的操作员——正是期待动作超时要的现场。计时与撤下的规则本身在单元测试
/// <c>SlotExpectedActionOverdueTests</c> 里。
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>门槛取 3 秒：夹具的 <c>OperationTimeout</c> 是 2 秒，门槛只要不是它的整数倍就能看出是配置值在起作用。</summary>
    private static readonly TimeSpan OverdueThreshold = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 等过门槛：服务端收到一条 <c>SLOT_EXPECTED_ACTION_OVERDUE</c>，字段照 CP-0005 第 4.1 节；执行器没有因此多开一次锁、
    /// 没有报结果。门关好、装货完成后，下一份快照里它就没了。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    [Trait("ProtocolVector", "CV-ONBOARD-ALARM-SNAPSHOT")]
    public async Task AWaitPastTheThresholdIsReportedAsASlotAlarmAndWithdrawnOnceTheSlotCloses()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server => server.StationDepartureDeadlineAt = null);
        await using OnboardAlarmMonitor monitor = OverdueMonitor(harness);

        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        SlotExpectedActionWait wait = Assert.IsType<SlotExpectedActionWait>(harness.Business.CurrentExpectedActionWait);
        Assert.Equal(AttemptId, wait.SlotOperationAttemptId);
        Assert.Equal(1, wait.PhysicalSlotNumber);

        await monitor.EvaluateOnceAsync(token);
        Assert.Null(OverdueAlarm(LatestAlarms(harness.Server)));

        TimeSpan untilOverdue = wait.FirstUnlockAt + OverdueThreshold - DateTimeOffset.UtcNow;
        if (untilOverdue > TimeSpan.Zero)
        {
            await Task.Delay(untilOverdue + TimeSpan.FromMilliseconds(100), token);
        }

        await monitor.EvaluateOnceAsync(token);
        JsonElement alarm = Assert.IsType<JsonElement>(OverdueAlarm(LatestAlarms(harness.Server)));

        Assert.Equal("SLOT", alarm.GetProperty("subjectType").GetString());
        Assert.Equal("1", alarm.GetProperty("subjectId").GetString());
        Assert.Equal("WARNING", alarm.GetProperty("severity").GetString());
        Assert.Equal("放入货物并关好1号仓门", alarm.GetProperty("displayMessage").GetString());
        Assert.Equal(
            (wait.FirstUnlockAt + OverdueThreshold).ToUnixTimeMilliseconds(),
            alarm.GetProperty("raisedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());

        // 只是上报：执行器照旧在等，没有多开锁，没有结果。
        Assert.Equal(1, harness.Io.UnlockCount);
        Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, item => item.MessageType == "OperationResult");
        Assert.Equal(WireToGateHmiOperationStage.WaitingOperator, harness.Business.CurrentOperationSnapshot?.Stage);

        harness.Io.CloseDoor(0, cargo: true);
        await harness.WaitForEventAsync("OPERATION_COMPLETED", token);
        await monitor.EvaluateOnceAsync(token);

        Assert.Null(harness.Business.CurrentExpectedActionWait);
        Assert.Null(OverdueAlarm(LatestAlarms(harness.Server)));
    }

    /// <summary>
    /// 会话中途收到 <c>SafetyStateSnapshotRequested</c>：回一份 <c>SafetyStateSnapshot</c>，三项读数是此刻的 IO，版本号接着
    /// 往上走；不当恢复消息处理，不弹「恢复被阻断」。门关上之后再要一次，读数跟着变——证明不是握手时的那份。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-REPLACE-AND-ACK")]
    public async Task AMidSessionSafetySnapshotRequestIsAnsweredFromTheLiveIo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.StationDepartureDeadlineAt = null;
                server.SendReadinessAfterSafetyStateChangedAck = true;
            });
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        int handshakeSnapshots = SafetySnapshots(harness.Server).Length;

        await harness.Server.RequestSafetyStateSnapshotAsync();
        await Harness.WaitUntilAsync(
            () => SafetySnapshots(harness.Server).Length == handshakeSnapshots + 1,
            "the requested SafetyStateSnapshot",
            token);

        JsonElement open = SafetySnapshots(harness.Server)[^1];
        JsonElement slot1 = Slot(open, 1);
        Assert.Equal("UNLOCKED", slot1.GetProperty("lockState").GetString());
        Assert.Equal("EMPTY", slot1.GetProperty("physicalState").GetString());
        Assert.Equal("RESET", slot1.GetProperty("unlockOutputState").GetString());
        Assert.Equal("LOCKED", Slot(open, 2).GetProperty("lockState").GetString());
        long openVersion = open.GetProperty("safetyStateVersion").GetInt64();
        Assert.Contains(("SafetyStateSnapshot", openVersion), harness.Server.AppliedSnapshots.ToArray());
        Assert.True(openVersion > SafetySnapshots(harness.Server)[0].GetProperty("safetyStateVersion").GetInt64());
        await Harness.WaitUntilAsync(
            () => harness.Client.Current.SafetyStateVersion >= openVersion,
            "the applied snapshot's version to become the accepted one",
            token);

        harness.Io.CloseDoor(0, cargo: true);
        await harness.WaitForEventAsync("OPERATION_COMPLETED", token);
        await harness.Server.RequestSafetyStateSnapshotAsync();
        await Harness.WaitUntilAsync(
            () => SafetySnapshots(harness.Server).Length == handshakeSnapshots + 2,
            "the second requested SafetyStateSnapshot",
            token);

        JsonElement closed = SafetySnapshots(harness.Server)[^1];
        Assert.Equal("LOCKED", Slot(closed, 1).GetProperty("lockState").GetString());
        Assert.Equal("OCCUPIED", Slot(closed, 1).GetProperty("physicalState").GetString());
        Assert.True(closed.GetProperty("safetyStateVersion").GetInt64() > openVersion);

        Assert.False(harness.HasEvent("RECOVERY_BLOCKED"), harness.DescribeEvents());
        Assert.True(harness.Client.Current.Connected);
        Assert.Equal(WireToGateSessionReadiness.Ready, harness.Client.Current.Readiness);
    }

    private static OnboardAlarmMonitor OverdueMonitor(Harness harness) => new(
        harness.AlarmBoard,
        () =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            IoSnapshot io = harness.Io.CurrentSnapshot with { ObservedAt = now };
            return new OnboardAlarmInputs(
                now,
                true,
                io,
                TimeSpan.FromSeconds(30),
                null,
                new OnboardSnapshot(
                    OnboardState.WaitingArrival, false, true, null, io, null, false, string.Empty, null, now),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(500),
                harness.Client.Current.ReasonCodes,
                harness.Business.CurrentOperationSnapshot)
            {
                ExpectedActionWait = harness.Business.CurrentExpectedActionWait,
                ExpectedActionOverdueThreshold = OverdueThreshold
            };
        },
        harness.Client,
        new RecordingLogger(),
        TimeSpan.FromMinutes(1));

    private static JsonElement[] LatestAlarms(FakeControlServer server)
    {
        (int _, string _, string _, string wireLine) = server.ReceivedEnvelopes
            .Last(item => item.MessageType == "OnboardAlarmSnapshot");
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return [.. document.RootElement.GetProperty("payload").GetProperty("alarms").EnumerateArray()
            .Select(alarm => alarm.Clone())];
    }

    private static JsonElement? OverdueAlarm(JsonElement[] alarms) =>
        alarms.Where(alarm => alarm.GetProperty("code").GetString() == OnboardAlarmCodes.SlotExpectedActionOverdue)
            .Select(alarm => (JsonElement?)alarm)
            .SingleOrDefault();

    private static JsonElement[] SafetySnapshots(FakeControlServer server) =>
        [.. server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SafetyStateSnapshot")
            .Select(item =>
            {
                using JsonDocument document = JsonDocument.Parse(item.WireLine);
                return document.RootElement.GetProperty("payload").Clone();
            })];

    private static JsonElement Slot(JsonElement snapshotPayload, int slotNo) =>
        snapshotPayload.GetProperty("slotStates").EnumerateArray()
            .Single(slot => slot.GetProperty("slotNo").GetInt32() == slotNo);
}
