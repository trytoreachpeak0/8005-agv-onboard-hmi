using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A load refused because its slot already holds a basket leaves the operator a way to get that basket
/// out (8005-agv-onboard-hmi#172).
/// </summary>
/// <remarks>
/// <para>
/// <b>What went wrong before.</b> The executor refused the load before any IO and journaled nothing, so
/// the attempt the server had just put into recovery existed nowhere on the vehicle. The compensation
/// entry looks its subject up in the journal, found nothing armed, and fell back to the last load that
/// completed. Nothing else at the stop took the basket out.
/// </para>
/// <para>
/// <b>Why there is a completed load first.</b> Without one, "the entry did not open" and "the entry
/// opened on the wrong demand" cannot be told apart, and the second is the one that looks right: a
/// lit button. Demand A completes on slot 5 first, so the fallback has a real wrong answer to pick, and
/// the assertions name demand B's attempt -- on screen (no fallback target shown) and on the wire (the
/// session request carries B and slot 1).
/// </para>
/// <para>
/// <b>Nothing is seeded.</b> The journal entry, the RECOVERY_REQUIRED readiness and the recovery session
/// all come from the product path: the executor's refusal, the double's verdict on a FAILED result (the
/// real server's <c>DeterminateLoadFailure.Judge</c> gives NOT_STARTED-only failures no terminal state),
/// and the vehicle's own session request on the press. The double answers that request by protocol; it
/// does not decide which demand it is about.
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    private const string ConflictProofVariable = "W2G_G2_MULTI_DEMAND_PROOF";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ALoadRefusedOverAnOccupiedSlotOffersCompensationForThatDemandNotTheLastCompletedOne()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Environment.SetEnvironmentVariable(ConflictProofVariable, "g2-multi-demand-proof");
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
                server.RespondToRecoveryRequests = true;
                server.RecoverySlotOperationAttemptId = AttemptB;
            },
            token,
            io: io,
            recoveryOptions: new WireToGateRecoveryOptions(
                ResumeAfterRepairEnabled: true,
                ConflictProofVariable,
                "MAINTENANCE_ADMINISTRATOR",
                "CONFIGURED_PROOF"));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null,
            "the worklist",
            token);

        // Demand A loads slot 5 and completes: the last completed load, and the fallback's answer.
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [5]);
        await harness.WaitUntilAsync(() => io.UnlockCount == 1, "A's slot to be unlocked", token);
        io.CloseDoor(4, cargo: true);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LastCompletedLoadOperationContext?.DemandId == DemandA
                && harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "A to complete and be recorded",
            token);
        Assert.False(harness.ViewModel.CanRequestLoadCompensation);

        // Demand B's slot 1 already holds a basket.
        io.CloseDoor(0, cargo: true);
        await SendSlotCommandAsync(harness, DemandB, AttemptB, [1]);

        // The report is the refusal's, as it was before #172.
        JsonElement result = await WaitForOperationResultAsync(harness, AttemptB, token);
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        JsonElement slot = Assert.Single(result.GetProperty("slotResults").EnumerateArray());
        Assert.Equal("NOT_STARTED", slot.GetProperty("outcome").GetString());
        Assert.Equal("OCCUPIED", slot.GetProperty("finalPhysicalState").GetString());
        Assert.Equal(
            ["SLOT_OPERATION_CONFLICT"],
            slot.GetProperty("reasonCodes").EnumerateArray().Select(code => code.GetString()));
        Assert.Equal(1, io.UnlockCount);

        // On screen: the entry is open, and it is about B -- no "目标：子批 SUBLOT-A" beside it.
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && harness.ViewModel.CanRequestLoadCompensation,
            "the compensation entry to open over B's refused load",
            token);
        Assert.Equal(string.Empty, harness.ViewModel.RecoveryFallbackTargetText);
        Assert.False(harness.ViewModel.HasRecoveryFallbackTarget);

        // On the wire: the press asks for a session over B's demand and slot, nothing else.
        Assert.True(await harness.ViewModel.RequestLoadCompensationAsync(token));
        JsonElement request = await WaitForPayloadAsync(harness, "ExceptionRecoverySessionRequested", token);
        Assert.Equal(DemandB, request.GetProperty("demandId").GetString());
        Assert.Equal([1], request.GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Empty(harness.UiErrors);
    }

    private static WireToGateRecoveryState ReadJournal(Harness harness, CancellationToken token) =>
        harness.Session.Journal.ReadRecoveryStateAsync(token).GetAwaiter().GetResult();

    private static async Task<JsonElement> WaitForOperationResultAsync(
        Harness harness,
        string attemptId,
        CancellationToken token)
    {
        JsonElement? found = null;
        await harness.WaitUntilAsync(
            () =>
            {
                found = ReceivedPayloads(harness, "OperationResult").FirstOrDefault(
                    payload => payload.GetProperty("slotOperationAttemptId").GetString() == attemptId);
                return found is { ValueKind: JsonValueKind.Object };
            },
            $"the OperationResult of {attemptId}",
            token);
        return found!.Value;
    }

    private static JsonElement[] ReceivedPayloads(Harness harness, string messageType) =>
    [
        .. harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == messageType)
            .Select(envelope =>
            {
                using JsonDocument document = JsonDocument.Parse(envelope.WireLine);
                return document.RootElement.GetProperty("payload").Clone();
            })
    ];
}
