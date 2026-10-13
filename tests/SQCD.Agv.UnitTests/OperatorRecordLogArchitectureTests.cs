using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The shape the operator record log rests on (onboard-hmi#317, T4 of <c>docs/W2G_HMI_OBSERVABILITY_RESEARCH.md</c>):
/// dispatcher isolation as research 4.3 item 1 asks, and the content boundary the coordinator set for it on 2026-10-12.
/// </summary>
/// <remarks>
/// <para>
/// <b>Isolation.</b> <c>OperatorRecordLog.OnLogsCollectionChanged</c> runs inside <c>Logs.Add</c>, inside a view-model
/// method, on the WPF dispatcher; an exception out of it latches the vehicle as <c>UNHANDLED_UI_ERROR</c> and leaves that
/// method half done. Its entry points are wrapped whole and its log writes wrapped again, checked with the same scan as
/// the screen-state snapshot (<see cref="HmiScreenStateIsolationArchitectureTests"/>, whose limits apply: source text,
/// comments and literals blanked, calls not followed). The behaviour is tested in
/// <c>SQCD.Agv.WireToGateG2Tests.OperatorRecordLogTests</c>.
/// </para>
/// <para>
/// <b>Content boundary.</b> The log line carries what the <c>LogLineViewModel</c> carries and nothing else: the record
/// log never names the view model, an input box, or a credential. What the scan cannot see is what a write site puts
/// into a line's message; that is the screen's own text, and the residual risk of PR for #317 lists the message
/// templates that insert operator-entered values.
/// </para>
/// <para>No <c>IntegrationSlice</c> trait: a diagnostic output, it proves no protocol vector.</para>
/// </remarks>
public sealed class OperatorRecordLogArchitectureTests
{
    private static readonly string[] SourcePath = ["src", "SQCD.Agv.Wpf", "OperatorRecordOutput", "OperatorRecordLog.cs"];

    /// <summary>Names the record log must not mention: where input boxes and credentials live.</summary>
    private static readonly string[] Forbidden =
    [
        "MainViewModel",
        "ScanText",
        "RecoveryReason",
        "ForcedHandoffSublot",
        "ForcedHandoffReceiverName",
        "HardwareRecoveryObservations",
        "Credential",
        "Environment",
    ];

    [Fact]
    public void EveryEntryPointIsWrappedWhole()
    {
        string code = HmiScreenStateIsolationArchitectureTests.Code(SourcePath);
        string[] entries =
        [
            .. Regex.Matches(code, @"\bvoid\s+(?<name>On\w+)\s*\(").Select(match => match.Groups["name"].Value),
            "TryStart",
            "Dispose",
        ];
        Assert.Contains("OnLogsCollectionChanged", entries);

        List<string> offences = [.. entries.SelectMany(entry => HmiScreenStateIsolationArchitectureTests.WrappedWholeOffences(code, entry))];
        offences.AddRange(HmiScreenStateIsolationArchitectureTests.WrappedWholeOffences(
            HmiScreenStateIsolationArchitectureTests.Code("src", "SQCD.Agv.Wpf", "App.xaml.cs"),
            "StartOperatorRecordLog"));

        Assert.True(offences.Count == 0, "Not wrapped whole in try/catch (Exception):\n" + string.Join("\n", offences));
    }

    [Fact]
    public void EveryHandlerCatchDisablesTheLogUnderItsOwnName()
    {
        string code = HmiScreenStateIsolationArchitectureTests.Code(SourcePath);
        string[] handlers = [.. Regex.Matches(code, @"\bvoid\s+(?<name>On\w+)\s*\(").Select(match => match.Groups["name"].Value)];
        Assert.NotEmpty(handlers);

        string[] offences =
        [
            .. handlers.Where(handler => !Regex.IsMatch(
                HmiScreenStateIsolationArchitectureTests.Body(code, handler)!,
                @"catch\s*\(\s*Exception\s+(?<v>\w+)\s*\)\s*\{\s*Disable\(\s*nameof\(\s*" + handler + @"\s*\)\s*,\s*\k<v>\s*\)\s*;\s*\}\s*\}\s*$"))
        ];

        Assert.True(offences.Length == 0, "These handlers' catch does not end in Disable(nameof(itself), …): " + string.Join(", ", offences));
    }

    /// <summary>Subscriptions are the handlers by name, and nothing is queued onto the dispatcher behind the scan's back.</summary>
    [Fact]
    public void SubscriptionsAreTheOnMethodsAndNothingIsQueuedOntoTheDispatcher()
    {
        string code = HmiScreenStateIsolationArchitectureTests.Code(SourcePath);
        string[] targets = [.. Regex.Matches(code, @"\+=\s*(?<target>[^;]+);").Select(match => match.Groups["target"].Value.Trim())];
        Assert.NotEmpty(targets);

        string[] offences =
        [
            .. targets.Where(target => !Regex.IsMatch(target, @"^(\w+\.)?On\w+$")).Select(target => $"subscription to {target}"),
            .. Regex.Matches(code, @"\b(BeginInvoke|InvokeAsync|Dispatcher)\b").Select(match => $"dispatcher use: {match.Value}"),
        ];

        Assert.True(offences.Length == 0, string.Join("\n", offences));
    }

    [Fact]
    public void EveryLogWriteIsWrappedAgain()
    {
        string code = HmiScreenStateIsolationArchitectureTests.Code(SourcePath);
        string app = HmiScreenStateIsolationArchitectureTests.Body(
            HmiScreenStateIsolationArchitectureTests.Code("src", "SQCD.Agv.Wpf", "App.xaml.cs"),
            "StartOperatorRecordLog")!;
        Assert.NotEmpty(Regex.Matches(code, @"logger\.Write\s*\("));

        string[] offences =
        [
            .. HmiScreenStateIsolationArchitectureTests.UnwrappedLogWrites(code).Select(index => $"OperatorRecordLog.cs at offset {index}"),
            .. HmiScreenStateIsolationArchitectureTests.UnwrappedLogWrites(app).Select(index => $"App.StartOperatorRecordLog at offset {index}"),
        ];

        Assert.True(offences.Length == 0, "Log writes outside their own try/catch (Exception): " + string.Join(", ", offences));
    }

    [Fact]
    public void TheRecordLogReadsNothingButTheLine()
    {
        string code = HmiScreenStateIsolationArchitectureTests.Code(SourcePath);
        string[] offences = [.. Forbidden.Where(name => Regex.IsMatch(code, @"\b" + name + @"\b"))];

        Assert.True(offences.Length == 0, "The operator record log must not name: " + string.Join(", ", offences));
    }

    /// <summary>The application starts it only through <c>TryStart</c>, handing it its own <c>logging</c> section and the view model's <c>Logs</c>.</summary>
    [Fact]
    public void TheApplicationStartsItWithTheLoggingSection()
    {
        string start = HmiScreenStateIsolationArchitectureTests.Body(
            HmiScreenStateIsolationArchitectureTests.Code("src", "SQCD.Agv.Wpf", "App.xaml.cs"),
            "StartOperatorRecordLog")!;

        Assert.Matches(@"\bOperatorRecordLog\.TryStart\(\s*settings\.Logging\s*,\s*viewModel\.Logs\s*,", start);
    }

    /// <summary>
    /// A line reaches <c>Logs</c> only through <c>MainViewModel.AppendLogLine</c>, and only <c>ClearLogsCommand</c> clears it:
    /// a write site that bypassed the helper would still be logged (the record log listens to the collection) but would
    /// skip the command refresh and the trim, and a second clear would be logged as the operator's.
    /// </summary>
    [Fact]
    public void LogsIsWrittenOnlyThroughAppendLogLineAndClearedOnlyByTheCommand()
    {
        string source = System.IO.Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "src");
        List<string> offences = [];
        int adds = 0;
        int clears = 0;
        foreach (string file in Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("Logs", StringComparison.Ordinal)))
        {
            string code = string.Join("\n", CSharpSourceLexer.Lex(File.ReadAllText(file)).Code);
            string name = System.IO.Path.GetFileName(file);
            foreach (Match indexer in Regex.Matches(code, @"\bLogs\s*\["))
            {
                offences.Add($"{name}: Logs[…] (indexer, at offset {indexer.Index})");
            }

            foreach (Match write in Regex.Matches(code, @"\bLogs\s*\.\s*(?<op>Add|Insert|Clear|RemoveAt|Remove|Move)\s*\("))
            {
                string op = write.Groups["op"].Value;
                string? helper = name == "MainViewModel.cs" ? HmiScreenStateIsolationArchitectureTests.Body(code, "AppendLogLine") : null;
                bool inHelper = helper is not null && code.IndexOf(helper, StringComparison.Ordinal) is var at and >= 0
                    && write.Index > at && write.Index < at + helper.Length;
                bool inTrim = name == "MainViewModel.cs" && op == "RemoveAt"
                    && HmiScreenStateIsolationArchitectureTests.Body(code, "TrimLogs") is { } trim
                    && code.IndexOf(trim, StringComparison.Ordinal) is var trimAt and >= 0
                    && write.Index > trimAt && write.Index < trimAt + trim.Length;
                if (op is "Add" && inHelper)
                {
                    adds++;
                }
                else if (op is "Clear" && name == "MainViewModel.cs")
                {
                    clears++;
                }
                else if (!inTrim)
                {
                    offences.Add($"{name}: Logs.{op}(");
                }
            }
        }

        Assert.Equal(1, adds);
        Assert.Equal(1, clears);
        Assert.True(offences.Count == 0, "Logs written outside AppendLogLine/TrimLogs/ClearLogsCommand:\n" + string.Join("\n", offences));
    }

    /// <summary>Every line built under <c>src/</c> names its source, so the technical log never says <c>Unknown</c> for a known site.</summary>
    [Fact]
    public void EveryLineBuiltUnderSrcNamesItsSource()
    {
        string source = System.IO.Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "src");
        List<string> offences = [];
        int built = 0;
        foreach (string file in Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("new LogLineViewModel", StringComparison.Ordinal)))
        {
            string code = string.Join("\n", CSharpSourceLexer.Lex(File.ReadAllText(file)).Code);
            foreach (string call in ConstructorCalls(code))
            {
                built++;
                if (!SourceNamed(call))
                {
                    offences.Add($"{System.IO.Path.GetFileName(file)}: {Regex.Replace(call, @"\s+", " ")}");
                }
            }
        }

        Assert.Equal(4, built);
        Assert.True(offences.Count == 0, "LogLineViewModel built without a known OperatorRecordSource:\n" + string.Join("\n", offences));
    }

    [Theory]
    [InlineData("Logs.Add(new LogLineViewModel(now, OperatorRecordKind.Warning, text));", false)]
    [InlineData("AppendLogLine(new LogLineViewModel(now, kind, text, OperatorRecordSource.Unknown));", false)]
    [InlineData("AppendLogLine(new LogLineViewModel(now, kind, text, OperatorRecordSource.OperatorRejection));", true)]
    public void TheSourceCheckRefusesAMissingOrUnknownSource(string code, bool named) =>
        Assert.Equal(named, SourceNamed(Assert.Single(ConstructorCalls(code))));

    private static bool SourceNamed(string call) =>
        Regex.IsMatch(call, @"\bOperatorRecordSource\.(?!Unknown\b)\w+");

    private static IEnumerable<string> ConstructorCalls(string code)
    {
        foreach (Match match in Regex.Matches(code, @"\bnew\s+LogLineViewModel\s*\("))
        {
            int open = match.Index + match.Length - 1;
            int depth = 0;
            for (int index = open; index < code.Length; index++)
            {
                depth += code[index] switch { '(' => 1, ')' => -1, _ => 0 };
                if (depth == 0)
                {
                    yield return code[open..(index + 1)];
                    break;
                }
            }
        }
    }

    /// <summary>The boundary check itself, on a shape it must refuse.</summary>
    [Theory]
    [InlineData("string text = _viewModel.ScanText;")]
    [InlineData("MainViewModel viewModel;")]
    public void NamingAnInputBoxOrTheViewModelIsCaught(string code) =>
        Assert.Contains(Forbidden, name => Regex.IsMatch(code, @"\b" + name + @"\b"));
}
