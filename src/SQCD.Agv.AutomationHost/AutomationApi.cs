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
    public static void Map(
        WebApplication app,
        IOnboardAutomationFacade facade,
        string runId)
    {
        AutomationRevisionState revision = new(facade, runId);
        ConcurrentDictionary<string, CachedSubmit> submissions = new(StringComparer.Ordinal);
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
            state.Snapshot.AgvId,
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
