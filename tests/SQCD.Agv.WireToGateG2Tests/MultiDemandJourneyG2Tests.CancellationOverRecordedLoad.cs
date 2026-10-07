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
            token);
        using ReleaseOnExit releaseAck = new(() => resultAckHeld.TrySetResult());

        Task<bool> press = await PrepareCancellationThenAcknowledgeLoadResultAsync(harness, io, resultAckHeld, token);

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
            token);
        using ReleaseOnExit releaseAck = new(() => resultAckHeld.TrySetResult());

        Task<bool> press = await PrepareCancellationThenAcknowledgeLoadResultAsync(first, io, resultAckHeld, token);
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
        await afterRestart.WaitUntilAsync(() => RefusalsOfB(afterRestart) == 1, "B's first copy to be refused", token);
        Assert.Contains(afterRestart.Events, item => item.Kind == "RECOVERY_VECTOR_UNSETTLED");
        Assert.Equal(unlocksBeforeRestart, io.UnlockCount);
        AssertCancellationOnFile(ReadJournal(afterRestart, token), activeSlots: [1]);

        Task<bool> again = afterRestart.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        await afterRestart.WaitUntilAsync(
            () => io.UnlockCount > unlocksBeforeRestart || again.IsCompleted,
            "slot 2 to be opened for emptying",
            token);
        io.CloseDoor(1, cargo: false);
        Assert.True(await again);
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
            token);
        using ReleaseOnExit releaseAck = new(() => resultAckHeld.TrySetResult());

        Task<bool> press = await PrepareCancellationThenAcknowledgeLoadResultAsync(harness, io, resultAckHeld, token);
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
        await using Harness harness = await StartCancellationStopAsync(
            io,
            Harness.NewJournalPath(),
            server => server.LoadCancellationAuthorizationHold = authorizationHeld.Task,
            token);
        using ReleaseOnExit releaseAuthorization = new(() => authorizationHeld.TrySetResult());

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

        authorizationHeld.SetResult();
        await harness.WaitUntilAsync(
            () => ReadJournal(harness, token).RecoveryVector is not null || press.IsCompleted,
            "the cancellation's prepare write",
            token);
        AssertCancellationOnFile(ReadJournal(harness, token), activeSlots: []);

        await EmptyBothSlotsAsync(harness, io, press, firstCancellationPulse: 3, token);
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
    private static async Task<Task<bool>> PrepareCancellationThenAcknowledgeLoadResultAsync(
        Harness harness,
        FakeIoModuleClient io,
        TaskCompletionSource resultAckHeld,
        CancellationToken token)
    {
        await OpenBothSlotsAsync(harness, io, token);
        io.CloseDoor(1, cargo: true);
        await harness.WaitUntilAsync(
            () => harness.Server.OperationResultsHeld == 1,
            "the load's COMPLETED result to be held unacknowledged",
            token);

        Task<bool> press = harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        await harness.WaitUntilAsync(
            () => io.UnlockCount == 3 || press.IsCompleted,
            "the cancellation to open slot 1",
            token);
        Assert.False(press.IsCompleted);
        AssertCancellationOnFile(ReadJournal(harness, token), activeSlots: [1]);

        resultAckHeld.SetResult();
        await harness.WaitUntilAsync(
            () => harness.ViewModel.Logs.Any(line =>
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
    private static async Task EmptyBothSlotsAsync(
        Harness harness,
        FakeIoModuleClient io,
        Task<bool> press,
        int firstCancellationPulse,
        CancellationToken token)
    {
        await harness.WaitUntilAsync(
            () => io.UnlockCount >= firstCancellationPulse || press.IsCompleted,
            "the cancellation to open slot 1",
            token);
        io.CloseDoor(0, cargo: false);
        await harness.WaitUntilAsync(
            () => io.UnlockCount >= firstCancellationPulse + 1 || press.IsCompleted,
            "the cancellation to open slot 2",
            token);
        io.CloseDoor(1, cargo: false);
    }

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
        CancellationToken token)
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
            messageTimeout: TimeSpan.FromSeconds(30));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist is not null
                && harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the worklist on a ready session",
            token);
        return harness;
    }
}
