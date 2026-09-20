using System.Text.RegularExpressions;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// Keeps <see cref="OnboardFailureClassification"/> total against the sources it classifies
/// (8005-agv-onboard-hmi#171).
/// </summary>
/// <remarks>
/// <para>
/// The defect this guards against is not "one code was classified wrong". It is that on 2026-09-16 an
/// ordinary operator mis-scan latched a fatal safety fault, because <c>HandleCommandError</c> had no
/// notion of a business refusal at all — and that the same thing would happen again the next time
/// somebody added one, silently, with nothing going red.
/// </para>
/// <para>
/// <b>So the gate is on the source, not on a list somebody maintains by hand.</b> Every
/// <c>throw new …Exception("CODE")</c> under <c>src/</c> has to be registered as one kind or the other,
/// and every <c>EnterFatalFault("CODE", …)</c> call site has to say whether maintenance can lift it.
/// Adding either without deciding fails here. The decision is what is being forced, not any particular
/// answer.
/// </para>
/// <para>
/// <b>Both scanners are asserted to still match their real sources</b>
/// (<see cref="BothScannersStillFindTheirTargetsInProductionSources"/>). A scanner that quietly stops
/// matching passes every other test in this file while covering nothing, which is the failure mode
/// <c>ReasonCodeRegistryArchitectureTests</c> already learned to assert against.
/// </para>
/// </remarks>
public sealed class LocalFailureCodeRegistryArchitectureTests
{
    /// <summary>
    /// A local failure code travels as the exception's message. The literal in the message position of
    /// a <c>throw new</c> is that code whenever it is upper snake case — nothing else in these sources
    /// is written that way in that position.
    /// </summary>
    private static readonly Regex ThrownCodeRegex = new(
        @"\bthrow\s+new\s+[A-Za-z_][A-Za-z0-9_.]*\s*\(\s*""(?<code>[A-Z][A-Z0-9_]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>The code a fatal-fault latch is entered with, which is always its first argument.</summary>
    private static readonly Regex EnterFatalFaultRegex = new(
        @"\bEnterFatalFault\s*\(\s*""(?<code>[A-Z][A-Z0-9_]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex LineCommentRegex = new(
        @"//[^\n]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BlockCommentRegex = new(
        @"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    [Fact]
    public void EveryLocalFailureCodeThrownUnderSrcIsRegistered()
    {
        SourceCode[] thrown = ScanSource(ThrownCodeRegex);

        Assert.NotEmpty(thrown);
        AssertAllRegistered(
            thrown,
            OnboardFailureClassification.IsRegisteredLocalFailureCode,
            "OnboardFailureClassification's operator-rejection / fatal-fault registry",
            "Decide which kind it is and add it there. Unregistered latches the vehicle.");
    }

    [Fact]
    public void EveryFatalFaultCodeEnteredUnderSrcIsRegistered()
    {
        SourceCode[] entered = ScanSource(EnterFatalFaultRegex);

        Assert.NotEmpty(entered);
        AssertAllRegistered(
            entered,
            OnboardFailureClassification.IsRegisteredFatalFaultCode,
            "OnboardFailureClassification's fatal-fault clearance registry",
            "Decide whether maintenance can lift it on the vehicle and add it there. "
            + "Unregistered means only a restart clears it.");
    }

    /// <summary>
    /// An operator rejection that renders as a bare code tells the person holding the scanner no more
    /// than the red banner did, so "shown instead of latched" has to come with something to show.
    /// </summary>
    [Fact]
    public void EveryOperatorRejectionCodeHasOperatorWording()
    {
        string[] withoutWording = OnboardFailureClassification.RegisteredLocalFailureCodes
            .Where(code =>
                OnboardFailureClassification.Classify(new InvalidOperationException(code))
                    == OnboardCommandFailureKind.OperatorRejection
                && !OnboardCommandRejectionText.HasWording(code))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            withoutWording.Length == 0,
            "These codes are shown to the operator instead of latching the vehicle, but "
            + $"OnboardCommandRejectionText has no sentence for them: {string.Join(", ", withoutWording)}");
    }

    [Theory]
    [InlineData("throw new InvalidOperationException(\"NOT_IN_LOCAL_REGISTRY\");")]
    [InlineData("_ => throw new InvalidDataException(\"NOT_IN_LOCAL_REGISTRY\")")]
    [InlineData("state.OperationContext ?? throw new WireToGateResumeNotStartedException(\"NOT_IN_LOCAL_REGISTRY\");")]
    public void TheThrownCodeScannerCatchesAnUnregisteredCodeInEveryThrowShape(string syntheticSource)
    {
        SourceCode[] thrown = ScanText(syntheticSource, "synthetic/Refusal.cs", ThrownCodeRegex);

        Assert.NotEmpty(thrown);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => AssertAllRegistered(
            thrown,
            OnboardFailureClassification.IsRegisteredLocalFailureCode,
            "registry",
            "hint"));
        Assert.Contains("NOT_IN_LOCAL_REGISTRY", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFatalFaultScannerCatchesAnUnregisteredLatchCode()
    {
        const string syntheticSource = """
            _controller.EnterFatalFault("NOT_IN_LATCH_REGISTRY", SomeBanner);
            """;

        SourceCode[] entered = ScanText(syntheticSource, "synthetic/Latch.cs", EnterFatalFaultRegex);

        Assert.NotEmpty(entered);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => AssertAllRegistered(
            entered,
            OnboardFailureClassification.IsRegisteredFatalFaultCode,
            "registry",
            "hint"));
        Assert.Contains("NOT_IN_LATCH_REGISTRY", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A code named only in a comment is prose, not a throw. Without this the gate would demand the
    /// registration of anything anyone quoted, and the cheapest way out of that is to weaken the gate.
    /// </summary>
    [Fact]
    public void TheScannersIgnoreCodesThatOnlyAppearInComments()
    {
        const string syntheticSource = """
            // was: throw new InvalidOperationException("NOT_IN_LOCAL_REGISTRY");
            /* and EnterFatalFault("NOT_IN_LATCH_REGISTRY", Banner) used to live here */
            """;

        Assert.Empty(ScanText(syntheticSource, "synthetic/Prose.cs", ThrownCodeRegex));
        Assert.Empty(ScanText(syntheticSource, "synthetic/Prose.cs", EnterFatalFaultRegex));
    }

    /// <summary>
    /// Both scanners anchor on shapes the production sources actually use. If either stops matching
    /// there, this gate has gone blind and should say so rather than keep passing.
    /// </summary>
    [Fact]
    public void BothScannersStillFindTheirTargetsInProductionSources()
    {
        SourceCode[] thrown = ScanSource(ThrownCodeRegex);
        SourceCode[] entered = ScanSource(EnterFatalFaultRegex);

        // The scan that started this ticket: the business service expresses business refusals as
        // exception messages, and the mis-scan code is one of them.
        Assert.Contains(
            thrown,
            item => item.Code == "SUBLOT_NOT_IN_WORKLIST"
                && item.Path == "src/SQCD.Agv.Wpf/WireToGateBusinessService.cs");

        // Both latch sites, the UI command one and the dispatcher one.
        Assert.Contains(
            entered,
            item => item.Code == "UI_COMMAND_FAILED"
                && item.Path == "src/SQCD.Agv.Wpf/ViewModels/MainViewModel.cs");
        Assert.Contains(
            entered,
            item => item.Code == "UNHANDLED_UI_ERROR" && item.Path == "src/SQCD.Agv.Wpf/App.xaml.cs");
    }

    /// <summary>
    /// Registry one is keyed by code, and that is the point: the same code is thrown as four different
    /// exception types, and one exception type carries codes of both kinds. A type-based rule cannot
    /// express this, which is why the ticket asked for a shape that is not one.
    /// </summary>
    [Fact]
    public void ClassificationReadsTheCodeAndNotTheExceptionType()
    {
        const string rejection = "RECOVERY_OPERATION_CONTEXT_MISSING";
        const string latching = "RECOVERY_SCOPE_MISMATCH";

        // Same code, four exception types, one answer.
        Assert.All(
            new Exception[]
            {
                new InvalidOperationException(rejection),
                new InvalidDataException(rejection),
                new IOException(rejection),
                new TimeoutException(rejection)
            },
            exception => Assert.Equal(
                OnboardCommandFailureKind.OperatorRejection,
                OnboardFailureClassification.Classify(exception)));

        // One exception type, two codes, two answers.
        Assert.Equal(
            OnboardCommandFailureKind.OperatorRejection,
            OnboardFailureClassification.Classify(new InvalidOperationException("SUBLOT_NOT_IN_WORKLIST")));
        Assert.Equal(
            OnboardCommandFailureKind.FatalFault,
            OnboardFailureClassification.Classify(new InvalidOperationException(latching)));
    }

    /// <summary>
    /// The fallback is the safe one in both registries: an unregistered code latches, and an
    /// unregistered latch cannot be lifted here. A technical failure carrying no code at all latches
    /// too — that is the case the original code was right about.
    /// </summary>
    [Fact]
    public void AnythingUnregisteredLatchesAndCannotBeLiftedHere()
    {
        Assert.Equal(
            OnboardCommandFailureKind.FatalFault,
            OnboardFailureClassification.Classify(new InvalidOperationException("NOT_IN_LOCAL_REGISTRY")));
        Assert.Equal(
            OnboardCommandFailureKind.FatalFault,
            OnboardFailureClassification.Classify(new IOException("Unable to read data from the transport connection.")));
        Assert.Equal(
            OnboardCommandFailureKind.FatalFault,
            OnboardFailureClassification.Classify(new TimeoutException()));

        Assert.Equal(
            FatalFaultClearance.TerminalUntilRestart,
            OnboardFailureClassification.Clearance("NOT_IN_LATCH_REGISTRY"));
    }

    /// <summary>
    /// The two latch codes are classified apart on purpose, and the ticket's second half rests on it:
    /// a failed UI command can be reviewed away, an exception from nowhere cannot.
    /// </summary>
    [Fact]
    public void OnlyTheUiCommandLatchIsClearableOnTheVehicle()
    {
        Assert.Equal(
            FatalFaultClearance.ClearableBySafetyReview,
            OnboardFailureClassification.Clearance("UI_COMMAND_FAILED"));
        Assert.Equal(
            FatalFaultClearance.TerminalUntilRestart,
            OnboardFailureClassification.Clearance("UNHANDLED_UI_ERROR"));
    }

    private static SourceCode[] ScanSource(Regex regex)
    {
        string repositoryRoot = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string sourceRoot = Path.Combine(repositoryRoot, "src");
        List<SourceCode> found = [];
        foreach (string path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
            found.AddRange(ScanText(File.ReadAllText(path), relative, regex));
        }

        return found
            .Distinct()
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Line)
            .ToArray();
    }

    private static SourceCode[] ScanText(string source, string path, Regex regex)
    {
        // Comments are blanked rather than removed so the reported line numbers stay true.
        string code = BlockCommentRegex.Replace(
            LineCommentRegex.Replace(source, match => new string(' ', match.Length)),
            match => new string('\n', match.Value.Count(character => character == '\n')));

        return regex.Matches(code)
            .Select(match => new SourceCode(
                match.Groups["code"].Value,
                path,
                code[..match.Groups["code"].Index].Count(character => character == '\n') + 1))
            .ToArray();
    }

    private static void AssertAllRegistered(
        IEnumerable<SourceCode> found,
        Func<string, bool> isRegistered,
        string registryName,
        string hint)
    {
        SourceCode[] violations = found.Where(item => !isRegistered(item.Code)).ToArray();
        if (violations.Length == 0)
        {
            return;
        }

        string details = string.Join(
            Environment.NewLine,
            violations.Select(item => $"{item.Code} at {item.Path}:{item.Line}"));
        throw new InvalidDataException(
            $"These codes are missing from {registryName}:{Environment.NewLine}{details}"
            + $"{Environment.NewLine}{hint}");
    }

    private sealed record SourceCode(string Code, string Path, int Line);
}
