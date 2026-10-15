using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ScreenState;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 待送点取货停靠上清单每条一个「取消」（CP-0014 修订项二，<c>REQ-0211</c>；<c>8005-agv-onboard-hmi#353</c>），走视图模型的真实
/// 路径：每条还没装的需求旁边一个按钮，按下确认后发这一条的扫码前取消（<c>LoadCancellationStartRequested</c>，
/// <c>slotOperationAttemptId</c> 为 null），取消号的派生与重发照旧。服务端把它终结并抑制（<c>8005-agv-control-server#726</c>），
/// 清单靠服务端下一份 worklist 刷新（<c>8005-agv-control-server#723</c>）。
/// </summary>
/// <remarks>
/// 共用的「先选再取消」入口在待送点仍然不出现（<c>8005-agv-onboard-hmi#348</c>）；机台取货的入口、按钮与文字一字不变。
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>确认框正文，照草稿原文（用户确认前以草稿为准）。</summary>
    private static string RowConfirmationText(string sublot, int baskets) =>
        "请由授权人员确认：这一条料这次不运。\n\n"
        + $"将要取消：子批 {sublot}，待送→焊线机台，{baskets} 篮。"
        + "\n\n确认后这条需求就此结束，系统不会再派车来运它，按错了也没有办法撤回；这批料需要人工或在 MES 上另行处理。"
        + "本站其他子批不受影响，取消结束后可以继续扫码装货。不会打开任何仓门。是否继续？";

    /// <summary>
    /// 验收 1、3：待送点两条需求、什么都没装，两行各有一个按得动的「取消」；共用入口仍不出现，也不提示「先选」。屏幕状态逐行报出。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task EveryUnloadedRowAtTheStagingStopHasItsOwnCancellationAndTheSharedEntryStaysHidden()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartStagingRowStopAsync(token);

        WorklistItemRow[] rows = harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray());
        Assert.Equal([DemandA, DemandB], rows.Select(row => row.DemandId));
        Assert.All(rows, row => Assert.True(row is { OffersCancellation: true, CanPressCancellation: true }));

        Assert.False(harness.OnUi(() => harness.ViewModel.CanRequestLoadCancellation));
        Assert.False(harness.OnUi(() => harness.ViewModel.HasLoadCancellationSelectionHint));
        Assert.False(ButtonOf(harness.ScreenState(), "OnLoadCancellationClick").Visible);

        HmiScreenState screen = harness.ScreenState();
        Assert.All(screen.WorklistRows, row => Assert.True(row is { CancellationShown: true, CancellationEnabled: true }));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 验收 2、4：按 B 那一行：确认框复述 B、说清不再派且不能撤回；确认后只发 B 的扫码前取消，不开门，结果 <c>ALL_EMPTY</c>。
    /// 服务端下一份清单不再有 B，那一行消失；A 那一行还在、仍可取消，扫码照常。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task PressingARowCancelsThatDemandOnlyAndTheRowLeavesWithTheNextWorklist()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartStagingRowStopAsync(token);

        WorklistRowCancellationConfirmation press = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB));
        Assert.Equal(RowConfirmationText("SUBLOT-B", 1), press.Text);
        Assert.Equal("取消未被接受，这条需求没有变化，仍在本站清单里。请检查授权和服务端状态后再试。", press.FailureText);

        Assert.True(await press.SendAsync(token));

        JsonElement request = Assert.Single(Received(harness, "LoadCancellationStartRequested"));
        Assert.Equal(DemandB, request.GetProperty("demandId").GetString());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("slotOperationAttemptId").ValueKind);
        JsonElement result = Assert.Single(Received(harness, "LoadCancellationResult"));
        Assert.Equal(DemandB, result.GetProperty("demandId").GetString());
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.WireLine.Contains(DemandA, StringComparison.Ordinal));

        // What the control server sends once B is ended with A still to do: a worklist without B and a new entry request,
        // together (8005-agv-control-server#723).
        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            Payloads.Worklist(
                2,
                Payloads.Item(DemandA, "TD-A", "SUBLOT-A", WireToGateStagingPickupStop.StagingToWire, "PICKUP", 2)));
        await harness.Server.SendCommandAsync(
            "SublotEntryRequested",
            Guid.NewGuid().ToString("D"),
            new
            {
                operationSessionId = OperationSessionId,
                stationId = "ST-01",
                worklistRevision = 2,
                expectedSublots = SublotAOnly,
                entryMethods = FrozenEntryMethods,
                expiresOnRevisionChange = true
            });
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.WorklistItems.Select(row => row.DemandId).SequenceEqual([DemandA])),
            "the worklist without B",
            token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanSubmitSublot
                && harness.OnUi(() => harness.ViewModel.WorklistItems.Single() is { OffersCancellation: true, CanPressCancellation: true }),
            "A still enterable and cancellable",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 一行按下、答复丢了：那一行再按一次按首次内容补发（同一取消号、同一载荷），服务端不判冲突；等答复期间别的行按不动、扫码关着。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ARowPressWhoseAnswerWasLostIsResentUnchangedAndTheOtherRowsWait()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartStagingRowStopAsync(token, server => server.LoadCancellationAuthorizationsToDrop = 1);

        WorklistRowCancellationConfirmation first = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB));
        Assert.False(await first.SendAsync(token));

        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray()) is
                [{ CanPressCancellation: false }, { CanPressCancellation: true }],
            "only B's row to stay pressable while its answer is out",
            token);
        Assert.False(harness.Business.CanSubmitSublot);
        Assert.False(harness.OnUi(() => harness.ViewModel.CanRequestLoadCancellation));

        WorklistRowCancellationConfirmation again = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB));
        Assert.True(await again.SendAsync(token));

        (string MessageId, string WireLine)[] requests =
        [
            .. harness.Server.ReceivedEnvelopes
                .Where(envelope => envelope.MessageType == "LoadCancellationStartRequested")
                .Select(envelope => (envelope.MessageId, envelope.WireLine))
        ];
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(requests[0].MessageId, requests[1].MessageId);
        Assert.Equal(PayloadText(requests[0].WireLine), PayloadText(requests[1].WireLine));
        Assert.Empty(harness.Server.RecoveryRequestConflicts);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// 按下之后、确认之前那一行离开了清单：确认时什么都不发，提示叫操作员按那一行的「取消」重来。
    /// </summary>
    [Fact]
    public async Task ARowThatLeftTheWorklistBeforeTheConfirmationSendsNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartStagingRowStopAsync(token);

        WorklistRowCancellationConfirmation press = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB));
        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            Payloads.Worklist(
                2,
                Payloads.Item(DemandA, "TD-A", "SUBLOT-A", WireToGateStagingPickupStop.StagingToWire, "PICKUP", 2)));
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.WorklistItems.Count) == 1,
            "the worklist without B",
            token);

        Assert.True(await press.SendAsync(token));

        Assert.Empty(Received(harness, "LoadCancellationStartRequested"));
        Assert.Contains(
            harness.Events,
            notice => notice.Message.Contains("请重新按那一行的「取消」", StringComparison.Ordinal));
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>服务端拒了：按键答「未被接受」，那一行留着、仍可再按，扫码恢复。</summary>
    [Fact]
    public async Task ARefusedRowCancellationLeavesTheRowAndScanningAsTheyWere()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartStagingRowStopAsync(token, server => server.LoadCancellationDecision = "REJECTED");

        WorklistRowCancellationConfirmation press = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandA));
        Assert.False(await press.SendAsync(token));

        Assert.Single(Received(harness, "LoadCancellationStartRequested"));
        await harness.WaitUntilAsync(
            () => harness.Business.CanSubmitSublot
                && harness.OnUi(() => harness.ViewModel.WorklistItems.All(row => row.CanPressCancellation)),
            "scanning and both rows back",
            token);
    }

    /// <summary>
    /// 装货进行中：在装的那一条没有「取消」（它的取消是「撤销本次开仓」），别的行有按钮但按不动，等这一篮结束。
    /// </summary>
    [Fact]
    public async Task ARowWithALoadUnderwayOffersNoCancellationAndTheOthersWait()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartStagingRowStopAsync(token, io: io);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray()) is
                [{ DemandId: DemandA, OffersCancellation: false }, { DemandId: DemandB, OffersCancellation: true, CanPressCancellation: false }],
            "A without its row cancellation, B's greyed out",
            token);

        Assert.True(harness.OnUi(() => harness.ViewModel.CanPressLoadCancellation));
        Assert.Equal(WireToGateLoadCancellationText.StagingCaption, harness.OnUi(() => harness.ViewModel.LoadCancellationCaption));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>验收 3（回归）：机台取货，同样两条需求，清单上没有行内「取消」，共用入口照旧要先选。</summary>
    [Theory]
    [InlineData("WIRE_TO_GATE")]
    [InlineData("DIE_TO_WIRE_STAGING")]
    public async Task AtAMachinePickupNoRowHasItsOwnCancellation(string workType)
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

        Assert.All(
            harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray()),
            row => Assert.False(row.OffersCancellation || row.CanPressCancellation));
        Assert.All(harness.ScreenState().WorklistRows, row => Assert.False(row.CancellationShown));
        Assert.True(harness.OnUi(() => harness.ViewModel.HasLoadCancellationSelectionHint));
        Assert.Equal("取消装货", harness.OnUi(() => harness.ViewModel.LoadCancellationCaption));
    }

    /// <summary>待送点两条需求（A 2 篮、B 1 篮）、录入请求开着、服务端应答取消。</summary>
    private static async Task<Harness> StartStagingRowStopAsync(
        CancellationToken token,
        Action<FakeControlServer>? alsoConfigure = null,
        FakeIoModuleClient? io = null)
    {
        Harness harness = await StartStagingLoadStopAsync(
            io ?? NewIoWithCargo(),
            WireToGateStagingPickupStop.StagingToWire,
            token,
            server =>
            {
                server.ReplayJourneySnapshotsWithStableIdentity = true;
                server.RespondToLoadCancellationRequests = true;
                server.SublotEntryExpectedSublots = ["SUBLOT-A", "SUBLOT-B"];
                alsoConfigure?.Invoke(server);
            });
        try
        {
            await harness.WaitUntilAsync(
                () => harness.Business.CanSubmitSublot
                    && harness.OnUi(() => harness.ViewModel.CurrentStopDeparture.IsShown),
                "the staging stop's entry request and its 「出发」",
                token);
            harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
            await harness.WaitUntilAsync(
                () => harness.OnUi(() => harness.ViewModel.WorklistItems.All(row => row.CanPressCancellation)),
                "both rows' cancellation",
                token);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }
}
