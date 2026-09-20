using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// What happens to the other demand when the picked one's cancellation is refused, disappears, is
/// pressed twice, or settles (batch 7-14, <c>trytoreachpeak0/8005-agv-onboard-hmi#135</c>).
/// </summary>
/// <remarks>
/// The thread through all of these is that a stop now carries two demands where the code was written
/// for one, so every "nothing else changed" that used to be vacuous is now a claim about B while A is
/// the subject. Each test names the other demand explicitly rather than asserting a count.
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// The server refuses the picked demand: its reason code reaches the operator verbatim, no door
    /// opens, the pending record goes so the next press is a new first press, sublot entry reopens,
    /// and nothing was ever asked about the demand that was not picked.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ARefusalOfThePickedDemandLeavesTheOtherDemandUntouched()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartTwoItemStopAsync(
            configure: server => server.LoadCancellationDecision = "REJECTED",
            cancellationToken: token);
        SelectWorklistItem(harness, DemandB);

        Assert.False(await harness.ViewModel.RequestLoadCancellationAsync(token));

        await harness.WaitUntilAsync(
            () => harness.Events.Any(item =>
                item.Kind == "RECOVERY_BLOCKED"
                && item.Message.Contains("ACTION_NOT_ALLOWED_IN_STATE", StringComparison.Ordinal)),
            "the server's reason code to reach the operator",
            token);

        JsonElement request = Assert.Single(Received(harness, "LoadCancellationStartRequested"));
        Assert.Equal(DemandB, request.GetProperty("demandId").GetString());
        Assert.Empty(Received(harness, "LoadCancellationResult"));
        Assert.Equal(0, harness.Io.UnlockCount);
        // The pending record is gone, so the next press is a new first press -- and sublot entry,
        // which the outstanding cancellation had closed, is open again for both demands.
        Assert.False(harness.Business.IsLoadCancellationBeforeSublotOpen);
        Assert.True(harness.Business.CanSubmitSublot);
        await harness.Business.SubmitSublotAsync("SUBLOT-A", "SCANNER", token);
        JsonElement submitted = await harness.WaitForSubmissionAsync(token);
        Assert.Equal("SUBLOT-A", submitted.GetProperty("sublot").GetString());

        // Nothing is pinned to B any more: the next press is free to name the other demand, and does.
        // A record that outlived the refusal would turn this press into a retry of B's request.
        SelectWorklistItem(harness, DemandA);
        Assert.False(await harness.ViewModel.RequestLoadCancellationAsync(token));
        Assert.Equal(
            [DemandB, DemandA],
            Received(harness, "LoadCancellationStartRequested")
                .Select(item => item.GetProperty("demandId").GetString()));
    }

    /// <summary>
    /// A demand this vehicle loaded earlier but not last: the local exclusions cannot see it, so the
    /// press goes out and the server is the one that refuses it.
    /// </summary>
    /// <remarks>
    /// <c>LastCompletedLoadOperationContext</c> keeps only the last load, which is the whole reason
    /// the correction window closes. The same fact makes an earlier load invisible to the local
    /// check, and this end does not try to reconstruct what it did not keep: it asks, and the server
    /// -- which has every command it issued -- says no. Pinning it here so that a later change
    /// reading the exclusion as "this vehicle has never loaded it" is caught.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ADemandLoadedEarlierButNotLastIsAskedAboutAndRefusedByTheServer()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        // B was the last load; A was loaded before it and left no trace here.
        string journalPath = await NewJournalSeededAsync(
            WireToGateRecoveryState.Empty with
            {
                ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ResultRecorded,
                LastCompletedLoadOperationContext = SettledLoadOf(DemandB)
            },
            token);
        await using Harness harness = await StartTwoItemStopAsync(
            journalPath,
            server => server.LoadCancellationDecision = "REJECTED",
            token);

        // B is excluded locally -- the vehicle knows it loaded it.
        SelectWorklistItem(harness, DemandB);
        Assert.False(await harness.ViewModel.RequestLoadCancellationAsync(token));
        Assert.Empty(Received(harness, "LoadCancellationStartRequested"));

        SelectWorklistItem(harness, DemandA);
        Assert.False(await harness.ViewModel.RequestLoadCancellationAsync(token));

        JsonElement request = Assert.Single(Received(harness, "LoadCancellationStartRequested"));
        Assert.Equal(DemandA, request.GetProperty("demandId").GetString());
        await harness.WaitUntilAsync(
            () => harness.Events.Any(item =>
                item.Message.Contains("ACTION_NOT_ALLOWED_IN_STATE", StringComparison.Ordinal)),
            "the server's refusal of the earlier-loaded demand",
            token);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// A demand that is not on this stop's worklist is refused here, not passed on: the view decides
    /// what the operator picked, but it does not get to decide what this vehicle asks the server about.
    /// </summary>
    /// <remarks>
    /// In the product the view has usually cleared the selection by now -- a replaced worklist drops a
    /// row that is gone, see
    /// <c>MultiDemandViewModelTests.AWorklistThatDropsThePickedRowClearsTheSelection</c> -- so this is
    /// the layer underneath that: between the snapshot reaching the business service and reaching the
    /// view there is a window, and a demand id arriving from anywhere is checked against the worklist
    /// before a byte goes out. Nothing is sent, nothing throws, and the operator is asked to pick again.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task APickThatIsNotOnThisStopsWorklistSendsNothingAndAsksForANewPick()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartTwoItemStopAsync(cancellationToken: token);

        Assert.False(await harness.Business.RequestLoadCancellationAsync(
            "选中的那条已经不在了，还是按了一次。",
            "dddddddd-0000-4000-8000-00000000000d",
            token));

        Assert.Empty(Received(harness, "LoadCancellationStartRequested"));
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
        Assert.True(harness.Business.CanSubmitSublot);
        Assert.Contains(
            harness.Events,
            item => item.Kind == "RECOVERY_BLOCKED"
                && item.Message.Contains("请重新选择", StringComparison.Ordinal));
        // And the stop is still fully cancellable once a real row is named.
        Assert.True(await harness.Business.RequestLoadCancellationAsync(
            "重新选了一条。",
            DemandB,
            token));
        Assert.Equal(
            DemandB,
            Assert.Single(Received(harness, "LoadCancellationStartRequested"))
                .GetProperty("demandId").GetString());
    }

    /// <summary>
    /// Two presses racing on the picked demand ask about one cancellation, not two: the same
    /// cancellationId and the same bytes, so the server sees a retry rather than a second request.
    /// </summary>
    /// <remarks>
    /// The gate in <c>RunRecoveryRequestAsync</c> serialises them, so what this pins is not that one
    /// of them is dropped -- both are allowed to reach the wire -- but that the second is built from
    /// the first press's journaled record. A second <c>cancellationId</c> would mean the stop has two
    /// open cancellations, which is what <c>RecoveryRequestConflicts</c> would not even catch.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task TwoPressesRacingOnThePickedDemandAskAboutOneCancellation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartTwoItemStopAsync(
            configure: server => server.LoadCancellationAuthorizationsToDrop = 2,
            cancellationToken: token);
        SelectWorklistItem(harness, DemandB);

        Task<bool> first = harness.ViewModel.RequestLoadCancellationAsync(token);
        Task<bool> second = harness.ViewModel.RequestLoadCancellationAsync(token);
        Assert.False(await first);
        Assert.False(await second);

        JsonElement[] requests = [.. Received(harness, "LoadCancellationStartRequested")];
        Assert.Equal(2, requests.Length);
        Assert.Equal(
            requests[0].GetProperty("cancellationId").GetString(),
            requests[1].GetProperty("cancellationId").GetString());
        Assert.Equal(requests[0].GetRawText(), requests[1].GetRawText());
        Assert.All(requests, request => Assert.Equal(DemandB, request.GetProperty("demandId").GetString()));
        Assert.Empty(harness.Server.RecoveryRequestConflicts);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// Settling a cancellation drops the outstanding entry request unless the new worklist proves it
    /// belongs to another demand -- unchanged by this ticket, pinned here because two items make the
    /// two branches reachable for the first time.
    /// </summary>
    /// <param name="worklistStillNamesTheCancelledDemand">
    /// Whether the worklist that arrives after the acknowledgement still lists the cancelled demand.
    /// </param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task SettlingKeepsTheEntryRequestOnlyWhenTheNewWorklistIsAboutAnotherDemand(
        bool worklistStillNamesTheCancelledDemand)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        // The first acknowledgement is dropped, so the cancellation is authorized and its result
        // sent but not settled: that is the window in which the worklist can be replaced.
        await using Harness harness = await StartTwoItemStopAsync(
            configure: server => server.LoadCancellationResultAcksToDrop = 1,
            cancellationToken: token);
        SelectWorklistItem(harness, DemandA);

        Assert.False(await harness.ViewModel.RequestLoadCancellationAsync(token));
        Assert.True(harness.Business.IsLoadCancellationBeforeSublotOpen);
        Assert.NotNull(harness.Business.ExpectedSublots);

        if (!worklistStillNamesTheCancelledDemand)
        {
            // The server has ended A's part of the stop; B alone is left, under a new revision --
            // the same revision with different content is a conflict this vehicle refuses outright.
            await harness.Server.SendJourneySnapshotAsync(
                "CurrentStopWorklistSnapshot",
                Payloads.Worklist(2, Payloads.ItemB));
            await harness.WaitUntilAsync(
                () => harness.Session.CurrentJourney.CurrentStopWorklist?.Items is [{ } only]
                    && only.DemandId == DemandB,
                "the worklist without A to arrive",
                token);
        }

        // The second press resends the same result; this time it is acknowledged and settles.
        Assert.True(await harness.ViewModel.RequestLoadCancellationAsync(token));

        Assert.False(harness.Business.IsLoadCancellationBeforeSublotOpen);
        Assert.Equal(
            worklistStillNamesTheCancelledDemand,
            harness.Business.ExpectedSublots is null);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// The session drops between the readiness check and the send: nothing left the vehicle, so the
    /// pick is not written down as a first press either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Were it written down, the stop would be pinned to that demand for as long as the record
    /// lasted: every later press -- including one made after picking the other row -- would be read
    /// as a retry of a request the server has never seen, and would go out naming the wrong demand.
    /// </para>
    /// <para>
    /// <b>The other half of this claim is pinned elsewhere, on purpose.</b> "The next press is free
    /// to name a different demand" needs a session again, and this fake cannot drop a connection
    /// without being disposed -- a second fake replays the same journey messageIds into a journal
    /// that already has them, which the vehicle refuses as a content conflict and drops the link
    /// (measured here). Both refusal and non-readiness clear the record through the same
    /// <c>ForgetLoadCancellationRequestAsync</c>, so
    /// <see cref="ARefusalOfThePickedDemandLeavesTheOtherDemandUntouched"/> carries that half within
    /// one session, and the single-demand mechanics are
    /// <c>RecoveryVectorG2Tests.ACancellationPressedWhileTheSessionIsNotReadyIsNotRememberedAsTheFirstPress</c>.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task APressThatCouldNotBeSentLeavesThePickUnremembered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        await using Harness dropped = await StartTwoItemStopAsync(journalPath, cancellationToken: token);
        SelectWorklistItem(dropped, DemandB);
        await dropped.StopServerAsync();
        await dropped.WaitUntilAsync(
            () => !dropped.Business.CanRequestLoadCancellation,
            "the session to drop once the control server is gone",
            token);

        Assert.False(await dropped.ViewModel.RequestLoadCancellationAsync(token));

        Assert.DoesNotContain(
            dropped.Server.ReceivedEnvelopes,
            envelope => envelope.MessageType == "LoadCancellationStartRequested");
        Assert.False(dropped.Business.IsLoadCancellationBeforeSublotOpen);
        await dropped.StopVehicleAsync();
        WireToGateRecoveryState journaled = await ReadRecoveryStateAsync(journalPath, token);
        Assert.Null(journaled.PendingLoadCancellation);
        Assert.Null(journaled.RecoveryVector);
    }

    private static async Task<WireToGateRecoveryState> ReadRecoveryStateAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        SqliteWireToGateJournal journal = new(journalPath);
        return await journal.ReadRecoveryStateAsync(cancellationToken);
    }

    /// <summary>
    /// The demand of a cancellation already sent is gone from the stop: the press is <b>not</b>
    /// redirected to the demand that is still there. Nothing goes out, and the operator is told the
    /// answer is the server's to give.
    /// </summary>
    /// <remarks>
    /// This is the rule this ticket adds that is easiest to break by accident, because the obvious
    /// implementation -- "find the item this cancellationId matches, else fall back to the pick" --
    /// looks reasonable and silently cancels the wrong task. The server keeps one workflow per
    /// cancellationId; a press that reused that id for another demand would be a different request
    /// under the same identity, and the demand the operator never chose would be the one cancelled.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ASentCancellationWhoseDemandLeftTheStopIsNotRedirectedToTheOtherDemand()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartTwoItemStopAsync(
            configure: server => server.LoadCancellationAuthorizationsToDrop = 1,
            cancellationToken: token);
        SelectWorklistItem(harness, DemandB);
        Assert.False(await harness.ViewModel.RequestLoadCancellationAsync(token));
        Assert.True(harness.Business.IsLoadCancellationBeforeSublotOpen);

        // The stop becomes A alone, with the entry request reissued for the new revision -- the pair
        // the real server sends together, and what makes the new worklist the one this press reads.
        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            Payloads.Worklist(2, Payloads.ItemA));
        await harness.Server.SendCommandAsync(
            "SublotEntryRequested",
            Guid.NewGuid().ToString("D"),
            new
            {
                operationSessionId = OperationSessionId,
                stationId = "ST-01",
                worklistRevision = 2,
                expectedSublots = SublotAOnly,
                entryMethods = FrozenEntryMethods,
                expiresOnRevisionChange = true
            });
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist?.Revision == 2
                && harness.Business.ExpectedSublots is ["SUBLOT-A"],
            "the stop to become A alone, entry request included",
            token);

        Assert.False(await harness.ViewModel.RequestLoadCancellationAsync(token));

        // Still exactly the one request, still about B: A was never asked about.
        JsonElement request = Assert.Single(Received(harness, "LoadCancellationStartRequested"));
        Assert.Equal(DemandB, request.GetProperty("demandId").GetString());
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
        Assert.Contains(
            harness.Events,
            item => item.Kind == "RECOVERY_BLOCKED"
                && item.Message.Contains("取消结果以服务端为准", StringComparison.Ordinal));
    }

    /// <summary>
    /// The fallback recovery entries name the last settled load only while nothing is armed: with an
    /// operation armed the subject is that operation, even though the settled load is still on file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pinned on the business service, not only through the view model: the view is handed the answer,
    /// so a view-model test that feeds it a demand id proves the formatting and nothing about the rule.
    /// </para>
    /// <para>
    /// <b>The armed case is seeded, not driven.</b> Driving a real command to the armed state and
    /// asserting <c>null</c> there passed against an implementation that ignored the arming entirely
    /// -- measured, see the injection evidence -- because by then the cached state no longer carried
    /// the settled load, so both a correct and a broken reading returned <c>null</c>. A state written
    /// with <i>both</i> present is the only shape in which the two readings differ, and that is the
    /// shape a restart restores after an armed command followed a completed load.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheFallbackTargetIsTheSettledLoadOnlyWhileNothingIsArmed(bool armed)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WireToGateRecoveryOperationContext settled = SettledLoadOf(DemandB);
        WireToGateRecoveryOperationContext inFlight = SettledLoadOf(DemandA);
        string journalPath = await NewJournalSeededAsync(
            WireToGateRecoveryState.Empty with
            {
                ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ResultRecorded,
                LastCompletedLoadOperationContext = settled,
                OperationContext = armed ? inFlight : null,
                UnsettledSlotOperationAttemptId = armed ? inFlight.SlotOperationAttemptId : null
            },
            token);
        await using Harness harness = await Harness.StartAsync(
            server => ConfigureStop(server, ["SUBLOT-A", "SUBLOT-B"], [Payloads.ItemA, Payloads.ItemB]),
            token,
            journalPath,
            new FakeIoModuleClient { OperatorNeverActs = true });
        // Both cases wait on the same fact -- the seeded state having reached the cache the entries
        // read -- reported by whichever of the two the seed makes observable.
        await harness.WaitUntilAsync(
            () => armed
                ? harness.Business.CurrentOperationSnapshot is not null
                : harness.Business.RecoveryFallbackDemandId is not null,
            "the seeded recovery state to reach the entry gates' cache",
            token);

        Assert.Equal(armed ? null : DemandB, harness.Business.RecoveryFallbackDemandId);
        harness.ViewModel.RefreshWireToGateInputState();
        Assert.Equal(!armed, harness.ViewModel.HasRecoveryFallbackTarget);
        Assert.Equal(
            armed ? string.Empty : "目标：子批 SUBLOT-B",
            harness.ViewModel.RecoveryFallbackTargetText);
    }

    /// <summary><c>SublotEntryRequested</c> freezes these as a <c>const</c> array in its schema.</summary>
    private static readonly string[] FrozenEntryMethods = ["SCANNER", "KEYBOARD"];

    private static readonly string[] SublotAOnly = ["SUBLOT-A"];

    /// <summary>Writes a journal with this recovery state already in it, for a stop that starts mid-story.</summary>
    private static async Task<string> NewJournalSeededAsync(
        WireToGateRecoveryState seed,
        CancellationToken cancellationToken)
    {
        string path = Harness.NewJournalPath();
        SqliteWireToGateJournal journal = new(path);
        await journal.InitializeAsync(cancellationToken);
        await journal.WriteRecoveryStateAsync(seed, cancellationToken);
        return path;
    }

    private static WireToGateRecoveryOperationContext SettledLoadOf(string demandId) =>
        new(
            Guid.NewGuid().ToString("D"),
            null,
            1,
            DateTimeOffset.UtcNow,
            demandId,
            OperationSessionId,
            Guid.NewGuid().ToString("D"),
            OperationType.Load,
            [1, 2],
            2,
            true,
            new string('0', 64));
}
