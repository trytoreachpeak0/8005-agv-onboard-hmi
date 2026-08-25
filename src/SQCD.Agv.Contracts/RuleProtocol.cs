using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SQCD.Agv.Contracts;

/// <summary>
/// 车载端和 RuleMock 共同使用的“通信协议合同”
/// RuleMock 按这个文件理解车载端消息,TcpJsonRuleGateway 按这个文件生成和解析消息
/// </summary>
public static class RuleMessageTypes
{
    //这些字符串会真实出现在 TCP JSON 中
    public const string Hello = "hello";
    public const string HelloAck = "hello.ack";
    public const string Heartbeat = "heartbeat";
    public const string HeartbeatAck = "heartbeat.ack";
    public const string VisitStarted = "visit.started";
    public const string VisitEnded = "visit.ended";
    public const string ScanVerifyRequest = "scan.verify.request";
    public const string ScanVerifyResponse = "scan.verify.response";
    public const string OperationResult = "slot.operation.result";
    public const string OperationResultAck = "slot.operation.result.ack";
    public const string ProtocolError = "protocol.error";
}

/// <summary>
/// 每条消息的统一信封 RuleEnvelope
/// </summary>
/// <param name="Version"></param>
/// <param name="Type"></param>
/// <param name="MessageId"></param>
/// <param name="CorrelationId"></param>
/// <param name="AgvId"></param>
/// <param name="VisitId"></param>
/// <param name="Timestamp"></param>
/// <param name="Payload"></param>
public sealed record RuleEnvelope(
    string Version,
    string Type,
    string MessageId,
    string? CorrelationId,
    string AgvId,
    string? VisitId,
    DateTimeOffset Timestamp,
    JsonElement Payload);

// 握手、心跳、到站 Payload
public sealed record HelloPayload(string OnboardInstanceId, string AppVersion, string ProtocolVersion);

public sealed record HelloAckPayload(bool Accepted, string? ErrorCode, string? ErrorMessage);

public sealed record HeartbeatPayload(bool IoOnline, string? ActiveOperationId, bool DeparturePermitted);

public sealed record VisitStartedPayload(
    string StationId,
    string StationName,
    bool AllowOperation,
    DateTimeOffset ExpiresAt);

public sealed record VisitEndedPayload(string? Reason);

// 扫码请求和响应
public sealed record ScanVerifyRequestPayload(string Sublot, string InputMethod);

public sealed record ScanVerifyResponsePayload(
    bool Accepted,
    string? OperationId,
    string? TaskId,
    string? Sublot,
    int? SlotIndex,
    string? OperationType,
    bool? ExpectedCargoAfter,
    string? ErrorCode,
    string? ErrorMessage);

// 结果上报和协议错误
public sealed record OperationResultPayload(
    string OperationId,
    string TaskId,
    string Sublot,
    int SlotIndex,
    string OperationType,
    bool Success,
    string? FailureCode,
    string? FailureStage,
    int? UnlockDoRaw,
    int? LockFeedbackRaw,
    int? LightCurtainRaw,
    bool DeparturePermitted,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record OperationResultAckPayload(string OperationId, bool Accepted, string? ErrorCode, string? ErrorMessage);

public sealed record ProtocolErrorPayload(string ErrorCode, string ErrorMessage);

// 序列化器 RuleProtocolSerializer, 它负责两件事，C# 对象 → 一行 JSON         一行 JSON → C# 对象
public static class RuleProtocolSerializer
{
    public const string CurrentVersion = "1.0";
    public const int MaxMessageBytes = 65_536;

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static RuleEnvelope Create<TPayload>(
        string type,
        string agvId,
        string? visitId,
        TPayload payload,
        string? correlationId = null,
        string? messageId = null,
        DateTimeOffset? timestamp = null)
    {
        return new RuleEnvelope(
            CurrentVersion,
            type,
            messageId ?? CreateMessageId(),
            correlationId,
            agvId,
            visitId,
            timestamp ?? DateTimeOffset.Now,
            JsonSerializer.SerializeToElement(payload, Options));
    }

    public static string SerializeLine(RuleEnvelope envelope)
    {
        string json = JsonSerializer.Serialize(envelope, Options);
        if (Encoding.UTF8.GetByteCount(json) > MaxMessageBytes)
        {
            throw new InvalidDataException($"协议消息超过{MaxMessageBytes}字节限制。");
        }

        return json;
    }

    public static RuleEnvelope DeserializeLine(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaxMessageBytes)
        {
            throw new InvalidDataException($"协议消息超过{MaxMessageBytes}字节限制。");
        }

        RuleEnvelope? envelope = JsonSerializer.Deserialize<RuleEnvelope>(line, Options);
        if (envelope is null
            || string.IsNullOrWhiteSpace(envelope.Version)
            || string.IsNullOrWhiteSpace(envelope.Type)
            || string.IsNullOrWhiteSpace(envelope.MessageId)
            || string.IsNullOrWhiteSpace(envelope.AgvId))
        {
            throw new InvalidDataException("协议消息缺少公共信封必填字段。");
        }

        return envelope;
    }

    public static TPayload DeserializePayload<TPayload>(RuleEnvelope envelope)
    {
        TPayload? payload = envelope.Payload.Deserialize<TPayload>(Options);
        return payload ?? throw new InvalidDataException($"消息{envelope.Type}的payload无效。");
    }

    public static string CreateMessageId() => $"MSG-{Guid.NewGuid():N}";

    private static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
    }
}
