using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SQCD.Agv.Core;

namespace SQCD.Agv.AutomationHost;

/// <param name="RecoveryResponseWait">
/// How long a recovery request is held open for its outcome before the host answers
/// <c>IN_PROGRESS</c>. Most recovery requests end at the server's authorization, a few protocol
/// round trips; cancelling a load that is underway runs the whole clearing vector first, and that
/// waits on an operator. Defaults to 15 seconds, five times the configured message timeout.
/// </param>
public sealed record OnboardAutomationHostOptions(
    string ListenAddress,
    int Port,
    TimeSpan? RecoveryResponseWait = null)
{
    public string Endpoint => $"http://{ListenAddress}:{Port}";

    public TimeSpan EffectiveRecoveryResponseWait => RecoveryResponseWait ?? TimeSpan.FromSeconds(15);

    public void Validate()
    {
        if (!System.Net.IPAddress.TryParse(ListenAddress, out System.Net.IPAddress? address)
            || !System.Net.IPAddress.IsLoopback(address)
            || Port is < 1 or > 65_535
            || EffectiveRecoveryResponseWait <= TimeSpan.Zero)
        {
            throw new InvalidDataException("车载端自动化接口必须绑定loopback地址、使用有效端口，恢复请求等待时长必须为正。 ");
        }
    }
}

/// <summary>
/// Embedded, loopback-only HTTP host for HMI automation and deterministic
/// integration tests. It owns no physical state and depends only on the safe
/// automation facade.
/// </summary>
public sealed class OnboardAutomationHttpServer : IAsyncDisposable
{
    private readonly OnboardAutomationHostOptions _options;
    private readonly IOnboardAutomationFacade _facade;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private WebApplication? _application;
    private bool _disposed;

    public OnboardAutomationHttpServer(
        OnboardAutomationHostOptions options,
        IOnboardAutomationFacade facade)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        _options.Validate();
        RunId = Guid.NewGuid().ToString("D");
    }

    public string RunId { get; }

    public string Endpoint => _options.Endpoint;

    public bool IsRunning => _application is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_application is not null)
            {
                return;
            }

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(OnboardAutomationHttpServer).Assembly.GetName().Name,
                EnvironmentName = Environments.Production,
                Args = []
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(Endpoint);
            builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

            WebApplication application = builder.Build();
            OnboardAutomationApi.Map(
                application,
                _facade,
                RunId,
                _options.EffectiveRecoveryResponseWait);
            try
            {
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
                _application = application;
            }
            catch
            {
                await application.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WebApplication? application = _application;
            if (application is null)
            {
                return;
            }

            _application = null;
            try
            {
                await application.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await application.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }
}
