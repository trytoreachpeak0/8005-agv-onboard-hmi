using SQCD.Agv.Contracts;

namespace SQCD.Agv.Infrastructure;

/// <summary>Where a <see cref="WireToGateAcceptedSafety"/> was registered (onboard-hmi#314).</summary>
public enum WireToGateAcceptedSafetySource
{
    /// <summary>The version the client was configured with; no summary was ever sent for it in this process.</summary>
    Configured,

    /// <summary>The handshake's <c>SafetyStateSnapshot</c>, acknowledged.</summary>
    HandshakeSnapshot,

    /// <summary>A mid-session <c>SafetyStateSnapshot</c> sent on the server's request, acknowledged.</summary>
    RequestedSnapshot,

    /// <summary>A <c>SafetyStateChanged</c> acknowledged in its own send, or found acknowledged already.</summary>
    SafetyStateChanged,

    /// <summary>A <c>SafetyStateChanged</c> rebound to a new session and acknowledged there.</summary>
    ReboundSafetyStateChanged,

    /// <summary>A <c>SafetyStateChanged</c> whose acknowledgement arrived after its send had given up.</summary>
    LateSafetyStateChangedAck,

    /// <summary>The version a <c>SessionReadiness</c> reported, with the summary this client built for it if any.</summary>
    SessionReadiness,
}

/// <summary>
/// The safety state the control server has accepted, version and summary together (onboard-hmi#314, research section
/// 4.1): what the screen-state snapshot reports as the reason behind "departure unsafe". Observation only -- nothing in
/// the session or the business reads it.
/// </summary>
/// <param name="Safety">The summary sent under <paramref name="SafetyStateVersion"/>; null when it is not known.</param>
/// <param name="SessionGeneration">The session generation the version was accepted in, when known.</param>
public sealed record WireToGateAcceptedSafety(
    long SafetyStateVersion,
    long? SessionGeneration,
    WireToGateSafetySummaryPayload? Safety,
    WireToGateAcceptedSafetySource Source);

/// <summary>
/// Holds the latest <see cref="WireToGateAcceptedSafety"/> for the session client (onboard-hmi#314). Lock-free: every
/// write builds a new record and swaps the reference with <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>, so a
/// reader never sees a version paired with another version's summary. No I/O, never throws for a valid record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which record wins</b> is <see cref="Supersedes"/>. It is the session client's "only up, never down" rule for the
/// accepted version, with two same-version exceptions the code makes necessary (coordinator ruling B, 2026-10-12):
/// </para>
/// <list type="bullet">
/// <item>An unknown summary is replaced by a known one at the same version: the configured version starts with none,
/// and the first handshake that resends nothing reuses that very number (<c>WireToGateSessionClient</c>, handshake).</item>
/// <item>A handshake snapshot of a newer generation replaces the same version: the server starts every generation from
/// zero, so a handshake that resends nothing reuses the accepted number for a summary read from the IO just then, and
/// that summary is what the new generation holds.</item>
/// </list>
/// <para>
/// Anything else at the same or a lower version is dropped, so a late acknowledgement of an older send cannot replace a
/// newer summary.
/// </para>
/// <para>
/// <b>Summaries by version.</b> <c>SessionReadiness</c> carries only a version. The summaries this client built are
/// remembered by version as they are built, before they are sent (the newest <see cref="RememberedCount"/>), so
/// a readiness that reaches the client before the acknowledgement of the same send has been processed still finds the
/// summary. A version never built here is registered with the summary unknown -- never another version's.
/// </para>
/// </remarks>
internal sealed class WireToGateAcceptedSafetyRegister
{
    internal const int RememberedCount = 16;

    private WireToGateAcceptedSafety _current;
    private Remembered[] _remembered = [];

    internal WireToGateAcceptedSafetyRegister(long configuredVersion) =>
        _current = new WireToGateAcceptedSafety(configuredVersion, null, null, WireToGateAcceptedSafetySource.Configured);

    internal WireToGateAcceptedSafety Current => Volatile.Read(ref _current);

    /// <summary>Whether <paramref name="next"/> replaces <paramref name="current"/>; see the class remarks.</summary>
    internal static bool Supersedes(WireToGateAcceptedSafety next, WireToGateAcceptedSafety current)
    {
        if (next.SafetyStateVersion != current.SafetyStateVersion)
        {
            return next.SafetyStateVersion > current.SafetyStateVersion;
        }

        if (next.Safety is null)
        {
            return false;
        }

        if (current.Safety is null)
        {
            return true;
        }

        return next.Source == WireToGateAcceptedSafetySource.HandshakeSnapshot
            && next.SessionGeneration is long nextGeneration
            && (current.SessionGeneration is not long currentGeneration || nextGeneration > currentGeneration);
    }

    /// <summary>Registers <paramref name="next"/> if it supersedes the current record; returns whether it did.</summary>
    internal bool Register(WireToGateAcceptedSafety next)
    {
        ArgumentNullException.ThrowIfNull(next);
        while (true)
        {
            WireToGateAcceptedSafety current = Volatile.Read(ref _current);
            if (!Supersedes(next, current))
            {
                return false;
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, next, current), current))
            {
                return true;
            }
        }
    }

    /// <summary>Remembers the summary built for <paramref name="version"/>, before it is sent.</summary>
    internal void Remember(long version, WireToGateSafetySummaryPayload safety)
    {
        ArgumentNullException.ThrowIfNull(safety);
        while (true)
        {
            Remembered[] current = Volatile.Read(ref _remembered);
            Remembered[] next = [.. current.Where(entry => entry.Version != version).TakeLast(RememberedCount - 1), new(version, safety)];
            if (ReferenceEquals(Interlocked.CompareExchange(ref _remembered, next, current), current))
            {
                return;
            }
        }
    }

    /// <summary>The summary remembered for <paramref name="version"/>, or null.</summary>
    internal WireToGateSafetySummaryPayload? Recall(long version) =>
        Volatile.Read(ref _remembered).LastOrDefault(entry => entry.Version == version)?.Safety;

    private sealed record Remembered(long Version, WireToGateSafetySummaryPayload Safety);
}
