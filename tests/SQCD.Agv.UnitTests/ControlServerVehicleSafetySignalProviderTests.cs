using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

public sealed class ControlServerVehicleSafetySignalProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] StoppedReasonCodes = ["MT_STOPPED_CONFIRMED"];
    private static readonly string[] CredentialReasonCodes = [Credential, "SAFE_REASON"];
    private const string VehicleKey = "AGV-8005-27";
    private const string Credential = "unit-test-onboard-credential";

    [Fact]
    public async Task FreshStoppedResponseIsPublishedWithIdentityAndEvidence()
    {
        StubHandler handler = new((_, _) => Task.FromResult(JsonResponse(new
        {
            vehicleKey = VehicleKey,
            motionState = "STOPPED",
            observedAt = Now.AddSeconds(-1),
            source = "RIOT_BEHAVIOR_LAB",
            reasonCodes = StoppedReasonCodes
        })));
        using HttpClient client = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = CreateProvider(client);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        VehicleSafetySignal signal = provider.Read();
        Assert.Equal(VehicleMotionState.Stopped, signal.MotionState);
        Assert.Equal(VehicleKey, signal.VehicleKey);
        Assert.Equal(Now.AddSeconds(-1), signal.ObservedAt);
        Assert.Equal("RIOT_BEHAVIOR_LAB", signal.Source);
        Assert.Contains("MT_STOPPED_CONFIRMED", signal.EffectiveReasonCodes);
        Assert.Equal("Bearer " + Credential, handler.Authorization);
        Assert.Equal("http://control.test/api/onboard/v1/vehicle-safety", handler.RequestUri?.ToString());
    }

    [Theory]
    [InlineData(100, VehicleMotionState.Stopped)]
    [InlineData(500, VehicleMotionState.Stopped)]
    [InlineData(501, VehicleMotionState.Unknown)]
    public async Task FutureEvidenceUsesConfiguredClockSkewTolerance(
        int observedAtOffsetMs,
        VehicleMotionState expected)
    {
        StubHandler handler = new((_, _) => Task.FromResult(JsonResponse(new
        {
            vehicleKey = VehicleKey,
            motionState = "STOPPED",
            observedAt = Now.AddMilliseconds(observedAtOffsetMs),
            source = "CONTROL_SERVER",
            reasonCodes = Array.Empty<string>()
        })));
        using HttpClient client = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = CreateProvider(client);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, provider.Read().MotionState);
        if (expected == VehicleMotionState.Unknown)
        {
            Assert.Contains("EVIDENCE_EXPIRED", provider.Read().EffectiveReasonCodes);
        }
    }

    [Theory]
    [InlineData("MOVING", VehicleMotionState.Moving)]
    [InlineData("UNKNOWN", VehicleMotionState.Unknown)]
    [InlineData("MT_NA", VehicleMotionState.Unknown)]
    public async Task MotionStateMappingIsFailClosed(string motionState, VehicleMotionState expected)
    {
        StubHandler handler = new((_, _) => Task.FromResult(JsonResponse(new
        {
            vehicleKey = VehicleKey,
            motionState,
            observedAt = Now,
            source = "CONTROL_SERVER",
            reasonCodes = Array.Empty<string>()
        })));
        using HttpClient client = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = CreateProvider(client);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, provider.Read().MotionState);
    }

    [Fact]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task ExpiredEvidenceIsUnknownAndDoesNotPreserveStopped()
    {
        StubHandler handler = new((_, _) => Task.FromResult(JsonResponse(new
        {
            vehicleKey = VehicleKey,
            motionState = "STOPPED",
            observedAt = Now.AddSeconds(-10),
            source = "CONTROL_SERVER",
            reasonCodes = Array.Empty<string>()
        })));
        using HttpClient client = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = CreateProvider(client);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        VehicleSafetySignal signal = provider.Read();
        Assert.Equal(VehicleMotionState.Unknown, signal.MotionState);
        Assert.Contains("EVIDENCE_EXPIRED", signal.EffectiveReasonCodes);
        Assert.Null(signal.VehicleKey);
    }

    [Fact]
    public async Task WrongVehicleIdentityIsUnknown()
    {
        StubHandler handler = new((_, _) => Task.FromResult(JsonResponse(new
        {
            vehicleKey = "AGV-OTHER",
            motionState = "STOPPED",
            observedAt = Now,
            source = "CONTROL_SERVER",
            reasonCodes = Array.Empty<string>()
        })));
        using HttpClient client = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = CreateProvider(client);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(VehicleMotionState.Unknown, provider.Read().MotionState);
        Assert.Contains("VEHICLE_IDENTITY_MISMATCH", provider.Read().EffectiveReasonCodes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.UpgradeRequired)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HttpFailureStatusesAreUnknown(HttpStatusCode statusCode)
    {
        StubHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)));
        using HttpClient client = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = CreateProvider(client);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(VehicleMotionState.Unknown, provider.Read().MotionState);
        Assert.Contains($"HTTP_{(int)statusCode}", provider.Read().EffectiveReasonCodes);
    }

    [Fact]
    public async Task HttpEndpointSendsRequestAndPublishesResponse()
    {
        StubHandler handler = new((_, _) => Task.FromResult(JsonResponse(new
        {
            vehicleKey = VehicleKey,
            motionState = "STOPPED",
            observedAt = Now,
            source = "CONTROL_SERVER",
            reasonCodes = Array.Empty<string>()
        })));
        using HttpClient client = new(handler);
        VehicleSafetySettings settings = CreateSettings(
            endpoint: "http://control.test/api/onboard/v1/vehicle-safety");
        using ControlServerVehicleSafetySignalProvider provider = new(
            settings,
            client,
            new FixedTimeProvider(Now),
            startPolling: false,
            credentialReader: () => Credential);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(VehicleMotionState.Stopped, provider.Read().MotionState);
    }

    [Fact]
    public async Task HttpsEndpointIsRejectedBeforeNetworkCall()
    {
        StubHandler handler = new((_, _) => Task.FromResult(JsonResponse(new { })));
        using HttpClient client = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = new(
            CreateSettings(endpoint: "https://control.test/api/onboard/v1/vehicle-safety"),
            client,
            new FixedTimeProvider(Now),
            startPolling: false,
            credentialReader: () => Credential);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(VehicleMotionState.Unknown, provider.Read().MotionState);
        Assert.Contains("HTTP_ENDPOINT_REQUIRED", provider.Read().EffectiveReasonCodes);
    }

    [Fact]
    public async Task MalformedJsonIsUnknown()
    {
        StubHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
        }));
        using HttpClient client = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = CreateProvider(client);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(VehicleMotionState.Unknown, provider.Read().MotionState);
        Assert.Contains("INVALID_JSON", provider.Read().EffectiveReasonCodes);
    }

    [Fact]
    public async Task TimeoutIsUnknown()
    {
        StubHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return JsonResponse(new { motionState = "STOPPED" });
        });
        using HttpClient client = new(handler);
        VehicleSafetySettings settings = CreateSettings(requestTimeoutMs: 20);
        using ControlServerVehicleSafetySignalProvider provider = new(
            settings,
            client,
            new FixedTimeProvider(Now),
            startPolling: false,
            credentialReader: () => Credential);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(VehicleMotionState.Unknown, provider.Read().MotionState);
        Assert.Contains("REQUEST_TIMEOUT", provider.Read().EffectiveReasonCodes);
    }

    [Fact]
    public async Task TransportFailureIsUnknown()
    {
        StubHandler handler = new((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection failed")));
        using HttpClient client = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = CreateProvider(client);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(VehicleMotionState.Unknown, provider.Read().MotionState);
        Assert.Contains("HTTP_REQUEST_FAILED", provider.Read().EffectiveReasonCodes);
    }

    [Fact]
    public async Task FailedRefreshImmediatelyReplacesPreviousStoppedSnapshot()
    {
        int calls = 0;
        StubHandler handler = new((_, _) =>
        {
            calls++;
            return calls == 1
                ? Task.FromResult(JsonResponse(new
                {
                    vehicleKey = VehicleKey,
                    motionState = "STOPPED",
                    observedAt = Now,
                    source = "CONTROL_SERVER",
                    reasonCodes = Array.Empty<string>()
                }))
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("connection closed"));
        });
        using HttpClient client = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = CreateProvider(client);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(VehicleMotionState.Stopped, provider.Read().MotionState);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        VehicleSafetySignal signal = provider.Read();
        Assert.Equal(VehicleMotionState.Unknown, signal.MotionState);
        Assert.False(signal.IsStoppedAndFresh(Now, TimeSpan.FromMinutes(1)));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task CredentialIsNotCopiedIntoPublishedSignal()
    {
        StubHandler handler = new((_, _) => Task.FromResult(JsonResponse(new
        {
            vehicleKey = VehicleKey,
            motionState = "UNKNOWN",
            observedAt = Now,
            source = Credential,
            reasonCodes = CredentialReasonCodes
        })));
        using HttpClient client = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = CreateProvider(client);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        VehicleSafetySignal signal = provider.Read();
        Assert.DoesNotContain(Credential, signal.Source, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, signal.EffectiveReasonCodes);
        Assert.Contains("SOURCE_MISSING", signal.EffectiveReasonCodes);
    }

    [Fact]
    public async Task DelayedFirstRefreshNotifiesStoppedWithoutBlockingSnapshotReads()
    {
        TaskCompletionSource<bool> releaseResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubHandler handler = new(async (_, cancellationToken) =>
        {
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return JsonResponse(new
            {
                vehicleKey = VehicleKey,
                motionState = "STOPPED",
                observedAt = Now,
                source = "CONTROL_SERVER",
                reasonCodes = StoppedReasonCodes
            });
        });
        using HttpClient client = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = new(
            CreateSettings(requestTimeoutMs: 2_000),
            client,
            new FixedTimeProvider(Now),
            startPolling: true,
            credentialReader: () => Credential);
        TaskCompletionSource<VehicleSafetySignal> changed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.SignalChanged += (_, args) => changed.TrySetResult(args.Value);

        Task firstRefresh = provider.WaitForFirstRefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(VehicleMotionState.Unknown, provider.Read().MotionState);
        Assert.False(firstRefresh.IsCompleted);

        releaseResponse.SetResult(true);
        await firstRefresh;
        VehicleSafetySignal signal = await changed.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(VehicleMotionState.Stopped, signal.MotionState);
        Assert.True(signal.IsStoppedAndFresh(Now, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FailedFirstRefreshCompletesStartupWaitAsUnknown()
    {
        StubHandler handler = new((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using HttpClient client = new(handler);
        using ControlServerVehicleSafetySignalProvider provider = new(
            CreateSettings(),
            client,
            new FixedTimeProvider(Now),
            startPolling: true,
            credentialReader: () => Credential);

        await provider.WaitForFirstRefreshAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        VehicleSafetySignal signal = provider.Read();
        Assert.Equal(VehicleMotionState.Unknown, signal.MotionState);
        Assert.Contains("HTTP_503", signal.EffectiveReasonCodes);
    }

    [Fact]
    public async Task ReadDoesNotWaitForAnInFlightNetworkRequest()
    {
        TaskCompletionSource<bool> requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubHandler handler = new(async (_, cancellationToken) =>
        {
            requestStarted.SetResult(true);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return JsonResponse(new { motionState = "STOPPED" });
        });
        using HttpClient client = new(handler);
        VehicleSafetySettings settings = CreateSettings(requestTimeoutMs: 10_000);
        using ControlServerVehicleSafetySignalProvider provider = new(
            settings,
            client,
            new FixedTimeProvider(Now),
            startPolling: false,
            credentialReader: () => Credential);

        Task refresh = provider.RefreshAsync(TestContext.Current.CancellationToken);
        await requestStarted.Task;
        DateTimeOffset before = DateTimeOffset.UtcNow;
        VehicleSafetySignal signal = provider.Read();
        TimeSpan readDuration = DateTimeOffset.UtcNow - before;

        Assert.Equal(VehicleMotionState.Unknown, signal.MotionState);
        Assert.True(readDuration < TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(50));
            await refresh.WaitAsync(cancellation.Token);
        });
    }

    private static ControlServerVehicleSafetySignalProvider CreateProvider(
        HttpClient client) =>
        new(
            CreateSettings(),
            client,
            new FixedTimeProvider(Now),
            startPolling: false,
            credentialReader: () => Credential);

    private static VehicleSafetySettings CreateSettings(
        string? endpoint = null,
        int requestTimeoutMs = 1_000) => new()
        {
            Enabled = true,
            Endpoint = endpoint ?? "http://control.test/api/onboard/v1/vehicle-safety",
            CredentialEnvironmentVariable = "TEST_CONTROL_SERVER_CREDENTIAL",
            ExpectedVehicleKey = VehicleKey,
            MaximumEvidenceAgeMs = 5_000,
            ClockSkewToleranceMs = 500,
            PollIntervalMs = 1_000,
            RequestTimeoutMs = requestTimeoutMs
        };

    private static HttpResponseMessage JsonResponse(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public string? Authorization { get; private set; }

        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Authorization = request.Headers.Authorization?.ToString();
            RequestUri = request.RequestUri;
            return await responseFactory(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
