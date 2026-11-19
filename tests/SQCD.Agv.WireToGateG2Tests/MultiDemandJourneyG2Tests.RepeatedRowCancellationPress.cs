using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 待送点同一行的「取消」按了两次（<c>8005-agv-onboard-hmi#408</c>，agv02 10-22 21:51 两次、10-24 03:19 一次）：第一次取消已结清
/// （授权、结果被确认，结清时本站录入请求一并清掉），服务端不带这条需求的新清单还没到，先把同一版录入请求又发了一遍，那一行
/// 又按得动；操作员再按、确认，就用同一个派生取消号、新的 <c>verifiedAt</c> 又发了一份，服务端以
/// <c>BUSINESS_ID_CONTENT_CONFLICT</c> 拒收，屏上报「取消失败」，而取消其实已经生效。第一次在途时再按、排在门闩后面的那次按键
/// 修前不发第二份（录入请求已清，确认时找不到对象），但答的是「对象已经变了，请重新按」。
/// </summary>
/// <remarks>
/// 修法：发之前先看这个取消号的结果是否已在发件箱里（取消已结清），在就不再发，按「已确认」答复操作员，不报失败。
/// 补发（答复丢了）仍照首次内容逐字重发，见 <see cref="ARowPressWhoseAnswerWasLostIsResentUnchangedAndTheOtherRowsWait"/>。
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 两次按键都在第一次发出前取下（同一行、同一对象），两次都确认：服务端只收到一份申请、一份结果，没有冲突；两次按键都不报失败，
    /// 屏上没有「被阻断」的提示。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ASecondConfirmedPressOnTheSameRowSendsNothingOnceTheFirstHasConcluded()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartStagingRowStopAsync(token);

        WorklistRowCancellationConfirmation first = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB));
        WorklistRowCancellationConfirmation second = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB));
        // The clock moves between the two confirmations, as it does on the screen: a second press that built its own content
        // would carry another verifiedAt.
        await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        Task<bool> firstSend = first.SendAsync(token);
        Task<bool> secondSend = second.SendAsync(token);
        bool[] answers = await Task.WhenAll(firstSend, secondSend);

        Assert.Equal([true, true], answers);
        AssertOneCancellationAndNoFailureShown(harness);
        // The queued press is told its cancellation took effect, not that its subject changed and to press again.
        Assert.Contains(
            harness.Events,
            notice => notice.Message.Contains("服务端已经确认", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Events, notice => notice.Kind == WireToGateBusinessService.LoadCancellationNothingToCancelKind);
    }

    /// <summary>
    /// 现场那一种：第一次已结清，服务端还没出不带这条需求的新清单，先把同一版录入请求又发了一遍（现场约 17 秒一遍，三次冲突前
    /// 2～3 秒都收到过一遍），那一行又按得动了；操作员再按、确认：什么都不发，告诉操作员这项取消服务端已经确认，不报失败。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task APressOnARowWhoseCancellationConcludedBeforeTheNextWorklistSendsNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartStagingRowStopAsync(token);

        WorklistRowCancellationConfirmation first = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB));
        Assert.True(await first.SendAsync(token));

        await harness.Server.SendCommandAsync(
            "SublotEntryRequested",
            Guid.NewGuid().ToString("D"),
            new
            {
                operationSessionId = OperationSessionId,
                stationId = "ST-01",
                worklistRevision = 1,
                expectedSublots = SublotsAAndB,
                entryMethods = FrozenEntryMethods,
                expiresOnRevisionChange = true
            });
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestWorklistRowCancellation(DemandB),
            "B's row pressable again under the entry request sent again",
            token);
        await Task.Delay(TimeSpan.FromMilliseconds(20), token);

        WorklistRowCancellationConfirmation again = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB));
        bool answered = await again.SendAsync(token);

        AssertOneCancellationAndNoFailureShown(harness);
        Assert.True(answered);
        Assert.Contains(
            harness.Events,
            notice => notice.Message.Contains("服务端已经确认", StringComparison.Ordinal));
    }

    /// <summary>
    /// 第一次的结果被服务端以人工核对类原因拒收、发件箱那一行已放弃：排队的第二次按键不发申请，告诉操作员在等维护人员核对，
    /// 不说「服务端已经确认」（审查 L1、L5）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ASecondPressOverACancellationWhoseResultWasRefusedForGoodSaysItAwaitsAMaintainer()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartStagingRowStopAsync(
            token,
            server => server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LoadCancellationResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
            });

        WorklistRowCancellationConfirmation first = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB));
        WorklistRowCancellationConfirmation second = harness.OnUi(() => harness.ViewModel.BeginWorklistRowCancellation(DemandB));
        _ = await first.SendAsync(token);
        bool answered = await second.SendAsync(token);

        Assert.Single(Received(harness, "LoadCancellationStartRequested"));
        Assert.Empty(harness.Server.RecoveryRequestConflicts);
        Assert.True(answered);
        Assert.Contains(
            harness.Events,
            notice => notice.Kind == "RESULT_ABANDONED"
                && notice.Message.Contains("正在等维护人员核对", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Events, notice => notice.Message.Contains("服务端已经确认", StringComparison.Ordinal));
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// 在途装货的取消（向量带 attempt）不进这条答复：按下时对象是那个向量，确认前另一次按键补报、结果被确认、向量结清，
    /// 确认时答的仍是修前的「对象已经变了」，不说「服务端已经确认」——在途取消迟到的结论未必是 <c>ALL_EMPTY</c>（审查 L2、L5）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task APressOverAnInFlightCancellationVectorThatSettledKeepsItsEarlierAnswer()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(
            io,
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationResultAcksToDrop = 1;
            });
        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        await ShutEmptyUntilCancellationCanBePressedAsync(harness, io, 0, token);
        _ = harness.ViewModel.RequestLoadCancellationAsync(token);
        await harness.WaitUntilAsync(
            () => harness.Business.CachedRecoveryStateForTest.RecoveryVector is { } vector && vector.DemandId == DemandA
                && ReceivedPayloads(harness, "LoadCancellationResult").Length > 0,
            "A's in-flight cancellation on file, its result unacknowledged",
            token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the entry that reports it again",
            token);

        LoadCancellationConfirmation held = harness.ViewModel.BeginLoadCancellation();
        LoadCancellationConfirmation reporting = harness.ViewModel.BeginLoadCancellation();
        Assert.True(await reporting.SendAsync(token));
        await harness.WaitUntilAsync(
            () => harness.Business.CachedRecoveryStateForTest.RecoveryVector is null,
            "the vector settled by the second press's report",
            token);

        Assert.True(await held.SendAsync(token));

        Assert.Single(Received(harness, "LoadCancellationStartRequested"));
        Assert.DoesNotContain(harness.Events, notice => notice.Message.Contains("这次按键没有再发申请", StringComparison.Ordinal));
        Assert.Contains(harness.Events, notice => notice.Kind == NothingToCancelKind);
        Assert.Empty(harness.UiErrors);
    }

    private static void AssertOneCancellationAndNoFailureShown(Harness harness)
    {
        Assert.Single(Received(harness, "LoadCancellationStartRequested"));
        Assert.Single(Received(harness, "LoadCancellationResult"));
        Assert.Empty(harness.Server.RecoveryRequestConflicts);
        Assert.DoesNotContain(harness.Events, notice => notice.Kind == "RECOVERY_BLOCKED");
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }
}
