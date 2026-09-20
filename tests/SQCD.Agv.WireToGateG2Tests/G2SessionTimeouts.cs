namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The connect and message timeouts every G2 harness hands the session client.
/// </summary>
/// <remarks>
/// <para>
/// These are a backstop, not a criterion. No test in this assembly asserts how quickly a connection
/// is made or an acknowledgement comes back; the three that do own timeout behaviour pass their own
/// short value (100 ms, 500 ms and 1 s) and are unaffected by what is here. All either value does
/// is stop a test hanging if the double never answers -- and the double's "drop" switches close the
/// socket rather than going quiet, so the vehicle learns immediately and never waits out
/// <see cref="Message"/>. Raising these therefore costs no run time.
/// </para>
/// <para>
/// Both were 2 seconds, written out in eleven places. Two seconds was stricter than the vehicle
/// ships with -- <c>ConnectTimeoutMs</c> and <c>MessageTimeoutMs</c> both default to 3000 in
/// <c>Configuration.cs</c> -- so the tests failed in conditions the product tolerates, which is
/// backwards for a backstop and was never argued for anywhere.
/// </para>
/// <para>
/// 15 seconds comes from measurement, not from taste. onboard-hmi#149 timed loopback connects from
/// inside a loaded test process: idle, 6034 connects had a median of 1.40 ms and a worst case of
/// 123 ms; with a second full solution test run alongside, 5427 connects had a median of 1.81 ms
/// and a worst case of 3694 ms. The tail is a process-wide pause, not a slow network -- the same
/// run measured a single 5171 ms stall in a 5 ms <c>Task.Delay</c>. 15 s clears the worst measured
/// connect four times over and the worst measured stall nearly three times, while staying far
/// inside a CI job that times out at 30 minutes and normally finishes in 3 to 4.
/// </para>
/// </remarks>
internal static class G2SessionTimeouts
{
    /// <summary>How long the client may take to open the loopback connection.</summary>
    internal static readonly TimeSpan Connect = TimeSpan.FromSeconds(15);

    /// <summary>How long the client waits for the double to answer one message.</summary>
    internal static readonly TimeSpan Message = TimeSpan.FromSeconds(15);
}
