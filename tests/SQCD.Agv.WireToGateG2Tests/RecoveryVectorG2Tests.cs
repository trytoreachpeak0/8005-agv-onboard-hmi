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
/// The two <c>FP-IS-07</c> vectors whose command half the control server issues and whose result
/// half this onboard reports: <c>CV-FAULT-CARGO-HANDOFF</c> and
/// <c>CV-FORCED-MECHANICAL-RECOVERY</c>.
/// </summary>
/// <remarks>
/// <para>
/// Until ticket 21 no test in this repository drove
/// <c>WireToGateBusinessService.HandleRecoveryVectorCommandAsync</c> at all. The only test that
/// touched either message was <see cref="ProtocolPayloadShapeArchitectureTests"/>, which proves a
/// payload's shape and says nothing about whether the vehicle may act on it -- so the whole
/// authorisation path shipped unexercised, and
/// <c>ProtocolVectorTestBindingArchitectureTests.VectorsThisBatchOwesANamedTest</c> pinned both
/// vectors as owing a named test.
/// </para>
/// <para>
/// Each vector gets a positive case and a refusal case, because the four product assertions the
/// protocol froze for them come in exactly that shape: something must be reported when the command
/// is authorised, and nothing at all may happen when it is not.
/// </para>
/// </remarks>
public sealed class RecoveryVectorG2Tests
{
    private const string CredentialVariable = "W2G_G2_VECTOR_CREDENTIAL";
    private const string OperatorVariable = "W2G_G2_VECTOR_OPERATOR";
    private const string ProofVariable = "W2G_G2_VECTOR_PROOF";

    private const string DemandId = "11111111-1111-4111-8111-111111111111";
    private const string OperationSessionId = "22222222-2222-4222-8222-222222222222";
    private const string AttemptId = "33333333-3333-4333-8333-333333333333";
    private const string CommandMessageId = "44444444-4444-4444-8444-444444444444";

    /// <summary>The exception recovery session id <see cref="FakeControlServer"/> always opens.</summary>
    private const string RecoverySessionId = "77777777-7777-4777-8777-777777777777";

    private const string FaultCargoHandoffAction = "FAULT_CARGO_HANDOFF";
    private const string ForcedMechanicalRecoveryAction = "FORCED_MECHANICAL_RECOVERY";

    /// <summary>
    /// The recovery action id the onboard mints, derived the way
    /// <c>RequestRecoveryActionVectorCoreAsync</c> derives it: from the opened session and the
    /// action, so it differs per vector.
    /// </summary>
    private static string ActionIdFor(string action) =>
        FakeControlServerIdentifiers.StableUuid($"{RecoverySessionId}|{action}");

    /// <summary>
    /// The handoff id the control server derives for the fault cargo vector, from the recovery
    /// action it accepted.
    /// </summary>
    private static string ExpectedHandoffId =>
        FakeControlServerIdentifiers.StableUuid(
            $"{ActionIdFor(FaultCargoHandoffAction)}|fault-cargo-handoff");

    /// <summary>
    /// Gives a refusal time to have produced a result if it were going to.
    /// </summary>
    /// <remarks>
    /// A refusal has no positive signal to wait for, so the only honest wait is a bounded one: the
    /// authorised cases answer their command well inside this, and any longer would only be padding.
    /// </remarks>
    private static Task SettleAsync(CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

    /// <summary>
    /// Named apart from the ones <see cref="WireToGateG2Tests"/> uses: environment variables are
    /// process-wide and xUnit runs the two classes in parallel collections.
    /// </summary>
    static RecoveryVectorG2Tests()
    {
        Environment.SetEnvironmentVariable(CredentialVariable, "g2-vector-credential");
        Environment.SetEnvironmentVariable(OperatorVariable, "maintenance-001");
        Environment.SetEnvironmentVariable(ProofVariable, "vector-test-proof");
    }

    /// <summary>
    /// REPORT_HANDOFF_OUTCOME, and the authorised half of HANDOFF_ONLY_ON_AUTHORIZED_COMMAND.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AuthorizedFaultCargoCommandIsExecutedAndItsOutcomeIsReported()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(token);

        Assert.True(harness.Business.CanRequestFaultCargoHandoff);
        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync(
            "现场确认故障仓货物需要交接处理。", token));

        JsonElement result = await harness.WaitForResultAsync("FaultCargoRecoveryResult", token);

        Assert.Equal("HANDED_OFF", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(DemandId, result.GetProperty("demandId").GetString());
        Assert.Equal(
            ActionIdFor(FaultCargoHandoffAction),
            result.GetProperty("recoveryActionId").GetString());

        // The handoff the onboard reports is the one the server authorised. Neither end sends the
        // other this id; both derive it from the recovery action, and this is the only place the
        // two derivations are ever compared.
        Assert.Equal(
            ExpectedHandoffId,
            result.GetProperty("handoffId").GetString());

        // The slots were already empty, so the safe finish is reached without opening anything --
        // the vector's forbidden side effect list includes duplicate-slot-unlock, and the result
        // still has to say COMPLETED per slot.
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Equal(
            ["COMPLETED", "COMPLETED"],
            result.GetProperty("slotResults").EnumerateArray()
                .Select(slot => slot.GetProperty("outcome").GetString()!)
                .ToArray());
    }

    /// <summary>
    /// HANDOFF_ONLY_ON_AUTHORIZED_COMMAND: a command whose <c>commandContentSha256</c> authorises
    /// different content is not a narrower authorisation, it is a different one.
    /// </summary>
    /// <remarks>
    /// The double is made to hash an empty <c>slotOperationAttemptId</c> while the onboard hashes
    /// the attempt it actually has bound. Every other field on the wire still matches, so the scope
    /// comparison passes and the digest is the only thing that can catch it -- which is the point:
    /// before ticket 21 the command's own digest was parsed, shape-checked and discarded, and this
    /// test passed nothing because it did not exist.
    /// </remarks>
    [Fact]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task FaultCargoCommandAuthorizingDifferentContentIsRefusedWithoutSlotIo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoveryVectorSlotOperationAttemptId = null);

        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync(
            "现场确认故障仓货物需要交接处理。", token));
        await harness.WaitForInboundAsync("FaultCargoRecoveryCommand", token);
        await SettleAsync(token);

        Assert.Empty(harness.ResultsOfType("FaultCargoRecoveryResult"));
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// REPORT_FORCED_RECOVERY_OUTCOME, including the two proofs the schema forbids this message
    /// from ever claiming.
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ForcedMechanicalRecoveryOutcomeIsReportedWithNeitherProofClaimed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.ForcedRecoveryGeneration = 4);

        Assert.True(harness.Business.CanRequestForcedMechanicalRecovery);
        Assert.True(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "现场确认仓门无法电动解锁，申请强制机械恢复。", token));

        JsonElement result = await harness.WaitForResultAsync(
            "ForcedMechanicalRecoveryResult", token);

        Assert.Equal("MECHANICALLY_ISOLATED", result.GetProperty("outcome").GetString());
        Assert.Equal(4, result.GetProperty("forcedRecoveryGeneration").GetInt64());
        Assert.Equal(
            ActionIdFor(ForcedMechanicalRecoveryAction),
            result.GetProperty("recoveryActionId").GetString());
        Assert.False(result.GetProperty("electronicEmptyProven").GetBoolean());
        Assert.False(result.GetProperty("vehicleReadyProven").GetBoolean());

        // Not a slotResults array with everything UNKNOWN: this message carries the slot set only.
        Assert.Equal(
            [1, 2],
            result.GetProperty("slots").EnumerateArray().Select(slot => slot.GetInt32()).ToArray());
        Assert.False(result.TryGetProperty("slotResults", out _));
        Assert.False(result.TryGetProperty("demandId", out _));

        // The generation the command carried is now the vehicle's own, which is what makes the
        // next fence decision meaningful.
        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(4, state.ForcedRecoveryGeneration);
    }

    /// <summary>
    /// REFUSE_STALE_FORCED_RECOVERY_GENERATION.
    /// </summary>
    /// <remarks>
    /// The vehicle is seeded at generation 5 and the server authorises under 3, which is what a
    /// command delayed across a bump looks like on the wire. It must not be answered and it must
    /// not reach the slot IO: the server has already fenced everything it issued under 3, so acting
    /// on it would open a slot set the server no longer believes is in scope. Asserting on the
    /// absence of a result is only meaningful because no result exists yet to be replayed -- the
    /// stale command is the first one this session sees.
    /// </remarks>
    [Fact]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task StaleForcedRecoveryGenerationIsRefusedWithoutSlotIoOrResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.ForcedRecoveryGeneration = 3,
            seededForcedRecoveryGeneration: 5);

        Assert.True(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "现场确认仓门无法电动解锁，申请强制机械恢复。", token));
        await harness.WaitForInboundAsync("ForcedMechanicalRecoveryCommand", token);
        await SettleAsync(token);

        Assert.Empty(harness.ResultsOfType("ForcedMechanicalRecoveryResult"));
        Assert.Equal(0, harness.Io.UnlockCount);

        // The refusal did not move the vehicle's generation backwards either.
        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(5, state.ForcedRecoveryGeneration);
    }

    /// <summary>
    /// One connected onboard sitting on an unsettled load operation, with an authenticated recovery
    /// operator, talking to a control server that issues the vector command an accepted action
    /// calls for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The readiness has to be <c>RECOVERY_REQUIRED</c> when the action is submitted -- with no
    /// open recovery session that is the only state
    /// <c>RequestRecoveryActionVectorCoreAsync</c> will open one from -- while the vehicle itself
    /// has to be genuinely stopped, because <c>EnsureVehicleStoppedAndFresh</c> gates the
    /// execution that follows. Both hold at once here: the double withholds readiness until it has
    /// accepted a departure-safe <c>SafetyStateChanged</c>, and it is not configured to re-send
    /// readiness after one.
    /// </para>
    /// <para>
    /// The eight lockers start locked, output reset and empty, so the clear runs to a safe finish
    /// without pulsing anything. That is deliberate: it keeps <c>UnlockCount</c> at zero for a
    /// successful vector, which is what makes the same assertion meaningful in the refusal tests.
    /// </para>
    /// </remarks>
    private sealed class RecoveryVectorHarness : IAsyncDisposable
    {
        private readonly WireToGateSessionService _session;
        private readonly SqliteWireToGateJournal _journal;

        private RecoveryVectorHarness(
            FakeControlServer server,
            FakeIoModuleClient io,
            WireToGateSessionService session,
            WireToGateBusinessService business,
            SqliteWireToGateJournal journal)
        {
            Server = server;
            Io = io;
            _session = session;
            Business = business;
            _journal = journal;
        }

        public FakeControlServer Server { get; }

        public FakeIoModuleClient Io { get; }

        public WireToGateBusinessService Business { get; }

        public static async Task<RecoveryVectorHarness> StartAsync(
            CancellationToken cancellationToken,
            Action<FakeControlServer>? configure = null,
            long seededForcedRecoveryGeneration = 0)
        {
            FakeControlServer server = new(IPAddress.Loopback)
            {
                RequireSafeSafetyForReadiness = true,
                SendReadinessAfterRecoveryAck = true,
                RespondToRecoveryRequests = true,
                SendRecoveryVectorCommandAfterRecoveryAction = true,
                RecoveryVectorSlotOperationAttemptId = AttemptId
            };
            configure?.Invoke(server);

            try
            {
                FakeIoModuleClient io = new();
                NullLogger logger = new();
                MutableSafetySignalProvider safety = new();
                string journalPath = Path.Combine(
                    Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
                Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);

                SqliteWireToGateJournal journal = new(journalPath);
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
                    journal,
                    logger,
                    new SystemClock(),
                    safety,
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
                    TimeSpan.FromMilliseconds(500),
                    new WireToGateRecoveryOptions(
                        ResumeAfterRepairEnabled: true,
                        ProofVariable,
                        "MAINTENANCE_ADMINISTRATOR",
                        "CONFIGURED_PROOF"));

                await journal.InitializeAsync(cancellationToken);
                await journal.WriteRecoveryStateAsync(
                    new WireToGateRecoveryState(
                        AttemptId,
                        WireToGateRecoveryCheckpoint.Prepared,
                        [],
                        seededForcedRecoveryGeneration,
                        [])
                    {
                        OperationContext = new WireToGateRecoveryOperationContext(
                            CommandMessageId,
                            null,
                            1,
                            DateTimeOffset.UtcNow,
                            DemandId,
                            OperationSessionId,
                            AttemptId,
                            OperationType.Load,
                            [1, 2],
                            2,
                            true,
                            new string('0', 64))
                    },
                    cancellationToken);

                // The readiness has to be RECOVERY_REQUIRED when the action is submitted -- with no
                // open recovery session that is the only state
                // RequestRecoveryActionVectorCoreAsync will open one from -- while the vehicle has
                // to be stopped for the execution that follows, because EnsureVehicleStoppedAndFresh
                // gates it. One signal feeds both: the handshake's SafetyStateSnapshot is what the
                // double reads departureSafe from.
                //
                // So the vehicle is unknown across the handshake and stopped from then on. The
                // provider is not IObservableVehicleSafetySignalProvider, so nothing pushes the
                // change; the pump sends one SafetyStateChanged when it starts, and the double is
                // not configured to re-announce readiness after one.
                WireToGateSessionSnapshot connected =
                    await session.Client.ConnectAndRecoverAsync(cancellationToken);
                Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, connected.Readiness);

                safety.SetStopped();
                business.Start();

                // The CanRequest* gates read a cached copy of the recovery state that the pump
                // refreshes on its first pass; the seeded journal alone does not answer them. The
                // request path reads the journal directly, so this wait is what makes the gates
                // meaningful to assert rather than what makes the request work.
                await WaitUntilAsync(
                    () => business.CurrentOperationSnapshot?.Stage
                        == WireToGateHmiOperationStage.RecoveryRequired,
                    cancellationToken);
                Assert.Equal(AttemptId, business.CurrentOperationSnapshot!.SlotOperationAttemptId);

                return new RecoveryVectorHarness(server, io, session, business, journal);
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(
            CancellationToken cancellationToken) =>
            _journal.ReadRecoveryStateAsync(cancellationToken);

        public IReadOnlyList<string> ResultsOfType(string messageType) =>
        [
            .. Server.ReceivedEnvelopes
                .Where(envelope => envelope.MessageType == messageType)
                .Select(envelope => envelope.WireLine)
        ];

        public async Task<JsonElement> WaitForResultAsync(
            string messageType,
            CancellationToken cancellationToken)
        {
            await WaitForInboundAsync(messageType, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(ResultsOfType(messageType)[0]);
            return document.RootElement.GetProperty("payload").Clone();
        }

        private static async Task WaitUntilAsync(
            Func<bool> predicate,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (!predicate())
            {
                await Task.Delay(5, timeout.Token);
            }
        }

        public async Task WaitForInboundAsync(
            string messageType,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (!Server.Received.Any(item => item.MessageType == messageType)
                && !Server.SentEnvelopes.Any(item => item.MessageType == messageType))
            {
                await Task.Delay(5, timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Business.DisposeAsync();
            await _session.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    /// <summary>
    /// Unknown until <see cref="SetStopped"/>, then stopped, and always freshly observed.
    /// </summary>
    private sealed class MutableSafetySignalProvider : IVehicleSafetySignalProvider
    {
        private int _stopped;

        public void SetStopped() => Interlocked.Exchange(ref _stopped, 1);

        public VehicleSafetySignal Read() => new(
            Volatile.Read(ref _stopped) == 1
                ? VehicleMotionState.Stopped
                : VehicleMotionState.Unknown,
            DateTimeOffset.UtcNow,
            "RECOVERY_VECTOR_G2_TEST");
    }

    private sealed class NullLogger : IAppLogger
    {
        public event EventHandler<LogEntryEventArgs>? EntryWritten;

        public void Write(
            LogSeverity severity,
            string source,
            string message,
            Exception? exception = null)
        {
            _ = severity;
            _ = source;
            _ = message;
            _ = exception;
        }
    }
}
