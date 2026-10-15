using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 8005-agv-onboard-hmi#352（C11 追加）：机台取货先装了 A、最后一条 B 在扫码前被取消，本站清单以 <c>LOAD_CANCELLED</c> 结束，
/// 车上仍有本站装的那一篮，「出发」照样出现。
/// </summary>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// A 装进 1 号仓并记录在案；服务端随后发只剩 B 的清单（同一作业会话），再发 B 被取消后的空清单（无作业会话、
    /// <c>LOAD_CANCELLED</c>）。车记得 A 是在本站装的，所以按钮出现、按得动，确认框是机台那一套。
    /// </summary>
    [Fact]
    public async Task AMachinePickupWhoseLastItemWasCancelledAfterALoadStillOffersDeparture()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(io, _ => { }, token);

        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await harness.Server.SendJourneySnapshotAsync("CurrentStopWorklistSnapshot", Payloads.Worklist(2, Payloads.ItemB));
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).HasLoadOnBoardAt("ST-01"),
            "A's load to be stamped with this station",
            token);
        Assert.False(harness.Business.CurrentStopDeparture.Shown);

        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            new
            {
                stationId = "ST-01",
                worklistRevision = 3,
                operationSessionId = (string?)null,
                stationDepartureDeadlineAt = (DateTimeOffset?)null,
                stopEndedReason = "LOAD_CANCELLED",
                items = Array.Empty<object>()
            });
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.CurrentStopDeparture) is { CanPress: true, Prompt.WorklistRevision: 3 },
            "「出发」 to be pressable after B was cancelled",
            token);

        CurrentStopDepartureDisplay shown = harness.OnUi(() => harness.ViewModel.CurrentStopDeparture);
        Assert.Null(shown.Prompt!.OperationSessionId);
        Assert.Equal(SQCD.Agv.Application.WireToGateCurrentStopDepartureText.MachineConfirmationText(0), shown.ConfirmationText);
        Assert.False(shown.HasSummary);
        Assert.Empty(harness.UiErrors);
    }
}
