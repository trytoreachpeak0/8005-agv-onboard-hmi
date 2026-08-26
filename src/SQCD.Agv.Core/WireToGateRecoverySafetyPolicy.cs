namespace SQCD.Agv.Core;

public sealed record WireToGateRecoverySafetyFacts(
    bool RecoverySessionAuthorized,
    bool RecoveryStatePersisted,
    bool VehicleStopped,
    bool VehicleSignalFresh,
    bool AllTargetSlotsKnown,
    bool AllTargetSlotsLocked,
    bool AllUnlockOutputsReset)
{
    public static WireToGateRecoverySafetyFacts Unknown { get; } = new(
        RecoverySessionAuthorized: false,
        RecoveryStatePersisted: false,
        VehicleStopped: false,
        VehicleSignalFresh: false,
        AllTargetSlotsKnown: false,
        AllTargetSlotsLocked: false,
        AllUnlockOutputsReset: false);
}

public sealed record WireToGateRecoverySafetyDecision(
    bool Allowed,
    string ReasonCode)
{
    public static WireToGateRecoverySafetyDecision Denied(string reasonCode) =>
        new(false, reasonCode);

    public static WireToGateRecoverySafetyDecision Allow() =>
        new(true, "NONE");
}

/// <summary>
/// Central fail-closed gate for recovery actions that could change physical state.
/// A normal UI command cannot manufacture an authorized recovery session or bypass
/// persistence and fresh hardware facts.
/// </summary>
public static class WireToGateRecoverySafetyPolicy
{
    public static WireToGateRecoverySafetyDecision Evaluate(
        WireToGateRecoverySafetyFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.RecoverySessionAuthorized)
        {
            return WireToGateRecoverySafetyDecision.Denied(
                "RECOVERY_AUTHORIZATION_REQUIRED");
        }

        if (!facts.RecoveryStatePersisted)
        {
            return WireToGateRecoverySafetyDecision.Denied(
                "RECOVERY_STATE_NOT_PERSISTED");
        }

        if (!facts.VehicleSignalFresh)
        {
            return WireToGateRecoverySafetyDecision.Denied(
                "VEHICLE_STATE_UNKNOWN");
        }

        if (!facts.VehicleStopped)
        {
            return WireToGateRecoverySafetyDecision.Denied(
                "ACTION_NOT_ALLOWED_IN_STATE");
        }

        if (!facts.AllTargetSlotsKnown)
        {
            return WireToGateRecoverySafetyDecision.Denied(
                "SLOT_STATE_UNKNOWN");
        }

        if (!facts.AllTargetSlotsLocked)
        {
            return WireToGateRecoverySafetyDecision.Denied(
                "LOCK_NOT_CLOSED");
        }

        if (!facts.AllUnlockOutputsReset)
        {
            return WireToGateRecoverySafetyDecision.Denied(
                "UNLOCK_OUTPUT_NOT_RESET");
        }

        return WireToGateRecoverySafetyDecision.Allow();
    }
}
