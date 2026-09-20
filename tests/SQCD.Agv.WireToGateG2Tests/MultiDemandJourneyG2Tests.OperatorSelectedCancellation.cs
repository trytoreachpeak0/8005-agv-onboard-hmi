using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The cancellation before any sublot at a stop carrying more than one demand: which demand it
/// cancels is the operator's pick in the worklist, not this vehicle's inference (batch 7-14,
/// <c>trytoreachpeak0/8005-agv-onboard-hmi#135</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the pick has to come from the operator.</b> One stop is one
/// <c>OperationSession</c> and the server serializes its demands through it, so the worklist carries
/// the session at its top and nothing per item (spec section 5.2 and the section 22 addendum). The
/// vehicle therefore cannot read off which item the outstanding entry request belongs to. Batch 7-13
/// (<c>onboard-hmi#134</c>) closed the entry rather than guess; this reopens it with the operator
/// naming the demand, which keeps <c>NEVER_DISCOVER_SELECT_OR_BIND_DEMAND</c> intact -- the vehicle
/// still discovers nothing and binds nothing, and the server validates the pick
/// (<c>control-server#211</c>).
/// </para>
/// <para>
/// <b>What the vehicle still decides alone.</b> A press that repeats a request already sent takes
/// its demand from the journal, never from today's selection or from the first row of whatever
/// worklist a restart found: the server compares a retry's whole payload with the one it authorized,
/// so a retry naming another demand is a different request under the same id and drops the
/// connection.
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// Two items, B picked: the request names B, no door opens, and nothing about A is asked of the
    /// server. The vehicle's own answer is <c>ALL_EMPTY</c> with no slot results, as for one item.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ThePickedDemandIsTheOneCancelledAndTheOtherIsNotNamedAtAll()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartTwoItemStopAsync(cancellationToken: token);
        SelectWorklistItem(harness, DemandB);

        Assert.True(await harness.ViewModel.RequestLoadCancellationAsync(token));

        JsonElement request = Assert.Single(Received(harness, "LoadCancellationStartRequested"));
        Assert.Equal(DemandB, request.GetProperty("demandId").GetString());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("slotOperationAttemptId").ValueKind);

        JsonElement result = Assert.Single(Received(harness, "LoadCancellationResult"));
        Assert.Equal(DemandB, result.GetProperty("demandId").GetString());
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(0, result.GetProperty("slotResults").GetArrayLength());

        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.Server.RecoveryRequestConflicts);
        Assert.Empty(harness.UiErrors);
        // A is never the subject of anything this press produced: no request, no result, no vector.
        Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.WireLine.Contains(DemandA, StringComparison.Ordinal));
    }

    /// <summary>
    /// Two items and nothing picked: the entry is offered but cannot be pressed, the hint says to
    /// pick first, and a press that reaches the business service anyway sends nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task WithTwoItemsAndNothingPickedTheEntryIsDisabledAndNothingIsSent()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartTwoItemStopAsync(cancellationToken: token);

        Assert.Null(harness.ViewModel.SelectedWorklistItem);
        Assert.True(harness.ViewModel.CanRequestLoadCancellation);
        Assert.False(harness.ViewModel.CanPressLoadCancellation);
        Assert.True(harness.ViewModel.HasLoadCancellationSelectionHint);
        Assert.Equal(
            "请先在清单中选择要取消的任务",
            harness.ViewModel.LoadCancellationSelectionHintText);

        Assert.False(await harness.ViewModel.RequestLoadCancellationAsync(token));

        Assert.Empty(Received(harness, "LoadCancellationStartRequested"));
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
        // Sublot entry stays open: nothing went out, so nothing is waiting for an answer.
        Assert.True(harness.Business.CanSubmitSublot);

        SelectWorklistItem(harness, DemandA);
        Assert.True(harness.ViewModel.CanPressLoadCancellation);
        Assert.False(harness.ViewModel.HasLoadCancellationSelectionHint);
    }

    /// <summary>
    /// One item: the press needs no pick, and passing one changes not a byte of the request.
    /// </summary>
    /// <remarks>
    /// The comparison is the server's own, not a transcription of today's payload into an assertion:
    /// the first press's answer is dropped, so the second press is a retry under the same
    /// <c>cancellationId</c>, and the fake binds a workflow id to the exact bytes it first carried
    /// (<c>BindRecoveryWorkflowContent</c>, after <c>UpsertSimpleWorkflowAsync</c>). Any difference
    /// -- including the demand -- lands in <c>RecoveryRequestConflicts</c>.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task WithOneItemNoPickIsNeededAndPassingOneChangesNothingOnTheWire()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartOneItemStopAsync(
            server => server.LoadCancellationAuthorizationsToDrop = 1,
            token);

        Assert.Null(harness.ViewModel.SelectedWorklistItem);
        Assert.True(harness.ViewModel.CanPressLoadCancellation);
        Assert.False(harness.ViewModel.HasLoadCancellationSelectionHint);

        // The first press goes out the way it did before this ticket: no demand named by the caller.
        Assert.False(await harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token));

        SelectWorklistItem(harness, DemandA);
        Assert.True(await harness.ViewModel.RequestLoadCancellationAsync(token));

        (string MessageId, string WireLine)[] requests =
        [
            .. harness.Server.ReceivedEnvelopes
                .Where(envelope => envelope.MessageType == "LoadCancellationStartRequested")
                .Select(envelope => (envelope.MessageId, envelope.WireLine))
        ];
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(requests[0].MessageId, requests[1].MessageId);
        Assert.Equal(PayloadText(requests[0].WireLine), PayloadText(requests[1].WireLine));
        Assert.Empty(harness.Server.RecoveryRequestConflicts);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// The answer is lost, the vehicle restarts, and the operator presses again: the retry repeats
    /// the first press's bytes and still names B -- not the worklist's first row, which a restart
    /// would otherwise make the obvious guess. Nothing is picked after the restart.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task AcrossARestartTheRetryStillNamesThePickedDemandWithNothingPicked()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        await using Harness first = await StartTwoItemStopAsync(
            journalPath,
            server => server.LoadCancellationAuthorizationsToDrop = 1,
            cancellationToken: token);
        SelectWorklistItem(first, DemandB);
        Assert.False(await first.ViewModel.RequestLoadCancellationAsync(token));
        await first.StopVehicleAsync();
        first.Server.SimulateOnboardProcessRestart();

        await using Harness afterRestart = await Harness.StartAgainstAsync(
            first.Server,
            token,
            journalPath,
            NewIoWithCargo());
        // Waited on the unanswered cancellation being back in the entry gates' cache, which is what
        // makes a pick unnecessary -- not on the entry merely being offered, which it also is while
        // the cache still reads empty and the stop therefore still looks like "pick one first".
        await afterRestart.WaitUntilAsync(
            () => afterRestart.Business.IsLoadCancellationBeforeSublotOpen,
            "the restarted vehicle to read its unanswered cancellation back from the journal",
            token);
        // What the entry request and an operator event do in the product, done once here.
        afterRestart.ViewModel.RefreshWireToGateInputState();

        // The restarted process has no selection, and the entry is pressable all the same: the
        // subject is the journal's, not a new pick.
        Assert.Null(afterRestart.ViewModel.SelectedWorklistItem);
        Assert.True(afterRestart.ViewModel.CanPressLoadCancellation);
        Assert.False(afterRestart.ViewModel.HasLoadCancellationSelectionHint);

        // Picked the *other* demand before pressing: a retry must ignore it. Asserting only "nothing
        // was picked" would leave the interesting failure -- today's pick overwriting the journal's
        // subject -- untested, because null is exactly what a broken implementation would fall back
        // from.
        SelectWorklistItem(afterRestart, DemandA);
        Assert.True(await afterRestart.ViewModel.RequestLoadCancellationAsync(token));

        (string MessageId, string WireLine)[] requests =
        [
            .. first.Server.ReceivedEnvelopes
                .Where(envelope => envelope.MessageType == "LoadCancellationStartRequested")
                .Select(envelope => (envelope.MessageId, envelope.WireLine))
        ];
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(requests[0].MessageId, requests[1].MessageId);
        Assert.Equal(PayloadText(requests[0].WireLine), PayloadText(requests[1].WireLine));
        using JsonDocument retried = JsonDocument.Parse(requests[1].WireLine);
        Assert.Equal(
            DemandB,
            retried.RootElement.GetProperty("payload").GetProperty("demandId").GetString());
        Assert.Empty(first.Server.RecoveryRequestConflicts);
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        // On the screen, not merely in the event stream: the whole point of this line is that the
        // operator has A highlighted while B is what goes out, and a message he never sees fixes
        // nothing.
        Assert.Contains(
            afterRestart.ViewModel.Logs,
            line => line.Message.Contains("这一次按下是它的重发", StringComparison.Ordinal));
    }

    /// <summary>A stop of these items, with the fake answering cancellation requests.</summary>
    private static void ConfigureStop(
        FakeControlServer server,
        string[] expectedSublots,
        object[] items,
        Action<FakeControlServer>? configure = null)
    {
        server.SendJourneySnapshotsAfterRecovery = true;
        // A restart replays the same revision; without this the vehicle reads the replay as a
        // conflicting revision of its own.
        server.ReplayJourneySnapshotsWithStableIdentity = true;
        server.RespondToLoadCancellationRequests = true;
        server.SublotEntryExpectedSublots = expectedSublots;
        server.JourneySnapshotPayloads = new Dictionary<string, object>
        {
            ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
            ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, items),
            ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
        };
        configure?.Invoke(server);
    }

    private static FakeIoModuleClient NewIoWithCargo()
    {
        // Cargo in the target slots: a clear would unlock, so UnlockCount == 0 means it never ran.
        FakeIoModuleClient io = new();
        io.SetCargoPresent(0, true);
        io.SetCargoPresent(1, true);
        return io;
    }

    private static Task<Harness> StartTwoItemStopAsync(
        string? journalPath = null,
        Action<FakeControlServer>? configure = null,
        bool awaitSublotEntry = true,
        CancellationToken cancellationToken = default) =>
        StartStopAsync(
            ["SUBLOT-A", "SUBLOT-B"],
            [Payloads.ItemA, Payloads.ItemB],
            journalPath,
            configure,
            awaitSublotEntry,
            cancellationToken);

    private static Task<Harness> StartOneItemStopAsync(
        Action<FakeControlServer>? configure = null,
        CancellationToken cancellationToken = default) =>
        StartStopAsync(
            ["SUBLOT-A"],
            [Payloads.ItemA],
            journalPath: null,
            configure,
            awaitSublotEntry: true,
            cancellationToken);

    private static async Task<Harness> StartStopAsync(
        string[] expectedSublots,
        object[] items,
        string? journalPath,
        Action<FakeControlServer>? configure,
        bool awaitSublotEntry,
        CancellationToken cancellationToken)
    {
        Harness harness = await Harness.StartAsync(
            server => ConfigureStop(server, expectedSublots, items, configure),
            cancellationToken,
            journalPath,
            NewIoWithCargo());
        try
        {
            // Two waits, not one: a single condition over both would report "the worklist never
            // arrived" for an entry that is simply not offered, which is the defect these tests are
            // about.
            // A seeded pending cancellation shuts sublot entry, so a test that starts from one waits
            // on the worklist alone and on its own signal for the seed having landed.
            await harness.WaitUntilAsync(
                () => (harness.Business.ExpectedSublots is not null || !awaitSublotEntry)
                    && harness.ViewModel.WorklistItems.Count == items.Length,
                $"the {items.Length}-item worklist and the entry request to arrive",
                cancellationToken);
            await harness.WaitUntilAsync(
                () => harness.Business.CanRequestLoadCancellation,
                "the cancellation-before-sublot entry to be offered",
                cancellationToken);
            harness.ViewModel.RefreshWireToGateInputState();
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Picks a worklist row the way the operator does, retrying across a rebuild for the reason
    /// <see cref="Harness.WorklistRows"/> gives.
    /// </summary>
    private static void SelectWorklistItem(Harness harness, string demandId)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                harness.ViewModel.SelectedWorklistItem =
                    harness.ViewModel.WorklistItems.Single(row => row.DemandId == demandId);
                return;
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or ArgumentOutOfRangeException
                    && attempt < 100)
            {
                Thread.Sleep(5);
            }
        }
    }

    private static IReadOnlyList<JsonElement> Received(Harness harness, string messageType) =>
    [
        .. harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == messageType)
            .Select(envelope => Payload(envelope.WireLine))
    ];

    private static string PayloadText(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("payload").GetRawText();
    }

    private static JsonElement Payload(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("payload").Clone();
    }
}
