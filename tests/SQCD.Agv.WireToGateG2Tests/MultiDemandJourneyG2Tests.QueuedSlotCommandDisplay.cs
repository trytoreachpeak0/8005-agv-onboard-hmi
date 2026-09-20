using SQCD.Agv.Application;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The current-operation display of a stop that carries two slot commands
/// (batch 7-146, <c>trytoreachpeak0/8005-agv-onboard-hmi#146</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What these guard.</b> Batch 7-13 proved that a second slot command queues behind the first
/// without being refused, without counting its queued time against its own clock, and without its
/// side mark leaking onto the other demand's row. What it did not assert is the display: the
/// business service announced <c>Preparing</c> for the second command <i>before</i> it queued on the
/// single executor, so the first demand's door stood open on slot 1 while the screen read
/// "准备执行装货：5号仓" and highlighted slot 5 as the target. An operator following the highlight
/// walks to the wrong row.
/// </para>
/// <para>
/// <b>The display has two directions to get wrong, and both are asserted.</b> Forwards: a queued
/// command must not take the display before it has the executor. Backwards: a settled command's
/// last events -- the result acknowledgement, which carries a snapshot of its own -- must not take
/// the display back off the command that has since started. Each test therefore holds its assertion
/// over a stretch of time rather than reading once.
/// </para>
/// <para>
/// <b>Why they wait on a second fact.</b> Nothing observable says "the queued command has reached
/// the gate", so a fixed delay followed by one read would pass on a version that simply had not got
/// there yet. Instead the queued period is asserted repeatedly while it lasts, and the queue itself
/// is proven afterwards by the second command taking over and pulsing its own slot.
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>How long an assertion about the display is held before it is believed.</summary>
    private static readonly TimeSpan DisplaySettleWindow = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// A's door is open and waiting for the operator when B's command arrives. For as long as B
    /// queues, the current operation and the target highlight stay on A's slot; B is not refused;
    /// nothing is pulsed a second time. A settles, and only then does B become the current operation
    /// and keep the highlight.
    /// </summary>
    [Fact]
    public async Task AQueuedSlotCommandLeavesTheCurrentOperationAndTheTargetHighlightOnTheRunningOne()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTwoDemandStopAsync(io, token);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator
                && harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptA
                && io.UnlockCount == 1,
            "A's slot to be unlocked and waiting on the operator",
            token);

        await SendSlotCommandAsync(harness, DemandB, AttemptB, [5]);
        await WaitForBothCommandsToReachTheVehicleAsync(harness, token);

        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                WireToGateHmiOperationSnapshot current = harness.Business.CurrentOperationSnapshot!;
                Assert.Equal(AttemptA, current.SlotOperationAttemptId);
                Assert.Equal([1], current.Slots);
                Assert.Equal(WireToGateHmiOperationStage.WaitingOperator, current.Stage);
                AssertHighlighted(harness, 1);
                Assert.Equal(1, io.UnlockCount);
            },
            token);

        AssertNotRefused(harness);

        io.CloseDoor(0, cargo: true);
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptB && io.UnlockCount == 2,
            "B to take over once A has settled",
            token);

        // A's own settlement keeps publishing after it has given the display up -- the result and its
        // acknowledgement -- and none of it may take slot 5 off the screen again.
        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                Assert.Equal(AttemptB, harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId);
                AssertHighlighted(harness, 5);
            },
            token);

        AssertAnnouncedInOrder(harness, "1号仓操作完成，正在上报结果。", "准备执行装货：5号仓。");
        AssertNotRefused(harness);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A ends <c>UNKNOWN</c> -- its lock feedback stops being readable while its door stands open --
    /// while B queues behind it. A's "needs recovery" line is the current operation before B's
    /// <c>Preparing</c> replaces it, and it does not come back once B has the display.
    /// </summary>
    [Fact]
    public async Task TheRunningDemandsUnknownResultIsShownBeforeTheQueuedCommandTakesOver()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTwoDemandStopAsync(io, token);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator
                && harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptA,
            "A's slot to be unlocked and waiting on the operator",
            token);

        await SendSlotCommandAsync(harness, DemandB, AttemptB, [5]);
        await WaitForBothCommandsToReachTheVehicleAsync(harness, token);

        // The lock feedback on A's open door goes unreadable: the executor's wait ends on its unknown
        // leg and the operation settles UNKNOWN. B is still queued behind it at that point.
        io.SetUnreadable(0);
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptB,
            "B to take over once A has ended UNKNOWN",
            token);

        await AssertWhileAsync(
            DisplaySettleWindow,
            () => Assert.Equal(AttemptB, harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId),
            token);

        AssertAnnouncedInOrder(harness, "1号仓操作未完成，需要恢复处理。", "准备执行装货：5号仓。");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A is refused before any unlock -- its slot already holds a basket, so the executor's precheck
    /// ends it <c>FAILED</c> -- and the acknowledgement of that result never comes. B has the
    /// executor and its door open by then, and neither A's late "no acknowledgement" notice nor
    /// anything else of A's settlement takes the display back off B.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the only cover on the <c>operation-result-pending:</c> branch</b>, one of the two
    /// places this ticket changed. Nothing else in the suite loses an acknowledgement, so without
    /// this test someone could put that snapshot back to unconditional and every test would stay
    /// green while the operator watched the highlight jump to a slot whose door is shut.
    /// </para>
    /// <para>
    /// <b>Why <c>FAILED</c> comes in here rather than in a test of its own.</b> <c>FAILED</c> has no
    /// executable stretch to queue behind: the executor produces it only in
    /// <c>CreateRejectedResult</c>, and both call sites are ahead of the first journal write and of
    /// any pulse (ADR-cross-0058 decision 5), so A returns as soon as it has the gate. B can
    /// therefore only overlap A's <i>reporting</i>, which is exactly the window a lost
    /// acknowledgement stretches out -- so the two belong in one test, and that test is
    /// deterministic where either half alone was a race.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARefusedDemandWhoseResultAckIsLostDoesNotTakeTheDisplayBackFromTheNextCommand()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTwoDemandStopAsync(
            io,
            token,
            // A's OperationResult is taken and never answered: its wait for the DurableAck times out
            // mid-session and the business service publishes RESULT_ACK_PENDING.
            server => server.OperationResultAcksToDrop = 1);
        // A basket is already in slot 1: loading it again is SLOT_OPERATION_CONFLICT, refused by the
        // executor's precheck without a pulse. Slot 5 is untouched, so B can still run.
        io.CloseDoor(0, cargo: true);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await SendSlotCommandAsync(harness, DemandB, AttemptB, [5]);

        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptB && io.UnlockCount == 1,
            "B to take the executor after A's refusal",
            token);
        await harness.WaitUntilAsync(
            () => OperatorLog(harness).Contains(
                "操作已安全结束，但结果确认暂未收到；系统将保持同一结果重放，不会重复执行IO。"),
            "A's lost acknowledgement to be reported",
            token);

        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                Assert.Equal(AttemptB, harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId);
                AssertHighlighted(harness, 5);
            },
            token);

        AssertAnnouncedInOrder(harness, "1号仓操作未完成，需要恢复处理。", "准备执行装货：5号仓。");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// Both commands arrive before either has the executor. Only the one that takes it announces
    /// itself: the current operation is always the attempt the expected-action wait names -- the
    /// projection batch 7-13 proved does not get crossed -- and the other one stays silent until its
    /// turn.
    /// </summary>
    [Fact]
    public async Task OnlyTheCommandThatTakesTheExecutorBecomesTheCurrentOperationWhenBothArriveAtOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTwoDemandStopAsync(io, token);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await SendSlotCommandAsync(harness, DemandB, AttemptB, [5]);

        // Whichever one took the gate: the wait is keyed on the attempt that is actually unlocking.
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentExpectedActionWait is not null && io.UnlockCount == 1,
            "one of the two to take the executor",
            token);
        SlotExpectedActionWait running = harness.Business.CurrentExpectedActionWait!;
        string queuedAnnouncement = running.PhysicalSlotNumber == 1
            ? "准备执行装货：5号仓。"
            : "准备执行装货：1号仓。";

        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                Assert.Equal(
                    running.SlotOperationAttemptId,
                    harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId);
                AssertHighlighted(harness, running.PhysicalSlotNumber);
                string[] log = OperatorLog(harness);
                Assert.True(
                    !log.Contains(queuedAnnouncement),
                    $"the queued command announced \"{queuedAnnouncement}\" without the executor: [{string.Join(" | ", log)}]");
                Assert.Equal(1, io.UnlockCount);
            },
            token);

        AssertNotRefused(harness);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The same command arriving twice -- a reconnect replaying it -- announces nothing a second time
    /// and does not disturb the current operation.
    /// </summary>
    [Fact]
    public async Task AReplayedSlotCommandAnnouncesNoSecondPreparing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartTwoDemandStopAsync(io, token);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator
                && harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId == AttemptA,
            "A's slot to be unlocked and waiting on the operator",
            token);

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);

        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                Assert.Single(OperatorLog(harness), line => line == "准备执行装货：1号仓。");
                Assert.Equal(AttemptA, harness.Business.CurrentOperationSnapshot?.SlotOperationAttemptId);
                Assert.Equal(
                    WireToGateHmiOperationStage.WaitingOperator,
                    harness.Business.CurrentOperationSnapshot?.Stage);
                Assert.Equal(1, io.UnlockCount);
            },
            token);

        AssertNotRefused(harness);
        Assert.Empty(harness.UiErrors);
    }

    private static Task SendSlotCommandAsync(Harness harness, string demandId, string attemptId, int[] slots) =>
        harness.Server.SendCommandAsync(
            "SlotOperationCommand",
            Guid.NewGuid().ToString("D"),
            SlotCommand(demandId, attemptId, slots),
            Guid.NewGuid().ToString("D"));

    /// <summary>
    /// Both commands have reached the vehicle: each row carries the side its own command named, which
    /// only that command can put there. The business service is on the same event, so its handler for
    /// the second one is under way by the time this returns.
    /// </summary>
    private static Task WaitForBothCommandsToReachTheVehicleAsync(Harness harness, CancellationToken token) =>
        harness.WaitUntilAsync(
            () => harness.WorklistRows() is [("SUBLOT-A", "FRONT"), ("SUBLOT-B", "REAR")],
            "the second command to reach the vehicle",
            token);

    private static async Task<Harness> StartTwoDemandStopAsync(
        FakeIoModuleClient io,
        CancellationToken token,
        Action<FakeControlServer>? alsoConfigure = null,
        Func<IWireToGateJournal, IWireToGateJournal>? wrapJournal = null,
        string? journalPath = null)
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
                alsoConfigure?.Invoke(server);
            },
            token,
            journalPath: journalPath,
            io: io,
            wrapJournal: wrapJournal);
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null,
            "the worklist",
            token);
        return harness;
    }

    /// <summary>
    /// Holds <paramref name="assertion"/> over <paramref name="window"/>. A display claim that is
    /// only read once passes on the version that had not published its overwrite yet.
    /// </summary>
    private static async Task AssertWhileAsync(TimeSpan window, Action assertion, CancellationToken token)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + window;
        while (DateTimeOffset.UtcNow < until)
        {
            assertion();
            await Task.Delay(25, token);
        }
    }

    /// <summary>The target highlight is on exactly this slot, and on no other.</summary>
    private static void AssertHighlighted(Harness harness, int physicalSlot)
    {
        int[] highlighted = TargetSlots(harness);
        Assert.True(
            highlighted.Length == 1 && highlighted[0] == physicalSlot,
            $"the highlight was on [{string.Join("、", highlighted)}], not on slot {physicalSlot} alone.");
    }

    /// <summary>
    /// The physical slots the locker cards currently highlight as the operation's target.
    /// <c>Lockers</c> is built once and never grows or shrinks -- only the properties of its items
    /// change -- so enumerating it cannot collide with a rebuild the way <c>Logs</c> can.
    /// </summary>
    private static int[] TargetSlots(Harness harness) =>
        harness.ViewModel.Lockers
            .Where(locker => locker.IsTarget)
            .Select(locker => locker.PhysicalNumber)
            .ToArray();

    /// <summary>Both lines reached the operator, in this order.</summary>
    private static void AssertAnnouncedInOrder(Harness harness, string first, string second)
    {
        string[] log = OperatorLog(harness);
        int firstAt = Array.IndexOf(log, first);
        int secondAt = Array.IndexOf(log, second);
        Assert.True(firstAt >= 0, $"\"{first}\" never reached the operator log: [{string.Join(" | ", log)}]");
        Assert.True(secondAt >= 0, $"\"{second}\" never reached the operator log: [{string.Join(" | ", log)}]");
        Assert.True(
            firstAt < secondAt,
            $"\"{second}\" was announced at {secondAt}, before \"{first}\" at {firstAt}: [{string.Join(" | ", log)}]");
    }

    private static void AssertNotRefused(Harness harness) =>
        Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            item => item.MessageType == "SlotOperationCommandRejected");

    /// <summary>
    /// The operator log as the screen shows it, oldest first. Read by index off a count taken first:
    /// the harness publishes view-model updates on whatever thread finished them (the product has a
    /// dispatcher and does not), so enumerating <c>Logs</c> can collide with an <c>Add</c>. Indexing
    /// does not check the collection's version, and <c>TrimLogs</c> cannot remove anything at these
    /// sizes, so the count taken up front stays valid -- no retry, and no sleep to tune.
    /// </summary>
    private static string[] OperatorLog(Harness harness)
    {
        System.Collections.ObjectModel.ObservableCollection<SQCD.Agv.Wpf.ViewModels.LogLineViewModel> lines =
            harness.ViewModel.Logs;
        int count = lines.Count;
        string[] messages = new string[count];
        for (int index = 0; index < count; index++)
        {
            messages[index] = lines[index].Message;
        }

        return messages;
    }
}
