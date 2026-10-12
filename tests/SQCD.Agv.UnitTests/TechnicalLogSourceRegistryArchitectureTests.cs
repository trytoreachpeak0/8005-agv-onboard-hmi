using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// Every log source under <c>src/</c> is registered with a default technical-log category
/// (REQ-0272, 8005-agv-onboard-hmi#312).
/// </summary>
/// <remarks>
/// <para>
/// About 200 call sites write the onboard technical log and most of them name no category: it comes from
/// <see cref="OnboardTechnicalLogCategories"/>, keyed by the source class name. A new class that starts logging
/// without a row there is filed under <c>LocalFault</c> at run time — nothing breaks, and the area it actually records
/// silently drops out of the requirement's coverage. So the gate is on the source, the same way
/// <c>LocalFailureCodeRegistryArchitectureTests</c> gates failure codes: adding a source without deciding fails here.
/// </para>
/// <para>
/// The source must be <c>nameof(…)</c> or a string literal. Anything else cannot be checked statically and is
/// reported rather than skipped. Which calls are log writes is <see cref="LoggerWriteScanner"/>'s job; its premise
/// (the receiver's name) is cross-checked here by two others, so that the gate cannot go blind through it.
/// </para>
/// <para>No <c>IntegrationSlice</c> trait, for the reason <see cref="OnboardAlarmSnapshotTests"/> gives.</para>
/// </remarks>
public sealed class TechnicalLogSourceRegistryArchitectureTests
{
    /// <summary>
    /// <c>.Write(</c> receivers under <c>src/</c> that are not loggers. A new one fails
    /// <see cref="EveryWriteReceiverIsALoggerOrAKnownNonLogger"/> until someone decides which it is.
    /// </summary>
    private static readonly string[] KnownNonLoggerReceivers = ["Volatile", "Console", "stream"];

    [Fact]
    public void EveryLogSourceUnderSrcIsRegistered()
    {
        LoggerWriteScanner.WriteCallSite[] writes = LoggerWrites();

        Assert.NotEmpty(writes);
        AssertAllRegistered(writes);
    }

    /// <summary>
    /// Cross-check one, on a premise the scanner does not use: every variable declared as a logger is named so the
    /// scanner sees its writes.
    /// </summary>
    [Fact]
    public void EveryDeclaredLoggerIsNamedSoItsWritesAreSeen()
    {
        LoggerWriteScanner.LoggerDeclarationSite[] declarations = LoggerWriteScanner.DeclarationsInSource();

        Assert.NotEmpty(declarations);
        LoggerWriteScanner.LoggerDeclarationSite[] misnamed = declarations
            .Where(site => !site.Name.EndsWith("logger", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(
            misnamed.Length == 0,
            "These loggers are named so the source-registry scan cannot see their writes; name them …logger: "
            + string.Join(", ", misnamed.Select(site => $"{site.Path}:{site.Line} {site.Name}")));
    }

    /// <summary>
    /// Cross-check two: every <c>.Write(</c> under <c>src/</c> is either a logger write or one of a few known
    /// non-loggers, and none of the latter carries a <c>LogSeverity</c> — the first scanner's premise, kept as a check.
    /// </summary>
    [Fact]
    public void EveryWriteReceiverIsALoggerOrAKnownNonLogger()
    {
        LoggerWriteScanner.WriteCallSite[] others = LoggerWriteScanner.ScanSource()
            .Where(site => !site.IsLoggerWrite)
            .ToArray();

        Assert.Contains(others, site => site.Receiver == "Volatile");
        LoggerWriteScanner.WriteCallSite[] unknown = others
            .Where(site => !KnownNonLoggerReceivers.Contains(site.Receiver, StringComparer.Ordinal)
                || (site.Arguments.Count > 0 && site.Arguments[0].Contains("LogSeverity.", StringComparison.Ordinal)))
            .ToArray();
        Assert.True(
            unknown.Length == 0,
            "These .Write( calls are neither a logger the scan recognises nor a known non-logger: "
            + string.Join(", ", unknown.Select(site => $"{site.Path}:{site.Line} {site.Receiver}")));
    }

    /// <summary>
    /// The scanner still sees the production call sites. A scanner that quietly stops matching passes the tests
    /// above while covering nothing.
    /// </summary>
    [Fact]
    public void TheScannerStillFindsTheProductionCallSites()
    {
        LoggerWriteScanner.WriteCallSite[] writes = LoggerWrites();
        string[] sources = writes.Select(write => write.Source).Distinct().ToArray();

        // Thirteen classes wrote the log when #312 was cut; the scan has to see at least all of them.
        foreach (string expected in new[]
        {
            "App", "MainViewModel", "OnboardController", "WireToGateBusinessService", "TcpJsonRuleGateway",
            "ModbusTcpIoModuleClient", "OnboardAlarmMonitor", "SlotGroupPresentation", "WireToGateSessionService",
            "WireToGateSessionClient", "JournaledOperationsFeed", "SingleInstanceGuard", "RunningInstanceWindow"
        })
        {
            Assert.Contains(expected, sources);
        }

        // All four receiver spellings in use, including the null-conditional one.
        foreach (string receiver in new[] { "_logger", "logger", "bootstrapLogger" })
        {
            Assert.Contains(writes, write => write.Receiver == receiver);
        }

        // The severity shape the first scanner lost: a call with commas inside the first argument.
        Assert.Contains(
            writes,
            write => write.Path == "src/SQCD.Agv.Wpf/WireToGateBusinessService.NewOperationGate.cs"
                && write.Arguments[0].Contains("TryAdd(", StringComparison.Ordinal));
        // 198 at #312's second review round, counted by grep on the raw text, not by this scanner:
        // 188 `_logger.Write(` + 5 `logger.Write(` + 4 `_logger?.Write(` + 1 `bootstrapLogger.Write(`.
        Assert.True(writes.Length >= 198, $"only {writes.Length} log writes found");
    }

    [Theory]
    [InlineData("_logger.Write(LogSeverity.Warning, nameof(NotRegisteredAnywhere), \"x\");")]
    [InlineData("_logger.Write(\n    LogSeverity.Information,\n    OnboardTechnicalLogCategory.SignalChange,\n    nameof(NotRegisteredAnywhere),\n    \"x\");")]
    [InlineData("_logger.Write(ok ? LogSeverity.Information : LogSeverity.Warning, \"NotRegisteredAnywhere\", \"x\");")]
    [InlineData("_logger.Write(\n    _warned.TryAdd(key, 0) ? LogSeverity.Warning : LogSeverity.Debug,\n    nameof(NotRegisteredAnywhere),\n    line);")]
    // Review of #312, V1: a severity that is not a LogSeverity literal at all.
    [InlineData("LogSeverity severity = LogSeverity.Warning;\n_logger.Write(severity, nameof(NotRegisteredAnywhere), \"x\");")]
    [InlineData("void Report(LogSeverity level) => logger.Write(level, nameof(NotRegisteredAnywhere), \"x\");")]
    [InlineData("_logger?.Write((LogSeverity)raw, nameof(NotRegisteredAnywhere), $\"a, b {x}, \\\"c\\\" // not a comment\");")]
    public void TheScannerCatchesAnUnregisteredSourceInEveryShape(string syntheticSource)
    {
        LoggerWriteScanner.WriteCallSite[] writes = LoggerWriteScanner.ScanText(syntheticSource, "synthetic/Log.cs");

        LoggerWriteScanner.WriteCallSite write = Assert.Single(writes);
        Assert.Equal("NotRegisteredAnywhere", write.Source);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => AssertAllRegistered(writes));
        Assert.Contains("NotRegisteredAnywhere", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceThatIsNotStaticallyKnownIsReportedNotSkipped()
    {
        LoggerWriteScanner.WriteCallSite[] writes =
            LoggerWriteScanner.ScanText("_logger.Write(LogSeverity.Information, someSource, \"x\");", "synthetic/Log.cs");

        Assert.Single(writes);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => AssertAllRegistered(writes));
        Assert.Contains("someSource", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A logger under another name is what the first cross-check is for.</summary>
    [Fact]
    public void ALoggerUnderAnUnexpectedNameIsCaughtByItsDeclaration()
    {
        const string syntheticSource = "private readonly IAppLogger _log;\nvoid M() => _log.Write(LogSeverity.Information, nameof(X), \"x\");";

        Assert.DoesNotContain(LoggerWriteScanner.ScanText(syntheticSource, "synthetic/Log.cs"), site => site.IsLoggerWrite);
        Assert.Contains(LoggerWriteScanner.DeclarationsIn(syntheticSource, "synthetic/Log.cs"), site => site.Name == "_log");
    }

    [Fact]
    public void WritesThatAreNotLogWritesAndCommentsAreIgnored()
    {
        const string syntheticSource = """
            Console.Write(line);
            writer.Write(buffer, 0, count);
            // _logger.Write(LogSeverity.Warning, nameof(NotRegisteredAnywhere), "x");
            /* _logger.Write(LogSeverity.Warning, nameof(AlsoNotRegistered), "x"); */
            string text = "_logger.Write(LogSeverity.Warning, nameof(InAString), \"x\");";
            """;

        Assert.DoesNotContain(LoggerWriteScanner.ScanText(syntheticSource, "synthetic/Other.cs"), site => site.IsLoggerWrite);
    }

    private static LoggerWriteScanner.WriteCallSite[] LoggerWrites() =>
        LoggerWriteScanner.ScanSource().Where(site => site.IsLoggerWrite).ToArray();

    private static void AssertAllRegistered(IEnumerable<LoggerWriteScanner.WriteCallSite> writes)
    {
        LoggerWriteScanner.WriteCallSite[] violations = writes
            .Where(write => write.IsLoggerWrite
                && (!write.SourceIsStatic || !OnboardTechnicalLogCategories.IsRegisteredSource(write.Source)))
            .ToArray();
        if (violations.Length == 0)
        {
            return;
        }

        throw new InvalidDataException(
            "These log writes name a source with no default technical-log category in "
            + "OnboardTechnicalLogCategories (REQ-0272). Decide which of the seven areas the class records and "
            + "register it; a source that is not nameof(...) or a literal cannot be checked:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                violations.Select(write => $"  {write.Path}:{write.Line}  {write.Source}")));
    }
}
