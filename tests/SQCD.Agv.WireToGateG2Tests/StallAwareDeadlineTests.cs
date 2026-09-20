using System.Diagnostics;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The judge every G2 wait in this assembly is measured against.
/// </summary>
/// <remarks>
/// No <c>IntegrationSlice</c> trait: this claims no protocol vector. It covers the harness itself,
/// which onboard-hmi#149 changed from a wall clock to a deadline that gives back the time this
/// process spent stopped.
/// </remarks>
public sealed class StallAwareDeadlineTests
{
    /// <summary>
    /// The point of the whole change: on a machine that is not stalling, nothing is given back and
    /// a wait still fails when it always did. A deadline that quietly waited longer would be a
    /// weaker test dressed up as a fix.
    /// </summary>
    [Fact]
    public async Task ADeadlineThatSeesNoStallExpiresAtItsBaseTimeout()
    {
        StallAwareDeadline deadline = new(TimeSpan.FromMilliseconds(200));
        long startedAt = Stopwatch.GetTimestamp();

        while (!deadline.HasExpired)
        {
            await deadline.PollAsync(TestContext.Current.CancellationToken);
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
        Assert.Equal(TimeSpan.Zero, deadline.Stalled);
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(200),
            $"expired early, after {elapsed.TotalMilliseconds:0} ms");
        Assert.True(
            elapsed < TimeSpan.FromSeconds(2),
            $"took {elapsed.TotalMilliseconds:0} ms, which is not 'about 200 ms'");
    }

    /// <summary>
    /// A poll slower than the threshold is this process having been stopped, and the part beyond
    /// the threshold is handed back rather than charged to the wait.
    /// </summary>
    [Fact]
    public void TimeSpentStalledIsGivenBackToTheDeadline()
    {
        StallAwareDeadline deadline = new(TimeSpan.FromSeconds(5));

        deadline.ObservePoll(TimeSpan.FromMilliseconds(5171));

        Assert.Equal(
            TimeSpan.FromMilliseconds(5171) - StallAwareDeadline.StallThreshold,
            deadline.Stalled);
    }

    /// <summary>Ordinary scheduling is not a stall; the threshold sits above the loaded p99.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(48)]
    [InlineData(119)]
    [InlineData(250)]
    public void AnOrdinaryPollGivesNothingBack(int actualMilliseconds)
    {
        StallAwareDeadline deadline = new(TimeSpan.FromSeconds(5));

        deadline.ObservePoll(TimeSpan.FromMilliseconds(actualMilliseconds));

        Assert.Equal(TimeSpan.Zero, deadline.Stalled);
    }

    /// <summary>
    /// The cap is what keeps this a bounded wait. Without it a machine that stalls forever would
    /// hold a test open forever, and a hung test on the single self-hosted runner ends with the
    /// job cancelled and the runner session wedged.
    /// </summary>
    [Fact]
    public void TheTimeGivenBackIsCapped()
    {
        StallAwareDeadline deadline = new(TimeSpan.FromSeconds(5));

        for (int i = 0; i < 20; i++)
        {
            deadline.ObservePoll(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(StallAwareDeadline.StallBudget, deadline.Stalled);
    }
}
