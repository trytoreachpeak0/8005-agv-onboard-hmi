using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The guard on the screen-state snapshot's "which path last wrote the banner" (onboard-hmi#313, T1a): every member of
/// <c>MainViewModel</c> that writes the banner -- <c>StateText</c>, <c>Guidance</c>, <c>HasWarning</c>, <c>HasError</c>, or
/// calls <c>PublishGuidance</c> -- also sets <c>_bannerSource</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The research named two banner writers (the controller snapshot and the W2G presentation); reading the
/// code found four (plus the operator rejection notice and the UI command failure). A fifth added without recording
/// itself would leave the snapshot naming the previous writer for a banner it did not write.
/// </para>
/// <para>
/// <b>What this cannot see.</b> A member is the text from one declaration at four-space indentation to the next; the
/// check is that <c>_bannerSource =</c> appears somewhere in it, not that it runs on every path that writes. Writes from
/// another file of the partial class are not scanned (there are none). <c>PublishGuidance</c> itself is exempt: it is
/// the shared helper of two writers, and both of its callers are held to the rule.
/// </para>
/// </remarks>
public sealed class HmiBannerWriteSiteArchitectureTests
{
    private const string Exempt = "PublishGuidance";

    [Fact]
    public void EveryBannerWriterRecordsItself()
    {
        string[] lines = File.ReadAllLines(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(), "src", "SQCD.Agv.Wpf", "ViewModels", "MainViewModel.cs"));
        (string Name, int Start, int End)[] members = Members(lines);

        (string Name, int Start, int End)[] writers = [.. members.Where(member => Body(lines, member)
            .Any(line => Regex.IsMatch(line, @"^\s*(StateText|Guidance|HasWarning|HasError)\s*=[^=]")
                || Regex.IsMatch(line, @"\bPublishGuidance\s*\(")))];
        string[] missing = [.. writers
            .Where(member => member.Name != Exempt)
            .Where(member => !Body(lines, member).Any(line => Regex.IsMatch(line, @"\b_bannerSource\s*=[^=]")))
            .Select(member => $"MainViewModel.cs:{member.Start + 1} {member.Name}")];

        Assert.True(missing.Length == 0, "These write the banner without setting _bannerSource:\n" + string.Join("\n", missing));
        // The four found on 2026-10-12; fewer would mean the scan lost them, not that they went away.
        Assert.True(writers.Count(member => member.Name != Exempt) >= 4, string.Join(", ", writers.Select(m => m.Name)));
    }

    private static IEnumerable<string> Body(string[] lines, (string Name, int Start, int End) member) =>
        lines[member.Start..member.End].Select(line => line.Split("//")[0]);

    private static (string Name, int Start, int End)[] Members(string[] lines)
    {
        int[] starts = [.. Enumerable.Range(0, lines.Length)
            .Where(index => Regex.IsMatch(lines[index], @"^    (public|internal|private|protected)\b"))];
        return [.. starts.Select((start, i) => (
            Regex.Match(lines[start], @"(\w+)\s*(?:\(|=>|$)").Groups[1].Value,
            start,
            i + 1 < starts.Length ? starts[i + 1] : lines.Length))];
    }
}
