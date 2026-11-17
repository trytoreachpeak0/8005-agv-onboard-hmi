using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;
using ScriptStep = SQCD.Agv.WireToGateG2Tests.FakeIoModuleClient.ScriptStep;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The onboard half of <c>CV-WORKLIST-EQP-DISPLAY-ONLY</c> (protocol 7.0.0, <c>8005-agv-protocol#29</c>,
/// 8005-agv-onboard-hmi#417): each worklist item carries MES's machine number, <c>eqp</c>, and the vehicle shows it beside
/// the sublot on the stop's worklist -- at a pickup, where the operator scans, and at a drop-off, where the operator unloads
/// -- and does nothing else with it. One test per onboard outcome the vector names:
/// <c>DISPLAY_EQP_BESIDE_SUBLOT_ON_PICKUP_SCAN_AND_UNLOAD_LISTS</c>, <c>DISPLAY_NULL_EQP_AS_ABSENT_WITHOUT_BLOCKING</c> and
/// <c>NEVER_DECIDE_BY_WORKLIST_EQP</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait.</b> The vector is frozen onto <c>FP-IS-09</c> alone, and this line does not
/// implement <c>FP-IS-09</c>: its other two vectors, the worklist selection, are scheduled into batch 11
/// (<c>ProtocolVectorTestBindingArchitectureTests.VectorsAwaitingTheirSlice</c>).
/// <c>IntegrationSliceTraitArchitectureTests</c> requires a test's slice traits to be exactly the projection of its
/// vectors onto the slices this line implements, which here is none.
/// </para>
/// <para>
/// The pickup scan list and the unload list are one control on this screen, <c>WorklistItems</c>, filled from the
/// current stop's worklist; the stop decides which of the two it is. So both are read the same way here: the row the
/// view model builds (<see cref="WorklistItemRow.Eqp"/>), which <c>MainWindow.xaml</c> binds beside the sublot, and
/// <see cref="TheMachineNumberSitsBesideTheSublotInTheWorklistRowAndNowhereElse"/> pins that binding.
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// A machine number with leading, inner and trailing spaces: shown as sent, not trimmed.
    /// </summary>
    private const string MachineNumberWithSpaces = " N1-03 机台 07 ";

    /// <summary>
    /// <c>DISPLAY_EQP_BESIDE_SUBLOT_ON_PICKUP_SCAN_AND_UNLOAD_LISTS</c>: at the pickup each row shows its own item's machine
    /// number verbatim; at the drop-off that follows, the unload list's row shows the drop-off item's.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-WORKLIST-EQP-DISPLAY-ONLY")]
    public async Task TheMachineNumberIsShownVerbatimBesideTheSublotOnThePickupScanListAndTheUnloadList()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartStopAsync(
            ["SUBLOT-A", "SUBLOT-B"],
            [
                Payloads.Item(DemandA, "TD-A", "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2, eqp: MachineNumberWithSpaces),
                Payloads.Item(DemandB, "TD-B", "SUBLOT-B", "WIRE_TO_OPTICAL", "PICKUP", 1, eqp: "N2-11")
            ],
            journalPath: null,
            configure: null,
            awaitSublotEntry: true,
            token);

        WorklistItemRow[] pickup = harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray());
        Assert.Equal(
            [("SUBLOT-A", MachineNumberWithSpaces), ("SUBLOT-B", "N2-11")],
            pickup.Select(row => (row.Sublot, row.Eqp)));
        Assert.True(harness.ViewModel.CanSubmit);

        await harness.Server.SendJourneySnapshotAsync(
            "CurrentStopWorklistSnapshot",
            new
            {
                stationId = "ST-GATE",
                worklistRevision = 2,
                operationSessionId = NextOperationSessionId,
                stationDepartureDeadlineAt = (DateTimeOffset?)null,
                stopEndedReason = (string?)null,
                items = new[] { Payloads.Item(DemandA, "TD-A", "SUBLOT-A", "WIRE_TO_GATE", "DROPOFF", 2, eqp: "N9-01") }
            });
        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray()) is [{ DemandId: DemandA, Eqp: "N9-01" }],
            "the drop-off worklist's row to show the drop-off item's machine number",
            token);

        Assert.Equal("SUBLOT-A", harness.OnUi(() => harness.ViewModel.WorklistItems.Single().Sublot));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>DISPLAY_NULL_EQP_AS_ABSENT_WITHOUT_BLOCKING</c> at the pickup: every item without a machine number shows none,
    /// and the scan is offered, taken and sent exactly as it is with one.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-WORKLIST-EQP-DISPLAY-ONLY")]
    public async Task ANullMachineNumberShowsNothingAndTheScanGoesThrough()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartStopAsync(
            ["SUBLOT-A", "SUBLOT-B"],
            [
                Payloads.Item(DemandA, "TD-A", "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2, eqp: null),
                Payloads.Item(DemandB, "TD-B", "SUBLOT-B", "WIRE_TO_OPTICAL", "PICKUP", 1, eqp: null)
            ],
            journalPath: null,
            configure: null,
            awaitSublotEntry: true,
            token);

        WorklistItemRow[] rows = harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray());
        Assert.Equal(["SUBLOT-A", "SUBLOT-B"], rows.Select(row => row.Sublot));
        Assert.All(rows, row => Assert.Null(row.Eqp));
        Assert.All(rows, row => Assert.True(row is { OffersCancellation: true, CanPressCancellation: true }));

        harness.ViewModel.ScanText = "SUBLOT-A";
        Assert.True(harness.ViewModel.ScannerSubmitCommand.CanExecute(null));
        harness.ViewModel.ScannerSubmitCommand.Execute(null);

        JsonElement submitted = await harness.WaitForSubmissionAsync(token);
        Assert.Equal("SUBLOT-A", submitted.GetProperty("sublot").GetString());
        Assert.Single(harness.Submissions);
        Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, envelope => envelope.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>DISPLAY_NULL_EQP_AS_ABSENT_WITHOUT_BLOCKING</c> at the drop-off: the unload list's row shows no machine number,
    /// and the unload the control server commands opens the door and completes.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-WORKLIST-EQP-DISPLAY-ONLY")]
    public async Task ANullMachineNumberShowsNothingAndTheUnloadGoesThrough()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.SetCargoPresent(0, present: true);
        io.ScriptPulse(1, OpensThenShutEmpty());
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.SendDropoffStopSnapshots = true;
                server.SendSlotOperationCommandAfterRecovery = true;
                server.SlotOperationType = "UNLOAD";
                server.JourneyWorklistEqp = null;
            },
            token,
            io: io,
            operationTimeout: TimeSpan.FromSeconds(2),
            unlockFeedbackTimeout: TimeSpan.FromSeconds(3),
            feedbackStableWindow: TimeSpan.FromMilliseconds(300));

        await harness.WaitUntilAsync(
            () => harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray()) is [{ Sublot: "SUBLOT-001" }],
            "the drop-off worklist on screen",
            token);
        Assert.Null(harness.OnUi(() => harness.ViewModel.WorklistItems.Single().Eqp));

        await harness.WaitUntilAsync(
            () => OperationResultLine(harness) is not null,
            "the unload's OperationResult",
            token);
        using JsonDocument result = JsonDocument.Parse(OperationResultLine(harness)!);
        JsonElement payload = result.RootElement.GetProperty("payload");
        Assert.Equal("COMPLETED", payload.GetProperty("overallOutcome").GetString());
        Assert.Equal(
            "EMPTY",
            Assert.Single(payload.GetProperty("slotResults").EnumerateArray()).GetProperty("finalPhysicalState").GetString());
        Assert.Equal(1, io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>NEVER_DECIDE_BY_WORKLIST_EQP</c>: the same stop run with machine numbers and without behaves the same in every
    /// way but the text beside the sublot. The machine numbers are chosen to tempt a matcher: A's is B's sublot, and B's is
    /// a sublot that is on no row.
    /// </summary>
    /// <remarks>
    /// Compared: the rows with the machine number taken out, the scan entry the vehicle offers, the local verdict on three
    /// scans (B's machine number scanned as if it were a sublot, then B's sublot -- which is also A's machine number --
    /// then A's), and the <c>SublotSubmitted</c> lines the vehicle sends, with each message's own identity and time
    /// taken out.
    /// </remarks>
    [Fact]
    [Trait("ProtocolVector", "CV-WORKLIST-EQP-DISPLAY-ONLY")]
    public async Task NoDecisionOnTheVehicleReadsTheMachineNumber()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        StopRun with = await RunStopAsync(eqpA: "SUBLOT-B", eqpB: "SUBLOT-Z", token);
        StopRun without = await RunStopAsync(eqpA: null, eqpB: null, token);

        Assert.Equal([("SUBLOT-A", "SUBLOT-B"), ("SUBLOT-B", "SUBLOT-Z")], with.Shown);
        Assert.Equal([("SUBLOT-A", (string?)null), ("SUBLOT-B", null)], without.Shown);
        Assert.Equal(without.RowsWithoutEqp, with.RowsWithoutEqp);
        Assert.Equal(without.Entry, with.Entry);
        Assert.Equal(without.Verdicts, with.Verdicts);
        Assert.Equal("SUBLOT-Z refused SUBLOT_NOT_IN_WORKLIST", with.Verdicts[0]);
        Assert.Equal("SUBLOT-B accepted", with.Verdicts[1]);
        Assert.Equal(without.Sent, with.Sent);
        Assert.NotEmpty(with.Sent);
    }

    /// <summary>
    /// The row template binds the machine number in exactly one place: a text beside the sublot, after it, so the row's
    /// first text is still the sublot (the real-rig L2 reads a row that way), and no other element of the window binds it.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-WORKLIST-EQP-DISPLAY-ONLY")]
    public void TheMachineNumberSitsBesideTheSublotInTheWorklistRowAndNowhereElse()
    {
        System.Xml.Linq.XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        System.Xml.Linq.XElement window = System.Xml.Linq.XElement.Load(System.IO.Path.Combine(
            ProtocolRepositoryRoot(), "src", "SQCD.Agv.Wpf", "MainWindow.xaml"));

        System.Xml.Linq.XElement[] bindingEqp =
        [
            .. window.DescendantsAndSelf()
                .Where(element => element.Attributes().Any(attribute => BindsEqp(attribute.Value)))
        ];
        System.Xml.Linq.XElement shown = Assert.Single(bindingEqp, element => (string?)element.Attribute("Text") == "{Binding Eqp}");
        Assert.All(bindingEqp, element => Assert.Same(shown, element));
        Assert.Equal(presentation + "TextBlock", shown.Name);

        System.Xml.Linq.XElement list = Assert.Single(
            shown.Ancestors(presentation + "ListBox"),
            element => (string?)element.Attribute("AutomationProperties.AutomationId") == "WorklistItems");
        System.Xml.Linq.XElement[] texts = [.. list.Descendants(presentation + "TextBlock")];
        Assert.Equal("{Binding Sublot}", (string?)texts[0].Attribute("Text"));
        Assert.Same(texts[0].Parent, shown.Parent);
        Assert.Same(shown, texts[1]);

        // Nothing between the row template and the text may hide it (review L2: a Visibility="Collapsed" on it kept every
        // other assertion here green). The template's own elements, from the text up to the DataTemplate.
        string[] hiding =
        [
            .. new[] { shown }.Concat(shown.Ancestors().TakeWhile(element => element.Name != presentation + "DataTemplate"))
                .SelectMany(element => element.Attributes()
                    .Where(attribute => attribute.Name.LocalName is "Visibility" or "Opacity"
                        || attribute.Name.LocalName.EndsWith(".Visibility", StringComparison.Ordinal))
                    .Select(attribute => $"{element.Name.LocalName} {attribute.Name.LocalName}={attribute.Value}"))
        ];
        Assert.Empty(hiding);

        static bool BindsEqp(string value) =>
            value.Contains("Binding Eqp", StringComparison.Ordinal)
            || value.Contains("Path=Eqp", StringComparison.Ordinal);
    }

    /// <summary>
    /// A vehicle upgraded with a pre-7.0.0 worklist still in its journal (review M1): the applied row has items and no
    /// <c>eqp</c>. Restarting on this build restores it before the handshake, then takes the server's next revision with
    /// <c>eqp</c>, and the row shows that revision's machine number. Today this rests on the deserializer reading a missing
    /// member as <c>null</c>; a stricter read (required constructor parameters, an inbound required-field check) would
    /// throw on the restore and keep the vehicle from starting, and this is the test that says so.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-WORKLIST-EQP-DISPLAY-ONLY")]
    public async Task AVehicleWithAPre700WorklistInItsJournalStartsAndShowsTheNextRevisionsMachineNumber()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        await using (SQCD.Agv.Infrastructure.SqliteWireToGateJournal journal = new(journalPath))
        {
            await journal.InitializeAsync(token);
            string v6Worklist = JsonSerializer.Serialize(new
            {
                stationId = "ST-01",
                worklistRevision = 1,
                operationSessionId = OperationSessionId,
                stationDepartureDeadlineAt = (DateTimeOffset?)null,
                stopEndedReason = (string?)null,
                items = new[]
                {
                    new
                    {
                        demandId = DemandA,
                        transportDemandKey = "TD-A",
                        sublot = "SUBLOT-A",
                        workType = "WIRE_TO_GATE",
                        stopRole = "PICKUP",
                        expectedBasketCount = 2
                    }
                }
            });
            Assert.DoesNotContain("eqp", v6Worklist, StringComparison.Ordinal);
            await journal.SaveAppliedJourneySnapshotAsync(
                new WireToGateAppliedJourneySnapshot(
                    "CurrentStopWorklistSnapshot",
                    Guid.NewGuid().ToString("D"),
                    1,
                    SQCD.Agv.Contracts.WireToGateProtocolSerializer.ComputePayloadContentSha256(v6Worklist),
                    v6Worklist,
                    DateTimeOffset.UtcNow),
                token);
        }

        await using Harness harness = await StartStopAsync(
            ["SUBLOT-A"],
            [Payloads.Item(DemandA, "TD-A", "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2, eqp: "N1-03")],
            journalPath,
            configure: server =>
            {
                server.JourneyWorklistRevision = 2;
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(
                        2,
                        Payloads.Item(DemandA, "TD-A", "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2, eqp: "N1-03")),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
            },
            awaitSublotEntry: true,
            token);

        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.CurrentStopWorklist?.Revision == 2
                && harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray()) is [{ Sublot: "SUBLOT-A", Eqp: "N1-03" }],
            "revision 2's row with its machine number",
            token);
        Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
        Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, envelope => envelope.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
    }

    private sealed record StopRun(
        (string Sublot, string? Eqp)[] Shown,
        WorklistItemRow[] RowsWithoutEqp,
        (bool CanSubmit, string? ExpectedSublots) Entry,
        string[] Verdicts,
        string[] Sent);

    private static async Task<StopRun> RunStopAsync(string? eqpA, string? eqpB, CancellationToken token)
    {
        await using Harness harness = await StartStopAsync(
            ["SUBLOT-A", "SUBLOT-B"],
            [
                Payloads.Item(DemandA, "TD-A", "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2, eqp: eqpA),
                Payloads.Item(DemandB, "TD-B", "SUBLOT-B", "WIRE_TO_OPTICAL", "PICKUP", 1, eqp: eqpB)
            ],
            journalPath: null,
            configure: null,
            awaitSublotEntry: true,
            token);

        WorklistItemRow[] rows = harness.OnUi(() => harness.ViewModel.WorklistItems.ToArray());
        (bool, string?) entry = (
            harness.OnUi(() => harness.ViewModel.CanSubmit),
            harness.Business.ExpectedSublots is { } expected ? string.Join(",", expected) : null);

        List<string> verdicts = [];
        foreach (string scanned in new[] { "SUBLOT-Z", "SUBLOT-B", "SUBLOT-A" })
        {
            try
            {
                await harness.Business.SubmitSublotAsync(scanned, "SCANNER", token);
                verdicts.Add($"{scanned} accepted");
            }
            catch (InvalidOperationException refused)
            {
                verdicts.Add($"{scanned} refused {refused.Message}");
            }
        }

        int accepted = verdicts.Count(verdict => verdict.EndsWith(" accepted", StringComparison.Ordinal));
        await harness.WaitUntilAsync(() => harness.Submissions.Count == accepted, "every accepted scan to be sent", token);
        await AssertWhileAsync(
            DisplaySettleWindow,
            () => Assert.Equal(accepted, harness.Submissions.Count),
            token);

        return new StopRun(
            [.. rows.Select(row => (row.Sublot, row.Eqp))],
            [.. rows.Select(row => row with { Eqp = null })],
            entry,
            [.. verdicts],
            [.. harness.Submissions.Select(WithoutIdentityAndTime)]);
    }

    /// <summary>
    /// A received line with the fields that differ between any two runs taken out: message and correlation ids,
    /// session generation and every time stamp. What is left is what the vehicle decided to send.
    /// </summary>
    private static string WithoutIdentityAndTime(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return Strip(document.RootElement);

        static string Strip(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(
                ",",
                element.EnumerateObject()
                    .Where(property => !IsVolatile(property.Name))
                    .Select(property => $"\"{property.Name}\":{Strip(property.Value)}")) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Strip)) + "]",
            _ => element.GetRawText()
        };

        static bool IsVolatile(string name) =>
            name.EndsWith("Id", StringComparison.Ordinal)
            && name is not ("demandId" or "agvId" or "stationId" or "operationSessionId")
            || name.EndsWith("At", StringComparison.Ordinal)
            || name is "sessionGeneration" or "contentSha256" or "commandContentSha256";
    }

    private static string? OperationResultLine(Harness harness) =>
        harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "OperationResult")
            .Select(envelope => envelope.WireLine)
            .LastOrDefault();

    /// <summary>
    /// The field's door at an unload: the lock releases, the output falls back, the operator takes the basket out and
    /// shuts the door (the readings and timings of <c>DoorShutOverOppositeOccupancyG2Tests</c>).
    /// </summary>
    private static ScriptStep[] OpensThenShutEmpty() =>
    [
        new(TimeSpan.FromMilliseconds(40), (fake, slot) => fake.SetLockFeedback(slot, locked: false)),
        new(TimeSpan.FromMilliseconds(600), (fake, slot) => fake.SetUnlockOutput(slot, energised: false)),
        new(TimeSpan.FromMilliseconds(1200), (fake, slot) => fake.ShutDoor(slot, false)),
    ];

    private static string ProtocolRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "SQCD_8005AGV.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("SQCD_8005AGV.sln not found above the test output.");
    }
}
