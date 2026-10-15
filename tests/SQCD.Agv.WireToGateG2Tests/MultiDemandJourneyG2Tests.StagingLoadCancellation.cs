using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 待送点取货停靠上的「取消装货」按钮（CP-0012 修订项四；<c>8005-agv-onboard-hmi#348</c>），走视图模型的真实路径：扫码前取消
/// 不出现；装货中取消照常可按，按钮与确认框换成「撤销本次开仓」的说法，因为服务端在待送点把它放回待派、不再取消需求
/// （<c>8005-agv-control-server#713</c>）。机台取货的按钮与确认框一字不变：真装置 L2 按「取消装货」这几个字找按钮和确认框。
/// </summary>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>机台取货装货中取消的确认框正文，照本票之前 <c>MainWindow.OnLoadCancellationClick</c> 里的原文。</summary>
    private static string MachineLoadCancellationConfirmationText(string taskTypeText) =>
        "请确认车辆已停稳、目标仓门已锁好，并由授权人员确认本次装货应取消。\n\n"
        + $"将要取消的任务：子批 SUBLOT-A，{taskTypeText}，2 篮。"
        + "\n\n系统只会执行服务端授权的目标仓位清空，不会重新选择仓位；本站尚未录入子批时不会打开任何仓门。是否继续？";

    /// <summary>
    /// 验收 1：待送点有开着的录入请求、两条需求、什么都没装，按钮不出现，旁边也不提示「先选需求」；「出发」在，说明车认出了待送点。
    /// </summary>
    [Fact]
    public async Task AtTheStagingPickupStopTheCancellationBeforeAnySublotIsNotOnTheScreen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartStagingLoadStopAsync(
            new FakeIoModuleClient(),
            WireToGateStagingPickupStop.StagingToWire,
            token,
            server => server.SublotEntryExpectedSublots = ["SUBLOT-A", "SUBLOT-B"]);
        await harness.WaitUntilAsync(
            () => harness.Business.CanSubmitSublot
                && harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.IsShown),
            "the staging stop's entry request and its 「出发」",
            token);

        Assert.False(harness.OnUi(() => harness.ViewModel.CanRequestLoadCancellation));
        Assert.False(harness.OnUi(() => harness.ViewModel.HasLoadCancellationSelectionHint));
        Assert.False(ButtonOf(harness.ScreenState(), "OnLoadCancellationClick").Visible);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 验收 3（回归）：机台取货，同样两条需求、同样的录入请求，按钮照旧出现、照旧要先选需求，叫「取消装货」。
    /// </summary>
    [Theory]
    [InlineData("WIRE_TO_GATE")]
    [InlineData("DIE_TO_WIRE_STAGING")]
    public async Task AtAMachinePickupTheCancellationBeforeAnySublotIsUnchanged(string workType)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartStagingLoadStopAsync(
            new FakeIoModuleClient(),
            workType,
            token,
            server => server.SublotEntryExpectedSublots = ["SUBLOT-A", "SUBLOT-B"]);
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CanRequestLoadCancellation),
            "the machine pickup's cancellation entry",
            token);

        Assert.False(harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.IsShown));
        Assert.True(harness.OnUi(() => harness.ViewModel.HasLoadCancellationSelectionHint));
        Assert.Equal("取消装货", harness.OnUi(() => harness.ViewModel.LoadCancellationCaption));
        Assert.True(ButtonOf(harness.ScreenState(), "OnLoadCancellationClick").Visible);
    }

    /// <summary>
    /// 验收 2：待送点扫码后开了门，装货中取消可按；按钮与确认框说「撤销本次开仓」、说需求放回待派，结果照现有流程上报。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AtTheStagingPickupStopALoadInFlightCanBeUndoneAndTheScreenSaysItGoesBackToBeDispatched()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartStagingLoadStopAsync(
            io,
            WireToGateStagingPickupStop.StagingToWire,
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
            });

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CanPressLoadCancellation),
            "the in-flight cancellation entry",
            token);

        Assert.True(harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.IsShown));
        Assert.Equal(WireToGateLoadCancellationText.StagingCaption, harness.OnUi(() => harness.ViewModel.LoadCancellationCaption));
        Assert.True(ButtonOf(harness.ScreenState(), "OnLoadCancellationClick") is { Visible: true, Enabled: true });

        LoadCancellationConfirmation press = harness.OnUi(() => harness.ViewModel.BeginLoadCancellation());
        Assert.True(press.AtStagingPickupStop);
        Assert.Equal(
            "请确认车辆已停稳，并由授权人员确认要撤销这一次开仓。\n\n"
            + "将要撤销的是：子批 SUBLOT-A，待送→焊线机台，2 篮。"
            + "\n\n系统只会执行服务端授权的目标仓位清空，不会重新选择仓位；这一篮这趟不带走。"
            + "这条需求不会取消，会放回待派，之后还会再派车来装。是否继续？",
            press.Text);
        Assert.Equal("撤销开仓未被接受。请检查授权、车辆停稳信号和服务端状态。", press.FailureText);
        Assert.DoesNotContain("取消装货", press.Text, StringComparison.Ordinal);

        Assert.True(await press.SendAsync(token));
        await harness.WaitUntilAsync(
            () => ReceivedPayloads(harness, "LoadCancellationResult").Length > 0,
            "the cancellation result",
            token);

        JsonElement request = Assert.Single(ReceivedPayloads(harness, "LoadCancellationStartRequested"));
        Assert.Equal(AttemptA, request.GetProperty("slotOperationAttemptId").GetString());
        JsonElement result = Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Equal(AttemptA, result.GetProperty("slotOperationAttemptId").GetString());
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 验收 3（回归）：机台取货的装货中取消，按钮与确认框一字不变。
    /// </summary>
    [Theory]
    [InlineData("WIRE_TO_GATE", "焊线→质检关卡")]
    [InlineData("DIE_TO_OVEN", "装片→烘箱")]
    public async Task AtAMachinePickupALoadInFlightKeepsItsCaptionAndConfirmation(string workType, string taskTypeText)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartStagingLoadStopAsync(io, workType, token);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CanPressLoadCancellation),
            "the in-flight cancellation entry",
            token);

        Assert.Equal("取消装货", harness.OnUi(() => harness.ViewModel.LoadCancellationCaption));
        LoadCancellationConfirmation press = harness.OnUi(() => harness.ViewModel.BeginLoadCancellation());
        Assert.False(press.AtStagingPickupStop);
        Assert.Equal(MachineLoadCancellationConfirmationText(taskTypeText), press.Text);
        Assert.Equal("装货取消未被接受。请检查授权、车辆停稳信号和服务端状态。", press.FailureText);
    }

    /// <summary>两条需求的取货停靠，项的任务类型由 <paramref name="workType"/> 给：<c>STAGING_TO_WIRE</c> 就是待送点。</summary>
    private static async Task<Harness> StartStagingLoadStopAsync(
        FakeIoModuleClient io,
        string workType,
        CancellationToken token,
        Action<FakeControlServer>? alsoConfigure = null)
    {
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(
                        1,
                        Payloads.Item(DemandA, "TD-A", "SUBLOT-A", workType, "PICKUP", 2),
                        Payloads.Item(DemandB, "TD-B", "SUBLOT-B", workType, "PICKUP", 1)),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
                alsoConfigure?.Invoke(server);
            },
            token,
            io: io);
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null
                && harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the worklist on a ready session",
            token);
        return harness;
    }
}
