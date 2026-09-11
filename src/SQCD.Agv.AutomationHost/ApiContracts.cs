using SQCD.Agv.Core;

namespace SQCD.Agv.AutomationHost;

public sealed record AutomationSublotSubmitRequest(
    string? RunId,
    string? CommandId,
    long? ExpectedRevision,
    string? Sublot);

public sealed record AutomationRecoveryRequest(
    string? RunId,
    string? CommandId,
    long? ExpectedRevision,
    string? Action,
    string? Reason);

public static class AutomationRecoveryStatus
{
    public const string Accepted = "ACCEPTED";
    public const string Rejected = "REJECTED";
    public const string InProgress = "IN_PROGRESS";
}

public sealed record AutomationRecoveryResponse(
    string SchemaVersion,
    string AgvId,
    string RunId,
    long Revision,
    DateTimeOffset ObservedAt,
    string CommandId,
    string Action,
    string Status,
    bool Replayed,
    string? ReasonCode,
    OnboardAutomationSnapshot State);

public sealed record AutomationHealthResponse(
    string SchemaVersion,
    string AgvId,
    string RunId,
    long Revision,
    DateTimeOffset ObservedAt,
    string Status);

public sealed record AutomationSnapshotResponse(
    string SchemaVersion,
    string AgvId,
    string RunId,
    long Revision,
    DateTimeOffset ObservedAt,
    OnboardAutomationSnapshot State);

public sealed record AutomationSubmitResponse(
    string SchemaVersion,
    string AgvId,
    string RunId,
    long Revision,
    DateTimeOffset ObservedAt,
    string CommandId,
    bool Accepted,
    bool Replayed,
    string? MessageId,
    string? ReasonCode,
    OnboardAutomationSnapshot State);

public sealed record AutomationErrorResponse(
    string SchemaVersion,
    string RunId,
    long Revision,
    DateTimeOffset ObservedAt,
    string? CommandId,
    string ReasonCode,
    string Message);

internal sealed record AutomationState(
    string RunId,
    long Revision,
    OnboardAutomationSnapshot Snapshot);
