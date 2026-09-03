using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// Produces the canonical WIRE_TO_GATE safety summary used by both the initial
/// session snapshot and subsequent safety-state revisions.
/// </summary>
public static class WireToGateSafetyEvaluator
{
    public static WireToGateSafetySummaryPayload Evaluate(
        IoSnapshot snapshot,
        VehicleSafetySignal vehicleSignal,
        DateTimeOffset now,
        TimeSpan ioSnapshotMaxAge,
        TimeSpan vehicleSafetyMaxAge,
        TimeSpan vehicleSafetyClockSkewTolerance)
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
            reasons.Add("VEHICLE_STATE_UNKNOWN");
        }
        else if (!vehicleStopped)
        {
            reasons.Add("ACTION_NOT_ALLOWED_IN_STATE");
        }

        return new WireToGateSafetySummaryPayload(
            !slotUnknown && allLocked && allOutputsReset && vehicleStopped,
            vehicleStopped,
            allLocked,
            allOutputsReset,
            slotUnknown || vehicleUnknown,
            reasons.Distinct(StringComparer.Ordinal).ToArray());
    }
}
