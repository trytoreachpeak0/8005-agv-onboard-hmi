namespace SQCD.Agv.Core;

/// <summary>
/// Everything the operator is shown before confirming that the vehicle could not charge (batch 9-17,
/// <c>8005-agv-onboard-hmi#222</c>; vector <c>CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION</c>): which charger, who
/// confirms, what they observed, and whether this press repeats a confirmation whose result is unknown.
/// </summary>
/// <remarks>
/// <b>The confirmation is bound to this whole record, not to the button</b>, as the manual station clearance is
/// (<see cref="WireToGateStationClearancePrompt"/>). The press hands back the prompt it was shown, and the request
/// goes out only if that is still one of the current ones. A plan that moved the vehicle to another charger while
/// the dialog was open, or a result that arrived meanwhile, makes it a different prompt and the press is refused --
/// the operator confirms what they read, never what it turned into (the lesson of <c>8005-agv-onboard-hmi#216</c>).
/// </remarks>
public sealed record WireToGateUnableToChargePrompt(
    string ChargerStationId,
    string OperatorId,
    string ObservedCondition,
    string? ResubmittedConfirmationRequestId);

/// <summary>Why the unable-to-charge entry is not offered.</summary>
public enum WireToGateUnableToChargeUnavailability
{
    None,
    /// <summary><c>wireToGate.unableToChargeEntryEnabled</c> is off.</summary>
    Disabled,
    NotCharging,
    NoVerifiedMaintainer,
    StationUnknown,
    AwaitingAnswer
}

public enum WireToGateUnableToChargeOutcomeKind
{
    Confirmed,
    Rejected,
    Unknown,
    NotAccepted
}

/// <summary>
/// What the last confirmation came to. <c>Unknown</c> is a timeout or a dropped session: the request may or may
/// not have been taken, and it is never sent again on its own. <c>NotAccepted</c> is the server answering the
/// request itself with a <c>ProtocolProblem</c>, which ends that confirmation request id as a result does.
/// <see cref="ChargingPolicyDecision"/> is the server's, as sent, and is only ever shown.
/// </summary>
/// <param name="ChargingEnded">
/// The server ended the charging claim while this press was waiting: the entry is gone, so an unknown or refused
/// press cannot be resubmitted, and the operator is not told to (8005-agv-onboard-hmi#222 incremental review, item 1).
/// </param>
public sealed record WireToGateUnableToChargeOutcome(
    WireToGateUnableToChargeOutcomeKind Kind,
    string ConfirmationRequestId,
    string ChargerStationId,
    string ObservedCondition,
    string? ChargingPolicyDecision,
    string? ReasonCode,
    bool ChargingEnded = false);

/// <summary>
/// The unable-to-charge entry as the business service offers it: one prompt per condition the operator may
/// report when it can be pressed -- the four of the schema, or the single one a resubmission is bound to -- why
/// not otherwise, and the last confirmation's outcome while the server still says the vehicle is charging.
/// </summary>
public sealed record WireToGateUnableToChargeView(
    IReadOnlyList<WireToGateUnableToChargePrompt> Prompts,
    WireToGateUnableToChargeUnavailability Unavailability,
    WireToGateUnableToChargeOutcome? LastOutcome)
{
    public static WireToGateUnableToChargeView NotCharging { get; } =
        new([], WireToGateUnableToChargeUnavailability.NotCharging, null);
}

public static class WireToGateUnableToCharge
{
    /// <summary>
    /// The schema's four <c>observedCondition</c> values, in its order. All four are offered: the vehicle reports
    /// what the operator saw and does not decide which of them are enough to confirm a failure to charge
    /// (<c>REQ-0175</c>; the control server's, <c>8005-agv-control-server#410</c>).
    /// </summary>
    public static IReadOnlyList<string> ObservedConditions { get; } =
    [
        "CHARGER_UNREACHABLE",
        "CHARGER_OCCUPIED",
        "CONNECTION_FAILED",
        "CHARGER_FAULT"
    ];

    /// <summary>The business state's purpose says the vehicle is on a charging claim.</summary>
    public static bool IsCharging(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        return journey.VehicleBusinessState?.ActivePurpose == "CHARGING";
    }

    /// <summary>
    /// The charger the confirmation is about: the <c>stationId</c> of the plan's current leg when that leg is a
    /// <c>CHARGER</c> leg, and <c>null</c> otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The current leg, not any charger leg in the plan: the statement is that this vehicle tried this charger
    /// now. The leg's state is not consulted -- a vehicle that cannot reach the charger
    /// (<c>CHARGER_UNREACHABLE</c>) is still on its way to it, and whether that is enough is the server's to say.
    /// </para>
    /// <para>
    /// <b><c>null</c> rather than a guess.</b> A current leg that is not a charger -- a plan that has not caught up
    /// with the business state, or none at all -- names no station, and none is taken from a completed leg, a
    /// later one or a name.
    /// </para>
    /// </remarks>
    public static string? ResolveCharger(WireToGateJourneySnapshot journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        return journey.CurrentLeg is { StopPurposeCategory: "CHARGER" } leg
            && !string.IsNullOrWhiteSpace(leg.StationId)
            ? leg.StationId
            : null;
    }
}
