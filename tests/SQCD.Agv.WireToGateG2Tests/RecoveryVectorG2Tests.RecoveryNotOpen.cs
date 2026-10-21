using System.Collections.Concurrent;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ScreenState;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// Recovery the server has not opened for this vehicle (8005-agv-onboard-hmi#391, the onboard half of control-server#797
/// option C). The recovery entries stay offered whether or not maintenance has granted this vehicle and journey a
/// recovery authorization; without one the server refuses the session with <c>RECOVERY_AUTHENTICATION_FAILED</c>, refuses
/// an action outside what it granted with the same code, and closes a session whose authorization has ended with that code
/// as its <c>closedReason</c> (ADR-cross-0066 decision 5, CP-0022 section 3). The screen says what that refusal means and
/// whom to call -- not that a credential was wrong, which read as a broken vehicle -- for this refusal and not an earlier
/// one, and a refusal is not asked again by the vehicle.
/// </summary>
public sealed partial class RecoveryVectorG2Tests
{
    private const string RecoveryNotOpenCode = "RECOVERY_AUTHENTICATION_FAILED";

    private const string RecoveryNotOpenSentence = "恢复尚未开放，请联系系统维护人员。";

    private const string RecoveryActionNotOpenSentence = "这项恢复操作不在本次开放的范围内，请联系系统维护人员。";

    private const string AuthorizationClosedSentence = "本次恢复已结束：恢复授权已失效。需要继续请联系系统维护人员重新开放。";

    private const string RefusalOtherwise = "恢复申请未被接受。";

    /// <summary>
    /// The press before any authorization: refused, said in the operator's words, and left there. Nothing on file is
    /// one the reconnect review would send again, and the press moved no press window.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeRefusedForWantOfAuthorizationSaysRecoveryIsNotOpenAndIsNotAskedAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartUnauthorizedAsync(token);

        Assert.True(harness.Business.CanRequestResumeAfterRepair);
        Assert.False(await harness.Business.RequestResumeAfterRepairAsync(null, token));
        await harness.WaitForRecoveryBlockedAsync(RecoveryNotOpenCode, token);

        WireToGateOperatorEvent shown = Assert.Single(
            harness.OperatorEvents,
            item => item.Kind == "RECOVERY_BLOCKED" && item.Message.Contains(RecoveryNotOpenCode, StringComparison.Ordinal));
        Assert.StartsWith(RecoveryNotOpenSentence, shown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("凭据", shown.Message, StringComparison.Ordinal);
        Assert.True(harness.Business.IsRecoveryNotOpen);
        Assert.Equal(0, harness.Business.OperatorPressCountForTest);
        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(state.ExceptionRecoverySessionId);
        Assert.Null(state.RecoveryActionId);
        Assert.Null(state.RecoveryVector);

        // A new session generation runs the reconnect review, the one path that asks for a recovery by itself
        // (onboard-hmi#236). Its log line for this generation is the second fact: the review has concluded.
        long? firstGeneration = harness.Session.Current.SessionGeneration;
        WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);
        Assert.True(reconnected.SessionGeneration > firstGeneration, $"{reconnected.SessionGeneration} after {firstGeneration}");
        string reviewed = $"重连后检查授权请求：generation={reconnected.SessionGeneration}，";
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry => entry.Message.StartsWith(reviewed, StringComparison.Ordinal)),
            "the reconnect review of the new generation to conclude",
            token);

        Assert.Single(harness.ResultsOfType("ExceptionRecoverySessionRequested"));
        Assert.Equal(0, harness.Business.OperatorPressCountForTest);
        Assert.True(harness.Business.IsRecoveryNotOpen);
    }

    /// <summary>Once maintenance grants the authorization, the next press opens the session, and the notice goes.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task OnceAuthorizedTheNextPressOpensTheSessionAndTheNoticeGoes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartUnauthorizedAsync(token);
        Assert.False(await harness.Business.RequestResumeAfterRepairAsync(null, token));
        await harness.WaitForRecoveryBlockedAsync(RecoveryNotOpenCode, token);
        Assert.True(harness.Business.IsRecoveryNotOpen);

        harness.Server.RecoverySessionRejectionReasonCode = null;
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanRequestResumeAfterRepair,
            "the resume entry to be offered after the refusal",
            token);
        Assert.True(await harness.Business.RequestResumeAfterRepairAsync(null, token));

        Assert.False(harness.Business.IsRecoveryNotOpen);
        string[] requests = [.. harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "ExceptionRecoverySessionRequested")
            .Select(envelope => envelope.MessageId)];
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(requests[0], requests[1]);
        Assert.Single(harness.ResultsOfType("RecoveryActionSubmitted"));
        // The authorized press is the one press this process holds.
        Assert.Equal(1, harness.Business.OperatorPressCountForTest);
    }

    /// <summary>
    /// A later press refused for another reason is said for that reason: the dialog, the line beside the entry and its
    /// raw code do not go on saying recovery is not open (review M1, probe 1).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ALaterRefusalForAnotherReasonIsSaidForItsOwnReason()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartUnauthorizedAsync(token);
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await ResumeViewModelAsync(harness, controller);
        Assert.False(await viewModel.RequestWireToGateRecoveryAsync(token));
        await harness.WaitForRecoveryBlockedAsync(RecoveryNotOpenCode, token);
        Assert.True(viewModel.HasRecoveryNotOpenNotice);

        // Granted now; the next press is refused for another reason.
        harness.Server.RecoverySessionRejectionReasonCode = "RECOVERY_DEMAND_NOT_BLOCKED";
        Assert.False(await viewModel.RequestWireToGateRecoveryAsync(token));
        await harness.WaitForRecoveryBlockedAsync("RECOVERY_DEMAND_NOT_BLOCKED", token);

        Assert.Equal(RefusalOtherwise, viewModel.DescribeRecoveryRefusal(RefusalOtherwise));
        Assert.False(harness.Business.IsRecoveryNotOpen);
        Assert.False(viewModel.HasRecoveryNotOpenNotice);
        HmiRawCodeState notice = Assert.Single(
            HmiScreenStateBuilder.Build(viewModel, new ModalDialogRegistry()).RawCodes,
            code => code.AutomationId == "RecoveryNotOpenNotice");
        Assert.Equal((string.Empty, false), (notice.Value, notice.Visible));
    }

    /// <summary>
    /// An action outside what was authorized, refused inside the session the server opened (CP-0022): said as outside what
    /// was opened, not as recovery not open -- the session is open, and the line beside the entry would contradict
    /// 「会话已开」 (review M2, probe 2). The action is not submitted again.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AnActionRefusedInsideAnOpenSessionSaysItIsOutsideWhatWasOpened()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartWithActionsRefusedAsync(token);
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await ResumeViewModelAsync(harness, controller);

        Assert.False(await viewModel.RequestWireToGateRecoveryAsync(token));
        await harness.WaitForRecoveryBlockedAsync(RecoveryNotOpenCode, token);

        WireToGateOperatorEvent shown = Assert.Single(
            harness.OperatorEvents,
            item => item.Kind == "RECOVERY_BLOCKED" && item.Message.Contains(RecoveryNotOpenCode, StringComparison.Ordinal));
        Assert.StartsWith(RecoveryActionNotOpenSentence, shown.Message, StringComparison.Ordinal);
        Assert.NotNull((await harness.ReadRecoveryStateAsync(token)).ExceptionRecoverySessionId);
        Assert.False(harness.Business.IsRecoveryNotOpen);
        Assert.False(viewModel.HasRecoveryNotOpenNotice);
        Assert.Equal(RecoveryActionNotOpenSentence, viewModel.DescribeRecoveryRefusal(RefusalOtherwise));
        Assert.Single(harness.ResultsOfType("RecoveryActionSubmitted"));
    }

    /// <summary>
    /// After an action refused inside the session, the next press is refused locally for another reason: neither the flag
    /// nor the dialog keeps the earlier refusal (review M1, probe 3).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task APressRefusedForAnotherReasonAfterAnActionRefusalSaysTheNewReason()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartWithActionsRefusedAsync(token);
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await ResumeViewModelAsync(harness, controller);
        Assert.False(await viewModel.RequestWireToGateRecoveryAsync(token));
        await harness.WaitForRecoveryBlockedAsync(RecoveryNotOpenCode, token);

        harness.Server.RecoveryActionRejectionReasonCode = null;
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanRequestResumeAfterRepair,
            "the resume entry after the refused action",
            token);
        Assert.False(await viewModel.RequestWireToGateRecoveryAsync(token));
        await harness.WaitForRecoveryBlockedAsync("RECOVERY_SESSION_STATE_PENDING", token);

        Assert.False(harness.Business.IsRecoveryNotOpen);
        Assert.False(viewModel.HasRecoveryNotOpenNotice);
        Assert.Equal(RefusalOtherwise, viewModel.DescribeRecoveryRefusal(RefusalOtherwise));
    }

    /// <summary>
    /// A compensation's authorization request refused for want of authorization, inside its session: said in the
    /// reviewed words, the vector is released so the reconnect review has nothing to send again, and the not-open line
    /// stays off (the session was open).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationRefusedForWantOfAuthorizationSaysItWasNotOpenedAndIsReleased()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartForClosedReasonAsync(token);
        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await harness.WaitForInboundAsync("LoadCompensationRequested", token);
        string requestMessageId = harness.Server.ReceivedEnvelopes
            .Last(envelope => envelope.MessageType == "LoadCompensationRequested")
            .MessageId;

        await harness.Server.SendCommandAsync(
            "LoadCompensationRejected",
            "abcdabcd-0000-4000-8000-000000003910",
            new
            {
                recoveryActionId = prepared.RecoveryVector!.PrimaryId,
                problem = new
                {
                    reasonCode = RecoveryNotOpenCode,
                    fieldPath = "payload",
                    displayMessage = "Load compensation is not authorized."
                }
            },
            correlationId: requestMessageId);
        await harness.WaitForRecoveryBlockedAsync(RecoveryNotOpenCode, token);

        WireToGateOperatorEvent shown = Assert.Single(
            harness.OperatorEvents,
            item => item.Kind == "RECOVERY_BLOCKED" && item.Message.Contains(RecoveryNotOpenCode, StringComparison.Ordinal));
        Assert.StartsWith("补偿清空未获开放，未开仓门。", shown.Message, StringComparison.Ordinal);
        Assert.False(harness.Business.IsRecoveryNotOpen);
        Assert.Null((await harness.ReadRecoveryStateAsync(token)).RecoveryVector);
    }

    /// <summary>
    /// A session the server closes because its authorization ended (cs#797 review ruling): the close is said in the
    /// operator's words, the vehicle is not read as recovered, the entry comes back, and a session opened once the
    /// authorization is granted again clears the notice.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ASessionClosedForItsAuthorizationSaysSoAndTheNextSessionClearsTheNotice()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartForClosedReasonAsync(token);
        ConcurrentQueue<WireToGateOperatorEvent> events = RecordEvents(harness);
        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        Assert.False(harness.Business.IsRecoveryNotOpen);

        await SendReasonedClosedSnapshotAsync(harness, prepared, RecoveryNotOpenCode);
        await WaitForClosedEventAsync(events, token);

        Assert.Equal(AuthorizationClosedSentence, Assert.Single(ClosedEvents(events)).Message);
        Assert.True(harness.Business.IsRecoveryNotOpen);
        Assert.NotEqual(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
        Assert.False(harness.Business.RecoveryReasonAlreadyGiven);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCompensation,
            "the compensation entry to be offered again after the authorization closed the session",
            token);
        await AssertANewSessionCanBeOpenedAsync(harness, token);
        Assert.False(harness.Business.IsRecoveryNotOpen);
    }

    /// <summary>
    /// The CLOSED of the first session, replayed on a later session generation after a second session has opened, does
    /// not light the notice again (review L3).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AReplayedCloseOfAnEarlierSessionDoesNotLightTheNoticeAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartForClosedReasonAsync(token);
        ConcurrentQueue<WireToGateOperatorEvent> events = RecordEvents(harness);
        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await SendReasonedClosedSnapshotAsync(harness, prepared, RecoveryNotOpenCode);
        await WaitForClosedEventAsync(events, token);
        await AssertANewSessionCanBeOpenedAsync(harness, token);
        Assert.False(harness.Business.IsRecoveryNotOpen);

        WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);
        Assert.True(reconnected.Connected);
        int acknowledgedBefore = AcknowledgedRecoverySnapshots(harness)
            .Count(id => id == ReasonedClosedSnapshotMessageId);
        await SendReasonedClosedSnapshotAsync(harness, prepared, RecoveryNotOpenCode);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => AcknowledgedRecoverySnapshots(harness).Count(id => id == ReasonedClosedSnapshotMessageId)
                > acknowledgedBefore,
            "the replayed CLOSED to be applied on the new session",
            token);

        Assert.False(harness.Business.IsRecoveryNotOpen);
    }

    /// <summary>
    /// The notice belongs to the recovery it was said about: once the unsettled attempt it named is no longer the one on
    /// file, it is gone (review L2).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task TheNoticeGoesWhenTheUnsettledAttemptChanges()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartUnauthorizedAsync(token);
        Assert.False(await harness.Business.RequestResumeAfterRepairAsync(null, token));
        await harness.WaitForRecoveryBlockedAsync(RecoveryNotOpenCode, token);
        Assert.True(harness.Business.IsRecoveryNotOpen);

        await harness.Business.UpdateCachedRecoveryStateForTestAsync(
            state => state with { UnsettledSlotOperationAttemptId = null, OperationContext = null },
            token);

        Assert.False(harness.Business.IsRecoveryNotOpen);
    }

    /// <summary>
    /// The server closes with the code once the authorization has ended, even when the action got done first and its result
    /// came back (control-server#797 rework). The record says the compensation completed before it says the recovery ended,
    /// and no not-open line stands: the attempt is settled and no entry is offered.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACloseForTheAuthorizationAfterTheCompensationCompletedSaysItCompletedFirst()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartForCompensationToCompleteAsync(token);
        ConcurrentQueue<WireToGateOperatorEvent> events = RecordEvents(harness);
        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
        await harness.WaitForResultAsync("LoadCompensationResult", token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => events.Any(item => item.Kind == "RECOVERY_VECTOR_COMPLETED"),
            "the compensation to be settled as completed",
            token);

        await SendReasonedClosedSnapshotAsync(harness, prepared, RecoveryNotOpenCode);
        await WaitForClosedEventAsync(events, token);

        AssertCompletedIsSaidBeforeTheClose(events);
        AssertNoRecoveryEntryOffered(harness);
        Assert.False(harness.Business.IsRecoveryNotOpen);
    }

    /// <summary>
    /// The same close arriving right behind the result's acknowledgement, before the vehicle has settled it: the
    /// completion is still said, and before the close.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACloseForTheAuthorizationRightBehindTheResultStillSaysTheCompensationCompleted()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartForCompensationToCompleteAsync(token);
        ConcurrentQueue<WireToGateOperatorEvent> events = RecordEvents(harness);
        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
        await harness.WaitForResultAsync("LoadCompensationResult", token);

        await SendReasonedClosedSnapshotAsync(harness, prepared, RecoveryNotOpenCode);
        await WaitForClosedEventAsync(events, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => events.Any(item => item.Kind == "RECOVERY_VECTOR_COMPLETED"),
            "the compensation to be said completed",
            token);

        AssertCompletedIsSaidBeforeTheClose(events);
        AssertNoRecoveryEntryOffered(harness);
    }

    /// <summary>
    /// The screen: a line beside the recovery entries says recovery is not open, carries the raw code for the
    /// screen-state snapshot, and the refusal's dialog says the same; once a session opens it is gone.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task TheScreenSaysRecoveryIsNotOpenBesideTheEntryUntilASessionOpens()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartUnauthorizedAsync(token);
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await ResumeViewModelAsync(harness, controller);
        Assert.False(viewModel.HasRecoveryNotOpenNotice);
        Assert.Equal(RefusalOtherwise, viewModel.DescribeRecoveryRefusal(RefusalOtherwise));

        Assert.False(await viewModel.RequestWireToGateRecoveryAsync(token));
        await harness.WaitForRecoveryBlockedAsync(RecoveryNotOpenCode, token);

        Assert.True(viewModel.CanRequestWireToGateRecovery);
        Assert.True(viewModel.HasRecoveryNotOpenNotice);
        Assert.Equal(RecoveryNotOpenSentence, viewModel.RecoveryNotOpenNoticeText);
        Assert.Equal(RecoveryNotOpenSentence, viewModel.DescribeRecoveryRefusal(RefusalOtherwise));
        HmiRawCodeState notice = Assert.Single(
            HmiScreenStateBuilder.Build(viewModel, new ModalDialogRegistry()).RawCodes,
            code => code.AutomationId == "RecoveryNotOpenNotice");
        Assert.Equal((RecoveryNotOpenCode, true), (notice.Value, notice.Visible));

        harness.Server.RecoverySessionRejectionReasonCode = null;
        Assert.True(await viewModel.RequestWireToGateRecoveryAsync(token));

        Assert.False(viewModel.HasRecoveryNotOpenNotice);
        Assert.Equal(RefusalOtherwise, viewModel.DescribeRecoveryRefusal(RefusalOtherwise));
        notice = Assert.Single(
            HmiScreenStateBuilder.Build(viewModel, new ModalDialogRegistry()).RawCodes,
            code => code.AutomationId == "RecoveryNotOpenNotice");
        Assert.Equal((string.Empty, false), (notice.Value, notice.Visible));
    }

    private static void AssertCompletedIsSaidBeforeTheClose(IEnumerable<WireToGateOperatorEvent> events)
    {
        WireToGateOperatorEvent[] said = [.. events];
        int completed = Array.FindIndex(said, item => item.Kind == "RECOVERY_VECTOR_COMPLETED"
            && item.Message.StartsWith("补偿清空已完成", StringComparison.Ordinal));
        WireToGateOperatorEvent closed = Assert.Single(ClosedEvents(said));
        Assert.True(
            completed >= 0 && completed < Array.IndexOf(said, closed),
            string.Join(" / ", said.Select(item => $"{item.Kind}:{item.Message}")));
        Assert.Equal(AuthorizationClosedSentence, closed.Message);
    }

    private static void AssertNoRecoveryEntryOffered(RecoveryVectorHarness harness) =>
        Assert.Equal(
            (false, false, false, false),
            (harness.Business.CanRequestResumeAfterRepair,
                harness.Business.CanRequestLoadCompensation,
                harness.Business.CanRequestFaultCargoHandoff,
                harness.Business.CanRequestForcedMechanicalRecovery));

    /// <summary>A view model on the resume entry, wired the way <c>App.xaml.cs</c> wires the not-open line.</summary>
    private static async Task<MainViewModel> ResumeViewModelAsync(RecoveryVectorHarness harness, OnboardController controller)
    {
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => false,
            () => harness.Business.CanRequestResumeAfterRepair,
            harness.Business.RequestResumeAfterRepairAsync);
        RecoveryNotOpenWiring.Configure(viewModel, harness.Business);
        harness.Business.OperatorEventPublished += (_, args) => viewModel.ApplyWireToGateOperatorEvent(args.Value);
        return viewModel;
    }

    private static Task<RecoveryVectorHarness> StartUnauthorizedAsync(CancellationToken token) =>
        RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.RecoverySessionRejectionReasonCode = RecoveryNotOpenCode;
            },
            cargoInTargetSlots: true);

    private static Task<RecoveryVectorHarness> StartWithActionsRefusedAsync(CancellationToken token) =>
        RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.RecoveryActionRejectionReasonCode = RecoveryNotOpenCode;
            },
            cargoInTargetSlots: true);

    /// <summary>Empty target slots, so the compensation command can run to <c>ALL_EMPTY</c>.</summary>
    private static Task<RecoveryVectorHarness> StartForCompensationToCompleteAsync(CancellationToken token) =>
        RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
            });
}
