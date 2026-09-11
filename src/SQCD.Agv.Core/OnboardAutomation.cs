namespace SQCD.Agv.Core;

/// <summary>
/// Read-only state and the business commands exposed to the local automation host. The host
/// never receives a WPF control or an IO primitive.
/// </summary>
public sealed record OnboardAutomationSnapshot(
    string AgvId,
    OnboardSnapshot Onboard,
    WireToGateSessionSnapshot? WireToGateSession,
    WireToGateJourneySnapshot? WireToGateJourney,
    WireToGateRecoveryState RecoveryState,
    bool CanSubmitSublot,
    IReadOnlyList<string> ExpectedSublots,
    string? CurrentOperationAttemptId,
    string? CurrentOperationPhase,
    IReadOnlyList<string> AvailableRecoveryActions,
    DateTimeOffset ObservedAt);

public sealed record OnboardAutomationSubmitOutcome(
    bool Accepted,
    string? MessageId,
    string? ReasonCode,
    OnboardAutomationSnapshot Snapshot);

public sealed record OnboardAutomationRecoveryOutcome(
    bool Accepted,
    string? ReasonCode,
    OnboardAutomationSnapshot Snapshot);

/// <summary>
/// The recovery requests an operator can make from the HMI, one per button. The first three are
/// the protocol's recovery actions; cancellation and correction are separate protocol flows and
/// keep their vector names. None of them opens a slot directly: each is a request the server
/// authorizes or refuses, exactly as when the button is pressed.
/// </summary>
public static class OnboardAutomationRecoveryActions
{
    public const string ResumeAfterRepair = "RESUME_AFTER_REPAIR";
    public const string CompensateLoadAllEmpty = "COMPENSATE_LOAD_ALL_EMPTY";
    public const string FaultCargoHandoff = "FAULT_CARGO_HANDOFF";
    public const string LoadCancellation = WireToGateRecoveryVectorTypes.LoadCancellation;
    public const string LoadCorrection = WireToGateRecoveryVectorTypes.LoadCorrection;

    public static IReadOnlyList<string> All { get; } =
    [
        ResumeAfterRepair,
        CompensateLoadAllEmpty,
        FaultCargoHandoff,
        LoadCancellation,
        LoadCorrection
    ];
}

public interface IOnboardAutomationFacade
{
    public OnboardAutomationSnapshot ReadSnapshot();

    public Task<OnboardAutomationSubmitOutcome> SubmitSublotAsync(
        string sublot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the request the HMI button for <paramref name="action"/> makes, under the same
    /// availability the button is shown with. Never takes an operator identity or a recovery proof
    /// from the caller: both are read from the vehicle's own environment, as for the button.
    /// </summary>
    public Task<OnboardAutomationRecoveryOutcome> RequestRecoveryAsync(
        string action,
        string reason,
        CancellationToken cancellationToken = default);
}
