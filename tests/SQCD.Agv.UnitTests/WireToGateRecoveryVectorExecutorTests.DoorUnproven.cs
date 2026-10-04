using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// A cancellation or compensation clear over a slot the light curtain proves EMPTY while the lock cannot
/// be proven locked or the unlock output cannot be proven reset (CP-0009, REQ-0357, REQ-0364,
/// 8005-agv-onboard-hmi#219).
/// </summary>
/// <remarks>
/// The literals are spelt out rather than read from the executor on purpose: the protocol values are the
/// contract, and a constant renamed or retyped on the product side has to turn these red.
/// </remarks>
public sealed partial class WireToGateRecoveryVectorExecutorTests
{
    private const string DoorUnprovenOutcome = "ALL_EMPTY_DOOR_UNPROVEN";
    private const string DoorUnprovenReason = "SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY";

    /// <summary>
    /// The compensation starts over a slot that is already empty and whose lock does not read locked or
    /// whose output does not read reset. Reopening it could not prove the lock either, and would only open
    /// one more door, so nothing is pulsed: the slot is reported EMPTY with its lock and output as read and
    /// the reason, and the whole result is the new value.
    /// </summary>
    [Theory]
    [InlineData("lock-unknown", "UNKNOWN", "RESET")]
    [InlineData("lock-open", "UNLOCKED", "RESET")]
    [InlineData("output-active", "LOCKED", "ACTIVE")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    public async Task ACompensationOverASlotAlreadyEmptyWithItsDoorUnprovenOpensNoDoorAndSettlesAllEmptyDoorUnproven(
        string condition,
        string lockState,
        string outputState)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([false, false], cancellationToken: token);
        switch (condition)
        {
            case "lock-unknown":
                fixture.Io.LoseLockFeedback(0);
                break;
            case "lock-open":
                fixture.Io.OpenDoor(0);
                break;
            default:
                fixture.Io.RaiseOutput(0);
                break;
        }

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(WireToGateRecoveryVectorTypes.LoadCompensation, "21921921-9219-4219-8219-219219219001", [1, 2]),
            null,
            token);

        Assert.Equal(DoorUnprovenOutcome, result.OverallOutcome);
        Assert.Empty(fixture.Io.Pulses);
        Assert.Equal(
            [
                new WireToGateSlotExecutionResult(1, "FAILED", "EMPTY", lockState, outputState, [DoorUnprovenReason]),
                new WireToGateSlotExecutionResult(2, "COMPLETED", "EMPTY", "LOCKED", "RESET", [])
            ],
            result.SlotResults,
            SlotResultComparer.Instance);
    }

    /// <summary>
    /// The operator empties the slot and its lock never reports closed again. The wait times out as it
    /// always did, but the light curtain proves the slot empty, so the clear settles EMPTY with the door
    /// unproven instead of reporting the slot UNKNOWN; the door may still be open, so it stays the active
    /// unlock set until the result is settled.
    /// </summary>
    [Theory]
    [InlineData(false, "UNLOCKED")]
    [InlineData(true, "UNKNOWN")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    public async Task AnEmptiedSlotWhoseLockNeverClosesEndsTheCompensationAsAllEmptyDoorUnproven(
        bool lockFeedbackLost,
        string lockState)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, false], cancellationToken: token);
        (lockFeedbackLost ? fixture.Io.LockFeedbackLostOnEmptySlots : fixture.Io.LockNeverClosesSlots).Add(1);
        WireToGateRecoveryVectorContext context = CreateContext(
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "21921921-9219-4219-8219-219219219002",
            [1, 2]);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(context, null, token);

        Assert.Equal(DoorUnprovenOutcome, result.OverallOutcome);
        Assert.Equal([1], fixture.Io.Pulses.Select(pulse => pulse.Slot));
        Assert.Equal(
            [
                new WireToGateSlotExecutionResult(1, "FAILED", "EMPTY", lockState, "RESET", [DoorUnprovenReason]),
                new WireToGateSlotExecutionResult(2, "COMPLETED", "EMPTY", "LOCKED", "RESET", [])
            ],
            result.SlotResults,
            SlotResultComparer.Instance);
        WireToGateRecoveryState state = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal([1], state.ActiveUnlockSlots);
        Assert.Equal(result.ObservedAt, state.RecoveryResultObservedAt);
    }

    /// <summary>
    /// The door an aborted load left open is emptied and never locks: the cancellation settles EMPTY with
    /// the door unproven, having pulsed nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-EMPTY-DOOR-UNPROVEN")]
    public async Task AHandedOverDoorEmptiedButNeverLockedEndsTheCancellationAsAllEmptyDoorUnproven()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, false], cancellationToken: token);
        fixture.Io.OpenDoor(0);
        fixture.Io.LockNeverClosesSlots.Add(1);
        WireToGateRecoveryVectorContext context = await HandOverAsync(
            fixture,
            "21921921-9219-4219-8219-219219219003",
            [1, 2],
            [1],
            token);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(context, null, token);

        Assert.Equal(DoorUnprovenOutcome, result.OverallOutcome);
        Assert.Empty(fixture.Io.Pulses);
        WireToGateSlotExecutionResult slot = result.SlotResults[0];
        Assert.Equal(("EMPTY", "UNLOCKED", "RESET"), (slot.FinalPhysicalState, slot.LockState, slot.UnlockOutputState));
        Assert.Equal([DoorUnprovenReason], slot.ReasonCodes);
    }

    /// <summary>
    /// Do not over-fix: the first slot is emptied and its lock never closes, and the second still holds a
    /// basket. Opening it would put a second door beside the unproven one (REQ-0357), so it is not opened;
    /// with a slot still to empty the clear is not settled, and pauses for manual recovery as it always has.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    public async Task ASlotEmptiedWhoseLockNeverClosesStopsTheClearBeforeALoadedSlotOpens()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, true], cancellationToken: token);
        fixture.Io.LockNeverClosesSlots.Add(1);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(WireToGateRecoveryVectorTypes.LoadCompensation, "21921921-9219-4219-8219-219219219004", [1, 2]),
            null,
            token);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal([1], fixture.Io.Pulses.Select(pulse => pulse.Slot));
        Assert.DoesNotContain(result.SlotResults, slot => slot.ReasonCodes.Contains(DoorUnprovenReason));
        Assert.Equal("NOT_STARTED", result.SlotResults[1].Outcome);
        Assert.Equal("OCCUPIED", result.SlotResults[1].FinalPhysicalState);
    }

    /// <summary>
    /// The same at the start: one slot already empty with its door unproven, the other still loaded. The
    /// loaded slot is not opened beside it, and the clear is refused as it always was -- no new value.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    public async Task AnUnprovenEmptyDoorAtTheStartRefusesAClearThatStillHasALoadedSlot()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([false, true], cancellationToken: token);
        fixture.Io.OpenDoor(0);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(WireToGateRecoveryVectorTypes.LoadCompensation, "21921921-9219-4219-8219-219219219005", [1, 2]),
            null,
            token);

        Assert.Equal("FAILED", result.OverallOutcome);
        Assert.Empty(fixture.Io.Pulses);
        Assert.DoesNotContain(result.SlotResults, slot => slot.ReasonCodes.Contains(DoorUnprovenReason));
    }

    /// <summary>
    /// The light curtain proves nothing when it cannot be read: emptied or not, the slot is UNKNOWN and the
    /// clear stays UNKNOWN, at the start (refused) and after the pulse (timed out).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    public async Task ALightCurtainThatCannotBeReadNeverSettlesAsDoorUnproven(bool atStart)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([!atStart], cancellationToken: token);
        if (atStart)
        {
            fixture.Io.LoseLightCurtain(0);
            fixture.Io.OpenDoor(0);
        }
        else
        {
            fixture.Io.LightCurtainLostSlots.Add(1);
        }

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(WireToGateRecoveryVectorTypes.LoadCompensation, "21921921-9219-4219-8219-219219219006", [1]),
            null,
            token);

        Assert.Equal(atStart ? "FAILED" : "UNKNOWN", result.OverallOutcome);
        Assert.Equal(atStart ? 0 : 1, fixture.Io.Pulses.Count);
        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults);
        Assert.DoesNotContain(DoorUnprovenReason, slot.ReasonCodes);
        Assert.Equal("UNKNOWN", slot.FinalPhysicalState);
    }

    /// <summary>
    /// The old success is untouched: every door relocks with its output reset, and the clear is COMPLETED --
    /// which the business service reports as ALL_EMPTY -- with no slot carrying the new reason.
    /// </summary>
    [Theory]
    [InlineData(WireToGateRecoveryVectorTypes.LoadCancellation)]
    [InlineData(WireToGateRecoveryVectorTypes.LoadCompensation)]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-EMPTY-DOOR-UNPROVEN")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    public async Task AClearWhoseDoorsAllRelockIsStillCompleted(string vectorType)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true, false, true], cancellationToken: token);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(vectorType, "21921921-9219-4219-8219-219219219007", [1, 2, 3]),
            null,
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.All(result.SlotResults, slot =>
        {
            Assert.Equal(("COMPLETED", "EMPTY", "LOCKED", "RESET"),
                (slot.Outcome, slot.FinalPhysicalState, slot.LockState, slot.UnlockOutputState));
            Assert.Empty(slot.ReasonCodes);
        });
    }

    /// <summary>
    /// A fault cargo handoff is not one of the two clears the change covers: an emptied slot whose lock
    /// never closes is still UNKNOWN there.
    /// </summary>
    [Fact]
    public async Task AFaultCargoHandoffKeepsAnUnprovenEmptyDoorUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true], cancellationToken: token);
        fixture.Io.LockNeverClosesSlots.Add(1);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(WireToGateRecoveryVectorTypes.FaultCargoHandoff, "21921921-9219-4219-8219-219219219008", [1]),
            null,
            token);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.DoesNotContain(DoorUnprovenReason, Assert.Single(result.SlotResults).ReasonCodes);
    }

    /// <summary>
    /// The settlement is replayed, never re-derived. A new executor over the same journal -- the process
    /// after a restart -- is asked again after the lock has since closed: it returns the recorded result
    /// byte for byte, the same observedAt, and asks the IO for no wait at all.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    public async Task ADoorUnprovenSettlementReplaysByteForByteAfterARestartWithoutReadingIoAgain(bool atStart)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([!atStart], cancellationToken: token);
        if (atStart)
        {
            fixture.Io.OpenDoor(0);
        }
        else
        {
            fixture.Io.LockNeverClosesSlots.Add(1);
        }

        WireToGateRecoveryVectorContext context = CreateContext(
            WireToGateRecoveryVectorTypes.LoadCompensation,
            "21921921-9219-4219-8219-219219219009",
            [1]);
        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(context, null, token);
        Assert.Equal(DoorUnprovenOutcome, first.OverallOutcome);

        fixture.Io.CloseDoorKeepingOutput(0);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        int waitsBefore = fixture.Io.WaitCount;
        int pulsesBefore = fixture.Io.Pulses.Count;
        await using WireToGateRecoveryVectorExecutor restarted = new(
            fixture.Io,
            fixture.Journal,
            fixture.Clock,
            new WireToGateSlotOperationExecutorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(1)));

        WireToGateRecoveryVectorExecutionResult replay = await restarted.ExecuteClearAsync(context, null, token);
        WireToGateRecoveryVectorExecutionResult settledWithoutUnlock =
            await restarted.SettleWithoutUnlockAsync(context, token);

        foreach (WireToGateRecoveryVectorExecutionResult again in new[] { replay, settledWithoutUnlock })
        {
            Assert.Equal(first.OverallOutcome, again.OverallOutcome);
            Assert.Equal(first.ObservedAt, again.ObservedAt);
            Assert.Equal(first.JournalCheckpoint, again.JournalCheckpoint);
            Assert.Equal(first.SlotResults, again.SlotResults, SlotResultComparer.Instance);
        }

        Assert.Equal(waitsBefore, fixture.Io.WaitCount);
        Assert.Equal(pulsesBefore, fixture.Io.Pulses.Count);
    }
}
