using System.Text;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The machine guard on "the recovery state is never written as a whole value decided outside the
/// journal's lock" (batch 7-19, <c>trytoreachpeak0/8005-agv-onboard-hmi#136</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a test and not a comment.</b> A writer that reads the state, decides outside the
/// lock and writes the whole record back puts its stale read over whatever landed in between. Nothing
/// throws and nothing logs: a recorded result comes back to life, a closed session's fields come
/// back, somebody else's pending cancellation disappears. The vehicle then believes it has an
/// unsettled operation after a restart, or the recovery entry answers
/// <c>RECOVERY_SESSION_STATE_PENDING</c> forever and the journal has to be cleared by hand.
/// </para>
/// <para>
/// <b><see cref="EveryChangeFunctionUsesTheStateTheJournalHandsIt"/> is the one that carries this;
/// <see cref="TheJournalHasNoWholeValueRecoveryStateWrite"/> only closes the obvious half.</b>
/// Deleting <c>IWireToGateJournal.WriteRecoveryStateAsync</c> stops a whole value being handed to a
/// method called "write". The shape that actually got past a written acceptance criterion is
/// <c>UpdateRecoveryStateAsync(_ =&gt; state, ...)</c> -- the atomic method called with the old
/// semantics, throwing away the very state it exists to hand over.
/// <c>WriteRecoveryStateCachedAsync</c> was written exactly that way by onboard-hmi#129, which cared
/// about the cache and not about the stale read; twelve paths went through it. A guard that only
/// checks <i>which method is called</i> would have passed every one of them.
/// </para>
/// <para>
/// <b>What it cannot see, by construction.</b> The check is "the parameter is mentioned somewhere in
/// the body", so three shapes get through:
/// a lambda that names its parameter and ignores it in favour of an outer variable
/// (<c>current =&gt; somethingElse</c>); <b>a lambda that reads the parameter in a predicate but
/// builds its result from an outer copy</b> (<c>current =&gt; current.X == y ? state : null</c>) --
/// worth naming on its own, because "predicate on the parameter, result from somewhere" is the exact
/// shape this ticket left all over the repository, so it is what a next change is most likely to be
/// copied into; and a parameter assigned to an unused local. A change function not written out at the
/// call site (a method group, or a variable holding one) is skipped as well, because there is nothing
/// at that line to read. The interleaving G2 tests are what catch a stale write behaviourally. It also
/// strips line comments only, so a <c>/* */</c> block holding call-shaped text would be read as code.
/// <para>
/// Requiring the parameter in the <i>returned expression</i> rather than anywhere in the body would
/// close the second shape. That is a worthwhile tightening and deliberately not done here: it needs
/// the body parsed into its return paths, and this guard is a textual scan.
/// </para>
/// </para>
/// </remarks>
public sealed class RecoveryStateWriteFunnelArchitectureTests
{
    /// <summary>The atomic entry points whose change function must use the state it is handed.</summary>
    private static readonly string[] AtomicWriteMethods =
    [
        "UpdateRecoveryStateAsync",
        "UpdateRecoveryStateCachedAsync"
    ];

    /// <summary>
    /// The journal offers no way to write the recovery state as a whole value. Stated by reflection so
    /// that re-adding the member fails here and not only in review.
    /// </summary>
    [Fact]
    public void TheJournalHasNoWholeValueRecoveryStateWrite()
    {
        string[] offenders =
        [
            .. typeof(IWireToGateJournal)
                .GetMembers()
                .Where(member => member.Name.Contains("WriteRecoveryState", StringComparison.Ordinal))
                .Select(member => member.ToString() ?? member.Name)
        ];

        Assert.True(
            offenders.Length == 0,
            "IWireToGateJournal still offers a whole-value recovery state write: "
            + string.Join(", ", offenders)
            + ". Every writer of that record decides on what it read, so the read and the write have "
            + "to be one step under the journal's lock (onboard-hmi#123 review A, onboard-hmi#136). "
            + "Use UpdateRecoveryStateAsync.");
    }

    /// <summary>
    /// Every change function written out at a call site uses the state the journal gives it -- unless
    /// it writes nothing at all, which is how a read-only step is spelt.
    /// </summary>
    [Fact]
    public void EveryChangeFunctionUsesTheStateTheJournalHandsIt()
    {
        List<string> offenders = [];
        foreach (string file in ProductSourcesThatMayWriteRecoveryState())
        {
            string source = StripLineComments(File.ReadAllText(file));
            foreach (string method in AtomicWriteMethods)
            {
                foreach ((int line, string parameter, string body) in ChangeFunctions(source, method))
                {
                    if (body.Trim() is "null" || MentionsIdentifier(body, parameter))
                    {
                        continue;
                    }

                    offenders.Add(
                        $"{Path.GetFileName(file)}:{line} {method}({Collapse(parameter)} => "
                        + $"{Collapse(body)}) never uses \"{Collapse(parameter)}\", the newest state "
                        + "the journal read");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A change function that ignores the state the journal hands it is a whole-value write "
            + "wearing the atomic method's name: the journal reads the newest state, the function "
            + "throws it away, and the caller's older copy lands on top of whatever it missed "
            + "(onboard-hmi#136). Write the change against the parameter, or return null to write "
            + "nothing.\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Whether <paramref name="identifier"/> appears in <paramref name="body"/> as a word of its own.
    /// </summary>
    private static bool MentionsIdentifier(string body, string identifier)
    {
        if (identifier.Length == 0)
        {
            return false;
        }

        for (int index = body.IndexOf(identifier, StringComparison.Ordinal);
            index >= 0;
            index = body.IndexOf(identifier, index + 1, StringComparison.Ordinal))
        {
            bool beforeIsWord = index > 0 && IsIdentifierChar(body[index - 1]);
            int after = index + identifier.Length;
            bool afterIsWord = after < body.Length && IsIdentifierChar(body[after]);
            if (!beforeIsWord && !afterIsWord)
            {
                return true;
            }
        }

        return false;

        static bool IsIdentifierChar(char value) => char.IsLetterOrDigit(value) || value == '_';
    }

    /// <summary>
    /// Every call to <paramref name="method"/> in <paramref name="source"/> whose first argument is a
    /// lambda written out at the call site, as its parameter and its body.
    /// </summary>
    /// <remarks>
    /// A call whose first argument is not such a lambda is skipped: that is how the declarations and
    /// the forwarding overload look, and a parameter list has no change function to read. The skip is
    /// a blind spot rather than a judgement -- see the type's remarks.
    /// </remarks>
    private static IEnumerable<(int Line, string Parameter, string Body)> ChangeFunctions(
        string source,
        string method)
    {
        string needle = method + "(";
        for (int index = source.IndexOf(needle, StringComparison.Ordinal);
            index >= 0;
            index = source.IndexOf(needle, index + 1, StringComparison.Ordinal))
        {
            if (index > 0 && (char.IsLetterOrDigit(source[index - 1]) || source[index - 1] == '_'))
            {
                continue;
            }

            if (ReadCall(source, index + needle.Length) is not { } call)
            {
                continue;
            }

            (string? parameter, string? body) = SplitFirstLambda(call);
            if (parameter is null || body is null)
            {
                continue;
            }

            yield return (
                Line: source.Take(index).Count(character => character == '\n') + 1,
                Parameter: parameter,
                Body: body);
        }
    }

    /// <summary>
    /// The argument list from <paramref name="start"/> up to the call's closing parenthesis, or
    /// <c>null</c> when the parentheses never balance. Only <c>()</c>, <c>[]</c> and <c>{}</c> are
    /// counted: <c>&lt;</c> and <c>&gt;</c> are comparison operators as often as generic brackets, and
    /// counting them would unbalance a body such as <c>a &gt; b</c>.
    /// </summary>
    private static string? ReadCall(string source, int start)
    {
        int depth = 0;
        bool inString = false;
        bool inChar = false;
        for (int index = start; index < source.Length; index++)
        {
            char character = source[index];
            if (inString || inChar)
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (inString && character == '"')
                {
                    inString = false;
                }
                else if (inChar && character == '\'')
                {
                    inChar = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '\'':
                    inChar = true;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' when depth == 0:
                    return source[start..index];
                case ')' or ']' or '}':
                    depth--;
                    break;
                default:
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// The parameter and body of <paramref name="call"/>'s first argument when that argument is a
    /// lambda -- that is, when the argument list's first top-level <c>=&gt;</c> comes before its first
    /// top-level comma. Both <c>null</c> otherwise.
    /// </summary>
    private static (string? Parameter, string? Body) SplitFirstLambda(string call)
    {
        int depth = 0;
        int arrow = -1;
        int comma = -1;
        bool inString = false;
        bool inChar = false;
        for (int index = 0; index < call.Length && (arrow < 0 || comma < 0); index++)
        {
            char character = call[index];
            if (inString || inChar)
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (inString && character == '"')
                {
                    inString = false;
                }
                else if (inChar && character == '\'')
                {
                    inChar = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '\'':
                    inChar = true;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case '=' when depth == 0 && arrow < 0 && index + 1 < call.Length && call[index + 1] == '>':
                    arrow = index;
                    break;
                case ',' when depth == 0 && comma < 0:
                    comma = index;
                    break;
                default:
                    break;
            }
        }

        if (arrow < 0 || (comma >= 0 && comma < arrow))
        {
            return (null, null);
        }

        string parameter = call[..arrow]
            .Replace("static", " ", StringComparison.Ordinal)
            .Replace("(", " ", StringComparison.Ordinal)
            .Replace(")", " ", StringComparison.Ordinal)
            .Trim();
        string body = comma < 0 ? call[(arrow + 2)..] : call[(arrow + 2)..comma];
        return (parameter, body);
    }

    /// <summary>Whitespace collapsed to single spaces, so a multi-line argument reads as one.</summary>
    private static string Collapse(string text)
    {
        StringBuilder collapsed = new(text.Length);
        bool space = false;
        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                space = collapsed.Length > 0;
                continue;
            }

            if (space)
            {
                collapsed.Append(' ');
                space = false;
            }

            collapsed.Append(character);
        }

        return collapsed.ToString();
    }

    /// <summary>
    /// Line comments emptied, keeping the newlines so reported line numbers stay true. XML docs name
    /// these methods with their signatures, and a signature read as a call would report a parameter
    /// list as a change function.
    /// </summary>
    private static string StripLineComments(string source)
    {
        StringBuilder stripped = new(source.Length);
        foreach (string line in source.Split('\n'))
        {
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            bool balanced = comment < 0 || line[..comment].Count(character => character == '"') % 2 == 0;
            stripped.Append(comment >= 0 && balanced ? line[..comment] : line).Append('\n');
        }

        return stripped.ToString();
    }

    /// <summary>
    /// The product sources that may hold a call. The journal's own interface and implementation are
    /// out: one declares these methods and the other implements and forwards them.
    /// </summary>
    private static IEnumerable<string> ProductSourcesThatMayWriteRecoveryState()
    {
        string[] declarations = ["WireToGateRecovery.cs", "SqliteWireToGateJournal.cs"];
        return Directory.EnumerateFiles(
            Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "src"),
            "*.cs",
            SearchOption.AllDirectories)
            .Where(file =>
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !declarations.Contains(Path.GetFileName(file), StringComparer.Ordinal));
    }
}
