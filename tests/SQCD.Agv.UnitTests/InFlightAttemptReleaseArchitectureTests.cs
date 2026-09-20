namespace SQCD.Agv.UnitTests;

/// <summary>
/// The machine guard on "a claim on <c>_operationAttempts</c> is given up in exactly one place"
/// (batch 7-156, <c>trytoreachpeak0/8005-agv-onboard-hmi#156</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a test and not a comment.</b> Releasing that claim is also what pays an owed
/// recovery entry -- the <c>RecoveryRequired</c> snapshot withheld while another attempt had the
/// screen (onboard-hmi#152). <c>PublishOwedRecoveryEntry</c> neither retries nor schedules: whoever
/// releases the last claim pays the debt there and then, or nobody does. So a release written
/// straight against the set does not throw, does not log, and does not turn any test red -- it
/// leaves the recovery entry off the operator's screen for the rest of the process, which is the
/// exact fault onboard-hmi#156 exists to fix.
/// </para>
/// <para>
/// <b>It has already been got wrong once, which is why it is stated as an invariant.</b> The first
/// implementation of onboard-hmi#156 hooked one of the three release points and its own comment
/// said "the two places that answer can change". Both the hook and the sentence were counts, and
/// counts go stale the next time somebody adds a path that executes something.
/// </para>
/// <para>
/// <b>What it does not check.</b> Adding to the set is unconstrained -- a new path that executes
/// something is welcome to claim. Only the release is funnelled, because only the release has the
/// side effect.
/// </para>
/// </remarks>
public sealed class InFlightAttemptReleaseArchitectureTests
{
    /// <summary>The one method allowed to take a claim out of the set.</summary>
    private const string ReleaseMethod = "ReleaseInFlightAttempt";

    /// <summary>
    /// <c>_operationAttempts.Remove</c> is written in exactly one place in the whole product, and
    /// that place is <see cref="ReleaseMethod"/>.
    /// </summary>
    [Fact]
    public void OnlyTheOneReleaseMethodTakesAClaimOutOfTheInFlightSet()
    {
        (string File, int Line, string Text)[] releases =
        [
            .. ProductSources()
                .SelectMany(file => File.ReadAllLines(file)
                    .Select((text, index) => (File: file, Line: index + 1, Text: text)))
                .Where(line => line.Text.Contains("_operationAttempts.Remove", StringComparison.Ordinal))
        ];

        (string File, int Line, string Text) release = Assert.Single(releases);
        string[] enclosing = File.ReadAllLines(release.File)[..release.Line];
        int declaration = Array.FindLastIndex(
            enclosing,
            text => text.Contains(" private ", StringComparison.Ordinal)
                || text.Contains(" public ", StringComparison.Ordinal)
                || text.Contains(" internal ", StringComparison.Ordinal));

        Assert.True(
            declaration >= 0 && enclosing[declaration].Contains(ReleaseMethod, StringComparison.Ordinal),
            $"_operationAttempts.Remove at {Path.GetFileName(release.File)}:{release.Line} sits in "
            + $"\"{(declaration >= 0 ? enclosing[declaration].Trim() : "<no enclosing member>")}\", not in "
            + $"{ReleaseMethod}. Releasing the claim is what pays an owed recovery entry "
            + "(onboard-hmi#156); a release written anywhere else leaves it unpaid without failing "
            + "anything.");
    }

    /// <summary>
    /// The release method pays the debt. Stated here rather than left to the behavioural G2 tests:
    /// those go through one caller, and this is the property the single funnel is worth having for.
    /// </summary>
    [Fact]
    public void TheOneReleaseMethodPaysTheOwedRecoveryEntry()
    {
        string source = File.ReadAllText(BusinessServiceSource());
        int start = source.IndexOf($"private void {ReleaseMethod}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{ReleaseMethod} is not declared in {Path.GetFileName(BusinessServiceSource())}.");

        int end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find the end of {ReleaseMethod}.");

        string body = source[start..end];
        Assert.Contains("_operationAttempts.Remove", body, StringComparison.Ordinal);
        Assert.Contains("PublishOwedRecoveryEntry()", body, StringComparison.Ordinal);
    }

    private static string BusinessServiceSource() => Path.Combine(
        ProtocolIdentityArchitectureTests.RepositoryRoot(),
        "src",
        "SQCD.Agv.Wpf",
        "WireToGateBusinessService.cs");

    private static IEnumerable<string> ProductSources() => Directory.EnumerateFiles(
        Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "src"),
        "*.cs",
        SearchOption.AllDirectories)
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
