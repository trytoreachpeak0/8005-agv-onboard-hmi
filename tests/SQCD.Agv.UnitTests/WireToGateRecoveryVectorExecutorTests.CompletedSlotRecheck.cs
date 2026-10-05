using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

public sealed partial class WireToGateRecoveryVectorExecutorTests
{
    /// <summary>
    /// A resumed vector whose completed slot 1 now reads open is refused by the whole-vector precheck. Slot 1 is
    /// UNKNOWN with what the IO reads, it is the door in the active unlock set, the answer is UNKNOWN, and every entry
    /// answers the same (8005-agv-onboard-hmi#255).
    /// </summary>
    /// <remarks>
    /// The journal is what a process leaves that died after the executor's prepared checkpoint counted the already-empty
    /// slot 1 complete and before slot 2's unlock was written. <c>AddRejectedResults</c> skipped completed slots, so slot
    /// 1 kept the <c>COMPLETED/LOCKED</c> it was recorded with, and the answer was <c>FAILED</c> over an empty set while
    /// door 1 read open.
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

        AssertCompletedSlotInDoubt(first, 1);
        Assert.Equal("UNKNOWN", first.OverallOutcome);
        Assert.Equal("PREPARED", first.JournalCheckpoint);
        Assert.Equal("NOT_STARTED", first.SlotResults.Single(slot => slot.SlotNo == 2).Outcome);
        await AssertRecordedAsync(fixture, first, WireToGateRecoveryCheckpoint.Prepared, [1], [1], token);
        Assert.Empty(fixture.Io.Pulses);

        await AssertEveryEntryAnswersTheSameAsync(fixture, context, first, token);
        Assert.Empty(fixture.Io.Pulses);
    }

    /// <summary>
    /// Slot 1 is counted complete, and its door is opened while the vector waits on slot 2. The vector does not reach
    /// the safe finish: slot 1 is UNKNOWN with what the IO reads and is the door in the active unlock set, the answer is
    /// UNKNOWN at the active unlock set, and every entry answers the same (8005-agv-onboard-hmi#255).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>handed-over</c>: slot 2 is the door the cancelled load handed over open, so it is waited on and never pulsed,
    /// and the check of the other doors before a pulse (<c>OtherDoorNotShut</c>) never runs. The reviewer's probe in
    /// hmi#249 round 4: <c>outcome=COMPLETED checkpoint=SafeFinishReached active=[] slot1=COMPLETED/LOCKED</c>.
    /// </para>
    /// <para>
    /// <c>after-pulse</c>: slot 2 is pulsed with every other door shut, and slot 1's door opens after the pulse, while
    /// the operator empties slot 2. The check before the pulse has already run and nothing checked again.
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
            context = await PrepareAsync(
                fixture,
                WireToGateRecoveryVectorTypes.LoadCompensation,
                "c5c5c5c5-c5c5-4c5c-8c5c-c5c5c5c5c5c3",
                [1, 2],
                token);
        }

        List<(string Phase, int[] Active)> phases = [];
        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            (phase, active, _, _) =>
            {
                phases.Add((phase, [.. active]));
                if (phase == "WAITING_OPERATOR" && active.Contains(2))
                {
                    fixture.Io.OpenDoor(0);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal(handedOver ? [] : [2], fixture.Io.Pulses.Select(pulse => pulse.Slot));
        AssertCompletedSlotInDoubt(first, 1);
        Assert.Equal("UNKNOWN", first.OverallOutcome);
        Assert.Equal("ACTIVE_UNLOCK_SET", first.JournalCheckpoint);
        Assert.Equal("COMPLETED", first.SlotResults.Single(slot => slot.SlotNo == 2).Outcome);
        Assert.DoesNotContain(phases, item => item.Phase == "SAFE_FINISH");
        Assert.Equal([1], phases.Last().Active);
        await AssertRecordedAsync(fixture, first, WireToGateRecoveryCheckpoint.ActiveUnlockSet, [1], [1, 2], token);

        int pulses = fixture.Io.Pulses.Count;
        await AssertEveryEntryAnswersTheSameAsync(fixture, context, first, token);
        Assert.Equal(pulses, fixture.Io.Pulses.Count);
    }

    /// <summary>
    /// The latch refuses slot 2's pulse, and by then slot 1 -- opened, emptied and shut earlier in this vector -- reads
    /// open again. Slot 1 is UNKNOWN and the door in the active unlock set, slot 2 keeps the latch's reason, the answer
    /// is UNKNOWN, and every entry answers the same (8005-agv-onboard-hmi#255).
    /// </summary>
    /// <remarks>
    /// The door opens inside the latch check itself: the check of the other doors just before it would otherwise stop
    /// the vector on the open door first, which is the IO-failure path the next test covers.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ALatchRefusalDoesNotReportACompletedSlotWhoseDoorReadsOpenAsCompleted()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TestFixture? created = null;
        int latchChecks = 0;
        await using TestFixture fixture = created = await TestFixture.CreateAsync(
            [true, true],
            cancellationToken: token,
            fatalFaultLatched: () =>
            {
                if (++latchChecks < 2)
                {
                    return false;
                }

                created!.Io.OpenDoor(0);
                return true;
            });
        WireToGateRecoveryVectorContext context = await PrepareAsync(
            fixture,
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "c5c5c5c5-c5c5-4c5c-8c5c-c5c5c5c5c5c4",
            [1, 2],
            token);
        List<(string Phase, int[] Active)> phases = [];

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            (phase, active, _, _) =>
            {
                phases.Add((phase, [.. active]));
                return Task.CompletedTask;
            },
            token);

        Assert.Equal([1], fixture.Io.Pulses.Select(pulse => pulse.Slot));
        AssertCompletedSlotInDoubt(first, 1);
        Assert.Equal("UNKNOWN", first.OverallOutcome);
        Assert.Equal("PREPARED", first.JournalCheckpoint);
        WireToGateSlotExecutionResult refused = first.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal("NOT_STARTED", refused.Outcome);
        Assert.Equal([WireToGateSlotOperationExecutor.FatalFaultLatchedReason], refused.ReasonCodes);
        Assert.Equal([1], phases.Last(item => item.Phase == "PAUSED").Active);
        await AssertRecordedAsync(fixture, first, WireToGateRecoveryCheckpoint.Prepared, [1], [1], token);

        await AssertEveryEntryAnswersTheSameAsync(fixture, context, first, token);
        Assert.Equal([1], fixture.Io.Pulses.Select(pulse => pulse.Slot));
    }

    /// <summary>
    /// Slot 1 was opened, emptied and shut, and reads open again when slot 2's turn comes: the check of the other doors
    /// before slot 2's pulse stops the vector with <c>LOCK_NOT_CLOSED</c>. Slot 2 was never pulsed, so it does not take
    /// the active unlock set; slot 1, the door that reads open, does, and is UNKNOWN rather than COMPLETED
    /// (8005-agv-onboard-hmi#255).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnIoFailureDoesNotReportACompletedSlotWhoseDoorReadsOpenAsCompleted()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, true], cancellationToken: token);
        WireToGateRecoveryVectorContext context = await PrepareAsync(
            fixture,
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "c5c5c5c5-c5c5-4c5c-8c5c-c5c5c5c5c5c5",
            [1, 2],
            token);
        List<(string Phase, int[] Active)> phases = [];

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            (phase, active, _, _) =>
            {
                phases.Add((phase, [.. active]));
                if (phase == "UNLOCKING" && active.Contains(2))
                {
                    fixture.Io.OpenDoor(0);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal([1], fixture.Io.Pulses.Select(pulse => pulse.Slot));
        AssertCompletedSlotInDoubt(first, 1);
        Assert.Equal("UNKNOWN", first.OverallOutcome);
        Assert.Equal("ACTIVE_UNLOCK_SET", first.JournalCheckpoint);
        WireToGateSlotExecutionResult stopped = first.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal("UNKNOWN", stopped.Outcome);
        Assert.Equal(["LOCK_NOT_CLOSED"], stopped.ReasonCodes);
        Assert.Equal([1], phases.Last(item => item.Phase == "PAUSED").Active);
        await AssertRecordedAsync(fixture, first, WireToGateRecoveryCheckpoint.ActiveUnlockSet, [1], [1], token);

        await AssertEveryEntryAnswersTheSameAsync(fixture, context, first, token);
        Assert.Equal([1], fixture.Io.Pulses.Select(pulse => pulse.Slot));
    }

    /// <summary>
    /// The IO module drops out after slot 1 was opened, emptied and shut, and slot 2's own precheck refuses for it. A
    /// reading the vehicle cannot trust shows nothing new about slot 1, which was proven final when it was counted
    /// complete: it stays COMPLETED, nothing is left in the active unlock set, and the answer is the FAILED of a refusal
    /// (8005-agv-onboard-hmi#255).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AReadingTheVehicleCannotTrustDoesNotTakeACompletedSlotBack()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, true], cancellationToken: token);
        WireToGateRecoveryVectorContext context = await PrepareAsync(
            fixture,
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "c5c5c5c5-c5c5-4c5c-8c5c-c5c5c5c5c5c6",
            [1, 2],
            token);

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            (phase, _, completed, _) =>
            {
                if (phase == "VERIFYING" && completed.Contains(1))
                {
                    fixture.Io.SetUnknown();
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("FAILED", first.OverallOutcome);
        Assert.Equal([1], fixture.Io.Pulses.Select(pulse => pulse.Slot));
        WireToGateSlotExecutionResult slot1 = first.SlotResults.Single(slot => slot.SlotNo == 1);
        Assert.Equal(("COMPLETED", "LOCKED"), (slot1.Outcome, slot1.LockState));
        Assert.Equal("NOT_STARTED", first.SlotResults.Single(slot => slot.SlotNo == 2).Outcome);
        await AssertRecordedAsync(fixture, first, WireToGateRecoveryCheckpoint.Prepared, [], [1], token);
    }

    /// <summary>
    /// Two finished slots read open by the time the vector would reach its safe finish: both are UNKNOWN, and only the
    /// first takes the active unlock set -- the journal refuses a wider set (<c>ACTIVE_UNLOCK_SET_MORE_THAN_ONE_SLOT</c>,
    /// REQ-0357), and a refusal there would latch the vehicle instead of answering (8005-agv-onboard-hmi#255 review S1).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task TwoFinishedDoorsOpenAtTheSafeFinishPutOneDoorInTheSet()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([false, false, true], cancellationToken: token);
        WireToGateRecoveryVectorContext context = await PrepareAsync(
            fixture,
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "c5c5c5c5-c5c5-4c5c-8c5c-c5c5c5c5c5c7",
            [1, 2, 3],
            token);

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            (phase, active, _, _) =>
            {
                if (phase == "WAITING_OPERATOR" && active.Contains(3))
                {
                    fixture.Io.OpenDoor(0);
                    fixture.Io.OpenDoor(1);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal([3], fixture.Io.Pulses.Select(pulse => pulse.Slot));
        AssertCompletedSlotInDoubt(first, 1);
        AssertCompletedSlotInDoubt(first, 2);
        Assert.Equal("COMPLETED", first.SlotResults.Single(slot => slot.SlotNo == 3).Outcome);
        Assert.Equal(("UNKNOWN", "ACTIVE_UNLOCK_SET"), (first.OverallOutcome, first.JournalCheckpoint));
        await AssertRecordedAsync(fixture, first, WireToGateRecoveryCheckpoint.ActiveUnlockSet, [1], [1, 2, 3], token);

        await AssertEveryEntryAnswersTheSameAsync(fixture, context, first, token);
        Assert.Equal([3], fixture.Io.Pulses.Select(pulse => pulse.Slot));
    }

    /// <summary>
    /// A cleared slot's door stays shut and locked, but a basket is back behind it before the safe finish. The slot is
    /// UNKNOWN with <c>SLOT_OPERATION_CONFLICT</c> -- the reading is fresh and known, so not <c>SLOT_STATE_UNKNOWN</c> --
    /// and stays out of the active unlock set: no door of it is open. The answer is UNKNOWN at the active unlock set
    /// (8005-agv-onboard-hmi#255 review E2, nit 1).
    /// </summary>
    /// <remarks>
    /// The same slot found by a refusal is FAILED overall at the prepared checkpoint, with the slot UNKNOWN in its result
    /// (<c>RecordRefusalAsync</c> writes Prepared; nothing in the set). The two differ by where the vector stops,
    /// not by what it says of the slot: a vector stopped short of its finish by a refusal has always been FAILED with no
    /// door in doubt, and one that ran every slot and then found a finished one changed can no longer claim COMPLETED, so
    /// it stops at the active unlock set, which reads UNKNOWN. The server takes both to RecoveryRequired.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AShutSlotWithABasketBackIsNotADoorInTheSetAndCarriesTheConflictReason()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([false, true], cancellationToken: token);
        WireToGateRecoveryVectorContext context = await PrepareAsync(
            fixture,
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "c5c5c5c5-c5c5-4c5c-8c5c-c5c5c5c5c5c8",
            [1, 2],
            token);
        List<(string Phase, int[] Active)> phases = [];

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            (phase, active, _, _) =>
            {
                phases.Add((phase, [.. active]));
                if (phase == "WAITING_OPERATOR" && active.Contains(2))
                {
                    fixture.Io.PutBasket(0);
                }

                return Task.CompletedTask;
            },
            token);

        WireToGateSlotExecutionResult slot1 = first.SlotResults.Single(slot => slot.SlotNo == 1);
        Assert.Equal(
            ("UNKNOWN", "OCCUPIED", "LOCKED", "RESET"),
            (slot1.Outcome, slot1.FinalPhysicalState, slot1.LockState, slot1.UnlockOutputState));
        Assert.Equal(["SLOT_OPERATION_CONFLICT"], slot1.ReasonCodes);
        Assert.Equal(("UNKNOWN", "ACTIVE_UNLOCK_SET"), (first.OverallOutcome, first.JournalCheckpoint));
        Assert.Empty(phases.Last().Active);
        await AssertRecordedAsync(fixture, first, WireToGateRecoveryCheckpoint.ActiveUnlockSet, [], [1, 2], token);

        await AssertEveryEntryAnswersTheSameAsync(fixture, context, first, token);
    }

    /// <summary>Slot <paramref name="slotNo"/> is UNKNOWN with the open door the IO reads and the precheck's reason.</summary>
    private static void AssertCompletedSlotInDoubt(WireToGateRecoveryVectorExecutionResult result, int slotNo)
    {
        WireToGateSlotExecutionResult slot = result.SlotResults.Single(item => item.SlotNo == slotNo);
        Assert.True(
            slot.Outcome == "UNKNOWN",
            $"outcome={result.OverallOutcome} checkpoint={result.JournalCheckpoint} slot{slotNo}={slot.Outcome}/{slot.LockState}");
        Assert.Equal("UNLOCKED", slot.LockState);
        Assert.Equal("RESET", slot.UnlockOutputState);
        Assert.Equal("EMPTY", slot.FinalPhysicalState);
        Assert.Equal(["LOCK_NOT_CLOSED"], slot.ReasonCodes);
    }

    private static async Task AssertRecordedAsync(
        TestFixture fixture,
        WireToGateRecoveryVectorExecutionResult result,
        WireToGateRecoveryCheckpoint checkpoint,
        int[] active,
        int[] completed,
        CancellationToken token)
    {
        WireToGateRecoveryState recorded = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(checkpoint, recorded.ProvenRecoveryCheckpoint);
        Assert.Equal(active, recorded.ActiveUnlockSlots);
        Assert.Equal(completed, recorded.CompletedSlots);
        Assert.Equal(result.ObservedAt, recorded.RecoveryResultObservedAt);
    }

    /// <summary>
    /// The settle, the replayed command and a fresh executor's replay give the first answer back, and the refusal entry
    /// declines a vector that acted.
    /// </summary>
    private static async Task AssertEveryEntryAnswersTheSameAsync(
        TestFixture fixture,
        WireToGateRecoveryVectorContext context,
        WireToGateRecoveryVectorExecutionResult first,
        CancellationToken token)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(await fixture.Executor.RefuseBeforeUnlockAsync(context, "VEHICLE_NOT_READY", token));
        AssertSameAnswer(first, await fixture.Executor.SettleWithoutUnlockAsync(context, token));
        AssertSameAnswer(first, await fixture.Executor.ExecuteClearAsync(context, null, token));
        await using WireToGateRecoveryVectorExecutor restarted = RestartedExecutor(fixture);
        AssertSameAnswer(first, await restarted.ExecuteClearAsync(context, null, token));
    }
}
