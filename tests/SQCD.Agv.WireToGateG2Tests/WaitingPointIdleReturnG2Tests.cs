using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using Xunit;
using Harness = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Harness;
using Payloads = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Payloads;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The onboard half of <c>FP-IS-12</c> (batch 8-22, <c>trytoreachpeak0/8005-agv-onboard-hmi#217</c>): a
/// waiting-point leg is a non-business stop. Driven from the fake control server through the real session
/// service, business service and <see cref="SQCD.Agv.Wpf.ViewModels.MainViewModel"/>, wired as
/// <c>App.xaml.cs</c> wires them.
/// </summary>
/// <remarks>
/// <para>
/// Vector <c>CV-WAITING-POINT-IDLE-RETURN</c> carries two onboard assertions and binds no payload (spec
/// section 6.6): <c>TREAT_WAITING_POINT_AS_NON_BUSINESS_STOP</c> and <c>NEVER_LOAD_AT_WAITING_POINT</c>. Each
/// named test below proves one of them on what the operator sees -- the visit cell, its UIA status, the
/// entry and cancellation buttons -- and on the two <c>SnapshotAppliedAck</c> the vector expects, not on the
/// payload's shape.
/// </para>
/// <para>
/// Whether the server sends a worklist at a waiting point is the control server's call
/// (<c>trytoreachpeak0/8005-agv-control-server#390</c>). No worklist and an empty one are both covered, and
/// neither reads 「旅程未同步」 or 「无待处理任务」.
/// </para>
/// </remarks>
public sealed class WaitingPointIdleReturnG2Tests
{
    private const string WaitingPoint = "ST-01";

    static WaitingPointIdleReturnG2Tests()
    {
        // The harness is MultiDemandJourneyG2Tests'; its credential and operator variables are set by that
        // class's static constructor, which using its nested types alone does not run.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(MultiDemandJourneyG2Tests).TypeHandle);
    }

    /// <summary>
    /// <c>TREAT_WAITING_POINT_AS_NON_BUSINESS_STOP</c>: the plan and the business state arrive in the vector's
    /// order and both are acknowledged; with no worklist the visit cell says the vehicle is returning to the
    /// waiting point, then that it stands by there once the leg is <c>ARRIVED</c>. Direction and task type stay
    /// empty and no entry is offered.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-12")]
    [Trait("ProtocolVector", "CV-WAITING-POINT-IDLE-RETURN")]
    public async Task AnIdleReturnIsShownAsANonBusinessStopOnTheWayAndAtThePoint()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.VectorJourneySnapshotsAfterRecovery = ["UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"];
                server.VectorPlanLegs = [(null, "ACTIVE", WaitingPoint)];
                server.VectorLegStopPurposeCategories = ["WAITING_POINT"];
                server.JourneyActivePurpose = "IDLE_RETURN";
            },
            token);

        await harness.WaitUntilAsync(
            () => AcknowledgedKinds(harness.Server).Length == 2
                && harness.ViewModel.IdleReturnStatus == WireToGateIdleReturnText.EnRouteStatus,
            "the plan and the business state to be applied and shown",
            token);

        Assert.Equal(["UPCOMING_STOP_PLAN", "VEHICLE_BUSINESS_STATE"], AcknowledgedKinds(harness.Server));
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        WireToGateMovementLeg leg = Assert.Single(harness.Session.CurrentJourney.UpcomingStopPlan!.Legs);
        Assert.Null(leg.LegType);
        Assert.Null(leg.DemandId);
        Assert.Null(harness.Session.CurrentJourney.CurrentStopWorklist);

        Assert.Equal($"空闲返回：前往等待点 {WaitingPoint}", harness.ViewModel.VisitText);
        AssertShownAsNonBusinessStop(harness);
        Assert.Equal(["等待点"], harness.ViewModel.JourneyPlanLegs.Select(row => row.StopPurposeText));

        await harness.Server.SendJourneySnapshotAsync(
            "UpcomingStopPlanSnapshot",
            Payloads.Plan(2, [WaitingPointLeg(1, "ARRIVED")]));
        await harness.WaitUntilAsync(
            () => harness.ViewModel.IdleReturnStatus == WireToGateIdleReturnText.AtWaitingPointStatus,
            "the arrived waiting-point leg to be shown",
            token);

        Assert.Equal($"在等待点 {WaitingPoint} 待命", harness.ViewModel.VisitText);
        AssertShownAsNonBusinessStop(harness);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>NEVER_LOAD_AT_WAITING_POINT</c>, the cell <c>HasConsistentDemand</c> used to let through: the plan has
    /// only a waiting-point leg (no demand), and the server nevertheless sends a worklist with an item and an
    /// entry request. Neither the entry nor the cancellation before any sublot is offered, a direct submit is
    /// refused with nothing sent, and the contradiction is logged once.
    /// </summary>
    /// <remarks>
    /// The purpose is left at <c>TRANSPORT</c> on purpose: the leg's category alone has to be enough. Before
    /// batch 8-22 this stop offered both buttons and sent the entry.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-12")]
    [Trait("ProtocolVector", "CV-WAITING-POINT-IDLE-RETURN")]
    public async Task NoLoadIsOfferedAtAWaitingPointEvenWithAWorklistItemAndAnEntryRequest()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.SublotEntryExpectedSublots = ["SUBLOT-A"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, [WaitingPointLeg(1, "ARRIVED")])
                };
            },
            token);

        await harness.WaitUntilAsync(
            () => harness.Business.ExpectedSublots is not null
                && harness.Session.CurrentJourney is { CurrentStopWorklist: not null, UpcomingStopPlan: not null }
                && AcknowledgedKinds(harness.Server).Length == 3,
            "the worklist, the waiting-point plan and the entry request",
            token);

        string[] acknowledged = AcknowledgedKinds(harness.Server);
        Assert.Contains("UPCOMING_STOP_PLAN", acknowledged);
        Assert.Contains("VEHICLE_BUSINESS_STATE", acknowledged);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Equal("TRANSPORT", harness.Session.CurrentJourney.VehicleBusinessState!.ActivePurpose);

        // What the operator sees: no entry, no cancellation, the stop described as the waiting point it is.
        await AssertEntryStaysClosedAsync(harness, token);
        Assert.Equal($"在等待点 {WaitingPoint} 待命", harness.ViewModel.VisitText);
        Assert.Equal(WireToGateIdleReturnText.AtWaitingPointStatus, harness.ViewModel.IdleReturnStatus);
        Assert.Equal(string.Empty, harness.ViewModel.StopDirectionText);
        Assert.Equal(string.Empty, harness.ViewModel.TaskTypeText);
        Assert.False(harness.Session.CurrentJourney.CanAcceptSublot);

        // And a press that bypasses the button does not go out either.
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Business.SubmitSublotAsync("SUBLOT-A", "SCANNER", token));
        Assert.Equal("WIRE_TO_GATE_JOURNEY_NOT_READY", refused.Message);
        await Task.Delay(200, token);
        Assert.Empty(harness.Submissions);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "LoadCancellationStartRequested");
        Assert.Equal(0, harness.Io.UnlockCount);

        Assert.Single(
            harness.Logger.Entries,
            entry => entry.Severity == LogSeverity.Warning && entry.Message.StartsWith("等待点停靠收到带项的清单", StringComparison.Ordinal));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The other way the server may do it: an empty worklist at the waiting point. Same words, not
    /// 「无待处理任务」.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-12")]
    [Trait("ProtocolVector", "CV-WAITING-POINT-IDLE-RETURN")]
    public async Task AnEmptyWorklistAtTheWaitingPointReadsAsStandingByNotAsNothingToDo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.VectorJourneySnapshotsAfterRecovery =
                    ["UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot", "CurrentStopWorklistSnapshot"];
                server.VectorPlanLegs = [(null, "ARRIVED", WaitingPoint)];
                server.VectorLegStopPurposeCategories = ["WAITING_POINT"];
                server.JourneyActivePurpose = "IDLE_RETURN";
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1)
                };
            },
            token);

        await harness.WaitUntilAsync(
            () => AcknowledgedKinds(harness.Server).Length == 3
                && harness.Session.CurrentJourney.CurrentStopWorklist is not null
                && harness.ViewModel.IdleReturnStatus == WireToGateIdleReturnText.AtWaitingPointStatus,
            "the plan, the business state and the empty worklist",
            token);

        Assert.Equal(
            ["UPCOMING_STOP_PLAN", "VEHICLE_BUSINESS_STATE", "CURRENT_STOP_WORKLIST"],
            AcknowledgedKinds(harness.Server));
        Assert.Empty(harness.Session.CurrentJourney.CurrentStopWorklist!.Items);
        Assert.Equal($"在等待点 {WaitingPoint} 待命", harness.ViewModel.VisitText);
        AssertShownAsNonBusinessStop(harness);
        Assert.DoesNotContain(
            harness.Logger.Entries,
            entry => entry.Message.StartsWith("等待点停靠收到带项的清单", StringComparison.Ordinal));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>REQ-0293</c>, and the guard against fixing too much: the worklist and the entry request stay exactly as
    /// they were, and only the two waiting-point facts go -- the idle return is released (<c>TRANSPORT</c>) and the
    /// plan's current leg becomes a business one. The entry, the direction and the station come back on those
    /// snapshots, with nothing local left over from the waiting point to clear first.
    /// </summary>
    [Fact]
    public async Task ReleasingTheIdleReturnBringsTheEntryBackAtOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneyActivePurpose = "IDLE_RETURN";
                server.SublotEntryExpectedSublots = ["SUBLOT-A"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, [WaitingPointLeg(1, "ARRIVED")])
                };
            },
            token);
        await harness.WaitUntilAsync(
            () => harness.Business.ExpectedSublots is not null
                && harness.ViewModel.IdleReturnStatus == WireToGateIdleReturnText.AtWaitingPointStatus,
            "the vehicle standing by at the waiting point with an entry request in hand",
            token);
        await AssertEntryStaysClosedAsync(harness, token);

        await harness.Server.SendJourneySnapshotAsync("VehicleBusinessStateSnapshot", Payloads.BusinessState(2, loadingPhase: null));
        await harness.Server.SendJourneySnapshotAsync(
            "UpcomingStopPlanSnapshot",
            Payloads.Plan(2, [WaitingPointLeg(1, "COMPLETED"), Payloads.Leg(2, "TO_PICKUP", "BUSINESS", ItemADemand, "ST-01", "ARRIVED")]));
        await harness.WaitUntilAsync(
            () => harness.Session.CurrentJourney.UpcomingStopPlan?.Revision == 2
                && harness.Session.CurrentJourney.VehicleBusinessState?.Revision == 2
                && harness.ViewModel.CanSubmit,
            "the released idle return and the business leg to reopen the entry",
            token);

        Assert.True(harness.Business.CanSubmitSublot);
        Assert.Equal("ST-01", harness.ViewModel.VisitText);
        Assert.Equal(string.Empty, harness.ViewModel.IdleReturnStatus);
        Assert.Equal("取货", harness.ViewModel.StopDirectionText);
        Assert.Equal("焊线→质检关卡", harness.ViewModel.TaskTypeText);

        await harness.Business.SubmitSublotAsync("SUBLOT-A", "SCANNER", token);
        JsonElement submitted = await harness.WaitForSubmissionAsync(token);
        Assert.Equal("SUBLOT-A", submitted.GetProperty("sublot").GetString());
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A dropped session clears the projection: the idle-return words and their UIA status go with it.
    /// </summary>
    [Fact]
    public async Task ADisconnectClearsTheIdleReturnCell()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartStandingByAsync(token);

        await harness.StopServerAsync();
        await harness.WaitUntilAsync(() => !harness.Session.Current.Connected, "the session to drop", token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.IdleReturnStatus.Length == 0,
            "the idle-return cell to clear",
            token);

        Assert.Equal("旅程未同步", harness.ViewModel.VisitText);
        Assert.Empty(harness.ViewModel.JourneyPlanLegs);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// A restart on the same journal, against a server that pushes nothing on the new session: the
    /// waiting-point plan and the <c>IDLE_RETURN</c> state come back from the journal
    /// (<c>RestorePersistedJourneyProjectionAsync</c>) with their revisions, and read the same words.
    /// </summary>
    [Fact]
    public async Task ARestartRestoresTheIdleReturnFromTheJournalWithTheSameWords()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness first = await StartStandingByAsync(token, planRevision: 7);
        string visitBefore = first.ViewModel.VisitText;

        await first.StopVehicleAsync();
        first.Server.SimulateOnboardProcessRestart();
        // Nothing is pushed on the next session, so what is shown can only have come from the journal.
        first.Server.VectorJourneySnapshotsAfterRecovery = [];
        await using Harness second = await Harness.StartAgainstAsync(first.Server, token, first.JournalPath);
        await second.WaitUntilAsync(
            () => second.ViewModel.IdleReturnStatus == WireToGateIdleReturnText.AtWaitingPointStatus,
            "the restored idle return",
            token);

        WireToGateJourneySnapshot restored = second.Session.CurrentJourney;
        Assert.Equal(7, restored.UpcomingStopPlan!.Revision);
        Assert.Equal("IDLE_RETURN", restored.VehicleBusinessState!.ActivePurpose);
        Assert.Equal($"在等待点 {WaitingPoint} 待命", visitBefore);
        Assert.Equal(visitBefore, second.ViewModel.VisitText);
        Assert.False(second.ViewModel.CanSubmit);
        Assert.Empty(second.UiErrors);
    }

    private const string ItemADemand = "aaaaaaaa-0000-4000-8000-00000000000a";

    private static async Task<Harness> StartStandingByAsync(CancellationToken token, long planRevision = 1)
    {
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.VectorJourneySnapshotsAfterRecovery = ["UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"];
                server.JourneyActivePurpose = "IDLE_RETURN";
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(planRevision, [WaitingPointLeg(1, "ARRIVED")])
                };
            },
            token);
        try
        {
            await harness.WaitUntilAsync(
                () => AcknowledgedKinds(harness.Server).Length == 2
                    && harness.ViewModel.IdleReturnStatus == WireToGateIdleReturnText.AtWaitingPointStatus,
                "the vehicle standing by at the waiting point",
                token);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The entry and the cancellation before any sublot stay shut for half a second, on the view model and on the
    /// business service alike.
    /// </summary>
    /// <remarks>
    /// Held over a window rather than read once: the view model re-reads the gates when the entry request's event
    /// reaches it, which is after the business service has stored the request the wait above keys on. A single
    /// read can land in that gap and see a button that has not been opened <i>yet</i>, which made this pass once
    /// with the waiting-point gate removed.
    /// </remarks>
    private static async Task AssertEntryStaysClosedAsync(Harness harness, CancellationToken token)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow.AddMilliseconds(500);
        do
        {
            Assert.False(harness.ViewModel.CanSubmit);
            Assert.False(harness.ViewModel.CanRequestLoadCancellation);
            Assert.False(harness.Business.CanSubmitSublot);
            Assert.False(harness.Business.CanRequestLoadCancellation);
            await Task.Delay(20, token);
        }
        while (DateTimeOffset.UtcNow < until);
    }

    private static object WaitingPointLeg(int sequence, string state) =>
        Payloads.Leg(sequence, null, "WAITING_POINT", null, WaitingPoint, state);

    private static void AssertShownAsNonBusinessStop(Harness harness)
    {
        Assert.DoesNotContain("旅程未同步", harness.ViewModel.VisitText, StringComparison.Ordinal);
        Assert.DoesNotContain("无待处理任务", harness.ViewModel.VisitText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, harness.ViewModel.StopDirectionText);
        Assert.Equal(string.Empty, harness.ViewModel.TaskTypeText);
        Assert.False(harness.ViewModel.CanSubmit);
        Assert.False(harness.ViewModel.CanRequestLoadCancellation);
    }

    private static string[] AcknowledgedKinds(FakeControlServer server) =>
    [
        .. server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SnapshotAppliedAck")
            .Select(item =>
            {
                using JsonDocument document = JsonDocument.Parse(item.WireLine);
                return document.RootElement.GetProperty("payload").GetProperty("snapshotKind").GetString()!;
            })
    ];
}
