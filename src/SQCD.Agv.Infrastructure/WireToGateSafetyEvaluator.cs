using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// Produces the canonical WIRE_TO_GATE safety summary used by both the initial
/// session snapshot and subsequent safety-state revisions.
/// </summary>
public static class WireToGateSafetyEvaluator
{
    /// <summary>
    /// The safety reason a latched fatal safety fault adds (8005-agv-onboard-hmi#197).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Protocol v2.0.0 has no code for "the onboard side has latched a fatal fault"; a dedicated one is on the
    /// v3.0.0 backlog (8005-agv-program#115). Of the registered codes this is the one that says what the latch
    /// means for the server -- do not depart -- and, just as important, the one outside both of the control
    /// server's allow-lists that excuse departure unsafety: <c>OwnMovementOrderExplanation.VehicleOnlyReasons</c>
    /// (control-server#314 lets the pickup dispatch plan past a closed readiness gate when every reason is in it)
    /// and <c>WireToGateStore.OperationInducedUnsafety</c> (readiness stays Ready mid-load when every reason is in
    /// it). <b>Do not switch it to <c>VEHICLE_NOT_READY</c></b>, the code the executors use for a refused pulse:
    /// a vehicle driving on the server's own order already reports exactly that, and a latch would vanish into it.
    /// </para>
    /// <para>
    /// Before this ticket the onboard side put this code in no safety summary, so on the wire it still names the
    /// latch alone; the control server itself maps its own <c>DEPARTURE_SAFETY_NOT_READY</c> readiness reason to
    /// the same word, which is a readiness reason, not a safety reason.
    /// </para>
    /// </remarks>
    public const string FatalFaultLatchedReason = "DEPARTURE_UNSAFE";

    public static WireToGateSafetySummaryPayload Evaluate(
        IoSnapshot snapshot,
        VehicleSafetySignal vehicleSignal,
        DateTimeOffset now,
        TimeSpan ioSnapshotMaxAge,
        TimeSpan vehicleSafetyMaxAge,
        TimeSpan vehicleSafetyClockSkewTolerance,
        bool fatalFaultLatched)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(vehicleSignal);

        bool ioFresh = snapshot.IsConnected
            && SafetyRules.IsSnapshotFresh(snapshot, now, ioSnapshotMaxAge);
        bool slotUnknown = !ioFresh
            || snapshot.Lockers.Count != 8
            || snapshot.Lockers.Any(locker => !locker.IsKnown);
        bool allLocked = !slotUnknown && snapshot.Lockers.All(locker => locker.IsLocked);
        bool allOutputsReset = !slotUnknown
            && snapshot.Lockers.All(locker => locker.UnlockOutputRaw is false);
        bool vehicleFresh = vehicleSignal.IsFresh(
            now,
            vehicleSafetyMaxAge,
            vehicleSafetyClockSkewTolerance);
        bool vehicleStopped = vehicleSignal.MotionState == VehicleMotionState.Stopped && vehicleFresh;
        bool vehicleUnknown = !vehicleFresh || vehicleSignal.MotionState == VehicleMotionState.Unknown;

        List<string> reasons = [];
        if (slotUnknown)
        {
            reasons.Add("SLOT_STATE_UNKNOWN");
        }
        if (!allLocked)
        {
            reasons.Add("LOCK_NOT_CLOSED");
        }
        if (!allOutputsReset)
        {
            reasons.Add("UNLOCK_OUTPUT_NOT_RESET");
        }
        if (vehicleUnknown)
        {
            // The protocol registry has no VEHICLE_STATE_UNKNOWN entry. An
            // expired or indeterminate upstream safety signal is represented
            // on the wire by the registered VEHICLE_NOT_READY code.
            reasons.Add("VEHICLE_NOT_READY");
        }
        else if (!vehicleStopped)
        {
            reasons.Add("ACTION_NOT_ALLOWED_IN_STATE");
        }
        if (fatalFaultLatched)
        {
            // Beside the physical reasons, never in place of them, and last: the other four fields keep saying
            // what the doors and the vehicle are doing. A latch is a known state, so it sets no unknown.
            reasons.Add(FatalFaultLatchedReason);
        }

        return new WireToGateSafetySummaryPayload(
            !fatalFaultLatched && !slotUnknown && allLocked && allOutputsReset && vehicleStopped,
            vehicleStopped,
            allLocked,
            allOutputsReset,
            slotUnknown || vehicleUnknown,
            reasons.Distinct(StringComparer.Ordinal).ToArray());
    }
}
