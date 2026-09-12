using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SQCD.Agv.Contracts;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The machine guard on protocol identity: the nine constants this build puts on every envelope are
/// the v2 candidate's own values, the vendored copy of the protocol is that release byte for byte,
/// and the gate script's third copy of the same nine has not drifted from the assembly's.
/// </summary>
/// <remarks>
/// <para>
/// Nothing checked any of this. On 2026-09-09 the identity moved from
/// <c>WIRE_TO_GATE_MVP</c>/<c>protocolVersion 1</c> to <c>AGV_FULL_PRODUCT</c>/<c>protocolVersion
/// 2</c>, three inbound payload shapes changed with it, and the suite stayed at 151 passed
/// throughout -- the same silence that let the control server ship v2 identity over v1 payloads.
/// The values themselves were hand-transcribed from a sibling checkout that the test machine is not
/// guaranteed to have.
/// </para>
/// <para>
/// <b>The pinning introduces no new approved hash.</b> <c>manifest/release.json</c>'s SHA-256 is by
/// definition <see cref="WireToGateRelease.ManifestSha256"/> -- the value this onboard puts on every
/// envelope it sends -- so the constant vouches for the copy and the copy makes the constant
/// checkable. Every other vendored file is listed in that manifest's own <c>files</c> table with its
/// raw-byte digest, so one wire-borne value pins all seventy-two.
/// <c>vendor/8005-agv-protocol/README.md</c> is how the copy is refreshed.
/// </para>
/// </remarks>
public sealed class ProtocolIdentityArchitectureTests
{
    /// <summary>
    /// The vendored manifest is the protocol's manifest, checked against the digest this build
    /// already claims on the wire.
    /// </summary>
    [Fact]
    public void TheVendoredManifestIsTheProtocolManifestByteForByte()
    {
        Assert.Equal(
            WireToGateRelease.ManifestSha256,
            Sha256(File.ReadAllBytes(ManifestPath())));
    }

    /// <summary>
    /// Every other vendored file is pinned by that manifest's own file table, in both directions.
    /// </summary>
    /// <remarks>
    /// The reverse direction is the load-bearing half: a file dropped into this directory that the
    /// manifest does not list would otherwise be an unpinned second copy of something, which is the
    /// exact failure the vendoring discipline exists to prevent.
    /// </remarks>
    [Fact]
    public void EveryOtherVendoredFileIsPinnedByTheManifestFileTable()
    {
        Dictionary<string, string> table = ManifestFileTable();
        string[] vendored = VendoredFiles();

        Assert.Equal(71, vendored.Length);
        Assert.Equal(69, vendored.Count(path => path.StartsWith("schemas/", StringComparison.Ordinal)));

        List<string> offences = [];
        foreach (string relative in vendored)
        {
            if (!table.TryGetValue(relative, out string? expected))
            {
                offences.Add($"{relative} is vendored but the manifest does not list it");
                continue;
            }

            string actual = Sha256(File.ReadAllBytes(Path.Combine(VendorRoot(), relative)));
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                offences.Add($"{relative} is {actual}, the manifest says {expected}");
            }
        }

        Assert.True(
            offences.Count == 0,
            "The vendored protocol copy is not the protocol: " + string.Join("; ", offences));
    }

    /// <summary>
    /// The six identity constants the manifest carries are the candidate's own values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Six of ten, and the other four are named here rather than left to look covered.</b>
    /// <c>ManifestSha256</c> is <see cref="TheVendoredManifestIsTheProtocolManifestByteForByte"/>'s
    /// job -- a manifest cannot carry its own digest. <c>Tag</c> and <c>ApprovalStatus</c> are
    /// <see cref="TheTagIsSchemaLegalAndTheApprovalStatusSaysItIsACandidate"/>'s.
    /// </para>
    /// <para>
    /// <b><c>Commit</c> is the one no assertion in this assembly can bind.</b> Nothing inside the
    /// repository knows which commit of <c>8005-agv-protocol</c> a copy came from;
    /// <see cref="TheGateScriptExpectsTheSameIdentityAsTheAssembly"/> only keeps the two local
    /// copies in step with each other. The check against the protocol repository itself is
    /// <c>scripts/run-w2g-g2.ps1</c>, which resolves the commit there and refuses when the
    /// candidate is not an ancestor of its HEAD. That gate needs both repositories on the machine,
    /// which is why it is a gate and not a test.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheIdentityConstantsAreTheCandidatesOwnValues()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(ManifestPath()));
        JsonElement root = manifest.RootElement;

        Assert.Equal(WireToGateRelease.ProtocolVersion, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(WireToGateRelease.ProfileId, root.GetProperty("profileId").GetString());
        Assert.Equal(WireToGateRelease.ReleaseVersion, root.GetProperty("releaseVersion").GetString());
        Assert.Equal(WireToGateRelease.Repository, root.GetProperty("repository").GetString());
        Assert.Equal(
            WireToGateRelease.SchemaBundleSha256,
            root.GetProperty("schemaBundleSha256").GetString());
        Assert.Equal(WireToGateRelease.VectorsSha256, root.GetProperty("vectorsSha256").GetString());
    }

    /// <summary>
    /// The wire identity carries exactly the nine names the frozen schema requires, and the approval
    /// status is not one of them.
    /// </summary>
    /// <remarks>
    /// <c>$defs/ProtocolReleaseIdentity</c> is <c>additionalProperties: false</c> with all nine
    /// required, so a tenth property would make <c>SessionHello</c> schema-invalid and a missing one
    /// would too -- and neither end validates against the schemas at runtime, so nothing else would
    /// say so. <see cref="WireToGateRelease.ApprovalStatus"/> is a fact about this build rather than
    /// a field of the identity, and this is what keeps it out of the payload.
    /// </remarks>
    [Fact]
    public void TheWireIdentityCarriesExactlyTheNineFrozenNames()
    {
        using JsonDocument types = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(VendorRoot(), "schemas", "common", "types.schema.json")));
        string[] required =
        [
            .. types.RootElement.GetProperty("$defs").GetProperty("ProtocolReleaseIdentity")
                .GetProperty("required").EnumerateArray().Select(name => name.GetString()!)
                .Order(StringComparer.Ordinal)
        ];

        using JsonDocument identity = JsonDocument.Parse(
            WireToGateProtocolSerializer.Serialize(WireToGateProtocolSerializer.Create(
                "SessionHello",
                "00000000-0000-4000-8000-000000000001",
                null,
                "AGV-001",
                null,
                new DateTimeOffset(2026, 9, 9, 9, 0, 0, TimeSpan.Zero),
                WireToGateRelease.Identity)));
        string[] actual =
        [
            .. identity.RootElement.GetProperty("payload").EnumerateObject()
                .Select(property => property.Name).Order(StringComparer.Ordinal)
        ];

        Assert.Equal(required, actual);
        Assert.DoesNotContain("approvalStatus", actual, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The tag is schema-legal, and the approval status says it names an unapproved candidate.
    /// </summary>
    /// <remarks>
    /// The two are checked together because either alone is a lie. <c>protocol-v1.0.0</c> has not
    /// been cut in the protocol repository -- section 6.6 item 6 of the full-product scope
    /// specification wants the product owner's attestation first (one owner since the protocol's
    /// governance changed on 2026-09-08; the specification still says two) -- but the schema requires a
    /// non-empty <c>^protocol-v</c> tag, so the name is carried and
    /// <see cref="WireToGateRelease.ApprovalStatus"/> carries the truth about it. This test is what
    /// stops the status being quietly promoted to <c>APPROVED_RELEASE</c> without the tag existing.
    /// </remarks>
    [Fact]
    public void TheTagIsSchemaLegalAndTheApprovalStatusSaysItIsACandidate()
    {
        using JsonDocument types = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(VendorRoot(), "schemas", "common", "types.schema.json")));
        JsonElement tag = types.RootElement.GetProperty("$defs").GetProperty("ProtocolReleaseIdentity")
            .GetProperty("properties").GetProperty("tag");

        Assert.Matches(tag.GetProperty("pattern").GetString()!, WireToGateRelease.Tag);
        Assert.True(WireToGateRelease.Tag.Length >= tag.GetProperty("minLength").GetInt32());
        Assert.Equal("SUPERSEDING_CANDIDATE", WireToGateRelease.ApprovalStatus);
    }

    /// <summary>
    /// The gate script's copy of the identity is the assembly's, field by field.
    /// </summary>
    /// <remarks>
    /// <c>scripts/run-w2g-g2.ps1</c> keeps its own <c>$expected</c> table because it checks the
    /// assembly against something, and checking the assembly against itself proves nothing. That
    /// makes it a second copy, and a second copy nothing compares is how the two drift: a run of the
    /// gate would then certify an identity the build does not carry.
    /// </remarks>
    [Fact]
    public void TheGateScriptExpectsTheSameIdentityAsTheAssembly()
    {
        string script = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "run-w2g-g2.ps1"));

        Assert.Equal(WireToGateRelease.ProtocolVersion.ToString(CultureInfo.InvariantCulture), ScriptValue(script, "ProtocolVersion"));
        Assert.Equal(WireToGateRelease.ProfileId, ScriptValue(script, "ProfileId"));
        Assert.Equal(WireToGateRelease.ReleaseVersion, ScriptValue(script, "ReleaseVersion"));
        Assert.Equal(WireToGateRelease.Repository, ScriptValue(script, "Repository"));
        Assert.Equal(WireToGateRelease.Tag, ScriptValue(script, "Tag"));
        Assert.Equal(WireToGateRelease.Commit, ScriptValue(script, "Commit"));
        Assert.Equal(WireToGateRelease.ManifestSha256, ScriptValue(script, "ManifestSha256"));
        Assert.Equal(WireToGateRelease.SchemaBundleSha256, ScriptValue(script, "SchemaBundleSha256"));
        Assert.Equal(WireToGateRelease.VectorsSha256, ScriptValue(script, "VectorsSha256"));
        Assert.Equal(WireToGateRelease.ApprovalStatus, ScriptValue(script, "ApprovalStatus"));
    }

    /// <summary>
    /// Proves the byte pinning is not vacuous, by running it over a copy with one character added.
    /// </summary>
    /// <remarks>
    /// A whitespace character rather than a semantic edit: if even that is reported, so is anything
    /// larger, and it says the check is on the bytes rather than on a parse of them.
    /// </remarks>
    [Fact]
    public void TheBytePinningReportsASingleChangedCharacter()
    {
        byte[] manifest = File.ReadAllBytes(ManifestPath());
        Assert.Equal(WireToGateRelease.ManifestSha256, Sha256(manifest));

        byte[] tampered = [.. manifest, (byte)' '];

        Assert.NotEqual(WireToGateRelease.ManifestSha256, Sha256(tampered));
    }

    private static string ScriptValue(string script, string name)
    {
        Match match = Regex.Match(
            script,
            @"^\s*" + Regex.Escape(name) + @"\s*=\s*'?(?<value>[^'\r\n]+?)'?\s*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(
            match.Success,
            $"scripts/run-w2g-g2.ps1 no longer declares '{name}' in its $expected table. This test "
            + "anchors on that table; update the anchor rather than deleting the comparison.");
        return match.Groups["value"].Value;
    }

    private static Dictionary<string, string> ManifestFileTable()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(ManifestPath()));
        return manifest.RootElement.GetProperty("files").EnumerateArray().ToDictionary(
            entry => entry.GetProperty("path").GetString()!,
            entry => entry.GetProperty("sha256").GetString()!,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The vendored files the manifest is expected to account for: everything under the vendor root
    /// except the manifest itself, which cannot contain its own digest, and the README, which is
    /// ours rather than the protocol's.
    /// </summary>
    private static string[] VendoredFiles() =>
    [
        .. Directory.EnumerateFiles(VendorRoot(), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(VendorRoot(), path).Replace('\\', '/'))
            .Where(path => path is not ("manifest/release.json" or "README.md"))
            .Order(StringComparer.Ordinal)
    ];

    internal static string ManifestPath() =>
        Path.Combine(VendorRoot(), "manifest", "release.json");

    internal static string VendorRoot() =>
        Path.Combine(RepositoryRoot(), "vendor", "8005-agv-protocol");

    internal static string RepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SQCD_8005AGV.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root from the test process directories.");
    }

    /// <summary>
    /// The same digest the product code puts on the wire, not a second implementation of it.
    /// </summary>
    private static string Sha256(byte[] bytes) => WireToGateProtocolSerializer.ComputeSha256(bytes);
}
