using System.Text.Json;
using System.Text.Json.Nodes;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The two seams the startup-failure path is built from (onboard-hmi#14): reading the configuration
/// against a bootstrap logger, and a notice that cannot hold the exit.
/// </summary>
/// <remarks>
/// <see cref="StartupConfigurationRejectionTests"/> proves the whole process shape through the real
/// executable, but on the session-0 CI runner the notice is never shown, so the "shown and never
/// dismissed" branch -- the one the vehicle's autologon desktop takes -- is covered only here.
/// No <c>IntegrationSlice</c> trait: this claims no protocol vector.
/// </remarks>
public sealed class StartupFailurePathTests
{
    private static readonly JsonDocumentOptions CommentTolerant = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [Fact]
    public void ARejectedConfigurationIsLoggedWithItsPathAndOriginalReason()
    {
        string settingsPath = WriteShippedSettings(messageTimeoutMs: 3000);
        try
        {
            RecordingLogger logger = new();

            OnboardSettings? settings = StartupConfiguration.TryLoad(settingsPath, logger);

            Assert.Null(settings);
            (LogSeverity severity, _, string message) = Assert.Single(logger.Entries);
            Assert.Equal(LogSeverity.Error, severity);
            Assert.Contains(settingsPath, message, StringComparison.Ordinal);
            Assert.Contains("InvalidDataException", message, StringComparison.Ordinal);
            Assert.Contains(
                "WIRE_TO_GATE消息超时必须严格小于ADR-cross-0027静默失联阈值的一半",
                message,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    /// <summary>
    /// The unchanged path: the same shipped file one millisecond under the bound loads, and the
    /// bootstrap logger hears nothing.
    /// </summary>
    [Fact]
    public void AnAcceptedConfigurationLoadsAndLogsNothing()
    {
        string settingsPath = WriteShippedSettings(messageTimeoutMs: 2999);
        try
        {
            RecordingLogger logger = new();

            OnboardSettings? settings = StartupConfiguration.TryLoad(settingsPath, logger);

            Assert.NotNull(settings);
            Assert.Equal(2999, settings.WireToGate.MessageTimeoutMs);
            Assert.Empty(logger.Entries);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    /// <summary>
    /// The vehicle's case: a notice nobody dismisses.  The exit waits for the timeout and no longer.
    /// </summary>
    [Fact]
    public async Task ANoticeNobodyDismissesStopsHoldingTheExitAtTheTimeout()
    {
        using ManualResetEventSlim neverDismissed = new(false);
        bool? ranOnBackgroundThread = null;
        try
        {
            // The outer bound is the test's own: a notice that ignored its timeout would otherwise hang
            // this test rather than fail it.
            bool dismissed = await StartupFailureNotice.ShowAsync(
                    () =>
                    {
                        ranOnBackgroundThread = Thread.CurrentThread.IsBackground;
                        neverDismissed.Wait();
                    },
                    TimeSpan.FromMilliseconds(300))
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.False(dismissed);
            // A foreground thread would keep the process alive after Shutdown: the same defect, moved.
            Assert.True(ranOnBackgroundThread);
        }
        finally
        {
            neverDismissed.Set();
        }
    }

    [Fact]
    public async Task ADismissedNoticeReleasesTheExitAtOnce()
    {
        bool dismissed = await StartupFailureNotice.ShowAsync(() => { }, TimeSpan.FromMinutes(5));

        Assert.True(dismissed);
    }

    private static string WriteShippedSettings(int messageTimeoutMs)
    {
        string shipped = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        JsonNode settings = JsonNode.Parse(File.ReadAllText(shipped), documentOptions: CommentTolerant)
            ?? throw new InvalidOperationException("appsettings.json is empty.");
        JsonNode wireToGate = settings["wireToGate"]
            ?? throw new InvalidOperationException("appsettings.json has no wireToGate section.");
        wireToGate["enabled"] = true;
        wireToGate["messageTimeoutMs"] = messageTimeoutMs;
        string path = Path.Combine(Path.GetTempPath(), $"sqcd-agv-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, settings.ToJsonString());
        return path;
    }
}
