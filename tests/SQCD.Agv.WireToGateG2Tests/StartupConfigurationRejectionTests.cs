using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A rejected configuration must leave a trace and end the process (onboard-hmi#14).
/// </summary>
/// <remarks>
/// <para>
/// These start the real <c>SQCD.Agv.Wpf.exe</c> rather than calling a loader, because the defect
/// was the process shape and not the loader: the configuration was rejected correctly, then the
/// catch ran with a logger that did not exist yet, and a modal dialog nobody was standing in front
/// of kept the process alive with no window. Only the real process can show "it ended", and only
/// its own log directory can show "it said why".
/// </para>
/// <para>
/// Each case runs a private copy of the build output, because the application reads
/// <c>appsettings.json</c> from, and writes <c>logs/</c> under, its own base directory. Editing the
/// shared copy would race every other test and leave a broken file behind.
/// </para>
/// <para>
/// The rejections are real ones taken from the shipped <c>appsettings.json</c>, not a deleted
/// validator: <c>messageTimeoutMs=3000</c> is what control-server#306 hit (the bound is the
/// ADR-cross-0027 half-liveness of 3000 ms, exclusive), plus a missing file and a JSON syntax error,
/// which fail in <c>OnboardSettings.Load</c> before validation ever runs.
/// </para>
/// <para>
/// No <c>IntegrationSlice</c> trait: this claims no protocol vector.
/// </para>
/// </remarks>
public sealed class StartupConfigurationRejectionTests
{
    /// <summary>
    /// How long the process may take to end.  It covers a cold start plus the bounded startup-failure
    /// notice, which is only shown in an interactive session (not on the session-0 CI runner).
    /// Before the fix the process never ended at all, so any finite bound separates the two.
    /// </summary>
    private static readonly TimeSpan ExitBudget = TimeSpan.FromSeconds(90);

    private static readonly JsonDocumentOptions CommentTolerant = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static TheoryData<string> Rejections => new()
    {
        "message-timeout-at-liveness-bound",
        "missing-file",
        "malformed-json"
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task RejectedConfigurationIsLoggedWithItsPathAndTheProcessExitsNonZero(string rejection)
    {
        string root = CopyApplication();
        try
        {
            string settingsPath = Path.Combine(root, "appsettings.json");
            string expectedReason = WriteRejectedSettings(settingsPath, rejection);

            (int exitCode, bool exited, TimeSpan elapsed, string standardError) =
                await RunAsync(Path.Combine(root, "SQCD.Agv.Wpf.exe"));

            Assert.True(exited, $"车载端在 {ExitBudget.TotalSeconds:0} 秒内没有退出：配置被拒后进程仍然活着。");
            Assert.NotEqual(0, exitCode);
            if (!Environment.UserInteractive)
            {
                // Where nobody can see a notice (the session-0 CI runner, like this test host), none
                // may be shown: the process has to end before the notice timeout could even elapse.
                // In an interactive session the notice is shown and bounded, which is what the
                // budget above allows for.
                Assert.True(
                    elapsed < StartupFailureNotice.Timeout,
                    $"非交互会话里车载端用了 {elapsed} 才退出：不该弹的提示框弹了，挡满了时限。");
            }
            string log = ReadLogs(root);
            Assert.Contains(expectedReason, log, StringComparison.Ordinal);
            Assert.Contains(settingsPath, log, StringComparison.OrdinalIgnoreCase);
            // The same line on stderr, which is where a launcher looks: the L2 harness points
            // logging.directory at its evidence folder and captures stderr as <name>.err.log, so a
            // reason written only under the bootstrap logs/ beside the executable does not land with
            // the rest of its evidence (review of onboard-hmi#196).
            Assert.Contains(expectedReason, standardError, StringComparison.Ordinal);
            Assert.Contains(settingsPath, standardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Writes the rejected configuration and returns a fragment of the rejection text the log must
    /// carry verbatim.
    /// </summary>
    private static string WriteRejectedSettings(string settingsPath, string rejection)
    {
        switch (rejection)
        {
            case "message-timeout-at-liveness-bound":
                JsonNode settings = JsonNode.Parse(File.ReadAllText(settingsPath), documentOptions: CommentTolerant)
                    ?? throw new InvalidOperationException("appsettings.json is empty.");
                JsonNode wireToGate = settings["wireToGate"]
                    ?? throw new InvalidOperationException("appsettings.json has no wireToGate section.");
                // Enabled, because the timeout is only validated for an enabled session; 3000 is the
                // exact value control-server#306 was rejected with.
                wireToGate["enabled"] = true;
                wireToGate["messageTimeoutMs"] = 3000;
                File.WriteAllText(settingsPath, settings.ToJsonString());
                return "WIRE_TO_GATE消息超时必须严格小于ADR-cross-0027静默失联阈值的一半";
            case "missing-file":
                File.Delete(settingsPath);
                return "找不到车载端配置文件";
            case "malformed-json":
                File.WriteAllText(settingsPath, "{ \"environment\": \"Development\", ");
                // The parser's own position report, which only the original exception text carries.
                return "BytePositionInLine";
            default:
                throw new ArgumentOutOfRangeException(nameof(rejection), rejection, null);
        }
    }

    /// <remarks>
    /// Whatever the outcome, the process is gone when this returns: past the budget it is killed with
    /// its whole tree and waited for, so a red run cannot leave an orphan holding the CI job open.  If
    /// even that fails, the test fails loudly rather than returning as if the process had ended.
    /// </remarks>
    private static async Task<(int ExitCode, bool Exited, TimeSpan Elapsed, string StandardError)> RunAsync(
        string executable)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        using Process process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            // Decoded as UTF-8 on purpose: the launcher's .err.log is read as UTF-8, so a child that
            // wrote the ANSI code page would fail here with mojibake instead of passing unnoticed.
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(executable)!
        }) ?? throw new InvalidOperationException("SQCD.Agv.Wpf.exe did not start.");
        // Drained concurrently: a child blocked on a full stderr pipe would look exactly like the hang.
        Task<string> standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using CancellationTokenSource budget = new(ExitBudget);
        try
        {
            await process.WaitForExitAsync(budget.Token);
            return (process.ExitCode, true, elapsed.Elapsed, await standardError);
        }
        catch (OperationCanceledException)
        {
            // The defect's own shape: kill it so a red run does not leave a windowless process behind.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended between the budget running out and the kill; the wait below confirms it.
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                Assert.Fail($"超时后结束车载端进程 {process.Id} 失败：{exception.Message}");
            }

            using CancellationTokenSource killBudget = new(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(killBudget.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"车载端进程 {process.Id} 被结束后 30 秒仍未退出，可能留下孤儿进程。");
            }

            return (process.ExitCode, false, elapsed.Elapsed, await standardError);
        }
    }

    private static string CopyApplication()
    {
        string source = AppContext.BaseDirectory;
        string root = Path.Combine(Path.GetTempPath(), "sqcd-agv-startup-" + Guid.NewGuid().ToString("N"));
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            if (IsLogDirectory(relative))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(root, relative));
        }

        Directory.CreateDirectory(root);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (IsLogDirectory(relative))
            {
                continue;
            }

            File.Copy(file, Path.Combine(root, relative));
        }

        return root;
    }

    private static bool IsLogDirectory(string relative) =>
        relative.Equals("logs", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string ReadLogs(string root)
    {
        string logs = Path.Combine(root, "logs");
        Assert.True(Directory.Exists(logs), $"车载端没有创建日志目录 {logs}：配置被拒时一个字节都没写。");
        string[] files = Directory.GetFiles(logs, "agv-*.log");
        Assert.NotEmpty(files);
        return string.Join(Environment.NewLine, files.Select(File.ReadAllText));
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
