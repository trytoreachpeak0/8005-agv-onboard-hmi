using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The known-violations ledger of the schema gate (<c>tools/SQCD.Agv.SchemaConformance</c>,
/// <c>schema-known-violations.json</c>) keeps its rules once it has a wildcard (8005-agv-onboard-hmi#388).
/// </summary>
/// <remarks>
/// Protocol 6.0.0 lets a <c>LoadCancellationAuthorization</c> authorize only an empty slot scope; the G2 double still
/// authorizes cancellations with slots, as under 5.0.0, until 8005-agv-onboard-hmi#392 moves it and the in-flight
/// cancellation path. Its lines are filed against that issue: two of the three errors with <c>actual</c> written <c>*</c>,
/// because one carries the whole payload and with it each cancellation's own id and the other the slots, which differ from
/// line to line; the third, decision const, exactly. The wildcard is allowed on an issue entry only, matches the synthetic
/// peer only, and leaves messageType, pointer and keyword exact; these tests hold all of it.
/// </remarks>
public sealed class SchemaConformanceKnownViolationArchitectureTests
{
    /// <summary>
    /// The validator's own self-test: the entries cover the three paths whatever the wildcarded values; an error at another
    /// path (under the same keyword too, review M3 of PR #406), under another keyword or on another message type is not
    /// swallowed; the vehicle's own lines are never covered; and a wildcard entry without an issue link is refused.
    /// </summary>
    [Fact]
    public async Task TheValidatorsWildcardRulesHold()
    {
        ProcessStartInfo start = new(OutboundSchemaConformance.ValidatorPath())
        {
            ArgumentList = { "--self-test" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, output);
        Assert.Equal(9, output.Split('\n').Count(line => line.StartsWith("PASS ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Every wildcard entry on file is a filed defect, of 8005-agv-onboard-hmi#392 and nothing else, and names one of the two
    /// schema paths of the cancellation authorization whose actual value changes from line to line (the slots, and the whole
    /// payload with its cancellationId). The third error, decision const, is always "AUTHORIZED" and is filed exactly (review
    /// L3 of PR #406). A third wildcard, or another issue, is a new widening that has to be argued for on its own.
    /// </summary>
    [Fact]
    public void TheOnlyWildcardsOnFileAreTheTwoCancellationAuthorizationPathsOwedBy392()
    {
        string path = Path.Combine(
            OutboundSchemaConformance.RepositoryRoot(),
            "tests",
            "SQCD.Agv.WireToGateG2Tests",
            "schema-known-violations.json");
        using JsonDocument known = JsonDocument.Parse(File.ReadAllBytes(path));
        string[] wildcards =
        [
            .. known.RootElement.EnumerateArray()
                .Where(entry => entry.GetProperty("actual").GetString() == "*")
                .Select(entry =>
                    $"{entry.GetProperty("messageType").GetString()} {entry.GetProperty("pointer").GetString()} "
                    + $"{entry.GetProperty("keyword").GetString()} "
                    + (entry.TryGetProperty("issue", out JsonElement issue) ? issue.GetString() : "no issue"))
                .Order(StringComparer.Ordinal)
        ];

        const string Issue = "https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/392";
        Assert.Equal(
            [
                $"LoadCancellationAuthorization #/payload else {Issue}",
                $"LoadCancellationAuthorization #/payload/slots maxItems {Issue}",
            ],
            wildcards);
    }
}
