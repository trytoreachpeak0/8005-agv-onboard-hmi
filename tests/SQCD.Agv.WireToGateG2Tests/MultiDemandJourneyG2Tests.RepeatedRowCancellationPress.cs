using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 待送点同一行的「取消」按了两次（<c>8005-agv-onboard-hmi#408</c>，agv02 10-22 21:51 两次、10-24 03:19 一次）：第一次在等答复，
/// 操作员又按了一次并确认。第二次按键排在恢复请求门闩后面，等第一次授权、结果被确认、日志簿清空之后才轮到它；这时服务端
/// 不带这条需求的新清单还没到，那一行还在，于是用同一个派生取消号、新的 <c>verifiedAt</c> 又发了一份，服务端以
/// <c>BUSINESS_ID_CONTENT_CONFLICT</c> 拒收，屏上报「取消失败」，而取消其实已经生效。
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
