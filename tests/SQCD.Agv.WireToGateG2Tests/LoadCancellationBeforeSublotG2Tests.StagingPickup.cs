using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 待送点取货停靠不提供扫码前取消（CP-0012 修订项四，<c>REQ-0211</c>；<c>8005-agv-onboard-hmi#348</c>）。机台取货照旧。
/// </summary>
/// <remarks>
/// <para>
/// 待送点的认法与「出发」同一套：<see cref="WireToGateStagingPickupStop.Is"/>（<c>8005-agv-onboard-hmi#349</c>）。这里的替身
/// 不给 <c>publicStationFunction</c>，所以走的是按清单推断那一支：项全是 <c>STAGING_TO_WIRE</c> 的取货项。
/// </para>
/// <para>
/// 已经发出、还没结清的那一次不收：它挂着时扫码入口关着（<c>CanSubmitSublot</c>），「出发」也按不了，入口一收就再没有路把它
/// 结掉。所以只是不再提供新的一次。
/// </para>
/// </remarks>
public sealed partial class LoadCancellationBeforeSublotG2Tests
{
    /// <summary>
    /// 验收 1：待送点取货停靠有开着的录入请求、没有任何仓位操作，扫码前取消不提供；硬按也什么都不发，扫码照常。
    /// </summary>
    [Fact]
    public async Task AStagingPickupOffersNoCancellationBeforeAnySublot()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        // An empty seed: the harness otherwise waits for the entry to be offered, which here is the point not to happen. The
        // journal is new, so the recovery state the entry reads is empty either way; the entry request and the worklist are
        // still waited for.
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server => server.JourneyWorkType = WireToGateStagingPickupStop.StagingToWire,
            seed: WireToGateRecoveryState.Empty);

        // 对照：车认出了待送点（「出发」入口在），录入请求也到了。
        Assert.True(harness.Business.CurrentStopDeparture.Shown);
        Assert.True(harness.Business.CanSubmitSublot);

        Assert.False(harness.Business.CanRequestLoadCancellation);
        Assert.False(harness.Business.CanRequestLoadCancellationBeforeAnySublot);
        Assert.False(harness.Business.IsLoadCancellationDemandSelectionRequired);

        // A press on an entry that was never offered -- a stale screen -- is what it has always been: refused before anything
        // goes out, and said to the operator.
        Assert.False(await harness.Business.RequestLoadCancellationAsync("待送点上硬按一次。", token));
        await harness.WaitForRecoveryBlockedAsync(
            OnboardCommandRejectionText.DescribeRecoveryBlocked("RECOVERY_OPERATION_CONTEXT_MISSING"),
            token);
        Assert.Empty(harness.PayloadsReceived("LoadCancellationStartRequested"));
        Assert.True(harness.Business.CanSubmitSublot);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// 验收 3（回归）：机台取货停靠，同样的停靠形状换成 <c>WIRE_TO_GATE</c> 或 <c>DIE_*</c> 的项，扫码前取消照旧提供、照旧走完。
    /// </summary>
    [Theory]
    [InlineData("WIRE_TO_GATE")]
    [InlineData("DIE_TO_WIRE_STAGING")]
    [InlineData("DIE_TO_OVEN")]
    public async Task AMachinePickupStillOffersTheCancellationBeforeAnySublot(string workType)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server => server.JourneyWorkType = workType);

        Assert.False(harness.Business.CurrentStopDeparture.Shown);
        Assert.True(harness.Business.CanRequestLoadCancellation);
        Assert.True(harness.Business.CanRequestLoadCancellationBeforeAnySublot);

        Assert.True(await harness.Business.RequestLoadCancellationAsync("到站后现场确认本站没有要装的货。", token));

        (_, JsonElement result) = Assert.Single(harness.ResultsReceived());
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// 已经发出、授权没回来的一次扫码前取消，重启后停靠被认作待送点（比如旧版车载端按下、升级后再起来）：入口仍在，再按一次
    /// 按首次内容补发并结清。只是不再提供新的一次，不把已经发出的那次困在日志里——它挂着时扫码与「出发」都按不了。
    /// </summary>
    /// <remarks>
    /// 护栏，实现之前就是绿的：它守的是实现不能连这一次也收掉。替身重启后发修订号 2 的待送点清单，同一修订号换了内容车会判冲突。
    /// </remarks>
    [Fact]
    public async Task ACancellationAlreadySentStillSettlesWhenTheStopTurnsOutToBeTheStagingStop()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = NewJournalPath();
        string? originalOperator = Environment.GetEnvironmentVariable(OperatorVariable);
        await using FakeControlServer server = BeforeSublotHarness.NewServer();
        server.LoadCancellationAuthorizationsToDrop = 1;
        try
        {
            Environment.SetEnvironmentVariable(OperatorVariable, "operator-001");
            await using (BeforeSublotHarness beforeRestart = await BeforeSublotHarness.StartAsync(
                token,
                existingServer: server,
                journalPath: journalPath))
            {
                Assert.False(await beforeRestart.Business.RequestLoadCancellationAsync(
                    "到站后现场确认本站没有要装的货。", token));
            }

            await using FakeControlServer serverAfterRestart = BeforeSublotHarness.NewServer();
            serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
            serverAfterRestart.JourneyWorkType = WireToGateStagingPickupStop.StagingToWire;
            serverAfterRestart.JourneyWorklistRevision = 2;
            await using BeforeSublotHarness afterRestart = await BeforeSublotHarness.StartAsync(
                token,
                existingServer: serverAfterRestart,
                journalPath: journalPath,
                baselineRevision: 2);
            Assert.True(afterRestart.Business.CurrentStopDeparture.Shown);
            // While it is out, neither a scan nor 「出发」 can be pressed: the entry is the only way to settle it.
            Assert.False(afterRestart.Business.CanSubmitSublot);
            Assert.Null(afterRestart.Business.CurrentStopDeparture.Prompt);
            Assert.True(afterRestart.Business.CanRequestLoadCancellation);

            Assert.True(await afterRestart.Business.RequestLoadCancellationAsync("重启之后再按一次。", token));

            AssertRetriedWithTheFirstPressContent(
                [.. server.ReceivedEnvelopes, .. serverAfterRestart.ReceivedEnvelopes]);
            // Settled: nothing of it is left open. The entry request went with it, as it does at any stop.
            Assert.False(afterRestart.Business.IsLoadCancellationBeforeSublotOpen);
            Assert.False(afterRestart.Business.CanRequestLoadCancellation);
            Assert.Equal(0, afterRestart.Io.UnlockCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OperatorVariable, originalOperator);
        }
    }

    /// <summary>
    /// 已发出未结的那一次，它的需求已经不在待送点的清单里（审查第二阶段 1）：入口仍在，按一下不发任何请求，告诉操作员本机不再
    /// 等它，扫码随之恢复，全程不开门。
    /// </summary>
    /// <remarks>
    /// 守的是 <c>FindLoadCancellationBeforeSublot</c> 里「需求已离开清单」那一支在待送点也走得到。例外条件若被改成「还要求那条需求
    /// 仍在清单里」，这里的入口就不出现：取消一直挂着、扫码关着、「出发」按不了，待送点又没有离站倒计时，车会卡在这一站，只能改日志。
    /// 预置的取消号对不上清单里任何一项（取消号由需求与作业会话派生），等同于那条需求已经离开。
    /// </remarks>
    [Fact]
    public async Task AtTheStagingStopASentCancellationWhoseDemandLeftTheWorklistIsLetGo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server => server.JourneyWorkType = WireToGateStagingPickupStop.StagingToWire,
            seed: WireToGateRecoveryState.Empty with
            {
                PendingLoadCancellation = new WireToGatePendingLoadCancellation(
                    "5b0c3e7a-2f41-4d8e-9a6b-1c2d3e4f5a6b",
                    null,
                    "operator-000",
                    "SESSION",
                    DateTimeOffset.UtcNow,
                    "旧版本按下的扫码前取消，那条需求后来离开了本站。")
            });
        Assert.True(harness.Business.CurrentStopDeparture.Shown);
        await BeforeSublotHarness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the entry to be offered over the cancellation already sent",
            token);
        Assert.False(harness.Business.CanSubmitSublot);

        Assert.False(await harness.Business.RequestLoadCancellationAsync("待送点上再按一次。", token));

        await harness.WaitForRecoveryBlockedAsync("本机不再等待它", token);
        Assert.Empty(harness.PayloadsReceived("LoadCancellationStartRequested"));
        await BeforeSublotHarness.WaitUntilAsync(
            () => harness.Business.CanSubmitSublot,
            "sublot entry to come back once the vehicle stops waiting",
            token);
        Assert.False(harness.Business.IsLoadCancellationBeforeSublotOpen);
        Assert.False(harness.Business.CanRequestLoadCancellation);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// 验收 4：<c>FP-IS-02</c> 的 <c>CV-LOAD-CANCELLATION-BEFORE-LOAD</c> 由这个类的夹具覆盖，它的停靠是机台取货，不是待送点，
    /// 所以待送点不提供扫码前取消不碰这条向量。夹具哪天换成待送点，向量用例会因为入口不在而红，这一条先说清是为什么。
    /// </summary>
    [Fact]
    public async Task TheBeforeLoadVectorFixtureIsAMachinePickup()
    {
        await using FakeControlServer server = BeforeSublotHarness.NewServer();
        Assert.Equal("WIRE_TO_GATE", server.JourneyWorkType);
    }
}
