namespace SQCD.Agv.Core;

/// <summary>
/// The one judgement of "each worklist row at this stop carries its own 「取消」" on this end: a business stop whose worklist is
/// all pickups, at the staging pickup stop (CP-0014 revision two, <c>REQ-0211</c>; 8005-agv-onboard-hmi#353) and at a machine
/// pickup alike (CP-0016; 8005-agv-onboard-hmi#363).
/// </summary>
/// <remarks>
/// <para>
/// Wherever it holds, a new cancellation before any sublot is offered only on a row: the shared entry's "pick, then cancel" is
/// gone, and the shared entry is left the load in flight and the close-out of a cancellation whose row has left the worklist.
/// </para>
/// <para>
/// <b>Offered is not pressable.</b> A row is pressed only while the server's entry request for this stop is open and nothing
/// has been commanded for its demand; the business service judges that per row.
/// </para>
/// </remarks>
public static class WireToGatePickupStop
{
    public const string PickupRole = "PICKUP";

    public static bool OffersWorklistRowCancellation(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        return !journey.IsNonBusinessStop
            && journey.CurrentStopWorklist is { Items.Count: > 0 } worklist
            && worklist.Items.All(item => string.Equals(item.StopRole, PickupRole, StringComparison.Ordinal));
    }
}
