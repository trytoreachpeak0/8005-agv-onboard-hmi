using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// How the queued second command meets the first one's door when that door is still in doubt (onboard-hmi#267).
/// </summary>
/// <remarks>
/// <para>
/// The display and recovery-projection tests of onboard-hmi#146, #152 and #156 queue B behind A, and A ends
/// <c>UNKNOWN</c> because its lock feedback goes unreadable while its door stands open: A's slot 1 is left in the active
/// unlock set. Until onboard-hmi#267 the queued B took the executor at once and journaled its own <c>Prepared</c> over that
/// record. Now a new operation is refused while the journal names a door no fresh reading proves shut, so B's first copy
/// is answered with <c>SlotOperationCommandRejected</c> and A's record stays.
/// </para>
/// <para>
/// The real server only acknowledges such a refusal and sends the same command again each round while the session is
/// ready. These tests do what the operator and the server would: the door is shut, and B is sent again. From B's second
/// copy on, each test runs as it did -- B takes the display, its <c>Prepared</c> write is where
/// <c>RestoreWindowJournal</c> holds it, and A's restore reads the journal while A is still the unsettled attempt.
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// B's first copy is refused over A's door in doubt, before any journal write, and A's record of that door is still on
    /// file. Then the operator shuts A's door and the server sends B again.
    /// </summary>
    private static async Task RefuseBOverAsDoorThenResendOnceShutAsync(
        Harness harness,
        FakeIoModuleClient io,
        CancellationToken token)
    {
        await harness.WaitUntilAsync(
            () => RefusalsOfB(harness) == 1,
            "B's first copy to be refused over A's door in doubt",
            token);
        WireToGateRecoveryState onFile = await harness.Session.Journal.ReadRecoveryStateAsync(token);
        Assert.Equal(AttemptA, onFile.UnsettledSlotOperationAttemptId);
        Assert.Contains(1, onFile.ActiveUnlockSlots);

        io.CloseDoor(0, cargo: true);
        await SendSlotCommandAsync(harness, DemandB, AttemptB, [5]);
    }

    /// <summary>The only refusal on the wire is the one of B's first copy.</summary>
    private static void AssertOnlyBsFirstCopyRefused(Harness harness)
    {
        Assert.Single(harness.Server.ReceivedEnvelopes, item => item.MessageType == "SlotOperationCommandRejected");
        Assert.Equal(1, RefusalsOfB(harness));
    }

    private static int RefusalsOfB(Harness harness) =>
        harness.Server.ReceivedEnvelopes.Count(item => item.MessageType == "SlotOperationCommandRejected"
            && item.WireLine.Contains(AttemptB, StringComparison.Ordinal));
}
