using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

public sealed partial class WireToGateRecoveryVectorExecutorTests
{
    /// <summary>
    /// A resumed vector whose completed slot 1 now reads open is refused by the whole-vector precheck. Slot 1 is not
    /// reported <c>COMPLETED</c> while its door reads open, and every entry answers the same (8005-agv-onboard-hmi#255).
    /// </summary>
    /// <remarks>
    /// The journal is what a process leaves that died after the executor's prepared checkpoint counted the already-empty
    /// slot 1 complete and before slot 2's unlock was written. <c>AddRejectedResults</c> skipped completed slots, so slot
    /// 1 kept its <c>COMPLETED</c> over a reading of <c>UNLOCKED</c>.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task APrecheckRefusalDoesNotReportACompletedSlotWhoseDoorReadsOpenAsCompleted()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([false, true], cancellationToken: token);
        WireToGateRecoveryVectorContext context = CreateContext(
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "c5c5c5c5-c5c5-4c5c-8c5c-c5c5c5c5c5c1",
            [1, 2]);
        await fixture.Journal.UpdateRecoveryStateAsync(
            _ => new WireToGateRecoveryState(
                context.SlotOperationAttemptId,
                WireToGateRecoveryCheckpoint.Prepared,
                [],
                0,
                [])
            {
                RecoveryVector = context,
                CompletedSlots = [1],
                SlotResults = [new WireToGateSlotExecutionResult(1, "COMPLETED", "EMPTY", "LOCKED", "RESET", [])]
            },
            token);
        fixture.Io.OpenDoor(0);

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(context, null, token);

        WireToGateSlotExecutionResult slot1 = first.SlotResults.Single(slot => slot.SlotNo == 1);
        Assert.True(
            slot1.Outcome != "COMPLETED",
            $"outcome={first.OverallOutcome} checkpoint={first.JournalCheckpoint} slot1={slot1.Outcome}/{slot1.LockState}");
        Assert.Equal("UNLOCKED", slot1.LockState);
        Assert.Empty(fixture.Io.Pulses);

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        AssertSameAnswer(first, await fixture.Executor.SettleWithoutUnlockAsync(context, token));
        AssertSameAnswer(first, await fixture.Executor.ExecuteClearAsync(context, null, token));
        await using WireToGateRecoveryVectorExecutor restarted = RestartedExecutor(fixture);
        AssertSameAnswer(first, await restarted.ExecuteClearAsync(context, null, token));
        Assert.Empty(fixture.Io.Pulses);
    }

    /// <summary>
    /// Slot 1 is counted complete from the vector's first reading, and its door is opened while the vector waits on
    /// slot 2. The vector does not reach the safe finish as <c>COMPLETED</c> with slot 1 <c>COMPLETED</c> over a door
    /// that reads open, and every entry answers the same (8005-agv-onboard-hmi#255).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>handed-over</c>: slot 2 is the door the cancelled load handed over open, so it is waited on and never pulsed,
    /// and the check of the other doors before a pulse (<c>OtherDoorNotShut</c>) never runs. The reviewer's probe in
    /// hmi#249 round 4: <c>outcome=COMPLETED checkpoint=SafeFinishReached active=[] slot1=COMPLETED/LOCKED</c>.
    /// </para>
    /// <para>
    /// <c>after-pulse</c>: slot 2 is pulsed with every other door shut, and slot 1's door opens after the pulse, while
    /// the operator empties slot 2. The check before the pulse has already run and nothing checks again.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("handed-over")]
    [InlineData("after-pulse")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompletedSlotOpenedBeforeTheSafeFinishIsNotReportedCompleted(string path)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        bool handedOver = path == "handed-over";
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [false, !handedOver],
            cancellationToken: token);
        WireToGateRecoveryVectorContext context;
        if (handedOver)
        {
            fixture.Io.OpenDoor(1);
            context = await HandOverAsync(fixture, "c5c5c5c5-c5c5-4c5c-8c5c-c5c5c5c5c5c2", [1, 2], [2], token);
        }
        else
        {
            context = CreateContext(
                WireToGateRecoveryVectorTypes.LoadCompensation,
                "c5c5c5c5-c5c5-4c5c-8c5c-c5c5c5c5c5c3",
                [1, 2]);
        }

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            (phase, active, _, _) =>
            {
                if (phase == "WAITING_OPERATOR" && active.Contains(2))
                {
                    fixture.Io.OpenDoor(0);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal(handedOver ? [] : [2], fixture.Io.Pulses.Select(pulse => pulse.Slot));
        Assert.False(fixture.Io.CurrentSnapshot.GetLocker(0).IsLocked);
        WireToGateSlotExecutionResult slot1 = first.SlotResults.Single(slot => slot.SlotNo == 1);
        Assert.True(
            first.OverallOutcome != "COMPLETED" && slot1.Outcome != "COMPLETED",
            $"outcome={first.OverallOutcome} checkpoint={first.JournalCheckpoint} slot1={slot1.Outcome}/{slot1.LockState}");

        int pulses = fixture.Io.Pulses.Count;
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        AssertSameAnswer(first, await fixture.Executor.SettleWithoutUnlockAsync(context, token));
        AssertSameAnswer(first, await fixture.Executor.ExecuteClearAsync(context, null, token));
        await using WireToGateRecoveryVectorExecutor restarted = RestartedExecutor(fixture);
        AssertSameAnswer(first, await restarted.ExecuteClearAsync(context, null, token));
        Assert.Equal(pulses, fixture.Io.Pulses.Count);
    }
}
