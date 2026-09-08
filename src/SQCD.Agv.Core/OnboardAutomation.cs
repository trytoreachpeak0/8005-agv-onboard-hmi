namespace SQCD.Agv.Core;

/// <summary>
/// Read-only state and the single business command exposed to the local
/// automation host. The host never receives a WPF control or an IO primitive.
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
    DateTimeOffset ObservedAt);

public sealed record OnboardAutomationSubmitOutcome(
    bool Accepted,
    string? MessageId,
    string? ReasonCode,
    OnboardAutomationSnapshot Snapshot);

public interface IOnboardAutomationFacade
{
    public OnboardAutomationSnapshot ReadSnapshot();

    public Task<OnboardAutomationSubmitOutcome> SubmitSublotAsync(
        string sublot,
        CancellationToken cancellationToken = default);
}
