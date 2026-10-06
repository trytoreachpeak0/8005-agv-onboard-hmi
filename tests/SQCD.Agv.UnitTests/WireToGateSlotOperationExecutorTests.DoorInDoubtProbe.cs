using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// Probes for onboard-hmi#267 (investigation first, no fix yet): what the executor does with a door the journal still
/// names as possibly open when a new slot operation command arrives.
/// </summary>
/// <remarks>
/// The journal is seeded the way onboard-hmi#150's manual close-out leaves it (<c>ForgetSettledVector</c>): the vector and
/// the recovery session gone, the unsettled attempt, its operation context and the active unlock set kept. These pin the
/// behaviour at 4bbbbfbe as found, so they assert what happens today, not what should.
/// </remarks>
public sealed partial class WireToGateSlotOperationExecutorTests
{
    /// <summary>Door 3 really is open on the IO: the single-door rule stops the new operation, the journal loses door 3.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ProbeHmi267ADoorReallyOpenStopsTheNewOperationButItsRecordIsDropped()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync(cancellationToken: token);
        WireToGateSlotOperationCommand earlier = await SeedDoorInDoubtAsync(fixture, token);
        // Lock feedback open, unlock output still energised.
        await fixture.Io.PulseUnlockAsync(2, token);
        Assert.False(fixture.Io.CurrentSnapshot.GetLocker(2).IsLocked);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            null,
            token);

        // The pre-check reads only slot 1 and passes; the whole-vehicle single-door check before the pulse
        // (REQ-0357) refuses. Slot 1 is never pulsed: the only pulse is the seeding one.
        Assert.Equal("UNKNOWN", result.OverallOutcome);
        Assert.Equal("NOT_STARTED", result.SlotResults.Single(slot => slot.SlotNo == 1).Outcome);
        Assert.Equal(1, fixture.Io.UnlockCount);

        // But the new attempt started from a fresh journal: door 3 and the earlier attempt are no longer on file.
        WireToGateRecoveryState after = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Empty(after.ActiveUnlockSlots);
        Assert.NotEqual(earlier.SlotOperationAttemptId, after.UnsettledSlotOperationAttemptId);
        Assert.NotEqual(earlier.SlotOperationAttemptId, after.OperationContext?.SlotOperationAttemptId);
        Assert.False(fixture.Io.CurrentSnapshot.GetLocker(2).IsLocked);
    }

    /// <summary>Door 3 reads shut on a fresh IO reading: the new operation completes and the record goes with it.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ProbeHmi267ADoorProvenShutLetsTheNewOperationRunAndItsRecordIsDropped()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync(cancellationToken: token);
        WireToGateSlotOperationCommand earlier = await SeedDoorInDoubtAsync(fixture, token);
        Assert.True(fixture.Io.CurrentSnapshot.GetLocker(2) is { IsKnown: true, IsLocked: true, UnlockOutputRaw: false });

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            null,
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal([(1, true)], fixture.Io.Pulses);
        WireToGateRecoveryState after = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Empty(after.ActiveUnlockSlots);
        Assert.NotEqual(earlier.SlotOperationAttemptId, after.UnsettledSlotOperationAttemptId);
    }

    private static async Task<WireToGateSlotOperationCommand> SeedDoorInDoubtAsync(
        TestFixture fixture,
        CancellationToken token)
    {
        WireToGateSlotOperationCommand earlier = CreateCommand(OperationType.Load, [3], expectedOccupied: true);
        await fixture.Journal.UpdateRecoveryStateAsync(
            _ => WireToGateRecoveryState.Empty with
            {
                UnsettledSlotOperationAttemptId = earlier.SlotOperationAttemptId,
                ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                OperationContext = WireToGateRecoveryOperationContext.FromCommand(earlier),
                ActiveUnlockSlots = [3]
            },
            token);
        return earlier;
    }
}
