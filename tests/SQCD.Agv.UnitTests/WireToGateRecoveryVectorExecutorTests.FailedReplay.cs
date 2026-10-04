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
    /// The business service sends both answers under the same deduplication key and messageId. The outbox refuses a
    /// second, different line under that key (<c>BUSINESS_ID_CONTENT_CONFLICT</c>); had the first never reached the
    /// outbox, the server would get UNKNOWN for a vector that had told the vehicle FAILED. Before the fix both paths
    /// checkpointed <c>ACTIVE_UNLOCK_SET</c>, which the replay reads as UNKNOWN.
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

        WireToGateRecoveryVectorExecutionResult first = await fixture.Executor.ExecuteClearAsync(
            context,
            null,
            token);
        Assert.Equal("FAILED", first.OverallOutcome);
        Assert.Equal([1], fixture.Io.Pulses.Select(pulse => pulse.Slot));
        Assert.Empty((await fixture.Journal.ReadRecoveryStateAsync(token)).ActiveUnlockSlots);

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        WireToGateRecoveryVectorExecutionResult again = replay == "execute"
            ? await fixture.Executor.ExecuteClearAsync(context, null, token)
            : await fixture.Executor.SettleWithoutUnlockAsync(context, token);

        Assert.Equal(first.OverallOutcome, again.OverallOutcome);
        Assert.Equal(first.JournalCheckpoint, again.JournalCheckpoint);
        Assert.Equal(first.ObservedAt, again.ObservedAt);
        Assert.Equal(first.SlotResults, again.SlotResults, SlotResultComparer.Instance);
        Assert.Equal([1], fixture.Io.Pulses.Select(pulse => pulse.Slot));
    }
}
