using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// A wait of a clear that runs out says which wait it was, read off the slot as it is then (8005-agv-onboard-hmi#392 item 3,
/// as 8005-agv-onboard-hmi#385 did for the slot executor): every timeout used to be <c>ACTION_NOT_ALLOWED_IN_STATE</c>, and
/// 10-21 agv03 @160's slot 5 was reported UNKNOWN／OCCUPIED／<c>ACTION_NOT_ALLOWED_IN_STATE</c>, which read as "a state that
/// does not allow it".
/// </summary>
/// <remarks>
/// The lock that would not release after the pulse, with its output back, is <c>SLOT_INOPERABLE</c>; an output that never
/// fell back is <c>UNLOCK_OUTPUT_NOT_RESET</c>; a slot that could not be read is <c>SLOT_STATE_UNKNOWN</c>; a door left open
/// is <c>LOCK_NOT_CLOSED</c>. A door shut and locked over the basket the operator did not take out has no code of its own
/// that the recovery results may carry (<c>OPERATOR_TIMEOUT</c> is registered for <c>OperationResult</c> only), and is
/// <see cref="WireToGateRecoveryVectorExecutor.OperatorDidNotClearReason"/>.
/// </remarks>
public sealed partial class WireToGateRecoveryVectorExecutorTests
{
    [Theory]
    [InlineData("lock-never-releases", WireToGateRecoveryVectorTypes.LoadCompensation, "SLOT_INOPERABLE")]
    [InlineData("output-stuck", WireToGateRecoveryVectorTypes.LoadCompensation, "UNLOCK_OUTPUT_NOT_RESET")]
    [InlineData("light-curtain-lost", WireToGateRecoveryVectorTypes.LoadCompensation, "SLOT_STATE_UNKNOWN")]
    [InlineData("lock-never-closes", WireToGateRecoveryVectorTypes.FaultCargoHandoff, "LOCK_NOT_CLOSED")]
    [InlineData("never-emptied", WireToGateRecoveryVectorTypes.LoadCompensation, WireToGateRecoveryVectorExecutor.OperatorDidNotClearReason)]
    public async Task ATimeoutSaysWhichWaitRanOut(string hardware, string vectorType, string expectedReason)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestFixture fixture = await TestFixture.CreateAsync([true], cancellationToken: token);
        HashSet<int> slots = hardware switch
        {
            "lock-never-releases" => fixture.Io.LockNeverReleasesSlots,
            "output-stuck" => fixture.Io.StuckOutputSlots,
            "light-curtain-lost" => fixture.Io.LightCurtainLostSlots,
            "lock-never-closes" => fixture.Io.LockNeverClosesSlots,
            _ => fixture.Io.NeverEmptiedSlots
        };
        slots.Add(1);

        WireToGateRecoveryVectorExecutionResult result = await fixture.Executor.ExecuteClearAsync(
            CreateContext(vectorType, "39239239-2392-4392-8392-392392392101", [1]),
            null,
            token);

        Assert.Equal("UNKNOWN", result.OverallOutcome);
        WireToGateSlotExecutionResult slot = Assert.Single(result.SlotResults);
        Assert.Equal("UNKNOWN", slot.Outcome);
        Assert.Equal([expectedReason], slot.ReasonCodes);
    }
}
