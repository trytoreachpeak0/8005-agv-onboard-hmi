using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 一次装货的非完成结果已交服务端、旅程停在 <c>LOAD_RESULT_REQUIRES_RECOVERY</c>，恢复开关开着：屏上那一行要说出路——
/// 维护人员先开放这辆车的恢复，开放后按「补偿清空」——并且照着按真能走通（trytoreachpeak0/8005-agv-onboard-hmi#410，
/// agv01 2026-10-22）。
/// </summary>
/// <remarks>
/// <para>
/// 现场形状：agv01 在 N11-9_N12-8_N12-9 装 Q26101950-4（attempt <c>3f01992d</c>），5 号仓没放货，旧版自动重开时锁没在反馈时限内
/// 松开，装货以 UNKNOWN 收尾；服务端判 RecoveryRequired、回 <c>SESSION_RECOVERY_REQUIRED</c>，旅程 Blocked。屏上取消入口灭了，
/// 「车上没有按钮」那一行在开关开着时也不出现，什么都没说。操作员误按「申请恢复」（那是续行，会重新开门），最后靠停程序、关会话、
/// 重启后按「补偿清空」脱困。
/// </para>
/// <para>
/// 这里按现场的顺序走：授权未开时按「补偿清空」被拒（替身答 <c>RECOVERY_AUTHENTICATION_FAILED</c>，与生产一样），那一行照旧；
/// 授权开了再按，补偿 <c>ALL_EMPTY</c>，attempt 收尾，会话回到就绪，那一行消失。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    private const string LoadResultHandedOverExitAfterUnknownLoad =
        "这次装货的结果未知，已交服务端处理。仓里没有放货的话：请联系系统维护人员开放这辆车的恢复，开放后按「补偿清空」。";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnUnknownLoadHandedToTheServerNamesCompensationAndPressingItAfterTheGrantSettlesTheLoad()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                server.SendLoadCompensationCommandOnRequest = true;
                server.RecoverySessionRejectionReasonCode = "RECOVERY_AUTHENTICATION_FAILED";
            },
            token);

        // 1 号仓开着时锁反馈读不到：装货以 UNKNOWN 收尾，结果交服务端，服务端要求恢复。
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.SetUnreadable(0);
        JsonElement loadResult = await WaitForOperationResultAsync(harness, AttemptA, token);
        Assert.Equal("UNKNOWN", loadResult.GetProperty("overallOutcome").GetString());
        await harness.WaitUntilAsync(
            () =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                return harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                    && harness.ViewModel.CanRequestLoadCompensation
                    && !harness.ViewModel.CanRequestLoadCancellation;
            },
            "the compensation entry over the load handed to the server, with the cancellation gone",
            token);

        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.NoOnboardExitNotice) == LoadResultHandedOverExitAfterUnknownLoad,
            "the screen to name the way out",
            token);
        AssertNoOnboardExitStatus(harness.ScreenState(), "LOAD:UNKNOWN");

        // 操作员关好空仓门（锁反馈又读得到）。授权未开：按「补偿清空」被服务端拒，那一行照旧在。
        io.CloseDoor(0, cargo: false);
        io.PublishSnapshot();
        Assert.False(await harness.ViewModel.RequestLoadCompensationAsync(token));
        Assert.True(harness.Business.IsRecoveryNotOpen);
        Assert.Single(ReceivedPayloads(harness, "ExceptionRecoverySessionRequested"));
        Assert.Equal(LoadResultHandedOverExitAfterUnknownLoad, harness.OnUi(() => harness.ViewModel.NoOnboardExitNotice));
        Assert.Empty(ReceivedPayloads(harness, "LoadCompensationResult"));

        // 维护人员开放了恢复；照屏上说的再按「补偿清空」。
        harness.Server.RecoverySessionRejectionReasonCode = null;
        await harness.WaitUntilAsync(
            () =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                return harness.ViewModel.CanRequestLoadCompensation;
            },
            "the compensation entry after the refusal",
            token);
        Assert.True(await harness.ViewModel.RequestLoadCompensationAsync(token));

        JsonElement compensation = await WaitForPayloadAsync(harness, "LoadCompensationResult", token);
        Assert.Equal("ALL_EMPTY", compensation.GetProperty("overallOutcome").GetString());
        Assert.Equal(AttemptA, compensation.GetProperty("slotOperationAttemptId").GetString());
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { RecoveryVector: null, UnsettledSlotOperationAttemptId: null }
                && harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the compensation to settle the load and the session to be ready again",
            token);
        await harness.WaitUntilAsync(
            () => !harness.OnUi(() => harness.ViewModel.HasNoOnboardExitNotice),
            "the line to go once the load is settled",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 「取消装货」按下后、确认之前装货以 UNKNOWN 收尾、结果交服务端（10-22 09:18:34 → 09:18:35 那一秒），开关开着：回答要说这次
    /// 没生效、没开门，并说现在该按哪个——「补偿清空」，不是叫人联系维护人员「在服务端处理」就完（验收 2）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ACancellationConfirmedAfterTheLoadWasHandedOverNamesCompensationWhenItIsOffered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server => server.RespondToLoadCancellationRequests = true,
            token);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry",
            token);
        LoadCancellationConfirmation press = harness.ViewModel.BeginLoadCancellation();
        io.SetUnreadable(0);
        await WaitForOperationResultAsync(harness, AttemptA, token);
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && harness.Business.CanRequestLoadCompensation,
            "the compensation entry over the load handed to the server",
            token);

        Assert.True(await press.SendAsync(token));

        Assert.Empty(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        WireToGateOperatorEvent notice = Assert.Single(harness.Events, item => item.Kind == NothingToCancelKind);
        Assert.Equal(
            "这次装货的结果未知，已交服务端处理，车上已不能再取消。这次取消没有执行，也没有开任何仓门。"
            + "仓里没有放货的话：请联系系统维护人员开放这辆车的恢复，开放后按「补偿清空」。",
            notice.Message);
        Assert.Equal(1, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }
}
