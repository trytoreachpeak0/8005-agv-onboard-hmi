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

    /// <summary>The boundary check itself, on a shape it must refuse.</summary>
    [Theory]
    [InlineData("string text = _viewModel.ScanText;")]
    [InlineData("MainViewModel viewModel;")]
    public void NamingAnInputBoxOrTheViewModelIsCaught(string code) =>
        Assert.Contains(Forbidden, name => Regex.IsMatch(code, @"\b" + name + @"\b"));
}
