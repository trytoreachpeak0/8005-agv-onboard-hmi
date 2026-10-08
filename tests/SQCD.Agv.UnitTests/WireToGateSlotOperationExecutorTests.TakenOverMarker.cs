using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

public sealed partial class WireToGateSlotOperationExecutorTests
{
    /// <summary>
    /// 检查点写入清掉「向量接管过这次尝试」的标记，哪怕标记记的正是这次尝试（8005-agv-onboard-hmi#278 审查 X1）。执行器又在
    /// 跑这次尝试——新操作，或续行被忘掉的向量持有过的那次尝试——它得出的结果是这一轮自己的结论，要按结清记录；标记带过
    /// 检查点的话，续行的 COMPLETED 只会被收成「结果自己那一份」，尝试永远结不掉。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ACheckpointOfTheMarkedAttemptDropsTheMarker()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        fixture.Io.JamLock(0);
        await fixture.Executor.ExecuteAsync(command, null, token);
        await fixture.Journal.UpdateRecoveryStateAsync(
            state => state with { TakenOverSlotOperationAttemptId = state.UnsettledSlotOperationAttemptId },
            token);
        Assert.Equal(
            command.SlotOperationAttemptId,
            (await fixture.Journal.ReadRecoveryStateAsync(token)).TakenOverSlotOperationAttemptId);

        // The interrupted settlement writes this attempt's checkpoint, as a resume does.
        await fixture.Executor.SettleInterruptedAsync(token);

        WireToGateRecoveryState after = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(command.SlotOperationAttemptId, after.UnsettledSlotOperationAttemptId);
        Assert.Null(after.TakenOverSlotOperationAttemptId);
    }
}
