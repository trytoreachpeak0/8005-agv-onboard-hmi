using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 装货中取消与这次装货自己的 COMPLETED 结果撞在一起（8005-agv-onboard-hmi#259，按协议 6.0.0 重写：8005-agv-onboard-hmi#392
/// 审查 S1）。
/// </summary>
/// <remarks>
/// <para>
/// v6 下取消只在一篮未装、门都锁好时按得出，按下之后装货不再开锁、等答复。但锁可以不经开锁自己读成脱开：答复在路上时门开了、
/// 操作员放进一篮关上，装货照常完成，结果报给服务端。审查用探针在 <c>b152315</c> 上造出过这种形状；那次提交说「造不出来、
/// 装货冻结」，不对。
/// </para>
/// <list type="bullet">
/// <item>顺序 a：结果先记录，授权后到。Coordinator14 裁定：装货算完成，不准备取消向量、不重开这次尝试，取消按
/// <c>FAILED</c>＋<c>ACTION_NOT_ALLOWED_IN_STATE</c> 回，不做任何仓位 IO。</item>
/// <item>顺序 b：授权先到，结果的确认还没到。向量照常接管这次尝试，读到有货报 <c>FAILED</c>；之后重启，中断结算先查到的是这次
/// 装货的 COMPLETED 结果，取消的结论在先（审查 S-1 护栏）：尝试留着、欠恢复入口，不按完成结掉。</item>
/// </list>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 顺序 a：取消发出、答复扣着时锁读成脱开，操作员放篮关门，装货完成、结果被确认；授权到达，取消不接管、不开门，报 <c>FAILED</c>，
    /// 每仓 <c>NOT_STARTED</c>＋<c>ACTION_NOT_ALLOWED_IN_STATE</c>、读到有货；这次尝试保持已结，之后的命令照常执行。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BASKET-LOADED")]
    public async Task ACancellationAuthorizedAfterTheLoadsResultWasRecordedLeavesTheCompletedLoadAndOpensNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        TaskCompletionSource authorizationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                ConfigureCancellationStop(server);
                server.LoadCancellationAuthorizationHold = authorizationHeld.Task;
            },
            token);
        using ReleaseOnExit releaseAuthorization = new(() => authorizationHeld.TrySetResult());

        Task<bool> press = await PressThenLoadBWhileTheAnswerIsOutAsync(harness, io, token);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { UnsettledSlotOperationAttemptId: null } state
                && state.LastCompletedLoadOperationContext?.SlotOperationAttemptId == AttemptB,
            "B's COMPLETED result to be recorded while the cancellation is unanswered",
            token);

        authorizationHeld.SetResult();
        Assert.False(await press);
        JsonElement result = Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        JsonElement slot = Assert.Single(result.GetProperty("slotResults").EnumerateArray());
        Assert.Equal("NOT_STARTED", slot.GetProperty("outcome").GetString());
        Assert.Equal("OCCUPIED", slot.GetProperty("finalPhysicalState").GetString());
        Assert.Equal(["ACTION_NOT_ALLOWED_IN_STATE"], ReasonCodes(slot));

        WireToGateRecoveryState after = ReadJournal(harness, token);
        Assert.Null(after.RecoveryVector);
        Assert.Null(after.UnsettledSlotOperationAttemptId);
        Assert.Null(after.PendingLoadCancellation);
        Assert.Equal(AttemptB, after.LastCompletedLoadOperationContext?.SlotOperationAttemptId);
        Assert.Equal(1, io.UnlockCount);
        JsonElement load = Assert.Single(ReceivedPayloads(harness, "OperationResult"));
        Assert.Equal("COMPLETED", load.GetProperty("overallOutcome").GetString());
        Assert.False(harness.Business.CanRequestLoadCancellation);

        // The vehicle takes work again: the next demand's command runs.
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [2]);
        await harness.WaitUntilAsync(
            () => io.UnlockCount == 2 && ReadJournal(harness, token).UnsettledSlotOperationAttemptId == AttemptA,
            "A's command to run after the refused cancellation",
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 顺序 b 再加一次重启：装货的 COMPLETED 结果发出、确认扣着，授权先到，向量接管这次尝试，读到有货报 <c>FAILED</c>。进程退出，
    /// 重启后握手补发装货结果并被确认；中断结算看到取消已有结论，尝试留着、恢复入口欠着，不按完成结掉（审查 S-1）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BASKET-LOADED")]
    public async Task ACancellationThatTookTheUnacknowledgedLoadOverKeepsItsAttemptAcrossARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        TaskCompletionSource authorizationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resultAckHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness first = await StartTakeOverStopAsync(
            io,
            server =>
            {
                ConfigureCancellationStop(server);
                server.LoadCancellationAuthorizationHold = authorizationHeld.Task;
                server.OperationResultAckHold = resultAckHeld.Task;
            },
            token,
            journalPath: journalPath);
        using ReleaseOnExit releaseAuthorization = new(() => authorizationHeld.TrySetResult());
        using ReleaseOnExit releaseAck = new(() => resultAckHeld.TrySetResult());

        Task<bool> press = await PressThenLoadBWhileTheAnswerIsOutAsync(first, io, token);
        await first.WaitUntilAsync(
            () => first.Server.OperationResultsHeld == 1,
            "B's COMPLETED result to be held unacknowledged",
            token);

        authorizationHeld.SetResult();
        await first.WaitUntilAsync(
            () => ReceivedPayloads(first, "LoadCancellationResult").Length == 1,
            "the cancellation that took B over to report",
            token);
        JsonElement failed = Assert.Single(ReceivedPayloads(first, "LoadCancellationResult"));
        Assert.Equal("FAILED", failed.GetProperty("overallOutcome").GetString());
        await Record.ExceptionAsync(() => press);
        Assert.Equal(AttemptB, ReadJournal(first, token).UnsettledSlotOperationAttemptId);

        await first.StopVehicleAsync();
        first.Server.SimulateOnboardProcessRestart();
        resultAckHeld.SetResult();
        await using Harness afterRestart = await Harness.StartAgainstAsync(
            first.Server,
            token,
            journalPath,
            io,
            recoveryOptions: TakeOverRecovery,
            messageTimeout: TimeSpan.FromSeconds(30));
        await afterRestart.WaitUntilAsync(
            () => RestoredEntryLines(afterRestart) > 0
                || ReadJournal(afterRestart, token).UnsettledSlotOperationAttemptId is null,
            "the restore to owe the recovery entry, or to settle the attempt",
            token);

        WireToGateRecoveryState after = ReadJournal(afterRestart, token);
        Assert.Equal(AttemptB, after.UnsettledSlotOperationAttemptId);
        Assert.NotEqual(WireToGateRecoveryCheckpoint.ResultRecorded, after.ProvenRecoveryCheckpoint);
        Assert.False(afterRestart.Business.CanRequestLoadCancellation);
        Assert.Equal(1, io.UnlockCount);
        Assert.Empty(afterRestart.UiErrors);
    }

    /// <summary>
    /// Sends B's one-slot load, presses the cancellation over its empty door shut and locked, and while the answer is out
    /// lets the lock read released without a pulse; the operator puts the basket in and shuts the door. Returns the press.
    /// </summary>
    private static async Task<Task<bool>> PressThenLoadBWhileTheAnswerIsOutAsync(
        Harness harness,
        FakeIoModuleClient io,
        CancellationToken token)
    {
        await SendSlotCommandAsync(harness, DemandB, AttemptB, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptB, pulses: 1, token);
        await ShutEmptyUntilCancellationCanBePressedAsync(harness, io, 0, token);
        Task<bool> press = harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        await harness.WaitUntilAsync(
            () => ReceivedPayloads(harness, "LoadCancellationStartRequested").Length == 1 || press.IsCompleted,
            "the cancellation request to reach the server",
            token);
        Assert.False(press.IsCompleted);

        // No pulse: the lock lets go by itself, and the load goes back to waiting at an open door.
        int promptsBefore = PutInPrompts(harness);
        io.OpenDoor(0);
        await harness.WaitUntilAsync(
            () => PutInPrompts(harness) > promptsBefore,
            "the load to prompt again for the door that reads open",
            token);
        Assert.Equal(1, io.UnlockCount);
        io.CloseDoor(0, cargo: true);
        await harness.WaitUntilAsync(
            () => ReceivedPayloads(harness, "OperationResult")
                .Any(result => result.GetProperty("slotOperationAttemptId").GetString() == AttemptB),
            "B's own OperationResult to reach the server while the cancellation is unanswered",
            token);
        return press;
    }

    private static int PutInPrompts(Harness harness) =>
        harness.Events.Count(item => item.Message.Contains("放入货物并关门", StringComparison.Ordinal));
}
