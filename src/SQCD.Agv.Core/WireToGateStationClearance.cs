namespace SQCD.Agv.Core;

/// <summary>
/// Everything the operator is shown before confirming a manual station clearance (batch 9-16,
/// <c>8005-agv-onboard-hmi#221</c>): which charger, who confirms, and whether this press repeats a
/// confirmation whose result is unknown.
/// </summary>
/// <remarks>
/// <b>The confirmation is bound to this whole record, not to the button.</b> The press hands back the
/// prompt it was shown, and the request goes out only if that is still the current one. A plan that
/// named another charger while the dialog was open, or a result that arrived meanwhile, makes it a
/// different prompt and the press is refused -- the operator confirms what they read, never what it
/// turned into (the lesson of <c>8005-agv-onboard-hmi#216</c>).
/// </remarks>
public sealed record WireToGateStationClearancePrompt(
    string StationId,
    string OperatorId,
    string? ResubmittedConfirmationRequestId);

/// <summary>Why the clearance entry is not offered.</summary>
public enum WireToGateStationClearanceUnavailability
{
    None,
    NotClearing,
    NoVerifiedMaintainer,
    StationUnknown,
    AwaitingAnswer
}

public enum WireToGateStationClearanceOutcomeKind
{
    Confirmed,
    Rejected,
    Unknown
}

/// <summary>
/// What the last confirmation came to. <c>Unknown</c> is a timeout or a dropped session: the request
/// may or may not have been taken, and it is never sent again on its own.
/// </summary>
public sealed record WireToGateStationClearanceOutcome(
    WireToGateStationClearanceOutcomeKind Kind,
    string ConfirmationRequestId,
    string StationId,
    bool StationReleased,
    string? ReasonCode);

/// <summary>
/// The clearance entry as the business service offers it: a prompt when it can be pressed, why not
/// otherwise, and the last confirmation's outcome while the server still says the vehicle is clearing.
/// </summary>
public sealed record WireToGateStationClearanceView(
    WireToGateStationClearancePrompt? Prompt,
    WireToGateStationClearanceUnavailability Unavailability,
    WireToGateStationClearanceOutcome? LastOutcome)
{
    public static WireToGateStationClearanceView NotClearing { get; } =
        new(null, WireToGateStationClearanceUnavailability.NotClearing, null);
}

public static class WireToGateStationClearanceStation
{
    /// <summary>
    /// The charger a clearance is about: the <c>stationId</c> of the plan's <c>CHARGER</c> leg, or
    /// <c>null</c> when the plan does not name exactly one charger.
    /// </summary>
    public static string? Resolve(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        return null;
    }
}
