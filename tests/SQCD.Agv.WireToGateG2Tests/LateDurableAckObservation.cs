using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// Watches a session for how it takes the ack of one message that reaches it after the send stopped waiting
/// (onboard-hmi#250): settled as a late ack, or the session ending first, which is what such an ack does when nothing
/// takes it as late (onboard-hmi#270).
/// </summary>
/// <remarks>
/// Both are events, so a wait on this ends the moment the ack is handled either way: a test without the late-ack branch
/// goes red at once, and a test with it never waits on a deadline for a fact that has already been decided.
/// </remarks>
internal sealed class LateDurableAckObservation : IDisposable
{
    /// <summary>
    /// How long a wait may go with neither the late ack settled nor the session ended. Not a criterion: once the ack is
    /// written one of the two follows within milliseconds, so this only stops a broken test from hanging.
    /// </summary>
    internal static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(1);

    private readonly WireToGateSessionService _session;
    private readonly string _messageId;
    private readonly TaskCompletionSource<WireToGateLateDurableAck?> _outcome =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Subscribes at once, so construct it before the ack is written and neither outcome can be missed.</summary>
    internal LateDurableAckObservation(WireToGateSessionService session, string messageId)
    {
        _session = session;
        _messageId = messageId;
        _session.LateDurableAckReceived += OnLateDurableAckReceived;
        _session.StateChanged += OnStateChanged;
    }

    /// <summary>The late ack, or null when the session ended first.</summary>
    internal Task<WireToGateLateDurableAck?> Outcome => _outcome.Task;

    /// <summary>
    /// Runs <paramref name="deliver"/>, which puts an ack of <paramref name="messageId"/> on the wire, and returns the
    /// session's <c>LateDurableAckReceived</c> for it -- raised after the late-ack line is logged and the row settled, so
    /// both can be asserted at once. Fails as soon as the session ends instead.
    /// </summary>
    internal static async Task<WireToGateLateDurableAck> SettleAsync(
        WireToGateSessionService session,
        string messageId,
        Func<Task> deliver,
        Func<string> describe,
        CancellationToken cancellationToken)
    {
        using LateDurableAckObservation observation = new(session, messageId);
        await deliver();
        WireToGateLateDurableAck? ack;
        try
        {
            ack = await observation.Outcome.WaitAsync(HangGuard, cancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"Within {HangGuard} of its ack being written, {messageId} was neither settled as a late ack nor did the "
                + $"session end.{Environment.NewLine}{describe()}");
            throw;
        }

        if (ack is null)
        {
            Assert.Fail(
                $"The session ended instead of taking the ack of {messageId} as late.{Environment.NewLine}{describe()}");
        }

        return ack;
    }

    public void Dispose()
    {
        _session.LateDurableAckReceived -= OnLateDurableAckReceived;
        _session.StateChanged -= OnStateChanged;
    }

    private void OnLateDurableAckReceived(object? sender, ValueChangedEventArgs<WireToGateLateDurableAck> args)
    {
        if (string.Equals(args.Value.MessageId, _messageId, StringComparison.Ordinal))
        {
            _outcome.TrySetResult(args.Value);
        }
    }

    private void OnStateChanged(object? sender, ValueChangedEventArgs<WireToGateSessionSnapshot> args)
    {
        if (!args.Value.Connected)
        {
            _outcome.TrySetResult(null);
        }
    }
}
