using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

public sealed partial class WireToGateRecoveryVectorExecutorTests
{
    /// <summary>The two ways a clear stops FAILED part-way, after one slot was opened and finished.</summary>
    public static TheoryData<string, string> FailedMidVectorPaths => new()
    {
        // The fatal-fault latch refuses the second slot's pulse (8005-agv-onboard-hmi#191).
        { "latch", "execute" },
        { "latch", "settle" },
        // The second slot no longer passes its own precheck when its turn comes.
        { "slot-precheck", "execute" },
        { "slot-precheck", "settle" }
    };

    /// <summary>
    /// A vector that stopped FAILED part-way is answered the same way when it is asked again
    /// (8005-agv-onboard-hmi#249): a command replayed after a reconnect or a restart, or the operator settling
    /// the held vector without carrying on, gets the recorded result -- the same outcome, slot results,
    /// checkpoint and observedAt -- and no door is opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The business service sends both answers under the same deduplication key and messageId. The outbox refuses a
    /// second, different line under that key (<c>BUSINESS_ID_CONTENT_CONFLICT</c>); had the first never reached the
    /// outbox, the server would get UNKNOWN for a vector that had told the vehicle FAILED. Before the fix both paths
    /// checkpointed <c>ACTIVE_UNLOCK_SET</c>, which the replay reads as UNKNOWN.
    /// </para>
    /// <para>
    /// The journal is read where it matters: when the latch path's PAUSED progress goes out the result is already on
    /// file, and the slot the vector finished stays counted complete, so the vector is never mistaken for one that
    /// did nothing.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(FailedMidVectorPaths))]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AVectorThatStoppedFailedPartWayIsAnsweredFailedAgainWithTheRecordedResult(
        string path,
        string replay)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        bool latched = false;
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true, true],
            cancellationToken: token,
            fatalFaultLatched: () => Volatile.Read(ref latched));
        fixture.Io.BeforePulse = slot =>
        {
            if (slot == 1)
            {
                if (path == "latch")
                {
                    Volatile.Write(ref latched, true);
                }
                else
                {
                    // Slot 2's door reads open by the time its turn comes (OpenDoor takes the slot index).
                    fixture.Io.OpenDoor(1);
                }
            }

            return Task.CompletedTask;
        };
        WireToGateRecoveryVectorContext context = await PrepareAsync(
            fixture,
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "b4b4b4b4-b4b4-4b4b-8b4b-b4b4b4b4b4b4",
            [1, 2],
            token);
        List<(string Phase, WireToGateRecoveryState State)> progress = [];

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            RecordJournalAtEachPhase(fixture, progress),
            token);

        Assert.Equal("FAILED", first.OverallOutcome);
        Assert.Equal([1], fixture.Io.Pulses.Select(pulse => pulse.Slot));
        WireToGateRecoveryState recorded = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(WireToGateRecoveryCheckpoint.Prepared, recorded.ProvenRecoveryCheckpoint);
        Assert.Empty(recorded.ActiveUnlockSlots);
        Assert.Equal([1], recorded.CompletedSlots);
        Assert.Equal(first.ObservedAt, recorded.RecoveryResultObservedAt);
        if (path == "latch")
        {
            WireToGateRecoveryState atPaused = Assert.Single(progress, item => item.Phase == "PAUSED").State;
            Assert.Equal(WireToGateRecoveryCheckpoint.Prepared, atPaused.ProvenRecoveryCheckpoint);
            Assert.Equal(first.ObservedAt, atPaused.RecoveryResultObservedAt);
        }

        // Slot 1 was opened and finished: the vector acted, so it is never answered as refused before an unlock.
        Assert.Null(await fixture.Executor.RefuseBeforeUnlockAsync(context, "VEHICLE_NOT_READY", token));

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        Volatile.Write(ref latched, false);
        AssertSameAnswer(
            first,
            replay == "execute"
                ? await fixture.Executor.ExecuteClearAsync(context, null, token)
                : await fixture.Executor.SettleWithoutUnlockAsync(context, token));
        await using WireToGateRecoveryVectorExecutor restarted = RestartedExecutor(fixture);
        AssertSameAnswer(first, await restarted.ExecuteClearAsync(context, null, token));
        Assert.Equal([1], fixture.Io.Pulses.Select(pulse => pulse.Slot));
    }

    /// <summary>
    /// The latch refuses the very first pulse: nothing was opened, so the journal is the shape a refusal before any
    /// unlock leaves -- prepared, nothing active, nothing complete, stamped -- and every entry that can be asked for
    /// the answer gives the first one back, including one on a fresh executor, without a pulse.
    /// </summary>
    /// <remarks>
    /// This is the one vector the change of 8005-agv-onboard-hmi#249 makes forgettable: the CLOSED snapshot's
    /// <c>ForgetRefusedVector</c> now lets it go, which is right for a vector that opened no door.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ALatchBeforeTheFirstUnlockIsAnsweredTheSameFromEveryEntry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true, true],
            cancellationToken: token,
            fatalFaultLatched: () => true);
        WireToGateRecoveryVectorContext context = await PrepareAsync(
            fixture,
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "b4b4b4b4-b4b4-4b4b-8b4b-b4b4b4b4b4b5",
            [1, 2],
            token);
        List<(string Phase, WireToGateRecoveryState State)> progress = [];

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            RecordJournalAtEachPhase(fixture, progress),
            token);

        Assert.Equal("FAILED", first.OverallOutcome);
        Assert.Empty(fixture.Io.Pulses);
        WireToGateRecoveryState recorded = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(WireToGateRecoveryCheckpoint.Prepared, recorded.ProvenRecoveryCheckpoint);
        Assert.Empty(recorded.ActiveUnlockSlots);
        Assert.Empty(recorded.CompletedSlots);
        Assert.Equal(first.ObservedAt, recorded.RecoveryResultObservedAt);
        WireToGateRecoveryState atPaused = Assert.Single(progress, item => item.Phase == "PAUSED").State;
        Assert.Equal(WireToGateRecoveryCheckpoint.Prepared, atPaused.ProvenRecoveryCheckpoint);
        Assert.Equal(first.ObservedAt, atPaused.RecoveryResultObservedAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        AssertSameAnswer(first, await fixture.Executor.RefuseBeforeUnlockAsync(context, "VEHICLE_NOT_READY", token));
        AssertSameAnswer(first, await fixture.Executor.SettleWithoutUnlockAsync(context, token));
        AssertSameAnswer(first, await fixture.Executor.ExecuteClearAsync(context, null, token));
        await using WireToGateRecoveryVectorExecutor restarted = RestartedExecutor(fixture);
        AssertSameAnswer(first, await restarted.ExecuteClearAsync(context, null, token));
        Assert.Empty(fixture.Io.Pulses);
    }

    /// <summary>
    /// A vector left at Prepared with no stamp -- the process died after the executor's prepared checkpoint and
    /// before its first unlock -- is settled by the operator, then asked for again by a settle and by the replayed
    /// command. All three give the same answer (8005-agv-onboard-hmi#249). The first settle used to say UNKNOWN and
    /// stamp the Prepared checkpoint, which both later readings took for FAILED.
    /// </summary>
    /// <remarks>
    /// With nothing in the active unlock set no door is in doubt, so the answer is FAILED. A door a cancelled load
    /// handed over open is in that set at Prepared, may still stand open, and is UNKNOWN throughout.
    /// </remarks>
    [Theory]
    [InlineData("slot-already-empty", "FAILED")]
    [InlineData("door-handed-over-open", "UNKNOWN")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AVectorSettledAtPreparedIsAnsweredTheSameWhenAskedAgain(string left, string expected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([false, true], cancellationToken: token);
        WireToGateRecoveryVectorContext context;
        if (left == "slot-already-empty")
        {
            context = await PrepareAsync(
                fixture,
                WireToGateRecoveryVectorTypes.LoadCompensation,
                "b4b4b4b4-b4b4-4b4b-8b4b-b4b4b4b4b4b7",
                [1, 2],
                token);
            // Slot 1 is already empty and counted complete in the executor's prepared checkpoint; the process dies
            // before the first unlock is written.
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Executor.ExecuteClearAsync(
                context,
                (phase, _, _, _) => phase == "PREPARING"
                    ? throw new InvalidOperationException("simulated crash")
                    : Task.CompletedTask,
                token));
            WireToGateRecoveryState crashed = await fixture.Journal.ReadRecoveryStateAsync(token);
            Assert.Equal(WireToGateRecoveryCheckpoint.Prepared, crashed.ProvenRecoveryCheckpoint);
            Assert.Empty(crashed.ActiveUnlockSlots);
            Assert.Equal([1], crashed.CompletedSlots);
            Assert.Null(crashed.RecoveryResultObservedAt);
        }
        else
        {
            fixture.Io.OpenDoor(1);
            context = await HandOverAsync(fixture, "b4b4b4b4-b4b4-4b4b-8b4b-b4b4b4b4b4b8", [1, 2], [2], token);
        }

        Assert.Null(await fixture.Executor.RefuseBeforeUnlockAsync(context, "VEHICLE_NOT_READY", token));

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.SettleWithoutUnlockAsync(context, token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        WireToGateRecoveryVectorExecutionResult settledAgain =
            await fixture.Executor.SettleWithoutUnlockAsync(context, token);
        WireToGateRecoveryVectorExecutionResult replayed = await fixture.Executor.ExecuteClearAsync(context, null, token);

        Assert.Equal(expected, first.OverallOutcome);
        AssertSameAnswer(first, settledAgain);
        AssertSameAnswer(first, replayed);
        Assert.Empty(fixture.Io.Pulses);
    }

    /// <summary>
    /// A cancellation whose whole-vector precheck refuses -- slot 1's unlock output reads active -- while the door the
    /// aborted load handed over is still open keeps that door in the active unlock set and answers UNKNOWN, the same
    /// from every entry (8005-agv-onboard-hmi#249). It used to write an empty set and answer FAILED, so the next
    /// handshake told the server no door was open while slot 2 stood open.
    /// </summary>
    /// <remarks>
    /// The open door's result is UNKNOWN with what the IO reads and its own precheck reason, as a handed-over door that
    /// ends uncertain mid-vector reports it. A handed-over door the snapshot proves shut is in no doubt -- shut again
    /// empty or over a basket, since shut is what proves a door is not open, whatever it holds: it leaves the set, and
    /// with nothing left in it the refusal is the FAILED it always was.
    /// </remarks>
    [Theory]
    [InlineData(true, false, "UNKNOWN")]
    [InlineData(false, false, "FAILED")]
    [InlineData(false, true, "FAILED")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task APrecheckRefusalKeepsAHandedOverDoorThatMayStandOpenInTheActiveSet(
        bool doorStillOpen,
        bool basketBehindDoor,
        string expected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true, basketBehindDoor],
            cancellationToken: token);
        if (doorStillOpen)
        {
            fixture.Io.OpenDoor(1);
        }

        WireToGateRecoveryVectorContext context = await HandOverAsync(
            fixture,
            "b4b4b4b4-b4b4-4b4b-8b4b-b4b4b4b4b4c1",
            [1, 2],
            [2],
            token);
        // Slot 1 is shut and locked, but its unlock output still reads active: it fails its own precheck.
        fixture.Io.OpenDoorWithOutputActive(0);
        fixture.Io.CloseDoorKeepingOutput(0);

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(context, null, token);

        Assert.Equal(expected, first.OverallOutcome);
        Assert.Equal("PREPARED", first.JournalCheckpoint);
        WireToGateRecoveryState recorded = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(WireToGateRecoveryCheckpoint.Prepared, recorded.ProvenRecoveryCheckpoint);
        Assert.Equal(doorStillOpen ? [2] : [], recorded.ActiveUnlockSlots);
        Assert.Empty(recorded.CompletedSlots);
        Assert.Equal(first.ObservedAt, recorded.RecoveryResultObservedAt);
        WireToGateSlotExecutionResult refused = first.SlotResults.Single(slot => slot.SlotNo == 1);
        Assert.Equal("NOT_STARTED", refused.Outcome);
        Assert.Equal(["UNLOCK_OUTPUT_NOT_RESET"], refused.ReasonCodes);
        WireToGateSlotExecutionResult handedOver = first.SlotResults.Single(slot => slot.SlotNo == 2);
        if (doorStillOpen)
        {
            Assert.Equal("UNKNOWN", handedOver.Outcome);
            Assert.Equal("UNLOCKED", handedOver.LockState);
            Assert.Equal("EMPTY", handedOver.FinalPhysicalState);
            Assert.Equal(["LOCK_NOT_CLOSED"], handedOver.ReasonCodes);
        }
        else
        {
            Assert.Equal("NOT_STARTED", handedOver.Outcome);
            Assert.Equal("LOCKED", handedOver.LockState);
            Assert.Equal(basketBehindDoor ? "OCCUPIED" : "EMPTY", handedOver.FinalPhysicalState);
            Assert.Empty(handedOver.ReasonCodes);
        }

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        WireToGateRecoveryVectorExecutionResult? refusedAgain =
            await fixture.Executor.RefuseBeforeUnlockAsync(context, "VEHICLE_NOT_READY", token);
        if (doorStillOpen)
        {
            // A door in the active set means the vector may have acted: never answered as refused before an unlock.
            Assert.Null(refusedAgain);
        }
        else
        {
            AssertSameAnswer(first, refusedAgain);
        }

        AssertSameAnswer(first, await fixture.Executor.SettleWithoutUnlockAsync(context, token));
        AssertSameAnswer(first, await fixture.Executor.ExecuteClearAsync(context, null, token));
        await using WireToGateRecoveryVectorExecutor restarted = RestartedExecutor(fixture);
        AssertSameAnswer(first, await restarted.ExecuteClearAsync(context, null, token));
        Assert.Empty(fixture.Io.Pulses);
    }

    /// <summary>
    /// The other ways a refusal meets a handed-over door it cannot prove shut, each keeping the door in the active
    /// unlock set and answering UNKNOWN, the same from every entry, with no pulse (8005-agv-onboard-hmi#249):
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>stale-snapshot</c>: the whole-vector precheck refuses because the IO reading is older than the age the
    /// executor trusts; the open door cannot be read at all, so its fields are UNKNOWN.</item>
    /// <item><c>handed-over-unreadable</c>: the handed-over door's own lock feedback cannot be read, which is enough to
    /// refuse the whole vector; slot 1 passes its precheck and is NOT_STARTED with no reason.</item>
    /// <item><c>slot-precheck-stale</c>: the handed-over door was shut over a basket again, so the clear takes it as an
    /// ordinary target; by its turn the reading has gone stale, and the per-slot precheck refuses. Shut in a stale
    /// reading is not proven shut.</item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("stale-snapshot")]
    [InlineData("handed-over-unreadable")]
    [InlineData("slot-precheck-stale")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ARefusalKeepsAHandedOverDoorItCannotProveShutInTheActiveSet(string trigger)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, true], cancellationToken: token);
        int door = trigger == "slot-precheck-stale" ? 1 : 2;
        if (trigger != "slot-precheck-stale")
        {
            fixture.Io.OpenDoor(1);
        }

        WireToGateRecoveryVectorContext context = await HandOverAsync(
            fixture,
            "b4b4b4b4-b4b4-4b4b-8b4b-b4b4b4b4b4c2",
            [1, 2],
            [door],
            token);
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>, CancellationToken, Task>? progress = null;
        switch (trigger)
        {
            case "stale-snapshot":
                // Nothing is read again: the snapshot ages past IoSnapshotMaxAge (one second).
                fixture.Clock.Advance(TimeSpan.FromSeconds(2));
                break;
            case "handed-over-unreadable":
                fixture.Io.LoseLockFeedback(1);
                break;
            default:
                // The whole-vector precheck passed on a fresh reading; the reading ages before slot 1's turn.
                progress = (phase, _, _, _) =>
                {
                    if (phase == "PREPARING")
                    {
                        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
                    }

                    return Task.CompletedTask;
                };
                break;
        }

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            progress,
            token);

        Assert.Equal("UNKNOWN", first.OverallOutcome);
        WireToGateRecoveryState recorded = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(WireToGateRecoveryCheckpoint.Prepared, recorded.ProvenRecoveryCheckpoint);
        Assert.Equal([door], recorded.ActiveUnlockSlots);
        Assert.Equal(first.ObservedAt, recorded.RecoveryResultObservedAt);
        WireToGateSlotExecutionResult handedOver = first.SlotResults.Single(slot => slot.SlotNo == door);
        Assert.Equal("UNKNOWN", handedOver.Outcome);
        Assert.Equal(["SLOT_STATE_UNKNOWN"], handedOver.ReasonCodes);
        if (trigger != "handed-over-unreadable")
        {
            // The reading is stale: what it last said about the door is not stated as the door's state.
            Assert.Equal(
                ("UNKNOWN", "UNKNOWN", "UNKNOWN"),
                (handedOver.FinalPhysicalState, handedOver.LockState, handedOver.UnlockOutputState));
        }
        if (trigger == "handed-over-unreadable")
        {
            WireToGateSlotExecutionResult other = first.SlotResults.Single(slot => slot.SlotNo == 1);
            Assert.Equal("NOT_STARTED", other.Outcome);
            Assert.Empty(other.ReasonCodes);
        }

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(await fixture.Executor.RefuseBeforeUnlockAsync(context, "VEHICLE_NOT_READY", token));
        AssertSameAnswer(first, await fixture.Executor.SettleWithoutUnlockAsync(context, token));
        AssertSameAnswer(first, await fixture.Executor.ExecuteClearAsync(context, null, token));
        await using WireToGateRecoveryVectorExecutor restarted = RestartedExecutor(fixture);
        AssertSameAnswer(first, await restarted.ExecuteClearAsync(context, null, token));
        Assert.Empty(fixture.Io.Pulses);
    }

    /// <summary>
    /// A resumed cancellation whose completed slot 1 no longer reads final, while the door the aborted load handed over
    /// may still stand open: the active unlock set names that door, the answer is UNKNOWN, and every entry answers the
    /// same (8005-agv-onboard-hmi#249 review round 3, S1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The journal is the one a process leaves that died after the prepared checkpoint and before the handed-over door's
    /// own unlock set: Prepared, slot 2 active, slot 1 complete. The set holds one door at most (REQ-0357), and the
    /// handed-over door -- known to have been open -- is the one it holds.
    /// </para>
    /// <para>
    /// <c>basket-appeared</c>: slot 1 is locked and reset but reads a basket. The whole-vector precheck passes and the
    /// resume path's own check stops the vector at the active unlock set, slot 1 UNKNOWN. That early return used to write
    /// slot 1 alone, so the next handshake no longer named slot 2.
    /// </para>
    /// <para>
    /// <c>door-reads-open</c>: slot 1's door reads open. The whole-vector precheck refuses before the resume path's check
    /// is reached, so this is the refusal of <see cref="RecordRefusalAsync"/>, recorded at the prepared checkpoint; the
    /// open door is also in the safety summary as <c>LOCK_NOT_CLOSED</c>.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("basket-appeared", WireToGateRecoveryCheckpoint.ActiveUnlockSet)]
    [InlineData("door-reads-open", WireToGateRecoveryCheckpoint.Prepared)]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AResumeThatFindsACompletedSlotChangedKeepsTheHandedOverDoorInTheActiveSet(
        string change,
        WireToGateRecoveryCheckpoint expectedCheckpoint)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        // Slot 2 is empty with its door open. Slot 1 reads a basket behind a locked door, or is empty with its door open.
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [change == "basket-appeared", false],
            cancellationToken: token);
        fixture.Io.OpenDoor(1);
        if (change == "door-reads-open")
        {
            fixture.Io.OpenDoor(0);
        }

        WireToGateRecoveryVectorContext context = CreateContext(
            WireToGateRecoveryVectorTypes.LoadCancellation,
            "b4b4b4b4-b4b4-4b4b-8b4b-b4b4b4b4b4f1",
            [1, 2]);
        await fixture.Journal.UpdateRecoveryStateAsync(
            _ => new WireToGateRecoveryState(
                context.SlotOperationAttemptId,
                WireToGateRecoveryCheckpoint.Prepared,
                [2],
                0,
                [])
            {
                RecoveryVector = context,
                CompletedSlots = [1],
                SlotResults = [new WireToGateSlotExecutionResult(1, "COMPLETED", "EMPTY", "LOCKED", "RESET", [])]
            },
            token);

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(context, null, token);

        Assert.Equal("UNKNOWN", first.OverallOutcome);
        WireToGateRecoveryState recorded = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(expectedCheckpoint, recorded.ProvenRecoveryCheckpoint);
        Assert.Equal([2], recorded.ActiveUnlockSlots);
        Assert.Equal(first.ObservedAt, recorded.RecoveryResultObservedAt);
        WireToGateSlotExecutionResult door = first.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal("UNKNOWN", door.Outcome);
        Assert.Equal("UNLOCKED", door.LockState);
        Assert.Equal(["LOCK_NOT_CLOSED"], door.ReasonCodes);
        if (change == "basket-appeared")
        {
            Assert.Equal("UNKNOWN", first.SlotResults.Single(slot => slot.SlotNo == 1).Outcome);
        }

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(await fixture.Executor.RefuseBeforeUnlockAsync(context, "VEHICLE_NOT_READY", token));
        AssertSameAnswer(first, await fixture.Executor.SettleWithoutUnlockAsync(context, token));
        AssertSameAnswer(first, await fixture.Executor.ExecuteClearAsync(context, null, token));
        await using WireToGateRecoveryVectorExecutor restarted = RestartedExecutor(fixture);
        AssertSameAnswer(first, await restarted.ExecuteClearAsync(context, null, token));
        Assert.Empty(fixture.Io.Pulses);
    }

    /// <summary>
    /// The latch refuses the pulse of a handed-over door that was shut again over a basket, after its lock feedback was
    /// lost: the door stays in the active unlock set, the PAUSED progress names it, the answer is UNKNOWN -- and the
    /// latch's reason code stays on the slot beside the precheck's, so the operator is still told the vehicle is latched
    /// and the server still hears it (8005-agv-onboard-hmi#249 review round 3, S2).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ALatchRefusalOverAHandedOverDoorItCannotProveShutKeepsTheDoorAndTheLatchReason()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        bool latched = false;
        await using TestFixture fixture = await TestFixture.CreateAsync(
            [true, true],
            fatalFaultLatched: () => Volatile.Read(ref latched),
            cancellationToken: token);
        // Slot 2 was handed over and shut again over its basket: an ordinary target, unlocked to be emptied.
        WireToGateRecoveryVectorContext context = await HandOverAsync(
            fixture,
            "b4b4b4b4-b4b4-4b4b-8b4b-b4b4b4b4b4f2",
            [1, 2],
            [2],
            token);
        List<(string Phase, int[] Active)> phases = [];

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            (phase, active, _, _) =>
            {
                phases.Add((phase, [.. active]));
                if (phase == "UNLOCKING")
                {
                    // Between the slot's own precheck and the latch check before its pulse.
                    Volatile.Write(ref latched, true);
                    fixture.Io.LoseLockFeedback(1);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("UNKNOWN", first.OverallOutcome);
        Assert.Empty(fixture.Io.Pulses);
        WireToGateRecoveryState recorded = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal([2], recorded.ActiveUnlockSlots);
        Assert.Equal([2], phases.Last(item => item.Phase == "PAUSED").Active);
        WireToGateSlotExecutionResult door = first.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal("UNKNOWN", door.Outcome);
        Assert.Equal(
            [WireToGateSlotOperationExecutor.FatalFaultLatchedReason, "SLOT_STATE_UNKNOWN"],
            door.ReasonCodes);
    }

    /// <summary>
    /// A handed-over door this vector already finished is not in doubt, whatever a later reading says: the clear empties
    /// and shuts the handed-over slot 1, the reading goes stale before slot 2's turn, slot 2's precheck refuses, and the
    /// answer is the FAILED of a refusal with nothing open -- slot 1 COMPLETED, nothing in the active unlock set.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AHandedOverDoorThisVectorFinishedIsNotKeptByALaterStaleRefusal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, true], cancellationToken: token);
        fixture.Io.OpenDoor(0);
        WireToGateRecoveryVectorContext context = await HandOverAsync(
            fixture,
            "b4b4b4b4-b4b4-4b4b-8b4b-b4b4b4b4b4f4",
            [1, 2],
            [1],
            token);

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            (phase, _, completed, _) =>
            {
                if (phase == "VERIFYING" && completed.Contains(1))
                {
                    // The operator emptied and shut slot 1; nothing is read again before slot 2's turn.
                    fixture.Clock.Advance(TimeSpan.FromSeconds(2));
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal("FAILED", first.OverallOutcome);
        Assert.Empty(fixture.Io.Pulses);
        WireToGateRecoveryState recorded = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Empty(recorded.ActiveUnlockSlots);
        Assert.Equal([1], recorded.CompletedSlots);
        Assert.Equal("COMPLETED", first.SlotResults.Single(slot => slot.SlotNo == 1).Outcome);
        Assert.Equal("NOT_STARTED", first.SlotResults.Single(slot => slot.SlotNo == 2).Outcome);
    }

    private static Func<string, IReadOnlyList<int>, IReadOnlyList<int>, CancellationToken, Task>
        RecordJournalAtEachPhase(TestFixture fixture, List<(string Phase, WireToGateRecoveryState State)> progress) =>
        async (phase, _, _, cancellationToken) =>
            progress.Add((phase, await fixture.Journal.ReadRecoveryStateAsync(cancellationToken)));

    /// <summary>A second executor over the same journal and IO, as after a restart.</summary>
    private static WireToGateRecoveryVectorExecutor RestartedExecutor(TestFixture fixture) =>
        new(
            fixture.Io,
            fixture.Journal,
            fixture.Clock,
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(1)),
            () => false);

    /// <summary>Everything the business service puts on the wire, plus the checkpoint it logs.</summary>
    private static void AssertSameAnswer(
        WireToGateRecoveryVectorExecutionResult first,
        WireToGateRecoveryVectorExecutionResult? again)
    {
        Assert.NotNull(again);
        Assert.Equal(first.VectorType, again.VectorType);
        Assert.Equal(first.PrimaryId, again.PrimaryId);
        Assert.Equal(first.OverallOutcome, again.OverallOutcome);
        Assert.Equal(first.JournalCheckpoint, again.JournalCheckpoint);
        Assert.Equal(first.ObservedAt, again.ObservedAt);
        Assert.Equal(first.ObservedAt.Offset, again.ObservedAt.Offset);
        Assert.Equal(first.SlotResults, again.SlotResults, SlotResultComparer.Instance);
    }
}
