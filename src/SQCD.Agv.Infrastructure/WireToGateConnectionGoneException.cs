namespace SQCD.Agv.Infrastructure;

/// <summary>
/// A send that failed because the connection it was meant for is no longer there: closed while its answer was
/// awaited, or replaced by the next connection before it could be written (8005-agv-onboard-hmi#204).
/// </summary>
/// <remarks>
/// <para>
/// The failure belongs to a connection that is already gone, so there is nothing left to disconnect: whoever closed
/// it -- a disconnect, a reconnect, the heartbeat loop noticing -- also brings the next one up. A caller that answers
/// a send failure by disconnecting must not do so for this one. By the time the failure reaches it, the next session
/// may be in its handshake or already Ready, and the disconnect would take that one down instead.
/// </para>
/// <para>
/// A type rather than a reading of the session state, because the reading is not reliable at that moment: the waiter
/// of an answer is woken inside <c>CloseConnectionAsync</c>, before <c>DisconnectAsync</c> publishes anything, so the
/// failure handler still reads the old generation as the live one.
/// </para>
/// <para>
/// An <see cref="IOException"/>, as the plain one thrown in these places before this type existed was, so every
/// caller that catches I/O failures still catches it.
/// </para>
/// </remarks>
public sealed class WireToGateConnectionGoneException : IOException
{
    public WireToGateConnectionGoneException(string message)
        : base(message)
    {
    }

    public WireToGateConnectionGoneException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
