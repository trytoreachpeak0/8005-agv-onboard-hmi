using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The guard on modal dialogs for the screen-state snapshot (onboard-hmi#313): every <c>MessageBox.Show</c> in the WPF
/// project goes through <c>ModalDialogs.Show</c>, which records that a dialog is open, and every such call passes its
/// title as a string literal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> While a confirmation is up the view model still says its buttons can be pressed, but the operator can
/// only press the dialog (<c>docs/W2G_HMI_OBSERVABILITY_RESEARCH.md</c>, O7). The snapshot learns about dialogs only
/// from <c>ModalDialogs</c>; a dialog shown around it is one the snapshot cannot see -- failure 2 of section 6
/// ("漏项"). The literal title is the content boundary: the snapshot records the title and never the body, and a
/// title built from data could carry a sublot the way two of the bodies do.
/// </para>
/// <para>
/// <b>What this cannot see.</b> It reads source text under <c>src/SQCD.Agv.Wpf</c>, so: a modal shown another way
/// (<c>Window.ShowDialog</c>, a <c>TaskDialog</c>, <c>System.Windows.Forms.MessageBox</c>), of which there is none
/// today; <c>MessageBox.Show</c> reached through an alias, <c>using static</c> or reflection; a call in another
/// project. Text after <c>//</c> is dropped, so a mention in a comment is not a call; a <c>/* ... */</c> block is not.
/// The literal check reads the second argument up to its first comma, so a title that is a literal followed by
/// <c>+ something</c> on the next line would pass; none is written that way. A helper that forwards a title it was
/// given (<c>ShowRecoveryFailure</c>) has to be listed in <c>Forwarders</c>; an unlisted one fails the literal check at
/// its own call, which is the safe direction.
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
        string[] offenders = WpfSources()
            .Where(file => Path.GetFileName(file) != Wrapper)
            .SelectMany(file => CodeLines(file)
                .Where(line => Regex.IsMatch(line.Text, @"\bMessageBox\.Show\s*\("))
                .Select(line => $"{Path.GetRelativePath(Root(), file)}:{line.Number}"))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These call MessageBox.Show directly; call ModalDialogs.Show instead, so the snapshot sees the dialog:\n"
                + string.Join("\n", offenders));
    }

    /// <summary>
    /// The 26 call sites the research counted (24 in <c>MainWindow.xaml.cs</c>, 2 in <c>App.xaml.cs</c>), all moved.
    /// A count, not a list, so a new dialog does not have to be added here -- only routed.
    /// </summary>
    [Fact]
    public void EveryDialogTheResearchCountedGoesThroughTheRegistry()
    {
        Assert.Equal(24, RegistryCalls("MainWindow.xaml.cs"));
        Assert.Equal(2, RegistryCalls("App.xaml.cs"));
    }

    [Fact]
    public void EveryTitleIsALiteral()
    {
        string[] offenders = WpfSources()
            .Where(file => Path.GetFileName(file) != Wrapper)
            .SelectMany(file => Calls(file).Select(call => (file, call)))
            .Where(item => !Regex.IsMatch(item.call.Title, "^\"[^\"]*\"$"))
            .Select(item => $"{Path.GetRelativePath(Root(), item.file)}:{item.call.Line}: {item.call.Title}")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A dialog title must be a string literal (the snapshot records it):\n" + string.Join("\n", offenders));
    }

    private static int RegistryCalls(string fileName)
    {
        string file = WpfSources().Single(path => Path.GetFileName(path) == fileName);
        return CodeLines(file).Sum(line => Regex.Matches(line.Text, @"\bModalDialogs\.Show\s*\(").Count);
    }

    /// <summary>
    /// Each dialog call and each forwarder call with its title argument, leaving out the forwarders' own bodies.
    /// </summary>
    private static (int Line, string Title)[] Calls(string file)
    {
        (int Number, string Text)[] lines = CodeLines(file).ToArray();
        string text = string.Join("\n", lines.Select(line => line.Text));
        string callees = string.Join("|", Forwarders.Prepend("ModalDialogs.Show").Select(Regex.Escape));
        return Regex.Matches(text, $@"(?<!\bvoid\s+)\b(?:{callees})\s*\(")
            .Where(match => !InsideForwarder(text, match.Index))
            .Select(match =>
            {
                int line = lines[text[..match.Index].Count(c => c == '\n')].Number;
                return (line, SecondArgument(text, match.Index + match.Length));
            })
            .ToArray();
    }

    /// <summary>Whether <paramref name="index"/> lies in the body of a forwarder, which is a single expression.</summary>
    private static bool InsideForwarder(string text, int index) =>
        Forwarders
            .Select(forwarder => Regex.Match(text, $@"\bvoid\s+{Regex.Escape(forwarder)}\s*\("))
            .Any(declaration => declaration.Success
                && declaration.Index < index
                && text.IndexOf(';', declaration.Index) > index);

    /// <summary>The second top-level argument of the call whose argument list starts at <paramref name="start"/>.</summary>
    private static string SecondArgument(string text, int start)
    {
        int depth = 0;
        int argument = 0;
        int begin = -1;
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
                    return begin < 0 ? string.Empty : text[begin..index].Trim();
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    argument++;
                    if (argument == 1)
                    {
                        begin = index + 1;
                    }
                    else if (argument == 2)
                    {
                        return text[begin..index].Trim();
                    }

                    break;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<(int Number, string Text)> CodeLines(string file) =>
        File.ReadAllLines(file).Select((line, index) =>
        {
            int comment = CommentStart(line);
            return (index + 1, comment < 0 ? line : line[..comment]);
        });

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
