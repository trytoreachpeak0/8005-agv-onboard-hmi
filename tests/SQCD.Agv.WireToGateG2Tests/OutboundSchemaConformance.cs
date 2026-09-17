using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SQCD.Agv.Contracts;
using Xunit;

[assembly: AssemblyFixture(typeof(SQCD.Agv.WireToGateG2Tests.OutboundSchemaConformance))]

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// Every protocol line this assembly sends during a test run -- the vehicle's own and
/// <see cref="FakeControlServer"/>'s -- is checked against the JSON Schemas of the protocol this
/// repository is bound to (8005-agv-onboard-hmi#74). Both origins are in scope because both have
/// already cost the MVP line: #15 was the fake never sending <c>stationDepartureDeadlineAt</c>, #22 its
/// hand-written <c>slotOperationAttemptId</c>. A fake that sends a wrong line lets this side develop
/// against a contract that does not exist while every G2 slice stays green.
/// </summary>
/// <remarks>
/// <para>
/// Lines are collected through <see cref="WireToGateProtocolSerializer.OutboundObserver"/> while the
/// tests run and validated once, when the test process ends, by <c>tools/SQCD.Agv.SchemaConformance</c>
/// in its own process -- that project says why it cannot run in here. It reads the vendored copy under
/// <c>vendor/8005-agv-protocol</c> directly and refuses (exit 2) when that copy is not the one
/// <see cref="WireToGateRelease.ManifestSha256"/> names.
/// </para>
/// <para>
/// <b>Judge the run by its exit code, never by the console summary.</b> A violation fails the run as a
/// test assembly cleanup failure: <c>dotnet test</c> exits non-zero, but its summary still reads
/// <c>Failed: 0</c>. The details are in the TRX and in <c>schema-conformance.txt</c> /
/// <c>schema-violations.json</c> in the report directory, which is <c>WIRE_TO_GATE_SCHEMA_REPORT_DIR</c>
/// when set (<c>scripts/run-w2g-g2.ps1</c> points it at the G2 evidence) and <c>schema-conformance/</c>
/// next to the test assembly otherwise.
/// </para>
/// <para>
/// The report names the sending method, not the observation point: the site is read off the stack at
/// the moment the envelope is built. <see cref="FakeControlServer"/> lives in this assembly, so the
/// origin is decided by type, not by assembly. A line whose nearest caller is any other test type is
/// the test exercising the serializer itself and is skipped, not validated.
/// </para>
/// </remarks>
public sealed class OutboundSchemaConformance : IAsyncDisposable
{
    private const string ReportDirectoryVariable = "WIRE_TO_GATE_SCHEMA_REPORT_DIR";
    private static readonly JsonSerializerOptions RecordOptions = new(JsonSerializerDefaults.Web);
    private static readonly string TestAssembly = typeof(OutboundSchemaConformance).Assembly.GetName().Name!;
    private readonly ConcurrentQueue<ObservedLine> _lines = new();

    public OutboundSchemaConformance() => WireToGateProtocolSerializer.OutboundObserver = Observe;

    public async ValueTask DisposeAsync()
    {
        WireToGateProtocolSerializer.OutboundObserver = null;
        string reportDirectory = Environment.GetEnvironmentVariable(ReportDirectoryVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "schema-conformance");
        Directory.CreateDirectory(reportDirectory);
        string knownPath = Path.Combine(RepositoryRoot(), "tests", "SQCD.Agv.WireToGateG2Tests", "schema-known-violations.json");
        RequireDeliberateTestsExist(knownPath);

        // The raw lines stay out of the report directory: a full run is megabytes, and each violation
        // already carries a sample line.
        string linesPath = Path.Combine(Path.GetTempPath(), "w2g-onboard-outbound-" + Guid.NewGuid().ToString("N") + ".ndjson");
        await File.WriteAllLinesAsync(linesPath, _lines.Select(line => JsonSerializer.Serialize(line, RecordOptions)));
        try
        {
            ProcessStartInfo start = new(ValidatorPath())
            {
                ArgumentList =
                {
                    "--lines", linesPath,
                    "--report", reportDirectory,
                    "--vendor", Path.Combine(RepositoryRoot(), "vendor", "8005-agv-protocol"),
                    "--known", knownPath
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start " + start.FileName);
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string report = await output + await error;
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "schema-conformance.txt"), report);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Outbound schema conformance failed (exit {process.ExitCode}); report in {reportDirectory}.{Environment.NewLine}{report}");
            }
        }
        finally
        {
            File.Delete(linesPath);
        }
    }

    private void Observe(string messageType, string line)
    {
        (string origin, string site) = Locate(new StackTrace(1, false));
        _lines.Enqueue(new ObservedLine(messageType, origin, site, line));
    }

    private static (string Origin, string Site) Locate(StackTrace trace)
    {
        string? origin = null;
        List<string> site = [];
        foreach (StackFrame frame in trace.GetFrames())
        {
            MethodBase? method = frame.GetMethod();
            Type? type = method?.DeclaringType;
            string assembly = type?.Assembly.GetName().Name ?? string.Empty;
            if (method is null || type is null ||
                !assembly.StartsWith("SQCD.Agv.", StringComparison.Ordinal) ||
                assembly == "SQCD.Agv.Contracts")
            {
                continue;
            }
            bool inTests = assembly == TestAssembly;
            bool syntheticPeer = inTests && OutermostType(type) == typeof(FakeControlServer);
            origin ??= !inTests ? "product" : syntheticPeer ? "synthetic-peer" : "test";
            if (inTests && !syntheticPeer)
            {
                break;
            }
            // An async method shows up twice in a row: its kickoff stub, then its state machine.
            string name = Describe(method, type);
            if (site.Count == 0 || site[^1] != name)
            {
                site.Add(name);
            }
            if (site.Count == 3)
            {
                break;
            }
        }
        return (origin ?? "unknown", string.Join(" <- ", site));
    }

    // Async bodies run as MoveNext on a compiler-generated <Name>d__N type, lambdas as <Name>b__N_M,
    // often on a <>c display class; name the method a reader would search for.
    private static string Describe(MethodBase method, Type type)
    {
        // An async lambda nests the manglings: its state machine is <<Outer>b__0>d.
        string generated = type.Name.StartsWith('<') && method.Name == "MoveNext" ? type.Name : method.Name;
        string name = Unmangle(generated);
        int localFunction = generated.IndexOf(">g__", StringComparison.Ordinal);
        if (localFunction >= 0)
        {
            int end = generated.IndexOf('|', localFunction);
            name += "." + (end > 0 ? generated[(localFunction + 4)..end] : generated[(localFunction + 4)..]);
        }
        else if (generated.Contains(">b__", StringComparison.Ordinal))
        {
            name += "(lambda)";
        }
        while (type.Name.StartsWith('<') && type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }
        return type.Name + "." + name;
    }

    private static string Unmangle(string generated)
    {
        string trimmed = generated.TrimStart('<');
        int close = trimmed.IndexOf('>', StringComparison.Ordinal);
        return close > 0 ? trimmed[..close] : generated;
    }

    private static Type OutermostType(Type type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }
        return type;
    }

    /// <summary>
    /// A <c>deliberate</c> entry exempts lines by naming the test that sends them on purpose; once that
    /// test is renamed or deleted, the entry would go on exempting lines nobody sends on purpose.
    /// </summary>
    private static void RequireDeliberateTestsExist(string knownPath)
    {
        using JsonDocument known = JsonDocument.Parse(File.ReadAllBytes(knownPath));
        string[] missing =
        [
            .. known.RootElement.EnumerateArray()
                .Select(entry => entry.TryGetProperty("deliberate", out JsonElement value) ? value.GetString() : null)
                .OfType<string>()
                .Where(name => !TestExists(name))
        ];
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "schema-known-violations.json names deliberate tests that do not exist in " + TestAssembly + ": " +
                string.Join(", ", missing));
        }
    }

    private static bool TestExists(string classAndMethod)
    {
        int dot = classAndMethod.LastIndexOf('.');
        return dot > 0 && typeof(OutboundSchemaConformance).Assembly.GetTypes()
            .Where(type => type.Name == classAndMethod[..dot])
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Any(method => method.Name == classAndMethod[(dot + 1)..] &&
                method.GetCustomAttributes().Any(attribute => attribute is FactAttribute));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SQCD_8005AGV.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("No SQCD_8005AGV.sln above " + AppContext.BaseDirectory);
    }

    private static string ValidatorPath()
    {
        string configuration = typeof(OutboundSchemaConformance).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
        string path = Path.Combine(
            RepositoryRoot(), "tools", "SQCD.Agv.SchemaConformance", "bin", configuration, "net8.0", "win-x64",
            "SQCD.Agv.SchemaConformance.exe");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                "The schema validator is not built. The test project references it for build order only.", path);
    }

    private sealed record ObservedLine(string MessageType, string Origin, string Site, string Line);
}
