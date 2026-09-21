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
/// <para>
/// <b>Why the restore is held.</b> The entry gates read a cached copy of the journal. A refusal never
/// emits the PREPARING progress that refreshes it for a run that opens a door, so unless the handler
/// refreshes it before sending the result, the copy still shows A settled and nothing armed when the
/// server's RECOVERY_REQUIRED arrives -- and the entry opens on A. The restore that readiness change starts
/// also refreshes the copy, a moment later, which hid the stale window in one run and not the next.
/// <see cref="HeldRestoreJournal"/> holds that restore's journal read until the assertions are done, so
/// the only refresh that can have happened is the handler's own.
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
        HeldRestoreJournal? held = null;
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
            wrapJournal: inner => held = new HeldRestoreJournal(inner),
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
        // Shut only once the executor is waiting on the operator: a door shut before it has seen the lock
        // release reads as a lock that never opened, and A ends UNKNOWN instead of completing.
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator
                && harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptA
                && io.UnlockCount == 1,
            "A's slot to be unlocked and waiting on the operator",
            token);
        io.CloseDoor(4, cargo: true);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).LastCompletedLoadOperationContext?.DemandId == DemandA
                && harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "A to complete and be recorded",
            token);
        Assert.False(harness.ViewModel.CanRequestLoadCompensation);
        held!.HoldRestores();
        try
        {
            await RefuseBAndPressCompensationAsync(harness, io, held, token);
        }
        finally
        {
            held.ReleaseRestores();
        }
    }

    private static async Task RefuseBAndPressCompensationAsync(
        Harness harness,
        FakeIoModuleClient io,
        HeldRestoreJournal held,
        CancellationToken token)
    {
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
        Assert.True(held.RestoreWasHeld, "no restore read was held, so the handler's own refresh was not isolated");
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

    /// <summary>
    /// Once armed, holds every cached journal read the business service's restore makes
    /// (<c>RestorePendingRecoveryOperationProjectionAsync</c>) until released; everything else passes.
    /// </summary>
    private sealed class HeldRestoreJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _held;

        public bool RestoreWasHeld => Volatile.Read(ref _held) == 1;

        public void HoldRestores() => Volatile.Write(ref _armed, 1);

        public void ReleaseRestores() => _release.TrySetResult();

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, cancellationToken);

        public async Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1
                && Environment.StackTrace.Contains("RestorePendingRecoveryOperationProjectionAsync", StringComparison.Ordinal))
            {
                Volatile.Write(ref _held, 1);
                await _release.Task.WaitAsync(cancellationToken);
            }

            return await inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);
        }

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

        public Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default) =>
            inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);

        public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
            WireToGateDurableMessage expected,
            WireToGateDurableMessage replacement,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceOutgoingForReplayAsync(expected, replacement, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByMessageIdAsync(messageId, cancellationToken);

        public Task MarkOutgoingAcknowledgedAsync(
            string messageId,
            string acceptedContentSha256,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAcknowledgedAsync(messageId, acceptedContentSha256, cancellationToken);

        public Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadUnacknowledgedOutgoingAsync(cancellationToken);

        public Task<IReadOnlyList<WireToGateAppliedJourneySnapshot>> ReadAppliedJourneySnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadAppliedJourneySnapshotsAsync(cancellationToken);

        public Task<WireToGateAppliedJourneySnapshot> SaveAppliedJourneySnapshotAsync(
            WireToGateAppliedJourneySnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            inner.SaveAppliedJourneySnapshotAsync(snapshot, cancellationToken);

        public Task<string> ComputeContentSha256Async(CancellationToken cancellationToken = default) =>
            inner.ComputeContentSha256Async(cancellationToken);

        public ValueTask DisposeAsync()
        {
            ReleaseRestores();
            return inner.DisposeAsync();
        }
    }
}
