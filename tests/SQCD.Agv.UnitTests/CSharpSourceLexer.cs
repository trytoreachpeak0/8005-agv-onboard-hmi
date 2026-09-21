using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// A small C# lexer and member cutter for the source-text architecture guards, shared by
/// <see cref="RecoveryMarkerPersistenceArchitectureTests"/> (where it was built, onboard-hmi#162) and
/// <see cref="RecoveryEntryWriteSiteArchitectureTests"/> (onboard-hmi#181).
/// </summary>
/// <remarks>
/// It exists because line-by-line regular expressions over C# kept missing shapes: a <c>//</c> inside a string cut
/// the rest of the line away as a comment, an assignment split over two lines matched on neither, a member ended
/// early where a line ended in <c>}</c>. It reads literals and comments the way the compiler does, cuts members by
/// brace depth and statement ends, and refuses what it cannot read (raw strings) instead of guessing. It is not a
/// parser: its limits are stated in each guard that uses it, and a guard built on it is a regression fence over
/// source text, not a proof about behaviour.
/// </remarks>
internal static class CSharpSourceLexer
{
    /// <summary>A member: its name, its first and last line (0-based), and its code from its first character.</summary>
    internal sealed record MemberSpan(string Name, int Start, int End, string Text);

    /// <summary>
    /// A source file lexed into two views with the same length and the same lines. <see cref="Code"/> has every
    /// comment and every literal's contents blanked -- what is left is code, and only code. <see cref="Kept"/> has the
    /// comments blanked and the string contents kept, for the checks that must see a name written inside a string (a
    /// reflective read, a JSON key).
    /// </summary>
    internal sealed record Lexed(string[] Code, string[] Kept);

    // ------------------------------------------------------------------------------------------------------------
    // The lexer (onboard-hmi#162, third review). Four rounds of regular expressions approximating C# each missed a
    // shape the next review found: a `//` inside a string cut the rest of the line away as a comment; `/*` in one string and `*/`
    // in another swallowed the code between them; a brace in an unusual literal could shift every member after it.
    // This walks the text character by character instead, the way the compiler reads literals and comments.
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Lexes C# source into <see cref="Lexed"/>. Handles regular, verbatim (<c>@"</c>) and interpolated (<c>$"</c>,
    /// <c>$@"</c>, <c>@$"</c>) strings -- an interpolation hole is code, lexed as code, nested strings and all --
    /// character literals, <c>//</c> and <c>/* */</c> comments, and preprocessor lines. A raw string literal
    /// (<c>"""</c>) is refused with <see cref="NotSupportedException"/>: the guard cannot read it, and saying so is
    /// the point -- a lexer that guesses is what the four rounds before this one were.
    /// </summary>
    internal static Lexed Lex(string source)
    {
        string text = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        char[] code = text.ToCharArray();
        char[] kept = text.ToCharArray();
        int position = 0;
        LexCode(text, ref position, code, kept, inHole: false);
        return new Lexed(new string(code).Split('\n'), new string(kept).Split('\n'));
    }

    private static void LexCode(string text, ref int position, char[] code, char[] kept, bool inHole)
    {
        int nesting = 0;
        bool lineStart = true;
        while (position < text.Length)
        {
            char current = text[position];
            if (inHole && nesting == 0 && current == '}')
            {
                return;
            }

            if (current == '\n')
            {
                lineStart = true;
                position++;
                continue;
            }

            if (lineStart && current == '#' && !inHole)
            {
                int end = text.IndexOf('\n', position);
                end = end < 0 ? text.Length : end;
                Blank(code, position, end);
                Blank(kept, position, end);
                position = end;
                continue;
            }

            if (!char.IsWhiteSpace(current))
            {
                lineStart = false;
            }

            if (current == '/' && At(text, position + 1) == '/')
            {
                int end = text.IndexOf('\n', position);
                end = end < 0 ? text.Length : end;
                Blank(code, position, end);
                Blank(kept, position, end);
                position = end;
            }
            else if (current == '/' && At(text, position + 1) == '*')
            {
                int end = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new NotSupportedException($"Unterminated block comment at {position}.");
                }

                Blank(code, position, end + 2);
                Blank(kept, position, end + 2);
                position = end + 2;
            }
            else if (current == '"' && At(text, position + 1) == '"' && At(text, position + 2) == '"')
            {
                throw RawStringRefused(text, position);
            }
            else if (current is '$' or '@')
            {
                int quote = position;
                bool interpolated = false;
                bool verbatim = false;
                while (quote < text.Length && text[quote] is '$' or '@')
                {
                    interpolated |= text[quote] == '$';
                    verbatim |= text[quote] == '@';
                    quote++;
                }

                if (At(text, quote) != '"')
                {
                    position++;
                    continue;
                }

                if (quote - position > 2 || (interpolated && text[position..quote].Count(character => character == '$') > 1)
                    || (At(text, quote + 1) == '"' && At(text, quote + 2) == '"'))
                {
                    throw RawStringRefused(text, position);
                }

                position = quote;
                LexString(text, ref position, code, kept, verbatim, interpolated);
            }
            else if (current == '"')
            {
                LexString(text, ref position, code, kept, verbatim: false, interpolated: false);
            }
            else if (current == '\'')
            {
                int end = position + 1;
                if (At(text, end) == '\\')
                {
                    end++;
                }

                end = text.IndexOf('\'', end + 1);
                if (end < 0)
                {
                    throw new NotSupportedException($"Unterminated character literal at {position}.");
                }

                Blank(code, position + 1, end);
                position = end + 1;
            }
            else
            {
                if (inHole)
                {
                    nesting += current is '{' or '(' or '[' ? 1 : current is '}' or ')' or ']' ? -1 : 0;
                }

                position++;
            }
        }

        if (inHole)
        {
            throw new NotSupportedException("Unterminated interpolation hole.");
        }
    }

    /// <summary>
    /// A string starting at the opening quote under <paramref name="position"/>. Its text is blanked in the code
    /// view; an interpolation hole's braces are blanked and its inside is lexed as code.
    /// </summary>
    private static void LexString(string text, ref int position, char[] code, char[] kept, bool verbatim, bool interpolated)
    {
        int start = position;
        position++;
        while (position < text.Length)
        {
            char current = text[position];
            if (!verbatim && current == '\\')
            {
                position += 2;
                continue;
            }

            if (!verbatim && current == '\n')
            {
                throw new NotSupportedException($"Newline inside a regular string starting at {start}.");
            }

            if (current == '"')
            {
                if (verbatim && At(text, position + 1) == '"')
                {
                    position += 2;
                    continue;
                }

                Blank(code, start + 1, position);
                position++;
                return;
            }

            if (interpolated && current is '{' or '}')
            {
                if (At(text, position + 1) == current)
                {
                    position += 2;
                    continue;
                }

                if (current == '}')
                {
                    throw new NotSupportedException($"Unbalanced '}}' in an interpolated string at {position}.");
                }

                // Blank the text before the hole and the hole's opening brace, lex the hole as code, then carry on
                // with the string after the hole's closing brace.
                Blank(code, start + 1, position + 1);
                position++;
                LexCode(text, ref position, code, kept, inHole: true);
                code[position] = ' ';
                start = position;
                position++;
                continue;
            }

            position++;
        }

        throw new NotSupportedException($"Unterminated string starting at {start}.");
    }

    private static NotSupportedException RawStringRefused(string text, int position) => new(
        $"This guard does not read raw string literals (\"\"\"), found at line {text[..position].Count(character => character == '\n') + 1}. "
        + "Extend CSharpSourceLexer before using one in a file a guard reads; guessing "
        + "at it is how a member boundary shifts without anything turning red.");

    private static char At(string text, int index) => index < text.Length ? text[index] : '\0';

    private static void Blank(char[] characters, int from, int to)
    {
        for (int index = from; index < to && index < characters.Length; index++)
        {
            if (characters[index] != '\n')
            {
                characters[index] = ' ';
            }
        }
    }

    internal const string Unnamed = "(unnamed)";

    /// <summary>
    /// The class body cut into members over the code view, character by character. A member starts at the first
    /// code character at class depth and ends at one of two places:
    /// <list type="bullet">
    /// <item>a <c>;</c> at class depth, outside parentheses and brackets; or</item>
    /// <item>a <c>}</c> that brings the depth back to the class's, <b>only if</b> the member has had no <c>=</c> or
    /// <c>=&gt;</c> at class depth before it and the next code character is not <c>=</c>. A method, a nested type or a
    /// property with accessors ends there; an expression-bodied member, a field initializer and an auto-property
    /// initializer (<c>{ get; } = x;</c>) go on to their <c>;</c>.</item>
    /// </list>
    /// The fourth review found the line-based cut before this one ending a member on any line at class depth whose code
    /// ended in <c>}</c>: <c>=&gt; x is { State: "A" }</c> followed by <c>|| ...</c> on the next line was two members,
    /// the second named after whatever came next (<c>Read</c>, <c>null</c>). Three members of
    /// <c>WireToGateBusinessService</c> were cut that way (onboard-hmi#162 fourth review). Naming and depth checks
    /// cannot see that -- both halves had names and the depth ended at zero -- so a guard using this should also
    /// check that no member starts with <see cref="ContinuationStartRegex"/>
    /// (<see cref="RecoveryMarkerPersistenceArchitectureTests.TheLexerNamesEveryMemberOfTheClass"/> does).
    /// Spans are whole lines: two members on one line share it (a limit each guard states). The depth must
    /// be back at zero at the end of the file -- otherwise the cut is wrong somewhere and this says so instead of
    /// cutting on (third review M-2).
    /// </summary>
    internal static MemberSpan[] MemberSpans(Lexed lexed)
    {
        string[] code = lexed.Code;
        List<MemberSpan> spans = [];
        int depth = 0;
        int grouping = 0;
        bool assigns = false;
        (int Line, int Column)? start = null;
        for (int index = 0; index < code.Length; index++)
        {
            string line = code[index];
            for (int column = 0; column < line.Length; column++)
            {
                char current = line[column];
                if (char.IsWhiteSpace(current))
                {
                    continue;
                }

                if (depth == 1 && start is null)
                {
                    start = (index, column);
                    grouping = 0;
                    assigns = false;
                }

                bool ends = false;
                switch (current)
                {
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        ends = depth == 1 && grouping == 0 && !assigns && NextCode(code, index, column + 1) != '=';
                        break;
                    case '(' or '[' when depth == 1:
                        grouping++;
                        break;
                    case ')' or ']' when depth == 1:
                        grouping--;
                        break;
                    case ';':
                        ends = depth == 1 && grouping == 0;
                        break;
                    case '=' when depth == 1 && grouping == 0:
                        // `=` and `=>`; not `==`, `!=`, `<=`, `>=` (an operator declaration's name).
                        assigns |= At(line, column + 1) != '=' && (column == 0 || line[column - 1] is not ('=' or '!' or '<' or '>'));
                        break;
                }

                if (ends && start is (int firstLine, int firstColumn))
                {
                    string text = firstLine == index
                        ? code[index][firstColumn..(column + 1)]
                        : code[firstLine][firstColumn..] + "\n"
                          + string.Join('\n', code[(firstLine + 1)..index]) + (index > firstLine + 1 ? "\n" : string.Empty)
                          + code[index][..(column + 1)];

                    spans.Add(new MemberSpan(NameOf(text), firstLine, index, text));
                    start = null;
                }
            }
        }

        if (depth != 0)
        {
            throw new InvalidOperationException(
                $"Brace depth ends at {depth}, not 0: the member cut cannot be trusted for this source.");
        }

        return [.. spans];
    }

    /// <summary>The next code character after (<paramref name="line"/>, <paramref name="column"/>), or <c>'\0'</c>.</summary>
    private static char NextCode(string[] code, int line, int column)
    {
        for (int index = line; index < code.Length; index++)
        {
            string text = code[index];
            for (int position = index == line ? column : 0; position < text.Length; position++)
            {
                if (!char.IsWhiteSpace(text[position]))
                {
                    return text[position];
                }
            }
        }

        return '\0';
    }

    /// <summary>
    /// A member whose code starts with one of these is the tail of the member before it, cut in the wrong place: no
    /// declaration starts with an operator, a member access or a pattern keyword (fourth review severe-A).
    /// </summary>
    internal static readonly Regex ContinuationStartRegex = new(
        @"^\s*(?:[&|?:.,;)\]}=+\-*/%^<>!]|(?:is|as|with|switch|when|and|or|not)\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> DeclarationKeywords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "readonly", "async", "override", "sealed", "virtual",
        "abstract", "unsafe", "extern", "partial", "new", "const", "volatile", "event", "implicit", "explicit",
        "required", "file", "ref", "fixed"
    };

    /// <summary>
    /// A member's name from its header, read token by token over code: the identifier directly before the first
    /// parameter list's <c>(</c> (a <c>(</c> after a keyword or <c>&lt;</c> opens a tuple type and is skipped, so
    /// <c>private (bool A, string B) Name(</c> and <c>Task&lt;(bool, string)&gt; Name(</c> give <c>Name</c>); otherwise the
    /// last identifier before <c>=&gt;</c>, <c>=</c>, <c>{</c> or <c>;</c> (a property, expression-bodied or not, an
    /// event, a field). An indexer is <c>this</c>. Attributes before the header and array brackets in a type are
    /// skipped; generic arguments do not hide the name before them (third review severe-1, severe-2).
    /// </summary>
    internal static string NameOf(string header)
    {
        string? last = null;
        string? previous = null;
        int position = 0;
        while (position < header.Length)
        {
            char current = header[position];
            if (char.IsWhiteSpace(current))
            {
                position++;
                continue;
            }

            if (char.IsLetter(current) || current == '_' || current == '@')
            {
                int end = position + 1;
                while (end < header.Length && (char.IsLetterOrDigit(header[end]) || header[end] == '_'))
                {
                    end++;
                }

                last = header[position..end].TrimStart('@');
                previous = last;
                position = end;
                continue;
            }

            if (current == '=' && At(header, position + 1) == '>')
            {
                return last ?? Unnamed;
            }

            if (current is '=' or '{' or ';')
            {
                return last ?? Unnamed;
            }

            if (current == '[')
            {
                if (last == "this")
                {
                    return "this";
                }

                position = SkipBalanced(header, position, '[', ']');
                continue;
            }

            if (current == '<')
            {
                position = SkipBalanced(header, position, '<', '>');
                continue;
            }

            if (current == '(')
            {
                if (previous is not null && !DeclarationKeywords.Contains(previous) && previous == last)
                {
                    return previous;
                }

                position = SkipBalanced(header, position, '(', ')');
                previous = ")";
                continue;
            }

            previous = current.ToString();
            position++;
        }

        return last ?? Unnamed;
    }

    private static int SkipBalanced(string text, int position, char open, char close)
    {
        int depth = 0;
        for (int index = position; index < text.Length; index++)
        {
            if (text[index] == open)
            {
                depth++;
            }
            else if (text[index] == close && --depth == 0)
            {
                return index + 1;
            }
        }

        return text.Length;
    }
}
