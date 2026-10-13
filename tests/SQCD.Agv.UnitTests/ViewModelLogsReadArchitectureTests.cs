using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The guard on how tests read the operator log: no test reads <c>ViewModel.Logs</c> or <c>viewModel.Logs</c>
/// directly; every read goes through one of the two snapshot methods -- the G2 harness's in
/// <c>MultiDemandJourneyG2Tests.cs</c>, and <c>OperatorLogSnapshot.Of</c> for the view-model tests that construct
/// the view model themselves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> <c>Logs</c> is an <c>ObservableCollection</c> the view model appends to. In the product every
/// append runs on the WPF dispatcher; in these tests there is no dispatcher, so appends run on whatever
/// thread raised them -- some inside the harness's stand-in UI lock, and some outside it (the controller's
/// <c>StateChanged</c> reaches <c>MainViewModel.AppendOperatorRecord</c> on the controller's own thread).
/// A test enumerating the collection at that instant fails with "Collection was modified; enumeration
/// operation may not execute." It did, once, at <c>MultiDemandJourneyG2Tests.HandoffOverUnrecordedLoad.cs:522</c>
/// (onboard-hmi#285): a race in the test, not a product defect. The snapshot methods copy the log in a way
/// that tolerates those appends; a test that reads the collection itself does not. onboard-hmi#307 extended the
/// guard from the harness's <c>ViewModel.Logs</c> to the view-model tests' <c>viewModel.Logs</c>.
/// </para>
/// <para>
/// <b>The one exemption.</b> Handing the collection itself to something that subscribes to it -- the record log's
/// <c>OperatorRecordLog.TryStart(..., viewModel.Logs, ...)</c>, or a count of its <c>CollectionChanged</c>
/// handlers -- reads nothing, and a snapshot cannot stand in for it. Such a line carries a trailing
/// <c>// logs-reference: &lt;why&gt;</c> marker with a reason after it. The marker only covers a reference handed on
/// as it is, that is followed by <c>,</c> or <c>)</c>; a line that reads through the collection
/// (<c>viewModel.Logs.Any(...)</c>, <c>.Count</c>, an index) is reported with or without it.
/// </para>
/// <para>
/// <b>The marked lines are registered.</b> The shape check cannot tell a subscriber from something that enumerates
/// what it is given: <c>Assert.Contains(viewModel.Logs, ...)</c>, <c>Assert.Single(viewModel.Logs)</c>,
/// <c>string.Join(",", viewModel.Logs)</c> and a <c>foreach</c> all hand the reference on as it is (onboard-hmi#307
/// review S1: nine such forms, all let through). So <see cref="MarkedReferences"/> lists, per file, the code of
/// every marked line, and a marked line that is added, removed or rewritten turns the guard red until the list is
/// changed to match. No line is judged by meaning; the list makes a new or changed exemption a line in this file's
/// diff, where a reviewer reads it.
/// </para>
/// <para>
/// <b>What this cannot see.</b> It looks for two written forms, <c>ViewModel.Logs</c> and <c>viewModel.Logs</c>,
/// on a single line with no identifier character in front (so <c>harness.ViewModel.Logs</c>, a bare
/// <c>ViewModel.Logs</c> and a local <c>viewModel.Logs</c>; not <c>_viewModel.Logs</c> or <c>MainViewModel.Logs</c>),
/// in code: text after <c>//</c> on a line is dropped, so <c>cref="MainViewModel.Logs"</c> in a doc comment is not
/// a read. It does not follow the collection once it has left those forms: <c>harness.ViewModel</c> on one line
/// and <c>.Logs</c> on the next, <c>var vm = harness.ViewModel;</c> followed by <c>vm.Logs.Any(...)</c>, a view
/// model held under another name (<c>OperatorRecordLogDispatcherTests</c> reads <c>observed.Logs</c> and
/// <c>reference.Logs</c>, inside <c>Dispatcher.Invoke</c> or as a reference handed on), reflection, a marked
/// reference handed to something that enumerates it when that line is registered as it stands (the list pins the
/// text, not what the receiver does with it; a reviewer reading the list is the check), a <c>//</c> inside a string
/// before a form on the same line (the comment is cut at the first <c>//</c>, so the form is dropped with it), or a
/// <c>/* ... */</c> block comment that happens to contain a form (a false report,
/// not a miss). A string literal containing a form is reported too, which is why sample text elsewhere spells it
/// as a concatenation. Chasing every such spelling is not the goal; keeping the forms everyone copies from going
/// back in is.
/// </para>
/// </remarks>
public sealed class ViewModelLogsReadArchitectureTests
{
    /// <summary>The file that holds the harness and its snapshot method.</summary>
    private const string HarnessFile = "MultiDemandJourneyG2Tests.cs";

    /// <summary>The file that holds the view-model tests' snapshot method.</summary>
    private const string HelperFile = "OperatorLogSnapshot.cs";

    /// <summary>
    /// <c>ViewModel.Logs</c> or <c>viewModel.Logs</c> not preceded by an identifier character:
    /// <c>harness.ViewModel.Logs</c>, a bare <c>ViewModel.Logs</c> and <c>viewModel.Logs</c> match;
    /// <c>MainViewModel.Logs</c>, <c>_viewModel.Logs</c> and <c>harness.ViewModel.LogsCount</c> do not.
    /// </summary>
    private static readonly Regex Read = new(@"(?<!\w)[Vv]iewModel\.Logs(?!\w)", RegexOptions.CultureInvariant);

    /// <summary>A form handed on as it is: the next thing after it is an argument separator or a closing parenthesis.</summary>
    private static readonly Regex Reference = new(@"(?<!\w)[Vv]iewModel\.Logs(?=\s*[,)])", RegexOptions.CultureInvariant);

    /// <summary>The exemption marker, with at least one word of reason after it.</summary>
    private static readonly Regex Marker = new(@"^\s*logs-reference:\s*\S", RegexOptions.CultureInvariant);

    /// <summary>The marker as written, reason or not: what <see cref="MarkedLines"/> counts.</summary>
    private const string MarkerText = "logs-reference:";

    /// <summary>
    /// Every marked line in the test sources, as file name and the line's code (trimmed, comment dropped), in file
    /// order. Each of these hands <c>viewModel.Logs</c> to something that subscribes to it and reads no line:
    /// <c>OperatorRecordLog.TryStart</c> hooks <c>CollectionChanged</c>, and <c>Handlers</c> counts its handlers.
    /// Change this list only together with the line it names; a change here is an exemption a reviewer has to read.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> MarkedReferences = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["HmiScreenStateWiringTests.cs"] =
        [
            "Handlers(viewModel.Logs, \"CollectionChanged\"),",
        ],
        ["MultiDemandViewModelTests.cs"] =
        [
            "SQCD.Agv.Wpf.OperatorRecordOutput.OperatorRecordLog.TryStart(new LogSettings { OperatorRecords = true }, viewModel.Logs, log);",
        ],
        ["OperatorEventVisibilityTests.cs"] =
        [
            "viewModel.Logs,",
        ],
        ["OperatorRecordLogDispatcherTests.cs"] =
        [
            "viewModel.Logs,",
        ],
        ["OperatorRecordLogTests.cs"] =
        [
            "using OperatorRecordLog? log = OperatorRecordLog.TryStart(On, viewModel.Logs, logger);",
            "using OperatorRecordLog? log = OperatorRecordLog.TryStart(On, viewModel.Logs, logger);",
            "using OperatorRecordLog? log = OperatorRecordLog.TryStart(On, viewModel.Logs, logger);",
        ],
        ["RecoveryVectorG2Tests.OperatorRecordLog.cs"] =
        [
            "OperatorRecordLog? recordLog = OperatorRecordLog.TryStart(new LogSettings { OperatorRecords = true }, viewModel.Logs, log);",
        ],
    };

    [Fact]
    public void NoTestReadsViewModelLogsOutsideTheSnapshotMethods()
    {
        List<string> reads = [];
        foreach (string path in TestSources())
        {
            // This file carries the forms as sample text for the self-checks below.
            if (Path.GetFileName(path) is HarnessFile or HelperFile or nameof(ViewModelLogsReadArchitectureTests) + ".cs")
            {
                continue;
            }

            string relative = Path.GetRelativePath(ProtocolIdentityArchitectureTests.RepositoryRoot(), path);
            reads.AddRange(DirectReads(File.ReadAllText(path)).Select(line => $"{relative}:{line}"));
        }

        Assert.True(
            reads.Count == 0,
            $"{reads.Count} direct read(s) of ViewModel.Logs / viewModel.Logs; read the log through the harness's LogsSnapshot"
            + " or OperatorLogSnapshot.Of instead (a reference handed on to a subscriber takes a // logs-reference: marker):"
            + Environment.NewLine + string.Join(Environment.NewLine, reads));
    }

    /// <summary>
    /// The two snapshot files are exempt from the check above only because a snapshot method lives in each; each may
    /// read the collection once, in that method, and nowhere else.
    /// </summary>
    [Theory]
    [InlineData(HarnessFile)]
    [InlineData(HelperFile)]
    public void EachSnapshotFileReadsTheLogExactlyOnce(string file)
    {
        string path = Assert.Single(TestSources(), source => Path.GetFileName(source) == file);

        int[] lines = DirectReads(File.ReadAllText(path));

        Assert.True(
            lines.Length == 1,
            $"{file} reads the operator log on line(s) [{string.Join(", ", lines)}]; only the snapshot method may.");
    }

    /// <summary>
    /// Proves the check is not vacuous: each way the 20 original harness reads and the onboard-hmi#307 view-model reads
    /// were written is reported, and the doc comment that names the property is not.
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
            int count = ViewModel.Logs.Count;
            await WaitUntilAsync(() => viewModel.Logs.Count > 0);
            Assert.DoesNotContain(viewModel.Logs, line => line.Message.Contains("z", StringComparison.Ordinal));
            _viewModel.Logs.Clear();
            int snapshot = OperatorLogSnapshot.Of(viewModel).Length;
            () => $"logs={viewModel.Logs.Count}",
            """;

        Assert.Equal([3, 7, 10, 14, 15, 16, 19], DirectReads(source));
    }

    /// <summary>
    /// The exemption marker covers a reference handed on, and only that: a marked read is still reported, and so is a
    /// marker with no reason or a reference with no marker.
    /// </summary>
    [Fact]
    public void TheMarkerExemptsAReferenceHandedOnAndNothingElse()
    {
        const string source = """
            OperatorRecordLog.TryStart(settings, viewModel.Logs, log); // logs-reference: the record log subscribes to it
                viewModel.Logs, // logs-reference: the record log subscribes to it
            Handlers(viewModel.Logs) // logs-reference: counts its CollectionChanged handlers
            Assert.True(viewModel.Logs.Any(line => line.Message == "x")); // logs-reference: not a reference
            int count = viewModel.Logs.Count; // logs-reference: not a reference
            LogLineViewModel first = viewModel.Logs[0]; // logs-reference: not a reference
            OperatorRecordLog.TryStart(settings, viewModel.Logs, log); // logs-reference:
            OperatorRecordLog.TryStart(settings, viewModel.Logs, log);
            Use(viewModel.Logs, viewModel.Logs.Count); // logs-reference: one of the two is a read
            """;

        Assert.Equal([4, 5, 6, 7, 8, 9], DirectReads(source));
    }

    /// <summary>
    /// The marked lines in the test sources are exactly the registered ones: none added, none removed, none rewritten
    /// (onboard-hmi#307 review S1).
    /// </summary>
    [Fact]
    public void EveryMarkedLineIsRegistered()
    {
        Dictionary<string, string[]> actual = new(StringComparer.Ordinal);
        foreach (string path in TestSources())
        {
            if (Path.GetFileName(path) == nameof(ViewModelLogsReadArchitectureTests) + ".cs")
            {
                continue;
            }

            string[] marked = MarkedLines(File.ReadAllText(path));
            if (marked.Length > 0)
            {
                actual.Add(Path.GetFileName(path), marked);
            }
        }

        string[] mismatches = RegisterMismatches(actual, MarkedReferences);

        Assert.True(
            mismatches.Length == 0,
            "The // logs-reference: lines differ from MarkedReferences in ViewModelLogsReadArchitectureTests; a marked line"
            + " is an exemption, so change the list together with it:" + Environment.NewLine
            + string.Join(Environment.NewLine, mismatches));
    }

    /// <summary>
    /// Proves the register check is not vacuous: a marked line is read as its code alone, and an added, a missing, a
    /// rewritten and a duplicated marked line, and a file the register does not name, are each reported.
    /// </summary>
    [Fact]
    public void TheRegisterReportsEachDifference()
    {
        const string source = """
            OperatorRecordLog.TryStart(settings, viewModel.Logs, log); // logs-reference: the record log subscribes to it
                viewModel.Logs, // logs-reference:
            int count = OperatorLogSnapshot.Of(viewModel).Length; // not marked
            """;
        Assert.Equal(["OperatorRecordLog.TryStart(settings, viewModel.Logs, log);", "viewModel.Logs,"], MarkedLines(source));

        Dictionary<string, string[]> register = new(StringComparer.Ordinal)
        {
            ["A.cs"] = ["Use(viewModel.Logs, log);"],
            ["B.cs"] = ["Use(viewModel.Logs, log);", "Hook(viewModel.Logs);"],
            ["C.cs"] = ["Use(viewModel.Logs, log);"],
        };

        Assert.Empty(RegisterMismatches(register, register));
        Assert.Single(RegisterMismatches(
            new Dictionary<string, string[]>(register) { ["A.cs"] = ["Assert.Single(viewModel.Logs, log);"] },
            register));
        Assert.Single(RegisterMismatches(
            new Dictionary<string, string[]>(register) { ["B.cs"] = ["Use(viewModel.Logs, log);"] },
            register));
        Assert.Single(RegisterMismatches(
            new Dictionary<string, string[]>(register) { ["C.cs"] = ["Use(viewModel.Logs, log);", "Use(viewModel.Logs, log);"] },
            register));
        Assert.Single(RegisterMismatches(
            new Dictionary<string, string[]>(register) { ["D.cs"] = ["Use(viewModel.Logs, log);"] },
            register));
        Dictionary<string, string[]> withoutC = new(register, StringComparer.Ordinal);
        withoutC.Remove("C.cs");
        Assert.Single(RegisterMismatches(withoutC, register));
    }

    /// <summary>The code of each line that carries the marker, reason or not: trimmed, with the comment dropped.</summary>
    internal static string[] MarkedLines(string source)
    {
        List<string> marked = [];
        foreach (string line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0 && line.IndexOf(MarkerText, comment, StringComparison.Ordinal) >= 0)
            {
                marked.Add(line[..comment].Trim());
            }
        }

        return [.. marked];
    }

    /// <summary>One line per file whose marked lines differ from the register, in either direction.</summary>
    private static string[] RegisterMismatches(
        Dictionary<string, string[]> actual,
        IReadOnlyDictionary<string, string[]> register) =>
        [.. actual.Keys.Union(register.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(file => (
                File: file,
                Actual: actual.TryGetValue(file, out string[]? found) ? found : [],
                Registered: register.TryGetValue(file, out string[]? listed) ? listed : []))
            .Where(entry => !entry.Actual.SequenceEqual(entry.Registered, StringComparer.Ordinal))
            .Select(entry => $"{entry.File}: marked [{string.Join(" | ", entry.Actual)}], registered [{string.Join(" | ", entry.Registered)}]")];

    internal static int[] DirectReads(string source)
    {
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<int> matches = [];
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            string code = comment < 0 ? line : line[..comment];
            int forms = Read.Matches(code).Count;
            if (forms == 0)
            {
                continue;
            }

            bool marked = comment >= 0 && Marker.IsMatch(line[(comment + 2)..]);
            if (marked && Reference.Matches(code).Count == forms)
            {
                continue;
            }

            matches.Add(index + 1);
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
