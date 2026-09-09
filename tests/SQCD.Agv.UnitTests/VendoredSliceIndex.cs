using System.Text.Json;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// One row of the protocol's frozen integration-slice family.
/// </summary>
internal sealed record Slice(string SliceId, int Sequence, IReadOnlyList<string> VectorIds);

/// <summary>
/// The vendored copy of the protocol's own integration-slice index, read rather than restated.
/// </summary>
/// <remarks>
/// <para>
/// Two architecture guards need the same sixteen rows -- <see cref="ProtocolVectorTestBindingArchitectureTests"/>
/// to say every frozen vector has a named test, <see cref="IntegrationSliceTraitArchitectureTests"/>
/// to say every slice trait is the projection of those vectors onto the family. Reading the file
/// twice would be harmless; <b>parsing it two different ways would not be</b>, and a second parse is
/// how a "vector list" quietly becomes two lists that disagree at the edges. So the parse lives
/// once, here.
/// </para>
/// <para>
/// <b>This class introduces no new approved hash.</b>
/// <c>vendor/8005-agv-protocol/integration-slices/index.json</c> is the protocol's own file byte for
/// byte, listed in <c>vendor/8005-agv-protocol/manifest/release.json</c>'s <c>files</c> table, and
/// that manifest is pinned to <c>WireToGateRelease.ManifestSha256</c> -- the digest this onboard
/// puts on every envelope it sends.
/// <see cref="ProtocolVectorTestBindingArchitectureTests.TheVendoredIndexIsPinnedByTheManifestFileTable"/>
/// checks that chain.
/// </para>
/// </remarks>
internal static class VendoredSliceIndex
{
    /// <summary>
    /// Path of the vendored slice index, relative to the vendor root -- and, spelled exactly like
    /// this, its key in the manifest's own <c>files</c> table.
    /// </summary>
    public const string IndexRelativePath = "integration-slices/index.json";

    public static Slice[] Slices()
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

    public static string[] FrozenVectorIds() =>
    [
        .. Slices()
            .SelectMany(slice => slice.VectorIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    public static string[] FrozenSliceIds() =>
    [
        .. Slices().Select(slice => slice.SliceId).Order(StringComparer.Ordinal)
    ];

    public static string IndexPath() => Path.Combine(
        ProtocolIdentityArchitectureTests.VendorRoot(),
        IndexRelativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string TestsRoot() => Path.Combine(RepositoryRoot(), "tests");

    public static string RepositoryRoot() => ProtocolIdentityArchitectureTests.RepositoryRoot();
}
