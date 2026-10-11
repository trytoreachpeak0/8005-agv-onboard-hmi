using System.Text.RegularExpressions;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// Every log source under <c>src/</c> is registered with a default technical-log category
/// (REQ-0272, 8005-agv-onboard-hmi#312).
/// </summary>
/// <remarks>
/// <para>
/// About 190 call sites write the onboard technical log and almost none of them name a category: the
/// category comes from <see cref="OnboardTechnicalLogCategories"/>, keyed by the source class name. A new
/// class that starts logging without a row there is filed under <c>LocalFault</c> at run time — nothing
/// breaks, and the area it actually records silently drops out of the requirement's coverage. So the gate
/// is on the source, the same way <c>LocalFailureCodeRegistryArchitectureTests</c> gates failure codes:
/// adding a source without deciding its category fails here.
/// </para>
/// <para>
/// The source must be <c>nameof(…)</c> or a string literal. Anything else (a variable, a call) cannot be
/// checked statically and is reported rather than skipped, because a scanner that skips what it cannot read
/// is a scanner that can be walked around.
/// </para>
/// <para>No <c>IntegrationSlice</c> trait, for the reason <see cref="OnboardAlarmSnapshotTests"/> gives.</para>
/// </remarks>
public sealed class TechnicalLogSourceRegistryArchitectureTests
{
    /// <summary>
    /// A log write: <c>.Write(</c>, a first argument naming a <c>LogSeverity</c> (which may itself contain
    /// calls with commas, as <c>_warned.TryAdd(key, 0) ? LogSeverity.Warning : LogSeverity.Debug</c> does),
    /// optionally an explicit category, then the source.
    /// </summary>
    private static readonly Regex LogWriteRegex = new(
        @"\.Write\(\s*(?<severity>(?:[^,;()]|\((?:[^()]|\([^()]*\))*\))*?\bLogSeverity\.\w+"
        + @"(?:[^,;()]|\((?:[^()]|\([^()]*\))*\))*?)\s*,\s*"
        + @"(?:OnboardTechnicalLogCategory\.\w+\s*,\s*)?"
        + @"(?:nameof\(\s*(?<source>\w+)\s*\)|""(?<source>[^""]*)""|(?<other>[^,;)]+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex LineCommentRegex = new(
        @"//[^\n]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BlockCommentRegex = new(
        @"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    [Fact]
    public void EveryLogSourceUnderSrcIsRegistered()
    {
        LogWrite[] writes = ScanSource();

        Assert.NotEmpty(writes);
        AssertAllRegistered(writes);
    }

    /// <summary>
    /// The scanner still sees the production call sites, in both shapes. A scanner that quietly stops
    /// matching passes the test above while covering nothing.
    /// </summary>
    [Fact]
    public void TheScannerStillFindsTheProductionCallSites()
    {
        LogWrite[] writes = ScanSource();
        string[] sources = writes.Select(write => write.Source).Distinct().ToArray();

        // Thirteen classes wrote the log when #312 was cut; the scan has to see at least all of them.
        foreach (string expected in new[]
        {
            "App", "MainViewModel", "OnboardController", "WireToGateBusinessService", "TcpJsonRuleGateway",
            "ModbusTcpIoModuleClient", "OnboardAlarmMonitor", "SlotGroupPresentation", "WireToGateSessionService",
            "WireToGateSessionClient", "JournaledOperationsFeed", "SingleInstanceGuard", "RunningInstanceWindow"
        })
        {
            Assert.Contains(expected, sources);
        }

        // The explicit-category shape, at the signal-change site.
        Assert.Contains(
            writes,
            write => write.Source == "ModbusTcpIoModuleClient"
                && write.Path == "src/SQCD.Agv.Infrastructure/ModbusTcpIoModuleClient.cs"
                && write.ExplicitCategory);

        // Counted independently of the scanner: every `LogSeverity.` that opens a Write argument list.
        int opened = CountSource(new Regex(@"\.Write\(\s*[^;]*?\bLogSeverity\.", RegexOptions.Singleline));
        Assert.True(writes.Length == opened, $"scanner read {writes.Length} writes, {opened} were opened");
    }

    [Theory]
    [InlineData("_logger.Write(LogSeverity.Warning, nameof(NotRegisteredAnywhere), \"x\");")]
    [InlineData("_logger.Write(\n    LogSeverity.Information,\n    OnboardTechnicalLogCategory.SignalChange,\n    nameof(NotRegisteredAnywhere),\n    \"x\");")]
    [InlineData("_logger.Write(ok ? LogSeverity.Information : LogSeverity.Warning, \"NotRegisteredAnywhere\", \"x\");")]
    [InlineData("_logger.Write(\n    _warned.TryAdd(key, 0) ? LogSeverity.Warning : LogSeverity.Debug,\n    nameof(NotRegisteredAnywhere),\n    line);")]
    public void TheScannerCatchesAnUnregisteredSourceInEveryShape(string syntheticSource)
    {
        LogWrite[] writes = ScanText(syntheticSource, "synthetic/Log.cs");

        LogWrite write = Assert.Single(writes);
        Assert.Equal("NotRegisteredAnywhere", write.Source);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => AssertAllRegistered(writes));
        Assert.Contains("NotRegisteredAnywhere", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceThatIsNotStaticallyKnownIsReportedNotSkipped()
    {
        LogWrite[] writes = ScanText("_logger.Write(LogSeverity.Information, someSource, \"x\");", "synthetic/Log.cs");

        Assert.Single(writes);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => AssertAllRegistered(writes));
        Assert.Contains("someSource", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WritesThatAreNotLogWritesAndCommentsAreIgnored()
    {
        const string syntheticSource = """
            Console.Write(line);
            writer.Write(buffer, 0, count);
            // _logger.Write(LogSeverity.Warning, nameof(NotRegisteredAnywhere), "x");
            /* _logger.Write(LogSeverity.Warning, nameof(AlsoNotRegistered), "x"); */
            """;

        Assert.Empty(ScanText(syntheticSource, "synthetic/Other.cs"));
    }

    private static LogWrite[] ScanSource()
    {
        string repositoryRoot = ProtocolIdentityArchitectureTests.RepositoryRoot();
        List<LogWrite> found = [];
        foreach (string path in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
            found.AddRange(ScanText(File.ReadAllText(path), relative));
        }

        return found.OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Line).ToArray();
    }

    private static int CountSource(Regex regex)
    {
        string repositoryRoot = ProtocolIdentityArchitectureTests.RepositoryRoot();
        return Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Sum(path => regex.Matches(StripComments(File.ReadAllText(path))).Count);
    }

    private static LogWrite[] ScanText(string source, string path)
    {
        string code = StripComments(source);
        return LogWriteRegex.Matches(code)
            .Select(match =>
            {
                Group source = match.Groups["source"].Success ? match.Groups["source"] : match.Groups["other"];
                return new LogWrite(
                    source.Value.Trim(),
                    match.Groups["source"].Success,
                    match.Value.Contains("OnboardTechnicalLogCategory.", StringComparison.Ordinal),
                    path,
                    code[..match.Index].Count(character => character == '\n') + 1);
            })
            .ToArray();
    }

    // Comments are blanked rather than removed so the reported line numbers stay true.
    private static string StripComments(string source) =>
        BlockCommentRegex.Replace(
            LineCommentRegex.Replace(source, match => new string(' ', match.Length)),
            match => new string('\n', match.Value.Count(character => character == '\n')));

    private static void AssertAllRegistered(IEnumerable<LogWrite> writes)
    {
        LogWrite[] violations = writes
            .Where(write => !write.SourceIsStatic || !OnboardTechnicalLogCategories.IsRegisteredSource(write.Source))
            .ToArray();
        if (violations.Length == 0)
        {
            return;
        }

        throw new InvalidDataException(
            "These log writes name a source with no default technical-log category in "
            + "OnboardTechnicalLogCategories (REQ-0272). Decide which of the seven areas the class records and "
            + "register it; a source that is not nameof(...) or a literal cannot be checked:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                violations.Select(write => $"  {write.Path}:{write.Line}  {write.Source}")));
    }

    private sealed record LogWrite(string Source, bool SourceIsStatic, bool ExplicitCategory, string Path, int Line);
}
