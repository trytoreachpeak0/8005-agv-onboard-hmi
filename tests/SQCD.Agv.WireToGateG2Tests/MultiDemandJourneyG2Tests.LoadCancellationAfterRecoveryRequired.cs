using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 装货的非完成结果已经报给服务端之后，「取消装货」入口不再亮（<c>trytoreachpeak0/8005-agv-onboard-hmi#188</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 服务端把每一个非完成的装货结果判进 <c>RecoveryRequired</c>，而 <c>AuthorizeLoadCancellationAsync</c> 对
/// <c>RecoveryRequired</c> 的 operation 一律拒绝。修之前车载端只看「日志里有未结算的装货」，于是操作员看到一个按了
/// 必定失败的按钮（hmi#172 审查探针：冲突之后 <c>cancel=True</c>）。
/// </para>
/// <para>
/// <b>两个方向都断。</b>先断正向对照：同一台车上一张装货门开着、在等操作员的时候，入口是亮的——这时结果还没发，
/// 服务端会授权，关掉它就是把操作员唯一的退路也收走了。再断本票：另一张装货被占用冲突拒绝、结果报出去之后，入口不亮。
/// 两者之间唯一的差别，就是日志里有没有这次 attempt 的待答 OperationResult。补偿清空入口在第二步亮着，
/// 说明入口确实在结果之后重算过，「不亮」不是因为没刷新。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task TheLoadCancellationEntryClosesOnceTheLoadsNonCompletedResultIsOnItsWay()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Environment.SetEnvironmentVariable(ConflictProofVariable, "g2-multi-demand-proof");
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
                server.RespondToRecoveryRequests = true;
                server.RecoverySlotOperationAttemptId = AttemptB;
            },
            token,
            io: io,
            recoveryOptions: new WireToGateRecoveryOptions(
                ResumeAfterRepairEnabled: true,
                ConflictProofVariable,
                "MAINTENANCE_ADMINISTRATOR",
                "CONFIGURED_PROOF"));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null,
            "the worklist",
            token);

        // 正向对照：A 的门开着、在等操作员，结果还没发——入口亮着。
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator
                && harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptA
                && io.UnlockCount == 1,
            "A's slot to be unlocked and waiting on the operator",
            token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanRequestLoadCancellation,
            "the cancellation entry over A's load in flight",
            token);
        io.CloseDoor(4, cargo: true);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LastCompletedLoadOperationContext?.DemandId == DemandA
                && harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "A to complete and be recorded",
            token);

        // 本票：B 的 1 号仓已有货，被拒；结果报出去，服务端回 RECOVERY_REQUIRED。
        io.CloseDoor(0, cargo: true);
        await SendSlotCommandAsync(harness, DemandB, AttemptB, [1]);
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && harness.ViewModel.CanRequestLoadCompensation,
            "the entries to be recomputed over B's refused load",
            token);
        Assert.Contains(
            ReadJournal(harness, token).PendingResults,
            pending => pending.MessageType == "OperationResult" && pending.BusinessId == AttemptB);

        Assert.False(harness.ViewModel.CanRequestLoadCancellation);
        Assert.False(harness.Business.CanRequestLoadCancellation);
        Assert.Empty(harness.UiErrors);
    }
}
