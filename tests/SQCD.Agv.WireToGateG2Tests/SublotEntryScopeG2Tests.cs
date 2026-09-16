using System.Net;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// What the vehicle checks a scanned sublot against, now that protocol 2.0.0 replaced the entry
/// request's single <c>expectedSublot</c> and its <c>demandId</c> with a set.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this repository drove <c>SubmitSublotAsync</c> before. The check it performs is the
/// one thing standing between an operator's keystrokes and a message the control server binds a
/// demand to, so it is exercised here from both sides of the set.
/// </para>
/// <para>
/// <b>Membership, not identity.</b> The vehicle never binds a demand (<c>FP-IS-01</c> gives this
/// end <c>NEVER_DISCOVER_SELECT_OR_BIND_DEMAND</c>), so what it can check is that the request still
/// belongs to the worklist in front of it and that the entry is one of the sublots that request
/// named. Which demand an accepted sublot belongs to -- and whether one outside the set belongs to
/// a demand elsewhere in the dispatch -- is the control server's to answer, with
/// <c>SUBLOT_NOT_IN_DISPATCH_SCOPE</c>.
/// </para>
/// </remarks>
public sealed class SublotEntryScopeG2Tests
{
    private const string OperatorVariable = "W2G_G2_SUBLOT_OPERATOR";
    private const string CredentialVariable = "W2G_G2_SUBLOT_CREDENTIAL";
    private const string OperationSessionId = "99999999-9999-4999-8999-999999999999";

    static SublotEntryScopeG2Tests()
    {
        Environment.SetEnvironmentVariable(CredentialVariable, "g2-sublot-credential");
        Environment.SetEnvironmentVariable(OperatorVariable, "operator-001");
    }

    /// <summary>
    /// An entry inside the expected set is submitted, and the submission carries no
    /// <c>demandId</c>.
    /// </summary>
    /// <remarks>
    /// The absent field is asserted directly rather than left to
    /// <see cref="ProtocolPayloadShapeArchitectureTests"/>: that class proves the payload is
    /// schema-legal, and this one proves the vehicle stopped having a demand id to put there.
    /// </remarks>
    [Fact]
    public async Task AnEntryInsideTheExpectedSetIsSubmittedWithoutADemandId()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SublotHarness harness = await SublotHarness.StartAsync(
            ["SUBLOT-001", "SUBLOT-002"], token);

        string messageId = await harness.Business.SubmitSublotAsync("SUBLOT-002", "SCANNER", token);
        Assert.False(string.IsNullOrWhiteSpace(messageId));

        JsonElement payload = await harness.WaitForSubmissionAsync(token);

        Assert.Equal("SUBLOT-002", payload.GetProperty("sublot").GetString());
        Assert.Equal(OperationSessionId, payload.GetProperty("operationSessionId").GetString());
        Assert.Equal("ST-01", payload.GetProperty("stationId").GetString());
        Assert.False(payload.TryGetProperty("demandId", out _));
    }

    /// <summary>
    /// The eight-element end of the set is accepted the same way the one-element end is.
    /// </summary>
    /// <remarks>
    /// A one-demand dispatch is the only shape the control server produces today, so a check
    /// written against "the expected sublot" would pass every test that exists and fail the first
    /// real multi-demand stop. The two ends of <c>minItems</c>/<c>maxItems</c> are driven here for
    /// that reason.
    /// </remarks>
    [Fact]
    public async Task AnEntryInsideAnEightSublotSetIsSubmitted()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string[] expected = [.. Enumerable.Range(1, 8).Select(n => $"SUBLOT-{n:D3}")];
        await using SublotHarness harness = await SublotHarness.StartAsync(expected, token);

        await harness.Business.SubmitSublotAsync("SUBLOT-008", "KEYBOARD", token);
        JsonElement payload = await harness.WaitForSubmissionAsync(token);

        Assert.Equal("SUBLOT-008", payload.GetProperty("sublot").GetString());
        Assert.Equal("KEYBOARD", payload.GetProperty("entryMethod").GetString());
    }

    /// <summary>
    /// A whitespace-only element in <c>expectedSublots</c> does not take the session down.
    /// </summary>
    /// <remarks>
    /// The schema says <c>minLength: 1</c> and nothing more, so <c>" "</c> is a legal element. No
    /// scanner or keyboard entry can ever match it -- entries are trimmed -- so accepting it costs
    /// nothing, while refusing it would answer a schema-legal request with
    /// <c>PROTOCOL_SCHEMA_INVALID</c>, which is exactly the stricter-than-the-contract inbound check
    /// <c>8005-agv-onboard-hmi#38</c> is about.
    /// </remarks>
    [Fact]
    public async Task AWhitespaceExpectedSublotIsAcceptedBecauseTheSchemaAllowsIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SublotHarness harness = await SublotHarness.StartAsync(
            [" ", "SUBLOT-001"], token);

        Assert.Equal([" ", "SUBLOT-001"], harness.Business.ExpectedSublots);
        await harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", token);
        await harness.WaitForSubmissionAsync(token);
    }

    /// <summary>
    /// An entry outside the set is refused locally, and nothing is sent.
    /// </summary>
    [Fact]
    public async Task AnEntryOutsideTheExpectedSetIsRefusedAndNothingIsSent()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SublotHarness harness = await SublotHarness.StartAsync(
            ["SUBLOT-001", "SUBLOT-002"], token);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Business.SubmitSublotAsync("SUBLOT-003", "SCANNER", token));

        Assert.Equal("SUBLOT_NOT_IN_WORKLIST", failure.Message);
        Assert.Empty(harness.Submissions);
    }

    /// <summary>
    /// The server's rejection of an accepted entry is received in its 2.0.0 shape: a null
    /// <c>demandId</c> and the refused sublot named.
    /// </summary>
    /// <remarks>
    /// The vehicle's handling of it -- showing the real reason rather than treating it as a
    /// recovery message -- is <c>8005-agv-onboard-hmi#77</c>. What is proved here is that the
    /// message is accepted at all: the payload record is a closed schema, so a missing
    /// <c>rejectedSublot</c> would have thrown on arrival and taken the session down.
    /// </remarks>
    [Fact]
    public async Task AnOutOfScopeRejectionIsAcceptedFromTheWire()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SublotHarness harness = await SublotHarness.StartAsync(
            ["SUBLOT-001"],
            token,
            server => server.RejectSublotSubmissionsWith = "SUBLOT_NOT_IN_DISPATCH_SCOPE");

        await harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", token);
        await harness.WaitForSubmissionAsync(token);

        // Clearing the outstanding entry request is what this build already does on a rejection, so
        // it is the observable that says the message was parsed rather than dropped: the payload
        // record is a closed schema, and a rejection carrying rejectedSublot would otherwise have
        // thrown on the receive pump and taken the session down. Whether clearing the request and
        // logging it as a recovery message is the right handling is 8005-agv-onboard-hmi#77.
        await SublotHarness.WaitUntilAsync(
            () => !harness.Business.CanSubmitSublot,
            "the rejection to be parsed and clear the outstanding entry request",
            token);
        Assert.True(harness.Session.Current.Connected);
    }

    /// <summary>
    /// A whitespace-only <c>rejectedSublot</c> is accepted, for the same reason a whitespace-only
    /// <c>expectedSublots</c> element is.
    /// </summary>
    /// <remarks>
    /// The schema says <c>minLength: 1</c> for both. The first relaxation covered only the entry
    /// request, so a stricter-than-the-contract check on the rejection could have come back without
    /// anything noticing.
    /// </remarks>
    [Fact]
    public async Task AWhitespaceRejectedSublotIsAcceptedBecauseTheSchemaAllowsIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SublotHarness harness = await SublotHarness.StartAsync(
            ["SUBLOT-001"],
            token,
            server =>
            {
                server.RejectSublotSubmissionsWith = "SUBLOT_NOT_IN_DISPATCH_SCOPE";
                server.RejectedSublotOverride = " ";
            });

        await harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", token);
        await harness.WaitForSubmissionAsync(token);

        await SublotHarness.WaitUntilAsync(
            () => !harness.Business.CanSubmitSublot,
            "the whitespace rejection to be parsed and clear the outstanding entry request",
            token);
        Assert.True(harness.Session.Current.Connected);
    }

    private sealed class SublotHarness : IAsyncDisposable
    {
        private readonly FakeControlServer _server;
        private readonly WireToGateSessionService _session;

        private SublotHarness(
            FakeControlServer server,
            WireToGateSessionService session,
            WireToGateBusinessService business)
        {
            _server = server;
            _session = session;
            Business = business;
        }

        public WireToGateBusinessService Business { get; }

        public WireToGateSessionService Session => _session;

        public IReadOnlyList<string> Submissions =>
        [
            .. _server.ReceivedEnvelopes
                .Where(envelope => envelope.MessageType == "SublotSubmitted")
                .Select(envelope => envelope.WireLine)
        ];

        public static async Task<SublotHarness> StartAsync(
            IReadOnlyList<string> expectedSublots,
            CancellationToken cancellationToken,
            Action<FakeControlServer>? configure = null)
        {
            FakeControlServer server = new(IPAddress.Loopback)
            {
                SendReadinessAfterRecoveryAck = true,
                SendJourneySnapshotsAfterRecovery = true,
                OperationSessionId = OperationSessionId,
                SublotEntryExpectedSublots = expectedSublots
            };
            configure?.Invoke(server);

            try
            {
                FakeIoModuleClient io = new();
                RecordingLogger logger = new();
                StoppedVehicle safety = new();

                string journalPath = Path.Combine(
                    Path.GetTempPath(), "w2g-sublot", Guid.NewGuid().ToString("N"), "journal.db");
                Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);

                WireToGateSessionService session = new(
                    new WireToGateSessionOptions(
                        "127.0.0.1",
                        server.Port,
                        "AGV-8005-01",
                        Guid.NewGuid().ToString("D"),
                        new string('a', 40),
                        CredentialVariable,
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(2),
                        1,
                        1,
                        "eight-slot-v1",
                        "eight-slot-modbus-v1",
                        SupportsBatchUnlock: false),
                    io,
                    new SqliteWireToGateJournal(journalPath),
                    logger,
                    new SystemClock(),
                    safety,
                    new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
                    new SlotConfigurationActivationCoordinator(
                        new DocumentActiveSlotConfigurationStore(
                            new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                            G2SlotConfigurationFixtures.Approved()),
                        TimeProvider.System),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(500));
                WireToGateBusinessService business = new(
                    session,
                    io,
                    logger,
                    new SystemClock(),
                    () => safety.Read().MotionState == VehicleMotionState.Stopped,
                    new WireToGateSlotOperationExecutorOptions(
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromMilliseconds(10),
                        TimeSpan.FromSeconds(30)),
                    OperatorVariable,
                    safety,
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMilliseconds(500));

                await session.Client.ConnectAndRecoverAsync(cancellationToken);
                business.Start();

                // The entry request rides behind the journey snapshots, and the local check reads
                // both -- so waiting for the request alone would still race the worklist.
                await WaitUntilAsync(
                    () => business.CanSubmitSublot
                        && session.CurrentJourney.CurrentStopWorklist is not null,
                    "the entry request and its worklist to arrive",
                    cancellationToken);

                return new SublotHarness(server, session, business);
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        public async Task<JsonElement> WaitForSubmissionAsync(CancellationToken cancellationToken)
        {
            await WaitUntilAsync(
                () => Submissions.Count > 0,
                "the control server to receive SublotSubmitted",
                cancellationToken);

            using JsonDocument document = JsonDocument.Parse(Submissions[0]);
            return document.RootElement.GetProperty("payload").Clone();
        }

        public static async Task WaitUntilAsync(
            Func<bool> predicate,
            string expectation,
            CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!predicate())
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    Assert.Fail($"Timed out after 5s waiting for: {expectation}");
                }

                await Task.Delay(5, cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Business.DisposeAsync();
            await _session.DisposeAsync();
            await _server.DisposeAsync();
        }

        /// <summary>A vehicle that is always stopped; nothing here turns on motion state.</summary>
        private sealed class StoppedVehicle : IVehicleSafetySignalProvider
        {
            public VehicleSafetySignal Read() =>
                new(VehicleMotionState.Stopped, DateTimeOffset.UtcNow, "SUBLOT_ENTRY_TEST");
        }
    }
}
