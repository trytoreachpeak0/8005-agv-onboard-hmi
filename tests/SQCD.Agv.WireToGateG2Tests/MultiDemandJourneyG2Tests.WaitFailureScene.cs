using System.Diagnostics;
using Xunit;
using Xunit.Sdk;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// What a wait of this harness says when it runs out (8005-agv-onboard-hmi#383).
/// </summary>
/// <remarks>
/// Twice on the new CI guest a wait here ran its five seconds out with nothing given back for process stalls: the test
/// thread kept looking and the fact it waited for never moved. Reproduced once on one core, the vehicle had sent
/// <c>WAITING_OPERATOR</c> and was waiting for its <c>DurableAck</c>, under the 30-second message timeout of the take-over
/// stop. The failure said none of that -- only "timed out" and an empty list of UI errors -- so each one had to be chased
/// again before anyone could say whether the double, the harness or the vehicle had stopped.
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// The progress acks of a fault cargo handoff are held back by the double, so the handoff's result never comes. The
    /// wait for it still fails in its own time, and its message names the message the vehicle is stuck on, says the
    /// double never acknowledged it, and carries the vehicle's log and the wire as the double saw them.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AWaitThatRunsOutNamesTheMessageTheVehicleIsStuckOn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptA;
                server.RecoveryVectorSlotOperationAttemptId = AttemptA;
                server.RecoverySessionScopeByDemand = TwoLoadScopes();
            },
            token);
        await LoadAndRecordAsync(harness, io, DemandA, AttemptA, slot: 1, token);
        await LoadAndRecordAsync(harness, io, DemandB, AttemptB, slot: 5, token);
        await HoldTheSessionForRecoveryAsync(harness, token);
        await WaitForChoicesAsync(harness, ["子批 SUBLOT-A / 1号仓", "子批 SUBLOT-B / 5号仓"], token);

        TaskCompletionSource acksHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.OperationProgressAckHold = acksHeld.Task;
        try
        {
            harness.OnUi(() => harness.ViewModel.SelectedRecoveryDemandChoice =
                harness.ViewModel.RecoveryDemandChoices.Single(row => row.DemandId == DemandA));
            Assert.True(await harness.ViewModel.RequestFaultCargoHandoffAsync(token));

            Stopwatch elapsed = Stopwatch.StartNew();
            FailException failure = await Assert.ThrowsAsync<FailException>(() => harness.WaitUntilAsync(
                () => harness.Server.ReceivedEnvelopes.Any(item => item.MessageType == "FaultCargoRecoveryResult"),
                "the control server to receive FaultCargoRecoveryResult",
                token));
            elapsed.Stop();

            // In its own time: the five seconds, and at most what a stall gives back on top.
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(5) + StallAwareDeadline.StallBudget + TimeSpan.FromSeconds(1),
                $"the wait took {elapsed.Elapsed}");
            // The handoff's first progress, the one whose ack the vehicle waits for before it goes on.
            var received = harness.Server.ReceivedEnvelopes;
            int submitted = received.Select(item => item.MessageType).ToList().LastIndexOf("RecoveryActionSubmitted");
            string progressId = received.Skip(submitted + 1).First(item => item.MessageType == "OperationProgress").MessageId;
            Assert.Contains("Timed out after 5s waiting for: the control server to receive FaultCargoRecoveryResult", failure.Message);
            Assert.Contains(
                $"Stuck on: OperationProgress {progressId} (PREPARING), which this double has not acknowledged",
                failure.Message);
            Assert.Contains("Vehicle log, last entries:", failure.Message);
            Assert.Contains("SessionReadiness", failure.Message);
            Assert.Contains($"OperationProgress {progressId} (PREPARING) sent ", failure.Message);
        }
        finally
        {
            acksHeld.TrySetResult();
        }
    }
}
