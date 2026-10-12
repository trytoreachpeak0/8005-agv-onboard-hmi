using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// Finds every technical-log write under <c>src/</c> and reads its first arguments, for
/// <see cref="TechnicalLogSourceRegistryArchitectureTests"/> and <see cref="ExplicitTechnicalLogCategoryPinTests"/>
/// (REQ-0272, 8005-agv-onboard-hmi#312).
/// </summary>
/// <remarks>
/// <para>
/// <b>A log write is recognised by its receiver, not by its severity.</b> The first version matched a first argument
/// containing <c>LogSeverity.</c>, which a severity held in a local, a parameter or a cast walks straight past
/// (review of #312, M-2). Every logger under <c>src/</c> is held in a variable whose name ends in <c>logger</c>
/// (<c>_logger</c>, <c>logger</c>, <c>bootstrapLogger</c>), so that is the anchor; two checks with other premises
/// keep it honest: every declared <c>IAppLogger</c>/<c>FileAppLogger</c> variable must be named that way, and every
/// other <c>.Write(</c> receiver must be on a short list of known non-loggers.
/// </para>
/// <para>
/// <b>Every <c>.Write(</c> is found first and its receiver read backwards from the dot</b>, so a receiver that is not
/// a bare name is read too (second review of #312): <c>logger!.Write(</c>, <c>_logger?.Write(</c>,
/// <c>(_logger ?? bootstrapLogger).Write(</c> (<c>App.xaml.cs</c>'s startup-failure catch), <c>Get().Write(</c>.
/// An expression receiver is a logger when every name in it ends in <c>logger</c>; one that is neither a logger nor a
/// known non-logger is reported by the cross-check, not skipped.
/// </para>
/// <para>
/// Literals and comments are read by <see cref="CSharpSourceLexer"/>, so a <c>//</c> or a comma inside a string cannot
/// cut an argument. A file the lexer refuses (a raw string literal) is skipped only when it contains no
/// <c>.Write(</c> call shape at all (read on the raw text, so even one inside a literal counts), and reported otherwise.
/// </para>
/// </remarks>
internal static class LoggerWriteScanner
{
    private const string CategoryPrefix = "OnboardTechnicalLogCategory.";

    private static readonly Regex WriteCall = new(
        @"\.\s*Write\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Name = new(
        @"[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LoggerDeclaration = new(
        @"\b(?:IAppLogger|FileAppLogger)\??\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NameOf = new(
        @"^nameof\(\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Literal = new(
        @"^""(?<name>[^""\\]*)""$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// One <c>.Write(</c> call: its receiver as written (a bare name, or a parenthesised or call expression, without a
    /// trailing <c>?</c> or <c>!</c>), and its arguments as written (comments blanked).
    /// </summary>
    internal sealed record WriteCallSite(string Path, int Line, string Receiver, IReadOnlyList<string> Arguments)
    {
        public bool ReceiverIsName => Name.Match(Receiver) is { Success: true } match && match.Length == Receiver.Length;

        /// <summary>Every name in the receiver ends in <c>logger</c>: <c>_logger</c>, <c>(_logger ?? bootstrapLogger)</c>.</summary>
        public bool IsLoggerWrite =>
            Name.Matches(Receiver) is { Count: > 0 } names
            && names.All(name => name.Value.EndsWith("logger", StringComparison.OrdinalIgnoreCase));

        public string Call => string.Join(", ", Arguments);

        /// <summary>The explicit category name, or null when the second argument is not one.</summary>
        public string? Category =>
            Arguments.Count > 1 && Arguments[1].StartsWith(CategoryPrefix, StringComparison.Ordinal)
                ? Arguments[1][CategoryPrefix.Length..]
                : null;

        private string SourceArgument => Arguments.Count > (Category is null ? 1 : 2)
            ? Arguments[Category is null ? 1 : 2]
            : string.Empty;

        /// <summary>The source as written: the name inside <c>nameof</c> or the literal, else the raw argument.</summary>
        public string Source
        {
            get
            {
                Match name = NameOf.Match(SourceArgument);
                if (name.Success)
                {
                    return name.Groups["name"].Value;
                }

                Match literal = Literal.Match(SourceArgument);
                return literal.Success ? literal.Groups["name"].Value : SourceArgument;
            }
        }

        public bool SourceIsStatic => NameOf.IsMatch(SourceArgument) || Literal.IsMatch(SourceArgument);
    }

    internal sealed record LoggerDeclarationSite(string Path, int Line, string Name);

    internal static WriteCallSite[] ScanSource() =>
        SourceFiles().SelectMany(file => ScanText(file.Text, file.Path)).ToArray();

    internal static LoggerDeclarationSite[] DeclarationsInSource() =>
        SourceFiles().SelectMany(file => DeclarationsIn(file.Text, file.Path)).ToArray();

    internal static WriteCallSite[] ScanText(string source, string path)
    {
        if (!TryLex(source, path, out string code, out string kept))
        {
            return [];
        }

        List<WriteCallSite> found = [];
        foreach (Match match in WriteCall.Matches(code))
        {
            int open = match.Index + match.Length - 1;
            if (!TrySplitArguments(code, open, out List<(int Start, int End)> spans))
            {
                throw new InvalidDataException($"{path}: unbalanced call after {match.Value} at offset {open}.");
            }

            found.Add(new WriteCallSite(
                path,
                code[..match.Index].Count(character => character == '\n') + 1,
                ReadReceiver(code, match.Index),
                spans.Select(span => kept[span.Start..span.End].Trim()).ToArray()));
        }

        return found.ToArray();
    }

    internal static LoggerDeclarationSite[] DeclarationsIn(string source, string path)
    {
        if (!TryLex(source, path, out string code, out _))
        {
            return [];
        }

        return LoggerDeclaration.Matches(code)
            .Select(match => new LoggerDeclarationSite(
                path,
                code[..match.Index].Count(character => character == '\n') + 1,
                match.Groups["name"].Value))
            .ToArray();
    }

    private static IEnumerable<(string Path, string Text)> SourceFiles()
    {
        string repositoryRoot = ProtocolIdentityArchitectureTests.RepositoryRoot();
        return Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => (Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'), File.ReadAllText(path)));
    }

    private static bool TryLex(string source, string path, out string code, out string kept)
    {
        try
        {
            CSharpSourceLexer.Lexed lexed = CSharpSourceLexer.Lex(source);
            code = string.Join('\n', lexed.Code);
            kept = string.Join('\n', lexed.Kept);
            return true;
        }
        catch (NotSupportedException) when (!WriteCall.IsMatch(source))
        {
            // A raw string literal the lexer refuses, in a file with no write to find.
            code = kept = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// The receiver ending just before the dot at <paramref name="dot"/>: a trailing <c>?</c> or <c>!</c> is dropped; a
    /// closing bracket is matched back to its opening one, together with the name it calls if any (<c>Get()</c>);
    /// otherwise the last name (<c>this._logger</c> reads as <c>_logger</c>). Literals are blanked already. An empty
    /// result means the scan could not read it, which the cross-check reports.
    /// </summary>
    private static string ReadReceiver(string code, int dot)
    {
        int end = dot - 1;
        while (end >= 0 && char.IsWhiteSpace(code[end]))
        {
            end--;
        }

        while (end >= 0 && code[end] is '?' or '!')
        {
            end--;
        }

        int start = end;
        if (end >= 0 && code[end] is ')' or ']')
        {
            int depth = 0;
            for (; start >= 0; start--)
            {
                if (code[start] is ')' or ']')
                {
                    depth++;
                }
                else if (code[start] is '(' or '[' && --depth == 0)
                {
                    break;
                }
            }

            if (start < 0)
            {
                return string.Empty;
            }
        }

        while (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_'))
        {
            start--;
        }

        return start >= 0 && end >= start ? code[start..(end + 1)].Trim() : string.Empty;
    }

    /// <summary>
    /// From the opening parenthesis, the top-level argument spans up to the matching close. Literals are already
    /// blanked in <paramref name="code"/>, so only brackets count.
    /// </summary>
    private static bool TrySplitArguments(string code, int open, out List<(int Start, int End)> spans)
    {
        spans = [];
        int depth = 0;
        int start = open + 1;
        for (int position = open + 1; position < code.Length; position++)
        {
            char character = code[position];
            if (character is '(' or '[' or '{')
            {
                depth++;
            }
            else if (character is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    if (position > start || spans.Count > 0)
                    {
                        spans.Add((start, position));
                    }

                    return true;
                }

                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                spans.Add((start, position));
                start = position + 1;
            }
        }

        return false;
    }
}
