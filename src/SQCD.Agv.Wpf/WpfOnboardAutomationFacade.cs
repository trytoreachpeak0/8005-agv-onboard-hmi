using System.IO;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf;

/// <summary>
/// Adapter between the WPF/application layer and the embedded automation host.
/// Only business-safe methods cross this boundary; WPF controls and raw IO are
/// intentionally not exposed. Recovery requests cross it as the button's own
/// request, never as a way around the button's availability.
/// </summary>
public sealed class WpfOnboardAutomationFacade : IOnboardAutomationFacade
{
    private readonly string _agvId;
    private readonly OnboardController _controller;
    private readonly WireToGateSessionService _session;
    private readonly WireToGateBusinessService _business;
    private readonly IClock _clock;

    public WpfOnboardAutomationFacade(
        string agvId,
        OnboardController controller,
        WireToGateSessionService session,
        WireToGateBusinessService business,
        IClock clock)
    {
        _agvId = agvId;
        _controller = controller;
        _session = session;
        _business = business;
        _clock = clock;
    }

    public OnboardAutomationSnapshot ReadSnapshot()
    {
        WireToGateRecoveryState recovery = ReadRecoveryState();
        WireToGateHmiOperationSnapshot? operation = _business.CurrentOperationSnapshot;
        string? currentAttemptId = recovery.UnsettledSlotOperationAttemptId;
        string? currentPhase = null;
        if (operation is not null
            && operation.Stage != WireToGateHmiOperationStage.Completed
            && (currentAttemptId is null
                || string.Equals(
                    currentAttemptId,
                    operation.SlotOperationAttemptId,
                    StringComparison.Ordinal)))
        {
            currentAttemptId ??= operation.SlotOperationAttemptId;
            currentPhase = operation.Stage.ToProtocolPhase();
        }

        return new OnboardAutomationSnapshot(
            _agvId,
            _controller.Current,
            _session.Current,
            _session.CurrentJourney,
            recovery,
            _business.CanSubmitSublot,
            _business.ExpectedSublots,
            currentAttemptId,
            currentPhase,
            OnboardAutomationRecoveryActions.All
                .Where(action => FindRecoveryBlocker(action, diagnose: false) is null)
                .ToArray(),
            _clock.Now.ToUniversalTime());
    }

    public async Task<OnboardAutomationRecoveryOutcome> RequestRecoveryAsync(
        string action,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (FindRecoveryBlocker(action, diagnose: true) is { } blocker)
        {
            return new OnboardAutomationRecoveryOutcome(false, blocker, ReadSnapshot());
        }

        WireToGateRecoveryRequestOutcome outcome = await _business
            .RequestRecoveryAsync(action, reason, cancellationToken)
            .ConfigureAwait(false);
        return new OnboardAutomationRecoveryOutcome(
            outcome.Accepted,
            outcome.ReasonCode,
            ReadSnapshot());
    }

    /// <summary>
    /// The same two things that decide whether the HMI shows a recovery button: a faulted
    /// controller hides all of them (MainViewModel.ApplyWireToGatePresentationCore), and otherwise
    /// the business service's predicate for that one button decides.
    /// </summary>
    private string? FindRecoveryBlocker(string action, bool diagnose)
    {
        if (!OnboardAutomationRecoveryActions.All.Contains(action, StringComparer.Ordinal))
        {
            return "RECOVERY_ACTION_UNKNOWN";
        }

        if (_controller.Current.State == OnboardState.Faulted)
        {
            return "ONBOARD_FAULTED";
        }

        if (_business.CanRequestRecovery(action))
        {
            return null;
        }

        return diagnose
            ? _business.DiagnoseRecoveryUnavailable(action)
            : "RECOVERY_ACTION_NOT_AVAILABLE";
    }

    public async Task<OnboardAutomationSubmitOutcome> SubmitSublotAsync(
        string sublot,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string messageId = await _business
                .SubmitSublotAsync(sublot, "SCANNER", cancellationToken)
                .ConfigureAwait(false);
            return new OnboardAutomationSubmitOutcome(
                true,
                messageId,
                null,
                ReadSnapshot());
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            string reasonCode = string.IsNullOrWhiteSpace(exception.Message)
                ? "SUBLOT_SUBMIT_REJECTED"
                : exception.Message;
            return new OnboardAutomationSubmitOutcome(
                false,
                null,
                reasonCode,
                ReadSnapshot());
        }
    }

    private WireToGateRecoveryState ReadRecoveryState()
    {
        try
        {
            return _session.Journal
                .ReadRecoveryStateAsync()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return WireToGateRecoveryState.Empty;
        }
    }
}
