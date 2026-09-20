using System.Text.Json;
using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The machine guard on the message surface: every one of the sixty-three message types protocol v2
/// froze is either named in <c>src/</c> or pinned here as unimplemented with its reason, and none of
/// the eleven denylisted types has come back.
/// </summary>
/// <remarks>
/// <para>
/// "The onboard implements its half of the message surface" was a sentence somebody counted by hand.
/// v2 took the surface from 54 message types to 63, and on the day the identity switched, eleven
/// of the sixty-three appeared nowhere in <c>src/</c> -- nine of them the messages v2 added, two of
/// them result halves that had been unimplemented since v1 and had ridden through every green gate.
/// Nothing said so, because nothing was comparing the two lists.
/// </para>
/// <para>
/// <b>The list is not copied here.</b> <c>vendor/8005-agv-protocol/manifest/release.json</c> is the
/// protocol's own manifest byte for byte, and
/// <see cref="ProtocolIdentityArchitectureTests.TheVendoredManifestIsTheProtocolManifestByteForByte"/>
/// binds it to <c>WireToGateRelease.ManifestSha256</c> -- the digest this onboard already puts on
/// every envelope. So the message table arrives with the same guarantee the identity has, and no
/// second approved hash was invented to hold it.
/// </para>
/// <para>
/// <b>A message counts as named when <c>src/</c> contains its type as a quoted string.</b> That is
/// how the onboard refers to a message type at all: the receive loop dispatches on
/// <c>case "SlotOperationCommand":</c>, the send helpers take <c>"OperationResult"</c> as their
/// first argument. Matching the bare word instead would count a property or column whose name merely
/// contains the type as an implementation of it -- which is exactly the false green this class
/// exists to avoid.
/// </para>
/// <para>
/// <b>What it does not check.</b> Only that the name appears. A v1-shaped payload sent under a v2
/// message type is an implementation as far as this class is concerned; shape is
/// <c>ProtocolPayloadShapeArchitectureTests</c>'s job, and the two do not overlap or impersonate
/// each other.
/// </para>
/// </remarks>
public sealed class ProtocolMessageSurfaceArchitectureTests
{
    /// <summary>
    /// The frozen message types with no implementation in <c>src/</c>, each with why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two kinds of entry, and they are not the same kind of debt.</b> Nine are messages v2
    /// added, and each belongs to a slice section 7.2 of the full-product scope specification
    /// schedules into a later batch; those empty as their batches land. Until onboard-hmi#107 one
    /// more predated v2 and was pinned as a finding rather than a schedule.
    /// </para>
    /// <para>
    /// That one was checked rather than assumed on 2026-09-09, and it was the result half of a pair
    /// whose request half <i>was</i> named: <c>HardwareRecoveryRecordResult</c>.
    /// <c>HardwareRecoveryRecordSubmitted</c> appeared only as a <c>case</c> label in the receive
    /// loop even though it is an <c>O_TO_C</c> message, so the onboard never submitted a record and
    /// had no result to read. That is exactly the hole
    /// <see cref="NoOnboardToServerMessageTypeIsDispatchedByTheReceiveLoopUnlessPinned"/> exists to
    /// keep visible: "named in src/" cannot tell which side of the wire the name is on. #107 gave the
    /// record a send path -- the device half of a forced mechanical recovery -- and the result a reader.
    /// </para>
    /// <para>
    /// <c>ForcedMechanicalRecoveryResult</c> was the second such finding until ticket 21 closed it
    /// on 2026-09-09. Its command half used to fall into the same general branch, be evaluated
    /// against <c>WireToGateRecoverySafetyFacts.Unknown</c>, and be blocked, so no path executed it
    /// and no path reported a result; it now has a typed command, a handler and a send path,
    /// because <c>CV-FORCED-MECHANICAL-RECOVERY</c> is one of <c>FP-IS-07</c>'s vectors and the
    /// slice cannot be certified while half of it is a log line.
    /// </para>
    /// <para>
    /// <b>The control server's pinned set is not this one.</b> Its
    /// <c>CapabilitySnapshotRequested</c>, <c>SafetyStateSnapshotRequested</c> and
    /// <c>SublotRejected</c> entries do not transfer: this end names all three, and
    /// <c>SublotRejected</c> is fully parsed and validated here. A slice being unprovable needs both
    /// ends; a message being unimplemented does not.
    /// </para>
    /// <para>
    /// <b>Pinning is not waiving.</b> The comparison is exact in both directions: implementing a
    /// message without deleting its line here fails, and a message quietly losing its last mention
    /// in <c>src/</c> fails too. <b>Keep the field when it empties</b> -- empty is itself the
    /// assertion.
    /// </para>
    /// </remarks>
    private static readonly SortedDictionary<string, string> MessagesWithoutAnImplementation =
        new(StringComparer.Ordinal)
        {
            ["DemandSelectionRequested"] = "FP-IS-09, batch 11",
            ["DemandSelectionResult"] = "FP-IS-09, batch 11",
            ["ManualStationClearanceConfirmationRequested"] = "FP-IS-13, batch 8",
            ["ManualStationClearanceConfirmationResult"] = "FP-IS-13, batch 8",
            ["UnableToChargeFieldConfirmationRequested"] = "FP-IS-13, batch 8",
            ["UnableToChargeFieldConfirmationResult"] = "FP-IS-13, batch 8"
        };

    /// <summary>
    /// The <c>O_TO_C</c> message types the receive loop still dispatches on, each pinned because it
    /// predates v2 rather than because it is correct.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>case</c> label for an <c>O_TO_C</c> type is dead in the normal case and wrong in the
    /// abnormal one: the control server never sends these, and if a peer ever did, the right answer
    /// is <c>UNKNOWN_MESSAGE_TYPE</c> rather than accepting it as a recovery command to be logged
    /// and blocked. Seven of the nine are messages this onboard also <i>sends</i>, so the label is
    /// its own message coming back; the other two, <c>HardwareRecoveryRecordSubmitted</c> and
    /// <c>SlotOperationCommandRejected</c>, are messages the onboard is supposed to originate and
    /// only ever parses.
    /// </para>
    /// <para>
    /// <b>None of this is a v2 change.</b> Every one of the nine was already <c>O_TO_C</c> in
    /// <c>protocol-v0.1.1</c> -- checked against that tag's manifest, where no message type changed
    /// direction between v1 and v2. They are v1 defects the v2 switch made visible, not work the
    /// switch created, so they are pinned here rather than fixed under a ticket whose boundary is
    /// protocol identity and message shape. Deleting a label changes what the onboard answers a
    /// stray message with, which is a behaviour change that wants its own ticket.
    /// </para>
    /// <para>
    /// The set is compared exactly, so this cannot grow quietly: adding a tenth fails until somebody
    /// writes down why.
    /// </para>
    /// </remarks>
    private static readonly string[] OnboardToServerTypesDispatchedInbound =
    [
        "ExceptionRecoverySessionRequested",
        "HardwareRecoveryRecordSubmitted",
        "LoadCancellationStartRequested",
        "LoadCompensationRequested",
        "LoadCorrectionRequested",
        "ManualChargingReturnToServiceRequested",
        "RecoveryActionSubmitted",
        "SafetyStateChanged",
        "SlotOperationCommandRejected"
    ];

    /// <summary>
    /// The parse of the manifest, checked against the shape v2 froze.
    /// </summary>
    /// <remarks>
    /// Without this, every assertion below could pass over an empty parse: an empty message table
    /// makes "no denylisted type is implemented" trivially true and turns the pinned set into a list
    /// of names nothing compares. The two counts differ from each other, so a parse that read the
    /// wrong property is reported here.
    /// </remarks>
    [Fact]
    public void TheManifestParsesIntoSixtyThreeMessagesAndElevenDenylistedTypes()
    {
        Assert.Equal(63, FrozenMessageTypes().Length);
        Assert.Equal(11, DenylistedMessageTypes().Length);
        Assert.Empty(FrozenMessageTypes().Intersect(DenylistedMessageTypes(), StringComparer.Ordinal));
    }

    /// <summary>
    /// Every frozen message type is implemented or pinned, and the comparison fails both ways.
    /// </summary>
    [Fact]
    public void EveryFrozenMessageTypeIsNamedInTheOnboardOrPinnedAsUnimplemented()
    {
        string source = OnboardSource();

        string[] unimplemented =
        [
            .. FrozenMessageTypes().Where(type => !IsNamedIn(source, type)).Order(StringComparer.Ordinal)
        ];
        string[] unimplementedAndUnpinned =
        [
            .. unimplemented
                .Except(MessagesWithoutAnImplementation.Keys, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
        string[] pinnedInVain =
        [
            .. MessagesWithoutAnImplementation.Keys
                .Except(unimplemented, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            unimplementedAndUnpinned.Length == 0,
            "These frozen message types are named nowhere in src/ and are not pinned. Either the "
            + "implementation was deleted, or the surface grew and nobody followed it: "
            + string.Join(", ", unimplementedAndUnpinned));

        Assert.True(
            pinnedInVain.Length == 0,
            "These message types are pinned as unimplemented but src/ now names them -- delete "
            + "their line from " + nameof(MessagesWithoutAnImplementation) + ", or the pinned name "
            + "is not a frozen message type at all and is misspelled: "
            + string.Join(", ", pinnedInVain));
    }

    /// <summary>
    /// None of the eleven types v2 denylisted is spoken by this onboard.
    /// </summary>
    /// <remarks>
    /// The denylist is the set of v1 message types the profile removed rather than renamed. An
    /// onboard that still emits one is not speaking a slightly older protocol; it is speaking a
    /// message its peer will refuse with <c>PROFILE_MESSAGE_NOT_ALLOWED</c>.
    /// </remarks>
    [Fact]
    public void NoDenylistedMessageTypeIsNamedInTheOnboard()
    {
        string source = OnboardSource();

        string[] resurrected =
        [
            .. DenylistedMessageTypes().Where(type => IsNamedIn(source, type)).Order(StringComparer.Ordinal)
        ];

        Assert.True(
            resurrected.Length == 0,
            "src/ names message types the profile denylisted: " + string.Join(", ", resurrected));
    }

    /// <summary>
    /// The receive loop dispatches on no <c>O_TO_C</c> message type it has not pinned.
    /// </summary>
    /// <remarks>
    /// The half of "implemented" that <see cref="IsNamedIn"/> cannot see. A quoted message type
    /// says the onboard knows the name, not which direction it travels, so a message the onboard is
    /// supposed to send can sit in the inbound dispatch and count as implemented. The direction
    /// comes from the manifest rather than from a second list here.
    /// </remarks>
    [Fact]
    public void NoOnboardToServerMessageTypeIsDispatchedByTheReceiveLoopUnlessPinned()
    {
        string receiveLoop = File.ReadAllText(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(),
            "src",
            "SQCD.Agv.Infrastructure",
            "WireToGateSessionClient.cs"));

        string[] dispatched =
        [
            .. Regex.Matches(receiveLoop, "case\\s+\"(?<type>[A-Za-z]+)\"\\s*:", RegexOptions.CultureInvariant)
                .Select(match => match.Groups["type"].Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
        Assert.NotEmpty(dispatched);

        Dictionary<string, string> directions = MessageDirections();
        string[] onboardToServer =
        [
            .. dispatched
                .Where(type => directions.TryGetValue(type, out string? direction)
                    && string.Equals(direction, "O_TO_C", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
        ];

        Assert.Equal(
            OnboardToServerTypesDispatchedInbound.Order(StringComparer.Ordinal).ToArray(),
            onboardToServer);
    }

    /// <summary>
    /// Proves the implementation check is not vacuous, by running it over a source text that lost a
    /// message the onboard really does speak.
    /// </summary>
    /// <remarks>
    /// A real message rather than a fabricated one: <c>OperationResult</c> is sent by name today, so
    /// removing its literal reproduces exactly what deleting that send path would do. The second
    /// assertion is the load-bearing half -- it says the message is reported <i>because</i> nothing
    /// names it, not because somebody had pinned it.
    /// </remarks>
    [Fact]
    public void TheImplementationCheckReportsAMessageThatLosesItsLastMention()
    {
        const string message = "OperationResult";
        string source = OnboardSource();
        Assert.True(IsNamedIn(source, message), "Precondition: src/ names " + message + ".");
        Assert.DoesNotContain(message, MessagesWithoutAnImplementation.Keys, StringComparer.Ordinal);

        string without = source.Replace(
            $"\"{message}\"", "\"MessageTypeThatDoesNotExist\"", StringComparison.Ordinal);

        Assert.False(IsNamedIn(without, message));
    }

    /// <summary>
    /// A message type counts as named when the source contains it as a quoted string.
    /// </summary>
    private static bool IsNamedIn(string source, string messageType) =>
        source.Contains($"\"{messageType}\"", StringComparison.Ordinal);

    /// <summary>
    /// Read once. The manifest is 474 KB and <c>src/</c> is the whole onboard; parsing and reading
    /// them per assertion made four passes over both in a single test class for no gain.
    /// </summary>
    private static readonly Lazy<string> Source = new(ReadOnboardSource);

    private static readonly Lazy<string[]> FrozenMessages =
        new(() => ManifestNames("messages", element => element.EnumerateObject().Select(m => m.Name)));

    private static readonly Lazy<string[]> Denylisted =
        new(() => ManifestNames(
            "denylistedMessageTypes", element => element.EnumerateArray().Select(t => t.GetString()!)));

    private static string OnboardSource() => Source.Value;

    private static string ReadOnboardSource()
    {
        string sourceRoot = Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(), "src");
        string[] files =
        [
            .. Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains(
                        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)
                    && !path.Contains(
                        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
        ];

        Assert.NotEmpty(files);

        return string.Join('\n', files.Select(File.ReadAllText));
    }

    private static string[] FrozenMessageTypes() => FrozenMessages.Value;

    private static Dictionary<string, string> MessageDirections()
    {
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllBytes(ProtocolIdentityArchitectureTests.ManifestPath()));

        return manifest.RootElement.GetProperty("messages").EnumerateObject().ToDictionary(
            message => message.Name,
            message => message.Value.GetProperty("direction").GetString()!,
            StringComparer.Ordinal);
    }

    private static string[] DenylistedMessageTypes() => Denylisted.Value;

    private static string[] ManifestNames(
        string property,
        Func<JsonElement, IEnumerable<string>> read)
    {
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllBytes(ProtocolIdentityArchitectureTests.ManifestPath()));

        return [.. read(manifest.RootElement.GetProperty(property)).Order(StringComparer.Ordinal)];
    }
}
