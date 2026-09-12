namespace SQCD.Agv.UnitTests;

/// <summary>
/// The machine guard on the <c>IntegrationSlice</c> trait: on this end it is exactly the projection
/// of each test's own <c>ProtocolVector</c> traits onto the slices this batch implements, so
/// <c>dotnet test --filter "IntegrationSlice=FP-IS-NN"</c> runs precisely the tests that claim to
/// prove that slice's vectors and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the trait exists at all.</b> <c>run-w2g-g2.ps1</c> ran the whole solution and wrote one
/// verdict, so <c>ONBOARD_HMI_G2</c> could say nothing per slice -- <c>FP-IS-00</c> and
/// <c>FP-IS-01</c> shared a conclusion and the other six did not appear. Ticket 17 has to produce
/// eight separate gate results, which needs a filter, which needs this trait. The sibling
/// ControlServer repository grew it first; this is the other end, and it is <b>not</b> a copy: the
/// rule below is stricter than that one, for a reason the next paragraph gives.
/// </para>
/// <para>
/// <b>The rule is an equality, not a covering.</b> A test carries <c>IntegrationSlice=S</c> if and
/// only if S is a slice this batch implements whose <c>vectorIds</c> contain a vector that same test
/// claims. The control server's equivalent is deliberately one-directional -- it lets a test carry
/// slices beyond its vectors, because 300 of its tests sit outside the slice family entirely and a
/// hand-kept ledger accounts for them. This end has no such population: the 58 vector traits are the
/// whole slice-bearing surface here, the slice traits are derived from them, and an equality is both
/// checkable and easier to keep true than a ledger. The day a test here really needs a slice its
/// vectors do not justify, this goes red and someone argues for it in writing -- which is the point.
/// </para>
/// <para>
/// <b>The batch bound is load-bearing, and <c>CV-MANUAL-CHARGING-RETURN</c> is why.</b> That vector
/// belongs to <c>FP-IS-07</c> and to <c>FP-IS-13</c>, and four tests here prove it. Projecting it
/// onto <c>FP-IS-13</c> would let <c>-Slice FP-IS-13</c> select four tests and write
/// <c>"status": "PASS"</c> for a slice whose other three vectors have no implementation on either
/// end -- a green gate over a quarter-built slice. Ticket 14 closed the zero-test form of that hole
/// on the control server by refusing a filter that selects nothing; a filter that selects
/// <i>some</i> tests cannot be caught that way, so it is closed here instead, at the trait.
/// <see cref="NoTestCarriesASliceThisBatchDoesNotImplement"/> states it on its own rather than
/// leaving it implied by the projection, because it is a decision and not an arithmetic consequence.
/// </para>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait on this class</b>, for the reason
/// <see cref="ProtocolVectorTestBindingArchitectureTests"/> gives: a cross-cutting guard hung off a
/// slice would be deferred along with that slice. Under the equality above it follows anyway --
/// this class claims no vector.
/// </para>
/// </remarks>
public sealed class IntegrationSliceTraitArchitectureTests
{
    /// <summary>
    /// The trait name a test uses to file itself under a slice.
    /// </summary>
    private const string SliceTrait = "IntegrationSlice";

    /// <summary>
    /// The trait name a test uses to claim it proves a frozen vector.
    /// </summary>
    private const string VectorTrait = "ProtocolVector";

    /// <summary>
    /// Every slice trait names one of the sixteen ids the protocol froze.
    /// </summary>
    /// <remarks>
    /// The leg that catches a <c>W2G-IS-NN</c> the v2 rename missed, a typo, and a seventeenth slice
    /// somebody invented -- each of which is a test no slice filter would ever run. The ids come
    /// from the vendored index rather than from a pattern, so the check is against what the protocol
    /// actually froze and not merely against a shape that looks right.
    /// </remarks>
    [Fact]
    public void EverySliceTraitNamesOneOfTheSixteenFrozenSlices()
    {
        HashSet<string> frozen = new(VendoredSliceIndex.FrozenSliceIds(), StringComparer.Ordinal);
        Assert.Equal(16, frozen.Count);

        TraitedTest[] tests = Scan();
        Assert.NotEmpty(tests);

        string[] offences =
        [
            .. tests
                .SelectMany(test => test.ValuesOf(SliceTrait).Select(slice => (test, slice)))
                .Where(pair => !frozen.Contains(pair.slice))
                .Select(pair => $"{pair.test.TestName} carries {pair.slice}")
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            offences.Length == 0,
            "Tests carry an " + SliceTrait + " trait naming a slice the protocol did not freeze, so "
            + "no slice filter runs them: " + string.Join("; ", offences));
    }

    /// <summary>
    /// Every test's slice traits are exactly the projection of its own vector traits onto the slices
    /// this batch implements -- the equality, checked in both directions at once.
    /// </summary>
    /// <remarks>
    /// One assertion rather than two because the two directions are two readings of one set
    /// difference, and splitting them would let a test be simultaneously short one slice and long
    /// another while each half reported only its own side. The message names which way each offence
    /// went.
    /// </remarks>
    [Fact]
    public void EverySliceTraitIsExactlyTheProjectionOfTheTestsOwnVectors()
    {
        TraitedTest[] tests = Scan();
        Assert.NotEmpty(tests);

        string[] offences = ProjectionOffences(tests);

        Assert.True(
            offences.Length == 0,
            "These tests are not filed under exactly the slices their own " + VectorTrait
            + " traits project onto, so a slice gate would run the wrong set: "
            + string.Join("; ", offences));
    }

    /// <summary>
    /// No test is filed under a slice scheduled into a later batch.
    /// </summary>
    /// <remarks>
    /// Implied by the projection above, and stated anyway: this is the
    /// <c>CV-MANUAL-CHARGING-RETURN</c> decision, and a decision that survives only as a side effect
    /// of an arithmetic bound is one nobody will find when they go looking for it. It also fails
    /// with a message about the batch rather than about a set difference.
    /// </remarks>
    [Fact]
    public void NoTestCarriesASliceThisBatchDoesNotImplement()
    {
        HashSet<string> implemented = new(SlicesThisBatchImplements(), StringComparer.Ordinal);
        // 八条重证的（FP-IS-00～07）加上批次 3 新落的两条（FP-IS-14、FP-IS-15）。数字写在这里而不是
        // 算出来，是为了让「这条线到底建了几个切片」在改的时候必须被看见一次。
        Assert.Equal(10, implemented.Count);

        string[] offences =
        [
            .. Scan()
                .SelectMany(test => test.ValuesOf(SliceTrait).Select(slice => (test, slice)))
                .Where(pair => !implemented.Contains(pair.slice))
                .Select(pair => $"{pair.test.TestName} carries {pair.slice}")
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            offences.Length == 0,
            "These tests are filed under a slice scheduled into a later batch. A slice gate for it "
            + "would select them and write a PASS over a slice nobody has finished building: "
            + string.Join("; ", offences));
    }

    /// <summary>
    /// Every slice this batch implements has at least one test filed under it.
    /// </summary>
    /// <remarks>
    /// The precondition for <c>run-w2g-g2.ps1 -Slice</c>: it refuses a slice whose filter selects
    /// nothing, on ticket 14's reasoning that a slice with no tests has nothing to certify and the
    /// honest artefact is no artefact. This says the refusal cannot fire for any of the eight this
    /// batch recertifies -- so a refusal there is a broken filter, not an expected state.
    /// </remarks>
    [Fact]
    public void EverySliceThisBatchImplementsHasAtLeastOneTest()
    {
        TraitedTest[] tests = Scan();

        string[] empty =
        [
            .. SlicesThisBatchImplements()
                .Where(slice => !tests.Any(test =>
                    test.ValuesOf(SliceTrait).Contains(slice, StringComparer.Ordinal)))
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            empty.Length == 0,
            "These slices this batch implements have no test filed under them, so ONBOARD_HMI_G2 "
            + "cannot certify them: " + string.Join(", ", empty));
    }

    /// <summary>
    /// The scanner reads method-level traits only, so a class-level slice trait fails rather than
    /// filing nothing quietly.
    /// </summary>
    [Fact]
    public void NoIntegrationSliceTraitSitsOnATypeDeclaration()
    {
        string[] claims = SliceProblems(ScanRaw().TypeLevelClaims);

        Assert.True(
            claims.Length == 0,
            "An " + SliceTrait + " trait sits on a type declaration, where this scanner does not "
            + "read it. Move it onto the test methods it was meant to file: "
            + string.Join("; ", claims));
    }

    /// <summary>
    /// No slice trait sits on a test that does not run.
    /// </summary>
    [Fact]
    public void NoIntegrationSliceTraitSitsOnATestThatDoesNotRun()
    {
        string[] claims = SliceProblems(ScanRaw().ClaimsOnTestsThatDoNotRun);

        Assert.True(
            claims.Length == 0,
            "An " + SliceTrait + " trait sits on a method that does not run as a test, so the slice "
            + "gate that selects it certifies nothing: " + string.Join("; ", claims));
    }

    /// <summary>
    /// Every slice claim lands on a declaration the scanner recognises.
    /// </summary>
    [Fact]
    public void NoIntegrationSliceTraitIsLeftUnattributed()
    {
        string[] claims = SliceProblems(ScanRaw().UnattributableClaims);

        Assert.True(
            claims.Length == 0,
            "An " + SliceTrait + " trait does not sit on any declaration this scanner recognises, "
            + "so it files nothing: " + string.Join("; ", claims));
    }

    /// <summary>
    /// The scan really covers both test projects, including the <c>net8.0-windows</c> one this
    /// headless assembly cannot reference.
    /// </summary>
    /// <remarks>
    /// A lower bound rather than an exact count, for the reason
    /// <see cref="ProtocolVectorTestBindingArchitectureTests.TheScanCoversBothTestProjects"/> gives:
    /// the failure worth guarding against is the scan going to zero on one side, not the suite
    /// gaining a test.
    /// </remarks>
    [Fact]
    public void TheScanCoversBothTestProjects()
    {
        Assert.NotEmpty(SlicedTestsUnder("SQCD.Agv.UnitTests"));
        Assert.NotEmpty(SlicedTestsUnder("SQCD.Agv.WireToGateG2Tests"));
    }

    /// <summary>
    /// Proves the projection check is not vacuous in the direction that catches a lost trait.
    /// </summary>
    /// <remarks>
    /// A real test rather than a fabricated one: the perturbed record is taken from the suite as it
    /// stands, so stripping its slices reproduces exactly what a careless edit would do. The first
    /// assertion is the load-bearing half -- it says the real suite is reported clean by the same
    /// computation that reports the damaged one, rather than by an emptier one.
    /// </remarks>
    [Fact]
    public void TheProjectionCheckCatchesATestThatLostItsSliceTrait()
    {
        TraitedTest[] tests = Scan();
        TraitedTest victim = tests.First(test => test.ValuesOf(SliceTrait).Length > 0);

        Assert.Empty(ProjectionOffences(tests));

        string[] offences = ProjectionOffences(
        [
            .. tests.Where(test => test != victim),
            victim with { Traits = [.. victim.Traits.Where(trait => !IsSlice(trait))] }
        ]);

        Assert.Contains(offences, offence => offence.Contains(victim.TestName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Proves the other direction is not vacuous: a slice trait no vector of that test justifies is
    /// reported, so the equality cannot decay into a covering.
    /// </summary>
    /// <remarks>
    /// The added slice is <c>FP-IS-04</c> against a test whose vectors do not reach it. Synthesised
    /// as a record rather than as source text on purpose -- writing the attribute out would put a
    /// literal trait claim in a file this very scanner reads, and the guard would then be measuring
    /// its own proof.
    /// </remarks>
    [Fact]
    public void TheProjectionCheckCatchesASliceTraitTheTestsVectorsDoNotJustify()
    {
        TraitedTest[] tests = Scan();
        TraitedTest victim = tests.First(test =>
            test.ValuesOf(VectorTrait).Length > 0
            && !test.ValuesOf(SliceTrait).Contains("FP-IS-04", StringComparer.Ordinal));

        Assert.Empty(ProjectionOffences(tests));

        string[] offences = ProjectionOffences(
        [
            .. tests.Where(test => test != victim),
            victim with { Traits = [.. victim.Traits, new TraitClaim(SliceTrait, "FP-IS-04")] }
        ]);

        Assert.Contains(offences, offence => offence.Contains("FP-IS-04", StringComparison.Ordinal));
    }

    /// <summary>
    /// The tests whose slice traits differ from the projection of their own vectors, each said in
    /// the direction it went.
    /// </summary>
    private static string[] ProjectionOffences(IEnumerable<TraitedTest> tests) =>
    [
        .. tests
            .Select(test => (test, Expected: ExpectedSlicesFor(test.ValuesOf(VectorTrait))))
            .Where(pair => !pair.Expected.SequenceEqual(
                pair.test.ValuesOf(SliceTrait), StringComparer.Ordinal))
            .Select(pair =>
            {
                string[] actual = pair.test.ValuesOf(SliceTrait);
                string missing = string.Join("/", pair.Expected.Except(actual, StringComparer.Ordinal));
                string extra = string.Join("/", actual.Except(pair.Expected, StringComparer.Ordinal));
                return $"{pair.test.TestName} is"
                    + (missing.Length == 0 ? string.Empty : $" missing {missing}")
                    + (missing.Length == 0 || extra.Length == 0 ? string.Empty : " and")
                    + (extra.Length == 0 ? string.Empty : $" wrongly filed under {extra}");
            })
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>
    /// The slices this batch implements whose vector list contains any of
    /// <paramref name="vectorIds"/>.
    /// </summary>
    private static string[] ExpectedSlicesFor(IEnumerable<string> vectorIds)
    {
        HashSet<string> claimed = new(vectorIds, StringComparer.Ordinal);

        return
        [
            .. ImplementedSlices()
                .Where(slice => slice.VectorIds.Any(claimed.Contains))
                .Select(slice => slice.SliceId)
                .Order(StringComparer.Ordinal)
        ];
    }

    private static string[] SlicesThisBatchImplements() =>
    [
        .. ImplementedSlices().Select(slice => slice.SliceId).Order(StringComparer.Ordinal)
    ];

    /// <summary>
    /// The batch boundary, read from
    /// <see cref="ProtocolVectorTestBindingArchitectureTests.SlicesThisLineImplements"/>
    /// rather than restated -- a second copy here is how the two guards would come to disagree about
    /// which slices this batch owes.
    /// </summary>
    private static Slice[] ImplementedSlices() =>
    [
        .. VendoredSliceIndex.Slices().Where(slice =>
            ProtocolVectorTestBindingArchitectureTests.SlicesThisLineImplements.Contains(
                slice.SliceId, StringComparer.Ordinal))
    ];

    private static TraitedTest[] SlicedTestsUnder(string project) =>
    [
        .. TestSourceTraitScanner
            .Scan(Path.Combine(VendoredSliceIndex.TestsRoot(), project), SliceTrait, VectorTrait)
            .Tests
            .Where(test => test.ValuesOf(SliceTrait).Length > 0)
    ];

    private static TraitedTest[] Scan() => [.. ScanRaw().Tests];

    private static TraitScanResult ScanRaw() => TestSourceTraitScanner.Scan(
        VendoredSliceIndex.TestsRoot(), SliceTrait, VectorTrait);

    private static bool IsSlice(TraitClaim trait) =>
        string.Equals(trait.TraitName, SliceTrait, StringComparison.Ordinal);

    /// <summary>
    /// This class's half of a problem list, rendered. The scanner is asked for both traits at once,
    /// so its three problem lists mix them; the vector guard reports the vector half.
    /// </summary>
    /// <remarks>
    /// Filtered on <see cref="ProblemClaim.TraitName"/> rather than on the rendered sentence. An
    /// earlier version searched the sentence for the trait's name, which a repository path or a
    /// trait value containing that name would have answered wrongly -- and a guard that reports the
    /// wrong problems is worse than one that reports none.
    /// </remarks>
    private static string[] SliceProblems(IEnumerable<ProblemClaim> claims) =>
    [
        .. claims
            .Where(claim => string.Equals(claim.TraitName, SliceTrait, StringComparison.Ordinal))
            .Select(claim => claim.ToString())
            .Order(StringComparer.Ordinal)
    ];
}
