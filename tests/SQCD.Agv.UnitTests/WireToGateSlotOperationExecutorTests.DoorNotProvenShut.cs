using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// A new slot operation over a door the journal still names as possibly open (8005-agv-onboard-hmi#267).
/// </summary>
/// <remarks>
/// The journal is seeded the way an acknowledged <c>UNKNOWN</c> recovery result or a maintainer's manual close-out
/// leaves it (<c>ForgetSettledVector</c>): the vector and the recovery session gone, the unsettled attempt, its
/// operation context and the active unlock set kept. Door 3 is in the set; the new command is for slot 1 only, so a
/// check that read the command's targets alone would find nothing to refuse.
/// </remarks>
public sealed partial class WireToGateSlotOperationExecutorTests
{
    /// <summary>
    /// Door 3 reads open: the command is refused before the journal is written or anything is pulsed, and the record
    /// stays. Shut, the same command -- the server sends it again each round -- runs, and the record it replaced is told.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ADoorInDoubtThatReadsOpenRefusesTheNewOperationAndKeepsTheRecord()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync(cancellationToken: token);
        WireToGateSlotOperationCommand earlier = await SeedDoorInDoubtAsync(fixture, token);
        // Lock feedback open, unlock output still energised.
        await fixture.Io.PulseUnlockAsync(2, token);
        WireToGateRecoveryState seeded = await fixture.Journal.ReadRecoveryStateAsync(token);
        WireToGateSlotOperationCommand command = CreateCommand(OperationType.Load, [1], expectedOccupied: true);

        WireToGateDoorNotProvenShutException refused = await Assert.ThrowsAsync<WireToGateDoorNotProvenShutException>(
            () => fixture.Executor.ExecuteAsync(command, null, token));

        Assert.Equal([3], refused.Doors);
        Assert.True(refused.ReadingFresh);
        Assert.Equal(earlier.SlotOperationAttemptId, refused.RecordedSlotOperationAttemptId);
        Assert.Equal(1, fixture.Io.UnlockCount);
        AssertJournalUnchanged(seeded, await fixture.Journal.ReadRecoveryStateAsync(token));
        Assert.Empty(fixture.Overwrites);
        Assert.False(fixture.Executor.HasOperationInFlight);

        fixture.Io.CloseDoor(2);
        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(command, null, token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal([(1, true)], fixture.Io.Pulses.Skip(1));
        WireToGateRecoveryState after = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.Empty(after.ActiveUnlockSlots);
        Assert.Equal(command.SlotOperationAttemptId, after.UnsettledSlotOperationAttemptId);
        WireToGateJournalOverwrite overwrite = Assert.Single(fixture.Overwrites);
        Assert.Equal(command.SlotOperationAttemptId, overwrite.NewSlotOperationAttemptId);
        Assert.Equal(earlier.SlotOperationAttemptId, overwrite.PreviousSlotOperationAttemptId);
        Assert.Equal([3], overwrite.ClearedActiveUnlockSlots);
        Assert.Null(overwrite.RecoveryVectorType);
    }

    /// <summary>
    /// Door 3 in the set but proven shut by a fresh reading: the operation runs as before and the cleared record is told.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task ADoorInDoubtProvenShutLetsTheNewOperationRun()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync(cancellationToken: token);
        WireToGateSlotOperationCommand earlier = await SeedDoorInDoubtAsync(fixture, token);
        // A fresh reading of door 3 shut, taken now: the fixture trusts a reading for one second, and the one its IO was
        // built with can be older than that by the time a loaded machine gets here.
        fixture.Io.CloseDoor(2);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            null,
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Equal([(1, true)], fixture.Io.Pulses);
        Assert.Empty((await fixture.Journal.ReadRecoveryStateAsync(token)).ActiveUnlockSlots);
        WireToGateJournalOverwrite overwrite = Assert.Single(fixture.Overwrites);
        Assert.Equal(earlier.SlotOperationAttemptId, overwrite.PreviousSlotOperationAttemptId);
        Assert.Equal([3], overwrite.ClearedActiveUnlockSlots);
    }

    /// <summary>
    /// Every slot reads shut, but the reading is older than the executor trusts: nothing is proven, so door 3 is still in
    /// doubt and the command is refused, with no journal write and no pulse.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AStaleReadingProvesNoDoorShutAndRefusesTheNewOperation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync(cancellationToken: token);
        await SeedDoorInDoubtAsync(fixture, token);
        WireToGateRecoveryState seeded = await fixture.Journal.ReadRecoveryStateAsync(token);
        Assert.True(fixture.Io.CurrentSnapshot.GetLocker(2) is { IsKnown: true, IsLocked: true, UnlockOutputRaw: false });
        // The fixture trusts a reading for one second.
        fixture.Io.MakeStale(TimeSpan.FromSeconds(5));

        WireToGateDoorNotProvenShutException refused = await Assert.ThrowsAsync<WireToGateDoorNotProvenShutException>(
            () => fixture.Executor.ExecuteAsync(
                CreateCommand(OperationType.Load, [1], expectedOccupied: true),
                null,
                token));

        Assert.Equal([3], refused.Doors);
        Assert.False(refused.ReadingFresh);
        Assert.Equal(0, fixture.Io.UnlockCount);
        AssertJournalUnchanged(seeded, await fixture.Journal.ReadRecoveryStateAsync(token));
        Assert.Empty(fixture.Overwrites);
    }

    /// <summary>A journal with nothing of an earlier operation on it is replaced without a word.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnOperationOverAnEmptyJournalTellsNoOverwrite()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync(cancellationToken: token);

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            null,
            token);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.Empty(fixture.Overwrites);
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

    private static void AssertJournalUnchanged(WireToGateRecoveryState seeded, WireToGateRecoveryState now)
    {
        Assert.Equal(seeded.UnsettledSlotOperationAttemptId, now.UnsettledSlotOperationAttemptId);
        Assert.Equal(seeded.ProvenRecoveryCheckpoint, now.ProvenRecoveryCheckpoint);
        Assert.Equal(seeded.OperationContext?.SlotOperationAttemptId, now.OperationContext?.SlotOperationAttemptId);
        Assert.Equal(seeded.OperationContext?.Slots, now.OperationContext?.Slots);
        Assert.Equal(seeded.ActiveUnlockSlots, now.ActiveUnlockSlots);
        Assert.Equal(seeded.CompletedSlots, now.CompletedSlots);
        Assert.Equal(seeded.SlotResults, now.SlotResults);
    }
}
