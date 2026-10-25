using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

public sealed partial class WireToGateSlotOperationExecutorTests
{
    /// <summary>
    /// The wall clock stepped between the last IO reading and the precheck changes nothing (8005-agv-onboard-hmi#404,
    /// workspace#55's hourly <c>Set-Date -Adjust</c>): the slot is opened and the load completes, where reading the age
    /// off the wall clock refused it as <c>SLOT_STATE_UNKNOWN</c> -- the reading "from the future" after a step back, or
    /// past its maximum age after a step forward.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    [InlineData(-500)]
    [InlineData(1_500)]
    public async Task WallClockSteppedBeforeThePrecheckStillOpensTheSlot(int stepMs)
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        fixture.Io.StampTimestamps = true;
        fixture.Io.Refresh();
        fixture.Clock.StepWall(TimeSpan.FromMilliseconds(stepMs));

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("COMPLETED", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults);
        Assert.Equal("COMPLETED", slot.Outcome);
        Assert.Empty(slot.ReasonCodes);
        Assert.Equal(1, fixture.Io.UnlockCount);
    }

    /// <summary>
    /// The wall clock stepped the moment the first door shuts over its basket does not stop the second slot
    /// (8005-agv-onboard-hmi#404): the one-door check in front of its pulse reads the reading the shut door left, which is
    /// no less fresh for the step, so both slots load. Reading its age off the wall clock refused the second slot as
    /// <c>SLOT_STATE_UNKNOWN</c>.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    [InlineData(-500)]
    [InlineData(1_500)]
    public async Task WallClockSteppedAsTheFirstDoorShutsStillOpensTheNextSlot(int stepMs)
    {
        await using TestFixture fixture = await TestFixture.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        fixture.Io.StampTimestamps = true;
        fixture.Io.Refresh();
        bool stepped = false;
        fixture.Io.Updated = locker =>
        {
            if (!stepped && locker.SlotIndex == 0 && locker.IsLocked && locker.HasCargo)
            {
                stepped = true;
                fixture.Clock.StepWall(TimeSpan.FromMilliseconds(stepMs));
            }
        };

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1, 2], expectedOccupied: true),
            null,
            TestContext.Current.CancellationToken);

        Assert.True(stepped, "the first door never shut over a basket, so the wall clock was never stepped");
        Assert.Equal("COMPLETED", result.OverallOutcome);
        Assert.All(result.SlotResults, slot => Assert.Equal("COMPLETED", slot.Outcome));
        Assert.Equal(2, fixture.Io.UnlockCount);
    }

    /// <summary>
    /// A lock that never confirms the unlock fails its slot at a safe finish -- shut, locked, output reset -- and the wall
    /// clock stepped right after the pulse leaves it there (8005-agv-onboard-hmi#404): <c>IsSafeFinish</c> reads the
    /// reading the pulse left as fresh, so the journal records <c>SAFE_FINISH_REACHED</c> with no active unlock set.
    /// Reading its age off the wall clock left the slot in the active unlock set, a door to recover that never opened.
    /// </summary>
    /// <remarks>
    /// The executor gives up on the unlock after a second of real time with no new reading, so the steps are larger than
    /// that second: three seconds back, or forward past the 30-second maximum age this fixture is configured with.
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    [InlineData(-3_000)]
    [InlineData(31_000)]
    public async Task WallClockSteppedAfterThePulseStillFailsTheJammedSlotAtASafeFinish(int stepMs)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        SteppableClock clock = new();
        await using ScriptedFixture fixture = await ScriptedFixture.CreateAsync(token, clock: clock);
        fixture.Io.StampTimestamps = true;
        fixture.Io.CloseDoor(0, cargo: false);
        fixture.Io.JamLock(0);
        fixture.Io.Pulsed = _ => clock.StepWall(TimeSpan.FromMilliseconds(stepMs));

        WireToGateOperationExecutionResult result = await fixture.Executor.ExecuteAsync(
            CreateCommand(OperationType.Load, [1], expectedOccupied: true),
            null,
            token);

        AssertFailedAtASafeFinishOutsideBothSets(result, await fixture.Journal.ReadRecoveryStateAsync(token));
    }

    /// <summary>
    /// The machine's clocks, with the wall clock stepped by the test alone, as <c>Set-Date -Adjust</c> steps it
    /// (workspace#55).
    /// </summary>
    private sealed class SteppableClock : IClock
    {
        private long _offsetTicks;

        public DateTimeOffset Now => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));

        public long GetTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();

        public void StepWall(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
    }
}