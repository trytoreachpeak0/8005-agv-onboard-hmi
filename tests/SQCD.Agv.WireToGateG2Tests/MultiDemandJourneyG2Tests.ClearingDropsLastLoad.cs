using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 旅程结束、车转去清桩之后，上一趟装货的修正与补偿不再出现（8005-agv-onboard-hmi#330）。
/// </summary>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 现场所见（10-12，agv02）：搬运装完两条、会话被判需恢复，屏上有「补偿目标：子批 …」和「只能修正本站最后一次装货：子批 …」；
    /// 车随后转为清桩（<c>activePurpose=CLEARING_MAINTENANCE</c>，计划只有充电桩那条腿），这两行仍然留着。修后：清桩那份业务状态一到，
    /// 日志里上一次完成的装货被清掉，修正与补偿入口关闭、补偿目标为空，操作员收到一句说明；会话仍是需恢复，说明关掉入口的是清掉的那条
    /// 记录，不是会话变就绪。到站那一格写「清桩：未收到开往等待点的计划」。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AClearingAfterTheJourneyOffersNoCorrectionOrCompensationOverItsLoads()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(io, _ => { }, token);
        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await LoadAndRecordAsync(harness, io, DemandB, AttemptB, slot: 5, token);
        await HoldTheSessionForRecoveryAsync(harness, token);
        await harness.WaitUntilAsync(
            () =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                // The two target lines come from the journal re-read, a different path from the entry gates' cache: right
                // after B's load the correction line could still name A. Waited for with the gates, not read after them.
                return harness.ViewModel.CanRequestLoadCompensation
                    && harness.ViewModel.CanRequestLoadCorrection
                    && harness.ViewModel.CompensationTargetText == "补偿目标：子批 SUBLOT-B"
                    && harness.ViewModel.LoadCorrectionTargetText == "只能修正本站最后一次装货：子批 SUBLOT-B";
            },
            "the compensation and correction entries over the transport's last load, B",
            token);

        await harness.Server.SendJourneySnapshotAsync(
            "UpcomingStopPlanSnapshot",
            Payloads.Plan(2, [Payloads.Leg(1, null, "CHARGER", null, "CH-01", "ARRIVED")]));
        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", ClearingBusinessState(revision: 2));

        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LastCompletedLoadOperationContext is null,
            "the clearing to drop the last completed load",
            token);
        await harness.WaitUntilAsync(
            () =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                return !harness.ViewModel.CanRequestLoadCompensation
                    && !harness.ViewModel.CanRequestLoadCorrection
                    && harness.ViewModel.CompensationTargetText.Length == 0;
            },
            "the compensation and correction entries to close",
            token);

        // The visit cell is the journey projection's, written on the UI thread by another handler than the one that drops
        // the record: waited for, not read once.
        await harness.WaitUntilAsync(
            () => harness.ViewModel.VisitText == "清桩：未收到开往等待点的计划",
            "the visit cell to say no plan to a waiting point was received",
            token);
        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, harness.Session.Current.Readiness);
        Assert.False(harness.ViewModel.HasCompensationTarget);
        Assert.Null(harness.Business.CompensationFallbackDemandId);
        Assert.Equal(WireToGateIdleReturnText.ClearingNoWaitingPointPlanStatus, harness.ViewModel.IdleReturnStatus);
        await harness.WaitUntilAsync(
            () => harness.LogsSnapshot().Any(line => line.Message.Contains(
                "本趟行程已结束，「车上待交接的需求」已清空。",
                StringComparison.Ordinal)),
            "the operator told that the journey ended",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 只剩「上一次完成的装货」可清（「车上待交接的需求」已被先到的一份收尾快照清空）时，也照样清掉并告诉操作员——现场正是这种形状。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AClearingDropsTheLastLoadEvenAfterTheListWasEmptied()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WireToGateRecoveryOperationContext loadB = SettledLoadOf(DemandB) with { Slots = [5], ExpectedBasketCount = 1 };
        string journalPath = await NewJournalSeededAsync(
            WireToGateRecoveryState.Empty with
            {
                ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ResultRecorded,
                LoadsOnBoard = [],
                LastCompletedLoadOperationContext = loadB
            },
            token);
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(io, _ => { }, token, journalPath: journalPath);
        await harness.WaitUntilAsync(
            () =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                return harness.ViewModel.CanRequestLoadCorrection;
            },
            "the correction entry over the seeded last load",
            token);

        await harness.Server.SendJourneySnapshotAsync(
            "UpcomingStopPlanSnapshot",
            Payloads.Plan(2, [Payloads.Leg(1, null, "CHARGER", null, "CH-01", "ARRIVED")]));
        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", ClearingBusinessState(revision: 2));

        await harness.WaitUntilAsync(
            () => harness.LogsSnapshot().Any(line => line.Message.Contains(
                "本趟行程已结束，上一趟装货的修正与补偿入口已关闭。",
                StringComparison.Ordinal)),
            "the operator told that the last load's entries closed",
            token);
        Assert.Null(ReadJournal(harness, token).LastCompletedLoadOperationContext);
        await harness.WaitUntilAsync(
            () =>
            {
                harness.OnUi(harness.ViewModel.RefreshWireToGateInputState);
                return !harness.ViewModel.CanRequestLoadCorrection;
            },
            "the correction entry to close",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>The business state of a charger clearance: purpose <c>CLEARING_MAINTENANCE</c>, nothing loading.</summary>
    private static object ClearingBusinessState(long revision) =>
        new
        {
            vehicleBusinessStateRevision = revision,
            readiness = "READY",
            activePurpose = "CLEARING_MAINTENANCE",
            manualChargingHold = false,
            batteryState = "SUFFICIENT",
            chargingCycleState = "UNABLE_TO_CHARGE",
            loadingPhase = (object?)null,
            blockingFacts = Array.Empty<object>(),
            observedAt = DateTimeOffset.UtcNow
        };
}
