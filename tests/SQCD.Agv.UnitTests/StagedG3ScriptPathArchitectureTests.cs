using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The guard on <c>scripts/run-staged-g3-recovery-ack-drop.ps1</c>'s only path parameter:
/// <c>-StageRoot</c> is made absolute against the caller's location before anything else in the
/// script reads it.
/// </summary>
/// <remarks>
/// <para>
/// The script used to take <c>-StageRoot</c> as given and hand <c>Join-Path</c> results built on it to
/// consumers that do not resolve against PowerShell's location: <c>dotnet</c> child processes started
/// with their own working directory (the simulator, control-server and onboard dlls), the control
/// server's connection string and the onboard journal and log paths (resolved by those children
/// against their working directories), and .NET calls in this process (the proxy transcript, the
/// read-only SQLite connection, <c>GetRelativePath</c>, <c>WriteAllText</c>) that resolve against the
/// process's current directory, which <c>Set-Location</c> does not move. A relative value therefore
/// lands in a different place at each of them (onboard-hmi#297, the same shape as #287). The
/// conversion belongs at the entry, so every consumer downstream holds the same absolute path.
/// </para>
/// <para>
/// <b>What this cannot see.</b> It checks, in the file's text, that a conversion of the right shape is
/// written before every other line that names <c>$StageRoot</c>. It does not prove the conversion is
/// correct (a wrong but well-shaped call passes), it does not follow the value into a dot-sourced file
/// or a helper defined elsewhere, and it does not cover any other script. That relative values actually
/// land under the stage root is shown by the offline reproduction kept with #297's evidence, not by
/// this test.
/// </para>
/// </remarks>
public sealed class StagedG3ScriptPathArchitectureTests
{
    private const string ConversionLine =
        "$StageRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($StageRoot)";

    [Fact]
    public void TheStageRootIsMadeAbsoluteBeforeItsFirstUse()
    {
        string script = File.ReadAllText(ScriptPath());

        Assert.True(
            ConversionPrecedesEveryUse(script, "StageRoot"),
            "run-staged-g3-recovery-ack-drop.ps1 must make -StageRoot absolute before any other line reads $StageRoot.");
    }

    /// <summary>
    /// Proves the check is not vacuous: the same script with the conversion moved to just after the
    /// first line that uses <c>$StageRoot</c> is reported.
    /// </summary>
    [Fact]
    public void AConversionMovedAfterTheFirstUseIsReported()
    {
        string script = File.ReadAllText(ScriptPath()).Replace("\r\n", "\n", StringComparison.Ordinal);
        List<string> lines = [.. script.Split('\n')];
        int conversion = lines.FindIndex(line => line.StartsWith(ConversionLine, StringComparison.Ordinal));
        Assert.True(conversion >= 0, "conversion of -StageRoot not found");
        string moved = lines[conversion];
        lines.RemoveAt(conversion);
        int firstUse = lines.FindIndex(conversion, IsUse("StageRoot"));
        Assert.True(firstUse >= conversion, "a use of $StageRoot must follow the conversion in the original");
        lines.Insert(firstUse + 1, moved);

        Assert.False(ConversionPrecedesEveryUse(string.Join('\n', lines), "StageRoot"));
    }

    /// <summary>
    /// True when <paramref name="script"/> assigns <c>$parameter</c> its absolute form on a line before
    /// every other non-comment line that names <c>$parameter</c>, other than its declaration in the
    /// <c>param</c> block. False when there is no conversion, no other use (the check would then be
    /// vacuous), or any use comes before it.
    /// </summary>
    internal static bool ConversionPrecedesEveryUse(string script, string parameter)
    {
        Regex conversion = new(
            @"^\s*\$" + Regex.Escape(parameter) +
            @"\s*=\s*\$ExecutionContext\.SessionState\.Path\.GetUnresolvedProviderPathFromPSPath\(\s*\$" +
            Regex.Escape(parameter) + @"\s*\)\s*$",
            RegexOptions.IgnoreCase);
        Predicate<string> isUse = IsUse(parameter);

        string[] lines = script.Split('\n');
        int conversionLine = -1;
        int firstUseLine = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (conversionLine < 0 && conversion.IsMatch(line))
            {
                conversionLine = i;
                continue;
            }

            if (firstUseLine < 0 && isUse(line))
            {
                firstUseLine = i;
            }
        }

        return conversionLine >= 0 && firstUseLine >= 0 && conversionLine < firstUseLine;
    }

    /// <summary>
    /// A non-comment line naming <c>$parameter</c> that is not its <c>[string]$parameter</c>
    /// declaration.
    /// </summary>
    private static Predicate<string> IsUse(string parameter)
    {
        Regex reference = new(@"\$" + Regex.Escape(parameter) + @"\b", RegexOptions.IgnoreCase);
        Regex declaration = new(@"^\s*\[string\]\s*\$" + Regex.Escape(parameter) + @"\s*,?\s*$", RegexOptions.IgnoreCase);
        return line =>
        {
            string trimmed = line.TrimEnd('\r');
            return !trimmed.TrimStart().StartsWith('#')
                && !declaration.IsMatch(trimmed)
                && reference.IsMatch(trimmed);
        };
    }

    private static string ScriptPath() =>
        Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "scripts", "run-staged-g3-recovery-ack-drop.ps1");
}
