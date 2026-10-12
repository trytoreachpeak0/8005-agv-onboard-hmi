using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A plan whose leg sequences skip a number, or do not start at 1, through the whole session (8005-agv-onboard-hmi#327).
/// </summary>
/// <remarks>
/// The control server stages exactly such plans: the closing plan when a vehicle clearing a charger reaches its waiting
/// point is one leg at sequence 2, and a second clearance attempt drives under <c>[1, 3]</c>. On 2026-10-12 agv02
/// refused the first as <c>PROTOCOL_SCHEMA_INVALID</c>, its receive loop ended, the server replayed the snapshot into
/// every next session, and the vehicle reconnected every 4 s. The unit tests pin the inbound check; this pins what the
/// check is for: the snapshot is applied and acknowledged, the session that carried it stays up, and the leg list shows
/// the legs in <c>sequence</c> order with their own numbers.
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    [Fact]
    public async Task APlanWhoseSequencesSkipIsAcknowledgedAndTheSessionStaysUp()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.VectorJourneySnapshotsAfterRecovery = ["UpcomingStopPlanSnapshot"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, [Payloads.Leg(2, null, "WAITING_POINT", null, "WP-03", "ARRIVED")])
                };
            },
            token);
        await harness.WaitUntilAsync(
            () => Acknowledged(harness.Server).Contains(("UPCOMING_STOP_PLAN", 1L)),
            "the one-leg plan at sequence 2 to be acknowledged",
            token);
        long generation = harness.Session.Current.SessionGeneration!.Value;

        Assert.Equal(["2|WAITING_POINT|ARRIVED"], harness.PlanLegStatuses());

        await harness.Server.SendJourneySnapshotAsync(
            "UpcomingStopPlanSnapshot",
            Payloads.Plan(
                2,
                [
                    Payloads.Leg(3, null, "WAITING_POINT", null, "WP-03", "ACTIVE"),
                    Payloads.Leg(1, null, "CHARGER", null, "CH-01", "COMPLETED")
                ]));
        await harness.WaitUntilAsync(
            () => Acknowledged(harness.Server).Contains(("UPCOMING_STOP_PLAN", 2L)),
            "the plan at sequences 1 and 3 to be acknowledged",
            token);

        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "ProtocolProblem");
        Assert.Single(harness.Server.Received, item => item.MessageType == "SessionHello");
        Assert.Single(harness.Server.Received.Select(item => item.Connection).Distinct());
        Assert.True(harness.Session.Current.Connected);
        Assert.Equal(generation, harness.Session.Current.SessionGeneration);
        Assert.Empty(harness.UiErrors);
        Assert.Equal(["1|CHARGER|COMPLETED", "3|WAITING_POINT|ACTIVE"], harness.PlanLegStatuses());
        Assert.Equal(["CH-01", "WP-03"], harness.ViewModel.JourneyPlanLegs.Select(row => row.StationId).ToArray());
    }
}
