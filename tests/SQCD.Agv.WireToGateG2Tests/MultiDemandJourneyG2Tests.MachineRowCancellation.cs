using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ScreenState;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 机台取货停靠上清单每条一个「取消」（CP-0016，修订 <c>REQ-0211</c>；<c>8005-agv-onboard-hmi#363</c>），与待送点
/// （<c>8005-agv-onboard-hmi#353</c>）同一套：每条还没装的需求旁边一个按钮，按下确认后发这一条的扫码前取消
/// （<c>LoadCancellationStartRequested</c>，<c>slotOperationAttemptId</c> 为 null），不开任何仓门。
/// </summary>
/// <remarks>
/// 底部「取消装货」在机台上只留两种用途：装货进行中的取消，以及那一行已离开清单时的收尾；扫码前不再「先选一行再按底部」。
/// 按钮文字「取消装货」不变，服务端 L2 脚本按它找按钮（<c>8005-agv-control-server#753</c>）。
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 验收 1：机台两条需求、什么都没装，两行各有一个按得动的「取消」；底部入口不出现，也不提示「先选」。屏幕状态逐行报出。
    /// </summary>
    /// <remarks>
    /// 原来这一条叫 <c>AtAMachinePickupNoRowHasItsOwnCancellation</c>，钉的是相反的行为（机台没有行内取消、底部入口要先选）。
    /// CP-0016 把机台改成与待送点一样，断言随之反过来；入口与认法不变。
    /// </remarks>
    [Theory]
    [InlineData("WIRE_TO_GATE")]
    [InlineData("DIE_TO_WIRE_STAGING")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task AtAMachinePickupEveryUnloadedRowHasItsOwnCancellation(string workType)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartMachineRowStopAsync(workType, token);

        WorklistItemRow[] rows = harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray());
        Assert.Equal([DemandA, DemandB], rows.Select(row => row.DemandId));
        Assert.All(rows, row => Assert.True(row is { OffersCancellation: true, CanPressCancellation: true }));
        Assert.True(harness.Business.CanRequestWorklistRowCancellation(DemandA));
        Assert.True(harness.Business.CanRequestWorklistRowCancellation(DemandB));

        Assert.False(harness.OnUi(() => harness.ViewModel.CanRequestLoadCancellation));
        Assert.False(harness.OnUi(() => harness.ViewModel.HasLoadCancellationSelectionHint));
        Assert.False(ButtonOf(harness.ScreenState(), "OnLoadCancellationClick").Visible);

        HmiScreenState screen = harness.ScreenState();
        Assert.All(screen.WorklistRows, row => Assert.True(row is { CancellationShown: true, CancellationEnabled: true }));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 验收 1：机台上按 B 那一行，确认框复述 B、说清不再派；确认后只发 B 的扫码前取消，不开门，结果 <c>ALL_EMPTY</c>，A 不受影响。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task PressingARowAtAMachinePickupCancelsThatDemandOnly()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartMachineRowStopAsync("WIRE_TO_GATE", token);

        WorklistRowCancellationConfirmation press = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB));
        Assert.Contains("将要取消：子批 SUBLOT-B", press.Text, StringComparison.Ordinal);
        Assert.Contains("系统不会再派车来运它", press.Text, StringComparison.Ordinal);

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
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>机台上一行按下、答复丢了：那一行再按一次按首次内容补发，服务端不判冲突；等答复期间别的行按不动、扫码关着。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task AMachineRowPressWhoseAnswerWasLostIsResentUnchanged()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartMachineRowStopAsync(
            "WIRE_TO_GATE",
            token,
            server => server.LoadCancellationAuthorizationsToDrop = 1);

        WorklistRowCancellationConfirmation first = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB));
        Assert.False(await first.SendAsync(token));

        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray()) is
                [{ CanPressCancellation: false }, { CanPressCancellation: true }],
            "only B's row to stay pressable while its answer is out",
            token);
        Assert.False(harness.Business.CanSubmitSublot);

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

    /// <summary>机台上服务端拒了：按键答「未被接受」，那一行留着、仍可再按，扫码恢复。</summary>
    [Fact]
    public async Task ARefusedMachineRowCancellationLeavesTheRowAndScanningAsTheyWere()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartMachineRowStopAsync(
            "WIRE_TO_GATE",
            token,
            server => server.LoadCancellationDecision = "REJECTED");

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
    /// 验收 1：机台上装货进行中，在装的那一条没有行内「取消」，别的行有按钮但按不动；在装那一篮的取消仍在底部「取消装货」，
    /// 按得动，确认框照机台原来的说法。
    /// </summary>
    [Fact]
    public async Task AtAMachinePickupARowWithALoadUnderwayOffersNoRowCancellationAndTheBottomEntryCancelsTheLoad()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartMachineRowStopAsync("WIRE_TO_GATE", token, io: io);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray()) is
                [{ DemandId: DemandA, OffersCancellation: false }, { DemandId: DemandB, OffersCancellation: true, CanPressCancellation: false }],
            "A without its row cancellation, B's greyed out",
            token);

        Assert.True(harness.OnUi(() => harness.ViewModel.CanPressLoadCancellation));
        Assert.Equal(WireToGateLoadCancellationText.MachineCaption, harness.OnUi(() => harness.ViewModel.LoadCancellationCaption));
        LoadCancellationConfirmation press = harness.OnUi(() => harness.ViewModel.BeginLoadCancellation());
        Assert.False(press.AtStagingPickupStop);
        Assert.False(press.EndsTheDemandAtStagingPickupStop);
        Assert.Contains("SUBLOT-A", press.Text, StringComparison.Ordinal);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 验收 3：重启后机台清单上更早装好的 A 侧标回到「待分配」，日志里的「车上待交接的需求」记着它在车上：A 那一行没有「取消」，
    /// 硬按也什么都不发；B 照旧可取消。照待送点那条（<see cref="AfterARestartARowAlreadyOnBoardOffersNoCancellation"/>）写。
    /// </summary>
    [Fact]
    public async Task AfterARestartARowAlreadyOnBoardAtAMachinePickupOffersNoCancellation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = await NewJournalSeededAsync(
            WireToGateRecoveryState.Empty with
            {
                LoadsOnBoard = [new WireToGateLoadOnBoard(SettledLoadOf(DemandA))]
            },
            token);
        await using Harness harness = await StartStagingLoadStopAsync(
            new FakeIoModuleClient(),
            "WIRE_TO_GATE",
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.SublotEntryExpectedSublots = ["SUBLOT-B"];
            },
            journalPath);
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray()) is
                [{ DemandId: DemandA }, { DemandId: DemandB, OffersCancellation: true, CanPressCancellation: true }],
            "B's row cancellation",
            token);

        WorklistItemRow a = harness.OnUi(() => harness.ViewModel.WorklistItems[0]);
        Assert.Equal("UNASSIGNED", a.SideCode);
        Assert.False(a.OffersCancellation);
        Assert.False(a.CanPressCancellation);
        Assert.False(harness.Business.CanRequestWorklistRowCancellation(DemandA));

        Assert.True(await harness.Business.BindWorklistRowCancellationPress("硬按 A。", DemandA).SendAsync(token));
        Assert.Empty(Received(harness, "LoadCancellationStartRequested"));
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// 审查 M1（探针 P2）：日志里留着 B 的扫码前取消、已授权、结果未得服务端确认，车在机台两条需求上起来。补报提示要叫操作员按
    /// 屏上有的那个按钮——那一行的「取消」，不是不在屏上的「取消装货」；B 那一行按得动、底部入口不出现，按下补报的是 B。
    /// </summary>
    [Fact]
    public async Task AtAMachinePickupTheRestoredNoticeSendsTheOperatorToTheRowThatIsOnTheScreen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = await NewJournalSeededAsync(
            WireToGateRecoveryState.Empty with
            {
                RecoveryVector = new WireToGateRecoveryVectorContext(
                    WireToGateRecoveryVectorTypes.LoadCancellation,
                    "6f1e2d3c-4b5a-4968-8778-695a4b3c2d1e",
                    null,
                    DemandB,
                    null,
                    null,
                    [],
                    null,
                    "operator-001",
                    "SESSION",
                    DateTimeOffset.UtcNow)
            },
            token);
        await using Harness harness = await StartStagingLoadStopAsync(
            new FakeIoModuleClient(),
            "WIRE_TO_GATE",
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.SublotEntryExpectedSublots = ["SUBLOT-A", "SUBLOT-B"];
            },
            journalPath);
        await harness.WaitUntilAsync(
            () => harness.Events.Any(item => item.Kind == "RESULT_ACK_PENDING"),
            "the restored cancellation's notice",
            token);
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray()) is
                [{ DemandId: DemandA }, { DemandId: DemandB, OffersCancellation: true, CanPressCancellation: true }],
            "B's row pressable",
            token);

        WireToGateOperatorEvent notice = harness.Events.First(item => item.Kind == "RESULT_ACK_PENDING");
        Assert.Contains("可再按一次那一行的「取消」补报", notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("取消装货", notice.Message, StringComparison.Ordinal);
        Assert.False(harness.OnUi(() => harness.ViewModel.CanRequestLoadCancellation));
        Assert.False(ButtonOf(harness.ScreenState(), "OnLoadCancellationClick").Visible);

        Assert.True(await harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB)).SendAsync(token));
        Assert.NotEmpty(Received(harness, "LoadCancellationResult"));
        Assert.All(
            Received(harness, "LoadCancellationResult"),
            result => Assert.Equal(DemandB, result.GetProperty("demandId").GetString()));
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>审查 L1：机台上按一行，暂停扫码的提示写的是那一行的子批，不是笼统的「本站装货」。</summary>
    [Fact]
    public async Task AtAMachinePickupARowPressNoticeNamesThatRowsSublot()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartMachineRowStopAsync(
            "WIRE_TO_GATE",
            token,
            server => server.LoadCancellationAuthorizationsToDrop = 1);

        Assert.False(await harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB)).SendAsync(token));

        Assert.Contains(
            harness.Events,
            item => item.Kind == "RECOVERY_VECTOR_REQUESTED"
                && item.Message.StartsWith("已申请取消子批 SUBLOT-B，", StringComparison.Ordinal));
    }

    /// <summary>机台两条需求（A 2 篮、B 1 篮）、录入请求开着、服务端应答取消，两行的「取消」都已按得动。</summary>
    private static async Task<Harness> StartMachineRowStopAsync(
        string workType,
        CancellationToken token,
        Action<FakeControlServer>? alsoConfigure = null,
        FakeIoModuleClient? io = null)
    {
        Harness harness = await StartStagingLoadStopAsync(
            io ?? new FakeIoModuleClient(),
            workType,
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
            await harness.WaitUntilAsync(() => harness.Business.CanSubmitSublot, "the machine pickup's entry request", token);
            harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
            await harness.WaitUntilAsync(
                () => harness.OnUi(() => harness.ViewModel.WorklistItems.Count == 2
                    && harness.ViewModel.WorklistItems.All(row => row.CanPressCancellation)),
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
