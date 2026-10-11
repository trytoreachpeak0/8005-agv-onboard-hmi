using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The guard on how G2 tests read the operator log: no test reads <c>ViewModel.Logs</c> directly; every
/// read goes through the harness's one snapshot method in <c>MultiDemandJourneyG2Tests.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> <c>Logs</c> is an <c>ObservableCollection</c> the view model appends to. In the product every
/// append runs on the WPF dispatcher; in the G2 harness there is no dispatcher, so appends run on whatever
/// thread raised them -- some inside the harness's stand-in UI lock, and some outside it (the controller's
/// <c>StateChanged</c> reaches <c>MainViewModel.AppendOperatorRecord</c> on the controller's own thread).
/// A test enumerating the collection at that instant fails with "Collection was modified; enumeration
/// operation may not execute." It did, once, at <c>MultiDemandJourneyG2Tests.HandoffOverUnrecordedLoad.cs:522</c>
/// (onboard-hmi#285): a race in the test, not a product defect. The snapshot method copies the log in a way
/// that tolerates those appends; a test that reads the collection itself does not.
/// </para>
/// <para>
/// <b>What this cannot see.</b> It looks for one written form, <c>.ViewModel.Logs</c>, in code (text after
/// <c>//</c> on a line is dropped, so <c>cref="MainViewModel.Logs"</c> in a doc comment is not a read). It
/// does not follow the collection once it has left that form: <c>var logs = harness.ViewModel;</c> followed
/// by <c>logs.Logs.Any(...)</c>, a view model held in a local and read as <c>viewModel.Logs</c> (the
/// single-threaded view-model tests such as <c>FatalFaultLatchViewModelTests</c> do exactly that, and are
/// not G2 harness tests), reflection, or a <c>/* ... */</c> block comment that happens to contain the form.
/// Chasing every such spelling is not the goal; keeping the one form everyone copies from going back in is.
/// </para>
/// </remarks>
public sealed class ViewModelLogsReadArchitectureTests
{
    /// <summary>The file that holds the harness and its snapshot method: the one place allowed to read it.</summary>
    private const string SnapshotFile = "MultiDemandJourneyG2Tests.cs";

    private static readonly Regex DirectRead = new(@"\.ViewModel\.Logs(?!\w)", RegexOptions.CultureInvariant);

    private static readonly Regex AnyRead = new(@"(?<!\w)ViewModel\.Logs(?!\w)", RegexOptions.CultureInvariant);

    [Fact]
    public void NoTestReadsViewModelLogsOutsideTheSnapshotMethod()
    {
        List<string> reads = [];
        foreach (string path in TestSources())
        {
            // This file carries the forms as sample text for the self-check below.
            if (Path.GetFileName(path) is SnapshotFile or nameof(ViewModelLogsReadArchitectureTests) + ".cs")
            {
                continue;
            }

            string relative = Path.GetRelativePath(ProtocolIdentityArchitectureTests.RepositoryRoot(), path);
            reads.AddRange(DirectReads(File.ReadAllText(path)).Select(line => $"{relative}:{line}"));
        }

        Assert.True(
            reads.Count == 0,
            $"{reads.Count} direct read(s) of ViewModel.Logs; read the log through the harness snapshot method instead:"
            + Environment.NewLine + string.Join(Environment.NewLine, reads));
    }

    /// <summary>
    /// The harness file is exempt from the check above only because the snapshot method lives there; it may
    /// read the collection once, in that method, and nowhere else.
    /// </summary>
    [Fact]
    public void TheHarnessFileReadsViewModelLogsExactlyOnce()
    {
        string path = Assert.Single(TestSources(), source => Path.GetFileName(source) == SnapshotFile);

        int[] lines = CodeLines(File.ReadAllText(path), AnyRead);

        Assert.True(
            lines.Length == 1,
            $"{SnapshotFile} reads ViewModel.Logs on line(s) [{string.Join(", ", lines)}]; only the snapshot method may.");
    }

    /// <summary>
    /// Proves the check is not vacuous: each way the 20 original reads were written is reported, and the doc
    /// comment that names the property is not.
    /// </summary>
    [Fact]
    public void TheCheckReportsEachWrittenFormAndIgnoresComments()
    {
        const string source = """
            /// nothing. Asserted on <see cref="MainViewModel.Logs"/>, the layer the operator reads;
            await harness.WaitUntilAsync(
                () => harness.ViewModel.Logs.Any(line => line.Message.Contains("x")),
                "x",
                token);
            Assert.Contains(
                afterRestart.ViewModel.Logs,
                line => line.Message == "y");
            System.Collections.ObjectModel.ObservableCollection<LogLineViewModel> lines =
                harness.ViewModel.Logs;
            // harness.ViewModel.Logs is not read here
            string[] fine = harness.LogsSnapshotLike().Select(line => line.Message).ToArray();
            int other = harness.ViewModel.LogsCount;
            """;

        Assert.Equal([3, 7, 10], DirectReads(source));
    }

    internal static int[] DirectReads(string source) => CodeLines(source, DirectRead);

    /// <summary>One-based numbers of the lines whose code, with any <c>//</c> comment dropped, matches.</summary>
    private static int[] CodeLines(string source, Regex pattern)
    {
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<int> matches = [];
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            string code = comment < 0 ? line : line[..comment];
            if (pattern.IsMatch(code))
            {
                matches.Add(index + 1);
            }
        }

        return [.. matches];
    }

    private static IEnumerable<string> TestSources()
    {
        string tests = Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "tests");
        char separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                && !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
    }
}
