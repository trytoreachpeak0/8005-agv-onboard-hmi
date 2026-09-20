using System.Diagnostics;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The deadline a G2 wait is judged against, with the time this process spent stalled given back.
/// </summary>
/// <remarks>
/// <para>
/// Every wait in this assembly used to be judged by a plain wall clock: take the time now, add five
/// or ten seconds, fail once that instant passes. That is the wrong judge, and onboard-hmi#149
/// measured why. A probe running inside the test process, in parallel with every other test class,
/// timed how long a 5 ms <see cref="Task.Delay(int)"/> actually took. Idle, the worst of 13221
/// samples was 186 ms. With a second full solution test run alongside -- the load CI puts on this
/// assembly, and the load a full local run puts on it -- the worst of 11784 samples was 5171 ms,
/// while the median did not move (15.4 ms both times) and the thread pool never backed up
/// (15 threads, 17 queued work items). A median that holds while the tail reaches seconds is one
/// process-wide pause, not a queue growing: a GC pause on a machine that is paging.
/// </para>
/// <para>
/// A wall clock counts those seconds as time spent waiting for the fact. They are not: during a
/// pause this thread cannot look at the fact at all. So the wait fails for having been stopped,
/// which says nothing about whether the thing it waits for happened -- and it fails at random,
/// because pauses land where they land.
/// </para>
/// <para>
/// What this does instead: time each poll, and when one takes far longer than a poll can honestly
/// take, treat the excess as a stall and move the deadline out by that much, up to
/// <see cref="StallBudget"/>. On an idle machine no poll crosses the threshold, nothing is given
/// back, and a wait fails at exactly the same instant it does today -- a fact that will never
/// happen still fails in 2, 5 or 10 seconds, so nothing here weakens what these waits catch. The
/// compensation is capped, so this is a bounded wait and never a hang.
/// </para>
/// <para>
/// The base timeouts are deliberately left at what they were. The defect was the kind of judge,
/// not the number; raising the numbers would have pushed the same failure further out and left
/// nobody able to say what any of these waits should cost.
/// </para>
/// </remarks>
internal sealed class StallAwareDeadline
{
    /// <summary>
    /// A poll slower than this is a stall, not scheduling. Idle p99 was 48 ms and loaded p99 was
    /// 119 ms, so ordinary polls stay well under it; measured stalls were 5171 ms, so a real one
    /// always crosses it. The gap either side is what keeps this from firing by accident.
    /// </summary>
    internal static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The most that can ever be given back. The largest stall measured was 5171 ms, so this holds
    /// nearly two consecutive ones. It also bounds the worst case: the longest wait here is 10 s,
    /// so no wait can exceed 20 s, against a CI job timeout of 30 minutes that whole runs finish
    /// inside 3 to 4.
    /// </summary>
    internal static readonly TimeSpan StallBudget = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _baseTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly long _startedAt;
    private TimeSpan _stalled;

    internal StallAwareDeadline(TimeSpan baseTimeout, TimeSpan? pollInterval = null)
    {
        _baseTimeout = baseTimeout;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(5);
        _startedAt = Stopwatch.GetTimestamp();
    }

    /// <summary>The base timeout, as the message this wait fails with used to name it.</summary>
    internal TimeSpan BaseTimeout => _baseTimeout;

    internal bool HasExpired => Stopwatch.GetElapsedTime(_startedAt) > _baseTimeout + _stalled;

    /// <summary>How much has been given back so far. Exposed so a test can assert the cap.</summary>
    internal TimeSpan Stalled => _stalled;

    /// <summary>
    /// Waits one poll interval and charges any stall it observes to the budget rather than to the
    /// wait.
    /// </summary>
    internal async Task PollAsync(CancellationToken cancellationToken)
    {
        long before = Stopwatch.GetTimestamp();
        await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        ObservePoll(Stopwatch.GetElapsedTime(before));
    }

    /// <summary>
    /// The judgement itself, separated from the waiting so it can be tested without a real stall:
    /// a poll that took <paramref name="actual"/> either was ordinary scheduling, or was this
    /// process being stopped, and only the part beyond <see cref="StallThreshold"/> is the latter.
    /// </summary>
    internal void ObservePoll(TimeSpan actual)
    {
        // Nothing is given back yet, so this deadline is still the plain wall clock it replaces:
        // _stalled is held at zero and HasExpired reduces to "elapsed > base". The tests that
        // follow fail here, which is the point -- they are what says the wall clock was the defect.
        _ = actual;
        _stalled = TimeSpan.Zero;
    }

    /// <summary>
    /// How long this wait was actually allowed, for the failure message: the base timeout, plus
    /// what was given back, so a timeout in CI says on its face whether this machine was stalling.
    /// </summary>
    internal string Describe() =>
        _stalled > TimeSpan.Zero
            ? $"{Format(_baseTimeout)} (+{Format(_stalled)} given back for process stalls)"
            : Format(_baseTimeout);

    private static string Format(TimeSpan value) =>
        value.TotalSeconds >= 1
            ? $"{value.TotalSeconds:0.##}s"
            : $"{value.TotalMilliseconds:0}ms";
}
