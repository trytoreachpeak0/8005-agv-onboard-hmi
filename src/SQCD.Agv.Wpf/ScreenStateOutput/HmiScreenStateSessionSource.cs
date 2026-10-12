using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf.ScreenStateOutput;

/// <summary>
/// The session as the screen-state snapshot sees it (onboard-hmi#314): the safety state the server accepted, and the
/// event raised after each acceptance moves. The view model may not change a single property when only that moves, so
/// without this the snapshot would wait for the next heartbeat.
/// </summary>
internal interface IHmiScreenStateSessionSource
{
    event EventHandler<ValueChangedEventArgs<WireToGateSessionSnapshot>>? StateChanged;

    WireToGateAcceptedSafety AcceptedSafety { get; }
}

/// <summary>
/// <see cref="IHmiScreenStateSessionSource"/> over the running session. It forwards subscriptions and has no handler of
/// its own, so it adds no entry point: the monitor's handler is the subscriber.
/// </summary>
internal sealed class HmiScreenStateSessionSource(WireToGateSessionService session) : IHmiScreenStateSessionSource
{
    public event EventHandler<ValueChangedEventArgs<WireToGateSessionSnapshot>>? StateChanged
    {
        add => session.StateChanged += value;
        remove => session.StateChanged -= value;
    }

    public WireToGateAcceptedSafety AcceptedSafety => session.Client.AcceptedSafety;
}
