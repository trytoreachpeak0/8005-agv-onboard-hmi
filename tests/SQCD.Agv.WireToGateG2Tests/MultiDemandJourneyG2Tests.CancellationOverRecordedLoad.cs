using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 装货中取消与这次装货自己的 COMPLETED 结果同时落地（8005-agv-onboard-hmi#259）。
/// </summary>
/// <remarks>
/// <para>
/// 取消向量用的尝试号就是那次装货的尝试号。真服务端先处理先到的取消请求、授权，再收下随后到的装货结果并回
/// <c>DurableAck</c>；两条应答在车上各有一个后续，谁先落进日志簿不确定。两种顺序各一组用例：
/// </para>
/// <list type="bullet">
/// <item>顺序 a：结果先记录（尝试结清），取消的准备写入在后。准备写入把尝试号写回未结，那是向量持有自己的尝试，
/// 与对已记录装货做补偿同形；取消照常跑完。护栏。</item>
/// <item>顺序 b：取消先准备好、执行器已开门，结果的确认后到。修复之前 <c>MarkResultRecordedAsync</c> 只核对尝试号，
/// 把正在运行的取消向量连同活动开锁集一起清掉；此时进程退出，重启后日志簿里没有向量、没有开着的门。</item>
/// </list>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 顺序 b：装货结果的确认在取消向量准备好、取消已开门之后才到。确认只收掉装货结果自己的那一份，向量、未结尝试与
    /// 活动开锁集原样留下；取消跑完时这次尝试随向量一起结掉。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ALoadResultAcknowledgedAfterTheCancellationTookItsAttemptOverLeavesTheCancellationOnFile()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        TaskCompletionSource resultAckHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = await StartCancellationStopAsync(
            io,
            Harness.NewJournalPath(),
            server => server.OperationResultAckHold = resultAckHeld.Task,
            token,
            operationTimeout: OrderBOperationTimeout);
        using ReleaseOnExit releaseAck = new(() => resultAckHeld.TrySetResult());

        Task<bool> press = await PrepareCancellationThenAcknowledgeLoadResultAsync(
            harness,
            io,
            resultAckHeld,
            holdSlotOneUntilShut: true,
            token);

        WireToGateRecoveryState journal = ReadJournal(harness, token);
        AssertCancellationOnFile(journal, activeSlots: [1]);
        Assert.Equal(AttemptA, journal.LastCompletedLoadOperationContext?.SlotOperationAttemptId);
        Assert.DoesNotContain(journal.PendingResults, pending => pending.BusinessId == AttemptA);

        await EmptyBothSlotsAsync(harness, io, press, firstCancellationPulse: 3, token);
        Assert.True(await press);
        JsonElement result = Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        AssertAttemptSettledWithTheCancellation(ReadJournal(harness, token));
        Assert.Single(ReceivedPayloads(harness, "OperationResult"));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// 顺序 b 再加一次重启：确认落地之后、取消执行器下一次写检查点之前进程退出。重启后向量与开着的 1 号仓门都还在，
    /// 握手照实报出这扇门、不补发装货结果，操作员再按一次取消就把它续完。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ARestartAfterTheLateLoadAcknowledgementResumesTheCancellationOverItsOpenDoor()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        TaskCompletionSource resultAckHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness first = await StartCancellationStopAsync(
            io,
            journalPath,
            server => server.OperationResultAckHold = resultAckHeld.Task,
            token,
            operationTimeout: OrderBOperationTimeout);
        using ReleaseOnExit releaseAck = new(() => resultAckHeld.TrySetResult());

        // Not held, so this one keeps OrderBOperationTimeout's ten seconds of margin and no more: StopVehicleAsync disposes
        // the business service, which waits for every task in flight, and stopping does not cancel the vector's wait on
        // slot 1. Held, that wait would last until a door shut that comes only after the restart, and the stop would
        // never return (8005-agv-onboard-hmi#331 review H1).
        Task<bool> press = await PrepareCancellationThenAcknowledgeLoadResultAsync(
            first,
            io,
            resultAckHeld,
            holdSlotOneUntilShut: false,
            token);
        await first.StopVehicleAsync();
        await Record.ExceptionAsync(() => press);
        first.Server.SimulateOnboardProcessRestart();
        int firstConnection = first.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
        int unlocksBeforeRestart = io.UnlockCount;

        await using Harness afterRestart = await Harness.StartAgainstAsync(
            first.Server,
            token,
            journalPath,
            io,
            recoveryOptions: CancellationOverRecordedLoadRecovery,
            messageTimeout: TimeSpan.FromSeconds(30));
        AssertCancellationOnFile(ReadJournal(afterRestart, token), activeSlots: [1]);
        (int _, string _, string _, string wireLine) = Assert.Single(
            first.Server.ReceivedEnvelopes,
            envelope => envelope.Connection > firstConnection && envelope.MessageType == "RecoveryStateReport");
        using (JsonDocument report = JsonDocument.Parse(wireLine))
        {
            JsonElement payload = report.RootElement.GetProperty("payload");
            Assert.Equal(
                [1],
                payload.GetProperty("activeUnlockSlots").EnumerateArray().Select(slot => slot.GetInt32()));
            Assert.Empty(payload.GetProperty("pendingResults").EnumerateArray());
        }

        // Restarting opened nothing.
        Assert.Equal(unlocksBeforeRestart, io.UnlockCount);
        await afterRestart.WaitUntilAsync(
            () => afterRestart.Business.CanRequestLoadCancellation,
            "the restarted vehicle to offer the cancellation on file",
            token);
        // Door 1 was left open by the run that died; the operator empties and shuts it before pressing again. Shut after
        // the press, it raced the resumed vector's first reading at its write-ahead fence and the slot came out UNKNOWN
        // about half the time (review M-1).
        io.CloseDoor(0, cargo: false);

        // While the cancellation is on file without a result of its own, the next demand's command is refused, door 1
        // proven shut or not: hmi#267's "settle before start" holds again now that the vector stays (review L-2). The
        // server sends it again once the cancellation has ended, and then it runs.
        await SendSlotCommandAsync(afterRestart, DemandB, AttemptB, [5]);
        // The refusal goes on the wire before the operator is told, so both are waited for.
        await afterRestart.WaitUntilAsync(
            () => RefusalsOfB(afterRestart) == 1
                && afterRestart.Events.Any(item => item.Kind == "RECOVERY_VECTOR_UNSETTLED"),
            "B's first copy to be refused over the cancellation on file, and the operator told",
            token);
        Assert.Equal(unlocksBeforeRestart, io.UnlockCount);
        AssertCancellationOnFile(ReadJournal(afterRestart, token), activeSlots: [1]);

        // Slot 2 is emptied and shut as soon as the resumed cancellation opens it: told in advance, so the vector's own
        // wait shuts it. Shut by this thread after it saw the pulse, a late wake-up ran the wait past its two-second
        // operation timeout and the cancellation ended UNKNOWN (8005-agv-onboard-hmi#323).
        io.CloseDoorAfterNextUnlock(1, cargo: false);
        Task<bool> again = afterRestart.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        Assert.True(await again);
        Assert.Equal(unlocksBeforeRestart + 1, io.UnlockCount);
        Assert.False(io.HasScheduledClose);
        JsonElement result = Assert.Single(ReceivedPayloads(afterRestart, "LoadCancellationResult"));
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        AssertAttemptSettledWithTheCancellation(ReadJournal(afterRestart, token));
        Assert.Single(ReceivedPayloads(afterRestart, "OperationResult"));

        int unlocksBeforeB = io.UnlockCount;
        await SendSlotCommandAsync(afterRestart, DemandB, AttemptB, [5]);
        await afterRestart.WaitUntilAsync(
            () => ReadJournal(afterRestart, token).UnsettledSlotOperationAttemptId == AttemptB
                && io.UnlockCount == unlocksBeforeB + 1,
            "B's second copy to run once the cancellation has ended",
            token);
        Assert.Equal(1, RefusalsOfB(afterRestart));
        Assert.Empty(afterRestart.UiErrors);
    }

    /// <summary>
    /// 顺序 b 之后，取消的结果被服务端永久拒收，维护人员核对后结束（review S-1）：向量去掉，装货的尝试、上下文和活动开锁集
    /// 留下。下一次握手后的还原里，中断结算先查到的是这次装货已确认的 COMPLETED 结果；修复之前它照此补记，把尝试结清、
    /// 活动开锁集写成空。取消的结论在先：尝试留着、不报装货结果、欠恢复入口。门开、门关两种。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ARefusedCancellationEndedAfterTheManualCheckKeepsTheLoadsAttemptAcrossAReconnect(bool doorLeftOpen)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        TaskCompletionSource resultAckHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = await StartCancellationStopAsync(
            io,
            Harness.NewJournalPath(),
            server =>
            {
                server.OperationResultAckHold = resultAckHeld.Task;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LoadCancellationResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            },
            token,
            operationTimeout: OrderBOperationTimeout);
        using ReleaseOnExit releaseAck = new(() => resultAckHeld.TrySetResult());

        // Held only when the door is to be shut: left open, the cancellation has to run its wait out to end UNKNOWN, and
        // SetUnreadable does not lift a hold.
        Task<bool> press = await PrepareCancellationThenAcknowledgeLoadResultAsync(
            harness,
            io,
            resultAckHeld,
            holdSlotOneUntilShut: !doorLeftOpen,
            token);
        if (doorLeftOpen)
        {
            // Door 1's lock feedback goes unreadable while it stands open: the cancellation ends UNKNOWN over it.
            io.SetUnreadable(0);
        }
        else
        {
            await EmptyBothSlotsAsync(harness, io, press, firstCancellationPulse: 3, token);
        }

        await Record.ExceptionAsync(() => press);
        await harness.WaitUntilAsync(
            () => harness.Business.CanCloseConflictedRecoveryAfterReview,
            "the refused cancellation to wait for a maintainer's manual check",
            token);
        Assert.True(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));
        WireToGateRecoveryState ended = ReadJournal(harness, token);
        Assert.Null(ended.RecoveryVector);
        Assert.Equal(AttemptA, ended.UnsettledSlotOperationAttemptId);
        Assert.Equal(AttemptA, ended.OperationContext?.SlotOperationAttemptId);
        int[] activeWhenEnded = [.. ended.ActiveUnlockSlots];
        Assert.Equal(doorLeftOpen ? [1] : [], activeWhenEnded);

        int restoresBefore = RestoredEntryLines(harness);
        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await harness.WaitUntilAsync(
            () => RestoredEntryLines(harness) > restoresBefore
                || ReadJournal(harness, token).UnsettledSlotOperationAttemptId is null,
            "the restore to owe the recovery entry, or to settle the attempt",
            token);

        WireToGateRecoveryState after = ReadJournal(harness, token);
        Assert.Equal(AttemptA, after.UnsettledSlotOperationAttemptId);
        Assert.Equal(activeWhenEnded, after.ActiveUnlockSlots);
        Assert.NotEqual(WireToGateRecoveryCheckpoint.ResultRecorded, after.ProvenRecoveryCheckpoint);
        Assert.False(harness.Business.CanRequestLoadCorrection);
        Assert.Single(ReceivedPayloads(harness, "OperationResult"));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>The restore's projection of an unfinished attempt: the line that puts the recovery entry up.</summary>
    private static int RestoredEntryLines(Harness harness) =>
        harness.Events.Count(item => item.Kind == "OPERATION_RECOVERY_REQUIRED"
            && item.Message.Contains("操作未完成", StringComparison.Ordinal)
            && item.Message.Contains("需要管理员恢复", StringComparison.Ordinal));

    /// <summary>
    /// 顺序 a：装货结果先记录（尝试已结清），取消的准备写入在后，把尝试号写回未结——向量持有自己的尝试，与对已记录
    /// 装货做补偿同形。护栏：取消照常跑完，报 <c>ALL_EMPTY</c>，尝试随向量一起结掉，不补发装货结果。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ACancellationAuthorizedAfterTheLoadsResultWasRecordedRunsToItsEnd()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource authorizationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        PreparedVectorJournal? fence = null;
        await using Harness harness = await StartCancellationStopAsync(
            io,
            Harness.NewJournalPath(),
            server => server.LoadCancellationAuthorizationHold = authorizationHeld.Task,
            token,
            wrapJournal: inner => fence = new PreparedVectorJournal(inner));
        using ReleaseOnExit releaseAuthorization = new(() => authorizationHeld.TrySetResult());
        using ReleaseOnExit releaseFence = new(() => fence!.Release());

        await OpenBothSlotsAsync(harness, io, token);
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

        // The prepare write is what is asserted, so the vector is held just after it until the journal has been read. Read
        // without the hold, the test thread had to get there within the round trip of the vector's PREPARING progress, after
        // which slot 1's unlock is written ahead of its pulse, and a late wake-up found slot 1 already in the active unlock
        // set (8005-agv-onboard-hmi#323).
        //
        // The closes are scheduled before anything is released: scheduled after the fence, a test thread that woke late
        // there still ran slot 1's wait out of its two seconds (review L1).
        ScheduleEmptyingOfBothSlots(io, firstCancellationPulse: 3);
        authorizationHeld.SetResult();
        await harness.WaitUntilAsync(
            () => fence!.PrepareWriteHeld || press.IsCompleted,
            "the cancellation's prepare write, and the vector held there",
            token);
        Assert.True(fence!.PrepareWriteHeld);
        AssertCancellationOnFile(ReadJournal(harness, token), activeSlots: []);
        // Held at its prepare write, the vector has unlocked nothing yet: the two pulses are the load's own.
        Assert.Equal(2, io.UnlockCount);
        fence.Release();

        await WaitForBothSlotsEmptiedAsync(harness, io, press, token);
        Assert.True(await press);
        JsonElement result = Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        AssertAttemptSettledWithTheCancellation(ReadJournal(harness, token));
        Assert.Single(ReceivedPayloads(harness, "OperationResult"));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// Builds order (b): the load completes with its acknowledgement held, the cancellation is authorized and prepared
    /// and opens slot 1, and only then is the acknowledgement released and the load's handler done with it.
    /// </summary>
    /// <remarks>
    /// From the pulse that opens slot 1 the cancellation waits on it with <see cref="OrderBOperationTimeout"/>, while this
    /// thread releases the acknowledgement, waits for the log line and reads the journal; the door is shut only after that,
    /// by the caller. With <paramref name="holdSlotOneUntilShut"/> that wait lasts until the door is shut, so a stall here
    /// longer than the timeout does not end slot 1 UNKNOWN (8005-agv-onboard-hmi#331 review M1: ten seconds of margin,
    /// not independence from the test thread). Told before the press: the load is over by then and nothing else waits on
    /// slot 1. Not for a caller whose cancellation must run its wait out -- a hold is lifted only by shutting the door --
    /// nor for one that stops the vehicle with the door still open: the stop waits for that wait to end.
    /// </remarks>
    private static async Task<Task<bool>> PrepareCancellationThenAcknowledgeLoadResultAsync(
        Harness harness,
        FakeIoModuleClient io,
        TaskCompletionSource resultAckHeld,
        bool holdSlotOneUntilShut,
        CancellationToken token)
    {
        await OpenBothSlotsAsync(harness, io, token);
        io.CloseDoor(1, cargo: true);
        await harness.WaitUntilAsync(
            () => harness.Server.OperationResultsHeld == 1,
            "the load's COMPLETED result to be held unacknowledged",
            token);

        if (holdSlotOneUntilShut)
        {
            io.HoldWaitsUntilShut(0);
        }

        Task<bool> press = harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        await harness.WaitUntilAsync(
            () => io.UnlockCount == 3 || press.IsCompleted,
            "the cancellation to open slot 1",
            token);
        Assert.False(press.IsCompleted);
        AssertCancellationOnFile(ReadJournal(harness, token), activeSlots: [1]);

        resultAckHeld.SetResult();
        await harness.WaitUntilAsync(
            () => harness.LogsSnapshot().Any(line =>
                line.Message.Contains("1、2号仓操作结果已被服务端确认", StringComparison.Ordinal)),
            "the load's handler to be done with the acknowledgement",
            token);
        return press;
    }

    private static async Task OpenBothSlotsAsync(Harness harness, FakeIoModuleClient io, CancellationToken token)
    {
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 2, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry",
            token);
    }

    /// <summary>The operator empties and shuts slot 1, then slot 2, as the cancellation opens each.</summary>
    /// <remarks>
    /// Both closes are told to the fake in advance, keyed on the pulse that opens each slot, so the vector's own wait shuts
    /// the door. Shut by this thread after it saw each pulse, a late wake-up ran the wait past its two-second operation
    /// timeout and the slot came out UNKNOWN (8005-agv-onboard-hmi#323). Slot 1 may already have been pulsed when this is
    /// called (order b opens it before the load's acknowledgement is released); the close then lands on the wait already
    /// under way.
    /// </remarks>
    private static Task EmptyBothSlotsAsync(
        Harness harness,
        FakeIoModuleClient io,
        Task<bool> press,
        int firstCancellationPulse,
        CancellationToken token)
    {
        ScheduleEmptyingOfBothSlots(io, firstCancellationPulse);
        return WaitForBothSlotsEmptiedAsync(harness, io, press, token);
    }

    /// <summary>Tells the fake, ahead of the pulses, that slot 1 and then slot 2 are emptied and shut once opened.</summary>
    private static void ScheduleEmptyingOfBothSlots(FakeIoModuleClient io, int firstCancellationPulse)
    {
        io.CloseDoorOnceUnlockedAt(0, cargo: false, firstCancellationPulse);
        io.CloseDoorOnceUnlockedAt(1, cargo: false, firstCancellationPulse + 1);
    }

    private static Task WaitForBothSlotsEmptiedAsync(
        Harness harness,
        FakeIoModuleClient io,
        Task<bool> press,
        CancellationToken token) =>
        harness.WaitUntilAsync(
            () => !io.HasScheduledClose || press.IsCompleted,
            "the cancellation to open and the operator to shut slot 1, then slot 2",
            token);

    private static void AssertCancellationOnFile(WireToGateRecoveryState journal, int[] activeSlots)
    {
        Assert.Equal(AttemptA, journal.UnsettledSlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryVectorTypes.LoadCancellation, journal.RecoveryVector?.VectorType);
        Assert.Equal(AttemptA, journal.RecoveryVector?.SlotOperationAttemptId);
        Assert.NotEqual(WireToGateRecoveryCheckpoint.ResultRecorded, journal.ProvenRecoveryCheckpoint);
        Assert.Equal(activeSlots, journal.ActiveUnlockSlots);
    }

    /// <summary>The cancellation settled, and the load's attempt with it: nothing left unsettled under it.</summary>
    private static void AssertAttemptSettledWithTheCancellation(WireToGateRecoveryState journal)
    {
        Assert.Null(journal.UnsettledSlotOperationAttemptId);
        Assert.Null(journal.RecoveryVector);
        Assert.Null(journal.OperationContext);
        Assert.Equal(WireToGateRecoveryCheckpoint.ResultRecorded, journal.ProvenRecoveryCheckpoint);
        Assert.Empty(journal.ActiveUnlockSlots);
        Assert.Empty(journal.PendingResults);
    }

    /// <summary>
    /// The operator's time at each door for the order (b) tests, in place of the harness's two seconds.
    /// </summary>
    /// <remarks>
    /// Order (b) keeps slot 1 open on purpose: the cancellation opens it, and only then does the test thread release the
    /// load's acknowledgement, wait for the handler's log line and read the journal -- and in the restart test stop the
    /// vehicle -- before the door is shut. That whole stretch runs against the vector's wait for the door, which ends
    /// UNKNOWN when the operation timeout runs out, so a test thread that woke late inside it failed the test
    /// (8005-agv-onboard-hmi#323 review S1). The timeout is the operator's time at the door, not a budget for the system to
    /// answer within, so a longer one hides nothing the vehicle does. Only these three tests take it: every other test in
    /// the class shuts its doors through the fake's scheduled closes and keeps the harness's two seconds.
    /// </remarks>
    private static readonly TimeSpan OrderBOperationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>This file's own proof variable: a maintainer's close-out of a refused result asks for one.</summary>
    private const string CancellationOverRecordedLoadProofVariable = "W2G_G2_HMI259_RECOVERY_PROOF";

    private static readonly WireToGateRecoveryOptions CancellationOverRecordedLoadRecovery = new(
        ResumeAfterRepairEnabled: false,
        AuthenticationProofEnvironmentVariable: CancellationOverRecordedLoadProofVariable,
        AdministratorRole: "MAINTENANCE_ADMINISTRATOR",
        VerificationMethod: "SESSION");

    private static async Task<Harness> StartCancellationStopAsync(
        FakeIoModuleClient io,
        string journalPath,
        Action<FakeControlServer> configure,
        CancellationToken token,
        Func<IWireToGateJournal, IWireToGateJournal>? wrapJournal = null,
        TimeSpan? operationTimeout = null)
    {
        Environment.SetEnvironmentVariable(CancellationOverRecordedLoadProofVariable, "hmi259-proof");
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
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1, 2];
                configure(server);
            },
            token,
            journalPath,
            io: io,
            recoveryOptions: CancellationOverRecordedLoadRecovery,
            messageTimeout: TimeSpan.FromSeconds(30),
            wrapJournal: wrapJournal,
            operationTimeout: operationTimeout);
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null
                && harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the worklist on a ready session",
            token);
        return harness;
    }

    /// <summary>
    /// Holds the vector executor once its prepare write has landed -- the first executor write naming a load cancellation
    /// vector -- until released; everything else passes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which writes it sees rests on how the two overloads are split.</b> The business service puts the vector on file
    /// first (<c>RequestLoadCancellationCoreAsync</c> → <c>WriteRecoveryVectorPreparedAsync</c>), but every business
    /// service write goes through <c>UpdateRecoveryStateCachedAsync</c>, the overload with the settled callback, which this
    /// passes straight through. The executor's checkpoints (<c>WriteVectorStateAsync</c>) use the overload without one,
    /// and only that overload is held, so the first write held is the executor's own <c>Prepared</c>. Were the executor
    /// moved to the other overload, nothing would be held: the test's wait would end on the press completing instead, and
    /// its <c>Assert.True(fence.PrepareWriteHeld)</c> would fail loudly rather than let the test pass on the wrong write.
    /// </para>
    /// <para>
    /// Held after the write rather than before the next one: the vector starts each slot's operation timeout after its
    /// PREPARING progress, ahead of the slot's unlock write, so a hold at the unlock write would be spent out of slot 1's
    /// two seconds. Here the vector has not started any slot's clock yet.
    /// </para>
    /// </remarks>
    private sealed class PreparedVectorJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public bool PrepareWriteHeld => Volatile.Read(ref _held) != 0;

        public void Release() => _release.TrySetResult();

        public async Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default)
        {
            WireToGateRecoveryState? written = await inner.UpdateRecoveryStateAsync(change, cancellationToken);
            if (written is { RecoveryVector.VectorType: WireToGateRecoveryVectorTypes.LoadCancellation }
                && Interlocked.Exchange(ref _held, 1) == 0)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            return written;
        }

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);

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

        public Task<WireToGateDurableMessage> MarkOutgoingAbandonedAsync(
            string messageId,
            string contentSha256,
            string reasonCode,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAbandonedAsync(messageId, contentSha256, reasonCode, cancellationToken);

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
            Release();
            return inner.DisposeAsync();
        }
    }
}
