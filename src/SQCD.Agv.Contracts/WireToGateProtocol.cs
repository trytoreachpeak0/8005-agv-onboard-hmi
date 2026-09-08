using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SQCD.Agv.Contracts;

/// <summary>
/// Immutable identity of the approved WIRE_TO_GATE protocol release.
/// </summary>
public static class WireToGateRelease
{
    public const int ProtocolVersion = 3;
    public const string ProfileId = "WIRE_TO_GATE_MVP";
    public const string ReleaseVersion = "0.3.0";
    public const string Repository = "8005-agv-protocol";
    public const string Tag = "protocol-v0.3.0";
    public const string Commit = "345c53c58517968192c87c3e7777ed08ddb48726";
    public const string ManifestSha256 = "b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138";
    public const string SchemaBundleSha256 = "68bfd531c4b9c08bc80f6d9c5a67264891efa200acdb154eb18e1d083bf4ed98";
    public const string VectorsSha256 = "bd272b63a1d0663d61c4a38d6e8633d7e7d4f7b561a7915c3df51c7a93bd4576";

    public static ProtocolReleaseIdentity Identity { get; } = new(
        Repository,
        ReleaseVersion,
        Tag,
        Commit,
        ProtocolVersion,
        ProfileId,
        ManifestSha256,
        SchemaBundleSha256,
        VectorsSha256);
}

public sealed record ProtocolReleaseIdentity(
    string Repository,
    string ReleaseVersion,
    string Tag,
    string Commit,
    int ProtocolVersion,
    string ProfileId,
    string ManifestSha256,
    string SchemaBundleSha256,
    string VectorsSha256);

public sealed record WireToGateEnvelope(
    int ProtocolVersion,
    string ProfileId,
    string ProtocolReleaseVersion,
    string ProtocolReleaseManifestSha256,
    string MessageType,
    string MessageId,
    string? CorrelationId,
    string AgvId,
    long? SessionGeneration,
    DateTimeOffset SentAt,
    JsonElement Payload);

/// <summary>
/// Minimal strict serializer for the immutable protocol-v0.3.0 envelope.
/// Message payloads remain explicit at their call sites so that later slices can be
/// generated from the tagged JSON Schemas without changing the transport contract.
/// </summary>
public static class WireToGateProtocolSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        // The protocol envelopes and payload records are closed schemas.  Silently
        // ignoring a newly-added or misspelled field would make the two ends appear
        // compatible while making different safety decisions.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static WireToGateEnvelope Create(
        string messageType,
        string messageId,
        string? correlationId,
        string agvId,
        long? sessionGeneration,
        DateTimeOffset sentAt,
        object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        RequireUuid(messageId, nameof(messageId));
        if (correlationId is not null)
        {
            RequireUuid(correlationId, nameof(correlationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        if (sessionGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionGeneration));
        }

        return new WireToGateEnvelope(
            WireToGateRelease.ProtocolVersion,
            WireToGateRelease.ProfileId,
            WireToGateRelease.ReleaseVersion,
            WireToGateRelease.ManifestSha256,
            messageType,
            messageId,
            correlationId,
            agvId,
            sessionGeneration,
            sentAt,
            JsonSerializer.SerializeToElement(payload, SerializerOptions));
    }

    public static string Serialize(WireToGateEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, SerializerOptions);

    public static string SerializeLine(WireToGateEnvelope envelope) => Serialize(envelope) + "\n";

    /// <summary>
    /// Rebinds a pending durable message to the active connection generation while
    /// preserving its message identity, correlation and raw business payload.
    /// Session generation is transport/session metadata and is intentionally not
    /// allowed to remain stale across reconnects.
    /// </summary>
    public static WireToGateEnvelope RebindSessionGeneration(
        WireToGateEnvelope envelope,
        long sessionGeneration)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentOutOfRangeException.ThrowIfNegative(sessionGeneration);

        return envelope with { SessionGeneration = sessionGeneration };
    }

    public static WireToGateEnvelope DeserializeAndValidate(
        string line,
        string expectedAgvId,
        long? expectedSessionGeneration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAgvId);

        WireToGateEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<WireToGateEnvelope>(line, SerializerOptions)
                ?? throw new InvalidDataException("WIRE_TO_GATE消息内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("WIRE_TO_GATE消息不是有效JSON。", exception);
        }

        if (envelope.ProtocolVersion != WireToGateRelease.ProtocolVersion
            || !string.Equals(envelope.ProfileId, WireToGateRelease.ProfileId, StringComparison.Ordinal)
            || !string.Equals(envelope.ProtocolReleaseVersion, WireToGateRelease.ReleaseVersion, StringComparison.Ordinal)
            || !string.Equals(
                envelope.ProtocolReleaseManifestSha256,
                WireToGateRelease.ManifestSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("PROTOCOL_RELEASE_IDENTITY_MISMATCH");
        }

        if (!string.Equals(envelope.AgvId, expectedAgvId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("AGV_ID_MISMATCH");
        }

        RequireUuid(envelope.MessageId, nameof(envelope.MessageId));
        if (envelope.CorrelationId is not null)
        {
            RequireUuid(envelope.CorrelationId, nameof(envelope.CorrelationId));
        }

        if (string.IsNullOrWhiteSpace(envelope.MessageType)
            || envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("PROTOCOL_ENVELOPE_INVALID");
        }

        if (envelope.SentAt == default)
        {
            throw new InvalidDataException("PROTOCOL_ENVELOPE_INVALID");
        }

        if (expectedSessionGeneration.HasValue
            && envelope.SessionGeneration != expectedSessionGeneration)
        {
            throw new InvalidDataException("STALE_SESSION_GENERATION");
        }

        return envelope;
    }

    public static void RequireMessage(
        WireToGateEnvelope envelope,
        string expectedMessageType,
        string? expectedCorrelationId = null)
    {
        if (!string.Equals(envelope.MessageType, expectedMessageType, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"HANDSHAKE_SEQUENCE_INVALID：期望{expectedMessageType}，实际{envelope.MessageType}。");
        }

        if (expectedCorrelationId is not null
            && !string.Equals(envelope.CorrelationId, expectedCorrelationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("CORRELATION_INVALID");
        }
    }

    public static T DeserializePayload<T>(WireToGateEnvelope envelope) =>
        envelope.Payload.Deserialize<T>(SerializerOptions)
        ?? throw new InvalidDataException($"{envelope.MessageType}的payload为空或格式错误。");

    public static void RequireExactReleaseIdentity(ProtocolReleaseIdentity identity)
    {
        if (identity != WireToGateRelease.Identity)
        {
            throw new InvalidDataException("PROTOCOL_RELEASE_IDENTITY_MISMATCH");
        }
    }

    public static string ComputeContentSha256(WireToGateEnvelope envelope) =>
        ComputeSha256(Encoding.UTF8.GetBytes(Serialize(envelope)));

    /// <summary>
    /// Computes the semantic identity used to compare revisions of a server-owned
    /// snapshot. Transport fields such as messageId, sentAt and sessionGeneration
    /// are deliberately excluded. Object properties are sorted recursively so
    /// harmless JSON property ordering differences do not create a false conflict.
    /// </summary>
    public static string ComputePayloadContentSha256(WireToGateEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return ComputePayloadContentSha256(envelope.Payload);
    }

    public static string ComputePayloadContentSha256(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        using JsonDocument document = JsonDocument.Parse(payloadJson);
        return ComputePayloadContentSha256(document.RootElement);
    }

    public static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ComputePayloadContentSha256(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("PROTOCOL_PAYLOAD_INVALID");
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            WriteCanonicalJson(writer, payload);
        }

        return ComputeSha256(stream.ToArray());
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element
                    .EnumerateObject()
                    .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("PROTOCOL_PAYLOAD_INVALID");
        }
    }

    private static void RequireUuid(string value, string parameterName)
    {
        if (!Guid.TryParseExact(value, "D", out _))
        {
            throw new InvalidDataException($"{parameterName}必须是小写或大写均可解析的标准UUID。 ");
        }
    }
}
