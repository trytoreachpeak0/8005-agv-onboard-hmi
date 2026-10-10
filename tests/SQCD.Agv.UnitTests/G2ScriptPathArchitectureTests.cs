using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The guard on <c>scripts/run-w2g-g2.ps1</c>'s path parameters: <c>-EvidenceRoot</c> and
/// <c>-ProtocolRoot</c> are made absolute against the caller's location before the script first
/// changes location.
/// </summary>
/// <remarks>
/// <para>
/// The script used to take both as given. A relative <c>-EvidenceRoot</c> built the evidence
/// directories in the right place, then the protocol G1 step <c>subst</c>-mapped the protocol
/// checkout to a drive letter, pushed to that drive's root and wrote its log there: every one of the
/// fifteen slices exited 1 without a <c>gate-result.json</c> (onboard-hmi#287). The same value
/// resolved after <c>Push-Location $hmiRoot</c> lands evidence under the repository root instead,
/// silently. The conversion belongs at the entry, so every consumer downstream holds the same
/// absolute path.
/// </para>
/// <para>
/// <b>What this cannot see.</b> It checks that a conversion of the right shape is written before the
/// first location change in the file's text. It does not prove the conversion is correct, and it
/// does not follow a location change hidden in a dot-sourced file or a helper defined elsewhere.
/// That the converted paths actually land where the caller meant is shown by running the script with
/// relative paths from two different directories -- the evidence for #287, not this test.
/// </para>
/// </remarks>
public sealed class G2ScriptPathArchitectureTests
{
    private static readonly string[] PathParameters = ["EvidenceRoot", "ProtocolRoot"];

    [Fact]
    public void ThePathParametersAreMadeAbsoluteBeforeTheFirstLocationChange()
    {
        string script = File.ReadAllText(ScriptPath());

        foreach (string parameter in PathParameters)
        {
            Assert.True(
                ConversionPrecedesFirstLocationChange(script, parameter),
                $"run-w2g-g2.ps1 must make -{parameter} absolute before its first Push-Location/Set-Location/subst.");
        }
    }

    /// <summary>
    /// True when <paramref name="script"/> assigns <c>$parameter</c> its absolute form on a line
    /// before the first line that changes location. False when there is no conversion, no location
    /// change (the check would then be vacuous), or the conversion comes after it.
    /// </summary>
    internal static bool ConversionPrecedesFirstLocationChange(string script, string parameter)
    {
        Regex conversion = new(
            @"^\s*\$" + Regex.Escape(parameter) +
            @"\s*=\s*\$ExecutionContext\.SessionState\.Path\.GetUnresolvedProviderPathFromPSPath\(\s*\$" +
            Regex.Escape(parameter) + @"\s*\)\s*$",
            RegexOptions.IgnoreCase);
        Regex locationChange = new(@"(^|[\s&(])(Push-Location|Set-Location|subst)(\s|$)", RegexOptions.IgnoreCase);

        string[] lines = script.Split('\n');
        int conversionLine = -1;
        int locationChangeLine = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (conversionLine < 0 && conversion.IsMatch(line))
            {
                conversionLine = i;
            }

            if (locationChangeLine < 0 && locationChange.IsMatch(line))
            {
                locationChangeLine = i;
            }
        }

        return conversionLine >= 0 && locationChangeLine >= 0 && conversionLine < locationChangeLine;
    }

    private static string ScriptPath() =>
        Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "scripts", "run-w2g-g2.ps1");
}
