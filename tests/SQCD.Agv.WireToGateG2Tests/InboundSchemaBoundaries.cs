using System.Text.Json.Nodes;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The vendored JSON Schemas of the pinned protocol release, read with nothing but the framework's own
/// System.Text.Json -- the validator's library must stay out of the test host (see
/// <see cref="OutboundSchemaConformance"/>). Only what the inbound schema boundary census needs is
/// understood: absolute <c>$ref</c> and <c>anyOf</c>/<c>oneOf</c>. Anything else that would move a
/// boundary throws instead of being guessed at.
/// </summary>
internal sealed class ProtocolSchemas
{
    private const string BaseUri = "https://schemas.8005-agv.local/wire-to-gate/v1/";
    private readonly Dictionary<string, JsonObject> _documents;

    private ProtocolSchemas(Dictionary<string, JsonObject> documents) => _documents = documents;

    public static ProtocolSchemas Load(string schemaRoot)
    {
        Dictionary<string, JsonObject> documents = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(schemaRoot, "*.json", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(schemaRoot, file).Replace('\\', '/');
            documents[BaseUri + relative] = JsonNode.Parse(File.ReadAllBytes(file))!.AsObject();
        }
        return new ProtocolSchemas(documents);
    }

    /// <param name="relativePath">Relative to <c>schemas/</c>, e.g. <c>messages/DurableAck.schema.json</c>.</param>
    public JsonObject Document(string relativePath) => _documents[BaseUri + relativePath];

    public JsonObject Resolve(JsonObject schema)
    {
        while (schema["$ref"] is JsonValue reference)
        {
            if (schema.Count != 1)
            {
                throw new NotSupportedException("$ref with sibling keywords is not understood: " + schema.ToJsonString());
            }
            string uri = reference.GetValue<string>();
            int hash = uri.IndexOf('#', StringComparison.Ordinal);
            if (!_documents.TryGetValue(hash < 0 ? uri : uri[..hash], out JsonObject? document))
            {
                throw new NotSupportedException("Only absolute $ref into the vendored schemas is understood: " + uri);
            }
            JsonNode node = document;
            if (hash >= 0)
            {
                foreach (string segment in uri[(hash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    node = node[Unescape(segment)] ?? throw new InvalidDataException("Dangling $ref " + uri);
                }
            }
            schema = node.AsObject();
        }
        return schema;
    }

    /// <summary>The resolved branches of <c>anyOf</c>/<c>oneOf</c>, or the schema itself when it has neither.</summary>
    public IReadOnlyList<JsonObject> Branches(JsonObject schema)
    {
        schema = Resolve(schema);
        JsonArray? branches = schema["anyOf"] as JsonArray ?? schema["oneOf"] as JsonArray;
        return branches is null ? [schema] : branches.Select(branch => Resolve(branch!.AsObject())).ToArray();
    }

    /// <summary>Nullable in either spelling: an <c>anyOf</c> with a null branch, or a <c>type</c> list naming null.</summary>
    public bool IsNullable(JsonObject schema) =>
        Branches(schema).Any(branch => IsNullType(branch)
            || branch["type"] is JsonArray types && types.Any(type => type?.GetValue<string>() == "null"));

    /// <summary>The shape a non-null value of this schema takes.</summary>
    public JsonObject NonNull(JsonObject schema) =>
        Branches(schema).FirstOrDefault(branch => !IsNullType(branch))
            ?? throw new InvalidDataException("A schema that only admits null has no shape: " + schema.ToJsonString());

    public static bool IsNullType(JsonObject schema) =>
        schema["type"] is JsonValue type && type.GetValue<string>() == "null";

    public static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string Unescape(string segment) => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}

/// <summary>
/// One boundary of a message's payload, located by a JSON Pointer in which <c>*</c> stands for every item
/// of an array. <see cref="Keyword"/> names the schema keyword the boundary comes from.
/// </summary>
internal sealed record SchemaBoundary(string Pointer, string Keyword, JsonNode? Value)
{
    /// <summary>The variant id: <c>&lt;MessageType&gt; &lt;JSON Pointer&gt; &lt;keyword&gt;=&lt;value&gt;</c>.</summary>
    public string Describe(string messageType) => $"{messageType} {Pointer} {Keyword}={ValueText}";

    private string ValueText => Value switch
    {
        null => "null",
        JsonValue value when value.TryGetValue(out string? text) => text,
        _ => Value.ToJsonString()
    };
}

/// <summary>
/// Derives a message's boundaries from its schema, following the table in 8005-agv-onboard-hmi#38's
/// decision: an array's <c>minItems</c> and <c>maxItems</c> (3 items when there is no <c>maxItems</c>),
/// each <c>enum</c> member, <c>null</c> where the schema admits it, both ends of a numeric range, and the
/// absence of a property that is not <c>required</c>. A <c>const</c> is the contract, not a boundary. String
/// <c>pattern</c>/<c>format</c>/<c>minLength</c> are deliberately not boundaries (the decision, section 2).
/// </summary>
internal static class SchemaBoundaryEnumerator
{
    public const int UnboundedArrayLength = 3;

    public static IReadOnlyList<SchemaBoundary> Enumerate(ProtocolSchemas schemas, JsonObject messageSchema)
    {
        List<SchemaBoundary> boundaries = [];
        JsonObject payload = messageSchema["properties"]?["payload"]?.AsObject()
            ?? throw new InvalidDataException("Message schema without a payload property.");
        Walk(schemas, payload, "/payload", boundaries);
        return boundaries;
    }

    private static void Walk(ProtocolSchemas schemas, JsonObject schema, string pointer, List<SchemaBoundary> into)
    {
        if (schemas.IsNullable(schema))
        {
            into.Add(new SchemaBoundary(pointer, "type", null));
        }
        foreach (JsonObject shape in schemas.Branches(schema).Where(branch => !ProtocolSchemas.IsNullType(branch)))
        {
            if (shape.ContainsKey("const"))
            {
                continue;
            }
            if (shape["enum"] is JsonArray members)
            {
                foreach (JsonNode? member in members)
                {
                    into.Add(new SchemaBoundary(pointer, "enum", member?.DeepClone()));
                }
            }
            foreach (string keyword in (string[])["minimum", "maximum"])
            {
                if (shape[keyword] is JsonValue bound)
                {
                    into.Add(new SchemaBoundary(pointer, keyword, bound.DeepClone()));
                }
            }
            if (shape["properties"] is JsonObject properties)
            {
                HashSet<string> required = shape["required"] is JsonArray names
                    ? names.Select(name => name!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
                    : [];
                foreach ((string name, JsonNode? property) in properties)
                {
                    string child = pointer + "/" + ProtocolSchemas.Escape(name);
                    if (!required.Contains(name))
                    {
                        into.Add(new SchemaBoundary(child, "required", JsonValue.Create("absent")));
                    }
                    Walk(schemas, property!.AsObject(), child, into);
                }
            }
            if (shape["items"] is JsonObject items)
            {
                into.Add(new SchemaBoundary(pointer, "minItems", JsonValue.Create(shape["minItems"]?.GetValue<int>() ?? 0)));
                into.Add(shape["maxItems"] is JsonValue maxItems
                    ? new SchemaBoundary(pointer, "maxItems", maxItems.DeepClone())
                    : new SchemaBoundary(pointer, "items", JsonValue.Create(UnboundedArrayLength)));
                Walk(schemas, items, pointer + "/*", into);
            }
        }
    }
}

/// <summary>
/// Builds one variant per boundary from a seed envelope: a copy with exactly that one place changed.
/// Growing an array is the one change that has to touch more than the place itself, and it honours what
/// the contract says about items the validator cannot check: <c>uniqueItems</c>, <c>x-sortedBy</c>,
/// <c>x-sortedAscending</c>. A boundary under an empty array or a null object is reached by first putting
/// one item or object there; the variant's notes say so. Whatever this builds, the schema validator judges
/// before the vehicle ever sees it.
/// </summary>
internal sealed class BoundaryVariantGenerator(ProtocolSchemas schemas, JsonObject messageSchema)
{
    public JsonObject Apply(JsonObject seedEnvelope, SchemaBoundary boundary, List<string> notes)
    {
        JsonObject envelope = seedEnvelope.DeepClone().AsObject();
        string[] segments = boundary.Pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Descend(envelope, Resolve(messageSchema), segments, 0, boundary, notes, string.Empty);
        return envelope;
    }

    /// <summary>Replaces the value at a concrete pointer (no <c>*</c>).</summary>
    public static void Set(JsonObject envelope, string pointer, JsonNode? value)
    {
        string[] segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        JsonNode container = envelope;
        foreach (string segment in segments[..^1])
        {
            container = (container is JsonArray array ? array[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)] : container[segment])
                ?? throw new InvalidDataException("Nothing at " + pointer);
        }
        string last = segments[^1];
        if (container is JsonArray items)
        {
            items[int.Parse(last, System.Globalization.CultureInfo.InvariantCulture)] = value;
        }
        else
        {
            container[last] = value;
        }
    }

    public static JsonNode? Get(JsonObject envelope, string pointer)
    {
        JsonNode? node = envelope;
        foreach (string segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node is JsonArray array ? array[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)] : node?[segment];
        }
        return node;
    }

    /// <summary>The non-null shape of the schema at a pointer; <c>*</c> and array indices both step into <c>items</c>.</summary>
    public JsonObject SchemaAt(string pointer)
    {
        JsonObject shape = Resolve(messageSchema);
        foreach (string segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            shape = shape["properties"]?[segment] is JsonObject property
                ? schemas.NonNull(property)
                : shape["items"] is JsonObject items
                    ? schemas.NonNull(items)
                    : throw new InvalidDataException($"No schema at {pointer} (stuck at '{segment}').");
        }
        return shape;
    }

    /// <summary>An array of exactly <paramref name="count"/> items, keeping the existing ones first.</summary>
    public JsonArray BuildArray(JsonArray? existing, int count, JsonObject arraySchema)
    {
        JsonObject itemSchema = schemas.Resolve(arraySchema["items"]!.AsObject());
        List<JsonNode?> items = existing?.Take(count).Select(item => item?.DeepClone()).ToList() ?? [];
        JsonNode? template = existing is { Count: > 0 } ? existing[0] : null;
        for (int ordinal = items.Count; items.Count < count; ordinal++)
        {
            items.Add(DistinctItem(template, itemSchema, arraySchema, items, ordinal));
        }
        if (arraySchema["x-sortedBy"]?.GetValue<string>() is { } sortedBy)
        {
            items.Sort((left, right) => CompareScalars(left?[sortedBy], right?[sortedBy]));
        }
        else if (arraySchema["x-sortedAscending"]?.GetValue<bool>() == true)
        {
            items.Sort(CompareScalars);
        }
        return new JsonArray([.. items]);
    }

    /// <summary>A minimal value of a schema: const, first enum member, first example, or the least of its type.</summary>
    public JsonNode? Synthesize(JsonObject schema)
    {
        JsonObject shape = schemas.NonNull(schema);
        if (shape["const"] is { } constant)
        {
            return constant.DeepClone();
        }
        if (shape["enum"] is JsonArray { Count: > 0 } members)
        {
            return members[0]?.DeepClone();
        }
        if (shape["examples"] is JsonArray { Count: > 0 } examples)
        {
            return examples[0]?.DeepClone();
        }
        switch (TypeOf(shape))
        {
            case "object":
                JsonObject value = [];
                foreach ((string name, JsonNode? property) in shape["properties"]?.AsObject() ?? [])
                {
                    value[name] = Synthesize(property!.AsObject());
                }
                return value;
            case "array":
                return BuildArray(null, shape["minItems"]?.GetValue<int>() ?? 0, shape);
            case "integer" or "number":
                return JsonValue.Create(shape["minimum"]?.GetValue<long>() ?? 0);
            case "boolean":
                return JsonValue.Create(false);
            case "string":
                return JsonValue.Create(IsUuid(shape) ? Uuid(0) : new string('x', Math.Max(1, shape["minLength"]?.GetValue<int>() ?? 1)));
            default:
                throw new NotSupportedException("Cannot synthesize a value of " + shape.ToJsonString());
        }
    }

    private void Descend(JsonNode container, JsonObject containerShape, string[] segments, int index, SchemaBoundary boundary, List<string> notes, string path)
    {
        string segment = segments[index];
        bool last = index == segments.Length - 1;
        if (segment == "*")
        {
            JsonArray array = container.AsArray();
            JsonObject itemSchema = containerShape["items"]?.AsObject()
                ?? throw new InvalidDataException($"'*' on a non-array at {path}.");
            if (array.Count == 0)
            {
                array.Add(Synthesize(itemSchema));
                notes.Add($"{path} was empty: one item added to reach {boundary.Pointer}");
            }
            for (int item = 0; item < array.Count; item++)
            {
                if (last)
                {
                    array[item] = Replacement(array[item], itemSchema, boundary);
                }
                else
                {
                    DescendInto(array[item], value => array[item] = value, itemSchema, segments, index, boundary, notes, path + "/" + item);
                }
            }
            return;
        }

        JsonObject target = container.AsObject();
        JsonObject propertySchema = containerShape["properties"]?[segment]?.AsObject()
            ?? throw new InvalidDataException($"No property '{segment}' at {path}.");
        if (!last)
        {
            DescendInto(target[segment], value => target[segment] = value, propertySchema, segments, index, boundary, notes, path + "/" + segment);
        }
        else if (boundary.Keyword == "required")
        {
            target.Remove(segment);
        }
        else
        {
            target[segment] = Replacement(target[segment], propertySchema, boundary);
        }
    }

    private void DescendInto(JsonNode? child, Action<JsonNode?> replace, JsonObject childSchema, string[] segments, int index, SchemaBoundary boundary, List<string> notes, string path)
    {
        if (child is null)
        {
            child = Synthesize(childSchema);
            replace(child);
            notes.Add($"{path} was null: a value put there to reach {boundary.Pointer}");
        }
        Descend(child!, schemas.NonNull(childSchema), segments, index + 1, boundary, notes, path);
    }

    private JsonNode? Replacement(JsonNode? current, JsonObject schema, SchemaBoundary boundary) => boundary.Keyword switch
    {
        "type" => null,
        "enum" or "minimum" or "maximum" => boundary.Value?.DeepClone(),
        "minItems" or "maxItems" or "items" => BuildArray(current as JsonArray, boundary.Value!.GetValue<int>(), schemas.NonNull(schema)),
        _ => throw new NotSupportedException("Boundary keyword " + boundary.Keyword)
    };

    private JsonNode? DistinctItem(JsonNode? template, JsonObject itemSchema, JsonObject arraySchema, List<JsonNode?> existing, int ordinal)
    {
        JsonObject shape = schemas.NonNull(itemSchema);
        if (shape["properties"] is not JsonObject properties)
        {
            return DistinctScalar(shape, existing, ordinal, template);
        }

        JsonObject item = (template?.DeepClone() ?? Synthesize(shape))!.AsObject();
        string? sortedBy = arraySchema["x-sortedBy"]?.GetValue<string>();
        List<(string Name, JsonObject Shape)> fields = properties
            .Select(property => (property.Key, schemas.NonNull(property.Value!.AsObject())))
            .ToList();
        foreach ((string name, JsonObject fieldShape) in fields)
        {
            // Identities differ between items, and so does the key the contract sorts them by.
            if (name == sortedBy)
            {
                item[name] = DistinctScalar(fieldShape, existing.Select(other => other?[name]).ToList(), ordinal, null);
            }
            else if (IsUuid(fieldShape))
            {
                item[name] = JsonValue.Create(Uuid(ordinal));
            }
        }
        if (arraySchema["uniqueItems"]?.GetValue<bool>() == true)
        {
            foreach ((string name, JsonObject fieldShape) in fields)
            {
                if (!existing.Any(other => JsonNode.DeepEquals(other, item)))
                {
                    break;
                }
                if (fieldShape["enum"] is JsonArray || TypeOf(fieldShape) == "string" && fieldShape["pattern"] is null && fieldShape["format"] is null)
                {
                    List<JsonNode?> taken = existing.Where(other => other is JsonObject).Select(other => other![name]).ToList();
                    item[name] = DistinctScalar(fieldShape, taken, ordinal, item[name]);
                }
            }
        }
        return item;
    }

    private static JsonNode? DistinctScalar(JsonObject shape, List<JsonNode?> taken, int ordinal, JsonNode? template)
    {
        bool Free(JsonNode? candidate) => !taken.Any(other => JsonNode.DeepEquals(other, candidate));
        if (shape["enum"] is JsonArray members)
        {
            return members.FirstOrDefault(Free)?.DeepClone()
                ?? throw new InvalidDataException("No unused enum member left in " + shape.ToJsonString());
        }
        switch (TypeOf(shape))
        {
            case "integer":
                long minimum = shape["minimum"]?.GetValue<long>() ?? 0;
                long maximum = shape["maximum"]?.GetValue<long>() ?? minimum + 1_000;
                for (long candidate = minimum; candidate <= maximum; candidate++)
                {
                    if (Free(JsonValue.Create(candidate)))
                    {
                        return JsonValue.Create(candidate);
                    }
                }
                throw new InvalidDataException("No unused integer left in " + shape.ToJsonString());
            case "string" when IsUuid(shape):
                for (int candidate = ordinal; ; candidate++)
                {
                    if (Free(JsonValue.Create(Uuid(candidate))))
                    {
                        return JsonValue.Create(Uuid(candidate));
                    }
                }
            case "string":
                string stem = template is JsonValue text && text.TryGetValue(out string? seed) ? seed : "item";
                for (int candidate = ordinal + 1; ; candidate++)
                {
                    if (Free(JsonValue.Create($"{stem}-{candidate}")))
                    {
                        return JsonValue.Create($"{stem}-{candidate}");
                    }
                }
            default:
                throw new NotSupportedException("Cannot make a distinct value of " + shape.ToJsonString());
        }
    }

    private JsonObject Resolve(JsonObject schema) => schemas.Resolve(schema);

    private static string? TypeOf(JsonObject shape) => shape["type"] switch
    {
        JsonValue type => type.GetValue<string>(),
        JsonArray types => types.Select(type => type?.GetValue<string>()).FirstOrDefault(type => type != "null"),
        _ => shape["properties"] is not null ? "object" : shape["items"] is not null ? "array" : null
    };

    private static bool IsUuid(JsonObject shape) => shape["format"]?.GetValue<string>() == "uuid";

    // Distinct from every seed id (the examples count up from ...0001), still RFC 4122 version 4 shaped.
    private static string Uuid(int ordinal) => $"00000000-0000-4000-8000-{0xC0DE00000000L + ordinal:x12}";

    private static int CompareScalars(JsonNode? left, JsonNode? right) =>
        left is JsonValue l && right is JsonValue r && l.TryGetValue(out long a) && r.TryGetValue(out long b)
            ? a.CompareTo(b)
            : string.CompareOrdinal(left?.ToJsonString(), right?.ToJsonString());
}
