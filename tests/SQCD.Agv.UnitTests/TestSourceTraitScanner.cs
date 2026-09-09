using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// One trait a test method carries: the trait's name and the value it claims.
/// </summary>
internal sealed record TraitClaim(string TraitName, string Value);

/// <summary>
/// One test method the scanner could attribute, with every requested trait it carries.
/// </summary>
/// <remarks>
/// The traits travel together on the method rather than as independent claims because the guards
/// ask questions about their co-occurrence -- "does this test's slice cover the vector this same
/// test proves" is not answerable from two flat lists.
/// </remarks>
internal sealed record TraitedTest(string TestName, IReadOnlyList<TraitClaim> Traits)
{
    public string[] ValuesOf(string traitName) =>
    [
        .. Traits
            .Where(trait => string.Equals(trait.TraitName, traitName, StringComparison.Ordinal))
            .Select(trait => trait.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];
}

/// <summary>
/// What one pass of the scanner collects.
/// </summary>
internal sealed record TraitScanResult(
    IReadOnlyList<TraitedTest> Tests,
    IReadOnlyList<string> TypeLevelClaims,
    IReadOnlyList<string> ClaimsOnTestsThatDoNotRun,
    IReadOnlyList<string> UnattributableClaims);

/// <summary>
/// Reads xUnit trait claims out of this repository's test <b>source</b> and attributes each to the
/// test method its attribute list decorates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why source and not reflection.</b> Half the vectors are proved by
/// <c>SQCD.Agv.WireToGateG2Tests</c>, which targets <c>net8.0-windows</c> because the business
/// service it drives lives in <c>src/SQCD.Agv.Wpf</c>. The guards that use this have to run
/// headless, so they live in <c>SQCD.Agv.UnitTests</c> (<c>net8.0</c>), and a <c>net8.0</c> project
/// cannot reference a <c>net8.0-windows</c> one -- reflection over <c>typeof(...).Assembly</c> would
/// see only this assembly and would report every G2-proved claim as missing. Reading the source of
/// both projects is the only form of the check that covers the repository rather than half of it.
/// The sibling ControlServer repository's equivalent guard does use reflection, and can: it has one
/// test project.
/// </para>
/// <para>
/// <b>What the source scan gives up, and what replaces it.</b> Reflection would know a skipped test
/// does not run, and would apply a class-level trait to every method in the class. The scanner
/// reproduces the first and refuses the second outright -- a class-level claim goes to
/// <see cref="TraitScanResult.TypeLevelClaims"/> for a guard to fail on, rather than being quietly
/// ignored, so the blind spot is a red rather than a silent under-count.
/// </para>
/// <para>
/// <b>Extracted here so two guards share one scanner.</b> It was
/// <see cref="ProtocolVectorTestBindingArchitectureTests"/>'s private scanner first, reading one
/// trait; the slice guard needs the same four hard-won behaviours -- comments blanked, skipped tests
/// refused, class-level claims reported, multi-line and combined attribute lists read whole -- over
/// two traits at once. A second copy of this parser is how the two guards would come to disagree
/// about what a test is.
/// </para>
/// </remarks>
internal static class TestSourceTraitScanner
{
    /// <summary>
    /// A <c>Fact</c> or <c>Theory</c> as an element of an attribute list, with its arguments.
    /// </summary>
    private static readonly Regex RunnableTestAttributeRegex = new(
        @"(?<=[\[,])\s*(?:Fact|Theory)\b\s*(?:\((?<arguments>[^)]*)\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The <c>Skip</c> argument specifically, not the word anywhere in the arguments -- a
    /// <c>DisplayName</c> of "Skips empty slots" is a test that runs.
    /// </summary>
    private static readonly Regex SkipArgumentRegex = new(
        @"\bSkip\s*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TypeDeclarationRegex = new(
        @"\b(?:class|record|struct|interface|enum)\s+[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A member declaration, which this repository always writes with an explicit accessibility
    /// modifier (<c>.editorconfig</c>: <c>dotnet_style_require_accessibility_modifiers = always</c>).
    /// </summary>
    /// <remarks>
    /// Requiring the modifier is what lets a claim that lands on something which is not a
    /// declaration at all be reported rather than bound to the file. Without it, the line after a
    /// block-commented-out test -- or after a stray bracket run -- would silently become the
    /// declaration a claim is attributed to.
    /// </remarks>
    private static readonly Regex MemberDeclarationRegex = new(
        @"^\s*(?:public|private|internal|protected)\b[^;=]*?\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// One compiled trait regex per set of trait names asked for, built once.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> TraitRegexCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Scans every hand-written <c>.cs</c> file under <paramref name="root"/>.
    /// </summary>
    public static TraitScanResult Scan(string root, params string[] traitNames)
    {
        ClaimAccumulator claims = new();

        foreach (string path in Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(IsHandWrittenSource)
            .Order(StringComparer.Ordinal))
        {
            ScanText(
                File.ReadAllText(path),
                Path.GetRelativePath(VendoredSliceIndex.RepositoryRoot(), path).Replace('\\', '/'),
                claims,
                traitNames);
        }

        return claims.ToResult();
    }

    /// <summary>
    /// Scans one source text, for the guards' own vacuity proofs.
    /// </summary>
    public static TraitScanResult ScanText(string source, string path, params string[] traitNames)
    {
        ClaimAccumulator claims = new();
        ScanText(source, path, claims, traitNames);
        return claims.ToResult();
    }

    /// <summary>
    /// Reads every attribute list in one file and attributes its trait claims to the declaration the
    /// list sits on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Comments are blanked first, so a commented-out test cannot go on proving its vector. That is
    /// the one blind spot here that failed <i>green</i> rather than red.
    /// </para>
    /// <para>
    /// A block then starts on a line whose trimmed form opens with <c>[</c> and runs until its
    /// brackets balance, so a multi-line attribute is read whole; consecutive lists are gathered
    /// into one block, which is how this repository writes them. A collection expression opening a
    /// line satisfies the same rule and is picked up too -- harmless, because a block yielding no
    /// trait match is discarded before anything else happens.
    /// </para>
    /// <para>
    /// The declaration is the line after the block, and it has to <b>be</b> a declaration.
    /// Everything else is reported as unattributable rather than bound to the file, because a claim
    /// the scanner cannot place is a claim nobody is checking.
    /// </para>
    /// </remarks>
    private static void ScanText(
        string source,
        string path,
        ClaimAccumulator claims,
        string[] traitNames)
    {
        Regex traitRegex = TraitRegex(traitNames);
        string[] lines = BlankOutComments(
            source.Replace("\r\n", "\n", StringComparison.Ordinal)).Split('\n');

        int index = 0;
        while (index < lines.Length)
        {
            if (!OpensAnAttributeList(lines[index]))
            {
                index++;
                continue;
            }

            int start = index;
            while (index < lines.Length && OpensAnAttributeList(lines[index]))
            {
                int depth = 0;
                do
                {
                    depth += BracketDelta(lines[index]);
                    index++;
                }
                while (index < lines.Length && depth > 0);
            }

            string block = string.Join('\n', lines[start..index]);
            TraitClaim[] claimed =
            [
                .. traitRegex.Matches(block).Select(match => new TraitClaim(
                    match.Groups["trait"].Value,
                    match.Groups["value"].Value))
            ];
            if (claimed.Length == 0)
            {
                continue;
            }

            // C# allows whitespace and comments between an attribute list and what it decorates,
            // and comments are blank by the time we get here, so the declaration is the next line
            // with anything on it.
            int declarationLine = index;
            while (declarationLine < lines.Length
                && string.IsNullOrWhiteSpace(lines[declarationLine]))
            {
                declarationLine++;
            }

            string declaration = declarationLine < lines.Length
                ? lines[declarationLine]
                : string.Empty;
            string site = $"{path}:{start + 1}";

            if (TypeDeclarationRegex.IsMatch(declaration))
            {
                Record(claims.TypeLevelClaims, claimed, site, "on a type declaration");
                continue;
            }

            Match member = MemberDeclarationRegex.Match(declaration);
            if (!member.Success)
            {
                Record(
                    claims.UnattributableClaims,
                    claimed,
                    site,
                    "on something that is not a declaration");
                continue;
            }

            if (!RunsAsATest(block))
            {
                Record(
                    claims.ClaimsOnTestsThatDoNotRun,
                    claimed,
                    site,
                    "on a member that does not run as a test");
                continue;
            }

            claims.Tests.Add(new TraitedTest($"{member.Groups["name"].Value} ({site})", claimed));
        }
    }

    /// <summary>
    /// The trait attribute, restricted to the names the caller asked for.
    /// </summary>
    /// <remarks>
    /// Restricted rather than "any trait" on purpose: a block that carries only traits nobody asked
    /// about is discarded whole, which is what keeps this scanner's cost proportional to the
    /// question and keeps an unrelated trait from turning an ordinary test into a claim a guard has
    /// to account for.
    /// </remarks>
    private static Regex TraitRegex(string[] traitNames)
    {
        if (traitNames.Length == 0)
        {
            throw new ArgumentException("At least one trait name is required.", nameof(traitNames));
        }

        return TraitRegexCache.GetOrAdd(
            string.Join('|', traitNames.Order(StringComparer.Ordinal)),
            key => new Regex(
                @"\bTrait\s*\(\s*""(?<trait>"
                + string.Join('|', key.Split('|').Select(Regex.Escape))
                + @")""\s*,\s*""(?<value>[^""]*)""\s*\)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant));
    }

    private static void Record(
        List<string> destination,
        IEnumerable<TraitClaim> claimed,
        string site,
        string what)
    {
        foreach (TraitClaim claim in claimed)
        {
            destination.Add($"{site} claims {claim.TraitName} {claim.Value} {what}");
        }
    }

    private static bool OpensAnAttributeList(string line) => line.TrimStart().StartsWith('[');

    /// <summary>
    /// Net bracket depth contributed by one line, ignoring brackets inside string literals.
    /// </summary>
    private static int BracketDelta(string line)
    {
        int delta = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                i = EndOfStringLiteral(line, i);
                continue;
            }

            if (line[i] == '[')
            {
                delta++;
            }
            else if (line[i] == ']')
            {
                delta--;
            }
        }

        return delta;
    }

    /// <summary>
    /// Replaces every comment with blanks, keeping the file's line numbering intact so reported
    /// sites still point at the right line.
    /// </summary>
    private static string BlankOutComments(string source)
    {
        StringBuilder builder = new(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '"')
            {
                int end = EndOfStringLiteral(source, i);
                builder.Append(source, i, end - i + 1);
                i = end;
                continue;
            }

            if (source[i] != '/' || i + 1 >= source.Length)
            {
                builder.Append(source[i]);
                continue;
            }

            int commentEnd;
            if (source[i + 1] == '/')
            {
                int newline = source.IndexOf('\n', i);
                commentEnd = newline < 0 ? source.Length : newline;
            }
            else if (source[i + 1] == '*')
            {
                int close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                commentEnd = close < 0 ? source.Length : close + 2;
            }
            else
            {
                builder.Append(source[i]);
                continue;
            }

            for (int j = i; j < commentEnd; j++)
            {
                builder.Append(source[j] == '\n' ? '\n' : ' ');
            }

            i = commentEnd - 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Index of the closing quote of the string literal opening at <paramref name="quote"/>,
    /// handling verbatim literals and escapes.
    /// </summary>
    private static int EndOfStringLiteral(string text, int quote)
    {
        bool verbatim = quote > 0 && text[quote - 1] == '@';
        for (int i = quote + 1; i < text.Length; i++)
        {
            if (!verbatim && text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] != '"')
            {
                continue;
            }

            if (verbatim && i + 1 < text.Length && text[i + 1] == '"')
            {
                i++;
                continue;
            }

            return i;
        }

        return text.Length - 1;
    }

    private static bool RunsAsATest(string attributeBlock) => RunnableTestAttributeRegex
        .Matches(attributeBlock)
        .Any(match => !SkipArgumentRegex.IsMatch(match.Groups["arguments"].Value));

    /// <summary>
    /// Hand-written source only: <c>bin/</c> and <c>obj/</c> hold generated and copied files, and a
    /// stale build output there would let a deleted test go on binding its vector.
    /// </summary>
    private static bool IsHandWrittenSource(string path)
    {
        string normalised = path.Replace('\\', '/');
        return !normalised.Contains("/bin/", StringComparison.Ordinal)
            && !normalised.Contains("/obj/", StringComparison.Ordinal);
    }

    /// <summary>
    /// What one pass collects. The four lists always travel together, so they travel as one thing
    /// rather than as four parameters.
    /// </summary>
    private sealed class ClaimAccumulator
    {
        public List<TraitedTest> Tests { get; } = [];

        public List<string> TypeLevelClaims { get; } = [];

        public List<string> ClaimsOnTestsThatDoNotRun { get; } = [];

        public List<string> UnattributableClaims { get; } = [];

        public TraitScanResult ToResult() => new(
            Tests, TypeLevelClaims, ClaimsOnTestsThatDoNotRun, UnattributableClaims);
    }
}
