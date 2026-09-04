using System.Text.Json;
using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// Keeps the formal WIRE_TO_GATE error-code boundary tied to the vendored
/// protocol release. This is intentionally a source-level gate because the
/// current onboard implementation does not use a JSON Schema runtime library.
/// </summary>
public sealed class ReasonCodeRegistryArchitectureTests
{
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

    private static readonly Regex SafetyReasonAdditionRegex = new(
        @"\b(?:reasons|reasonCodes|ReasonCodes)\.Add\s*\(\s*""(?<code>[A-Za-z][A-Za-z0-9_]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SlotReasonReturnRegex = new(
        @"\breturn\s+(?:command\.ExpectedOccupied\s*\?\s*)?""(?<code>[A-Za-z][A-Za-z0-9_]*)""(?:\s*:\s*""(?<code>[A-Za-z][A-Za-z0-9_]*)"")?\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UnknownSlotReasonListRegex = new(
        @"\?\s*\[\]\s*:\s*\[\s*""(?<code>[A-Za-z][A-Za-z0-9_]*)""",
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
        bool formalWireToGateFile = Path.GetFileName(path)
            .StartsWith("WireToGate", StringComparison.OrdinalIgnoreCase);

        // These patterns identify values at a protocol payload boundary. They
        // are applied to every source file so a future protocol payload cannot
        // evade the gate merely because it lives in a different project.
        AddMatches(source, path, literals, WireProblemPayloadRegex, "problem.reasonCode");
        AddMatches(source, path, literals, WireBlockingFactPayloadRegex, "blockingFact.reasonCode");
        AddMatches(source, path, literals, RecoveryDecisionRegex, "recovery decision reasonCode");
        AddMatches(source, path, literals, OperationRejectedRegex, "operationRejected.reasonCode");

        if (!formalWireToGateFile)
        {
            return;
        }

        AddMatches(source, path, literals, ReasonCodeAssignmentRegex, "reasonCode assignment");
        AddListMatches(source, path, literals, ReasonCodesListAssignmentRegex, "reasonCodes list");

        if (path.EndsWith(
                "src/SQCD.Agv.Infrastructure/WireToGateSafetyEvaluator.cs",
                StringComparison.OrdinalIgnoreCase))
        {
            AddMatches(source, path, literals, SafetyReasonAdditionRegex, "safety reasonCodes");
        }

        if (path.EndsWith(
                "src/SQCD.Agv.Application/WireToGateSlotOperationExecutor.cs",
                StringComparison.OrdinalIgnoreCase))
        {
            AddMatches(source, path, literals, SlotReasonReturnRegex, "slot result reasonCode");
        }

        if (path.EndsWith(
                "src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs",
                StringComparison.OrdinalIgnoreCase))
        {
            AddMatches(source, path, literals, UnknownSlotReasonListRegex, "slot state reasonCodes");
        }
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

    private static HashSet<string> LoadRegistry(string repositoryRoot)
    {
        string registryPath = Path.Combine(
            repositoryRoot,
            "vendor",
            "8005-agv-protocol",
            "protocol-v0.1.1",
            "errors",
            "error-codes.json");
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

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SQCD_8005AGV.sln"))
                    && File.Exists(Path.Combine(
                        directory.FullName,
                        "vendor",
                        "8005-agv-protocol",
                        "protocol-v0.1.1",
                        "errors",
                        "error-codes.json")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root from the test process directories.");
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
