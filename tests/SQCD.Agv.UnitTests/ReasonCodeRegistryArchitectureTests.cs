using System.Text.Json;
using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// Keeps the formal WIRE_TO_GATE error-code boundary tied to the vendored
/// protocol release. This is intentionally a source-level gate because the
/// current onboard implementation does not use a JSON Schema runtime library.
///
/// Every rule below keys off a syntactic signal that only a reason code
/// carries -- an identifier literally named reasonCode, a protocol payload
/// constructor, a named send API -- and is applied to every file under src/.
/// Scanning is deliberately NOT gated on a file being named WireToGate*: a
/// reason code must not escape the gate merely by living somewhere else.
///
/// Three values have no such signal at their site, so their rules are anchored
/// to a named member instead. Anchors are asserted to still match
/// (<see cref="AnchoredScanRulesStillMatchTheirTargets"/>), so reshaping that
/// code makes the gate fail loudly rather than silently go blind. Anchor on a
/// member name, never on the shape of an expression.
///
/// Local diagnostic codes are not protocol bytes and match no rule here:
/// SafetyRules returns SLOT_NOT_EMPTY / SLOT_HAS_NO_CARGO for the legacy HMI
/// path, and WireToGateBusinessService throws codes as exception messages.
/// No exemption list is needed today. If a future local code does match a
/// rule, add an explicit exemption with its reason rather than narrowing the
/// rule -- narrowing silently drops coverage for real protocol codes too.
/// </summary>
public sealed class ReasonCodeRegistryArchitectureTests
{
    private const string RegistryRelativePath =
        "vendor/8005-agv-protocol/errors/error-codes.json";

    /// <summary>
    /// Predicate whose whole purpose is "is this value a protocol error
    /// code". It is a second copy of the registry living in product code, so
    /// it must equal the registry exactly, not merely be a subset of it.
    /// </summary>
    private const string ProtocolErrorCodePredicate = "IsProtocolErrorCode";

    /// <summary>Returns the slot precondition reason code, or null.</summary>
    private const string SlotPreconditionMethod = "ValidateBeforeOperation";

    /// <summary>
    /// Returns the protocol code a SlotOperationResumeCommand refused before any door IO is rejected
    /// with (onboard-hmi#119). Its input is a local code; only what it returns goes on the wire.
    /// </summary>
    private const string ResumeRejectionReasonMethod = "ResumeNotStartedReasonCode";

    /// <summary>Members whose returned string literals are reason codes by contract.</summary>
    private static readonly string[] ReturnAnchoredMethods = [SlotPreconditionMethod, ResumeRejectionReasonMethod];

    /// <summary>
    /// Records a recovery vector refused before any unlock as <c>FAILED</c> (onboard-hmi#123); its second
    /// argument is the reason code the result carries.
    /// </summary>
    private const string RefusedBeforeUnlockMethod = "RefuseBeforeUnlockAsync";

    private const string RefusedBeforeUnlockContext = "refusedBeforeUnlock.reasonCode";

    /// <summary>Carries a reasonCodes collection as a positional argument.</summary>
    private const string ProtocolSlotStateType = "ProtocolSlotState";

    private static readonly Regex ReasonCodeAssignmentRegex = new(
        @"\b(?:reasonCode|ReasonCode)\s*(?:=|:)\s*""(?<code>[A-Za-z][A-Za-z0-9_]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ReasonCodesListAssignmentRegex = new(
        @"\b(?:reasonCodes|ReasonCodes)\s*(?:=|:)\s*(?<values>\[[^\]]*\])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex StringLiteralRegex = new(
        @"""(?<code>[A-Za-z][A-Za-z0-9_]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WireProblemPayloadRegex = new(
        @"\bnew\s+WireToGateProblemPayload\s*\(\s*""(?<code>[A-Za-z][A-Za-z0-9_]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WireBlockingFactPayloadRegex = new(
        @"\bnew\s+WireToGateBlockingFactPayload\s*\(\s*""(?<code>[A-Za-z][A-Za-z0-9_]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RecoveryDecisionRegex = new(
        @"\bWireToGateRecoverySafetyDecision\.Denied\s*\(\s*""(?<code>[A-Za-z][A-Za-z0-9_]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OperationRejectedRegex = new(
        @"\bSendOperationRejectedAsync\s*\(\s*[^,]+,\s*""(?<code>[A-Za-z][A-Za-z0-9_]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex RefusedBeforeUnlockRegex = new(
        $@"\b{RefusedBeforeUnlockMethod}\s*\(\s*[^,]+,\s*""(?<code>[A-Za-z][A-Za-z0-9_]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex SafetyReasonAdditionRegex = new(
        @"\b(?:reasons|reasonCodes|ReasonCodes)\.Add\s*\(\s*""(?<code>[A-Za-z][A-Za-z0-9_]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ReturnLiteralRegex = new(
        @"\breturn\s+""(?<code>[A-Za-z][A-Za-z0-9_]*)""\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CollectionLiteralRegex = new(
        @"\[[^\]]*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void FormalWireToGateReasonCodeLiteralsBelongToVendoredRegistry()
    {
        string repositoryRoot = FindRepositoryRoot();
        HashSet<string> registry = LoadRegistry(repositoryRoot);
        SourceReasonCode[] literals = ScanSource(repositoryRoot);

        Assert.NotEmpty(literals);
        AssertAllRegistered(literals, registry);
    }

    [Fact]
    public void ScannerGateRejectsSyntheticUnregisteredReasonCode()
    {
        string repositoryRoot = FindRepositoryRoot();
        HashSet<string> registry = LoadRegistry(repositoryRoot);
        const string syntheticSource = """
            new WireToGateProblemPayload("NOT_IN_PROTOCOL_REGISTRY", null, null);
            """;

        List<SourceReasonCode> literals = ScanSourceText(
            syntheticSource,
            "synthetic/WireToGateProblemPayload.cs");

        Assert.NotEmpty(literals);
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => AssertAllRegistered(literals, registry));
        Assert.Contains("NOT_IN_PROTOCOL_REGISTRY", error.Message);
    }

    /// <summary>
    /// Pins the coverage hole that gating the scan on a WireToGate* file name
    /// would reopen: the same declaration must be caught wherever it lives.
    /// </summary>
    [Fact]
    public void ScannerGateCoversFilesOutsideTheWireToGateNamingConvention()
    {
        string repositoryRoot = FindRepositoryRoot();
        HashSet<string> registry = LoadRegistry(repositoryRoot);
        const string syntheticSource = """
            var reasonCode = "NOT_IN_PROTOCOL_REGISTRY";
            """;

        foreach (string path in new[]
                 {
                     "src/SQCD.Agv.Core/SafetyRules.cs",
                     "src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs",
                 })
        {
            List<SourceReasonCode> literals = ScanSourceText(syntheticSource, path);

            Assert.NotEmpty(literals);
            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => AssertAllRegistered(literals, registry));
            Assert.Contains("NOT_IN_PROTOCOL_REGISTRY", error.Message);
            Assert.Contains(path, error.Message);
        }
    }

    /// <summary>
    /// The resume rejection mapping is scanned by its name, so an unregistered code it returns is
    /// caught even though nothing at the site says "reason code".
    /// </summary>
    [Fact]
    public void ScannerGateRejectsAnUnregisteredResumeRejectionCode()
    {
        string repositoryRoot = FindRepositoryRoot();
        HashSet<string> registry = LoadRegistry(repositoryRoot);
        const string syntheticSource = """
            private static string ResumeNotStartedReasonCode(string localCode)
            {
                return "NOT_IN_PROTOCOL_REGISTRY";
            }
            """;

        List<SourceReasonCode> literals = ScanSourceText(
            syntheticSource,
            "src/SQCD.Agv.Wpf/WireToGateBusinessService.cs");

        Assert.NotEmpty(literals);
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => AssertAllRegistered(literals, registry));
        Assert.Contains("NOT_IN_PROTOCOL_REGISTRY", error.Message);
    }

    /// <summary>
    /// The code a recovery vector refused before any unlock is reported <c>FAILED</c> with
    /// (onboard-hmi#123) is passed as a bare argument, so it is scanned by the refusal's name
    /// (onboard-hmi#129 C-1).
    /// </summary>
    [Fact]
    public void ScannerGateRejectsAnUnregisteredRefusedBeforeUnlockCode()
    {
        string repositoryRoot = FindRepositoryRoot();
        HashSet<string> registry = LoadRegistry(repositoryRoot);
        const string syntheticSource = """
            WireToGateRecoveryVectorExecutionResult? refused = await _vectorExecutor
                .RefuseBeforeUnlockAsync(context, "NOT_IN_PROTOCOL_REGISTRY", cancellationToken)
                .ConfigureAwait(false);
            """;

        List<SourceReasonCode> literals = ScanSourceText(
            syntheticSource,
            "src/SQCD.Agv.Wpf/WireToGateBusinessService.RecoveryVectors.cs");

        Assert.NotEmpty(literals);
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => AssertAllRegistered(literals, registry));
        Assert.Contains("NOT_IN_PROTOCOL_REGISTRY", error.Message);
    }

    /// <summary>
    /// The inline predicate decides whether an inbound reason code is a
    /// protocol error code at all. A registry entry it does not list is
    /// rejected as PROTOCOL_SCHEMA_INVALID, so subset is not enough here --
    /// the two sets must be equal in both directions.
    /// </summary>
    [Fact]
    public void InlineProtocolErrorCodeSetMatchesVendoredRegistry()
    {
        string repositoryRoot = FindRepositoryRoot();
        HashSet<string> registry = LoadRegistry(repositoryRoot);
        string source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "SQCD.Agv.Infrastructure",
            "WireToGateSessionClient.cs"));

        string body = ExtractExpressionBody(source, ProtocolErrorCodePredicate);
        Assert.False(
            string.IsNullOrEmpty(body),
            $"'{ProtocolErrorCodePredicate}' no longer exists in WireToGateSessionClient.cs. "
            + "This gate anchors on it; update the anchor rather than deleting the check.");

        HashSet<string> inline = StringLiteralRegex
            .Matches(body)
            .Select(match => match.Groups["code"].Value)
            .ToHashSet(StringComparer.Ordinal);

        string[] notInRegistry = inline
            .Except(registry, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] notInline = registry
            .Except(inline, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            notInRegistry.Length == 0 && notInline.Length == 0,
            $"'{ProtocolErrorCodePredicate}' is a second copy of the error-code registry and has drifted."
            + $"{Environment.NewLine}Listed inline but absent from the registry: {FormatCodes(notInRegistry)}"
            + $"{Environment.NewLine}In the registry but not listed inline: {FormatCodes(notInline)}");
    }

    /// <summary>
    /// Two rules are anchored to a named member because the value they carry
    /// has no reason-code signal at its site. If either anchor stops matching,
    /// the gate has gone blind there -- fail instead of passing quietly.
    /// </summary>
    [Fact]
    public void AnchoredScanRulesStillMatchTheirTargets()
    {
        string repositoryRoot = FindRepositoryRoot();

        string executor = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "SQCD.Agv.Application",
            "WireToGateSlotOperationExecutor.cs"));
        string preconditionBody = ExtractBlockBody(executor, SlotPreconditionMethod);
        Assert.False(
            string.IsNullOrEmpty(preconditionBody),
            $"'{SlotPreconditionMethod}' no longer exists in WireToGateSlotOperationExecutor.cs. "
            + "This gate anchors on it; update the anchor rather than deleting the check.");
        Assert.NotEmpty(ReturnLiteralRegex.Matches(preconditionBody));

        string business = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "SQCD.Agv.Wpf",
            "WireToGateBusinessService.cs"));
        string resumeRejectionBody = ExtractBlockBody(business, ResumeRejectionReasonMethod);
        Assert.True(
            ReturnLiteralRegex.Matches(resumeRejectionBody).Count > 0,
            $"'{ResumeRejectionReasonMethod}' in WireToGateBusinessService.cs no longer returns its codes as "
            + "'return \"CODE\";' statements. This gate anchors on them; keep that shape or update the anchor.");

        string sessionClient = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "SQCD.Agv.Infrastructure",
            "WireToGateSessionClient.cs"));
        Assert.NotEmpty(ScanProtocolSlotStateReasonCodes(sessionClient, "anchor-probe.cs"));

        string recoveryVectors = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "SQCD.Agv.Wpf",
            "WireToGateBusinessService.RecoveryVectors.cs"));
        Assert.True(
            ScanSourceText(recoveryVectors, "anchor-probe.cs")
                .Any(code => code.Context == RefusedBeforeUnlockContext),
            $"No '{RefusedBeforeUnlockMethod}(context, \"CODE\", ...)' call is left in "
            + "WireToGateBusinessService.RecoveryVectors.cs. This gate anchors on it; keep the code a literal "
            + "second argument or update the anchor.");
    }

    private static SourceReasonCode[] ScanSource(string repositoryRoot)
    {
        List<SourceReasonCode> literals = [];
        string sourceRoot = Path.Combine(repositoryRoot, "src");
        foreach (string path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relativePath = Normalize(Path.GetRelativePath(repositoryRoot, path));
            string source = File.ReadAllText(path);
            ScanSourceText(source, relativePath, literals);
        }

        return literals
            .Distinct()
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Line)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();
    }

    private static List<SourceReasonCode> ScanSourceText(
        string source,
        string path)
    {
        List<SourceReasonCode> literals = [];
        ScanSourceText(source, path, literals);
        return literals;
    }

    private static void ScanSourceText(
        string source,
        string path,
        ICollection<SourceReasonCode> literals)
    {
        // Global rules. Each keys off a syntactic signal only a reason code
        // carries, so they are safe -- and necessary -- to apply everywhere.
        AddMatches(source, path, literals, WireProblemPayloadRegex, "problem.reasonCode");
        AddMatches(source, path, literals, WireBlockingFactPayloadRegex, "blockingFact.reasonCode");
        AddMatches(source, path, literals, RecoveryDecisionRegex, "recovery decision reasonCode");
        AddMatches(source, path, literals, OperationRejectedRegex, "operationRejected.reasonCode");
        AddMatches(source, path, literals, RefusedBeforeUnlockRegex, RefusedBeforeUnlockContext);
        AddMatches(source, path, literals, ReasonCodeAssignmentRegex, "reasonCode assignment");
        AddMatches(source, path, literals, SafetyReasonAdditionRegex, "reasonCodes addition");
        AddListMatches(source, path, literals, ReasonCodesListAssignmentRegex, "reasonCodes list");

        // Anchored rules. The values below are reason codes by contract of the
        // member that produces them, with nothing at the site to say so.
        foreach (string method in ReturnAnchoredMethods)
        {
            string body = ExtractBlockBody(source, method);
            if (body.Length == 0)
            {
                continue;
            }

            int offset = source.IndexOf(body, StringComparison.Ordinal);
            foreach (Match match in ReturnLiteralRegex.Matches(body))
            {
                Capture capture = match.Groups["code"];
                literals.Add(new SourceReasonCode(
                    capture.Value,
                    path,
                    GetLineNumber(source, offset + capture.Index),
                    $"{method} result reasonCode"));
            }
        }

        foreach (SourceReasonCode code in ScanProtocolSlotStateReasonCodes(source, path))
        {
            literals.Add(code);
        }
    }

    private static List<SourceReasonCode> ScanProtocolSlotStateReasonCodes(string source, string path)
    {
        List<SourceReasonCode> literals = [];
        Regex constructor = new(
            $@"\bnew\s+{Regex.Escape(ProtocolSlotStateType)}\s*\(",
            RegexOptions.CultureInvariant);

        foreach (Match match in constructor.Matches(source))
        {
            int open = source.IndexOf('(', match.Index);
            if (open < 0)
            {
                continue;
            }

            string arguments = ExtractBalanced(source, open, '(', ')');
            foreach (Match list in CollectionLiteralRegex.Matches(arguments))
            {
                foreach (Match literal in StringLiteralRegex.Matches(list.Value))
                {
                    int index = open + list.Index + literal.Groups["code"].Index;
                    literals.Add(new SourceReasonCode(
                        literal.Groups["code"].Value,
                        path,
                        GetLineNumber(source, index),
                        $"{ProtocolSlotStateType} reasonCodes"));
                }
            }
        }

        return literals;
    }

    private static void AddMatches(
        string source,
        string path,
        ICollection<SourceReasonCode> literals,
        Regex regex,
        string context)
    {
        foreach (Match match in regex.Matches(source))
        {
            foreach (Capture capture in match.Groups["code"].Captures)
            {
                literals.Add(new SourceReasonCode(
                    capture.Value,
                    path,
                    GetLineNumber(source, capture.Index),
                    context));
            }
        }
    }

    private static void AddListMatches(
        string source,
        string path,
        ICollection<SourceReasonCode> literals,
        Regex regex,
        string context)
    {
        foreach (Match listMatch in regex.Matches(source))
        {
            string values = listMatch.Groups["values"].Value;
            foreach (Match literalMatch in StringLiteralRegex.Matches(values))
            {
                int sourceIndex = listMatch.Groups["values"].Index + literalMatch.Index;
                literals.Add(new SourceReasonCode(
                    literalMatch.Groups["code"].Value,
                    path,
                    GetLineNumber(source, sourceIndex),
                    context));
            }
        }
    }

    /// <summary>
    /// Returns the body of an expression-bodied member, from its "=>" to the
    /// terminating semicolon, or an empty string when the member is absent.
    /// </summary>
    private static string ExtractExpressionBody(string source, string memberName)
    {
        Match declaration = Regex.Match(
            source,
            $@"\b{Regex.Escape(memberName)}\s*\([^)]*\)\s*=>",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        if (!declaration.Success)
        {
            return string.Empty;
        }

        int start = declaration.Index + declaration.Length;
        for (int i = start; i < source.Length; i++)
        {
            char character = source[i];
            if (character == '"')
            {
                i = SkipStringLiteral(source, i);
                continue;
            }

            if (character == '/' && i + 1 < source.Length)
            {
                i = SkipComment(source, i);
                continue;
            }

            if (character == ';')
            {
                return source[start..i];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns the braced body of a method declaration, or an empty string
    /// when the method is absent. Matches the declaration rather than a call
    /// site by requiring an access modifier ahead of the name.
    /// </summary>
    private static string ExtractBlockBody(string source, string methodName)
    {
        Match declaration = Regex.Match(
            source,
            $@"\b(?:private|public|internal|protected)[^;{{}}()]*\b{Regex.Escape(methodName)}\s*\(",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        if (!declaration.Success)
        {
            return string.Empty;
        }

        int parameters = source.IndexOf('(', declaration.Index);
        if (parameters < 0)
        {
            return string.Empty;
        }

        int afterParameters = parameters + ExtractBalanced(source, parameters, '(', ')').Length;
        int open = source.IndexOf('{', afterParameters);
        return open < 0 ? string.Empty : ExtractBalanced(source, open, '{', '}');
    }

    /// <summary>
    /// Returns the text from <paramref name="open"/> through its matching
    /// close character, skipping string literals and comments.
    /// </summary>
    private static string ExtractBalanced(string source, int open, char openChar, char closeChar)
    {
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            char character = source[i];
            if (character == '"')
            {
                i = SkipStringLiteral(source, i);
                continue;
            }

            if (character == '/' && i + 1 < source.Length)
            {
                i = SkipComment(source, i);
                continue;
            }

            if (character == openChar)
            {
                depth++;
            }
            else if (character == closeChar && --depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        return string.Empty;
    }

    /// <summary>Returns the index of the closing quote of a string literal.</summary>
    private static int SkipStringLiteral(string source, int quote)
    {
        bool verbatim = quote > 0 && source[quote - 1] == '@';
        for (int i = quote + 1; i < source.Length; i++)
        {
            if (!verbatim && source[i] == '\\')
            {
                i++;
                continue;
            }

            if (source[i] != '"')
            {
                continue;
            }

            if (verbatim && i + 1 < source.Length && source[i + 1] == '"')
            {
                i++;
                continue;
            }

            return i;
        }

        return source.Length - 1;
    }

    /// <summary>
    /// Returns the last index of a comment starting at <paramref name="slash"/>,
    /// or that index unchanged when it does not start one.
    /// </summary>
    private static int SkipComment(string source, int slash)
    {
        if (source[slash + 1] == '/')
        {
            int newline = source.IndexOf('\n', slash);
            return newline < 0 ? source.Length - 1 : newline;
        }

        if (source[slash + 1] == '*')
        {
            int end = source.IndexOf("*/", slash + 2, StringComparison.Ordinal);
            return end < 0 ? source.Length - 1 : end + 1;
        }

        return slash;
    }

    private static HashSet<string> LoadRegistry(string repositoryRoot)
    {
        string registryPath = Path.Combine(
            repositoryRoot,
            RegistryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(registryPath))
        {
            throw new FileNotFoundException("Vendored protocol error registry is missing.", registryPath);
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(registryPath));
        if (!document.RootElement.TryGetProperty("codes", out JsonElement codes)
            || codes.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Vendored protocol registry has no codes array.");
        }

        HashSet<string> registry = new(StringComparer.Ordinal);
        foreach (JsonElement entry in codes.EnumerateArray())
        {
            if (!entry.TryGetProperty("code", out JsonElement code)
                || code.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(code.GetString()))
            {
                throw new InvalidDataException("Vendored protocol registry contains an invalid code entry.");
            }

            if (!registry.Add(code.GetString()!))
            {
                throw new InvalidDataException(
                    $"Vendored protocol registry contains duplicate code '{code.GetString()}'.");
            }
        }

        if (registry.Count == 0)
        {
            throw new InvalidDataException("Vendored protocol registry is empty.");
        }

        return registry;
    }

    private static void AssertAllRegistered(
        IEnumerable<SourceReasonCode> literals,
        HashSet<string> registry)
    {
        SourceReasonCode[] violations = literals
            .Where(item => !registry.Contains(item.Code))
            .ToArray();
        if (violations.Length == 0)
        {
            return;
        }

        string details = string.Join(
            Environment.NewLine,
            violations.Select(item =>
                $"{item.Code} at {item.Path}:{item.Line} ({item.Context})"));
        throw new InvalidDataException(
            $"WIRE_TO_GATE reasonCode literals are missing from the vendored registry:{Environment.NewLine}{details}");
    }

    private static string FormatCodes(IReadOnlyCollection<string> codes) =>
        codes.Count == 0 ? "(none)" : string.Join(", ", codes);

    /// <summary>
    /// The repository root, plus the precondition that the vendored registry is under it.
    /// </summary>
    /// <remarks>
    /// The walk itself lives in <see cref="ProtocolIdentityArchitectureTests.RepositoryRoot"/> --
    /// one locator per assembly, not one per test class. The registry check stays here because it
    /// is this class's precondition, and a missing registry should say so rather than surface as an
    /// empty scan.
    /// </remarks>
    private static string FindRepositoryRoot()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string registry = Path.Combine(
            root, RegistryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(registry))
        {
            throw new FileNotFoundException(
                "Vendored protocol error registry is missing.", registry);
        }

        return root;
    }

    private static int GetLineNumber(string source, int index) =>
        source[..index].Count(character => character == '\n') + 1;

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed record SourceReasonCode(
        string Code,
        string Path,
        int Line,
        string Context);
}
