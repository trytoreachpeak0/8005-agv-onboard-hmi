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
/// <b>This names the <c>2.0.0</c> candidate, which is not a release.</b> Every value below is read
/// off <c>8005-agv-protocol</c> commit <c>86575456c847041515b7b75e8851a00e0d939804</c>, the frozen
/// head of <c>fp/v2-candidate</c> published by the candidate's own delivery ticket
/// (<c>8005-agv-program#96</c>). <see cref="Tag"/> names <c>protocol-v2.0.0</c>, a tag that does
/// <b>not</b> exist yet -- <c>8005-agv-program#97</c> creates it on this same commit -- so
/// <see cref="Tag"/> and <see cref="ApprovalStatus"/> have to be read together to be read
/// truthfully. <see cref="ApprovalStatus"/> is the field to read before treating this identity as
/// releasable, and it says <c>SUPERSEDING_CANDIDATE</c>.
/// </para>
/// <para>
/// <b><see cref="ProtocolVersion"/> stays 3 across two different releases, so never compare it
/// alone.</b> The integer only increases monotonically <i>within</i> one <c>profileId</c>:
/// <c>WIRE_TO_GATE_MVP 0.2.0</c> and <c>AGV_FULL_PRODUCT 1.0.0</c> were both 2, and
/// <c>WIRE_TO_GATE_MVP 0.3.0</c> and <c>AGV_FULL_PRODUCT 2.0.0</c> are both 3. Every identity
/// comparison -- the handshake's and
/// <see cref="WireToGateProtocolSerializer.DeserializeAndValidate"/>'s -- therefore compares the
/// whole <see cref="ProtocolReleaseIdentity"/>, and every log or evidence field that records a
/// protocol version writes the pair <c>(profileId, protocolVersion)</c>.
/// </para>
/// <para>
/// <b>These nine values have to match the control server's, and today they do not yet.</b>
/// <c>ProtocolCandidateIdentity</c> in <c>8005-agv-control-server</c> is the other copy, and the
/// handshake compares <c>commit</c>, <c>manifestSha256</c>, <c>profileId</c> and
/// <c>protocolVersion</c> and refuses the session on any difference. That end moves to the same
/// candidate in <c>8005-agv-control-server#84</c>; until it merges, this build and the control
/// server's integration branch are on different identities and will not complete a handshake.
/// Neither end copies the other: both read the candidate's published identity table.
/// </para>
/// <para>
/// <b>Evidence produced on this identity is development-grade.</b> A <c>ONBOARD_HMI_G2</c> run
/// against a <c>SUPERSEDING_CANDIDATE</c> is not the gate's formal evidence; the formal run happens
/// after the release, on <c>8005-agv-onboard-hmi#79</c>. The evidence recorded on
/// <c>protocol-v1.0.0</c> stops being current evidence the moment this constant moves.
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
    public const int ProtocolVersion = 3;
    public const string ProfileId = "AGV_FULL_PRODUCT";
    public const string ReleaseVersion = "2.0.0";
    public const string Repository = "8005-agv-protocol";
    public const string Tag = "protocol-v2.0.0";
    public const string Commit = "86575456c847041515b7b75e8851a00e0d939804";
    public const string ManifestSha256 = "4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7";
    public const string SchemaBundleSha256 = "9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221";
    public const string VectorsSha256 = "391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a";
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
/// Minimal strict serializer for the envelope of the protocol release named by
/// <see cref="WireToGateRelease"/> -- currently the 2.0.0 candidate.
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
