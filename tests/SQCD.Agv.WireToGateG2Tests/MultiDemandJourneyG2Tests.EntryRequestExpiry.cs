using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// When the vehicle stops honouring a <c>SublotEntryRequested</c>: the stop it was made for has ended
/// (<c>expiresOnRevisionChange</c>), or a load command has answered it
/// (<c>trytoreachpeak0/8005-agv-onboard-hmi#199</c>, program#86 on v2).
/// </summary>
/// <remarks>
/// <para>
/// <b>What was wrong.</b> The entry request was dropped in three places only -- the session leaving
/// Ready, a rejection naming another session or revision, and an acknowledged cancellation before any
/// sublot. A server that ended the stop early (station deadline, the last demand cancelled) and said
/// so with a newer, empty worklist left the scan entry open and the expected sublots in place; a stop
/// loaded to the end left them open too, through the whole cargo-holding wait.
/// </para>
/// <para>
/// <b>"The stop ended" and "the stop moved on" are two different answers, and both are asserted.</b>
/// A newer worklist that is empty, or names another operation session or station, ends the stop: the
/// request goes. A newer worklist under the same operation session with items left is the same stop
/// working through its demands one by one, and the control server still accepts an entry made against
/// any revision this stop has issued (<c>StopEntryAddress.Covers</c>, control-server
/// <c>JourneyStopCursor.cs</c>); dropping the request there would shut the entry for nothing.
/// </para>
/// <para>
/// <b>Every assertion that matters is on the view model.</b> <c>MainViewModel</c> reads the entry gates
/// only when an event reaches it, and in <c>App.xaml.cs</c> its <c>JourneyChanged</c> handler is
/// subscribed before the business service's. A business service that drops the request on the worklist
/// and publishes nothing has <c>CanSubmitSublot</c> false while the screen goes on offering the scan.
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>The line the operator reads when the stop's entry request is withdrawn.</summary>
    private const string EntryWithdrawnLine = "本站作业已结束，录入请求已撤销，不再接收扫码。";

    /// <summary>The operation session of the stop after <c>ST-01</c>.</summary>
    private const string NextOperationSessionId = "88888888-8888-4888-8888-888888888888";

    /// <summary>
    /// The A-form closure control-server#323 sends: a higher revision, no items, no operation session,
    /// no deadline. The entry request goes, and the screen says the stop has nothing left.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AHigherRevisionEmptyWorklistWithdrawsTheEntryRequestOnScreen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartOneItemStopAsync(
            configure: server => server.JourneySnapshotPayloads = new Dictionary<string, object>
            {
                ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                // A live deadline first, so "无倒计时" afterwards is the closure's doing and not the default.
                ["CurrentStopWorklistSnapshot"] = Payloads.WorklistAt(
                    1,
                    DateTimeOffset.UtcNow.AddMinutes(10),
                    Payloads.ItemA),
                ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
            },
            cancellationToken: token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanSubmit && harness.ViewModel.CanRequestLoadCancellation,
            "the scan entry and the cancel-before-scan entry to be on screen",
            token);
        Assert.NotEqual(StationDepartureCountdownFormatter.AbsentText, harness.ViewModel.StationDepartureCountdownText);

        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            ClosureWorklist(revision: 2));

        // Waited on the screen, not on the session: the session stores the snapshot before it raises
        // JourneyChanged, so "revision 2 applied" is true a moment before the withdrawal has run at all --
        // and on a busy machine the assertions below landed in that moment.
        await WaitForEntryWithdrawnOnScreenAsync(harness, token);
        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                Assert.False(harness.ViewModel.CanSubmit, "the scan entry is still open on screen");
                Assert.False(
                    harness.ViewModel.CanRequestLoadCancellation,
                    "the cancel-before-scan entry is still on screen");
                Assert.Equal("ST-01 / 无待处理任务", harness.ViewModel.VisitText);
                Assert.Equal(StationDepartureCountdownFormatter.AbsentText, harness.ViewModel.StationDepartureCountdownText);
            },
            token);
        Assert.Null(harness.Business.ExpectedSublots);
        Assert.False(harness.Business.CanSubmitSublot);
        Assert.Single(OperatorLog(harness), line => line == EntryWithdrawnLine);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The B-form control-server#324 sends: the stop ends and the journey goes on, so the next worklist
    /// has items and names another operation session and station. The request made for the last stop goes.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ANewerWorklistForAnotherOperationSessionWithdrawsTheEntryRequestOnScreen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartOneItemStopAsync(cancellationToken: token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanSubmit,
            "the scan entry to be on screen",
            token);

        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            new
            {
                stationId = "ST-GATE",
                worklistRevision = 2,
                operationSessionId = NextOperationSessionId,
                stationDepartureDeadlineAt = (DateTimeOffset?)null,
                items = new[]
                {
                    Payloads.Item(DemandA, "TD-A", "SUBLOT-A", "WIRE_TO_GATE", "DROPOFF", 2)
                }
            });

        await WaitForEntryWithdrawnOnScreenAsync(harness, token);
        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                Assert.False(harness.ViewModel.CanSubmit, "the last stop's scan entry is still open on screen");
                Assert.False(harness.ViewModel.CanRequestLoadCancellation);
            },
            token);
        Assert.Null(harness.Business.ExpectedSublots);
        Assert.Single(OperatorLog(harness), line => line == EntryWithdrawnLine);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The counter-case: the same stop working through its demands. One of two demands leaves, and the
    /// server sends the next revision of the worklist and of the entry request under the same operation
    /// session. Nothing is withdrawn -- not the request in hand while the worklist is ahead of it, and
    /// not the new one -- whichever of the two messages arrives first.
    /// </summary>
    /// <remarks>
    /// <paramref name="requestFirst"/> false is the control server's own order
    /// (<c>JourneyRuntimeEngine</c> publishes the worklist, then the entry request). True is the order a
    /// replay can produce, and it is the one a "the worklist changed, drop the request" rule gets wrong:
    /// it would drop the request that has just arrived.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AnAdvancingRevisionUnderTheSameOperationSessionDoesNotWithdrawTheEntryRequest(bool requestFirst)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartTwoItemStopAsync(cancellationToken: token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanSubmit,
            "the scan entry to be on screen",
            token);

        if (requestFirst)
        {
            await SendEntryRequestAsync(harness, revision: 2, SublotAOnly);
            await harness.WaitUntilAsync(
                () => harness.Business.ExpectedSublots is ["SUBLOT-A"],
                "the revision-2 entry request to be taken",
                token);

            // The request is now ahead of the worklist (still revision 1). The server accepts it -- its
            // revision is the stop's current one -- so the vehicle sends it (PR #200 second review, L4).
            harness.ViewModel.ScanText = "SUBLOT-A";
            harness.ViewModel.ScannerSubmitCommand.Execute(null);
            JsonElement ahead = await harness.WaitForSubmissionAsync(token);
            Assert.Equal("SUBLOT-A", ahead.GetProperty("sublot").GetString());
            Assert.Equal(2, ahead.GetProperty("worklistRevision").GetInt64());
        }

        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            Payloads.Worklist(2, Payloads.ItemA));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist?.Revision == 2,
            "the revision-2 worklist to be applied",
            token);

        // Held, not read once: the withdrawal this guards against would come a moment after the worklist.
        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                Assert.True(harness.ViewModel.CanSubmit, "the scan entry was shut by a same-stop revision");
                Assert.NotNull(harness.Business.ExpectedSublots);
            },
            token);

        if (!requestFirst)
        {
            // The entry kept open here has to be one that works, not only a live button (PR #200 review,
            // the user's decision of 2026-09-22 on #199): A, still on the stop, scanned on screen against
            // the revision-1 request, leaves the vehicle and is not refused.
            harness.ViewModel.ScanText = "SUBLOT-A";
            harness.ViewModel.ScannerSubmitCommand.Execute(null);
            JsonElement submitted = await harness.WaitForSubmissionAsync(token);
            Assert.Equal("SUBLOT-A", submitted.GetProperty("sublot").GetString());
            Assert.Equal(1, submitted.GetProperty("worklistRevision").GetInt64());
            Assert.DoesNotContain(
                harness.Server.SentEnvelopes,
                envelope => envelope.MessageType == "SublotRejected");

            await SendEntryRequestAsync(harness, revision: 2, SublotAOnly);
            await harness.WaitUntilAsync(
                () => harness.Business.ExpectedSublots is ["SUBLOT-A"],
                "the revision-2 entry request to be taken",
                token);
        }

        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                Assert.True(harness.ViewModel.CanSubmit, "the revision-2 entry request is not on screen");
                Assert.Equal(["SUBLOT-A"], harness.Business.ExpectedSublots);
            },
            token);
        Assert.DoesNotContain(EntryWithdrawnLine, OperatorLog(harness));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The entry the counter-case keeps open is one that works: between the same stop's revision-2 worklist
    /// and its revision-2 request, a sublot still on the stop scanned on screen reaches the server against
    /// the revision-1 request -- which the server accepts (<c>StopEntryAddress.Covers</c>) -- and a sublot
    /// that has left the stop is refused on the vehicle and never sent.
    /// </summary>
    /// <remarks>
    /// Until PR #200's review the vehicle's own check demanded the worklist and the request be at the same
    /// revision, so in this window the button was live and every scan was refused locally as
    /// <c>SUBLOT_NOT_IN_WORKLIST</c>. Keeping the request (the user's decision, recorded on #199) is only
    /// right if the scan it offers goes through; this is what says it does.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AScanAfterASameStopRevisionAdvanceReachesTheServerAgainstTheRequestInHand()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartTwoItemStopAsync(cancellationToken: token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanSubmit,
            "the scan entry to be on screen",
            token);

        // B leaves the stop; the revision-2 request has not arrived yet.
        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            Payloads.Worklist(2, Payloads.ItemA));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist?.Revision == 2,
            "the revision-2 worklist to be applied",
            token);

        // B is no longer on the stop: refused here, nothing sent.
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Business.SubmitSublotAsync("SUBLOT-B", "SCANNER", token));
        Assert.Equal("SUBLOT_NOT_IN_WORKLIST", refused.Message);

        // A, scanned the way the operator scans it.
        harness.ViewModel.ScanText = "SUBLOT-A";
        Assert.True(harness.ViewModel.ScannerSubmitCommand.CanExecute(null));
        harness.ViewModel.ScannerSubmitCommand.Execute(null);

        JsonElement submitted = await harness.WaitForSubmissionAsync(token);
        Assert.Equal("SUBLOT-A", submitted.GetProperty("sublot").GetString());
        Assert.Equal(1, submitted.GetProperty("worklistRevision").GetInt64());
        Assert.Equal(OperationSessionId, submitted.GetProperty("operationSessionId").GetString());
        Assert.Single(harness.Submissions);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The window's scan is refused by the server for a reason of its own, naming the revision it has
    /// moved to. The stop is the same, so the request stays and the operator is told to scan again -- not
    /// "wait for a new request" (PR #200 second review, L1).
    /// </summary>
    /// <remarks>
    /// Until then a rejection kept the request only at the request's own revision, so after a same-stop
    /// advance every server refusal withdrew an entry the user decided must stay (#199).
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SUBLOT-REJECTED-AFTER-ENTRY")]
    public async Task AServerRefusalAfterASameStopRevisionAdvanceKeepsTheEntryOpenForARescan()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartTwoItemStopAsync(
            configure: server =>
            {
                server.RejectSublotSubmissionsWith = "SUBLOT_BOX_COUNT_UNAVAILABLE";
                server.RejectionWorklistRevision = 2;
            },
            cancellationToken: token);
        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            Payloads.Worklist(2, Payloads.ItemA));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist?.Revision == 2,
            "the revision-2 worklist to be applied",
            token);

        harness.ViewModel.ScanText = "SUBLOT-A";
        harness.ViewModel.ScannerSubmitCommand.Execute(null);
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentSublotRejection is not null,
            "the server's refusal to reach the vehicle",
            token);

        Assert.True(harness.Business.CurrentSublotRejection!.EntryRequestKept);
        Assert.Contains(OperatorLog(harness), line => line.Contains("请核对物料后重新扫码", StringComparison.Ordinal));
        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                Assert.True(harness.ViewModel.CanSubmit, "the refusal withdrew a same-stop entry");
                Assert.NotNull(harness.Business.ExpectedSublots);
            },
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The other side of the keep rule (PR #200 delta review, F1): the server refuses at a newer revision
    /// while the worklist in front of the vehicle is not the request's stop -- here the next stop's request
    /// (same operation session, another station, as a single-demand journey's drop-off has) arrived before
    /// its worklist, and the vehicle still shows the pickup's. The request is not kept.
    /// </summary>
    /// <remarks>
    /// Without this case nothing exercised <c>keep == false</c> after the flip: forcing <c>keep = true</c>
    /// left every rejection test green (measured in the delta review).
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SUBLOT-REJECTED-AFTER-ENTRY")]
    public async Task ARefusalAtANewerRevisionWhileTheWorklistIsAnotherStationDoesNotKeepTheRequest()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartOneItemStopAsync(cancellationToken: token);
        await harness.Server.SendCommandAsync(
            "SublotEntryRequested",
            Guid.NewGuid().ToString("D"),
            new
            {
                operationSessionId = OperationSessionId,
                stationId = "ST-GATE",
                worklistRevision = 3,
                expectedSublots = SublotAOnly,
                entryMethods = FrozenEntryMethods,
                expiresOnRevisionChange = true
            });
        await harness.WaitUntilAsync(
            () => harness.Business.ExpectedSublots is ["SUBLOT-A"]
                && harness.Session.CurrentJourney.CurrentStopWorklist?.StationId == "ST-01",
            "the next stop's request to be taken while the pickup's worklist is still shown",
            token);

        await harness.Server.SendCommandAsync(
            "SublotRejected",
            Guid.NewGuid().ToString("D"),
            new
            {
                demandId = (string?)null,
                operationSessionId = OperationSessionId,
                problem = new
                {
                    reasonCode = "EXPECTED_BASKET_COUNT_MISMATCH",
                    fieldPath = (string?)null,
                    displayMessage = (string?)null
                },
                currentWorklistRevision = 4,
                rejectedSublot = "SUBLOT-A"
            },
            Guid.NewGuid().ToString("D"));
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentSublotRejection is not null,
            "the refusal to reach the vehicle",
            token);

        Assert.False(harness.Business.CurrentSublotRejection!.EntryRequestKept);
        Assert.False(harness.Business.CanSubmitSublot);
        Assert.Contains(
            OperatorLog(harness),
            line => line.Contains("等待服务端新的录入请求", StringComparison.Ordinal));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A refusal as <c>WORKLIST_REVISION_STALE</c> says the stop has ended, and may arrive before the
    /// closure snapshot does (PR #200 delta review, F2). The request is not kept, whatever the worklist in
    /// front of the vehicle still says, so the operator is not told both "no more scans" and "scan again".
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SUBLOT-REJECTED-AFTER-ENTRY")]
    public async Task AStaleWorklistRefusalWithdrawsTheRequestEvenBeforeTheClosureSnapshot()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartOneItemStopAsync(
            configure: server =>
            {
                server.RejectSublotSubmissionsWith = "WORKLIST_REVISION_STALE";
                server.RejectionWorklistRevision = 2;
            },
            cancellationToken: token);

        harness.ViewModel.ScanText = "SUBLOT-A";
        harness.ViewModel.ScannerSubmitCommand.Execute(null);
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentSublotRejection is not null,
            "the stale refusal to reach the vehicle",
            token);

        Assert.False(harness.Business.CurrentSublotRejection!.EntryRequestKept);
        Assert.False(harness.Business.CanSubmitSublot);
        string refusal = Assert.Single(
            OperatorLog(harness),
            line => line.Contains("本站作业已结束，不再接收扫码", StringComparison.Ordinal));
        Assert.DoesNotContain("请核对物料后重新扫码", refusal, StringComparison.Ordinal);
        await AssertWhileAsync(
            DisplaySettleWindow,
            () => Assert.False(harness.ViewModel.CanSubmit, "a stale refusal left the scan entry open"),
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// In the same window the cancel-before-scan entry is offered too, and pressing it asks the server about
    /// the demand still on the stop (PR #200 second review, L2). The server's cancellation path accepts the
    /// stop's revision range as its entry path does (<c>LoadCancellationBeforeSublot.AnswersTheStop</c>).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task TheCancelBeforeScanEntryStaysOfferedAcrossASameStopRevisionAdvance()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartTwoItemStopAsync(cancellationToken: token);
        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            Payloads.Worklist(2, Payloads.ItemA));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist?.Revision == 2
                && harness.WorklistRows() is [("SUBLOT-A", _)],
            "the revision-2 worklist to be on screen",
            token);

        await AssertWhileAsync(
            DisplaySettleWindow,
            () => Assert.True(
                harness.ViewModel.CanRequestLoadCancellation,
                "the cancel-before-scan entry was hidden by a same-stop revision"),
            token);

        await harness.ViewModel.RequestLoadCancellationAsync(token);
        JsonElement request = Assert.Single(Received(harness, "LoadCancellationStartRequested"));
        Assert.Equal(DemandA, request.GetProperty("demandId").GetString());
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// An entry request that arrives after the worklist has already ended its stop -- a replay racing the
    /// closure snapshot -- is not taken: the entry stays shut.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AnEntryRequestArrivingAfterItsStopHasEndedIsNotTaken()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartOneItemStopAsync(cancellationToken: token);
        await harness.Server.SendJourneySnapshotAsync("CurrentStopWorklistSnapshot", ClosureWorklist(revision: 2));
        await WaitForEntryWithdrawnOnScreenAsync(harness, token);

        await SendEntryRequestAsync(harness, revision: 1, ["SUBLOT-A"]);
        await harness.WaitUntilAsync(
            () => harness.Server.SentEnvelopes.Count(envelope => envelope.MessageType == "SublotEntryRequested") >= 2,
            "the late entry request to have been sent",
            token);

        // Held: sent is not yet handled, and a taken request would open the entry a moment later.
        await AssertWhileAsync(
            DisplaySettleWindow,
            () =>
            {
                Assert.False(harness.ViewModel.CanSubmit, "a request for an ended stop reopened the scan entry");
                Assert.Null(harness.Business.ExpectedSublots);
            },
            token);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The ticket's third point, checked rather than assumed: the stop's only demand is scanned, loaded
    /// and acknowledged, and the server -- which sends nothing more for a stop with nothing outstanding --
    /// moves into the cargo-holding wait. The load command answered the entry request, so the scan entry
    /// is shut from the load onwards and stays shut through the wait.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task TheLoadCommandThatAnswersTheEntryRequestWithdrawsItThroughTheCargoHoldingWait()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true, KeepSnapshotFresh = true };
        await using Harness harness = await Harness.StartAsync(
            server => ConfigureStop(server, ["SUBLOT-A"], [Payloads.ItemA]),
            token,
            io: io);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanSubmit,
            "the scan entry to be on screen",
            token);

        await harness.Business.SubmitSublotAsync("SUBLOT-A", "SCANNER", token);
        await harness.WaitForSubmissionAsync(token);
        string submissionId = SubmissionMessageId(harness);
        await harness.Server.SendCommandAsync(
            "SlotOperationCommand",
            Guid.NewGuid().ToString("D"),
            SlotCommand(DemandA, AttemptA, [1]),
            submissionId);
        await harness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator
                && io.UnlockCount == 1,
            "slot 1 to be unlocked for the load",
            token);
        Assert.False(harness.ViewModel.CanSubmit, "the scan entry is still open while its load is running");

        io.CloseDoor(0, cargo: true);
        await harness.WaitUntilAsync(
            () => OperatorLog(harness).Contains("1号仓操作结果已被服务端确认。"),
            "the load's result to be acknowledged",
            token);
        await harness.Server.SendJourneySnapshotAsync(
            "VehicleBusinessStateSnapshot",
            Payloads.BusinessState(
                2,
                Payloads.LoadingPhase("CARGO_HOLDING_WAIT", DateTimeOffset.UtcNow.AddMinutes(10))));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.HasCargoHoldingCountdown,
            "the cargo-holding wait to be on screen",
            token);

        await AssertWhileAsync(
            DisplaySettleWindow,
            () => Assert.False(harness.ViewModel.CanSubmit, "the scan entry reopened after the stop was loaded"),
            token);
        Assert.Null(harness.Business.ExpectedSublots);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A cancellation before any sublot that reaches the server after the stop has ended is refused as
    /// <c>WORKLIST_REVISION_STALE</c> (control-server#324), and the operator reads what that means rather
    /// than the bare code.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ACancellationRefusedAsStaleTellsTheOperatorTheStopHasEnded()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartOneItemStopAsync(
            configure: server =>
            {
                server.LoadCancellationDecision = "REJECTED";
                server.LoadCancellationRejectionReasonCode = "WORKLIST_REVISION_STALE";
            },
            cancellationToken: token);

        Assert.False(await harness.ViewModel.RequestLoadCancellationAsync(token));

        await harness.WaitUntilAsync(
            () => OperatorLog(harness).Any(line => line.StartsWith("服务端拒绝装货取消", StringComparison.Ordinal)),
            "the refusal to reach the operator",
            token);
        Assert.Contains(
            "服务端拒绝装货取消：本站作业已结束，无需再取消（WORKLIST_REVISION_STALE）。",
            OperatorLog(harness).Select(line => line.Trim()));
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The scan entry is shut on screen and the withdrawal has been announced. Both, because the entry can
    /// also be shut by some other refresh that happens to read the business gate after the request was
    /// dropped; only the announcement says the withdrawal itself reached the view.
    /// </summary>
    private static Task WaitForEntryWithdrawnOnScreenAsync(Harness harness, CancellationToken token) =>
        harness.WaitUntilAsync(
            () => !harness.ViewModel.CanSubmit && OperatorLog(harness).Contains(EntryWithdrawnLine),
            "the scan entry to be shut on screen and the withdrawal announced",
            token);

    /// <summary>The closure worklist of control-server#323's shape at <paramref name="revision"/>.</summary>
    private static object ClosureWorklist(long revision) =>
        new
        {
            stationId = "ST-01",
            worklistRevision = revision,
            operationSessionId = (string?)null,
            stationDepartureDeadlineAt = (DateTimeOffset?)null,
            items = Array.Empty<object>()
        };

    private static Task SendEntryRequestAsync(Harness harness, long revision, string[] expectedSublots) =>
        harness.Server.SendCommandAsync(
            "SublotEntryRequested",
            Guid.NewGuid().ToString("D"),
            new
            {
                operationSessionId = OperationSessionId,
                stationId = "ST-01",
                worklistRevision = revision,
                expectedSublots,
                entryMethods = FrozenEntryMethods,
                expiresOnRevisionChange = true
            });

    /// <summary>The messageId of the one <c>SublotSubmitted</c> the server has taken, which a LOAD answers.</summary>
    private static string SubmissionMessageId(Harness harness)
    {
        using JsonDocument document = JsonDocument.Parse(Assert.Single(harness.Submissions));
        return document.RootElement.GetProperty("messageId").GetString()!;
    }
}
