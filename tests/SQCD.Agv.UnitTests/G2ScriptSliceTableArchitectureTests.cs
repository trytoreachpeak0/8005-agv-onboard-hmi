using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The guard on <c>scripts/run-w2g-g2.ps1</c>'s table of slices this line does not implement:
/// it is exactly the vendored slice index minus
/// <see cref="ProtocolVectorTestBindingArchitectureTests.SlicesThisLineImplements"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>-AllSlices</c> walks every slice in the index. A slice whose filter selects nothing used to be
/// skipped and listed, which turned "an implemented slice lost all its tests" (a mistyped trait, a
/// test project dropped from the solution) into fourteen PASS verdicts and exit code 0 -- the hole
/// docs/defects/20260908-empty-slice-filter-mints-a-green-g2.md closed for <c>-Slice</c>, reopened
/// for the run a batch exit cites (onboard-hmi#295 review, M1). The script now skips only the slices
/// its table names and refuses any other empty one, so the table has to be right: a slice missing
/// from it is refused for no reason, and an implemented slice wrongly in it is skipped again.
/// </para>
/// <para>
/// The table is a second copy of a fact this assembly already holds, kept in the script because the
/// script must not depend on this test project being in the solution -- that is one of the ways the
/// tests go missing. Same arrangement as <c>$expected</c> and
/// <see cref="ProtocolIdentityArchitectureTests.TheGateScriptExpectsTheSameIdentityAsTheAssembly"/>.
/// </para>
/// </remarks>
public sealed class G2ScriptSliceTableArchitectureTests
{
    [Fact]
    public void TheScriptsNotImplementedTableIsTheIndexMinusTheImplementedSlices()
    {
        string[] expected = NotImplementedSlices();

        Assert.Equal(expected, ScriptTable(File.ReadAllText(ScriptPath())));
    }

    /// <summary>
    /// Proves the comparison is not vacuous: the same script with an implemented slice added to the
    /// table, and with the table emptied, both disagree with the expected set.
    /// </summary>
    [Fact]
    public void ATableThatSkipsAnImplementedSliceOrForgetsAnUnimplementedOneDisagrees()
    {
        string script = File.ReadAllText(ScriptPath());
        string[] expected = NotImplementedSlices();
        Assert.NotEmpty(expected);

        string widened = TableLine().Replace(script, match => match.Value.Replace(
            "@(", "@('FP-IS-04', ", StringComparison.Ordinal), 1);
        string emptied = TableLine().Replace(script, "$slicesNotImplementedHere = @()", 1);

        Assert.NotEqual(expected, ScriptTable(widened));
        Assert.NotEqual(expected, ScriptTable(emptied));
    }

    private static string[] NotImplementedSlices() =>
    [
        .. VendoredSliceIndex.Slices()
            .Select(slice => slice.SliceId)
            .Where(sliceId => !ProtocolVectorTestBindingArchitectureTests.SlicesThisLineImplements
                .Contains(sliceId, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
    ];

    private static string[] ScriptTable(string script)
    {
        Match line = TableLine().Match(script);
        Assert.True(
            line.Success,
            "scripts/run-w2g-g2.ps1 no longer declares $slicesNotImplementedHere = @(...) on one line. "
            + "This test anchors on that table; update the anchor rather than deleting the comparison.");
        return
        [
            .. Regex.Matches(line.Groups["items"].Value, @"'(?<id>[^']*)'")
                .Select(match => match.Groups["id"].Value)
                .Order(StringComparer.Ordinal)
        ];
    }

    private static Regex TableLine() =>
        new(@"^\$slicesNotImplementedHere\s*=\s*@\((?<items>[^)\r\n]*)\)\s*$", RegexOptions.Multiline);

    private static string ScriptPath() =>
        Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "scripts", "run-w2g-g2.ps1");
}
