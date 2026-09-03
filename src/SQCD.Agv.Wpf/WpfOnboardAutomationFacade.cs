using System.IO;
using SQCD.Agv.Core;
using SQCD.Agv.Application;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf;

/// <summary>
/// Adapter between the WPF/application layer and the embedded automation host.
/// Only business-safe methods cross this boundary; WPF controls and raw IO are
/// intentionally not exposed.
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
        return new OnboardAutomationSnapshot(
            _agvId,
            _controller.Current,
            _session.Current,
            _session.CurrentJourney,
            recovery,
            _business.CanSubmitSublot,
            _business.ExpectedSublot,
            recovery.UnsettledSlotOperationAttemptId,
            _clock.Now.ToUniversalTime());
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
