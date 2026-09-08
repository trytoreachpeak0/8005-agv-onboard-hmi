using System.Net;
using System.Text;
using System.Text.Json;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The machine guard on the shape of what this onboard actually puts on the wire: for every message
/// it sends, the payload's property names are exactly the ones the frozen v2 schema requires, and
/// every field the schema constrains to an enumeration carries one of its values.
/// </summary>
/// <remarks>
/// <para>
/// Nothing checked this, and the v2 switch is how it showed. Moving the identity to
/// <c>protocolVersion 2</c> / <c>AGV_FULL_PRODUCT</c> left the suite at 151 passed while the onboard
/// omitted <c>CapabilitySnapshot</c>'s newly-required <c>activeSlotConfigurationFingerprint</c>,
/// carried an <c>UpcomingStopPlanSnapshot</c> record with a top-level <c>demandId</c> v2 forbids and
/// three leg properties it requires, had no <c>activePurpose</c> on
/// <c>VehicleBusinessStateSnapshot</c>, and validated inbound <c>stopRole</c> against
/// <c>PICKUP</c>/<c>GATE</c> and <c>legType</c> against <c>TO_PICKUP</c>/<c>TO_GATE</c> -- values v2
/// does not have. The control server ships v2 today, so the last two would have refused every
/// worklist and plan it sends, with <c>PROTOCOL_SCHEMA_INVALID</c>.
/// </para>
/// <para>
/// <b>The payloads come from the real client, not from fixtures written beside the assertion.</b> A
/// hand-built sample proves the sample conforms. <see cref="EveryMessageTheClientSendsMatchesItsFrozenSchema"/>
/// drives a real session against <see cref="FakeControlServer"/> over a loopback socket and reads
/// the exact bytes the client wrote. The inbound direction is covered too: <b>every</b> envelope
/// the fake puts on the wire -- handshake, acknowledgements and recovery responses, not only the
/// three journey snapshots -- is checked against the same schemas, so the double cannot drift into
/// a shape the real server would never send and quietly keep the client's parsing honest against a
/// fiction.
/// </para>
/// <para>
/// <b>What this checks, and what it does not.</b> It reads the frozen schema and compares the
/// emitted object's property-name set against <c>required</c>, and checks enum membership. It is
/// <b>not</b> a JSON Schema validator: string patterns, numeric bounds, formats, <c>$ref</c> chains
/// beyond one hop into <c>common/types.schema.json</c>, and cross-field rules are all unchecked.
/// Those two properties are chosen because they are the two that broke, and because these payload
/// objects are <c>additionalProperties: false</c> with every property required -- which makes
/// name-set equality exactly structural conformance for them, rather than an approximation of it.
/// Building a real validator is a dependency decision this repository has not taken; section 6.6
/// item 1 of the full-product scope specification puts schema authorship on the protocol side.
/// </para>
/// <para>
/// <b>The field lists are not copied here.</b> <c>vendor/8005-agv-protocol/schemas/</c> is the
/// protocol's schema tree file for file, and every one of those files is pinned by the vendored
/// manifest's own <c>files</c> table, which is in turn pinned by
/// <c>WireToGateRelease.ManifestSha256</c> -- the digest on every envelope this onboard sends. No
/// new approved hash was invented to hold it. Refreshing the copy is
/// <c>vendor/8005-agv-protocol/README.md</c>.
/// </para>
/// </remarks>
public sealed class ProtocolPayloadShapeArchitectureTests
{
    private const string CredentialVariable = "W2G_SHAPE_TEST_CREDENTIAL";

    static ProtocolPayloadShapeArchitectureTests()
    {
        Environment.SetEnvironmentVariable(CredentialVariable, "shape-test-credential");
    }

    /// <summary>
    /// The <c>O_TO_C</c> message types this test does not drive, each with why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two kinds of entry.</b> Two are send paths a single happy session cannot reach:
    /// <c>LoadCancellationStartRequested</c> blocks on a <c>LoadCancellationAuthorization</c> the
    /// fake does not synthesise, and the client validates that response against the request before
    /// returning, so answering it needs a real authorisation model in the double rather than a stub;
    /// <c>ProtocolProblem</c> is emitted only when the client rejects an inbound message, which is a
    /// failure path with its own tests
    /// (<c>WireToGateG2Tests.SameJourneyRevisionWithDifferentContentFailsClosed</c>) and would end
    /// the session this test is still using.
    /// </para>
    /// <para>
    /// The other three have <b>no send path at all</b>, which is a finding rather than a limit of
    /// this test. <c>DurableAck</c> is <c>BIDIRECTIONAL</c> in the manifest but only ever arrives
    /// here -- the server acknowledges the onboard's durable messages, never the reverse.
    /// <c>HardwareRecoveryRecordSubmitted</c> and <c>SlotOperationCommandRejected</c> are
    /// <c>O_TO_C</c> messages this onboard is supposed to originate and only ever parses; both are
    /// pinned for that reason in
    /// <c>ProtocolMessageSurfaceArchitectureTests.OnboardToServerTypesDispatchedInbound</c>, and
    /// both predate v2.
    /// </para>
    /// <para>
    /// The nine <c>O_TO_C</c> types with no mention in <c>src/</c> at all are not listed here: they
    /// are <c>ProtocolMessageSurfaceArchitectureTests.MessagesWithoutAnImplementation</c>'s
    /// business, and duplicating them would create a second list to keep in step.
    /// </para>
    /// </remarks>
    private static readonly SortedDictionary<string, string> SendPathsNotDriven =
        new(StringComparer.Ordinal)
        {
            ["DurableAck"] =
                "BIDIRECTIONAL in the manifest, but this end only receives it; the onboard has no send path",
            ["HardwareRecoveryRecordSubmitted"] =
                "O_TO_C with no send path; named only as an inbound case label, which predates v2",
            ["LoadCancellationStartRequested"] =
                "blocks on a LoadCancellationAuthorization the fake server does not synthesise",
            ["ProtocolProblem"] =
                "emitted only when the client rejects an inbound message, which ends the session",
            ["SlotOperationCommandRejected"] =
                "O_TO_C with no send path; named only as an inbound case label, which predates v2"
        };

    /// <summary>
    /// Every message the client sends during a full session, checked against its own schema.
    /// </summary>
    [Fact]
    public async Task EveryMessageTheClientSendsMatchesItsFrozenSchema()
    {
        FakeControlServer server = await DriveAFullSessionAsync();
        await using (server)
        {
            string[] sent =
            [
                .. server.ReceivedEnvelopes.Select(envelope => envelope.WireLine)
            ];
            Assert.NotEmpty(sent);

            string[] offences = [.. sent.SelectMany(Offences)];

            Assert.True(
                offences.Length == 0,
                "The onboard sends payloads its own frozen schemas reject: "
                + string.Join("; ", offences));
        }
    }

    /// <summary>
    /// The session above drove every implemented <c>O_TO_C</c> message type except the five pinned.
    /// </summary>
    /// <remarks>
    /// Without this, the check above degrades silently: a send path that stops being exercised stops
    /// being checked, and the test still passes on whatever is left. The expected set is derived
    /// from the vendored manifest's directions rather than typed out here, so a message v2 adds to
    /// the onboard's side arrives as a failure with its own name in it.
    /// </remarks>
    [Fact]
    public async Task TheSessionDrivesEveryImplementedOnboardToServerMessageType()
    {
        FakeControlServer server = await DriveAFullSessionAsync();
        await using (server)
        {
            string[] driven =
            [
                .. server.ReceivedEnvelopes.Select(envelope => envelope.MessageType)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            ];

            string source = OnboardSource();
            string[] expected =
            [
                .. OnboardToServerMessageTypes()
                    .Where(type => source.Contains($"\"{type}\"", StringComparison.Ordinal))
                    .Except(SendPathsNotDriven.Keys, StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
            ];

            string[] undriven = [.. expected.Except(driven, StringComparer.Ordinal)];
            Assert.True(
                undriven.Length == 0,
                "These onboard-to-server message types are implemented but this session no longer "
                + "drives them, so their payload shape is unchecked: " + string.Join(", ", undriven));

            string[] pinnedInVain =
            [
                .. SendPathsNotDriven.Keys.Intersect(driven, StringComparer.Ordinal)
            ];
            Assert.True(
                pinnedInVain.Length == 0,
                "These types are pinned as undriven but the session drove them -- delete their line "
                + "from " + nameof(SendPathsNotDriven) + ": " + string.Join(", ", pinnedInVain));
        }
    }

    /// <summary>
    /// The fake server's own C_TO_O envelopes match the frozen schemas too.
    /// </summary>
    /// <remarks>
    /// The client parses inbound payloads with <c>JsonUnmappedMemberHandling.Disallow</c>, so its
    /// records and the double's literals have to agree -- but they can agree on something the real
    /// server would never send, and then the whole G2 suite is green against a fiction. This is what
    /// stops that: the double is held to the same schemas the real peer is.
    /// </remarks>
    [Fact]
    public async Task EveryMessageTheFakeServerSendsMatchesItsFrozenSchema()
    {
        FakeControlServer server = await DriveAFullSessionAsync();
        await using (server)
        {
            string[] sent = [.. server.SentEnvelopes.Select(envelope => envelope.WireLine)];
            Assert.NotEmpty(sent);

            // Not just the three journey snapshots: the handshake, the acknowledgements and the
            // recovery responses are on the wire too, and drift there is just as invisible.
            Assert.Contains("SessionAccepted", server.SentEnvelopes.Select(e => e.MessageType));
            Assert.Contains("SessionReadiness", server.SentEnvelopes.Select(e => e.MessageType));
            Assert.Contains("DurableAck", server.SentEnvelopes.Select(e => e.MessageType));

            string[] offences = [.. sent.SelectMany(Offences)];

            Assert.True(
                offences.Length == 0,
                "The fake control server sends payloads the frozen schemas reject, so the client is "
                + "being parsed against a fiction: " + string.Join("; ", offences));
        }
    }

    /// <summary>
    /// Proves the shape check is not vacuous, by running it over the payload shapes v1 sent.
    /// </summary>
    /// <remarks>
    /// The real defects, reproduced exactly: a <c>CapabilitySnapshot</c> without the fingerprint v2
    /// added, and an <c>UpcomingStopPlanSnapshot</c> with a top-level <c>demandId</c> the payload
    /// forbids, a leg missing the three fields v2 added, and <c>legType: "TO_GATE"</c>. If the check
    /// reported nothing here it would have reported nothing on 2026-09-09 either.
    /// </remarks>
    [Fact]
    public void TheShapeCheckReportsThePayloadShapesVersionOneSent()
    {
        const string v1Capability = """
            {
              "capabilityVersion": 1,
              "observedAt": "2026-09-09T09:00:00+00:00",
              "slotModelVersion": "eight-slot-v1",
              "activeSlotConfigurationVersion": "eight-slot-modbus-v1",
              "slotStates": [],
              "supportsBatchUnlock": false,
              "onboardJournalFormatVersion": 1
            }
            """;
        const string v1Plan = """
            {
              "planRevision": 7,
              "demandId": "11111111-1111-4111-8111-111111111111",
              "legs": [
                {
                  "movementLegId": "22222222-2222-4222-8222-222222222222",
                  "legType": "TO_GATE",
                  "sequence": 1,
                  "stationId": "ST-01",
                  "mapId": "MAP-01",
                  "state": "PLANNED"
                }
              ]
            }
            """;

        string[] capability = [.. PayloadOffences("CapabilitySnapshot", v1Capability)];
        string[] plan = [.. PayloadOffences("UpcomingStopPlanSnapshot", v1Plan)];

        Assert.Contains(
            capability,
            offence => offence.Contains("activeSlotConfigurationFingerprint", StringComparison.Ordinal));
        Assert.Contains(plan, offence => offence.Contains("demandId", StringComparison.Ordinal));
        Assert.Contains(
            plan,
            offence => offence.Contains("stopPurposeCategory", StringComparison.Ordinal));
        Assert.Contains(plan, offence => offence.Contains("TO_GATE", StringComparison.Ordinal));
    }

    /// <summary>
    /// Runs one session that exercises every implemented onboard-to-server send path, and returns
    /// the fake server holding the exact bytes both ends wrote.
    /// </summary>
    /// <remarks>
    /// The caller disposes the server. The client is disposed here, because a send after disposal is
    /// not what any of these assertions are about.
    /// </remarks>
    private static async Task<FakeControlServer> DriveAFullSessionAsync()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            SendJourneySnapshotsAfterRecovery = true,
            RespondToRecoveryRequests = true,
            RespondToManualChargingReturnToServiceRequests = true,
            ManualChargingReturnToServiceVehicleBusinessStateRevision = 4
        };

        try
        {
            FakeIoModuleClient io = new();
            string journalPath = Path.Combine(
                Path.GetTempPath(), $"w2g-shape-{Guid.NewGuid():N}.db");
            await using WireToGateSessionClient client = new(
                new WireToGateSessionOptions(
                    "127.0.0.1",
                    server.Port,
                    "AGV-8005-01",
                    Guid.NewGuid().ToString("D"),
                    new string('a', 40),
                    CredentialVariable,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                    1,
                    1,
                    "eight-slot-v1",
                    "eight-slot-modbus-v1",
                    false),
                io,
                new SqliteWireToGateJournal(journalPath),
                new SystemClock(),
                new AlwaysStopped(),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(500));

            // SessionHello, CapabilitySnapshot, SafetyStateSnapshot, RecoveryStateReport, and the
            // three SnapshotAppliedAcks the journey snapshots draw out.
            await client.ConnectAndRecoverAsync(token);
            await WaitUntilAsync(
                () => server.Received.Count(item => item.MessageType == "SnapshotAppliedAck") == 3,
                token);

            await client.SendHeartbeatAsync(token);

            const string demandId = "11111111-1111-4111-8111-111111111111";
            const string sessionId = "11111111-1111-4111-8111-111111111112";
            const string attemptId = "33333333-3333-4333-8333-333333333333";
            const string checkId = "44444444-4444-4444-8444-444444444444";
            const string legId = "55555555-5555-4555-8555-555555555555";
            WireToGateOperatorContextPayload operatorContext = new(
                "operator-001", "BADGE", DateTimeOffset.UtcNow);

            await client.SendSublotSubmittedAsync(
                demandId, sessionId, "ST-01", 1, "SUBLOT-001", "SCANNER",
                operatorContext.OperatorId, operatorContext.VerificationMethod,
                operatorContext.VerifiedAt, token);
            await client.SendOperationProgressAsync(
                attemptId, "UNLOCKING", [1], [], DateTimeOffset.UtcNow, token);
            await client.SendOperationResultAsync(
                $"operation-result:{attemptId}",
                attemptId,
                new WireToGateOperationResultPayload(
                    demandId, attemptId, "LOAD", "COMPLETED",
                    [SlotResult(1, "COMPLETED", "OCCUPIED")],
                    DateTimeOffset.UtcNow, "NONE", Sha256Of("operation-result")),
                token);
            await client.SendPreDepartureSafetyCheckResultAsync(
                checkId, "SAFE", DateTimeOffset.UtcNow, 1,
                DateTimeOffset.UtcNow.AddMinutes(1), Safety(), token);
            await client.SendSafetyStateChangedAsync(2, DateTimeOffset.UtcNow, Safety(), [1], token);

            ExceptionRecoverySessionOpenedPayload opened = await client
                .RequestExceptionRecoverySessionAsync(
                    "77777777-7777-4777-8777-777777777770",
                    new ExceptionRecoverySessionRequestedPayload(
                        "77777777-7777-4777-8777-777777777770",
                        operatorContext,
                        "MAINTENANCE_ADMINISTRATOR",
                        "88888888-8888-4888-8888-888888888888",
                        demandId,
                        [1, 2],
                        "repair complete",
                        "test-proof"),
                    token);

            const string actionId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
            await client.SubmitRecoveryActionAsync(
                actionId,
                new RecoveryActionSubmittedPayload(
                    actionId, opened.ExceptionRecoverySessionId, "RESUME_AFTER_REPAIR",
                    opened.EventId, opened.DemandId, opened.Slots, operatorContext,
                    "repair complete"),
                token);

            await client.RequestManualChargingReturnToServiceAsync(
                "22222222-2222-4222-8222-222222222226",
                new ManualChargingReturnToServiceRequestedPayload(
                    "22222222-2222-4222-8222-222222222227",
                    operatorContext,
                    "MAINTENANCE_ADMINISTRATOR",
                    "manual charging completed",
                    86.5),
                token);

            await client.RequestLoadCompensationAsync(
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                new LoadCompensationRequestedPayload(
                    "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                    opened.ExceptionRecoverySessionId,
                    demandId,
                    attemptId,
                    operatorContext),
                token);
            await client.RequestLoadCorrectionAsync(
                "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                new LoadCorrectionRequestedPayload(
                    "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                    demandId,
                    attemptId,
                    [1],
                    operatorContext,
                    "misplaced basket"),
                token);

            await client.SendLoadCompensationResultAsync(
                $"load-compensation-result:{attemptId}",
                "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
                new LoadCompensationResultPayload(
                    "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", demandId, attemptId, "ALL_EMPTY",
                    [SlotResult(1, "COMPLETED", "EMPTY")], DateTimeOffset.UtcNow),
                token);
            await client.SendLoadCorrectionResultAsync(
                $"load-correction-result:{attemptId}",
                "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
                new LoadCorrectionResultPayload(
                    "cccccccc-cccc-4ccc-8ccc-cccccccccccc", demandId, attemptId, "COMPLETED",
                    [SlotResult(1, "COMPLETED", "OCCUPIED")], DateTimeOffset.UtcNow),
                token);
            await client.SendLoadCancellationResultAsync(
                $"load-cancellation-result:{attemptId}",
                "ffffffff-ffff-4fff-8fff-ffffffffffff",
                new LoadCancellationResultPayload(
                    "99999999-9999-4999-8999-999999999999", demandId, attemptId, "ALL_EMPTY",
                    [SlotResult(1, "COMPLETED", "EMPTY")], DateTimeOffset.UtcNow),
                token);
            await client.SendFaultCargoRecoveryResultAsync(
                $"fault-cargo-result:{attemptId}",
                "66666666-6666-4666-8666-666666666666",
                new FaultCargoRecoveryResultPayload(
                    opened.ExceptionRecoverySessionId, actionId, demandId, legId, "HANDED_OFF",
                    [SlotResult(1, "COMPLETED", "EMPTY")], operatorContext, DateTimeOffset.UtcNow),
                token);

            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>A vehicle that is always stopped, so nothing here turns on motion state.</summary>
    private sealed class AlwaysStopped : IVehicleSafetySignalProvider
    {
        public VehicleSafetySignal Read() =>
            new(VehicleMotionState.Stopped, DateTimeOffset.UtcNow, "PAYLOAD_SHAPE_TEST");
    }

    private static WireToGateSlotResultPayload SlotResult(int slot, string outcome, string state) =>
        new(slot, outcome, state, "LOCKED", "RESET", []);

    private static WireToGateSafetySummaryPayload Safety() =>
        new(true, true, true, true, false, []);

    /// <summary>
    /// The same digest the product code puts on the wire, not a second implementation of it.
    /// </summary>
    private static string Sha256Of(string value) =>
        WireToGateProtocolSerializer.ComputeSha256(Encoding.UTF8.GetBytes(value));

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The offences in one whole wire line, resolved through the message type it names.
    /// </summary>
    private static List<string> Offences(string wireLine)
    {
        using JsonDocument envelope = JsonDocument.Parse(wireLine);
        string messageType = envelope.RootElement.GetProperty("messageType").GetString()!;
        return PayloadOffences(
            messageType, envelope.RootElement.GetProperty("payload").GetRawText());
    }

    private static List<string> PayloadOffences(string messageType, string payloadJson)
    {
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(SchemaPath(messageType)));

        return Offences(
            messageType,
            "payload",
            payload.RootElement,
            schema.RootElement.GetProperty("properties").GetProperty("payload"));
    }

    /// <summary>
    /// Name-set equality against <c>required</c>, then enum membership, then the same recursively
    /// for each array element.
    /// </summary>
    private static List<string> Offences(
        string messageType,
        string path,
        JsonElement value,
        JsonElement schema)
    {
        List<string> offences = [];
        if (value.ValueKind == JsonValueKind.Null)
        {
            // The schema's nullable branch permits it; there is nothing further to check.
            return offences;
        }

        JsonElement resolved = Resolve(schema);

        if (value.ValueKind == JsonValueKind.Array)
        {
            if (!resolved.TryGetProperty("items", out JsonElement items))
            {
                return offences;
            }

            int index = 0;
            foreach (JsonElement element in value.EnumerateArray())
            {
                offences.AddRange(Offences(messageType, $"{path}[{index++}]", element, items));
            }

            return offences;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!resolved.TryGetProperty("required", out JsonElement required))
            {
                return offences;
            }

            string[] expected =
            [
                .. required.EnumerateArray().Select(name => name.GetString()!).Order(StringComparer.Ordinal)
            ];
            string[] actual =
            [
                .. value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            ];

            string[] missing = [.. expected.Except(actual, StringComparer.Ordinal)];
            string[] unexpected = [.. actual.Except(expected, StringComparer.Ordinal)];

            if (missing.Length > 0)
            {
                offences.Add($"{messageType}.{path} omits required {string.Join(", ", missing)}");
            }

            if (unexpected.Length > 0)
            {
                offences.Add(
                    $"{messageType}.{path} carries {string.Join(", ", unexpected)}, which the schema "
                    + "does not declare and additionalProperties forbids");
            }

            JsonElement properties = resolved.GetProperty("properties");
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (properties.TryGetProperty(property.Name, out JsonElement propertySchema))
                {
                    offences.AddRange(Offences(
                        messageType, $"{path}.{property.Name}", property.Value, propertySchema));
                }
            }

            return offences;
        }

        string[] permitted = EnumValues(resolved);
        if (permitted.Length > 0
            && value.ValueKind == JsonValueKind.String
            && !permitted.Contains(value.GetString(), StringComparer.Ordinal))
        {
            offences.Add(
                $"{messageType}.{path} is \"{value.GetString()}\", outside the frozen enumeration "
                + string.Join("/", permitted));
        }

        return offences;
    }

    /// <summary>
    /// The enumeration a field is constrained to, following one <c>$ref</c> hop and unwrapping the
    /// <c>anyOf [ ..., null ]</c> the protocol uses for a nullable enum.
    /// </summary>
    private static string[] EnumValues(JsonElement schema)
    {
        if (schema.TryGetProperty("enum", out JsonElement values))
        {
            return
            [
                .. values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()!)
            ];
        }

        if (schema.TryGetProperty("anyOf", out JsonElement branches))
        {
            foreach (JsonElement branch in branches.EnumerateArray())
            {
                string[] nested = EnumValues(Resolve(branch));
                if (nested.Length > 0)
                {
                    return nested;
                }
            }
        }

        return [];
    }

    /// <summary>
    /// One <c>$ref</c> hop into <c>common/types.schema.json</c>, which is as deep as the protocol's
    /// own payloads go, plus the <c>anyOf [ &lt;something&gt;, null ]</c> wrapper it uses for a
    /// nullable field.
    /// </summary>
    /// <remarks>
    /// <b>The <c>anyOf</c> unwrap is load-bearing, not tidiness.</b> A nullable object -- v2 writes
    /// <c>problem</c> and <c>expectedProtocolReleaseIdentity</c> that way -- arrives as a node with
    /// no <c>required</c> of its own, so without unwrapping it <see cref="Offences"/> returns
    /// nothing for the whole subtree and reports success. That is the exact failure mode this class
    /// exists to prevent, one level down.
    /// <para>
    /// Anything the two hops do not cover is returned unchanged and simply goes unchecked --
    /// stated rather than hidden, because a resolver that silently returned an empty schema would
    /// make this whole class quietly weaker.
    /// </para>
    /// </remarks>
    private static JsonElement Resolve(JsonElement schema)
    {
        if (schema.TryGetProperty("anyOf", out JsonElement branches))
        {
            foreach (JsonElement branch in branches.EnumerateArray())
            {
                if (branch.TryGetProperty("type", out JsonElement type)
                    && string.Equals(type.GetString(), "null", StringComparison.Ordinal))
                {
                    continue;
                }

                return Resolve(branch);
            }

            return schema;
        }

        if (!schema.TryGetProperty("$ref", out JsonElement reference))
        {
            return schema;
        }

        string target = reference.GetString()!;
        int fragment = target.IndexOf("#/$defs/", StringComparison.Ordinal);
        if (fragment < 0 || !target.Contains("common/types.schema.json", StringComparison.Ordinal))
        {
            return schema;
        }

        return TypeDefinitions.Value.RootElement.GetProperty("$defs")
            .GetProperty(target[(fragment + 8)..]);
    }

    private static readonly Lazy<JsonDocument> TypeDefinitions = new(() => JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(SchemaRoot(), "common", "types.schema.json"))));

    private static readonly Lazy<string> Source = new(ReadOnboardSource);

    private static readonly Lazy<string[]> OnboardToServer = new(() =>
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(RepositoryRoot(), "vendor", "8005-agv-protocol", "manifest", "release.json")));

        return
        [
            .. manifest.RootElement.GetProperty("messages").EnumerateObject()
                .Where(message => message.Value.GetProperty("direction").GetString()
                    is "O_TO_C" or "BIDIRECTIONAL")
                .Select(message => message.Name)
                .Order(StringComparer.Ordinal)
        ];
    });

    private static string[] OnboardToServerMessageTypes() => OnboardToServer.Value;

    private static string OnboardSource() => Source.Value;

    private static string ReadOnboardSource()
    {
        string sourceRoot = Path.Combine(RepositoryRoot(), "src");
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

    private static string SchemaPath(string messageType) =>
        Path.Combine(SchemaRoot(), "messages", $"{messageType}.schema.json");

    private static string SchemaRoot() =>
        Path.Combine(RepositoryRoot(), "vendor", "8005-agv-protocol", "schemas");

    private static string RepositoryRoot()
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
}
