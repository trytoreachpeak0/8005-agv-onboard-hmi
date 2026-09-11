using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SQCD.Agv.Core;

namespace SQCD.Agv.AutomationHost;

public static class OnboardAutomationApi
{
    /// <summary>
    /// Prepended to every recovery reason sent through this face. The protocol has no field for
    /// where a request came from, and the reason is the one free-text field the server keeps, so
    /// this is what lets a scripted recovery be told apart from a pressed button afterwards.
    /// </summary>
    public const string RecoveryReasonPrefix = "【车载端自动化接口】";

    public static void Map(
        WebApplication app,
        IOnboardAutomationFacade facade,
        string runId,
        TimeSpan recoveryResponseWait)
    {
        AutomationRevisionState revision = new(facade, runId);
        ConcurrentDictionary<string, CachedSubmit> submissions = new(StringComparer.Ordinal);
        ConcurrentDictionary<string, CachedRecovery> recoveries = new(StringComparer.Ordinal);
        CachedRecovery? latestRecovery = null;
        SemaphoreSlim commandGate = new(1, 1);

        app.UseStatusCodePages(async statusContext =>
        {
            HttpResponse response = statusContext.HttpContext.Response;
            if (response.HasStarted)
            {
                return;
            }

            (string reasonCode, string message) = response.StatusCode switch
            {
                StatusCodes.Status404NotFound => ("PATH_NOT_FOUND", "请求路径不存在。"),
                StatusCodes.Status405MethodNotAllowed => ("METHOD_NOT_ALLOWED", "请求路径不支持该HTTP方法。"),
                _ => ("HTTP_ERROR", $"HTTP请求失败，状态码{response.StatusCode}。")
            };
            await response.WriteAsJsonAsync(
                new AutomationErrorResponse(
                    "1.0.0",
                    runId,
                    revision.Read().Revision,
                    DateTimeOffset.UtcNow,
                    null,
                    reasonCode,
                    message));
        });

        RouteGroupBuilder group = app.MapGroup("/api/v1");
        group.MapGet("/health", () =>
        {
            AutomationState state = revision.Read();
            string status = state.Snapshot.Onboard.IoConnected
                && state.Snapshot.WireToGateSession?.Connected == true
                && state.Snapshot.Onboard.State != OnboardState.Faulted
                ? "READY"
                : "DEGRADED";
            return Results.Json(new AutomationHealthResponse(
                "1.0.0",
                state.Snapshot.AgvId,
                runId,
                state.Revision,
                DateTimeOffset.UtcNow,
                status));
        });

        group.MapGet("/snapshot", () =>
        {
            AutomationState state = revision.Read();
            return Results.Json(new AutomationSnapshotResponse(
                "1.0.0",
                state.Snapshot.AgvId,
                runId,
                state.Revision,
                DateTimeOffset.UtcNow,
                state.Snapshot));
        });

        group.MapPost("/sublots/submit", async (
            AutomationSublotSubmitRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.RunId)
                || string.IsNullOrWhiteSpace(request.CommandId)
                || request.ExpectedRevision is null
                || string.IsNullOrWhiteSpace(request.Sublot)
                || !Guid.TryParseExact(request.RunId, "D", out _)
                || !Guid.TryParseExact(request.CommandId, "D", out _)
                || request.ExpectedRevision.Value < 1)
            {
                return Results.Json(
                    new AutomationErrorResponse(
                        "1.0.0",
                        runId,
                        revision.Read().Revision,
                        DateTimeOffset.UtcNow,
                        request.CommandId,
                        "INVALID_HTTP_REQUEST",
                        "runId、commandId、expectedRevision和sublot均为必填项。"),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!string.Equals(request.RunId, runId, StringComparison.Ordinal))
            {
                return Results.Json(
                    CreateError(revision.Read(), request.CommandId, "RUN_ID_MISMATCH", "runId不属于当前自动化进程。"),
                    statusCode: StatusCodes.Status409Conflict);
            }

            string fingerprint = JsonSerializer.Serialize(new
            {
                request.RunId,
                request.CommandId,
                request.ExpectedRevision,
                Sublot = request.Sublot.Trim()
            });
            await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AutomationState current = revision.Read();
                if (submissions.TryGetValue(request.CommandId, out CachedSubmit? cached))
                {
                    if (!string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        return Results.Json(
                            CreateError(current, request.CommandId, "COMMAND_ID_CONFLICT", "同一commandId对应了不同请求内容。"),
                            statusCode: StatusCodes.Status409Conflict);
                    }

                    return Results.Json(cached.Response with { Replayed = true });
                }

                if (request.ExpectedRevision.Value != current.Revision)
                {
                    return Results.Json(
                        CreateError(current, request.CommandId, "REVISION_CONFLICT", "快照revision已经变化，请重新读取快照。"),
                        statusCode: StatusCodes.Status409Conflict);
                }

                OnboardAutomationSubmitOutcome outcome = await facade
                    .SubmitSublotAsync(request.Sublot.Trim(), cancellationToken)
                    .ConfigureAwait(false);
                AutomationState after = revision.Read();
                AutomationSubmitResponse response = new(
                    "1.0.0",
                    after.Snapshot.AgvId,
                    runId,
                    after.Revision,
                    DateTimeOffset.UtcNow,
                    request.CommandId,
                    outcome.Accepted,
                    false,
                    outcome.MessageId,
                    outcome.ReasonCode,
                    after.Snapshot);
                submissions[request.CommandId] = new CachedSubmit(fingerprint, response);
                return outcome.Accepted
                    ? Results.Json(response)
                    : Results.Json(response, statusCode: StatusCodes.Status409Conflict);
            }
            finally
            {
                commandGate.Release();
            }
        });

        // Recovery is the button's own request, reached without the button. What that must not
        // become is a way around it, so nothing the caller sends can widen what the button could do:
        // the operator identity and the recovery proof stay in the vehicle's environment, the action
        // must be one the HMI currently offers (the facade decides that), and the server authorizes
        // or refuses it exactly as it would a press.
        group.MapPost("/recovery/requests", async (
            AutomationRecoveryRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.RunId)
                || string.IsNullOrWhiteSpace(request.CommandId)
                || request.ExpectedRevision is null
                || string.IsNullOrWhiteSpace(request.Action)
                || string.IsNullOrWhiteSpace(request.Reason)
                || !Guid.TryParseExact(request.RunId, "D", out _)
                || !Guid.TryParseExact(request.CommandId, "D", out _)
                || request.ExpectedRevision.Value < 1)
            {
                return Results.Json(
                    CreateError(revision.Read(), request.CommandId, "INVALID_HTTP_REQUEST", "runId、commandId、expectedRevision、action和reason均为必填项。"),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!OnboardAutomationRecoveryActions.All.Contains(request.Action, StringComparer.Ordinal))
            {
                return Results.Json(
                    CreateError(revision.Read(), request.CommandId, "RECOVERY_ACTION_UNKNOWN", $"action必须是{string.Join('、', OnboardAutomationRecoveryActions.All)}之一。"),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!string.Equals(request.RunId, runId, StringComparison.Ordinal))
            {
                return Results.Json(
                    CreateError(revision.Read(), request.CommandId, "RUN_ID_MISMATCH", "runId不属于当前自动化进程。"),
                    statusCode: StatusCodes.Status409Conflict);
            }

            string reason = request.Reason.Trim();
            string fingerprint = JsonSerializer.Serialize(new
            {
                request.RunId,
                request.CommandId,
                request.ExpectedRevision,
                request.Action,
                Reason = reason
            });
            CachedRecovery recovery;
            bool replayed;
            await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AutomationState current = revision.Read();
                if (recoveries.TryGetValue(request.CommandId, out CachedRecovery? cached))
                {
                    if (!string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        return Results.Json(
                            CreateError(current, request.CommandId, "COMMAND_ID_CONFLICT", "同一commandId对应了不同请求内容。"),
                            statusCode: StatusCodes.Status409Conflict);
                    }

                    recovery = cached;
                    replayed = true;
                }
                else
                {
                    // One recovery at a time. A second one issued while the first is still running
                    // was decided against a state that is in motion, and the business service would
                    // only queue it behind the first and run it against whatever state that left.
                    if (latestRecovery is { Outcome.IsCompleted: false })
                    {
                        return Results.Json(
                            CreateError(current, request.CommandId, "RECOVERY_REQUEST_IN_PROGRESS", "上一条恢复请求尚未结束，请用它的commandId重放以读取结果。"),
                            statusCode: StatusCodes.Status409Conflict);
                    }

                    if (request.ExpectedRevision.Value != current.Revision)
                    {
                        return Results.Json(
                            CreateError(current, request.CommandId, "REVISION_CONFLICT", "快照revision已经变化，请重新读取快照。"),
                            statusCode: StatusCodes.Status409Conflict);
                    }

                    // Not the HTTP request's token. A caller that hangs up must not abort a recovery
                    // halfway through its slot IO; the button does not cancel either.
                    recovery = new CachedRecovery(
                        fingerprint,
                        facade.RequestRecoveryAsync(
                            request.Action,
                            RecoveryReasonPrefix + reason,
                            CancellationToken.None));
                    recoveries[request.CommandId] = recovery;
                    latestRecovery = recovery;
                    replayed = false;
                }
            }
            finally
            {
                commandGate.Release();
            }

            // Replaying the commandId is how a caller reads an outcome that was still IN_PROGRESS.
            await Task.WhenAny(recovery.Outcome, Task.Delay(recoveryResponseWait, cancellationToken))
                .ConfigureAwait(false);
            if (!recovery.Outcome.IsCompleted)
            {
                AutomationState pending = revision.Read();
                return Results.Json(
                    new AutomationRecoveryResponse(
                        "1.0.0",
                        pending.Snapshot.AgvId,
                        runId,
                        pending.Revision,
                        DateTimeOffset.UtcNow,
                        request.CommandId,
                        request.Action,
                        AutomationRecoveryStatus.InProgress,
                        replayed,
                        null,
                        pending.Snapshot),
                    statusCode: StatusCodes.Status202Accepted);
            }

            OnboardAutomationRecoveryOutcome outcome = await recovery.Outcome.ConfigureAwait(false);
            AutomationState after = revision.Read();
            AutomationRecoveryResponse response = new(
                "1.0.0",
                after.Snapshot.AgvId,
                runId,
                after.Revision,
                DateTimeOffset.UtcNow,
                request.CommandId,
                request.Action,
                outcome.Accepted ? AutomationRecoveryStatus.Accepted : AutomationRecoveryStatus.Rejected,
                replayed,
                outcome.ReasonCode,
                after.Snapshot);
            return outcome.Accepted
                ? Results.Json(response)
                : Results.Json(response, statusCode: StatusCodes.Status409Conflict);
        });

        app.MapGet("/openapi/v1.json", () =>
            Results.File(OpenApiDocument, "application/json", enableRangeProcessing: false));
    }

    private static AutomationErrorResponse CreateError(
        AutomationState state,
        string? commandId,
        string reasonCode,
        string message) =>
        new(
            "1.0.0",
            state.RunId,
            state.Revision,
            DateTimeOffset.UtcNow,
            commandId,
            reasonCode,
            message);

    private static byte[] OpenApiDocument { get; } = LoadOpenApiDocument();

    private static byte[] LoadOpenApiDocument()
    {
        using Stream stream = typeof(OnboardAutomationApi).Assembly
            .GetManifestResourceStream("SQCD.Agv.AutomationHost.openapi.json")
            ?? throw new InvalidOperationException("车载端自动化接口OpenAPI文档未嵌入。 ");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed record CachedSubmit(string Fingerprint, AutomationSubmitResponse Response);

    private sealed record CachedRecovery(
        string Fingerprint,
        Task<OnboardAutomationRecoveryOutcome> Outcome);

    private sealed class AutomationRevisionState
    {
        private static readonly JsonSerializerOptions FingerprintJsonOptions =
            new(JsonSerializerDefaults.Web);
        private readonly IOnboardAutomationFacade _facade;
        private readonly string _runId;
        private readonly object _sync = new();
        private string? _fingerprint;
        private long _revision;

        public AutomationRevisionState(IOnboardAutomationFacade facade, string runId)
        {
            _facade = facade;
            _runId = runId;
        }

        public AutomationState Read()
        {
            OnboardAutomationSnapshot snapshot = _facade.ReadSnapshot();
            string fingerprint = CreateRevisionFingerprint(snapshot);
            lock (_sync)
            {
                if (_fingerprint is null)
                {
                    _revision = 1;
                }
                else if (!string.Equals(_fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    _revision++;
                }

                _fingerprint = fingerprint;
                return new AutomationState(_runId, _revision, snapshot);
            }
        }

        private static string CreateRevisionFingerprint(OnboardAutomationSnapshot snapshot)
        {
            JsonNode node = JsonSerializer.SerializeToNode(snapshot, FingerprintJsonOptions)
                ?? throw new InvalidDataException("车载端自动化快照无法生成revision指纹。 ");
            RemoveObservationTimestamps(node);
            return node.ToJsonString(FingerprintJsonOptions);
        }

        private static void RemoveObservationTimestamps(JsonNode node)
        {
            switch (node)
            {
                case JsonObject jsonObject:
                    foreach (string propertyName in jsonObject.Select(property => property.Key).ToArray())
                    {
                        if (propertyName is "observedAt" or "updatedAt" or "startedAt")
                        {
                            jsonObject.Remove(propertyName);
                        }
                        else if (jsonObject[propertyName] is JsonNode child)
                        {
                            RemoveObservationTimestamps(child);
                        }
                    }

                    break;
                case JsonArray jsonArray:
                    foreach (JsonNode? child in jsonArray)
                    {
                        if (child is not null)
                        {
                            RemoveObservationTimestamps(child);
                        }
                    }

                    break;
            }
        }
    }
}
