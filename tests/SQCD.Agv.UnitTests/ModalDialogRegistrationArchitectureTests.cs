using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The guard on modal dialogs for the screen-state snapshot (onboard-hmi#313): every <c>MessageBox.Show</c> in the WPF
/// project goes through <c>ModalDialogs.Show</c>, which records that a dialog is open; every such call passes its title
/// as a string literal; and none passes the recorded call site itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> While a confirmation is up the view model still says its buttons can be pressed, but the operator can
/// only press the dialog (<c>docs/W2G_HMI_OBSERVABILITY_RESEARCH.md</c>, O7). The snapshot learns about dialogs only
/// from <c>ModalDialogs</c>; a dialog shown around it is one the snapshot cannot see -- failure 2 of section 6
/// ("漏项"). The literal title and the compiler-filled call site are the content boundary: the snapshot records both and
/// never the body, and either one built from data could carry a sublot the way two of the bodies do.
/// </para>
/// <para>
/// <b>What this cannot see.</b> It reads source text under <c>src/SQCD.Agv.Wpf</c>, so: a modal shown another way
/// (<c>Window.ShowDialog</c>, a <c>TaskDialog</c>, <c>System.Windows.Forms.MessageBox</c>) -- there is one today,
/// <c>RunningInstanceWindow</c>'s <c>ShowDialog</c>, shown only by a second process the single-instance guard turns
/// away, which has no snapshot to miss it; <c>MessageBox.Show</c> reached through an alias, <c>using static</c> or
/// reflection; a call in another project. Text after <c>//</c> is dropped, so a mention in a comment is not a call; a
/// <c>/* ... */</c> block is not. The title must be one literal as a whole argument: a literal concatenated with
/// anything, on the same line or the next, fails. A helper that forwards a title it was given
/// (<c>ShowRecoveryFailure</c>) has to be listed in <c>Forwarders</c>; an unlisted one fails the literal check at its
/// own call, which is the safe direction.
/// </para>
/// </remarks>
public sealed class ModalDialogRegistrationArchitectureTests
{
    private const string Wrapper = "ModalDialogs.cs";

    /// <summary>
    /// Helpers that show a dialog under a title they were given. Their own <c>ModalDialogs.Show</c> passes a parameter,
    /// so what is checked instead is that every call of the helper passes a literal.
    /// </summary>
    private static readonly string[] Forwarders = ["ShowRecoveryFailure"];

    [Fact]
    public void NoMessageBoxIsShownAroundTheRegistry()
    {
        // On the whole text, not line by line: "MessageBox" on one line and ".Show(" on the next is the same call.
        string[] offenders = WpfSources()
            .Where(file => Path.GetFileName(file) != Wrapper)
            .SelectMany(file =>
            {
                (string text, int[] lineOf) = Code(file);
                return Regex.Matches(text, @"\bMessageBox\s*\.\s*Show\s*\(")
                    .Select(match => $"{Path.GetRelativePath(Root(), file)}:{lineOf[match.Index]}");
            })
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These call MessageBox.Show directly; call ModalDialogs.Show instead, so the snapshot sees the dialog:\n"
                + string.Join("\n", offenders));
    }

    /// <summary>
    /// The 26 call sites the research counted (24 in <c>MainWindow.xaml.cs</c>, 2 in <c>App.xaml.cs</c>) all moved: at
    /// least that many registry calls, so a scan that lost them fails here. A floor, not an exact count, so a new dialog
    /// only has to be routed, not added here too.
    /// </summary>
    [Fact]
    public void EveryDialogTheResearchCountedGoesThroughTheRegistry()
    {
        Assert.InRange(RegistryCalls("MainWindow.xaml.cs"), 24, int.MaxValue);
        Assert.InRange(RegistryCalls("App.xaml.cs"), 2, int.MaxValue);
    }

    [Fact]
    public void EveryTitleIsALiteral()
    {
        string[] offenders = [.. AllCalls()
            .Where(call => call.Arguments.Count < 2 || !Regex.IsMatch(call.Arguments[1], "^\"[^\"]*\"$"))
            .Select(call => $"{call.Where}: {(call.Arguments.Count < 2 ? "(no title)" : call.Arguments[1])}")];

        Assert.True(
            offenders.Length == 0,
            "A dialog title must be a string literal (the snapshot records it):\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// The call site the snapshot records is filled by the compiler (<c>[CallerMemberName]</c>); a caller that passed one
    /// would put whatever it liked into the snapshot. Only the forwarder's own body passes it on, and that body is not a
    /// call this scan sees.
    /// </summary>
    [Fact]
    public void NoCallerPassesTheCallSite()
    {
        string[] offenders = [.. AllCalls()
            .Where(call => call.Arguments.Any(argument => Regex.IsMatch(argument, @"^callSite\s*:"))
                || (call.Callee == "ModalDialogs.Show"
                    ? call.Arguments.Count > 5
                        || call.Arguments.Count == 5 && !call.Arguments[4].StartsWith("MessageBoxResult.", StringComparison.Ordinal)
                    : call.Arguments.Count > 2))
            .Select(call => $"{call.Where}: {call.Callee}({string.Join(", ", call.Arguments)})")];

        Assert.True(offenders.Length == 0, "Leave the call site to the compiler:\n" + string.Join("\n", offenders));
    }

    private static IEnumerable<(string Where, string Callee, IReadOnlyList<string> Arguments)> AllCalls() =>
        WpfSources()
            .Where(file => Path.GetFileName(file) != Wrapper)
            .SelectMany(file => Calls(file).Select(call =>
                ($"{Path.GetRelativePath(Root(), file)}:{call.Line}", call.Callee, call.Arguments)));

    private static int RegistryCalls(string fileName)
    {
        string file = WpfSources().Single(path => Path.GetFileName(path) == fileName);
        return Regex.Matches(Code(file).Text, @"\bModalDialogs\s*\.\s*Show\s*\(").Count;
    }

    /// <summary>
    /// Each dialog call and each forwarder call with its arguments, leaving out the forwarders' own bodies.
    /// </summary>
    private static (int Line, string Callee, IReadOnlyList<string> Arguments)[] Calls(string file)
    {
        (string text, int[] lineOf) = Code(file);
        string callees = string.Join("|", Forwarders.Append(@"ModalDialogs\s*\.\s*Show"));
        return Regex.Matches(text, $@"(?<!\bvoid\s+)\b(?<callee>{callees})\s*\(")
            .Where(match => !InsideForwarder(text, match.Index))
            .Select(match => (
                lineOf[match.Index],
                Regex.Replace(match.Groups["callee"].Value, @"\s", string.Empty),
                (IReadOnlyList<string>)Arguments(text, match.Index + match.Length)))
            .ToArray();
    }

    /// <summary>Whether <paramref name="index"/> lies in the body of a forwarder, which is a single expression.</summary>
    private static bool InsideForwarder(string text, int index) =>
        Forwarders
            .Select(forwarder => Regex.Match(text, $@"\bvoid\s+{Regex.Escape(forwarder)}\s*\("))
            .Any(declaration => declaration.Success
                && declaration.Index < index
                && text.IndexOf(';', declaration.Index) > index);

    /// <summary>The top-level arguments of the call whose argument list starts at <paramref name="start"/>, trimmed.</summary>
    private static List<string> Arguments(string text, int start)
    {
        List<string> arguments = [];
        int depth = 0;
        int begin = start;
        bool inString = false;
        for (int index = start; index < text.Length; index++)
        {
            char c = text[index];
            if (inString)
            {
                if (c == '\\')
                {
                    index++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}' when depth == 0:
                    string last = text[begin..index].Trim();
                    if (last.Length > 0 || arguments.Count > 0)
                    {
                        arguments.Add(last);
                    }

                    return arguments;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    arguments.Add(text[begin..index].Trim());
                    begin = index + 1;
                    break;
            }
        }

        return arguments;
    }

    /// <summary>The file's code with comments dropped, joined by newlines, and the source line of each character.</summary>
    private static (string Text, int[] LineOf) Code(string file)
    {
        string[] lines = File.ReadAllLines(file);
        System.Text.StringBuilder text = new();
        List<int> lineOf = [];
        for (int index = 0; index < lines.Length; index++)
        {
            int comment = CommentStart(lines[index]);
            string code = (comment < 0 ? lines[index] : lines[index][..comment]) + "\n";
            text.Append(code);
            lineOf.AddRange(Enumerable.Repeat(index + 1, code.Length));
        }

        return (text.ToString(), [.. lineOf]);
    }

    /// <summary>The <c>//</c> that starts a comment, ignoring one inside a string literal (a URL, for instance).</summary>
    private static int CommentStart(string line)
    {
        bool inString = false;
        for (int index = 0; index < line.Length - 1; index++)
        {
            if (line[index] == '"' && (index == 0 || line[index - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (!inString && line[index] == '/' && line[index + 1] == '/')
            {
                return index;
            }
        }

        return -1;
    }

    private static IEnumerable<string> WpfSources() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src", "SQCD.Agv.Wpf"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Root() => ProtocolIdentityArchitectureTests.RepositoryRoot();
}
