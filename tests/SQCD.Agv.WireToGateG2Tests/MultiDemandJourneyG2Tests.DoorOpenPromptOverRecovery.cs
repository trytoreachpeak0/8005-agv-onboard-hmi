using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The door-open banner stays off a recovery vector's doors (<c>trytoreachpeak0/8005-agv-onboard-hmi#354</c> review M1):
/// a compensation that opens slot 1 and waits for the operator, its door read open, never puts the banner up -- while
/// the ordinary load before it, on the same screen and the same door, does.
/// </summary>
/// <remarks>
/// <para>
/// Through the product path: the business service publishes the vector's progress, the view model takes it with the IO
/// reading, and only the screen is asserted. The control half -- the ordinary load's banner on slot 1 -- is what makes a
/// "no banner" here mean the exclusion and not a harness that never shows one.
/// </para>
/// <para>
/// A load correction goes through the same <c>PublishRecoveryVectorOperation</c> as the compensation; this harness has no
/// path that holds a correction's door open, so its form (a Load-typed vector) is pinned in
/// <c>DoorOpenPromptViewModelTests.NoBannerOverARecoveryVectorsOpenDoor</c>. No <c>IntegrationSlice</c> or
/// <c>ProtocolVector</c> trait: it proves a screen rule, not a frozen vector.
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    [Fact]
    public async Task ACompensationsOpenDoorPutsUpNoBannerWhereTheOrdinaryLoadsDid()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTakeOverStopAsync(
            io,
            server =>
            {
                server.SendLoadCompensationCommandOnRequest = true;
                server.SendRecoveryRequiredReadinessAfterOperationResultAck = true;
            },
            token);

        // The control: the ordinary load's door 1 stands open and the banner says so.
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.PublishSnapshot();
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.HasDoorOpenPrompt
                && harness.ViewModel.DoorOpenPromptHeadline.StartsWith("1号仓门开着", StringComparison.Ordinal)),
            "the ordinary load's banner over slot 1",
            token);
        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 2, token);
        io.CloseDoor(1, cargo: true);
        io.PublishSnapshot();
        await harness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && harness.Business.CanRequestLoadCompensation,
            "the compensation entry over the recorded load",
            token);

        int unlocksBefore = io.UnlockCount;
        Assert.True(await harness.Business.RequestLoadCompensationAsync("现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await harness.WaitUntilAsync(
            () => io.UnlockCount > unlocksBefore || ReceivedPayloads(harness, "LoadCompensationResult").Length > 0,
            "the compensation to open slot 1",
            token);
        io.PublishSnapshot();
        // The vector's own prompt reached the slot card, with the door read open: everything the banner would need.
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.Lockers[0] is { TargetText: "请取出货物并关门", DoorText: "仓门：未锁" }),
            "the compensation waiting on the operator over door 1, read open",
            token);
        await AssertWhileAsync(
            DisplaySettleWindow,
            () => Assert.False(
                harness.OnUi(() => harness.ViewModel.HasDoorOpenPrompt),
                $"the banner went up over the compensation: {harness.OnUi(() => harness.ViewModel.DoorOpenPromptHeadline)}"),
            token);

        io.SetUnreadable(0);
        JsonElement compensation = await WaitForPayloadAsync(harness, "LoadCompensationResult", token);
        Assert.Equal("UNKNOWN", compensation.GetProperty("overallOutcome").GetString());
        Assert.Empty(harness.UiErrors);
    }
}
