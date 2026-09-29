using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The journal state a real forced mechanical recovery leaves once the control server has acknowledged
/// its <c>MECHANICALLY_ISOLATED</c> result, for the hardware-recovery tests that start from there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why seeded and not driven (8005-agv-onboard-hmi#214).</b> Protocol 3.0.0 requires that result to
/// carry a cargo handoff record whenever the forced recovery is on a demand -- which every forced recovery
/// this build executes is -- and this build has no screen to take one, so the confirmation fails closed
/// and no result goes out. The acknowledged isolation the downstream tests need is unreachable on the
/// batch branch until 8005-agv-onboard-hmi#216 adds that screen; that ticket turns these tests back into
/// ones driven through the real acknowledgement.
/// </para>
/// <para>
/// <b>Captured, not written by hand.</b> The value below is what
/// <c>RecoveryVectorHarness.ReadRecoveryStateAsync</c> returned, serialized with the journal's own web
/// defaults, right after <c>IsolateByForcedRecoveryAsync</c> ran over
/// <c>StartAsync(token, cargoInTargetSlots: true)</c> on <c>w2g/batch-p3/v3@59dd5452</c> -- the batch
/// branch before this ticket, where the result still went out and was acknowledged. The captured
/// <c>pendingResults</c> entry is the interrupted load the harness settles UNKNOWN on start; it is part of
/// the real state and is kept; its <c>contentSha256</c> differs from run to run (the settled result carries
/// an observation time), so a capture on another day will not match this text byte for byte -- the value
/// here is one real run's, not a constant of the product. That the capture was taken from the real path is
/// recorded here and was reproduced in review on <c>59dd5452</c>; no test in this assembly can prove it.
/// <see cref="TheSeedReachesTheJournalWithoutLoss"/> checks only the harness side: what the harness writes
/// is what the journal reads back.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    internal const string AcknowledgedForcedIsolationState = """
        {
          "unsettledSlotOperationAttemptId": null,
          "provenRecoveryCheckpoint": 4,
          "activeUnlockSlots": [],
          "forcedRecoveryGeneration": 1,
          "pendingResults": [
            {
              "messageType": "OperationResult",
              "messageId": "33333333-3333-4333-8333-333333333333",
              "businessId": "33333333-3333-4333-8333-333333333333",
              "contentSha256": "a72553eb30829df1a407cb7c461898c0b7141f36f6db5c513171ec54d0718a03"
            }
          ],
          "operationContext": null,
          "completedSlots": [],
          "slotResults": [],
          "exceptionRecoverySessionId": null,
          "recoveryActionId": null,
          "recoverySessionRequestId": null,
          "recoveryActionRequestId": null,
          "recoveryReason": null,
          "recoveryOperatorId": null,
          "recoveryOperatorVerifiedAt": null,
          "recoveryVector": null,
          "recoveryResultObservedAt": null,
          "lastCompletedLoadOperationContext": null,
          "pendingLoadCancellation": null,
          "forcedIsolation": {
            "exceptionRecoverySessionId": "77777777-7777-4777-8777-777777777777",
            "recoveryActionId": "2af5c209-93df-085b-9cf0-5722e37ed751",
            "physicallyUnknownSlots": [
              1,
              2
            ],
            "pendingRecord": null
          }
        }
        """;

    private static readonly JsonSerializerOptions SeedSerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The seed reaches the journal without loss: what the harness reads back after starting over it
    /// serializes to the seed text, field for field.
    /// </summary>
    /// <remarks>
    /// This is a check on the seeding, not on the seed: change a field of the seed and both sides move
    /// together. What makes the seed right is where it came from (see the class remarks).
    /// </remarks>
    [Fact]
    public async Task TheSeedReachesTheJournalWithoutLoss()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true,
            seededRecoveryState: AcknowledgedForcedIsolationState);

        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);

        using JsonDocument expected = JsonDocument.Parse(AcknowledgedForcedIsolationState);
        using JsonDocument actual = JsonDocument.Parse(JsonSerializer.Serialize(state, SeedSerializerOptions));
        Assert.Equal(
            JsonSerializer.Serialize(expected.RootElement),
            JsonSerializer.Serialize(actual.RootElement));
        Assert.Equal([1, 2], harness.Business.PhysicallyUnknownSlots);
    }
}
