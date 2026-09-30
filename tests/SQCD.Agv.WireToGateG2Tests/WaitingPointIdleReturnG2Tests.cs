using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
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
        // Both cells, because the view model writes the status one statement before the visit text: the
        // status alone is true a moment before the text below has been written at all (onboard-hmi#228).
        // Waited for as "no longer the en-route text", so what it changed to is still the assertion's to say.
        await harness.WaitUntilAsync(
            () => harness.ViewModel.IdleReturnStatus == WireToGateIdleReturnText.AtWaitingPointStatus
                && harness.ViewModel.VisitText != $"空闲返回：前往等待点 {WaitingPoint}",
            "the arrived waiting-point leg to be shown in the status and the visit text",
            token);

        Assert.Equal($"在等待点 {WaitingPoint} 待命", harness.ViewModel.VisitText);
        AssertShownAsNonBusinessStop(harness);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// <c>NEVER_LOAD_AT_WAITING_POINT</c>, the cell <c>HasConsistentDemand</c> used to let through: the plan has
    /// only a waiting-point leg (no demand), and the server nevertheless sends a worklist with an item and an
    /// entry request. The entry is not offered, the stop reads as the waiting point it is, and the
    /// contradiction is logged once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The purpose is left at <c>TRANSPORT</c> on purpose: the leg's category alone has to be enough.
    /// </para>
    /// <para>
    /// One of three gates, pinned alone: the one in <c>CanSubmitSublot</c>. A submit that bypasses the button is
    /// <see cref="ADirectSubmitAtAWaitingPointIsRefusedAndSendsNothing"/>, the cancellation before any sublot is
    /// <see cref="TheCancellationBeforeAnySublotIsNotOfferedAtAWaitingPoint"/>; each goes red with only its own
    /// gate removed. The same stop made a business one is the control,
    /// <see cref="AnOrdinaryBusinessStopOffersTheEntryAndSendsIt"/>.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-12")]
    [Trait("ProtocolVector", "CV-WAITING-POINT-IDLE-RETURN")]
    public async Task NoEntryIsOfferedAtAWaitingPointEvenWithAWorklistItemAndAnEntryRequest()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartWithEntryRequestAsync("TRANSPORT", WaitingPointLeg(1, "ARRIVED"), token);

        string[] acknowledged = AcknowledgedKinds(harness.Server);
        Assert.Contains("UPCOMING_STOP_PLAN", acknowledged);
        Assert.Contains("VEHICLE_BUSINESS_STATE", acknowledged);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");

        await AssertEntryStaysClosedAsync(harness, token);
        Assert.Equal($"在等待点 {WaitingPoint} 待命", harness.ViewModel.VisitText);
        Assert.Equal(WireToGateIdleReturnText.AtWaitingPointStatus, harness.ViewModel.IdleReturnStatus);
        Assert.Equal(string.Empty, harness.ViewModel.StopDirectionText);
        Assert.Equal(string.Empty, harness.ViewModel.TaskTypeText);
        Assert.Single(
            harness.Logger.Entries,
            entry => entry.Severity == LogSeverity.Warning && entry.Message.StartsWith("等待点停靠收到带项的清单", StringComparison.Ordinal));
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The second gate of <c>NEVER_LOAD_AT_WAITING_POINT</c>: a submit that does not ask the button first -- the
    /// automation host, any path straight into the business service -- is refused at the waiting point, no
    /// <c>SublotSubmitted</c> goes out and no door opens.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-12")]
    [Trait("ProtocolVector", "CV-WAITING-POINT-IDLE-RETURN")]
    public async Task ADirectSubmitAtAWaitingPointIsRefusedAndSendsNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartWithEntryRequestAsync("TRANSPORT", WaitingPointLeg(1, "ARRIVED"), token);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Business.SubmitSublotAsync("SUBLOT-A", "SCANNER", token));

        Assert.Equal("WIRE_TO_GATE_JOURNEY_NOT_READY", refused.Message);
        await Task.Delay(300, token);
        Assert.Empty(harness.Submissions);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The third gate: the cancellation before any sublot is not offered at a waiting point -- there is nothing
    /// to give up before loading -- and a press that reaches the business service anyway sends nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-12")]
    [Trait("ProtocolVector", "CV-WAITING-POINT-IDLE-RETURN")]
    public async Task TheCancellationBeforeAnySublotIsNotOfferedAtAWaitingPoint()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartWithEntryRequestAsync("TRANSPORT", WaitingPointLeg(1, "ARRIVED"), token);

        await AssertCancellationStaysClosedAsync(harness, token);
        try
        {
            await harness.Business.RequestLoadCancellationAsync(
                WireToGateBusinessService.LoadCancellationDefaultReason, token);
        }
        catch (InvalidOperationException)
        {
            // A press on an entry that was never offered has always been refused this way; what counts is below.
        }

        await Task.Delay(300, token);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "LoadCancellationStartRequested");
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// The two facts disagree: the business state says <c>IDLE_RETURN</c>, the plan's current leg is still a
    /// <c>BUSINESS</c> one (a plan that has not caught up, or a server contradiction). Either fact is enough, so
    /// the entry stays shut and the cell says an idle return without borrowing the business leg's station.
    /// </summary>
    /// <remarks>
    /// Why "either": the two snapshots arrive separately, and a load must not be offered in the window between
    /// them, whichever comes first. The cost is that a purpose released late holds the entry shut until it
    /// arrives -- the operator waits one snapshot, where the other choice could open a door at a waiting point.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-12")]
    [Trait("ProtocolVector", "CV-WAITING-POINT-IDLE-RETURN")]
    public async Task AnIdleReturnPurposeOverABusinessLegStillOffersNoEntry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartWithEntryRequestAsync("IDLE_RETURN", BusinessLeg(1, "ARRIVED"), token);

        Assert.Equal("BUSINESS", harness.Session.CurrentJourney.CurrentLeg!.StopPurposeCategory);
        await AssertEntryStaysClosedAsync(harness, token);
        Assert.Equal("空闲返回：前往等待点", harness.ViewModel.VisitText);
        Assert.Equal(WireToGateIdleReturnText.EnRouteStatus, harness.ViewModel.IdleReturnStatus);
        Assert.Equal(string.Empty, harness.ViewModel.StopDirectionText);

        // All three gates read the purpose too, not the leg alone: with the leg a business one, a gate that looked
        // only at the leg would offer the cancellation or send the entry here (review mutation M8).
        await AssertCancellationStaysClosedAsync(harness, token);
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Business.SubmitSublotAsync("SUBLOT-A", "SCANNER", token));
        Assert.Equal("WIRE_TO_GATE_JOURNEY_NOT_READY", refused.Message);
        await Task.Delay(300, token);
        Assert.Empty(harness.Submissions);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.UiErrors);
    }

    /// <summary>
    /// The control for the four above: the same stop, worklist and entry request with the leg a <c>BUSINESS</c>
    /// one and the purpose <c>TRANSPORT</c>. Both entries are offered and the entry goes out -- the waiting-point
    /// gates do not reach an ordinary transport stop.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryBusinessStopOffersTheEntryAndSendsIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartWithEntryRequestAsync("TRANSPORT", BusinessLeg(1, "ARRIVED"), token);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.CanSubmit && harness.ViewModel.CanRequestLoadCancellation,
            "the entry and the cancellation before any sublot to be offered",
            token);

        Assert.True(harness.Business.CanSubmitSublot);
        Assert.True(harness.Business.CanRequestLoadCancellation);
        Assert.Equal("ST-01", harness.ViewModel.VisitText);
        Assert.Equal(string.Empty, harness.ViewModel.IdleReturnStatus);
        Assert.Equal("取货", harness.ViewModel.StopDirectionText);

        await harness.Business.SubmitSublotAsync("SUBLOT-A", "SCANNER", token);
        JsonElement submitted = await harness.WaitForSubmissionAsync(token);
        Assert.Equal("SUBLOT-A", submitted.GetProperty("sublot").GetString());
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
    private static Task AssertEntryStaysClosedAsync(Harness harness, CancellationToken token) =>
        HoldsForHalfASecondAsync(
            () =>
            {
                Assert.False(harness.ViewModel.CanSubmit);
                Assert.False(harness.Business.CanSubmitSublot);
            },
            token);

    /// <summary>The same, for the cancellation before any sublot.</summary>
    private static Task AssertCancellationStaysClosedAsync(Harness harness, CancellationToken token) =>
        HoldsForHalfASecondAsync(
            () =>
            {
                Assert.False(harness.ViewModel.CanRequestLoadCancellation);
                Assert.False(harness.Business.CanRequestLoadCancellation);
            },
            token);

    private static async Task HoldsForHalfASecondAsync(Action assertion, CancellationToken token)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow.AddMilliseconds(500);
        do
        {
            assertion();
            await Task.Delay(20, token);
        }
        while (DateTimeOffset.UtcNow < until);
    }

    /// <summary>
    /// A stop at <c>ST-01</c> with one worklist item (<c>SUBLOT-A</c>) and an entry request for it, under the given
    /// purpose and plan leg -- the only two things the tests above vary.
    /// </summary>
    private static async Task<Harness> StartWithEntryRequestAsync(string activePurpose, object leg, CancellationToken token)
    {
        Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.JourneyActivePurpose = activePurpose;
                server.SublotEntryExpectedSublots = ["SUBLOT-A"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, [leg])
                };
            },
            token);
        try
        {
            await harness.WaitUntilAsync(
                () => harness.Business.ExpectedSublots is not null
                    && harness.Session.CurrentJourney is { CurrentStopWorklist: not null, UpcomingStopPlan: not null }
                    && AcknowledgedKinds(harness.Server).Length == 3,
                "the business state, the worklist, the plan and the entry request",
                token);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    private static object BusinessLeg(int sequence, string state) =>
        Payloads.Leg(sequence, "TO_PICKUP", "BUSINESS", ItemADemand, "ST-01", state);

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
