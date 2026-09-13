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
/// FakeControlServer's -- is checked against the protocol's JSON Schema (ADR-cross-0058 map,
/// 8005-agv-program#27 / #34). Both are in scope because both have already cost us: #15 was
/// FakeControlServer never sending stationDepartureDeadlineAt, #22 its hand-written
/// slotOperationAttemptId, and a peer that sends a wrong line lets this side develop against a
/// contract that does not exist while every G2 slice stays green.
/// </summary>
/// <remarks>
/// <para>
/// Lines are collected through <see cref="WireToGateProtocolSerializer.OutboundObserver"/> while the
/// tests run and validated once, at the end, by tools/SQCD.Agv.SchemaConformance in its own process --
/// that project says why it cannot run in here. A violation fails the run as a test assembly cleanup
/// failure: <c>dotnet test</c> and every G2 slice exit non-zero, but the console summary still reads
/// "Failed: 0". The details are in the TRX and in <c>schema-conformance.txt</c> /
/// <c>schema-violations.json</c> in the report directory, which is <c>WIRE_TO_GATE_SCHEMA_REPORT_DIR</c>
/// when set (run-w2g-g2.ps1 points it at the G2 evidence) and <c>schema-conformance</c> next to the
/// test assembly otherwise.
/// </para>
/// <para>
/// The report names the sending method, not the observation point: the site is read off the stack at
/// the moment the envelope is built. FakeControlServer lives in this assembly, so the origin is decided
/// by type, not by assembly. A line whose nearest caller is any other test type is the test exercising
/// the serializer itself and is skipped, not validated.
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
        // The raw lines stay out of the report directory: a full run is megabytes, and each violation
        // already carries a sample line.
        string linesPath = Path.Combine(Path.GetTempPath(), "w2g-onboard-outbound-" + Guid.NewGuid().ToString("N") + ".ndjson");
        await File.WriteAllLinesAsync(linesPath, _lines.Select(line => JsonSerializer.Serialize(line, RecordOptions)));
        try
        {
            (int exitCode, string report) = await RunValidatorAsync(
                "--lines", linesPath,
                "--report", reportDirectory,
                "--known", Path.Combine(RepositoryRoot(), "tests", "SQCD.Agv.WireToGateG2Tests", "schema-known-violations.json"));
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "schema-conformance.txt"), report);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Outbound schema conformance failed (exit {exitCode}); report in {reportDirectory}.{Environment.NewLine}{report}");
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

    internal static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SQCD_8005AGV.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("No SQCD_8005AGV.sln above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Runs tools/SQCD.Agv.SchemaConformance to completion. Returns its exit code, and its standard output
    /// followed by its standard error.
    /// </summary>
    internal static async Task<(int ExitCode, string Output)> RunValidatorAsync(params string[] arguments)
    {
        ProcessStartInfo start = new(ValidatorPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start " + start.FileName);
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    internal static string ValidatorPath()
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
