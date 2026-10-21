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
/// recovery authorization; without one the server refuses the session with <c>RECOVERY_AUTHENTICATION_FAILED</c>, and a
/// session whose authorization expires or is withdrawn is closed with that code as its <c>closedReason</c>
/// (ADR-cross-0066 decision 5, CP-0022 section 3). The screen says recovery is not open and whom to call -- not that a
/// credential was wrong, which read as a broken vehicle -- and a refusal is not asked again by the vehicle.
/// </summary>
public sealed partial class RecoveryVectorG2Tests
{
    private const string RecoveryNotOpenCode = "RECOVERY_AUTHENTICATION_FAILED";

    private const string RecoveryNotOpenSentence = "恢复尚未开放，请联系系统维护人员。";

    private const string AuthorizationClosedSentence =
        "服务端恢复会话已关闭：恢复授权已失效（到期、被收回或服务端重启）。需要继续恢复请联系系统维护人员重新开放。";

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
    /// An action outside what was authorized is refused with the same code (CP-0022): the same sentence, and the action is
    /// not submitted again.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AnActionRefusedForWantOfAuthorizationSaysRecoveryIsNotOpen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.RecoveryActionRejectionReasonCode = RecoveryNotOpenCode;
            },
            cargoInTargetSlots: true);

        Assert.False(await harness.Business.RequestResumeAfterRepairAsync(null, token));
        await harness.WaitForRecoveryBlockedAsync(RecoveryNotOpenCode, token);

        WireToGateOperatorEvent shown = Assert.Single(
            harness.OperatorEvents,
            item => item.Kind == "RECOVERY_BLOCKED" && item.Message.Contains(RecoveryNotOpenCode, StringComparison.Ordinal));
        Assert.StartsWith(RecoveryNotOpenSentence, shown.Message, StringComparison.Ordinal);
        Assert.True(harness.Business.IsRecoveryNotOpen);
        Assert.Single(harness.ResultsOfType("RecoveryActionSubmitted"));
    }

    /// <summary>
    /// A session the server closes because its authorization expired or was withdrawn (cs#797 review ruling): the close
    /// is said in the operator's words, the vehicle is not read as recovered, the entry comes back, and a session opened
    /// once the authorization is granted again clears the notice.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-RECOVERY-SESSION-CLOSED-RESULT-NOT-RECONCILED")]
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
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);
        viewModel.ConfigureWireToGate(
            (_, _, _) => Task.CompletedTask,
            () => false,
            () => harness.Business.CanRequestResumeAfterRepair,
            harness.Business.RequestResumeAfterRepairAsync);
        RecoveryNotOpenWiring.Configure(viewModel, harness.Business);
        harness.Business.OperatorEventPublished += (_, args) => viewModel.ApplyWireToGateOperatorEvent(args.Value);
        const string Otherwise = "恢复申请未被接受。";
        Assert.False(viewModel.HasRecoveryNotOpenNotice);
        Assert.Equal(Otherwise, viewModel.DescribeRecoveryRefusal(Otherwise));

        Assert.False(await viewModel.RequestWireToGateRecoveryAsync(token));
        await harness.WaitForRecoveryBlockedAsync(RecoveryNotOpenCode, token);

        Assert.True(viewModel.CanRequestWireToGateRecovery);
        Assert.True(viewModel.HasRecoveryNotOpenNotice);
        Assert.Equal(RecoveryNotOpenSentence, viewModel.RecoveryNotOpenNoticeText);
        Assert.Equal(RecoveryNotOpenSentence, viewModel.DescribeRecoveryRefusal(Otherwise));
        HmiRawCodeState notice = Assert.Single(
            HmiScreenStateBuilder.Build(viewModel, new ModalDialogRegistry()).RawCodes,
            code => code.AutomationId == "RecoveryNotOpenNotice");
        Assert.Equal((RecoveryNotOpenCode, true), (notice.Value, notice.Visible));

        harness.Server.RecoverySessionRejectionReasonCode = null;
        Assert.True(await viewModel.RequestWireToGateRecoveryAsync(token));

        Assert.False(viewModel.HasRecoveryNotOpenNotice);
        Assert.Equal(Otherwise, viewModel.DescribeRecoveryRefusal(Otherwise));
        notice = Assert.Single(
            HmiScreenStateBuilder.Build(viewModel, new ModalDialogRegistry()).RawCodes,
            code => code.AutomationId == "RecoveryNotOpenNotice");
        Assert.Equal((string.Empty, false), (notice.Value, notice.Visible));
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
}
