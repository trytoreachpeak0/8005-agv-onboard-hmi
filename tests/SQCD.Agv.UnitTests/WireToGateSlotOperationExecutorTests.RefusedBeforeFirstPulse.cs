using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// A slot the one-door check (REQ-0357) refuses before it is ever pulsed (8005-agv-onboard-hmi#186, review of
/// PR #190, point 1). It was never opened, so it must not be counted as opened -- by the result, by a restart's
/// settlement, or by a resume -- and "never pulsed" is only claimed where this process knows it.
/// </summary>
public sealed partial class WireToGateSlotOperationExecutorTests
{
    /// <summary>
    /// The review's reproduction. A load of [1, 2] while slot 5, outside the command, is open: slot 1 is refused
    /// before its first pulse. The process exits before the result reaches the outbox; slot 5 is shut since. The
    /// settlement reports slot 1 NOT_STARTED under the reason that stopped it -- it was UNKNOWN, counted as opened
    /// because its journaled result was not NOT_STARTED.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ASlotRefusedBeforeItsFirstPulseSettlesNotStarted()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        fixture.Io.OpenDoor(4);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);
        await fixture.Executor.ExecuteAsync(command, null, token);
        Assert.Empty(fixture.Io.Pulses);
        fixture.Io.CloseDoor(4, cargo: false);

        WireToGateOperationExecutionResult settled = await fixture.Executor.SettleInterruptedAsync(token);

        WireToGateSlotExecutionResult refused = settled.SlotResults.Single(slot => slot.SlotNo == 1);
        Assert.Equal("NOT_STARTED", refused.Outcome);
        Assert.Equal(["LOCK_NOT_CLOSED"], refused.ReasonCodes);
        Assert.Equal("NOT_STARTED", settled.SlotResults.Single(slot => slot.SlotNo == 2).Outcome);
        Assert.Equal("UNKNOWN", settled.OverallOutcome);
        Assert.Empty(fixture.Io.Pulses);
    }

    /// <summary>
    /// The other side: a slot already pulsed and refused on a reopen. The operator shut slot 1 empty on a load, so
    /// it is to be opened again, and by then slot 5 is open. That slot was opened by this attempt and stays UNKNOWN
    /// under the reason, exactly as before.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ASlotRefusedOnAReopenIsStillUnknown()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        bool shut = false;

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (!shut && progress.Phase == "WAITING_OPERATOR")
                {
                    shut = true;
                    fixture.Io.OpenDoor(4);
                    fixture.Io.CloseDoor(0, cargo: false);
                }

                return Task.CompletedTask;
            },
            token);

        Assert.Equal([(1, true)], fixture.Io.Pulses);
        WireToGateSlotExecutionResult slot = result.SlotResults.Single();
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal(["LOCK_NOT_CLOSED"], slot.ReasonCodes);
        Assert.Equal("UNKNOWN", result.OverallOutcome);
    }

    /// <summary>
    /// A resume re-drives slots an earlier run may have pulsed, and the journal cannot tell which: the failure
    /// branch overwrites every unfinished slot as NOT_STARTED. So a slot the one-door check refuses in a resume
    /// stays UNKNOWN even though this run never pulsed it -- conservative where "never pulsed" is not known.
    /// Here slot 1 was pulsed by the first run (its lock never confirmed) and is refused before the resume's pulse.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ASlotRefusedBeforeAResumesFirstPulseIsStillUnknown()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        fixture.Io.JamLock(0);
        WireToGateOperationExecutionResult first = await fixture.Executor.ExecuteAsync(command, null, token);
        Assert.Equal("SAFE_FINISH_REACHED", first.JournalCheckpoint);
        await ArmResumeAsync(fixture);
        fixture.Io.UnjamLock(0);
        fixture.Io.OpenDoor(4);
        int pulsesBeforeResume = fixture.Io.Pulses.Count;

        WireToGateOperationExecutionResult resumed = await fixture.Executor.ResumeAsync(ResumeOf(command), null, token);

        Assert.Equal(pulsesBeforeResume, fixture.Io.Pulses.Count);
        WireToGateSlotExecutionResult slot = resumed.SlotResults.Single();
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal(["LOCK_NOT_CLOSED"], slot.ReasonCodes);
        Assert.Equal("UNKNOWN", resumed.OverallOutcome);
    }

    /// <summary>
    /// The IO drops after slot 1 completes, so the one-door check before slot 2's first pulse cannot read the other
    /// doors and refuses with SLOT_STATE_UNKNOWN. Slot 2 was never opened, so its door cannot be standing open because
    /// of this attempt: the live result, the journal and a later settlement all say so. Before the second review of
    /// PR #190 the failure branch judged the safe finish from the same unreadable snapshot, put slot 2 into the active
    /// set under ACTIVE_UNLOCK_SET, and a settlement then counted it as opened: NOT_STARTED live, UNKNOWN settled.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ASlotRefusedBeforeItsFirstPulseOnAnUnreadableSnapshotIsNotStartedEverywhere()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            command,
            (progress, _) =>
            {
                if (progress.Phase == "WAITING_OPERATOR" && progress.Active.Single() == 1)
                {
                    fixture.Io.CloseDoor(0, cargo: true);
                }
                else if (progress.Phase == "VERIFYING")
                {
                    fixture.Io.Disconnect();
                }

                return Task.CompletedTask;
            },
            token);

        // Live: slot 2 never opened.
        Assert.Equal([(1, true)], fixture.Io.Pulses);
        WireToGateSlotExecutionResult live = result.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal("NOT_STARTED", live.Outcome);
        Assert.Equal(["SLOT_STATE_UNKNOWN"], live.ReasonCodes);
        Assert.Equal("SAFE_FINISH_REACHED", result.JournalCheckpoint);

        // Journal: no door of this attempt may be open.
        WireToGateRecoveryState journaled = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, journaled.ProvenRecoveryCheckpoint);
        Assert.Empty(journaled.ActiveUnlockSlots);

        // Settlement, IO still gone: slot 2 is still the slot this attempt never opened.
        WireToGateOperationExecutionResult settled = await fixture.Executor.SettleInterruptedAsync(token);
        WireToGateSlotExecutionResult settledSlot = settled.SlotResults.Single(slot => slot.SlotNo == 2);
        Assert.Equal("NOT_STARTED", settledSlot.Outcome);
        Assert.Equal(["SLOT_STATE_UNKNOWN"], settledSlot.ReasonCodes);
    }

    /// <summary>
    /// The journal holds a failure and the IO is gone when the settlement reads it. The failed slot keeps the reason it
    /// failed with, not SLOT_STATE_UNKNOWN: the reason says why the slot is in doubt, and the missing reading does not
    /// change that (review of PR #190, point 5). Its physical fields are UNKNOWN, as the reading is.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AFailedSlotKeepsItsReasonWhenTheSettlementCannotReadTheIo()
    {
        CancellationToken token = BoundedToken(out CancellationTokenSource bounded);
        using CancellationTokenSource _ = bounded;
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);
        fixture.Io.JamLock(0);
        WireToGateOperationExecutionResult first = await fixture.Executor.ExecuteAsync(command, null, token);
        IReadOnlyList<string> failedWith = first.SlotResults.Single().ReasonCodes;
        Assert.Equal("UNKNOWN", first.SlotResults.Single().Outcome);
        Assert.NotEqual(["SLOT_STATE_UNKNOWN"], failedWith);
        fixture.Io.Disconnect();

        WireToGateOperationExecutionResult settled = await fixture.Executor.SettleInterruptedAsync(token);

        WireToGateSlotExecutionResult slot = settled.SlotResults.Single();
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal(failedWith, slot.ReasonCodes);
        Assert.Equal("UNKNOWN", slot.FinalPhysicalState);
    }

    /// <summary>Bounded, so an unlock that should have been refused fails the test instead of waiting forever.</summary>
    private static CancellationToken BoundedToken(out CancellationTokenSource bounded)
    {
        bounded = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));
        return bounded.Token;
    }
}
