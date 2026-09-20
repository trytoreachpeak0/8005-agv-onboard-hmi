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
    /// <remarks>
    /// Asserted through <see cref="StallAwareDeadline.ObservePoll"/> rather than through real polls,
    /// because this is the guard and a guard must not itself be flaky. Written against real timing
    /// it would fail whenever this machine stalled -- the very condition the code under test exists
    /// for -- and the cheapest way to green a flaky guard is to delete its assertions, which is how
    /// a guard quietly stops guarding (2026-09-20 审查 S1).
    /// </remarks>
    [Fact]
    public void ADeadlineThatSeesNoStallGivesNothingBack()
    {
        StallAwareDeadline deadline = new(TimeSpan.FromMilliseconds(200));

        for (int i = 0; i < 40; i++)
        {
            deadline.ObservePoll(TimeSpan.FromMilliseconds(15));
        }

        Assert.Equal(TimeSpan.Zero, deadline.Stalled);
    }

    /// <summary>
    /// The same claim against the real clock, asserted only from below: a deadline must not expire
    /// before its base timeout. There is deliberately no upper bound here -- a stall would push the
    /// real elapsed time out, and that is correct behaviour, not a failure.
    /// </summary>
    [Fact]
    public async Task ADeadlineNeverExpiresBeforeItsBaseTimeout()
    {
        StallAwareDeadline deadline = new(TimeSpan.FromMilliseconds(200));
        long startedAt = Stopwatch.GetTimestamp();

        while (!deadline.HasExpired)
        {
            await deadline.PollAsync(TestContext.Current.CancellationToken);
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(200),
            $"expired early, after {elapsed.TotalMilliseconds:0} ms");
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

        // A literal, not StallAwareDeadline.StallBudget: asserting a constant against itself passes
        // for every value the constant could take (2026-09-20 审查 S2).
        Assert.Equal(TimeSpan.FromSeconds(10), deadline.Stalled);
    }

    /// <summary>
    /// The budget is the one loosening knob this design has, so it gets a guard of its own.
    /// </summary>
    /// <remarks>
    /// Raising it is the exact move this ticket exists to forbid -- "just make the timeout bigger",
    /// wearing a different hat. Every wait in this assembly can be stretched by changing this single
    /// number, and no other test would notice. 10 seconds is what the measurement supports: the
    /// worst single stall observed was 5171 ms, so this holds nearly two consecutive ones.
    /// **Changing it means changing this test first, on purpose, with a new measurement.**
    /// </remarks>
    [Fact]
    public void TheBudgetStaysWithinWhatWasMeasured()
    {
        Assert.True(
            StallAwareDeadline.StallBudget <= TimeSpan.FromSeconds(10),
            $"停顿补偿上限被放大到 {StallAwareDeadline.StallBudget.TotalSeconds:0.#} 秒。"
            + "它是全族等待唯一的放松旋钮：调大它等于把每一条等待的上界一起放大，"
            + "而没有任何别的测试会发现。要改先拿出新的停顿实测，并改这条测试。");
    }
}
