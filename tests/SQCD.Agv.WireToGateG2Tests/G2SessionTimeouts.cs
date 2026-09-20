namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The connect timeout every G2 harness hands the session client.
/// </summary>
/// <remarks>
/// <para>
/// Only the connect timeout lives here, and that asymmetry is the point. The two timeouts sit side
/// by side in <c>WireToGateSessionOptions</c> and were both written as 2 seconds in twelve places,
/// which makes them look like one setting. They are not.
/// </para>
/// <para>
/// <b>Connect is a backstop.</b> Nothing in this assembly asserts how quickly a connection is made;
/// the double is always listening, so a connect either succeeds in about a millisecond or the
/// socket is refused outright. All this value does is stop a test hanging.
/// </para>
/// <para>
/// <b>Message is a criterion, and must not be touched.</b> The vehicle turns "no acknowledgement
/// by now" into operator-visible events -- <c>RESULT_ACK_PENDING</c>, releasing an unacknowledged
/// refusal, giving up on a dropped ack -- and eleven tests wait for exactly those. Raising it from
/// 2 seconds to 15 was tried under onboard-hmi#149 and failed all eleven, every round, with
/// messages like <c>Timed out after 10s waiting for: an operator event RESULT_ACK_PENDING</c>:
/// the vehicle was still waiting out its own timeout when the test gave up on it. So each call
/// site keeps the message timeout it had (2 seconds, or 5 in the two shape tests), and this class
/// deliberately offers no constant for it.
/// </para>
/// <para>
/// 15 seconds for connect comes from measurement. onboard-hmi#149 timed loopback connects from
/// inside a loaded test process: idle, 6034 connects had a median of 1.40 ms and a worst case of
/// 123 ms; with a second full solution test run alongside, 5427 connects had a median of 1.81 ms
/// and a worst case of 3694 ms, two of them past the old 2 second limit. That tail is not a slow
/// network -- at the same instant a 5 ms <c>Task.Delay</c> was late by 3694 ms as well, with the
/// thread pool idle at 8 threads, so the whole process was stopped. 2 seconds was also stricter
/// than the vehicle ships with (<c>ConnectTimeoutMs</c> defaults to 3000 in
/// <c>Configuration.cs</c>; <c>MessageTimeoutMs</c> defaults to 2500 there, and its own remarks
/// call it the upper bound on the gap between two heartbeats -- one more sign that it is a
/// criterion), which is backwards for a backstop. 15 s clears the worst measured
/// connect four times over while staying far inside a CI job that times out at 30 minutes and
/// normally finishes in 3 to 4.
/// </para>
/// </remarks>
internal static class G2SessionTimeouts
{
    /// <summary>How long the client may take to open the loopback connection.</summary>
    internal static readonly TimeSpan Connect = TimeSpan.FromSeconds(15);
}
