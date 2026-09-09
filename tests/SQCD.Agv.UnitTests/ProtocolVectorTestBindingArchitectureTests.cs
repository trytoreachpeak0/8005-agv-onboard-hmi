using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SQCD.Agv.Contracts;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The machine guard on the binding between the protocol's frozen conformance vectors and this
/// onboard's named tests: every <c>vectorId</c> the protocol froze has a named test that runs, and
/// no test claims a <c>vectorId</c> the protocol never froze.
/// </summary>
/// <remarks>
/// <para>
/// "The onboard proves its half of the vectors" was a sentence nobody had even counted by hand. v2
/// took the vector set to 31 and the slice family table to 16 rows, and this repository carried
/// <b>zero</b> vector traits and zero slice traits -- a vector could be added to the protocol, or
/// the last test proving one deleted, and every gate here would stay green. The control server grew
/// the same guard in its own repository; this is the other end of it, and the two ends were measured
/// separately rather than copied, because a vector needs both ends and the two are not in the same
/// state.
/// </para>
/// <para>
/// <b>The vector list is not copied here.</b>
/// <c>vendor/8005-agv-protocol/integration-slices/index.json</c> is the protocol's own file byte for
/// byte. It introduces no new approved hash: it is listed in
/// <c>vendor/8005-agv-protocol/manifest/release.json</c>'s own <c>files</c> table, and that manifest
/// is pinned to <see cref="WireToGateRelease.ManifestSha256"/> -- the digest this onboard puts on
/// every envelope it sends. <see cref="TheVendoredIndexIsPinnedByTheManifestFileTable"/> checks that
/// chain here rather than trusting a sibling class to have done it, because an unpinned index would
/// silently turn this whole class into a second, quietly diverging vector list. Refreshing the copy
/// is <c>vendor/8005-agv-protocol/README.md</c>.
/// </para>
/// <para>
/// <b>The binding is a trait, read out of the source rather than out of the assembly.</b> A trait
/// earns something a bespoke attribute does not, in that
/// <c>dotnet test --filter "ProtocolVector=CV-SESSION-RECOVERY-HAPPY"</c> runs exactly the tests
/// that claim to prove that vector. (Both test projects run xunit.v3 through
/// <c>xunit.runner.visualstudio</c> -- VSTest mode -- so the filter is the VSTest expression, not
/// <c>--filter-trait</c>.)
/// </para>
/// <para>
/// <b>Why source and not reflection.</b> Half the vectors are proved by
/// <c>SQCD.Agv.WireToGateG2Tests</c>, which targets <c>net8.0-windows</c> because the business
/// service it drives lives in <c>src/SQCD.Agv.Wpf</c>. This guard has to run headless, so it lives
/// in <c>SQCD.Agv.UnitTests</c> (<c>net8.0</c>), and a <c>net8.0</c> project cannot reference a
/// <c>net8.0-windows</c> one -- reflection over <c>typeof(...).Assembly</c> would see only this
/// assembly and would report every G2-proved vector as unbound. Reading the source of both projects
/// is the only form of the check that covers the repository rather than half of it, and it follows
/// the precedent <see cref="ReasonCodeRegistryArchitectureTests"/> already set here.
/// </para>
/// <para>
/// <b>What the source scan gives up, and what replaces it.</b> Reflection would know a
/// <c>[Fact(Skip = ...)]</c> does not run, and would apply a class-level trait to every method in
/// the class. The scanner reproduces the first -- <see cref="TheScannerRefusesToCountASkippedTest"/>
/// proves it -- and refuses the second outright:
/// <see cref="NoProtocolVectorTraitSitsOnATypeDeclaration"/> fails on a class-level vector trait
/// rather than quietly ignoring one, so the blind spot is a red rather than a silent under-count.
/// </para>
/// </remarks>
public sealed class ProtocolVectorTestBindingArchitectureTests
{
    /// <summary>
    /// The trait name a test uses to claim it proves a frozen vector.
    /// </summary>
    private const string VectorTrait = "ProtocolVector";

    /// <summary>
    /// Path of the vendored slice index, relative to the vendor root -- and, spelled exactly like
    /// this, its key in the manifest's own <c>files</c> table.
    /// </summary>
    private const string IndexRelativePath = "integration-slices/index.json";

    /// <summary>
    /// The <c>sequence</c> of the last slice this batch implements. Sequences 0 through 7 are
    /// <c>FP-IS-00</c> through <c>FP-IS-07</c>, batch 2 track A, recertified under v2; 8 through 15
    /// are scheduled into batches 3 through 8 by section 7.2 of the full-product scope
    /// specification.
    /// </summary>
    /// <remarks>
    /// One integer rather than a second vector list. The slice-to-batch mapping is deliberately kept
    /// out of the protocol repository -- section 7.2 says so, because rescheduling a batch must not
    /// become a protocol change that voids both ends' gate evidence -- so the boundary has to be
    /// stated somewhere on this side, and this is the smallest form it takes. The two pin sets below
    /// are what make the number load-bearing instead of decorative, and
    /// <see cref="TheIndexParsesIntoSixteenSlicesAndThirtyOneDistinctVectors"/> is what lets it be
    /// stated as a sequence at all: it pins each slice's id to its own sequence, so "sequence 7" and
    /// "<c>FP-IS-07</c>" cannot drift apart.
    /// </remarks>
    private const int LastSliceSequenceThisBatchImplements = 7;

    /// <summary>
    /// The frozen vectors that have no named test <b>because nobody has built their slice yet</b>,
    /// each with the slice that would prove it and the batch that slice is scheduled into. It is
    /// meant to empty as those batches land.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eleven entries, and each was checked here rather than transferred from the control server.
    /// The wire messages these slices are defined in terms of -- <c>DemandSelectionRequested</c>,
    /// <c>SlotConfigurationActivationCommand</c>, <c>OnboardAlarmSnapshot</c>,
    /// <c>UnableToChargeFieldConfirmationRequested</c> and
    /// <c>ManualStationClearanceConfirmationRequested</c> -- are the same eleven
    /// <see cref="ProtocolMessageSurfaceArchitectureTests"/> pins as unimplemented in <c>src/</c> on
    /// this end. There is no test to bind because there is no implementation to test.
    /// </para>
    /// <para>
    /// <b>Pinning is not waiving.</b> The comparison below is exact in both directions, so binding a
    /// vector without deleting its line here fails, and a vector quietly losing its last named test
    /// fails too. What a pin cannot do is hide: every entry names the slice and the batch, and
    /// <see cref="EveryScheduledPinBelongsOnlyToSlicesThisBatchDoesNotImplement"/> refuses a pin on
    /// any vector belonging to a slice this batch does implement -- which is the only way this set
    /// could have become a place to park an inconvenient red.
    /// </para>
    /// <para>
    /// <b>Keep the field when it empties</b>: empty is itself the assertion, and a deviation with
    /// nowhere to go is how a gap survives eight green gates.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> VectorsAwaitingTheirSlice =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["CV-AUTOMATIC-CHARGING-CYCLE"] = "FP-IS-13, batch 8",
            ["CV-MANUAL-STATION-CLEARANCE"] = "FP-IS-13, batch 8",
            ["CV-MULTI-STOP-PLAN-NINE-LEGS"] = "FP-IS-08, batch 6",
            ["CV-ONBOARD-ALARM-SNAPSHOT"] = "FP-IS-15, batch 3",
            ["CV-REVERSED-DIRECTION-JOURNEY"] = "FP-IS-11, batch 4 second stage",
            ["CV-SLOT-CONFIGURATION-ACTIVATION"] = "FP-IS-14, batch 3",
            ["CV-TASK-TYPE-ADMISSION-FAIL-CLOSED"] = "FP-IS-10, batch 4",
            ["CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION"] = "FP-IS-13, batch 8",
            ["CV-WAITING-POINT-IDLE-RETURN"] = "FP-IS-12, batch 5",
            ["CV-WORKLIST-SELECTION-ACCEPTED"] = "FP-IS-09, batch 7",
            ["CV-WORKLIST-SELECTION-STALE-REVISION"] = "FP-IS-09, batch 7"
        };

    /// <summary>
    /// The frozen vectors that belong to a slice <b>this batch does implement</b> and still have no
    /// named test on this end. These are findings, not schedules, and the note on each is the thing
    /// to argue with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This set exists because the control server's shape did not transfer.</b> That end binds
    /// all twenty in-batch vectors; this end binds eighteen. Collapsing these two into
    /// <see cref="VectorsAwaitingTheirSlice"/> would have described a defect as a schedule, and
    /// leaving them out would have made this class red on the day it landed with nothing to say
    /// about why. So they are pinned in a set that is loudly a different kind of thing:
    /// <see cref="EveryOwedNamedTestPinBelongsToASliceThisBatchImplements"/> is the mirror of the
    /// rule on the other set, and between them neither set can absorb the other's entries.
    /// </para>
    /// <para>
    /// <b>It held two entries when it landed on 2026-09-09, and ticket 21 closed both.</b> They
    /// were not the same kind of gap. <c>CV-FAULT-CARGO-HANDOFF</c> was implemented end to end and
    /// driven by no test -- only <c>ProtocolPayloadShapeArchitectureTests</c> touched the message,
    /// and it proves a payload's shape rather than the handoff's behaviour, so tagging it would
    /// have been exactly the false green this class exists to prevent.
    /// <c>CV-FORCED-MECHANICAL-RECOVERY</c> could not be closed by a test at all:
    /// <c>ForcedMechanicalRecoveryResult</c> had no implementation in <c>src/</c>, so
    /// <c>REPORT_FORCED_RECOVERY_OUTCOME</c> had nothing to assert against until product code
    /// changed. Both are now bound by <c>RecoveryVectorG2Tests</c>, which drives the shared
    /// recovery-vector path end to end for each.
    /// </para>
    /// <para>
    /// <b>Keep the field now that it is empty</b> -- empty is itself the assertion, and the two
    /// rules below still run over it. <c>FP-IS-07</c> is inside track A's recertification scope,
    /// and an empty set here is the statement that the recertification has no vector debt left to
    /// dispose of on this end.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> VectorsThisBatchOwesANamedTest =
        new SortedDictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// A vector claim anywhere inside an attribute list. The trait name is interpolated from
    /// <see cref="VectorTrait"/> rather than spelled again: a second, uncompared copy of it here
    /// would go blind the day the trait is renamed, which is the same drift the vendoring
    /// discipline exists to prevent.
    /// </summary>
    /// <remarks>
    /// Not anchored to <c>[</c>, so it reads a combined list -- <c>[Fact, Trait(...)]</c> -- as
    /// well as the one-attribute-per-line form this repository writes. It is only ever run over
    /// text already established to be an attribute list.
    /// </remarks>
    private static readonly Regex VectorTraitRegex = new(
        @"\bTrait\s*\(\s*""" + VectorTrait + @"""\s*,\s*""(?<vectorId>[^""]*)""\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A <c>[Fact]</c> or <c>[Theory]</c> as an element of an attribute list, with its arguments.
    /// </summary>
    private static readonly Regex RunnableTestAttributeRegex = new(
        @"(?<=[\[,])\s*(?:Fact|Theory)\b\s*(?:\((?<arguments>[^)]*)\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The <c>Skip</c> argument specifically, not the word anywhere in the arguments -- a
    /// <c>[Fact(DisplayName = "Skips empty slots")]</c> is a test that runs.
    /// </summary>
    private static readonly Regex SkipArgumentRegex = new(
        @"\bSkip\s*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TypeDeclarationRegex = new(
        @"\b(?:class|record|struct|interface|enum)\s+[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A member declaration, which this repository always writes with an explicit accessibility
    /// modifier (<c>.editorconfig</c>: <c>dotnet_style_require_accessibility_modifiers = always</c>).
    /// </summary>
    /// <remarks>
    /// Requiring the modifier is what lets a claim that lands on something which is not a
    /// declaration at all be reported rather than bound to the file. Without it, the line after a
    /// block-commented-out test -- or after a stray bracket run -- would silently become the
    /// declaration a vector is proved by.
    /// </remarks>
    private static readonly Regex MemberDeclarationRegex = new(
        @"^\s*(?:public|private|internal|protected)\b[^;=]*?\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record Slice(string SliceId, int Sequence, IReadOnlyList<string> VectorIds);

    private sealed record VectorBinding(string VectorId, string TestName);

    private sealed record ScanResult(
        IReadOnlyList<VectorBinding> Bindings,
        IReadOnlyList<string> TypeLevelClaims,
        IReadOnlyList<string> ClaimsOnTestsThatDoNotRun,
        IReadOnlyList<string> UnattributableClaims);

    /// <summary>
    /// What one pass of the scanner collects. The four lists always travel together, so they travel
    /// as one thing rather than as four parameters.
    /// </summary>
    private sealed class ClaimAccumulator
    {
        public List<VectorBinding> Bindings { get; } = [];

        public List<string> TypeLevelClaims { get; } = [];

        public List<string> ClaimsOnTestsThatDoNotRun { get; } = [];

        public List<string> UnattributableClaims { get; } = [];

        public ScanResult ToResult() => new(
            Bindings, TypeLevelClaims, ClaimsOnTestsThatDoNotRun, UnattributableClaims);
    }

    /// <summary>
    /// The vendored index is the protocol's own file, pinned by the manifest this onboard's wire
    /// identity already vouches for.
    /// </summary>
    /// <remarks>
    /// <see cref="ProtocolIdentityArchitectureTests.EveryOtherVendoredFileIsPinnedByTheManifestFileTable"/>
    /// sweeps the whole vendor tree and would catch this too. It is checked again here, at this
    /// class's own site, because it is this class's precondition: every assertion below reads that
    /// file as if it were the protocol, and someone narrowing the sweep has to be told by this class
    /// rather than by a distant one.
    /// </remarks>
    [Fact]
    public void TheVendoredIndexIsPinnedByTheManifestFileTable()
    {
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllBytes(ProtocolIdentityArchitectureTests.ManifestPath()));

        JsonElement entry = Assert.Single(
            manifest.RootElement.GetProperty("files").EnumerateArray(),
            file => string.Equals(
                file.GetProperty("path").GetString(),
                IndexRelativePath,
                StringComparison.Ordinal));

        byte[] bytes = File.ReadAllBytes(IndexPath());

        Assert.Equal(entry.GetProperty("bytes").GetInt32(), bytes.Length);
        Assert.Equal(
            entry.GetProperty("sha256").GetString(),
            WireToGateProtocolSerializer.ComputeSha256(bytes));
    }

    /// <summary>
    /// The parse of the index, checked against the shape v2 froze.
    /// </summary>
    /// <remarks>
    /// Without this, every assertion below could pass over an empty parse. The three counts are the
    /// ones that differ from each other -- 16 slices, 34 <c>vectorIds</c> entries, 31 distinct
    /// vectors -- so a parse that lost a slice, or one that forgot to deduplicate, is reported here
    /// rather than silently narrowing what the binding check covers. The three vectors shared across
    /// slices are counted rather than named: the count is the entire difference between 34 and 31,
    /// and writing their ids out would put a hand-copied fragment of the vector list in a file whose
    /// whole point is not to hold one.
    /// </remarks>
    [Fact]
    public void TheIndexParsesIntoSixteenSlicesAndThirtyOneDistinctVectors()
    {
        Slice[] slices = Slices();
        string[] entries = [.. slices.SelectMany(slice => slice.VectorIds)];

        Assert.Equal(16, slices.Length);
        Assert.Equal(34, entries.Length);
        Assert.Equal(31, FrozenVectorIds().Length);

        // Each slice's id paired with its own sequence, not the two sets compared separately.
        // LastSliceSequenceThisBatchImplements is stated as a sequence and read as a batch boundary
        // on FP-IS-NN, which only holds while the two agree row by row.
        Assert.Equal(
            [.. Enumerable.Range(0, 16).Select(sequence => $"FP-IS-{sequence:D2}/{sequence}")],
            [
                .. slices.OrderBy(slice => slice.Sequence)
                    .Select(slice => $"{slice.SliceId}/{slice.Sequence}")
            ]);
        Assert.Equal(
            3,
            entries.GroupBy(vectorId => vectorId, StringComparer.Ordinal)
                .Count(group => group.Count() > 1));
    }

    /// <summary>
    /// Every frozen vector either has a named test that runs, or is pinned -- as a slice nobody has
    /// built yet, or as a gap this batch owes. The comparison is exact, so it fails in both
    /// directions.
    /// </summary>
    [Fact]
    public void EveryFrozenVectorIsBoundToANamedTestOrPinned()
    {
        VectorBinding[] bindings = [.. Scan().Bindings];
        Assert.NotEmpty(bindings);

        string[] withoutANamedTest =
            VectorsWithoutANamedTest(bindings.Select(binding => binding.VectorId));
        string[] unboundAndUnpinned = UnboundAndUnpinned(AllPinnedVectorIds(), withoutANamedTest);
        string[] pinnedInVain = PinnedInVain(AllPinnedVectorIds(), withoutANamedTest);

        Assert.True(
            unboundAndUnpinned.Length == 0,
            "These frozen vectors have no named test and are not pinned. Either a test proving them "
            + "was deleted, or its " + VectorTrait + " trait was: "
            + string.Join(", ", unboundAndUnpinned));

        Assert.True(
            pinnedInVain.Length == 0,
            "These vectors are pinned as having no named test, but they are not among the frozen "
            + "vectors that lack one -- either a test now binds them, in which case delete their "
            + "line from " + nameof(VectorsAwaitingTheirSlice) + " or "
            + nameof(VectorsThisBatchOwesANamedTest) + ", or the pinned id is not a frozen vector "
            + "at all and is misspelled: " + string.Join(", ", pinnedInVain));
    }

    /// <summary>
    /// No test claims a <c>vectorId</c> the protocol never froze. This is the leg that catches a
    /// misspelling: a mistyped id binds nothing, and the vector it was meant to bind is reported by
    /// the check above as having lost its last named test.
    /// </summary>
    [Fact]
    public void NoTestClaimsAVectorIdTheProtocolNeverFroze()
    {
        VectorBinding[] bindings = [.. Scan().Bindings];
        HashSet<string> claimed = new(
            ClaimedButNeverFrozen(bindings.Select(binding => binding.VectorId)),
            StringComparer.Ordinal);

        string[] offences =
        [
            .. bindings
                .Where(binding => claimed.Contains(binding.VectorId))
                .Select(binding => $"{binding.TestName} claims {binding.VectorId}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            offences.Length == 0,
            "Tests carry a " + VectorTrait + " trait naming vectors the protocol did not freeze: "
            + string.Join("; ", offences));
    }

    /// <summary>
    /// Nothing is pinned as a schedule that this batch is supposed to have built.
    /// </summary>
    /// <remarks>
    /// Without this, <see cref="VectorsAwaitingTheirSlice"/> would be a way to turn any red green by
    /// adding a line. A vector belonging to <c>FP-IS-00</c> through <c>FP-IS-07</c> is a vector this
    /// batch recertifies under v2, and its absence from the suite is a gap rather than a schedule.
    /// </remarks>
    [Fact]
    public void EveryScheduledPinBelongsOnlyToSlicesThisBatchDoesNotImplement()
    {
        Slice[] slices = Slices();

        string[] wronglyPinned =
        [
            .. VectorsAwaitingTheirSlice.Keys
                .Where(vectorId => BelongsToASliceThisBatchImplements(slices, vectorId))
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            wronglyPinned.Length == 0,
            "These vectors belong to slices this batch implements, so a missing named test is a gap "
            + "rather than a schedule -- move them to " + nameof(VectorsThisBatchOwesANamedTest)
            + " with a finding: " + string.Join(", ", wronglyPinned));
    }

    /// <summary>
    /// The mirror: nothing is pinned as a gap this batch owes unless it really belongs to a slice
    /// this batch implements.
    /// </summary>
    /// <remarks>
    /// The two rules together are what keep the sets from being interchangeable. Without this one,
    /// <see cref="VectorsThisBatchOwesANamedTest"/> would be the parking place that
    /// <see cref="EveryScheduledPinBelongsOnlyToSlicesThisBatchDoesNotImplement"/> denies the other
    /// set -- a batch-6 vector could be quietly relabelled as a finding this batch owes, and nothing
    /// would notice.
    /// </remarks>
    [Fact]
    public void EveryOwedNamedTestPinBelongsToASliceThisBatchImplements()
    {
        Slice[] slices = Slices();

        string[] wronglyPinned =
        [
            .. VectorsThisBatchOwesANamedTest.Keys
                .Where(vectorId => !BelongsToASliceThisBatchImplements(slices, vectorId))
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            wronglyPinned.Length == 0,
            "These vectors belong only to slices this batch does not implement, so a missing named "
            + "test is a schedule rather than a gap this batch owes -- move them to "
            + nameof(VectorsAwaitingTheirSlice) + ": " + string.Join(", ", wronglyPinned));

        // The two rules above already make an overlap impossible; this says so directly rather than
        // leaving a reader to re-derive it.
        Assert.Empty(VectorsThisBatchOwesANamedTest.Keys.Intersect(
            VectorsAwaitingTheirSlice.Keys,
            StringComparer.Ordinal));
    }

    /// <summary>
    /// The scanner reads method-level traits only, so a class-level one fails rather than binding
    /// nothing quietly.
    /// </summary>
    /// <remarks>
    /// xUnit applies a class-level trait to every test in the class, and a reader would reasonably
    /// expect one to bind. Teaching the scanner to resolve class scope out of source text is the
    /// kind of half-right parsing that goes wrong silently, so the rule runs the other way: put the
    /// trait on the method, and a class-level one is reported here.
    /// </remarks>
    [Fact]
    public void NoProtocolVectorTraitSitsOnATypeDeclaration()
    {
        string[] claims = [.. Scan().TypeLevelClaims];

        Assert.True(
            claims.Length == 0,
            "A " + VectorTrait + " trait sits on a type declaration, where this scanner does not "
            + "read it. Move it onto the test methods it was meant to bind: "
            + string.Join("; ", claims));
    }

    /// <summary>
    /// No trait sits on a test that does not run.
    /// </summary>
    /// <remarks>
    /// Reported rather than silently dropped: a vector marked proven by a skipped test is the
    /// mechanised form of the hand-counting this class replaces, and without this the vector would
    /// merely turn up as unbound with no explanation of why.
    /// </remarks>
    [Fact]
    public void NoProtocolVectorTraitSitsOnATestThatDoesNotRun()
    {
        string[] claims = [.. Scan().ClaimsOnTestsThatDoNotRun];

        Assert.True(
            claims.Length == 0,
            "A " + VectorTrait + " trait sits on a method that does not run as a test, so the vector "
            + "it claims is not in fact proven: " + string.Join("; ", claims));
    }

    /// <summary>
    /// Every claim lands on a declaration the scanner recognises.
    /// </summary>
    /// <remarks>
    /// The scanner reads text, so there are shapes it does not understand -- an attribute list
    /// followed by something that is not a declaration, most of all. This is what keeps such a claim
    /// from being bound to the file and counted: a claim the scanner cannot place is a claim nobody
    /// is checking, and it is reported rather than believed.
    /// </remarks>
    [Fact]
    public void NoProtocolVectorTraitIsLeftUnattributed()
    {
        string[] claims = [.. Scan().UnattributableClaims];

        Assert.True(
            claims.Length == 0,
            "A " + VectorTrait + " trait does not sit on any declaration this scanner recognises, "
            + "so the vector it claims is bound to nothing: " + string.Join("; ", claims));
    }

    /// <summary>
    /// The scan really covers both test projects, including the <c>net8.0-windows</c> one this
    /// headless assembly cannot reference.
    /// </summary>
    /// <remarks>
    /// This is the assertion that makes the source scan honest. If the walk ever stopped reaching
    /// the G2 project -- a moved directory, a renamed project, an exclusion pattern that ate too
    /// much -- every vector proved only there would silently need pinning, and the pin would read as
    /// a finding rather than as a broken scanner. The check is a lower bound rather than an exact
    /// count on purpose: an exact count is a third hand-maintained list, and the failure this guards
    /// against is the scan going to zero, not the suite gaining a test.
    /// </remarks>
    [Fact]
    public void TheScanCoversBothTestProjects()
    {
        ScanResult unitTests = Scan(Path.Combine(TestsRoot(), "SQCD.Agv.UnitTests"));
        ScanResult g2Tests = Scan(Path.Combine(TestsRoot(), "SQCD.Agv.WireToGateG2Tests"));

        Assert.NotEmpty(unitTests.Bindings);
        Assert.NotEmpty(g2Tests.Bindings);
    }

    /// <summary>
    /// Proves the binding check is not vacuous, by running it over a set that lost a binding the
    /// suite really has.
    /// </summary>
    /// <remarks>
    /// A real vector rather than a fabricated one: <c>CV-SESSION-RECOVERY-HAPPY</c> is bound by
    /// named tests today, so dropping it reproduces exactly what deleting those tests would do. The
    /// second assertion is the load-bearing half -- it says the vector is reported <i>because</i>
    /// nothing binds it, not because somebody had pinned it.
    /// </remarks>
    [Fact]
    public void TheBindingCheckCatchesAVectorThatLostItsLastNamedTest()
    {
        const string vectorId = "CV-SESSION-RECOVERY-HAPPY";
        string[] boundVectorIds =
        [
            .. Scan().Bindings.Select(binding => binding.VectorId)
                .Where(bound => !string.Equals(bound, vectorId, StringComparison.Ordinal))
        ];

        string[] withoutANamedTest = VectorsWithoutANamedTest(boundVectorIds);

        Assert.Contains(vectorId, withoutANamedTest);
        Assert.Contains(vectorId, UnboundAndUnpinned(AllPinnedVectorIds(), withoutANamedTest));
    }

    /// <summary>
    /// Proves the other direction is not vacuous: a pinned vector that a test does bind is reported,
    /// so a pin cannot outlive the gap it records.
    /// </summary>
    /// <remarks>
    /// The pinned set here is synthesised rather than read, because the vector being perturbed has
    /// to be one the suite really binds -- and by construction no such vector is in the real pins.
    /// It also means this proof survives the day batches 3 through 8 empty
    /// <see cref="VectorsAwaitingTheirSlice"/> and the recertification empties
    /// <see cref="VectorsThisBatchOwesANamedTest"/>, which is exactly when someone might be tempted
    /// to delete the comparison it guards.
    /// </remarks>
    [Fact]
    public void TheBindingCheckCatchesAPinnedVectorThatSomethingNowBinds()
    {
        const string vectorId = "CV-SESSION-RECOVERY-HAPPY";
        string[] boundVectorIds = [.. Scan().Bindings.Select(binding => binding.VectorId)];

        Assert.Contains(vectorId, boundVectorIds);
        Assert.Contains(
            vectorId,
            PinnedInVain([vectorId], VectorsWithoutANamedTest(boundVectorIds)));
    }

    /// <summary>
    /// Proves the misspelling check is not vacuous, with an id shaped exactly like a real one.
    /// </summary>
    [Fact]
    public void TheBindingCheckCatchesATestThatNamesAVectorNobodyFroze()
    {
        const string vectorId = "CV-SESSION-RECOVERY-HAPPPY";
        string[] boundVectorIds = [.. Scan().Bindings.Select(binding => binding.VectorId), vectorId];

        Assert.Contains(vectorId, ClaimedButNeverFrozen(boundVectorIds));
    }

    /// <summary>
    /// Proves the scanner does not count a trait on a skipped test as a binding.
    /// </summary>
    /// <remarks>
    /// The synthetic source is assembled from string arguments rather than written as a raw string
    /// literal, here and in <see cref="TheScannerReadsMethodTraitsAndReportsTypeTraits"/>. The scan
    /// reads <c>tests/</c>, which includes this file: a literal's lines would be indistinguishable
    /// from a real attribute block sitting in this class, and the two probes would report themselves
    /// as offences. Every line below starts with a quote, so no line of this file ever looks like an
    /// attribute.
    /// </remarks>
    [Fact]
    public void TheScannerRefusesToCountASkippedTest()
    {
        string source = string.Join(
            '\n',
            "[Fact(Skip = \"under repair\")]",
            "[Trait(\"" + VectorTrait + "\", \"CV-SESSION-RECOVERY-HAPPY\")]",
            "public void ASkippedTest()");

        ScanResult result = ScanText(source, "tests/synthetic/Skipped.cs");

        Assert.Empty(result.Bindings);
        Assert.Contains(
            result.ClaimsOnTestsThatDoNotRun,
            claim => claim.Contains("CV-SESSION-RECOVERY-HAPPY", StringComparison.Ordinal));
    }

    /// <summary>
    /// Proves the scanner reads a method-level trait, and reports a type-level one instead of
    /// binding it.
    /// </summary>
    [Fact]
    public void TheScannerReadsMethodTraitsAndReportsTypeTraits()
    {
        string source = string.Join(
            '\n',
            "[Trait(\"" + VectorTrait + "\", \"CV-LOAD-CORRECTION\")]",
            "public sealed class SomeTests",
            "{",
            "    [Fact]",
            "    [Trait(\"" + VectorTrait + "\", \"CV-EXCEPTION-RESUME\")]",
            "    public void AProvingTest()",
            "    {",
            "    }",
            "}");

        ScanResult result = ScanText(source, "tests/synthetic/Mixed.cs");

        Assert.Equal("CV-EXCEPTION-RESUME", Assert.Single(result.Bindings).VectorId);
        Assert.Contains(
            result.TypeLevelClaims,
            claim => claim.Contains("CV-LOAD-CORRECTION", StringComparison.Ordinal));
    }

    /// <summary>
    /// A commented-out test does not go on proving its vector.
    /// </summary>
    /// <remarks>
    /// <b>This is the one blind spot that failed green rather than red.</b> Before comments were
    /// blanked, the attribute lines inside a block comment read as a real attribute list and the
    /// first line after the comment read as their declaration -- so commenting a test out left its
    /// vector looking proved. Every other shape the scanner does not understand fails towards a red.
    /// </remarks>
    [Fact]
    public void TheScannerIgnoresATraitInsideABlockComment()
    {
        string source = string.Join(
            '\n',
            "/*",
            "[Fact]",
            "[Trait(\"" + VectorTrait + "\", \"CV-LOAD-CORRECTION\")]",
            "public void CommentedOut()",
            "{",
            "}",
            "*/",
            "[Fact]",
            "[Trait(\"" + VectorTrait + "\", \"CV-EXCEPTION-RESUME\")]",
            "// [Trait(\"" + VectorTrait + "\", \"CV-LOAD-CANCELLATION-ALL-EMPTY\")]",
            "public void StillHere()");

        ScanResult result = ScanText(source, "tests/synthetic/Commented.cs");

        Assert.Equal("CV-EXCEPTION-RESUME", Assert.Single(result.Bindings).VectorId);
        Assert.Empty(result.TypeLevelClaims);
        Assert.Empty(result.ClaimsOnTestsThatDoNotRun);
        Assert.Empty(result.UnattributableClaims);
    }

    /// <summary>
    /// A multi-line attribute and a combined attribute list are both read whole.
    /// </summary>
    /// <remarks>
    /// Neither shape appears in this repository today, and both would have failed towards a red
    /// rather than a false green -- the claim would simply not be found, and the vector would report
    /// as having lost its last named test. That is a safe failure and a baffling message, so the
    /// scanner reads them instead.
    /// </remarks>
    [Fact]
    public void TheScannerReadsMultiLineAndCombinedAttributeLists()
    {
        string source = string.Join(
            '\n',
            "[Fact, Trait(\"" + VectorTrait + "\", \"CV-LOAD-CORRECTION\")]",
            "public void ACombinedList()",
            "",
            "[Fact]",
            "[Trait(",
            "    \"" + VectorTrait + "\",",
            "    \"CV-EXCEPTION-RESUME\")]",
            "public void AMultiLineAttribute()");

        ScanResult result = ScanText(source, "tests/synthetic/Shapes.cs");

        Assert.Equal(
            ["CV-EXCEPTION-RESUME", "CV-LOAD-CORRECTION"],
            result.Bindings.Select(binding => binding.VectorId).Order(StringComparer.Ordinal));
        Assert.Empty(result.ClaimsOnTestsThatDoNotRun);
        Assert.Empty(result.UnattributableClaims);
    }

    /// <summary>
    /// The word "Skip" inside another argument is not a skip.
    /// </summary>
    [Fact]
    public void TheScannerDoesNotReadADisplayNameAsASkip()
    {
        string source = string.Join(
            '\n',
            "[Fact(DisplayName = \"Skips empty slots\")]",
            "[Trait(\"" + VectorTrait + "\", \"CV-LOAD-CANCELLATION-ALL-EMPTY\")]",
            "public void ARunningTest()");

        ScanResult result = ScanText(source, "tests/synthetic/DisplayName.cs");

        Assert.Equal(
            "CV-LOAD-CANCELLATION-ALL-EMPTY",
            Assert.Single(result.Bindings).VectorId);
        Assert.Empty(result.ClaimsOnTestsThatDoNotRun);
    }

    /// <summary>
    /// A claim that lands on something which is not a declaration is reported, not bound.
    /// </summary>
    [Fact]
    public void TheScannerReportsAClaimItCannotAttribute()
    {
        string source = string.Join(
            '\n',
            "[Fact]",
            "[Trait(\"" + VectorTrait + "\", \"CV-LOAD-CORRECTION\")]",
            "await DoSomething();");

        ScanResult result = ScanText(source, "tests/synthetic/Unattributable.cs");

        Assert.Empty(result.Bindings);
        Assert.Contains(
            result.UnattributableClaims,
            claim => claim.Contains("CV-LOAD-CORRECTION", StringComparison.Ordinal));
    }

    /// <summary>
    /// Frozen vectors nothing binds and nobody pinned -- the direction that catches a deleted test.
    /// </summary>
    /// <remarks>
    /// The pinned set is a parameter rather than a direct read of the two fields so that the vacuity
    /// proofs can feed it a perturbed one. That also means they keep working after the real sets
    /// empty, which is where batches 3 through 8 and the track A recertification are meant to take
    /// them -- a proof that needed a real pin to perturb would have had to skip itself on that day,
    /// and this suite has no skipped tests.
    /// </remarks>
    private static string[] UnboundAndUnpinned(
        IEnumerable<string> pinned,
        IEnumerable<string> withoutANamedTest) =>
    [
        .. withoutANamedTest.Except(pinned, StringComparer.Ordinal).Order(StringComparer.Ordinal)
    ];

    /// <summary>
    /// Whether any slice this batch implements lists the vector. The single place the batch boundary
    /// is applied, so the two pin rules stay exact mirrors of each other rather than two copies of
    /// one condition that could drift apart.
    /// </summary>
    private static bool BelongsToASliceThisBatchImplements(
        IEnumerable<Slice> slices,
        string vectorId) => slices.Any(slice =>
            slice.Sequence <= LastSliceSequenceThisBatchImplements
            && slice.VectorIds.Contains(vectorId, StringComparer.Ordinal));

    /// <summary>
    /// Pinned vectors that are not in fact frozen vectors lacking a named test -- either something
    /// binds them after all, or the pinned id is not a frozen vector. The direction that keeps a pin
    /// from outliving the gap it records.
    /// </summary>
    private static string[] PinnedInVain(
        IEnumerable<string> pinned,
        IEnumerable<string> withoutANamedTest) =>
    [
        .. pinned.Except(withoutANamedTest, StringComparer.Ordinal).Order(StringComparer.Ordinal)
    ];

    private static string[] AllPinnedVectorIds() =>
    [
        .. VectorsAwaitingTheirSlice.Keys
            .Concat(VectorsThisBatchOwesANamedTest.Keys)
            .Distinct(StringComparer.Ordinal)
    ];

    private static string[] VectorsWithoutANamedTest(IEnumerable<string> boundVectorIds) =>
    [
        .. FrozenVectorIds()
            .Except(boundVectorIds, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    private static string[] ClaimedButNeverFrozen(IEnumerable<string> boundVectorIds) =>
    [
        .. boundVectorIds
            .Except(FrozenVectorIds(), StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    private static ScanResult Scan() => Scan(TestsRoot());

    private static ScanResult Scan(string root)
    {
        ClaimAccumulator claims = new();

        foreach (string path in Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(IsHandWrittenSource)
            .Order(StringComparer.Ordinal))
        {
            ScanText(
                File.ReadAllText(path),
                Path.GetRelativePath(RepositoryRoot(), path).Replace('\\', '/'),
                claims);
        }

        return claims.ToResult();
    }

    private static ScanResult ScanText(string source, string path)
    {
        ClaimAccumulator claims = new();
        ScanText(source, path, claims);
        return claims.ToResult();
    }

    /// <summary>
    /// Reads every attribute list in one file and attributes its vector claims to the declaration
    /// the list sits on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Comments are blanked first, so a commented-out test cannot go on proving its vector. That is
    /// the one blind spot here that failed <i>green</i> rather than red:
    /// <see cref="TheScannerIgnoresATraitInsideABlockComment"/> is the proof.
    /// </para>
    /// <para>
    /// A block then starts on a line whose trimmed form opens with <c>[</c> and runs until its
    /// brackets balance, so a multi-line attribute is read whole; consecutive lists are gathered
    /// into one block, which is how this repository writes them. A collection expression opening a
    /// line satisfies the same rule and is picked up too -- harmless, because a block yielding no
    /// <see cref="VectorTrait"/> match is discarded before anything else happens.
    /// </para>
    /// <para>
    /// The declaration is the line after the block, and it has to <b>be</b> a declaration.
    /// Everything else is reported as unattributable rather than bound to the file, because a claim
    /// the scanner cannot place is a claim nobody is checking.
    /// </para>
    /// </remarks>
    private static void ScanText(string source, string path, ClaimAccumulator claims)
    {
        string[] lines = BlankOutComments(
            source.Replace("\r\n", "\n", StringComparison.Ordinal)).Split('\n');

        int index = 0;
        while (index < lines.Length)
        {
            if (!OpensAnAttributeList(lines[index]))
            {
                index++;
                continue;
            }

            int start = index;
            while (index < lines.Length && OpensAnAttributeList(lines[index]))
            {
                int depth = 0;
                do
                {
                    depth += BracketDelta(lines[index]);
                    index++;
                }
                while (index < lines.Length && depth > 0);
            }

            string block = string.Join('\n', lines[start..index]);
            string[] claimed =
            [
                .. VectorTraitRegex.Matches(block).Select(match => match.Groups["vectorId"].Value)
            ];
            if (claimed.Length == 0)
            {
                continue;
            }

            // C# allows whitespace and comments between an attribute list and what it decorates,
            // and comments are blank by the time we get here, so the declaration is the next line
            // with anything on it.
            int declarationLine = index;
            while (declarationLine < lines.Length
                && string.IsNullOrWhiteSpace(lines[declarationLine]))
            {
                declarationLine++;
            }

            string declaration = declarationLine < lines.Length
                ? lines[declarationLine]
                : string.Empty;
            string site = $"{path}:{start + 1}";

            if (TypeDeclarationRegex.IsMatch(declaration))
            {
                Record(claims.TypeLevelClaims, claimed, site, "on a type declaration");
                continue;
            }

            Match member = MemberDeclarationRegex.Match(declaration);
            if (!member.Success)
            {
                Record(
                    claims.UnattributableClaims,
                    claimed,
                    site,
                    "on something that is not a declaration");
                continue;
            }

            if (!RunsAsATest(block))
            {
                Record(
                    claims.ClaimsOnTestsThatDoNotRun,
                    claimed,
                    site,
                    "on a member that does not run as a test");
                continue;
            }

            string testName = $"{member.Groups["name"].Value} ({site})";
            foreach (string vectorId in claimed)
            {
                claims.Bindings.Add(new VectorBinding(vectorId, testName));
            }
        }
    }

    private static void Record(
        List<string> destination,
        IEnumerable<string> claimed,
        string site,
        string what)
    {
        foreach (string vectorId in claimed)
        {
            destination.Add($"{site} claims {vectorId} {what}");
        }
    }

    private static bool OpensAnAttributeList(string line) => line.TrimStart().StartsWith('[');

    /// <summary>
    /// Net bracket depth contributed by one line, ignoring brackets inside string literals.
    /// </summary>
    private static int BracketDelta(string line)
    {
        int delta = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                i = EndOfStringLiteral(line, i);
                continue;
            }

            if (line[i] == '[')
            {
                delta++;
            }
            else if (line[i] == ']')
            {
                delta--;
            }
        }

        return delta;
    }

    /// <summary>
    /// Replaces every comment with blanks, keeping the file's line numbering intact so reported
    /// sites still point at the right line.
    /// </summary>
    private static string BlankOutComments(string source)
    {
        StringBuilder builder = new(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '"')
            {
                int end = EndOfStringLiteral(source, i);
                builder.Append(source, i, end - i + 1);
                i = end;
                continue;
            }

            if (source[i] != '/' || i + 1 >= source.Length)
            {
                builder.Append(source[i]);
                continue;
            }

            int commentEnd;
            if (source[i + 1] == '/')
            {
                int newline = source.IndexOf('\n', i);
                commentEnd = newline < 0 ? source.Length : newline;
            }
            else if (source[i + 1] == '*')
            {
                int close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                commentEnd = close < 0 ? source.Length : close + 2;
            }
            else
            {
                builder.Append(source[i]);
                continue;
            }

            for (int j = i; j < commentEnd; j++)
            {
                builder.Append(source[j] == '\n' ? '\n' : ' ');
            }

            i = commentEnd - 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Index of the closing quote of the string literal opening at <paramref name="quote"/>,
    /// handling verbatim literals and escapes.
    /// </summary>
    private static int EndOfStringLiteral(string text, int quote)
    {
        bool verbatim = quote > 0 && text[quote - 1] == '@';
        for (int i = quote + 1; i < text.Length; i++)
        {
            if (!verbatim && text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] != '"')
            {
                continue;
            }

            if (verbatim && i + 1 < text.Length && text[i + 1] == '"')
            {
                i++;
                continue;
            }

            return i;
        }

        return text.Length - 1;
    }

    private static bool RunsAsATest(string attributeBlock) => RunnableTestAttributeRegex
        .Matches(attributeBlock)
        .Any(match => !SkipArgumentRegex.IsMatch(match.Groups["arguments"].Value));

    /// <summary>
    /// Hand-written source only: <c>bin/</c> and <c>obj/</c> hold generated and copied files, and a
    /// stale build output there would let a deleted test go on binding its vector.
    /// </summary>
    private static bool IsHandWrittenSource(string path)
    {
        string normalised = path.Replace('\\', '/');
        return !normalised.Contains("/bin/", StringComparison.Ordinal)
            && !normalised.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string[] FrozenVectorIds() =>
    [
        .. Slices()
            .SelectMany(slice => slice.VectorIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    private static Slice[] Slices()
    {
        using JsonDocument index = JsonDocument.Parse(File.ReadAllBytes(IndexPath()));

        return
        [
            .. index.RootElement.GetProperty("slices").EnumerateArray().Select(slice => new Slice(
                slice.GetProperty("integrationSliceId").GetString()!,
                slice.GetProperty("sequence").GetInt32(),
                [
                    .. slice.GetProperty("vectorIds").EnumerateArray()
                        .Select(vectorId => vectorId.GetString()!)
                ]))
        ];
    }

    private static string IndexPath() => Path.Combine(
        ProtocolIdentityArchitectureTests.VendorRoot(),
        IndexRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string TestsRoot() => Path.Combine(RepositoryRoot(), "tests");

    private static string RepositoryRoot() => ProtocolIdentityArchitectureTests.RepositoryRoot();
}
