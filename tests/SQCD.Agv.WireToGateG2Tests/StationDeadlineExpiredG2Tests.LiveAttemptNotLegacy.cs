using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 本进程正在执行的装货不是「上次没做完的操作」（<c>trytoreachpeak0/8005-agv-onboard-hmi#120</c>，
/// <c>8005-agv-control-server#167</c> 的真装置场景查出）：服务端对每条安全态变化与中途快照都回一条
/// <c>SessionReadiness</c>，车载端每收到一条都去读恢复状态；正在执行的 attempt 此时在 journal 里就是未结算的，
/// 曾被当成遗留发布 <c>RecoveryRequired</c> 投影——期待动作超时的计时被清零、告警约 30 ms 就被撤、
/// 每次开关门闪一条 <c>ONBOARD_SLOT_OPERATION_UNFINISHED</c>。
/// </summary>
public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>
    /// 装货等操作员期间收到几条 <c>SessionReadiness</c>：不发布 <c>RecoveryRequired</c> 投影，阶段仍是
    /// <c>WaitingOperator</c>，等待计时的起点不变。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-REPLACE-AND-ACK")]
    public async Task SessionReadinessDuringALiveLoadDoesNotRestoreItAsAnUnfinishedOperation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server => server.StationDepartureDeadlineAt = null);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        SlotExpectedActionWait wait = Assert.IsType<SlotExpectedActionWait>(harness.Business.CurrentExpectedActionWait);

        for (int i = 0; i < 3; i++)
        {
            await harness.ReceiveMidSessionReadinessAsync(token);
            await harness.AssertNoRecoveryProjectionForAWhileAsync(token);
        }

        Assert.Equal(WireToGateHmiOperationStage.WaitingOperator, harness.Business.CurrentOperationSnapshot?.Stage);
        Assert.Equal(wait, harness.Business.CurrentExpectedActionWait);
        Assert.Equal(1, harness.Io.UnlockCount);
        Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, item => item.MessageType == "OperationResult");
    }

    /// <summary>
    /// 期待动作超时告警（REQ-0358）在门槛时出现，服务端随后再回几条 <c>SessionReadiness</c> 也不把它撤下：计时从这个仓
    /// 第一次开锁算起，中途的就绪回复不清零它。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    [Trait("ProtocolVector", "CV-ONBOARD-ALARM-SNAPSHOT")]
    public async Task TheExpectedActionOverdueAlarmSurvivesSessionReadinessDuringTheWait()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server => server.StationDepartureDeadlineAt = null);
        await using OnboardAlarmMonitor monitor = OverdueMonitor(harness);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        SlotExpectedActionWait wait = Assert.IsType<SlotExpectedActionWait>(harness.Business.CurrentExpectedActionWait);

        // Readiness keeps arriving while the operator does not act, the way every door or safety change brings one.
        while (DateTimeOffset.UtcNow < wait.FirstUnlockAt + OverdueThreshold + TimeSpan.FromMilliseconds(100))
        {
            await harness.ReceiveMidSessionReadinessAsync(token);
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }

        await monitor.EvaluateOnceAsync(token);
        Assert.NotNull(OverdueAlarm(LatestAlarms(harness.Server)));

        await harness.ReceiveMidSessionReadinessAsync(token);
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        await monitor.EvaluateOnceAsync(token);

        JsonElement[] alarms = LatestAlarms(harness.Server);
        JsonElement alarm = Assert.IsType<JsonElement>(OverdueAlarm(alarms));
        Assert.Equal(
            (wait.FirstUnlockAt + OverdueThreshold).ToUnixTimeMilliseconds(),
            alarm.GetProperty("raisedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        Assert.DoesNotContain(
            alarms,
            item => item.GetProperty("code").GetString() == OnboardAlarmCodes.SlotOperationUnfinished);
        Assert.Equal(wait, harness.Business.CurrentExpectedActionWait);
    }

    /// <summary>
    /// 真遗留照旧：上个进程开了锁、等操作员时退出，重启后没有任何命令再来。本进程没有在执行它，于是照旧按实时 IO
    /// 中断结算（不再开锁、报 <c>UNKNOWN</c>）并发布 <c>RecoveryRequired</c> 投影；再重启一次，结果已经给过、结算不成，
    /// 仍发布「上次装货操作未完成」的 <c>RecoveryRequired</c> 投影。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnOperationLeftByThePreviousProcessIsStillRestoredAsRecoveryRequiredAndSettled()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        FakeControlServer first;
        await using (Harness beforeRestart = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server => server.StationDepartureDeadlineAt = null,
            journalPath: journalPath))
        {
            first = beforeRestart.Server;
            await beforeRestart.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        }

        FakeControlServer second;
        await using (Harness afterRestart = await Harness.StartAsync(
            new FakeIoModuleClient(),
            token,
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = false;
                server.SendSlotOperationCommandAfterRecovery = false;
                server.AdoptDurableRecoveryMemoryFrom(first);
            },
            journalPath: journalPath,
            baselineRevision: 2))
        {
            second = afterRestart.Server;
            await afterRestart.WaitForInboundAsync("OperationResult", token);
            await afterRestart.WaitForEventAsync("OPERATION_RECOVERY_REQUIRED", token);

            Assert.Equal("UNKNOWN", afterRestart.SingleResult("OperationResult").GetProperty("overallOutcome").GetString());
            Assert.Equal(0, afterRestart.Io.UnlockCount);
            Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, afterRestart.Business.CurrentOperationSnapshot?.Stage);
            Assert.Equal(AttemptId, afterRestart.Business.CurrentOperationSnapshot?.SlotOperationAttemptId);
        }

        await using Harness again = await Harness.StartAsync(
            new FakeIoModuleClient(),
            token,
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = false;
                server.SendSlotOperationCommandAfterRecovery = false;
                server.AdoptDurableRecoveryMemoryFrom(second);
            },
            journalPath: journalPath,
            baselineRevision: 3);
        await again.WaitForEventAsync("OPERATION_RECOVERY_REQUIRED", token);

        Assert.Contains("上次装货操作未完成：1号仓，需要管理员恢复。", again.DescribeEvents(), StringComparison.Ordinal);
        Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, again.Business.CurrentOperationSnapshot?.Stage);
        Assert.Equal(0, again.Io.UnlockCount);
    }

    private sealed partial class Harness
    {
        /// <summary>
        /// The server's answer to a mid-session snapshot: ack, then one <c>SessionReadiness</c> -- the same pair the
        /// real server sends after every <c>SafetyStateChanged</c> (control-server#142).
        /// </summary>
        public async Task ReceiveMidSessionReadinessAsync(CancellationToken cancellationToken)
        {
            int before = Server.SentEnvelopes.Count(item => item.MessageType == "SessionReadiness");
            await Server.RequestSafetyStateSnapshotAsync();
            await WaitUntilAsync(
                () => Server.SentEnvelopes.Count(item => item.MessageType == "SessionReadiness") > before
                    && Client.Current.Readiness == WireToGateSessionReadiness.Ready,
                "a mid-session SessionReadiness",
                cancellationToken);
        }

        /// <summary>
        /// The recovery projection, when it came, came within ~30 ms of the readiness (control-server#167); half a
        /// second is well past it.
        /// </summary>
        public async Task AssertNoRecoveryProjectionForAWhileAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset until = DateTimeOffset.UtcNow.AddMilliseconds(500);
            while (DateTimeOffset.UtcNow < until)
            {
                lock (_events)
                {
                    Assert.DoesNotContain(
                        _events,
                        item => item.Kind == "OPERATION_RECOVERY_REQUIRED"
                            || item.Operation?.Stage == WireToGateHmiOperationStage.RecoveryRequired);
                }

                await Task.Delay(10, cancellationToken);
            }
        }
    }
}
