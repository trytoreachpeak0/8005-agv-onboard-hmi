using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SQCD.Agv.Contracts;

/// <summary>
/// The protocol release this onboard build is built against, mirrored as constants because the
/// onboard runtime does not read the protocol's files at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>This names the v2 candidate, and the candidate is not an approved release.</b> Every value
/// below is read off <c>8005-agv-protocol</c> commit
/// <c>16e2567a7033883f00fc999f7fa08f954dd13a26</c> (branch <c>fp/v2-candidate</c>), the candidate
/// G1 passed on 2026-09-12 once the single-owner release rule was carried over.
/// <see cref="ApprovalStatus"/> says <c>SUPERSEDING_CANDIDATE</c> rather than
/// <c>APPROVED_RELEASE</c> for exactly that reason, and it is the field to read before treating
/// this identity as releasable.
/// </para>
/// <para>
/// <b>These nine values are byte-for-byte the control server's.</b> <c>ProtocolCandidateIdentity</c>
/// in <c>8005-agv-control-server</c> carries the same ones, because the handshake compares
/// <c>commit</c>, <c>manifestSha256</c>, <c>profileId</c> and <c>protocolVersion</c> and refuses the
/// session on any difference. Neither end copied the other: both read the candidate.
/// </para>
/// <para>
/// <b><see cref="Tag"/> names a tag that does not exist yet.</b> Section 6.6 of the full-product
/// scope specification lists what a <c>ProtocolRelease</c> still needs, and item 6 is two product
/// owners' external attestation plus the annotated tag <c>protocol-v1.0.0</c>; neither has
/// happened. The constant still carries the name because <c>$defs/ProtocolReleaseIdentity</c>
/// requires <c>tag</c>, constrains it to <c>minLength: 1</c> and <c>^protocol-v</c>, and forbids
/// additional properties -- an empty string would put a schema-invalid value on
/// <c>SessionHello</c>, and neither end validates against the schemas at runtime, so nothing would
/// catch it. The pair is what tells the truth: this build targets <c>protocol-v1.0.0</c>, and that
/// release is not approved.
/// </para>
/// <para>
/// <see cref="ApprovalStatus"/> is deliberately <b>not</b> part of <see cref="Identity"/>: the
/// frozen <c>$defs/ProtocolReleaseIdentity</c> is <c>additionalProperties: false</c> over exactly
/// nine names, and approval status is not one of them. It is a fact about this build, not a field
/// of the wire identity.
/// </para>
/// </remarks>
public static class WireToGateRelease
{
    public const int ProtocolVersion = 2;
    public const string ProfileId = "AGV_FULL_PRODUCT";
    public const string ReleaseVersion = "1.0.0";
    public const string Repository = "8005-agv-protocol";
    public const string Tag = "protocol-v1.0.0";
    public const string Commit = "16e2567a7033883f00fc999f7fa08f954dd13a26";
    public const string ManifestSha256 = "25fd6689e8234b7d481874b408109cd27eb0f02fbb023225385d6642e9bfd3d0";
    public const string SchemaBundleSha256 = "225a83340eb5f27c4e6dfd7bf8aba8007cf787d29f1df860deaf0ba039baf3ff";
    public const string VectorsSha256 = "51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df";
    public const string ApprovalStatus = "SUPERSEDING_CANDIDATE";

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
/// Minimal strict serializer for the immutable protocol-v1.0.0 candidate envelope.
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
