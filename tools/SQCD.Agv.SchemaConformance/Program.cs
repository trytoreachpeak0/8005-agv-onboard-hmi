using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvus.Json;
using Corvus.Json.Validator;
using SQCD.Agv.Contracts;

// Validates outbound protocol lines against the vendored JSON Schemas of the pinned protocol release
// (ADR-cross-0058 map: 8005-agv-program#27 decided it, #34 is this side).
//
//   SQCD.Agv.SchemaConformance --lines <ndjson> --report <directory> [--known <json>]
//   SQCD.Agv.SchemaConformance --judge <ndjson> --report <directory>
//
// --lines: each input line is {"messageType","origin","site","line"}: origin is product, synthetic-peer
// (FakeControlServer) or test, site names the sending method. Writes schema-coverage.json always and
// schema-violations.json when anything failed. Exit 0 = every line conforms or every violation is
// already on file, 1 = violations, 2 = the vendored contract does not match WireToGateRelease or the
// input is unusable.
//
// --judge: each input line is {"id","messageType","line"}; writes judgements.json, one
// {"id","valid","errors"} per input line in input order, and exits 0 whatever the verdicts. It only says
// whether the schema accepts a line -- nothing is a violation and nothing is on file. The inbound schema
// boundary census (SQCD.Agv.WireToGateG2Tests, onboard-hmi#44) asks it about the variants it generates
// before feeding them to the vehicle: a variant the schema rejects is a generator bug, and must never be
// read as the vehicle being stricter than the schema.
//
// It runs as its own process on purpose. Corvus.Json.Validator 4.6.7 is the version the protocol
// repository pins for an "isolated .NET conformance process", and it brings System.Text.Json 10.0.4
// and Roslyn with it: loaded into the test host, that System.Text.Json would replace the 8.0 one the
// vehicle ships with, and WireToGateProtocolSerializer's exact bytes are what G2 is meant to measure.

const string SchemaBaseUri = "https://schemas.8005-agv.local/wire-to-gate/v1/";
const int ErrorsPerLine = 5;

Dictionary<string, string> arguments = ParseArguments(args);
string? judgePath = arguments.GetValueOrDefault("judge");
if (judgePath is not null && (arguments.ContainsKey("lines") || arguments.ContainsKey("known")))
{
    throw new ArgumentException("--judge takes neither --lines nor --known.");
}
string linesPath = judgePath is not null
    ? string.Empty
    : arguments.GetValueOrDefault("lines") ?? throw new ArgumentException("--lines or --judge is required.");
string reportDirectory = arguments.GetValueOrDefault("report") ?? throw new ArgumentException("--report is required.");
Directory.CreateDirectory(reportDirectory);
KnownViolation[] known = arguments.GetValueOrDefault("known") is { } knownPath
    ? JsonSerializer.Deserialize<KnownViolation[]>(File.ReadAllText(knownPath), JsonSerializerOptions.Web) ?? []
    : [];
if (known.FirstOrDefault(entry => string.IsNullOrWhiteSpace(entry.Issue) == string.IsNullOrWhiteSpace(entry.Deliberate)) is { } malformed)
{
    return Fail(
        "Every known-violations entry names exactly one of issue (a filed defect) or deliberate (the test that " +
        "sends it on purpose): " + malformed);
}

string protocolRoot = Path.Combine(AppContext.BaseDirectory, WireToGateRelease.Tag);
string manifestPath = Path.Combine(protocolRoot, "manifest", "release.json");
string schemaRoot = Path.Combine(protocolRoot, "schemas");
if (!File.Exists(manifestPath) || !Directory.Exists(schemaRoot))
{
    return Fail(
        $"No vendored {WireToGateRelease.Tag} next to the validator ({protocolRoot}). The pinned identity moved " +
        "without vendor/8005-agv-protocol/ moving with it.");
}

// The two hashes are the contract; the files are only trusted once they reproduce them. Until this
// check existed, SchemaBundleSha256 had only ever been compared with another copy of itself.
string manifestSha256 = Sha256Hex(File.ReadAllBytes(manifestPath));
if (manifestSha256 != WireToGateRelease.ManifestSha256)
{
    return Fail($"Vendored manifest hashes to {manifestSha256}, WireToGateRelease.ManifestSha256 is {WireToGateRelease.ManifestSha256}.");
}
string schemaBundleSha256 = SchemaBundleSha256(schemaRoot);
if (schemaBundleSha256 != WireToGateRelease.SchemaBundleSha256)
{
    return Fail($"Vendored schemas/ hashes to {schemaBundleSha256}, WireToGateRelease.SchemaBundleSha256 is {WireToGateRelease.SchemaBundleSha256}.");
}

System.Text.Json.Nodes.JsonObject messages = JsonNode.Parse(File.ReadAllText(manifestPath))?["messages"]?.AsObject()
    ?? throw new InvalidDataException("Vendored manifest has no messages table.");

PrepopulatedDocumentResolver resolver = new();
foreach (string file in Directory.EnumerateFiles(schemaRoot, "*.json", SearchOption.AllDirectories))
{
    resolver.AddDocument(SchemaBaseUri + RelativeUnixPath(schemaRoot, file), JsonDocument.Parse(File.ReadAllBytes(file)));
}
JsonSchema.Options schemaOptions = new(resolver, false, null, true);

if (judgePath is not null)
{
    return Judge(judgePath);
}

List<ObservedLine> observed = [];
foreach (string text in File.ReadLines(linesPath))
{
    if (text.Length == 0)
    {
        continue;
    }
    observed.Add(JsonSerializer.Deserialize<ObservedLine>(text, JsonSerializerOptions.Web)
        ?? throw new InvalidDataException("Empty record in " + linesPath));
}

// A test calling WireToGateProtocolSerializer.Create itself is exercising the envelope, not sending anything.
ObservedLine[] checkedLines = observed.Where(line => line.Origin != "test").ToArray();

Dictionary<string, Violation> violations = [];
Stopwatch compileTime = new();
int distinctLines = 0;
foreach (IGrouping<string, ObservedLine> byType in checkedLines.GroupBy(line => line.MessageType).OrderBy(group => group.Key, StringComparer.Ordinal))
{
    // One schema per messageType, never the bundle's oneOf: the envelope's fields are copied into
    // every message schema, so one file is self-contained, and a oneOf failure buries the real cause
    // under every other branch's (#27, point 12).
    string? schemaPath = messages[byType.Key]?["schema"]?.GetValue<string>();
    if (schemaPath is null)
    {
        foreach (ObservedLine line in byType)
        {
            Record(line, [new Error("#", "messageType", $"'{byType.Key}' is not a message of {WireToGateRelease.Tag}.", Quote(byType.Key))]);
        }
        continue;
    }
    string schemaRelative = schemaPath["schemas/".Length..];
    compileTime.Start();
    JsonSchema schema = JsonSchema.FromText(
        File.ReadAllText(Path.Combine(schemaRoot, schemaRelative)), SchemaBaseUri + schemaRelative, schemaOptions);
    compileTime.Stop();
    foreach (IGrouping<string, ObservedLine> byContent in byType.GroupBy(line => line.Line, StringComparer.Ordinal))
    {
        distinctLines++;
        Error[] errors = Validate(schema, byContent.Key);
        if (errors.Length > 0)
        {
            foreach (ObservedLine line in byContent)
            {
                Record(line, errors);
            }
        }
    }
}

object Coverage(string origin, params string[] directions)
{
    SortedSet<string> expected = new(
        messages.Where(entry => directions.Contains(entry.Value?["direction"]?.GetValue<string>())).Select(entry => entry.Key),
        StringComparer.Ordinal);
    SortedDictionary<string, int> seen = new(
        checkedLines.Where(line => line.Origin == origin).GroupBy(line => line.MessageType).ToDictionary(group => group.Key, group => group.Count()),
        StringComparer.Ordinal);
    return new
    {
        directions,
        observed = seen,
        notObserved = expected.Where(type => !seen.ContainsKey(type)).ToArray(),
        outsideDirection = seen.Keys.Where(type => !expected.Contains(type)).ToArray()
    };
}

Violation[] ordered = violations.Values
    .OrderBy(violation => violation.MessageType, StringComparer.Ordinal)
    .ThenBy(violation => violation.Site, StringComparer.Ordinal)
    .ToArray();
foreach (Violation violation in ordered)
{
    KnownViolation?[] matches = violation.Errors
        .Select(error => known.FirstOrDefault(entry => entry.Matches(violation.MessageType, violation.Origin, error)))
        .ToArray();
    violation.OnFile = matches.All(match => match is not null) ? matches[0] : null;
}
int unknownViolations = ordered.Count(violation => violation.OnFile is null);
JsonSerializerOptions reportOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
File.WriteAllText(
    Path.Combine(reportDirectory, "schema-coverage.json"),
    JsonSerializer.Serialize(new
    {
        protocolTag = WireToGateRelease.Tag,
        manifestSha256,
        schemaBundleSha256,
        validator = "Corvus.Json.Validator 4.6.7",
        linesChecked = checkedLines.Length,
        distinctLinesChecked = distinctLines,
        testAuthoredLinesSkipped = observed.Count - checkedLines.Length,
        linesInViolation = ordered.Where(violation => violation.OnFile is null).Sum(violation => violation.Count),
        linesInKnownViolation = ordered.Where(violation => violation.OnFile is { IsDeliberate: false }).Sum(violation => violation.Count),
        linesInDeliberateViolation = ordered.Where(violation => violation.OnFile is { IsDeliberate: true }).Sum(violation => violation.Count),
        // Reported, never judged: which messages a run happens to exercise is a fact about the tests.
        origins = new
        {
            product = Coverage("product", "O_TO_C", "BIDIRECTIONAL"),
            syntheticPeer = Coverage("synthetic-peer", "C_TO_O", "BIDIRECTIONAL")
        }
    }, reportOptions) + "\n");

string summary = string.Create(
    CultureInfo.InvariantCulture,
    $"Schema conformance ({WireToGateRelease.Tag}): {checkedLines.Length} lines, {distinctLines} distinct, " +
    $"{checkedLines.Select(line => line.MessageType).Distinct().Count()} message types, {unknownViolations} distinct violations, {ordered.Length - unknownViolations} on file; " +
    $"schema compilation {compileTime.ElapsedMilliseconds} ms.");
Console.WriteLine(summary);
if (ordered.Length == 0)
{
    return 0;
}

File.WriteAllText(Path.Combine(reportDirectory, "schema-violations.json"), JsonSerializer.Serialize(ordered, reportOptions) + "\n");
foreach (Violation violation in ordered)
{
    string label = violation.OnFile switch
    {
        null => "SCHEMA VIOLATION",
        { IsDeliberate: true } onFile => "DELIBERATE SCHEMA VIOLATION (" + onFile.Deliberate + ")",
        { } onFile => "KNOWN SCHEMA VIOLATION (" + onFile.Issue + ")"
    };
    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"{label} x{violation.Count}: {violation.MessageType} sent by {violation.Origin} {violation.Site}"));
    foreach (Error error in violation.Errors)
    {
        Console.WriteLine($"  {error.Pointer} [{error.Keyword}] {error.Message} actual: {error.Actual}");
    }
}
return unknownViolations > 0 ? 1 : 0;

int Judge(string path)
{
    Dictionary<string, JsonSchema> compiled = new(StringComparer.Ordinal);
    List<Judgement> judgements = [];
    foreach (string text in File.ReadLines(path))
    {
        if (text.Length == 0)
        {
            continue;
        }
        JudgedLine item = JsonSerializer.Deserialize<JudgedLine>(text, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Empty record in " + path);
        Error[] errors;
        if (messages[item.MessageType]?["schema"]?.GetValue<string>() is not { } schemaPath)
        {
            errors = [new Error("#", "messageType", $"'{item.MessageType}' is not a message of {WireToGateRelease.Tag}.", Quote(item.MessageType))];
        }
        else
        {
            if (!compiled.TryGetValue(item.MessageType, out JsonSchema schema))
            {
                string schemaRelative = schemaPath["schemas/".Length..];
                schema = JsonSchema.FromText(
                    File.ReadAllText(Path.Combine(schemaRoot, schemaRelative)), SchemaBaseUri + schemaRelative, schemaOptions);
                compiled[item.MessageType] = schema;
            }
            errors = Validate(schema, item.Line);
        }
        judgements.Add(new Judgement(item.Id, errors.Length == 0, errors));
    }
    // Read by the census, not by people: no indentation.
    File.WriteAllText(Path.Combine(reportDirectory, "judgements.json"), JsonSerializer.Serialize(judgements, JsonSerializerOptions.Web) + "\n");
    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"Judged {judgements.Count} lines against {compiled.Count} message schemas ({WireToGateRelease.Tag}): {judgements.Count(judgement => !judgement.Valid)} invalid."));
    return 0;
}

void Record(ObservedLine line, Error[] errors)
{
    string key = line.MessageType + "\n" + line.Origin + "\n" + line.Site + "\n" +
        string.Join("\n", errors.Select(error => error.Pointer + " " + error.Keyword));
    if (violations.TryGetValue(key, out Violation? existing))
    {
        existing.Count++;
        return;
    }
    violations[key] = new Violation(line.MessageType, line.Origin, line.Site, errors, line.Line) { Count = 1 };
}

static Error[] Validate(JsonSchema schema, string line)
{
    JsonDocument document;
    try
    {
        document = JsonDocument.Parse(line);
    }
    catch (JsonException exception)
    {
        return [new Error("#", "json", exception.Message, Truncate(line))];
    }
    using (document)
    {
        if (schema.Validate(document.RootElement, ValidationLevel.Flag).IsValid)
        {
            return [];
        }
        return schema.Validate(document.RootElement, ValidationLevel.Detailed).Results
            .Where(result => !result.Valid)
            .Take(ErrorsPerLine)
            .Select(result => Describe(result, document.RootElement))
            .ToArray();
    }
}

static Error Describe(ValidationResult result, JsonElement root)
{
    string validationLocation = string.Empty;
    string pointer = string.Empty;
    if (result.Location is { } location)
    {
        validationLocation = location.Item1.ToString();
        pointer = location.Item3.ToString().TrimStart('#');
    }
    string keyword = KeywordOf(validationLocation);
    const string MessagePrefix = "Validation ";
    if (keyword == "(unknown)" && result.Message is { } text && text.StartsWith(MessagePrefix, StringComparison.Ordinal) &&
        text.IndexOf(' ', MessagePrefix.Length) is > 0 and var end)
    {
        // Some keywords (const among them) are reported at a location that does not name them; the
        // message always does: "Validation const - the value '1' did not match '3'."
        keyword = text[MessagePrefix.Length..end];
    }
    string actual = Resolve(root, pointer) is { } value ? Truncate(value.GetRawText()) : "(absent)";
    string message = string.IsNullOrEmpty(result.Message) ? "violates '" + keyword + "'" : result.Message;
    return new Error("#" + pointer, keyword, message, actual);
}

// The validation location is a path through the schema; the violated keyword is the last segment that
// is one. Anything after it names the offending property (additionalProperties/extra) or index.
static string KeywordOf(string validationLocation)
{
    string[] keywords =
    [
        "additionalProperties", "allOf", "anyOf", "const", "contains", "dependentRequired", "enum",
        "exclusiveMaximum", "exclusiveMinimum", "format", "items", "maxItems", "maxLength", "maxProperties",
        "maximum", "minItems", "minLength", "minProperties", "minimum", "multipleOf", "not", "oneOf", "pattern",
        "prefixItems", "propertyNames", "required", "type", "unevaluatedProperties", "uniqueItems"
    ];
    string[] segments = validationLocation.Split('/');
    for (int index = segments.Length - 1; index >= 0; index--)
    {
        if (keywords.Contains(segments[index], StringComparer.Ordinal))
        {
            return segments[index];
        }
    }
    return "(unknown)";
}

static JsonElement? Resolve(JsonElement root, string pointer)
{
    JsonElement current = root;
    foreach (string raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
    {
        string segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out JsonElement property))
        {
            current = property;
        }
        else if (current.ValueKind == JsonValueKind.Array &&
            int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
            index < current.GetArrayLength())
        {
            current = current[index];
        }
        else
        {
            return null;
        }
    }
    return current;
}

// Same definition as the protocol repository's tools/finalize-manifest.mjs: SHA-256 over one
// "schemas/<path>:<sha256>" line per file, sorted by path, each line newline-terminated.
static string SchemaBundleSha256(string schemaRoot)
{
    StringBuilder lines = new();
    foreach (string file in Directory.EnumerateFiles(schemaRoot, "*", SearchOption.AllDirectories)
        .OrderBy(file => RelativeUnixPath(schemaRoot, file), StringComparer.Ordinal))
    {
        lines.Append("schemas/").Append(RelativeUnixPath(schemaRoot, file)).Append(':')
            .Append(Sha256Hex(File.ReadAllBytes(file))).Append('\n');
    }
    return Sha256Hex(Encoding.UTF8.GetBytes(lines.ToString()));
}

static string RelativeUnixPath(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static string Quote(string value) => JsonSerializer.Serialize(value);

static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "...";

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

static Dictionary<string, string> ParseArguments(string[] values)
{
    Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
    for (int index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Arguments must use --name value pairs.");
        }
        result[values[index][2..]] = values[index + 1];
    }
    return result;
}

internal sealed record ObservedLine(string MessageType, string Origin, string Site, string Line);

internal sealed record Error(string Pointer, string Keyword, string Message, string Actual);

internal sealed record JudgedLine(string Id, string MessageType, string Line);

internal sealed record Judgement(string Id, bool Valid, Error[] Errors);

internal sealed record Violation(string MessageType, string Origin, string Site, Error[] Errors, string SampleLine)
{
    public int Count { get; set; }

    public KnownViolation? OnFile { get; set; }
}

// A violation on file: printed and reported every run but not failed on. Either a defect already
// filed (Issue), until it is fixed and the entry deleted, or a line a test makes FakeControlServer
// send on purpose to see the vehicle refuse it (Deliberate, naming that test) -- which only ever
// matches the synthetic peer, never the vehicle's own lines. Every error of a violation must match an
// entry, so a new defect riding on the same line still fails. Array indices in Pointer are '*'.
internal sealed record KnownViolation(string MessageType, string Pointer, string Keyword, string Actual, string? Issue, string? Deliberate)
{
    public bool IsDeliberate => !string.IsNullOrWhiteSpace(Deliberate);

    public bool Matches(string messageType, string origin, Error error) =>
        messageType == MessageType &&
        (!IsDeliberate || origin == "synthetic-peer") &&
        error.Keyword == Keyword &&
        error.Actual == Actual &&
        string.Join('/', error.Pointer.Split('/').Select(segment => segment.Length > 0 && segment.All(char.IsAsciiDigit) ? "*" : segment)) == Pointer;
}
