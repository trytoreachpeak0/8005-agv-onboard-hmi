using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The screen-state writer and its dispatcher side call no window, focus or input API and change nothing on the view
/// model (onboard-hmi#315, T2 acceptance "源码禁用调用扫描"; research 4.3 items 2 and 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Every file under <c>src/SQCD.Agv.Wpf/ScreenStateOutput</c>: the monitor (the change handlers and the
/// timers), the writer and the file seam. The builder under <c>ScreenState</c> has its own, stricter scan
/// (<c>HmiScreenStateBuilderReadOnlyArchitectureTests</c>), which also forbids the dispatcher; this directory is where the
/// dispatcher is used, so this scan allows it and forbids the rest.
/// </para>
/// <para>
/// <b>Why focus matters.</b> The scanner types into whichever window has focus (research, "焦点"): an observer that took
/// focus would send a barcode into the wrong window.
/// </para>
/// <para>
/// <b>What it cannot see.</b> Source text with comments and literals blanked, line by line. It does not follow a call into
/// another file, and it recognises the view model only under the names <c>viewModel</c>/<c>_viewModel</c>. Subscribing
/// with <c>+=</c>/<c>-=</c> to <c>PropertyChanged</c>, <c>CollectionChanged</c> or <c>CanExecuteChanged</c> is the one
/// write to a view-model member it allows.
/// </para>
/// </remarks>
public sealed class HmiScreenStateOutputForbiddenCallArchitectureTests
{
    private static readonly (string Name, string Pattern)[] Forbidden =
    [
        ("assignment to a view-model member", @"\b_?viewModel\.[\w.]+\s*(=(?!=|>)|\+\+|--)"),
        ("view-model write other than an event subscription",
            @"\b_?viewModel\.(?![\w.]*(PropertyChanged|CollectionChanged|CanExecuteChanged)\s*[+-]=)[\w.]+\s*[+-]="),
        ("view-model method call", @"\b_?viewModel\.[\w.]+\s*\("),
        ("ICommand.Execute", @"\.Execute\s*\("),
        ("RaiseCanExecuteChanged", @"\bRaiseCanExecuteChanged\b"),
        ("reflection write", @"\b(SetValue|SetMethod|GetSetMethod)\b"),
        ("focus", @"\b(Focus|MoveFocus|Keyboard|FocusManager)\b"),
        ("window", @"\b(Window|MainWindow|Popup|Activate|SetForegroundWindow|BringIntoView|Topmost|ShowDialog|WindowInteropHelper)\b"),
        ("input", @"\b(Mouse|SendKeys|SendInput|InputManager|AutomationElement)\b"),
        ("message box", @"\b(MessageBox|ModalDialogs)\b"),
        ("native call", @"\b(DllImport|LibraryImport)\b"),
        ("view-model collection change", @"\b_?viewModel\.[\w.]+\.(Clear|Insert|RemoveAt|Remove|Add)\s*\("),
    ];

    [Fact]
    public void TheOutputCodeTouchesNoWindowFocusOrInputAndWritesNothingOnTheViewModel()
    {
        string directory = Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "src", "SQCD.Agv.Wpf", "ScreenStateOutput");
        string[] files = Directory.GetFiles(directory, "*.cs");
        Assert.Contains(files, file => Path.GetFileName(file) == "HmiScreenStateMonitor.cs");
        Assert.Contains(files, file => Path.GetFileName(file) == "HmiScreenStateFileWriter.cs");

        string[] offenders =
        [
            .. files.SelectMany(file => CSharpSourceLexer.Lex(File.ReadAllText(file)).Code
                .Select((code, index) => (Line: index + 1, Code: code))
                .SelectMany(line => Forbidden
                    .Where(rule => Regex.IsMatch(line.Code, rule.Pattern))
                    .Select(rule => $"{Path.GetFileName(file)}:{line.Line}: {rule.Name}: {line.Code.Trim()}")))
        ];

        Assert.True(offenders.Length == 0, "The screen-state output must not:\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("_viewModel.ScanText = string.Empty;")]
    [InlineData("_viewModel.Guidance += \"x\";")]
    [InlineData("_viewModel.ClearLogsCommand.Execute(null);")]
    [InlineData("_viewModel.RefreshWireToGateInputState();")]
    [InlineData("_viewModel.Logs.Clear();")]
    [InlineData("window.Activate();")]
    [InlineData("box.Focus();")]
    [InlineData("Keyboard.ClearFocus();")]
    [InlineData("ModalDialogs.Show(a, b, c, d);")]
    [InlineData("var helper = new WindowInteropHelper(w);")]
    public void EachFormIsCaught(string code) =>
        Assert.Contains(Forbidden, rule => Regex.IsMatch(code, rule.Pattern));

    [Theory]
    [InlineData("_viewModel.PropertyChanged += OnViewModelPropertyChanged;")]
    [InlineData("_viewModel.PropertyChanged -= OnViewModelPropertyChanged;")]
    [InlineData("foreach (LockerCardViewModel locker in _viewModel.Lockers)")]
    [InlineData("[_viewModel.ScannerSubmitCommand, _viewModel.ManualSubmitCommand, _viewModel.ClearLogsCommand];")]
    [InlineData("_flushTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = flushInterval };")]
    [InlineData("_sink.Submit(_build(), heartbeat: false);")]
    public void ReadsAndSubscriptionsAreNotCaught(string code) =>
        Assert.DoesNotContain(Forbidden, rule => Regex.IsMatch(code, rule.Pattern));
}
