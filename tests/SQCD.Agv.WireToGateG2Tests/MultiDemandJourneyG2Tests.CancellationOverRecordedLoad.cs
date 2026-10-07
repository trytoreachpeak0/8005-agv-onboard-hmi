using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// Reachability probe for 8005-agv-onboard-hmi#259, order (a): the load's COMPLETED result is recorded between the
    /// press reading the journal and the cancellation's prepare write.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ProbeResultRecordedBeforeThePrepareWrite()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource authorizationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationStopAsync(
            io,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1, 2];
                server.LoadCancellationAuthorizationHold = authorizationHeld.Task;
            },
            token);
        using ReleaseOnExit releaseAuthorization = new(() => authorizationHeld.TrySetResult());

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 2, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry",
            token);

        Task<bool> press = harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        await harness.WaitUntilAsync(
            () => ReceivedPayloads(harness, "LoadCancellationStartRequested").Length == 1,
            "the cancellation request to reach the server",
            token);
        io.CloseDoor(1, cargo: true);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token) is { UnsettledSlotOperationAttemptId: null } state
                && state.LastCompletedLoadOperationContext?.SlotOperationAttemptId == AttemptA,
            "the load's COMPLETED result to be recorded",
            token);
        WireToGateRecoveryState before = ReadJournal(harness, token);
        Dump("before-release", before);

        authorizationHeld.SetResult();
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is not null || press.IsCompleted,
            "the prepare write, or the press to end",
            token);
        WireToGateRecoveryState after = ReadJournal(harness, token);
        Dump("after-prepare", after);
        TestContext.Current.TestOutputHelper!.WriteLine($"probe press-completed={press.IsCompleted} unlocks={io.UnlockCount}");
    }

    /// <summary>
    /// Reachability probe for 8005-agv-onboard-hmi#259, order (b): the cancellation is prepared first and the load's
    /// COMPLETED result is recorded after it, while the cancellation's executor runs.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ProbeResultRecordedAfterThePrepareWrite()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource resultAckHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartCancellationStopAsync(
            io,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1, 2];
                server.OperationResultAckHold = resultAckHeld.Task;
            },
            token);
        using ReleaseOnExit releaseAck = new(() => resultAckHeld.TrySetResult());

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 2, token);
        io.CloseDoor(1, cargo: true);
        await harness.WaitUntilAsync(
            () => harness.Server.OperationResultsHeld == 1,
            "the load's COMPLETED result to be held unacknowledged",
            token);
        Dump("result-held", ReadJournal(harness, token));
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"probe entry-open={harness.Business.CanRequestLoadCancellation}");

        Task<bool> press = harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is not null || press.IsCompleted,
            "the prepare write, or the press to end",
            token);
        Dump("after-prepare", ReadJournal(harness, token));
        TestContext.Current.TestOutputHelper!.WriteLine($"probe press-completed={press.IsCompleted} unlocks={io.UnlockCount}");
        if (press.IsCompleted)
        {
            return;
        }

        await harness.WaitUntilAsync(() => io.UnlockCount == 3, "the cancellation's first pulse", token);
        resultAckHeld.SetResult();
        try
        {
            await harness.WaitUntilAsync(
                () => ReadJournal(harness, token).RecoveryVector is null,
                "the late acknowledgement to land",
                token);
        }
        catch (Xunit.Sdk.FailException)
        {
            TestContext.Current.TestOutputHelper!.WriteLine("probe the vector stayed on file");
        }
        Dump("after-late-ack", ReadJournal(harness, token));
        TestContext.Current.TestOutputHelper!.WriteLine($"probe unlocks={io.UnlockCount}");
    }

    private static void Dump(string label, WireToGateRecoveryState state) =>
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"probe {label}: unsettled={state.UnsettledSlotOperationAttemptId ?? "null"} "
            + $"checkpoint={state.ProvenRecoveryCheckpoint} active=[{string.Join(",", state.ActiveUnlockSlots)}] "
            + $"operationContext={state.OperationContext?.SlotOperationAttemptId ?? "null"} "
            + $"vector={state.RecoveryVector?.VectorType ?? "null"}/{state.RecoveryVector?.SlotOperationAttemptId ?? "-"} "
            + $"lastCompletedLoad={state.LastCompletedLoadOperationContext?.SlotOperationAttemptId ?? "null"} "
            + $"pendingResults={state.PendingResults.Count} pendingCancellation={state.PendingLoadCancellation?.CancellationId ?? "null"}");

    private static async Task<Harness> StartCancellationStopAsync(
        FakeIoModuleClient io,
        Action<FakeControlServer> configure,
        CancellationToken token)
    {
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
                configure(server);
            },
            token,
            io: io,
            messageTimeout: TimeSpan.FromSeconds(30));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null
                && harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the worklist on a ready session",
            token);
        return harness;
    }
}
