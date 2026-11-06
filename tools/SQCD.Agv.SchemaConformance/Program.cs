using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Corvus.Json;
using Corvus.Json.Validator;
using SQCD.Agv.Contracts;

// Validates outbound protocol lines against the JSON Schemas of the protocol this repository is bound
// to, read straight from the vendored copy under vendor/8005-agv-protocol (8005-agv-onboard-hmi#74).
//
//   SQCD.Agv.SchemaConformance --lines <ndjson> --report <directory> --vendor <directory> [--known <json>]
//
// Each input line is {"messageType","origin","site","line"}: origin is product, synthetic-peer
// (FakeControlServer) or test, site names the sending method. Writes schema-coverage.json always, and
// schema-violations.json when any line failed. Exit 0 = every line conforms or every violation is on
// file, 1 = violations, 2 = the vendored contract is not the one WireToGateRelease names, or the input
// is unusable.
//
// It runs as its own process on purpose. Corvus.Json.Validator 4.6.7 is the version the protocol
// repository's compatibility matrix pins for an "isolated .NET conformance process", and it brings
// System.Text.Json 10.x and Roslyn with it: loaded into the test host, that System.Text.Json would
// replace the 8.0 one the vehicle ships with, and WireToGateProtocolSerializer's exact bytes are what
// G2 is meant to measure.

const int ErrorsPerLine = 10;
const string Validator = "Corvus.Json.Validator 4.6.7";

Dictionary<string, string> arguments = ParseArguments(args);
string linesPath = Required(arguments, "lines");
string reportDirectory = Required(arguments, "report");
string vendorRoot = Required(arguments, "vendor");
Directory.CreateDirectory(reportDirectory);
// A violations file left by an earlier run in the same directory must not outlive the run that wrote it.
File.Delete(Path.Combine(reportDirectory, "schema-violations.json"));

KnownViolation[] known = arguments.GetValueOrDefault("known") is { } knownPath
    ? JsonSerializer.Deserialize<KnownViolation[]>(File.ReadAllText(knownPath), JsonSerializerOptions.Web) ?? []
    : [];
if (known.FirstOrDefault(entry => !entry.IsWellFormed) is { } malformed)
{
    return Fail(
        "Every known-violations entry names messageType, pointer, keyword and actual, and exactly one of issue " +
        "(a filed defect, as its GitHub issue URL) or deliberate (the test that sends it on purpose): " + malformed);
}

// The vendored copy is only trusted once it reproduces the identity this build puts on every envelope:
// the manifest's bytes hash to WireToGateRelease.ManifestSha256, and every schema file is exactly the
// one that manifest's own file table lists, in both directions. Checked on every run, so the gate
// always validates against the contract this repository is currently bound to and follows it when the
// binding moves.
string manifestPath = Path.Combine(vendorRoot, "manifest", "release.json");
string schemaRoot = Path.Combine(vendorRoot, "schemas");
if (!File.Exists(manifestPath) || !Directory.Exists(schemaRoot))
{
    return Fail($"No vendored protocol at {vendorRoot} (expected manifest/release.json and schemas/).");
}
byte[] manifestBytes = File.ReadAllBytes(manifestPath);
string manifestSha256 = Sha256Hex(manifestBytes);
if (manifestSha256 != WireToGateRelease.ManifestSha256)
{
    return Fail($"Vendored manifest hashes to {manifestSha256}, WireToGateRelease.ManifestSha256 is {WireToGateRelease.ManifestSha256}.");
}
System.Text.Json.Nodes.JsonObject manifest = JsonNode.Parse(manifestBytes)?.AsObject()
    ?? throw new InvalidDataException("Vendored manifest is not a JSON object.");
Dictionary<string, string> fileTable = manifest["files"]!.AsArray()
    .Select(entry => (Path: entry!["path"]!.GetValue<string>(), Sha256: entry["sha256"]!.GetValue<string>()))
    .Where(entry => entry.Path.StartsWith("schemas/", StringComparison.Ordinal))
    .ToDictionary(entry => entry.Path, entry => entry.Sha256, StringComparer.Ordinal);
List<string> offences = [];
HashSet<string> vendoredSchemas = new(StringComparer.Ordinal);
foreach (string file in Directory.EnumerateFiles(schemaRoot, "*", SearchOption.AllDirectories))
{
    string relative = "schemas/" + RelativeUnixPath(schemaRoot, file);
    vendoredSchemas.Add(relative);
    string actual = Sha256Hex(File.ReadAllBytes(file));
    if (!fileTable.TryGetValue(relative, out string? expected))
    {
        offences.Add($"{relative} is vendored but the manifest does not list it");
    }
    else if (actual != expected)
    {
        offences.Add($"{relative} is {actual}, the manifest says {expected}");
    }
}
offences.AddRange(fileTable.Keys.Where(path => !vendoredSchemas.Contains(path)).Select(path => $"{path} is in the manifest but not vendored"));
if (offences.Count > 0)
{
    return Fail("The vendored schemas are not the manifest's: " + string.Join("; ", offences.Order(StringComparer.Ordinal)));
}

System.Text.Json.Nodes.JsonObject messages = manifest["messages"]?.AsObject()
    ?? throw new InvalidDataException("Vendored manifest has no messages table.");

List<ObservedLine> observed = [];
foreach (string text in File.ReadLines(linesPath))
{
    if (text.Length > 0)
    {
        observed.Add(JsonSerializer.Deserialize<ObservedLine>(text, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Empty record in " + linesPath));
    }
}

// A test calling WireToGateProtocolSerializer.Create itself is exercising the serializer, not sending.
ObservedLine[] checkedLines = observed.Where(line => line.Origin != "test").ToArray();

// Every schema is registered under its own $id, which is what every $ref names.
PrepopulatedDocumentResolver resolver = new();
Dictionary<string, string> schemaIds = new(StringComparer.Ordinal);
foreach (string file in Directory.EnumerateFiles(schemaRoot, "*.json", SearchOption.AllDirectories))
{
    JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
    string id = document.RootElement.GetProperty("$id").GetString()
        ?? throw new InvalidDataException(file + " has no $id.");
    resolver.AddDocument(id, document);
    schemaIds["schemas/" + RelativeUnixPath(schemaRoot, file)] = id;
}
JsonSchema.Options schemaOptions = new(resolver, false, null, true);

Stopwatch compileTime = new();
Stopwatch validationTime = new();
JsonSchema Compile(string manifestRelativePath)
{
    compileTime.Start();
    try
    {
        return JsonSchema.FromText(
            File.ReadAllText(Path.Combine(vendorRoot, manifestRelativePath)), schemaIds[manifestRelativePath], schemaOptions);
    }
    finally
    {
        compileTime.Stop();
    }
}

// Compiled only when there is something to validate: a run that sent nothing pays nothing.
JsonSchema[] envelopeSchema = checkedLines.Length > 0 ? [Compile("schemas/envelope.schema.json")] : [];
Dictionary<string, Violation> violations = [];
int distinctLines = 0;
foreach (IGrouping<string, ObservedLine> byType in checkedLines.GroupBy(line => line.MessageType).OrderBy(group => group.Key, StringComparer.Ordinal))
{
    // The envelope schema and the message's own schema, one line each -- never the bundle's oneOf, which
    // buries the real cause under every other branch's failure.
    string? schemaPath = messages[byType.Key]?["schema"]?.GetValue<string>();
    JsonSchema[] messageSchema = schemaPath is null ? [] : [Compile(schemaPath)];
    validationTime.Start();
    foreach (IGrouping<string, ObservedLine> byContent in byType.GroupBy(line => line.Line, StringComparer.Ordinal))
    {
        distinctLines++;
        Error[] errors = schemaPath is null
            ? [new Error("#/messageType", "messageType", $"'{byType.Key}' is not a message of {WireToGateRelease.Tag} {WireToGateRelease.ReleaseVersion}.", Quote(byType.Key))]
            : Validate(envelopeSchema, messageSchema, byContent.Key);
        if (errors.Length > 0)
        {
            foreach (ObservedLine line in byContent)
            {
                Record(line, errors);
            }
        }
    }
    validationTime.Stop();
}

Violation[] ordered = violations.Values
    .OrderBy(violation => violation.MessageType, StringComparer.Ordinal)
    .ThenBy(violation => violation.Origin, StringComparer.Ordinal)
    .ThenBy(violation => violation.Site, StringComparer.Ordinal)
    .ToArray();
HashSet<KnownViolation> usedEntries = [];
foreach (Violation violation in ordered)
{
    KnownViolation?[] matches = violation.Errors
        .Select(error => known.FirstOrDefault(entry => entry.Matches(violation.MessageType, violation.Origin, error)))
        .ToArray();
    // One error left unregistered keeps the whole line failing: a new defect riding on a known one
    // must not hide behind it.
    if (matches.All(match => match is not null))
    {
        violation.OnFile = matches.Distinct().Cast<KnownViolation>().ToArray();
        usedEntries.UnionWith(violation.OnFile);
    }
}
int LinesWhere(Func<Violation, bool> predicate) => ordered.Where(predicate).Sum(violation => violation.Count);

object OriginCoverage(string origin, params string[] directions)
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
        messageTypesObserved = seen.Count,
        messageTypesNotObserved = expected.Where(type => !seen.ContainsKey(type)).ToArray(),
        messageTypesOutsideDirection = seen.Keys.Where(type => !expected.Contains(type)).ToArray()
    };
}

JsonSerializerOptions reportOptions = new(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    // The reports are read by people, and a sample line with every quote escaped is not readable.
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};
File.WriteAllText(
    Path.Combine(reportDirectory, "schema-coverage.json"),
    JsonSerializer.Serialize(new
    {
        protocol = new
        {
            WireToGateRelease.Tag,
            WireToGateRelease.ReleaseVersion,
            WireToGateRelease.ProfileId,
            WireToGateRelease.ProtocolVersion,
            WireToGateRelease.ApprovalStatus,
            manifestSha256,
            vendoredSchemaFiles = vendoredSchemas.Count
        },
        validator = Validator,
        linesObserved = observed.Count,
        testAuthoredLinesSkipped = observed.Count - checkedLines.Length,
        linesChecked = checkedLines.Length,
        distinctLinesChecked = distinctLines,
        linesInViolation = LinesWhere(violation => violation.OnFile is null),
        linesInKnownViolation = LinesWhere(violation => violation.OnFile is not null),
        linesInIssueViolation = LinesWhere(violation => violation.OnFile is { } onFile && onFile.All(entry => !entry.IsDeliberate)),
        linesInDeliberateViolation = LinesWhere(violation => violation.OnFile is { } onFile && onFile.Any(entry => entry.IsDeliberate)),
        schemaCompilationMilliseconds = compileTime.ElapsedMilliseconds,
        validationMilliseconds = validationTime.ElapsedMilliseconds,
        // Reported, never judged: which messages a run happens to send is a fact about the tests. A
        // message no test sends is one whose sender this gate cannot see.
        byMessageType = new SortedDictionary<string, SortedDictionary<string, int>>(
            checkedLines.GroupBy(line => line.MessageType).ToDictionary(
                group => group.Key,
                group => new SortedDictionary<string, int>(
                    group.GroupBy(line => line.Origin).ToDictionary(byOrigin => byOrigin.Key, byOrigin => byOrigin.Count()),
                    StringComparer.Ordinal)),
            StringComparer.Ordinal),
        origins = new
        {
            product = OriginCoverage("product", "O_TO_C", "BIDIRECTIONAL"),
            syntheticPeer = OriginCoverage("synthetic-peer", "C_TO_O", "BIDIRECTIONAL")
        },
        knownViolationEntriesNotMatched = known.Where(entry => !usedEntries.Contains(entry)).ToArray()
    }, reportOptions) + "\n");

int unknownViolations = ordered.Count(violation => violation.OnFile is null);
Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Schema conformance ({WireToGateRelease.Tag} {WireToGateRelease.ApprovalStatus}): {checkedLines.Length} lines, {distinctLines} distinct, " +
    $"{checkedLines.Select(line => line.MessageType).Distinct().Count()} message types; {unknownViolations} distinct violations, " +
    $"{ordered.Length - unknownViolations} on file; schema compilation {compileTime.ElapsedMilliseconds} ms, validation {validationTime.ElapsedMilliseconds} ms."));
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
        { } onFile when onFile.Any(entry => entry.IsDeliberate) => "DELIBERATE SCHEMA VIOLATION (" + string.Join(", ", onFile.Select(entry => entry.Deliberate ?? entry.Issue)) + ")",
        { } onFile => "KNOWN SCHEMA VIOLATION (" + string.Join(", ", onFile.Select(entry => entry.Issue).Distinct()) + ")"
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

void Record(ObservedLine line, Error[] errors)
{
    string key = line.MessageType + "\n" + line.Origin + "\n" + line.Site + "\n" +
        string.Join("\n", errors.Select(error => error.Pointer + " " + error.Keyword + " " + error.Actual));
    if (violations.TryGetValue(key, out Violation? existing))
    {
        existing.Count++;
        return;
    }
    violations[key] = new Violation(line.MessageType, line.Origin, line.Site, errors, line.Line) { Count = 1 };
}

// envelope.schema.json is the envelope alone: its payload is a closed empty object (properties {},
// additionalProperties false), and the protocol's own G1 never applies it to a line that carries one.
// So it is applied to the line with its payload emptied, and the message's own schema -- which repeats
// every envelope field and adds the payload -- to the line as sent. A payload error is reported once,
// by the message schema; an envelope error can be reported by both and is de-duplicated.
static Error[] Validate(JsonSchema[] envelopeSchema, JsonSchema[] messageSchema, string line)
{
    JsonNode? parsed;
    try
    {
        parsed = JsonNode.Parse(line);
    }
    catch (JsonException exception)
    {
        return [new Error("#", "json", exception.Message, Truncate(line))];
    }
    string envelopeOnly = line;
    if (parsed is System.Text.Json.Nodes.JsonObject root && root.ContainsKey("payload"))
    {
        root["payload"] = new System.Text.Json.Nodes.JsonObject();
        envelopeOnly = root.ToJsonString();
    }
    return [
        .. Check(envelopeSchema, envelopeOnly)
            .Concat(Check(messageSchema, line))
            .DistinctBy(error => (error.Pointer, error.Keyword, error.Actual))
            .Take(ErrorsPerLine)
    ];

    static IEnumerable<Error> Check(JsonSchema[] schemas, string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        List<Error> errors = [];
        foreach (JsonSchema schema in schemas)
        {
            if (!schema.Validate(document.RootElement, ValidationLevel.Flag).IsValid)
            {
                errors.AddRange(schema.Validate(document.RootElement, ValidationLevel.Detailed).Results
                    .Where(result => !result.Valid)
                    .Select(result => Describe(result, document.RootElement)));
            }
        }
        return errors;
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
    const string RequiredPrefix = "the required property '";
    if (keyword == "required" && result.Message is { } required &&
        required.IndexOf(RequiredPrefix, StringComparison.Ordinal) is >= 0 and var start &&
        required.IndexOf('\'', start + RequiredPrefix.Length) is > 0 and var close)
    {
        // Point at the missing property itself, not at the object that lacks it: the object's raw text
        // carries ids that differ on every line, which would split one defect into as many groups as
        // lines and make it impossible to register.
        string name = required[(start + RequiredPrefix.Length)..close];
        pointer += "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
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

static string RelativeUnixPath(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static string Quote(string value) => JsonSerializer.Serialize(value);

static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "...";

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

static string Required(Dictionary<string, string> arguments, string name) =>
    arguments.GetValueOrDefault(name) ?? throw new ArgumentException($"--{name} is required.");

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

internal sealed record Violation(string MessageType, string Origin, string Site, Error[] Errors, string SampleLine)
{
    public int Count { get; set; }

    public KnownViolation[]? OnFile { get; set; }
}

// A violation on file: printed and reported every run but not failed on. Either a defect already filed
// (Issue), until it is fixed and the entry deleted, or a line a test makes FakeControlServer send on
// purpose to see the vehicle refuse it (Deliberate, naming that test as Class.Method) -- which only ever
// matches the synthetic peer, never the vehicle's own lines. Every error of a line must match an entry,
// so a new defect riding on the same line still fails. Array indices in Pointer are written '*'.
internal sealed record KnownViolation(string MessageType, string Pointer, string Keyword, string Actual, string? Issue, string? Deliberate)
{
    [JsonIgnore]
    public bool IsDeliberate => !string.IsNullOrWhiteSpace(Deliberate);

    [JsonIgnore]
    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(MessageType) && !string.IsNullOrWhiteSpace(Pointer) &&
        !string.IsNullOrWhiteSpace(Keyword) && Actual is not null &&
        (IsDeliberate
            ? string.IsNullOrWhiteSpace(Issue)
            : Issue is { } issue && issue.StartsWith("https://github.com/trytoreachpeak0/", StringComparison.Ordinal) && issue.Contains("/issues/", StringComparison.Ordinal));

    public bool Matches(string messageType, string origin, Error error) =>
        messageType == MessageType &&
        (!IsDeliberate || origin == "synthetic-peer") &&
        error.Keyword == Keyword &&
        error.Actual == Actual &&
        string.Join('/', error.Pointer.Split('/').Select(segment => segment.Length > 0 && segment.All(char.IsAsciiDigit) ? "*" : segment)) == Pointer;
}
