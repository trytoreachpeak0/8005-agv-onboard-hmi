using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The guard that the screen-state snapshot only reads (onboard-hmi#313, T1a): nothing under
/// <c>src/SQCD.Agv.Wpf/ScreenState</c> writes to the view model, runs a command, calls a view-model method, or touches
/// focus, windows or input.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The snapshot is an observer; research section 4.3 makes "it does not change the HMI" rest on its code
/// calling getters and nothing else (item 2) and calling no window, focus or input API (item 4). Focus is not
/// cosmetic here: the scanner types into whichever window has focus (research, "焦点").
/// </para>
/// <para>
/// <b>What this cannot see.</b> It scans source text of the files in that directory, comments dropped. It does not
/// follow a getter into the view model: a getter that itself had a side effect would pass (none does today; the
/// commands' <c>CanExecute</c> read <c>CanSubmit</c>, <c>ScanText</c> and the log count). It does not see a write
/// reached through a local alias of the view model under another name than <c>viewModel</c>, or through reflection
/// other than <c>SetValue</c>, <c>SetMethod</c>/<c>GetSetMethod</c> and <c>Invoke</c>. Chasing every spelling is not
/// the goal; keeping the obvious forms out is.
/// </para>
/// </remarks>
public sealed class HmiScreenStateBuilderReadOnlyArchitectureTests
{
    private static readonly (string Name, string Pattern)[] Forbidden =
    [
        ("assignment to a view-model member", @"\bviewModel\.[\w.]+\s*(=(?!=|>)|\+=|-=|\+\+|--)"),
        ("view-model method call", @"\bviewModel\.[\w.]+\s*\("),
        ("ICommand.Execute", @"\.Execute\s*\("),
        ("RaiseCanExecuteChanged", @"\bRaiseCanExecuteChanged\b"),
        ("reflection write", @"\b(SetValue|SetMethod|GetSetMethod)\b"),
        ("reflection invoke", @"\.Invoke\s*\("),
        ("focus", @"\b(Focus|MoveFocus|Keyboard|FocusManager)\b"),
        ("window activation", @"\b(Activate|SetForegroundWindow|BringIntoView|Topmost|ShowDialog)\b"),
        ("input", @"\b(Mouse|SendKeys|SendInput|InputManager)\b"),
        ("dispatcher", @"\bDispatcher\b"),
        ("collection change", @"\.(Clear|Insert|RemoveAt|Remove)\s*\("),
        ("message box", @"\bMessageBox\b"),
    ];

    [Fact]
    public void TheSnapshotCodeOnlyReads()
    {
        string directory = Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "src", "SQCD.Agv.Wpf", "ScreenState");
        string[] files = Directory.GetFiles(directory, "*.cs");
        Assert.Contains(files, file => Path.GetFileName(file) == "HmiScreenStateBuilder.cs");

        string[] offenders = [.. files.SelectMany(file => File.ReadAllLines(file)
            .Select((line, index) => (Line: index + 1, Code: StripComment(line)))
            .SelectMany(line => Forbidden
                .Where(rule => Regex.IsMatch(line.Code, rule.Pattern))
                .Select(rule => $"{Path.GetFileName(file)}:{line.Line}: {rule.Name}: {line.Code.Trim()}")))];

        Assert.True(offenders.Length == 0, "The snapshot must only read:\n" + string.Join("\n", offenders));
    }

    /// <summary>The patterns themselves, so a typo in one does not turn the guard into a pass.</summary>
    [Theory]
    [InlineData("viewModel.ScanText = string.Empty;")]
    [InlineData("viewModel.HeldRecoveryCommand.Prompt = null;")]
    [InlineData("viewModel.ClearLogsCommand.Execute(null);")]
    [InlineData("viewModel.RefreshLoadingPhase();")]
    [InlineData("property.SetValue(viewModel, true);")]
    [InlineData("window.Activate();")]
    [InlineData("box.Focus();")]
    [InlineData("Dispatcher.Invoke(() => { });")]
    [InlineData("viewModel.Logs.Clear();")]
    public void EachFormIsCaught(string code) =>
        Assert.Contains(Forbidden, rule => Regex.IsMatch(code, rule.Pattern));

    [Theory]
    [InlineData("viewModel.HasWarning,")]
    [InlineData("viewModel.Guidance == notice")]
    [InlineData("string.Equals(viewModel.Guidance, notice, StringComparison.Ordinal))")]
    [InlineData("value = property.GetValue(value);")]
    [InlineData("((ICommand)Read(viewModel, command)!).CanExecute(null)")]
    public void ReadsAreNotCaught(string code) =>
        Assert.DoesNotContain(Forbidden, rule => Regex.IsMatch(code, rule.Pattern));

    private static string StripComment(string line)
    {
        int comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }
}
