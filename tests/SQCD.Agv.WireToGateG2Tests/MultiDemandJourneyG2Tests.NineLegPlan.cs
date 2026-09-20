using System.Text.Json;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// <c>FP-IS-08</c> (<c>MULTI_STOP_JOURNEY_PLAN</c>), onboard half: <c>CV-MULTI-STOP-PLAN-NINE-LEGS</c>.
/// </summary>
/// <remarks>
/// <para>
/// The vector's order is <c>UpcomingStopPlanSnapshot → SnapshotAppliedAck → CurrentStopWorklistSnapshot →
/// SnapshotAppliedAck</c> and its onboard assertions are <c>DISPLAY_FULL_JOURNEY_PLAN</c> and
/// <c>NEVER_REORDER_LEGS_LOCALLY</c>. Both tests read the plan off the view model's leg list, the thing
/// the operator sees, not off the payload: a plan acknowledged and then shown in the order it arrived
/// would pass a payload-shape check.
/// </para>
/// <para>
/// What these prove is the two ends' G2 for nine legs (spec section 8.8). They do not say nine legs
/// have been driven; that is the control server's G3 in batch 7-15.
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    private const string DemandC = "cccccccc-0000-4000-8000-00000000000c";

    /// <summary>
    /// Nine legs mixing business stops, a waiting point and a charger, sent in an order that is not
    /// their <c>sequence</c>, are received in the vector's four steps, both snapshots acknowledged,
    /// and the leg list shows all nine in <c>sequence</c> order.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("ProtocolVector", "CV-MULTI-STOP-PLAN-NINE-LEGS")]
    public async Task ANineLegPlanIsAcknowledgedAndShownInFullInSequenceOrder()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.VectorJourneySnapshotsAfterRecovery = ["UpcomingStopPlanSnapshot", "CurrentStopWorklistSnapshot"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, NineLegsOutOfOrder),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB)
                };
            },
            token);

        await harness.WaitUntilAsync(
            () => Acknowledged(harness.Server).Length == 2,
            "both snapshots to be acknowledged",
            token);

        Assert.Equal(
            [("UPCOMING_STOP_PLAN", 1L), ("CURRENT_STOP_WORKLIST", 1L)],
            Acknowledged(harness.Server));
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
        Assert.Equal(
            [
                "1|BUSINESS|COMPLETED",
                "2|BUSINESS|ARRIVED",
                "3|WAITING_POINT|PLANNED",
                "4|BUSINESS|PLANNED",
                "5|CHARGER|PLANNED",
                "6|BUSINESS|PLANNED",
                "7|BUSINESS|PLANNED",
                "8|WAITING_POINT|PLANNED",
                "9|BUSINESS|PLANNED"
            ],
            harness.PlanLegStatuses());
        Assert.Equal(
            ["ST-01", "ST-01", "WP-03", "ST-GATE", "CH-05", "ST-OPT", "ST-N2", "WP-08", "ST-GATE"],
            harness.ViewModel.JourneyPlanLegs.Select(row => row.StationId).ToArray());
        Assert.Equal(["SUBLOT-A", "SUBLOT-B"], harness.WorklistRows().Select(row => row.Sublot));
    }

    /// <summary>
    /// A demand added mid-journey: the plan's revision advances, the new plan replaces the old one as a
    /// whole, its revision is acknowledged, and the leg list shows the new order -- no leg kept from the
    /// old plan, nothing re-sorted from it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("ProtocolVector", "CV-MULTI-STOP-PLAN-NINE-LEGS")]
    public async Task AnAddedDemandReplacesThePlanAndItsNewOrderIsShown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.VectorJourneySnapshotsAfterRecovery = ["UpcomingStopPlanSnapshot", "CurrentStopWorklistSnapshot"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB)
                };
            },
            token);
        await harness.WaitUntilAsync(
            () => Acknowledged(harness.Server).Length == 2,
            "the first plan and worklist to be acknowledged",
            token);

        // C joins at the next stop: its pickup goes in before both drop-offs, and B's drop-off now
        // comes before A's. Sent out of sequence order again.
        await harness.Server.SendJourneySnapshotAsync(
            "UpcomingStopPlanSnapshot",
            Payloads.Plan(
                2,
                [
                    Payloads.Leg(5, "TO_DROPOFF", "BUSINESS", DemandC, "ST-N2", "PLANNED"),
                    Payloads.Leg(1, "TO_PICKUP", "BUSINESS", DemandA, "ST-01", "ARRIVED"),
                    Payloads.Leg(4, "TO_DROPOFF", "BUSINESS", DemandA, "ST-GATE", "PLANNED"),
                    Payloads.Leg(2, "TO_PICKUP", "BUSINESS", DemandC, "ST-02", "PLANNED"),
                    Payloads.Leg(3, "TO_DROPOFF", "BUSINESS", DemandB, "ST-OPT", "PLANNED")
                ]));
        await harness.WaitUntilAsync(
            () => Acknowledged(harness.Server).Contains(("UPCOMING_STOP_PLAN", 2L)),
            "the revised plan to be acknowledged",
            token);

        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Empty(harness.UiErrors);
        Assert.Equal(
            ["ST-01", "ST-02", "ST-OPT", "ST-GATE", "ST-N2"],
            harness.ViewModel.JourneyPlanLegs.Select(row => row.StationId).ToArray());
        Assert.Equal(
            ["1|BUSINESS|ARRIVED", "2|BUSINESS|PLANNED", "3|BUSINESS|PLANNED", "4|BUSINESS|PLANNED", "5|BUSINESS|PLANNED"],
            harness.PlanLegStatuses());
        Assert.Equal(2, harness.Session.CurrentJourney.UpcomingStopPlan!.Revision);
    }

    /// <summary>
    /// Nine legs, in the order the server sends them: not their <c>sequence</c> order. Two demands are
    /// picked up at ST-01 (the completed and the arrived leg); waiting points and the charger carry no
    /// leg type and no demand.
    /// </summary>
    private static readonly object[] NineLegsOutOfOrder =
    [
        Payloads.Leg(9, "TO_DROPOFF", "BUSINESS", DemandA, "ST-GATE", "PLANNED"),
        Payloads.Leg(3, null, "WAITING_POINT", null, "WP-03", "PLANNED"),
        Payloads.Leg(6, "TO_DROPOFF", "BUSINESS", DemandB, "ST-OPT", "PLANNED"),
        Payloads.Leg(1, "TO_PICKUP", "BUSINESS", DemandA, "ST-01", "COMPLETED"),
        Payloads.Leg(5, null, "CHARGER", null, "CH-05", "PLANNED"),
        Payloads.Leg(8, null, "WAITING_POINT", null, "WP-08", "PLANNED"),
        Payloads.Leg(2, "TO_PICKUP", "BUSINESS", DemandB, "ST-01", "ARRIVED"),
        Payloads.Leg(7, "TO_DROPOFF", "BUSINESS", DemandC, "ST-N2", "PLANNED"),
        Payloads.Leg(4, "TO_PICKUP", "BUSINESS", DemandC, "ST-GATE", "PLANNED")
    ];

    /// <summary>The (snapshotKind, revision) of every <c>SnapshotAppliedAck</c> the server received, in order.</summary>
    private static (string Kind, long Revision)[] Acknowledged(FakeControlServer server) =>
    [
        .. server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SnapshotAppliedAck")
            .Select(item =>
            {
                using JsonDocument document = JsonDocument.Parse(item.WireLine);
                JsonElement payload = document.RootElement.GetProperty("payload");
                return (payload.GetProperty("snapshotKind").GetString()!, payload.GetProperty("appliedRevision").GetInt64());
            })
    ];
}
