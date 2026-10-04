using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The two check purposes protocol 3.0.0 added besides <c>DEPARTURE</c>, answered since 8005-agv-onboard-hmi#219.
/// Until then both were refused with <c>ACTION_NOT_ALLOWED_IN_STATE</c> (8005-agv-onboard-hmi#214), which left a
/// vehicle held for an unproven door with no way out: the release needs one <c>SAFE</c> <c>HOLD_RELEASE</c> answer.
/// </summary>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// A move without a demand -- to a waiting point, to a charger -- is the same question about the vehicle as a
    /// departure, so it is answered from the same evaluation, and the result carries the purpose it answers.
    /// </summary>
    [Theory]
    [InlineData("SAFE")]
    [InlineData("UNKNOWN")]
    public async Task ANonBusinessMoveCheckIsAnsweredFromTheDepartureEvaluation(string expected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartWithBusinessStateAsync(io, [], token);
        if (expected == "UNKNOWN")
        {
            io.SetUnreadable(3);
        }

        JsonElement result = await AskAsync(harness, io, "NON_BUSINESS_MOVE", token);

        Assert.Equal(expected, result.GetProperty("outcome").GetString());
        Assert.Equal("NON_BUSINESS_MOVE", result.GetProperty("checkPurpose").GetString());
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.True(harness.Session.Current.Connected);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>ANSWER_HOLD_RELEASE_CHECK_WITHOUT_DEMAND_OR_LEG</c>: a check with no demand, leg or station, about the slots
    /// the server holds for an unproven door. <c>SAFE</c> only when every held slot reads EMPTY, LOCKED and RESET and
    /// the vehicle could depart. The departure verdict is never the answer by itself: with a basket in the held slot
    /// every door is still locked and reset and the departure evaluation is safe, and the answer is
    /// <c>UNSAFE</c>. A held slot that cannot be read is <c>UNKNOWN</c>, and so is a check about a hold this vehicle
    /// has no record of -- it cannot say which slots are meant. With every held slot proven, the answer is still
    /// <c>UNSAFE</c> when another door is left unlocked (the departure evaluation is not safe) or a forced recovery
    /// left slots physically unknown (PR #248 review: neither condition was pinned).
    /// </summary>
    [Theory]
    [InlineData("proven", "SAFE")]
    [InlineData("basket-in-held-slot", "UNSAFE")]
    [InlineData("held-slot-unreadable", "UNKNOWN")]
    [InlineData("no-hold-on-record", "UNKNOWN")]
    [InlineData("other-door-unlocked", "UNSAFE")]
    [InlineData("forced-isolation-standing", "UNSAFE")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task AHoldReleaseCheckIsSafeOnlyWhenEveryHeldSlotIsProvenEmptyLockedAndReset(
        string condition,
        string expected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        IReadOnlyList<int> held = condition == "no-hold-on-record" ? [] : [1, 3];
        await using Harness harness = await StartWithBusinessStateAsync(io, held, token);
        switch (condition)
        {
            case "basket-in-held-slot":
                io.SetCargoPresent(2, true);
                break;
            case "held-slot-unreadable":
                io.SetUnreadable(0);
                break;
            case "other-door-unlocked":
                io.LeaveDoorUnlocked(4);
                break;
            case "forced-isolation-standing":
                // Slot 6, outside the held set: the held slots themselves are proven, and the vehicle is still not
                // releasable while a forced recovery's slots stay physically unknown.
                await harness.Session.Journal.UpdateRecoveryStateAsync(
                    current => current with
                    {
                        ForcedIsolation = new WireToGateForcedIsolation(
                            "99999999-9999-4999-8999-999999999999",
                            "abababab-abab-4bab-8bab-abababababab",
                            [6])
                    },
                    token);
                break;
        }

        JsonElement result = await AskAsync(harness, io, "HOLD_RELEASE", token);

        Assert.Equal(expected, result.GetProperty("outcome").GetString());
        Assert.Equal("HOLD_RELEASE", result.GetProperty("checkPurpose").GetString());
        if (condition is "basket-in-held-slot" or "forced-isolation-standing")
        {
            // The departure evaluation alone would have said yes.
            Assert.True(result.GetProperty("safety").GetProperty("departureSafe").GetBoolean());
        }
        else if (condition == "other-door-unlocked")
        {
            Assert.False(result.GetProperty("safety").GetProperty("departureSafe").GetBoolean());
        }

        Assert.Equal(0, io.UnlockCount);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.True(harness.Session.Current.Connected);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// Who may ask for the repair release (PR #248 review): the hold is on the screen, but this vehicle has the recovery
    /// switch off and no administrator proof. The entry stays shut on the view model and on the business service, and a
    /// press made anyway -- a caller that never asked the entry -- sends nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task TheRepairReleaseIsShutToAVehicleWithNoRecoveryAdministrator()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartWithBusinessStateAsync(io, [1, 3], token);
        await harness.WaitUntilAsync(() => harness.ViewModel.HasDoorHold, "the hold to be shown", token);

        Assert.Equal([1, 3], harness.Business.DoorHeldSlots);
        Assert.False(harness.Business.CanRequestHardwareRepairRelease);
        Assert.False(harness.ViewModel.CanRequestHardwareRepairRelease);
        Assert.False(await harness.Business.RequestHardwareRepairReleaseAsync("无凭据的按下。", token));
        Assert.DoesNotContain(
            harness.Server.Received,
            item => item.MessageType is "ExceptionRecoverySessionRequested" or "RecoveryActionSubmitted");
        Assert.Null((await harness.Session.Journal.ReadRecoveryStateAsync(token)).RepairRelease);
    }

    private static async Task<Harness> StartWithBusinessStateAsync(
        FakeIoModuleClient io,
        IReadOnlyList<int> heldSlots,
        CancellationToken token)
    {
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = new
                    {
                        vehicleBusinessStateRevision = 1,
                        readiness = heldSlots.Count == 0 ? "READY" : "RECOVERY_REQUIRED",
                        activePurpose = (string?)null,
                        manualChargingHold = false,
                        batteryState = "SUFFICIENT",
                        chargingCycleState = "NOT_CHARGING",
                        loadingPhase = (object?)null,
                        blockingFacts = heldSlots
                            .Select(slot => new
                            {
                                reasonCode = "SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY",
                                subjectType = "SLOT",
                                subjectId = slot.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            })
                            .ToArray(),
                        observedAt = DateTimeOffset.UtcNow
                    },
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
            },
            token,
            io: io);
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.VehicleBusinessState is not null
                && harness.Session.Current.SafetyStateVersion > 0,
            "the business state and an accepted safety state",
            token);
        return harness;
    }

    private static async Task<JsonElement> AskAsync(
        Harness harness,
        FakeIoModuleClient io,
        string purpose,
        CancellationToken token)
    {
        io.PublishSnapshot();
        await Task.Delay(300, token);
        bool nonBusinessMove = purpose == "NON_BUSINESS_MOVE";
        string checkId = Guid.NewGuid().ToString("D");
        await harness.Server.SendCommandAsync(
            "PreDepartureSafetyCheck",
            Guid.NewGuid().ToString("D"),
            new
            {
                preDepartureSafetyCheckId = checkId,
                checkPurpose = purpose,
                demandId = (string?)null,
                movementLegId = nonBusinessMove ? "22222222-2222-4222-8222-000000000009" : null,
                expectedSafetyStateVersion = harness.Session.Current.SafetyStateVersion,
                targetStationId = nonBusinessMove ? "ST-WAIT" : null
            });
        JsonElement result = await WaitForPayloadAsync(harness, "PreDepartureSafetyCheckResult", token);
        Assert.Equal(checkId, result.GetProperty("preDepartureSafetyCheckId").GetString());
        return result;
    }
}
