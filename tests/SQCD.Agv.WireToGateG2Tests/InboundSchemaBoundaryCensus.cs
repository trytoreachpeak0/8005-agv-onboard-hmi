using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SQCD.Agv.Contracts;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The inbound schema boundary census (8005-agv-onboard-hmi#38 decided it, #44 and #45 build it). An
/// inbound message the protocol's JSON Schema accepts must not be refused by the vehicle's own
/// context-free checks: twice a hand-written limit stricter than the schema (#37, 8005-agv-program#48)
/// locked a live session into a refuse-and-resend loop, and nothing was watching for a third.
/// </summary>
/// <remarks>
/// <para>
/// For each message type <see cref="WireToGateSessionClient.CheckInboundShape"/> reaches, the seed is the
/// protocol's own <c>examples/valid/&lt;type&gt;/V-&lt;type&gt;-MIN-001.json</c>, vendored and checked
/// against the manifest's files table. One variant per schema boundary changes one place of it
/// (<see cref="SchemaBoundaryEnumerator"/>). Every variant is first judged by
/// tools/SQCD.Agv.SchemaConformance: one the schema rejects is a generator error and fails as such -- it
/// must never be registered, or a generator bug would be read as the vehicle being stricter, and then
/// "fixed" by loosening the vehicle past the schema. A schema-valid variant the vehicle refuses (any
/// throw) fails, unless it is on file in <c>inbound-stricter-than-schema.json</c>.
/// </para>
/// <para>
/// That file has three kinds of entry. <c>companion</c>: fields changed together with a variant, for a
/// cross-field rule the schema cannot state. <c>intentional</c>: the vehicle is stricter on a contract
/// basis the entry cites (an <c>x-*</c> annotation or protocol text). <c>issue</c>: a filed defect,
/// including one waiting for the next protocol batch. Everything else stricter than the schema is loosened
/// in the vehicle. An entry that matches no variant, or whose refused variant the vehicle now accepts,
/// fails too, so the file cannot rot.
/// </para>
/// <para>
/// No <c>IntegrationSlice</c> trait on purpose: run-w2g-g2.ps1 runs it once, in the whole-repository pass,
/// with or without <c>-SkipProtocolG1</c>, instead of again under each of the eight slices.
/// </para>
/// </remarks>
public sealed class InboundSchemaBoundaryCensus(InboundSchemaBoundaryCensusFixture census)
    : IClassFixture<InboundSchemaBoundaryCensusFixture>
{
    public static TheoryData<string> CheckedMessageTypes()
    {
        TheoryData<string> data = new();
        foreach (string messageType in WireToGateSessionClient.InboundShapeCheckedMessageTypes.Order(StringComparer.Ordinal))
        {
            data.Add(messageType);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CheckedMessageTypes))]
    public void VehicleAcceptsEverySchemaValidBoundaryVariant(string messageType)
    {
        MessageCensus result = census.Of(messageType);
        List<string> failures = [];
        int refusedOnFile = 0;
        foreach (CensusVariant variant in result.Variants)
        {
            if (variant.SchemaErrors.Count > 0)
            {
                failures.Add(
                    $"GENERATOR ERROR (the schema rejects this variant, so it says nothing about the vehicle: fix the generator or the companion, never register it): {variant.Id}{Notes(variant)}\n" +
                    $"  schema: {string.Join(" | ", variant.SchemaErrors)}\n" +
                    $"  {variant.Line}");
                continue;
            }

            RegistryEntry? onFile = variant.Boundary is null ? null : census.Registry.StricterOnFile(messageType, variant.Boundary);
            Exception? refusal = RefusalOf(variant);
            if (refusal is not null && onFile is null)
            {
                failures.Add(
                    $"REFUSED {variant.Id}{Notes(variant)}\n" +
                    $"  {refusal.GetType().Name}: {refusal.Message}\n" +
                    $"  thrown at {ThrowSite(refusal)}\n" +
                    $"  {variant.Line}");
            }
            else if (refusal is null && onFile is not null)
            {
                failures.Add($"STALE ENTRY (the vehicle accepts this variant now; delete the entry): {variant.Id}\n  {onFile}");
            }
            else if (refusal is not null)
            {
                refusedOnFile++;
            }
        }
        failures.AddRange(result.UnmatchedEntries.Select(entry => $"STALE ENTRY (matches no variant of {messageType}; delete or correct it):\n  {entry}"));

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{messageType}: {result.Variants.Count} variants including the seed, " +
            $"{result.Variants.Count(variant => variant.Notes.Any(note => note.StartsWith("companion", StringComparison.Ordinal)))} with a companion, " +
            $"{refusedOnFile} refused and on file, {failures.Count} failure(s).");

        Assert.True(
            failures.Count == 0,
            $"Inbound schema boundary census, {messageType}: {failures.Count} failure(s) across {result.Variants.Count} variants.\n\n" +
            string.Join("\n\n", failures));
    }

    /// <summary>What the vehicle throws on receiving this variant, or null when it accepts it.</summary>
    private static Exception? RefusalOf(CensusVariant variant)
    {
        try
        {
            WireToGateEnvelope received = WireToGateProtocolSerializer.DeserializeAndValidate(variant.Line, variant.AgvId);
            WireToGateSessionClient.CheckInboundShape(received);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static string ThrowSite(Exception exception)
    {
        foreach (StackFrame frame in new StackTrace(exception, true).GetFrames())
        {
            MethodBase? method = frame.GetMethod();
            Type? type = method?.DeclaringType;
            if (method is null || type is null || type.Assembly.GetName().Name?.StartsWith("SQCD.Agv.", StringComparison.Ordinal) != true)
            {
                continue;
            }
            // A lambda runs on a compiler-generated display class; name the type a reader would search for.
            while (type.Name.StartsWith('<') && type.DeclaringType is not null)
            {
                type = type.DeclaringType;
            }
            int line = frame.GetFileLineNumber();
            return $"{type.Name}.{method.Name}" + (line > 0 ? $" line {line}" : string.Empty);
        }
        return "(no SQCD.Agv frame on the stack)";
    }

    private static string Notes(CensusVariant variant) =>
        variant.Notes.Count == 0 ? string.Empty : "\n  note: " + string.Join("\n  note: ", variant.Notes);
}

/// <summary>
/// Builds every variant once for the whole class and has the schema validator judge them all in one
/// process run, so a generator error is known before any variant reaches the vehicle.
/// </summary>
public sealed class InboundSchemaBoundaryCensusFixture : IAsyncLifetime
{
    private readonly Dictionary<string, MessageCensus> _byMessageType = new(StringComparer.Ordinal);

    internal CensusRegistry Registry { get; private set; } = null!;

    internal MessageCensus Of(string messageType) => _byMessageType[messageType];

    public async ValueTask InitializeAsync()
    {
        string repository = OutboundSchemaConformance.RepositoryRoot();
        string release = Path.Combine(repository, "vendor", "8005-agv-protocol", WireToGateRelease.Tag);

        // The manifest is trusted because it reproduces the pinned hash; each seed because the manifest
        // lists its hash. Both checks bind the seeds to the same pin the vehicle ships with.
        byte[] manifestBytes = await File.ReadAllBytesAsync(Path.Combine(release, "manifest", "release.json"));
        if (Sha256Hex(manifestBytes) != WireToGateRelease.ManifestSha256)
        {
            throw new InvalidDataException(
                $"Vendored manifest hashes to {Sha256Hex(manifestBytes)}, WireToGateRelease.ManifestSha256 is {WireToGateRelease.ManifestSha256}.");
        }
        JsonObject manifest = JsonNode.Parse(manifestBytes)!.AsObject();
        Dictionary<string, string> listedSha256 = manifest["files"]!.AsArray().ToDictionary(
            file => file!["path"]!.GetValue<string>(),
            file => file!["sha256"]!.GetValue<string>(),
            StringComparer.Ordinal);

        ProtocolSchemas schemas = ProtocolSchemas.Load(Path.Combine(release, "schemas"));
        Registry = CensusRegistry.Load(Path.Combine(repository, "tests", "SQCD.Agv.WireToGateG2Tests", "inbound-stricter-than-schema.json"));
        if (Registry.MessageTypes.FirstOrDefault(type => !WireToGateSessionClient.InboundShapeCheckedMessageTypes.Contains(type)) is { } uncovered)
        {
            throw new InvalidDataException(
                $"inbound-stricter-than-schema.json has entries for {uncovered}, which the census does not check: they could never match anything.");
        }

        foreach (string messageType in WireToGateSessionClient.InboundShapeCheckedMessageTypes.Order(StringComparer.Ordinal))
        {
            string seedPath = $"examples/valid/{messageType}/V-{messageType}-MIN-001.json";
            byte[] seedBytes = await File.ReadAllBytesAsync(Path.Combine(release, seedPath));
            if (!listedSha256.TryGetValue(seedPath, out string? listed) || Sha256Hex(seedBytes) != listed)
            {
                throw new InvalidDataException(
                    $"Vendored seed {seedPath} hashes to {Sha256Hex(seedBytes)}; the manifest lists {listed ?? "no such file"}.");
            }
            string schemaPath = manifest["messages"]?[messageType]?["schema"]?.GetValue<string>()
                ?? throw new InvalidDataException($"{messageType} is not a message of {WireToGateRelease.Tag}.");
            JsonObject messageSchema = schemas.Document(schemaPath["schemas/".Length..]);
            BoundaryVariantGenerator generator = new(schemas, messageSchema);
            JsonObject seed = JsonNode.Parse(seedBytes)!.AsObject();

            List<CensusVariant> variants = [new CensusVariant(messageType + " (seed)", null, seed, [])];
            HashSet<RegistryEntry> matched = [];
            foreach (SchemaBoundary boundary in SchemaBoundaryEnumerator.Enumerate(schemas, messageSchema))
            {
                List<string> notes = [];
                JsonObject envelope = generator.Apply(seed, boundary, notes);
                foreach (RegistryEntry entry in Registry.Matching(messageType, boundary))
                {
                    matched.Add(entry);
                    if (entry.Disposition == "companion")
                    {
                        entry.ApplyTo(envelope, generator);
                        notes.Add("companion " + entry.SetText);
                    }
                }
                variants.Add(new CensusVariant(boundary.Describe(messageType), boundary, envelope, notes));
            }
            if (variants.GroupBy(variant => variant.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
            {
                throw new InvalidDataException("Two variants share the id " + duplicate.Key);
            }
            _byMessageType[messageType] = new MessageCensus(
                variants,
                Registry.Of(messageType).Where(entry => !matched.Contains(entry)).ToArray());
        }

        Dictionary<string, IReadOnlyList<string>> judgements = await JudgeAsync(
            _byMessageType.SelectMany(entry => entry.Value.Variants.Select(variant => (entry.Key, variant))).ToList());
        foreach (CensusVariant variant in _byMessageType.Values.SelectMany(entry => entry.Variants))
        {
            variant.SchemaErrors = judgements[variant.Id];
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<Dictionary<string, IReadOnlyList<string>>> JudgeAsync(
        List<(string MessageType, CensusVariant Variant)> variants)
    {
        string work = Path.Combine(Path.GetTempPath(), "w2g-onboard-census-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string input = Path.Combine(work, "variants.ndjson");
            await File.WriteAllLinesAsync(input, variants.Select(item => JsonSerializer.Serialize(
                new { id = item.Variant.Id, messageType = item.MessageType, line = item.Variant.Line })));
            (int exitCode, string report) = await OutboundSchemaConformance.RunValidatorAsync("--judge", input, "--report", work);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The schema validator could not judge the census variants (exit {exitCode}).{Environment.NewLine}{report}");
            }
            JsonArray judged = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(work, "judgements.json")))!.AsArray();
            return judged.ToDictionary(
                judgement => judgement!["id"]!.GetValue<string>(),
                judgement => (IReadOnlyList<string>)judgement!["errors"]!.AsArray()
                    .Select(item => $"{item!["pointer"]} [{item["keyword"]}] {item["message"]} actual: {item["actual"]}")
                    .ToArray(),
                StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal sealed record MessageCensus(IReadOnlyList<CensusVariant> Variants, IReadOnlyList<RegistryEntry> UnmatchedEntries);

/// <summary>A seed (no <see cref="Boundary"/>) or one boundary variant, as the line both judges see.</summary>
internal sealed class CensusVariant(string id, SchemaBoundary? boundary, JsonObject envelope, IReadOnlyList<string> notes)
{
    public string Id { get; } = id;

    public SchemaBoundary? Boundary { get; } = boundary;

    public IReadOnlyList<string> Notes { get; } = notes;

    public string AgvId { get; } = envelope["agvId"]!.GetValue<string>();

    /// <summary>
    /// The envelope rebuilt by <see cref="WireToGateProtocolSerializer.Create"/>: the examples carry release
    /// 0.1.0 and an all-zero manifest hash, so only the payload and the message's own identity are kept.
    /// </summary>
    public string Line { get; } = WireToGateProtocolSerializer.Serialize(WireToGateProtocolSerializer.Create(
        envelope["messageType"]!.GetValue<string>(),
        envelope["messageId"]!.GetValue<string>(),
        envelope["correlationId"]?.GetValue<string>(),
        envelope["agvId"]!.GetValue<string>(),
        envelope["sessionGeneration"]?.GetValue<long>(),
        DateTimeOffset.Parse(envelope["sentAt"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        envelope["payload"]!));

    public IReadOnlyList<string> SchemaErrors { get; set; } = [];
}

internal sealed class CensusRegistry(IReadOnlyList<RegistryEntry> entries)
{
    public static CensusRegistry Load(string path) =>
        new(JsonNode.Parse(File.ReadAllBytes(path))!.AsArray().Select(node => new RegistryEntry(node!.AsObject())).ToArray());

    public IEnumerable<string> MessageTypes => entries.Select(entry => entry.MessageType).Distinct(StringComparer.Ordinal);

    public IEnumerable<RegistryEntry> Of(string messageType) => entries.Where(entry => entry.MessageType == messageType);

    public IEnumerable<RegistryEntry> Matching(string messageType, SchemaBoundary boundary) =>
        entries.Where(entry => entry.Matches(messageType, boundary));

    public RegistryEntry? StricterOnFile(string messageType, SchemaBoundary boundary) =>
        Matching(messageType, boundary).FirstOrDefault(entry => entry.Disposition is "intentional" or "issue");
}

/// <summary>
/// One entry of inbound-stricter-than-schema.json. It matches variants by message type and pointer, and
/// optionally by <c>keywords</c> and an exact <c>value</c>. A companion's <c>set</c> maps concrete pointers
/// to a literal, <c>{"lengthOf": pointer}</c> (that array's length) or <c>{"itemsCountFrom": pointer}</c>
/// (this array resized to that integer).
/// </summary>
internal sealed class RegistryEntry
{
    private readonly JsonObject _source;
    private readonly string[]? _keywords;
    private readonly JsonObject? _set;

    public RegistryEntry(JsonObject source)
    {
        _source = source;
        MessageType = Required("messageType");
        Pointer = Required("pointer");
        Disposition = Required("disposition");
        _keywords = source["keywords"]?.AsArray().Select(keyword => keyword!.GetValue<string>()).ToArray();
        _set = source["set"] as JsonObject;
        bool valid = Disposition switch
        {
            "companion" => _set is { Count: > 0 } && Has("basis"),
            "intentional" => _set is null && Has("basis"),
            "issue" => _set is null && source["issue"]?.GetValue<string>().StartsWith("https://github.com/", StringComparison.Ordinal) == true,
            _ => false
        };
        if (!valid || !Pointer.StartsWith("/payload", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "An inbound-stricter-than-schema.json entry is a companion (set + basis), intentional (basis) or issue " +
                "(a GitHub issue URL), with a pointer under /payload: " + source.ToJsonString());
        }

        string Required(string name) => source[name]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Entry without {name}: {source.ToJsonString()}");
        bool Has(string name) => source[name]?.GetValue<string>() is { Length: > 0 };
    }

    public string MessageType { get; }

    public string Pointer { get; }

    public string Disposition { get; }

    public string SetText => _set?.ToJsonString() ?? string.Empty;

    public bool Matches(string messageType, SchemaBoundary boundary) =>
        MessageType == messageType
        && Pointer == boundary.Pointer
        && (_keywords is null || _keywords.Contains(boundary.Keyword, StringComparer.Ordinal))
        && (!_source.ContainsKey("value") || JsonNode.DeepEquals(_source["value"], boundary.Value));

    public void ApplyTo(JsonObject envelope, BoundaryVariantGenerator generator)
    {
        foreach ((string pointer, JsonNode? directive) in _set!)
        {
            JsonNode? value = directive switch
            {
                JsonObject { Count: 1 } length when length["lengthOf"] is JsonValue of =>
                    JsonValue.Create(JsonPointer.Get(envelope, of.GetValue<string>())!.AsArray().Count),
                JsonObject { Count: 1 } resize when resize["itemsCountFrom"] is JsonValue from =>
                    generator.BuildArray(
                        JsonPointer.Get(envelope, pointer) as JsonArray,
                        JsonPointer.Get(envelope, from.GetValue<string>())!.GetValue<int>(),
                        generator.SchemaAt(pointer)),
                JsonObject => throw new InvalidDataException("Unknown companion directive " + directive.ToJsonString()),
                _ => directive?.DeepClone()
            };
            JsonPointer.Set(envelope, pointer, value);
        }
    }

    public override string ToString() => _source.ToJsonString();
}
