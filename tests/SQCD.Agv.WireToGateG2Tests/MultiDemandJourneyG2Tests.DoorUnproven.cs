using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The onboard half of <c>CV-LOAD-CANCELLATION-EMPTY-DOOR-UNPROVEN</c> (CP-0009, REQ-0357, REQ-0364,
/// 8005-agv-onboard-hmi#219), through the real session and business services and the main view model.
/// </summary>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// The load has slot 1 open and nothing in it when the operator cancels; the open door is handed over to the
    /// cancellation and is never locked again. The light curtain proves the slot empty, so the cancellation settles
    /// <c>ALL_EMPTY_DOOR_UNPROVEN</c> with the slot as read -- no further door opened -- and once the server holds the
    /// vehicle, the screen says so in the user's words and keeps saying it (<c>DISPLAY_REPAIR_REQUIRED_NOTICE</c>).
    /// </summary>
    /// <remarks>
    /// Red before this ticket: the wait for the handed-over door to lock timed out and the slot was UNKNOWN, so the
    /// cancellation reported <c>UNKNOWN</c> and the demand stayed blocked.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-EMPTY-DOOR-UNPROVEN")]
    public async Task ACancelledLoadWhoseOpenEmptyDoorNeverLocksSettlesAllEmptyDoorUnprovenAndTheScreenShowsTheHold()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(
            io,
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
            });
        Assert.False(harness.ViewModel.HasDoorHold);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry",
            token);

        await harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        await harness.WaitUntilAsync(
            () => ReceivedPayloads(harness, "LoadCancellationResult").Length > 0,
            "the cancellation result",
            token);

        JsonElement result = Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Equal("ALL_EMPTY_DOOR_UNPROVEN", result.GetProperty("overallOutcome").GetString());
        JsonElement slot = Assert.Single(result.GetProperty("slotResults").EnumerateArray());
        Assert.Equal(1, slot.GetProperty("slotNo").GetInt32());
        Assert.Equal("EMPTY", slot.GetProperty("finalPhysicalState").GetString());
        Assert.Equal("UNLOCKED", slot.GetProperty("lockState").GetString());
        Assert.Equal("RESET", slot.GetProperty("unlockOutputState").GetString());
        Assert.Equal(["SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY"], ReasonCodes(slot));
        Assert.Equal(1, io.UnlockCount);

        await harness.WaitUntilAsync(
            () => harness.ViewModel.HasDoorHold,
            "the server's hold to be shown",
            token);
        Assert.Equal("1号仓：仓已确认无货，门锁未锁闭，本车需维修后才能继续。", harness.ViewModel.DoorHoldNotice);
        Assert.Equal("1", harness.ViewModel.DoorHoldSlots);
        // Settled after the result's DurableAck, as on the compensation path: waited for, never assumed (PR #248
        // review, must-fix 2).
        WireToGateRecoveryState settled = WireToGateRecoveryState.Empty;
        await harness.WaitUntilAsync(
            () =>
            {
                settled = ReadJournal(harness, token);
                return settled.RecoveryVector is null
                    && harness.Events.Any(item => item.Kind == "RECOVERY_VECTOR_DOOR_UNPROVEN");
            },
            "the business side to be settled and the operator told after the result's DurableAck",
            token);
        Assert.Contains(
            harness.Events,
            item => item.Kind == "RECOVERY_VECTOR_DOOR_UNPROVEN"
                && item.Message == "1号仓：仓已确认无货，门锁未锁闭，本车需维修后才能继续。");
        Assert.Null(settled.UnsettledSlotOperationAttemptId);
        // Still one pulse: nothing was opened after the unproven door, and nothing is pulsed to "prove" it.
        Assert.Equal(1, io.UnlockCount);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");

        // The hold lifted, the line goes.
        await harness.Server.PublishDoorHoldAsync([]);
        await harness.WaitUntilAsync(
            () => !harness.ViewModel.HasDoorHold,
            "the lifted hold to clear the line",
            token);
        Assert.Equal(string.Empty, harness.ViewModel.DoorHoldNotice);
        Assert.Empty(harness.UiErrors);
    }
}
