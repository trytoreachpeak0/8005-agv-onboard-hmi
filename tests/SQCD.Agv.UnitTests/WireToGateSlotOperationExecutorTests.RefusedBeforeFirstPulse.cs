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

    /// <summary>Bounded, so an unlock that should have been refused fails the test instead of waiting forever.</summary>
    private static CancellationToken BoundedToken(out CancellationTokenSource bounded)
    {
        bounded = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));
        return bounded.Token;
    }
}
