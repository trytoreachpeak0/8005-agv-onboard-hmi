using System.Net.Http.Headers;
using System.Text.Json;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// Reads the ControlServer vehicle-safety projection without blocking the WPF
/// thread.  The background poller publishes an immutable snapshot and Read()
/// only performs a volatile reference read, so a stale or failed request can
/// never leave the previous STOPPED result in force.
/// </summary>
public sealed class ControlServerVehicleSafetySignalProvider : IVehicleSafetySignalProvider, IDisposable
{
    private const string ProviderSource = "CONTROL_SERVER_VEHICLE_SAFETY";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private readonly VehicleSafetySettings _settings;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string?> _credentialReader;
    private readonly Uri? _endpoint;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task? _pollingTask;
    private VehicleSafetySignal _current;
    private bool _disposed;

    public ControlServerVehicleSafetySignalProvider(
        VehicleSafetySettings settings,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        bool startPolling = true,
        Func<string?>? credentialReader = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _credentialReader = credentialReader
            ?? (() => Environment.GetEnvironmentVariable(_settings.CredentialEnvironmentVariable));
        _httpClient = httpClient ?? CreateTrustedHttpClient();
        _ownsHttpClient = httpClient is null;
        _endpoint = Uri.TryCreate(_settings.Endpoint, UriKind.Absolute, out Uri? endpoint)
            ? endpoint
            : null;
        _current = UnknownSignal("INITIALIZING");

        if (_settings.Enabled && startPolling)
        {
            _pollingTask = PollLoopAsync();
        }
    }

    /// <summary>
    /// Gets the latest safety snapshot.  This method never performs network IO.
    /// </summary>
    public VehicleSafetySignal Read() => Volatile.Read(ref _current);

    /// <summary>
    /// Runs one request immediately.  Production uses the background loop;
    /// exposing a single refresh also makes transport and fail-closed behavior
    /// deterministic in tests and during diagnostics.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!_settings.Enabled)
        {
            Publish(UnknownSignal("DISABLED"));
            return;
        }

        if (_endpoint is null
            || !_endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(_endpoint.UserInfo)
            || !string.IsNullOrEmpty(_endpoint.Fragment))
        {
            Publish(UnknownSignal("HTTPS_REQUIRED"));
            return;
        }

        string? credential;
        try
        {
            credential = _credentialReader();
        }
        catch (ArgumentException)
        {
            Publish(UnknownSignal("CREDENTIAL_UNAVAILABLE"));
            return;
        }
        if (string.IsNullOrWhiteSpace(credential))
        {
            Publish(UnknownSignal("CREDENTIAL_MISSING"));
            return;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stopping.Token);
        int requestTimeoutMs = _settings.RequestTimeoutMs > 0 ? _settings.RequestTimeoutMs : 1_000;
        timeout.CancelAfter(requestTimeoutMs);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Publish(UnknownSignal($"HTTP_{(int)response.StatusCode}"));
                return;
            }

            await using Stream responseStream = await response.Content
                .ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            VehicleSafetyResponse? payload = await JsonSerializer
                .DeserializeAsync<VehicleSafetyResponse>(responseStream, JsonOptions, timeout.Token)
                .ConfigureAwait(false);
            Publish(MapResponse(payload, credential));
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Disposal is not a safety observation and must not race a final
            // snapshot update while the application is shutting down.
        }
        catch (OperationCanceledException)
        {
            Publish(UnknownSignal("REQUEST_TIMEOUT"));
        }
        catch (JsonException)
        {
            Publish(UnknownSignal("INVALID_JSON"));
        }
        catch (TimeoutException)
        {
            Publish(UnknownSignal("REQUEST_TIMEOUT"));
        }
        catch (IOException)
        {
            Publish(UnknownSignal("HTTPS_REQUEST_FAILED"));
        }
        catch (HttpRequestException)
        {
            // This includes TLS trust failures, DNS failures and connection
            // failures.  No exception text is retained because it could contain
            // endpoint or transport details that do not belong in evidence.
            Publish(UnknownSignal("HTTPS_REQUEST_FAILED"));
        }
        catch (InvalidOperationException)
        {
            Publish(UnknownSignal("INVALID_RESPONSE"));
        }
        catch (ArgumentException)
        {
            // Invalid header/URI data is also fail-closed and never logged.
            Publish(UnknownSignal("INVALID_REQUEST"));
        }
        catch (Exception)
        {
            // A provider failure must never escape into the WPF command path or
            // leave a previous STOPPED snapshot active.
            Publish(UnknownSignal("PROVIDER_FAILURE"));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();
        if (_pollingTask is not null)
        {
            try
            {
                _pollingTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stopping.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task PollLoopAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                await RefreshAsync(_stopping.Token).ConfigureAwait(false);
                int pollIntervalMs = _settings.PollIntervalMs > 0 ? _settings.PollIntervalMs : 1_000;
                await Task.Delay(pollIntervalMs, _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch
        {
            // RefreshAsync is deliberately defensive, but a scheduler failure
            // must still leave the safety state fail-closed.
            Publish(UnknownSignal("POLLING_FAILED"));
        }
    }

    private VehicleSafetySignal MapResponse(VehicleSafetyResponse? payload, string credential)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (payload is null
            || string.IsNullOrWhiteSpace(payload.VehicleKey)
            || string.IsNullOrWhiteSpace(_settings.ExpectedVehicleKey)
            || payload.VehicleKey.Contains(credential, StringComparison.Ordinal)
            || !string.Equals(payload.VehicleKey, _settings.ExpectedVehicleKey, StringComparison.Ordinal))
        {
            return UnknownSignal("VEHICLE_IDENTITY_MISMATCH", now);
        }

        if (payload.ObservedAt is not DateTimeOffset observedAt
            || !IsFresh(observedAt, now))
        {
            return UnknownSignal("EVIDENCE_EXPIRED", now);
        }

        string? source = SanitizePublicValue(payload.Source, credential, 128);
        if (string.IsNullOrWhiteSpace(source))
        {
            return UnknownSignal("SOURCE_MISSING", now);
        }

        IReadOnlyList<string> reasonCodes = SanitizeReasonCodes(payload.ReasonCodes, credential);
        VehicleMotionState motionState = ParseMotionState(payload.MotionState);
        return new VehicleSafetySignal(
            motionState,
            observedAt,
            source,
            payload.VehicleKey,
            reasonCodes);
    }

    private bool IsFresh(DateTimeOffset observedAt, DateTimeOffset now)
    {
        if (_settings.MaximumEvidenceAgeMs <= 0 || observedAt > now)
        {
            return false;
        }

        return now - observedAt <= TimeSpan.FromMilliseconds(_settings.MaximumEvidenceAgeMs);
    }

    private static VehicleMotionState ParseMotionState(string? value) =>
        value?.Trim().ToUpperInvariant() switch
        {
            "STOPPED" => VehicleMotionState.Stopped,
            "MOVING" => VehicleMotionState.Moving,
            "UNKNOWN" => VehicleMotionState.Unknown,
            "MT_NA" => VehicleMotionState.Unknown,
            _ => VehicleMotionState.Unknown
        };

    private static string[] SanitizeReasonCodes(
        IEnumerable<string>? reasonCodes,
        string credential)
    {
        if (reasonCodes is null)
        {
            return Array.Empty<string>();
        }

        return reasonCodes
            .Select(code => SanitizePublicValue(code, credential, 128))
            .Where(code => code is not null)
            .Select(code => code!)
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToArray();
    }

    private static string? SanitizePublicValue(string? value, string credential, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains(credential, StringComparison.Ordinal))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > maxLength
            || trimmed.Any(char.IsControl))
        {
            return null;
        }

        return trimmed;
    }

    private VehicleSafetySignal UnknownSignal(string reasonCode, DateTimeOffset? now = null) =>
        new(
            VehicleMotionState.Unknown,
            now ?? _timeProvider.GetUtcNow(),
            ProviderSource,
            null,
            [reasonCode]);

    private void Publish(VehicleSafetySignal signal) => Volatile.Write(ref _current, signal);

    private static HttpClient CreateTrustedHttpClient()
    {
        // Do not set ServerCertificateCustomValidationCallback: the platform
        // handler must use normal Windows certificate trust and hostname checks.
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record VehicleSafetyResponse(
        string? VehicleKey,
        string? MotionState,
        DateTimeOffset? ObservedAt,
        string? Source,
        string[]? ReasonCodes);
}
