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
    public const int ProtocolVersion = 1;
    public const string ProfileId = "WIRE_TO_GATE_MVP";
    public const string ReleaseVersion = "0.1.1";
    public const string Repository = "8005-agv-protocol";
    public const string Tag = "protocol-v0.1.1";
    public const string Commit = "1531489e42e328f28bfe0c51ed3f8c56e5ce0279";
    public const string ManifestSha256 = "a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f";
    public const string SchemaBundleSha256 = "e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c";
    public const string VectorsSha256 = "fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e";

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
/// Minimal strict serializer for the immutable protocol-v0.1.1 envelope.
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

    public static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void RequireUuid(string value, string parameterName)
    {
        if (!Guid.TryParseExact(value, "D", out _))
        {
            throw new InvalidDataException($"{parameterName}必须是小写或大写均可解析的标准UUID。 ");
        }
    }
}
