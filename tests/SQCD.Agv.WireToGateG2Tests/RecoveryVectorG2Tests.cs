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
/// authorization path shipped unexercised, and
/// <c>ProtocolVectorTestBindingArchitectureTests.VectorsThisBatchOwesANamedTest</c> pinned both
/// vectors as owing a named test.
/// </para>
/// <para>
/// Each vector gets a positive case and a refusal case, because the four product assertions the
/// protocol froze for them come in exactly that shape: something must be reported when the command
/// is authorized, and nothing at all may happen when it is not.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    private const string CredentialVariable = "W2G_G2_VECTOR_CREDENTIAL";
    private const string OperatorVariable = "W2G_G2_VECTOR_OPERATOR";
    private const string ProofVariable = "W2G_G2_VECTOR_PROOF";

    private const string DemandId = "11111111-1111-4111-8111-111111111111";
    private const string OperationSessionId = "22222222-2222-4222-8222-222222222222";
    private const string AttemptId = "33333333-3333-4333-8333-333333333333";
    private const string CommandMessageId = "44444444-4444-4444-8444-444444444444";
    private const string UnloadAttemptId = "55555555-5555-4555-8555-555555555555";
    private const string UnloadCommandMessageId = "66666666-6666-4666-8666-666666666666";

    /// <summary>The exception recovery session id <see cref="FakeControlServer"/> always opens.</summary>
    private const string RecoverySessionId = "77777777-7777-4777-8777-777777777777";

    private const string CompensateLoadAction = "COMPENSATE_LOAD_ALL_EMPTY";
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
    /// The compensation entry appears for a load the vehicle already settled, and the request it
    /// sends names that settled attempt (ported from MVP <c>f1077b4</c>, behaviour half).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two ends can reach opposite conclusions about one load: the vehicle reports COMPLETED,
    /// <c>MarkResultRecordedAsync</c> clears the armed context and moves the identity into
    /// <c>LastCompletedLoadOperationContext</c>, and the server judges that same attempt
    /// <c>RecoveryRequired</c>. That is precisely the state a compensation exists for -- and it was
    /// the one state the entry did not appear in, because it looked only at an armed operation.
    /// So the vehicle stood there with no way to ask for the slots to be emptied.
    /// </para>
    /// <para>
    /// The identity is read, never reconstructed: the settled load is taken from the journal, which
    /// is why <c>WireToGateRecoveryState</c>'s rule that a missing context is a hard block, never a
    /// guess, still holds.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task TheCompensationEntryAppearsForALoadTheVehicleAlreadySettled()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        // The server names the settled load's attempt on all three recovery messages, the way the
        // 2.0.0 control server does once loading had started (8005-agv-program#95). This is the
        // 8005-agv-control-server#5 case on the v2 line: the recovery is decided after the vehicle
        // cleared the attempt, and the compensation still goes out -- and executes -- carrying the
        // attempt the server named.
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = AttemptId,
            loadAlreadySettled: true);

        WireToGateRecoveryState seeded = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(seeded.OperationContext);
        Assert.Null(seeded.UnsettledSlotOperationAttemptId);
        Assert.NotNull(seeded.LastCompletedLoadOperationContext);

        Assert.True(harness.Business.CanRequestLoadCompensation);
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ResultsOfType("LoadCompensationRequested").Count == 1,
            "the compensation request to reach the server",
            token);
        using JsonDocument document = JsonDocument.Parse(
            harness.ResultsOfType("LoadCompensationRequested")[0]);
        JsonElement payload = document.RootElement.GetProperty("payload");
        Assert.Equal(AttemptId, payload.GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal(DemandId, payload.GetProperty("demandId").GetString());
        Assert.Equal(RecoverySessionId, payload.GetProperty("exceptionRecoverySessionId").GetString());

        // The command the request earns has to be executable too, or the entry only appears to work:
        // the bind path checks the vector against an armed operation, and a settled load has none.
        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(AttemptId, result.GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal(
            ActionIdFor(CompensateLoadAction),
            result.GetProperty("recoveryActionId").GetString());
    }

    /// <summary>
    /// An unload the vehicle is in the middle of is not a settled load's stand-in: the compensation
    /// entry stays shut while an unrelated operation is armed, however recent the last completed
    /// load is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ordinary sequence at the gate -- the load completed, so its identity sits in
    /// <c>LastCompletedLoadOperationContext</c>, and the vehicle is now unloading with a door open.
    /// Falling back to the settled load here would be wrong twice over. The entry would open on a
    /// load nobody is asking about, and taking it would overwrite the journal: preparing a vector
    /// rewrites <c>UnsettledSlotOperationAttemptId</c> to the vector's attempt and empties
    /// <c>ActiveUnlockSlots</c>, so the record of the door standing open right now would be gone.
    /// </para>
    /// <para>
    /// So the rule the two ends of this share is "whatever is armed and unsettled is the subject" --
    /// a load if that is what it is, and otherwise nothing -- and only an operation the vehicle has
    /// finished with lets the settled load take over. The entry and the bind path read it through
    /// the same helper, because an entry that opens on a subject the bind path then refuses is the
    /// very failure this batch exists to remove.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnArmedUnloadKeepsTheCompensationEntryShutEvenWithASettledLoadOnFile()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            armedUnloadOverSettledLoad: true);

        // The unload is still the journal's unsettled operation: the interrupted settlement that ran
        // on this seeded state read slot 3 as still full and reported UNKNOWN, which settles nothing.
        WireToGateRecoveryState seeded = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(UnloadAttemptId, seeded.UnsettledSlotOperationAttemptId);
        Assert.Equal(OperationType.Unload, seeded.OperationContext!.OperationType);
        Assert.NotNull(seeded.LastCompletedLoadOperationContext);

        Assert.False(harness.Business.CanRequestLoadCompensation);
        Assert.Empty(harness.ResultsOfType("LoadCompensationRequested"));

        // And the unload is still the subject afterwards -- preparing a compensation vector here
        // would have rewritten this attempt to the settled load's.
        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(UnloadAttemptId, after.UnsettledSlotOperationAttemptId);
        Assert.Equal(OperationType.Unload, after.OperationContext!.OperationType);
    }

    /// <summary>
    /// REPORT_HANDOFF_OUTCOME, and the authorized half of HANDOFF_ONLY_ON_AUTHORIZED_COMMAND.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
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

        // The handoff the onboard reports is the one the server authorized. Neither end sends the
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
    /// HANDOFF_ONLY_ON_AUTHORIZED_COMMAND: a command naming a different handoff is not a narrower
    /// authorization, it is a different one.
    /// </summary>
    /// <remarks>
    /// Every other field on the wire matches the vector this end prepared; only the
    /// <c>handoffId</c> differs, so nothing but the scope comparison in
    /// <c>BindRecoveryVectorCommandAsync</c> can catch it. The slots hold cargo here, which is what
    /// makes <c>UnlockCount</c> worth asserting: had the command been accepted, the executor would
    /// have pulsed the first slot before anything else could fail.
    /// <para>
    /// The refusal is answered on the wire since onboard-hmi#145 (b): the server's workflow is
    /// waiting in <c>AwaitingResult</c> for this command, and nothing else would ever end that wait.
    /// The result echoes the <b>command's</b> handoff, not the one this end prepared, because the
    /// workflow it has to land on is the one the command came from. Why that is the right answer
    /// rather than a claim about slots nobody touched is argued in
    /// <c>RecoveryVectorG2Tests.BindRefusalResult.cs</c>.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task FaultCargoCommandNamingADifferentHandoffIsRefusedWithAFailedResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string CommandHandoffId = "5a5a5a5a-5a5a-4a5a-8a5a-5a5a5a5a5a5a";
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.FaultCargoRecoveryHandoffIdOverride = CommandHandoffId,
            cargoInTargetSlots: true);

        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync(
            "现场确认故障仓货物需要交接处理。", token));
        await harness.WaitForRecoveryBlockedAsync("RECOVERY_SCOPE_MISMATCH", token);

        JsonElement result = await harness.WaitForResultAsync("FaultCargoRecoveryResult", token);
        // One, not "at least one": the old assertion was Assert.Empty, so without this the change from
        // silence to an answer would also stop noticing a vehicle that answers twice.
        Assert.Single(harness.ResultsOfType("FaultCargoRecoveryResult"));
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(CommandHandoffId, result.GetProperty("handoffId").GetString());
        Assert.Equal(
            ["NOT_STARTED", "NOT_STARTED"],
            result.GetProperty("slotResults").EnumerateArray()
                .Select(slot => slot.GetProperty("outcome").GetString()!)
                .ToArray());
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// REPORT_FORCED_RECOVERY_OUTCOME, including the two proofs the schema forbids this message
    /// from ever claiming.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
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
        await harness.ConfirmForcedMechanicalRecoveryAsync(token);

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
    /// REQ-0241: a forced mechanical recovery sends no unlock DO at any point, and reports
    /// <c>MECHANICALLY_ISOLATED</c> only once the operator has confirmed the isolation and the
    /// manual extraction (onboard-hmi#107).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until #107 the command ran through the ordinary clear executor: it pulsed every occupied
    /// target slot and waited for <c>EMPTY</c> and a closed lock, then mapped that electronic
    /// finish onto <c>MECHANICALLY_ISOLATED</c>. That is the opposite of the requirement -- the
    /// lock is exactly what cannot be trusted here, the people at the vehicle have cut its power,
    /// and a qualified person opens it by hand. The slots hold cargo in this case, so the old path
    /// would have pulsed slot 1 before anything else.
    /// </para>
    /// <para>
    /// Once the server acknowledged the result, the business side is settled -- the server has
    /// cancelled the operation -- so the attempt goes from the journal with no OperationResult,
    /// and what remains is the device side: the two slots are physically unknown until a hardware
    /// recovery record clears them.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ForcedMechanicalRecoverySendsNoUnlockAndReportsOnlyAfterTheOperatorConfirms()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);

        Assert.True(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "现场确认仓门无法电动解锁，申请强制机械恢复。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanConfirmForcedMechanicalRecovery,
            "the authorized forced recovery to wait for the operator's confirmation",
            token);

        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.ResultsOfType("ForcedMechanicalRecoveryResult"));
        // The seeded load was settled UNKNOWN when the vehicle came up; that one result is the
        // harness's, and the forced recovery must add none.
        int operationResultsBefore = harness.ResultsOfType("OperationResult").Count;

        Assert.True(await harness.Business.ConfirmForcedMechanicalRecoveryAsync(token));
        JsonElement result = await harness.WaitForResultAsync(
            "ForcedMechanicalRecoveryResult", token);

        Assert.Equal("MECHANICALLY_ISOLATED", result.GetProperty("outcome").GetString());
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Equal(operationResultsBefore, harness.ResultsOfType("OperationResult").Count);
        Assert.False(harness.Business.CanConfirmForcedMechanicalRecovery);

        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(state.UnsettledSlotOperationAttemptId);
        Assert.Null(state.OperationContext);
        Assert.Null(state.RecoveryVector);
        Assert.Equal([1, 2], state.ForcedIsolation!.PhysicallyUnknownSlots);
        Assert.Equal(RecoverySessionId, state.ForcedIsolation.ExceptionRecoverySessionId);
        Assert.Equal(
            ActionIdFor(ForcedMechanicalRecoveryAction),
            state.ForcedIsolation.RecoveryActionId);
        Assert.Equal([1, 2], harness.Business.PhysicallyUnknownSlots);
    }

    /// <summary>
    /// The wait for the operator's confirmation is on disk: a restart keeps waiting, and the
    /// confirmation after it still reports without a single unlock.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedRecoveryAwaitingConfirmationIsStillAwaitingItAfterARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Path.Combine(
            Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();

        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            cargoInTargetSlots: true))
        {
            Assert.True(await beforeRestart.Business.RequestForcedMechanicalRecoveryAsync(
                "现场确认仓门无法电动解锁，申请强制机械恢复。", token));
            await RecoveryVectorHarness.WaitUntilAsync(
                () => beforeRestart.Business.CanConfirmForcedMechanicalRecovery,
                "the authorized forced recovery to wait for the operator's confirmation",
                token);
            Assert.Equal(0, beforeRestart.Io.UnlockCount);
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            baselineRevision: 2,
            restart: true,
            cargoInTargetSlots: true);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Business.CanConfirmForcedMechanicalRecovery,
            "the forced recovery to still wait for the confirmation after the restart",
            token);

        Assert.True(await afterRestart.Business.ConfirmForcedMechanicalRecoveryAsync(token));
        JsonElement result = await afterRestart.WaitForResultAsync(
            "ForcedMechanicalRecoveryResult", token);

        Assert.Equal("MECHANICALLY_ISOLATED", result.GetProperty("outcome").GetString());
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        Assert.DoesNotContain(
            server.ReceivedEnvelopes,
            envelope => envelope.MessageType == "ForcedMechanicalRecoveryResult");
    }

    /// <summary>
    /// The device half of a forced recovery: a hardware recovery record for the whole isolated set,
    /// recorded by the server over valid live readings, clears the set -- and resumes nothing
    /// (ADR-cross-0036, REQ-0242, onboard-hmi#107).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ARecordedHardwareRecoveryClearsTheWholeIsolatedSet()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        await harness.IsolateByForcedRecoveryAsync(token);
        Assert.True(harness.Business.CanSubmitHardwareRecoveryRecord);
        int operationResultsBefore = harness.ResultsOfType("OperationResult").Count;

        Assert.True(await harness.Business.SubmitHardwareRecoveryRecordAsync(
            "更换 1、2 号仓锁体，复测锁反馈正常。", token));

        using JsonDocument document = JsonDocument.Parse(
            Assert.Single(harness.ResultsOfType("HardwareRecoveryRecordSubmitted")));
        JsonElement record = document.RootElement.GetProperty("payload");
        Assert.Equal(RecoverySessionId, record.GetProperty("exceptionRecoverySessionId").GetString());
        Assert.Equal(
            ActionIdFor(ForcedMechanicalRecoveryAction),
            record.GetProperty("recoveryActionId").GetString());
        Assert.Equal(
            [1, 2],
            record.GetProperty("slots").EnumerateArray().Select(slot => slot.GetInt32()).ToArray());
        Assert.Equal(
            ["LIVE_SLOT_SIGNALS_VALID"],
            record.GetProperty("checksPerformed").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(
            ["ADMINISTRATOR_CONFIRMED_HARDWARE_REPAIRED"],
            record.GetProperty("actionsPerformed").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(
            ["更换 1、2 号仓锁体，复测锁反馈正常。"],
            record.GetProperty("observations").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal("MAINTENANCE_ADMINISTRATOR", record.GetProperty("administratorRole").GetString());

        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(state.ForcedIsolation);
        Assert.Empty(harness.Business.PhysicallyUnknownSlots);
        Assert.False(harness.Business.CanSubmitHardwareRecoveryRecord);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Equal(operationResultsBefore, harness.ResultsOfType("OperationResult").Count);
    }

    /// <summary>
    /// A refused record changes nothing on the vehicle: the set stays physically unknown, and the
    /// next press is a new record.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ARejectedHardwareRecoveryRecordLeavesTheSetPhysicallyUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.HardwareRecoveryRecordOutcome = "REJECTED",
            cargoInTargetSlots: true);
        await harness.IsolateByForcedRecoveryAsync(token);

        Assert.False(await harness.Business.SubmitHardwareRecoveryRecordAsync(
            "复测锁反馈正常。", token));

        Assert.Single(harness.ResultsOfType("HardwareRecoveryRecordSubmitted"));
        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal([1, 2], state.ForcedIsolation!.PhysicallyUnknownSlots);
        Assert.Null(state.ForcedIsolation.PendingRecord);
        Assert.Equal([1, 2], harness.Business.PhysicallyUnknownSlots);
    }

    /// <summary>
    /// A slot whose live readings are not valid is not cleared, and no record is made for it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task NoHardwareRecoveryRecordIsSentWhileAnIsolatedSlotCannotBeRead()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        await harness.IsolateByForcedRecoveryAsync(token);
        harness.Io.SetUnreadable(1);

        Assert.False(await harness.Business.SubmitHardwareRecoveryRecordAsync(
            "复测锁反馈正常。", token));

        await harness.WaitForRecoveryBlockedAsync("SLOT_STATE_UNKNOWN", token);
        Assert.Empty(harness.ResultsOfType("HardwareRecoveryRecordSubmitted"));
        Assert.Equal(
            [1, 2],
            (await harness.ReadRecoveryStateAsync(token)).ForcedIsolation!.PhysicallyUnknownSlots);
    }

    /// <summary>
    /// onboard-hmi#107: an unload whose slot operation ended UNKNOWN offers the controlled pickup,
    /// with the permissions and preconditions a load has, and the handoff is reported against the
    /// unload's own demand and attempt. Before #107 only "resume after repair" was on offer, which is
    /// no way out when the lock cannot be repaired.
    /// </summary>
    /// <remarks>
    /// Slot 3 held the basket when the vehicle came back, so the interrupted settlement left the
    /// unload UNKNOWN; the operator has since taken it out. A settled load is on file too, and the
    /// compensation entry still stays shut over the unload -- only the two actions that take cargo
    /// out of an unload are opened.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AnUnknownUnloadOffersTheFaultCargoHandoffAndReportsItAgainstTheUnload()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoveryVectorSlotOperationAttemptId = UnloadAttemptId,
            armedUnloadOverSettledLoad: true);
        harness.Io.SetCargoPresent(2, false);

        Assert.True(harness.Business.CanRequestFaultCargoHandoff);
        Assert.True(harness.Business.CanRequestForcedMechanicalRecovery);
        Assert.False(harness.Business.CanRequestLoadCompensation);
        Assert.True(await harness.Business.RequestFaultCargoHandoffAsync(
            "卸货仓锁故障，现场受控取货。", token));

        JsonElement result = await harness.WaitForResultAsync("FaultCargoRecoveryResult", token);
        Assert.Equal("HANDED_OFF", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(DemandId, result.GetProperty("demandId").GetString());
        JsonElement slot = Assert.Single(result.GetProperty("slotResults").EnumerateArray());
        Assert.Equal(3, slot.GetProperty("slotNo").GetInt32());
        Assert.Equal("EMPTY", slot.GetProperty("finalPhysicalState").GetString());

        using JsonDocument submitted = JsonDocument.Parse(
            Assert.Single(harness.ResultsOfType("RecoveryActionSubmitted")));
        Assert.Equal(
            [3],
            submitted.RootElement.GetProperty("payload").GetProperty("slots").EnumerateArray()
                .Select(item => item.GetInt32()).ToArray());
    }

    /// <summary>
    /// onboard-hmi#107: the forced mechanical recovery is on offer over an UNKNOWN unload too, and
    /// sends no unlock there either.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AnUnknownUnloadOffersTheForcedMechanicalRecoveryWithoutAnyUnlock()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoveryVectorSlotOperationAttemptId = UnloadAttemptId,
            armedUnloadOverSettledLoad: true);

        Assert.True(harness.Business.CanRequestForcedMechanicalRecovery);
        Assert.True(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "卸货仓锁无法电动解锁，申请强制机械取出。", token));
        await harness.ConfirmForcedMechanicalRecoveryAsync(token);

        JsonElement result = await harness.WaitForResultAsync(
            "ForcedMechanicalRecoveryResult", token);
        Assert.Equal("MECHANICALLY_ISOLATED", result.GetProperty("outcome").GetString());
        Assert.Equal(
            [3],
            result.GetProperty("slots").EnumerateArray().Select(item => item.GetInt32()).ToArray());
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Equal([3], harness.Business.PhysicallyUnknownSlots);
    }

    /// <summary>
    /// A second forced mechanical recovery is refused while the first one's slots are still
    /// physically unknown: the vehicle keeps one isolation, and replacing it would make those slots
    /// operable again with no hardware recovery record (REQ-0242, review of onboard-hmi#110).
    /// </summary>
    /// <remarks>
    /// Slot 5 was isolated earlier and never cleared; the seeded load over slots 1 and 2 is now the
    /// subject. The server holds the whole vehicle until the record arrives anyway, so refusing here
    /// costs nothing -- but the onboard does not lean on the server to keep this line.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ASecondForcedRecoveryIsRefusedWhileAnIsolationIsUncleared()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true,
            seededForcedIsolation: [5]);

        Assert.False(harness.Business.CanRequestForcedMechanicalRecovery);
        Assert.False(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "现场确认仓门无法电动解锁，申请强制机械恢复。", token));

        await harness.WaitForRecoveryBlockedAsync("HARDWARE_RECOVERY_RECORD_REQUIRED", token);
        Assert.Empty(harness.ResultsOfType("RecoveryActionSubmitted"));
        Assert.Empty(harness.ResultsOfType("ExceptionRecoverySessionRequested"));
        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal([5], state.ForcedIsolation!.PhysicallyUnknownSlots);
        Assert.Equal("abababab-abab-4bab-8bab-abababababab", state.ForcedIsolation.RecoveryActionId);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// An isolation does not stop a restart from settling an operation on other slots from the live
    /// IO (8005-agv-program#40), nor from telling the operator it did not finish.
    /// </summary>
    /// <remarks>
    /// Slot 5 is isolated; the seeded load over slots 1 and 2 was cut off mid-run with nothing
    /// reported. Its slots are not the isolated ones, so reading them is as safe as ever, and the
    /// executor refuses any command that touches slot 5 anyway.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnInterruptedOperationOnOtherSlotsIsStillSettledWhileAnIsolationStands()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true,
            seededForcedIsolation: [5]);

        // The settlement publishes its operation before the result goes out, so the harness being
        // up is not yet the result having arrived.
        await harness.WaitForInboundAsync("OperationResult", token);
        using JsonDocument settled = JsonDocument.Parse(
            Assert.Single(harness.ResultsOfType("OperationResult")));
        Assert.Equal(
            AttemptId,
            settled.RootElement.GetProperty("payload").GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal(AttemptId, harness.Business.CurrentOperationSnapshot!.SlotOperationAttemptId);
        Assert.Equal([5], harness.Business.PhysicallyUnknownSlots);
        Assert.Equal(
            [5],
            (await harness.ReadRecoveryStateAsync(token)).ForcedIsolation!.PhysicallyUnknownSlots);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// The server recorded the hardware recovery, but a slot could no longer be read when the vehicle
    /// checked again: nothing is cleared, and the next press repeats the same record -- same recordId,
    /// same content, a new messageId -- rather than making a second one.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ARecordedHardwareRecoveryOverUnreadableSignalsIsRepeatedUnderTheSameRecordId()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        await harness.IsolateByForcedRecoveryAsync(token);
        harness.Server.BeforeHardwareRecoveryRecordResult = () => harness.Io.SetUnreadable(1);

        Assert.False(await harness.Business.SubmitHardwareRecoveryRecordAsync(
            "更换 2 号仓锁体。", token));
        await harness.WaitForRecoveryBlockedAsync("SLOT_STATE_UNKNOWN", token);
        WireToGateRecoveryState held = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal([1, 2], held.ForcedIsolation!.PhysicallyUnknownSlots);
        Assert.NotNull(held.ForcedIsolation.PendingRecord);

        harness.Server.BeforeHardwareRecoveryRecordResult = null;
        harness.Io.CloseDoor(1, cargo: false);
        Assert.True(await harness.Business.SubmitHardwareRecoveryRecordAsync(
            "第二次按下时换了说明。", token));

        IReadOnlyList<string> records = harness.ResultsOfType("HardwareRecoveryRecordSubmitted");
        Assert.Equal(2, records.Count);
        using JsonDocument first = JsonDocument.Parse(records[0]);
        using JsonDocument second = JsonDocument.Parse(records[1]);
        Assert.NotEqual(
            first.RootElement.GetProperty("messageId").GetString(),
            second.RootElement.GetProperty("messageId").GetString());
        Assert.Equal(
            first.RootElement.GetProperty("payload").GetRawText(),
            second.RootElement.GetProperty("payload").GetRawText());
        Assert.Null((await harness.ReadRecoveryStateAsync(token)).ForcedIsolation);
    }

    /// <summary>
    /// REFUSE_STALE_FORCED_RECOVERY_GENERATION.
    /// </summary>
    /// <remarks>
    /// The vehicle is seeded at generation 5 and the server authorizes under 3, which is what a
    /// command delayed across a bump looks like on the wire. It must not be answered and it must
    /// not reach the slot IO: the server has already fenced everything it issued under 3, so acting
    /// on it would open a slot set the server no longer believes is in scope. Asserting on the
    /// absence of a result is only meaningful because no result exists yet to be replayed -- the
    /// stale command is the first one this session sees -- and the slots hold cargo, so an accepted
    /// command would have pulsed one before it could fail for any other reason.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task StaleForcedRecoveryGenerationIsRefusedWithoutSlotIoOrResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.ForcedRecoveryGeneration = 3,
            seededForcedRecoveryGeneration: 5,
            cargoInTargetSlots: true);

        Assert.True(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "现场确认仓门无法电动解锁，申请强制机械恢复。", token));
        await harness.WaitForRecoveryBlockedAsync(token);

        Assert.Empty(harness.ResultsOfType("ForcedMechanicalRecoveryResult"));
        Assert.Equal(0, harness.Io.UnlockCount);

        // The refusal did not move the vehicle's generation backwards either.
        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(5, state.ForcedRecoveryGeneration);
    }

    /// <summary>
    /// A command the vehicle refuses on scope does not move the fence it was carrying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generation and the authorization travel in the same message, so the order the onboard
    /// trusts them in is a decision, not an implementation detail. Raising the fence on arrival
    /// would let one command the vehicle then rejects park the fence above every genuine command
    /// the server can still issue: the server keeps authorizing under its own generation, the
    /// vehicle keeps refusing them as stale, and nothing lowers a fence -- so the recovery path
    /// closes permanently, from a single message the vehicle already decided not to obey.
    /// </para>
    /// <para>
    /// Here the command carries generation 9 over slot 1 while the vector is bound to slots 1 and
    /// 2. The scope comparison rejects it; the fence must still read 0.
    /// </para>
    /// <para>
    /// Since onboard-hmi#145 (b) the refusal is answered with a <c>FAILED</c> result, and that answer
    /// reports generation 9 -- the one the command it answers was issued under, which is how the server
    /// files it against the right workflow. Reporting a generation is not adopting it: what the fence
    /// reads is the journal, and the journal still says 0.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ForcedRecoveryGenerationIsNotRaisedByACommandRefusedOnScope()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.ForcedRecoveryGeneration = 9;
                server.RecoveryVectorSlotsOverride = [1];
            },
            cargoInTargetSlots: true);

        Assert.True(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "现场确认仓门无法电动解锁，申请强制机械恢复。", token));
        await harness.WaitForRecoveryBlockedAsync("RECOVERY_SCOPE_MISMATCH", token);

        JsonElement result = await harness.WaitForResultAsync(
            "ForcedMechanicalRecoveryResult", token);
        // See the fault cargo case: the old Assert.Empty covered "not twice" for free, and the new
        // shape does not.
        Assert.Single(harness.ResultsOfType("ForcedMechanicalRecoveryResult"));
        Assert.Equal("FAILED", result.GetProperty("outcome").GetString());
        Assert.Equal(9, result.GetProperty("forcedRecoveryGeneration").GetInt64());
        Assert.Equal(
            [1],
            result.GetProperty("slots").EnumerateArray().Select(slot => slot.GetInt32()).ToArray());
        Assert.Equal(0, harness.Io.UnlockCount);

        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(0, state.ForcedRecoveryGeneration);
    }

    /// <summary>
    /// 被拒过一次，同一个 attempt 还得能再请求。请求 id 原来由 attempt 算出来，于是第二次按下带着
    /// 同一个 messageId、却是新的 <c>verifiedAt</c>/<c>sentAt</c>——真服务端判内容冲突、掐连接，就算
    /// 内容逐字节相同也只会回放那条拒绝。每次按下是一条新消息、拿新 id。
    /// </summary>
    /// <remarks>
    /// 移植自 MVP 线 <c>ab346ed</c> 的同名测试。v2 没有自动化面的恢复端点，所以直接按业务服务的
    /// 「补偿清空」，布置用本类的 <see cref="RecoveryVectorHarness"/>。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task RecoveryCanBeRequestedAgainAfterTheServerRefusedTheSession()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySessionRejectionReasonCode = "RECOVERY_SCOPE_MISMATCH");

        Assert.True(harness.Business.CanRequestLoadCompensation);
        Assert.False(await harness.Business.RequestLoadCompensationAsync(
            "旅程还没 Blocked 时的补偿清空。", token));
        await harness.WaitForRecoveryBlockedAsync(token);

        harness.Server.RecoverySessionRejectionReasonCode = null;
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCompensation,
            "the compensation entry to be offered again after the refusal",
            token);
        bool accepted = await harness.Business.RequestLoadCompensationAsync(
            "旅程转 Blocked 之后再请求一次。", token);

        Assert.Empty(harness.Server.RecoveryRequestConflicts);
        Assert.True(accepted);
        await harness.WaitForInboundAsync("LoadCompensationRequested", token);
        var sessionRequests = harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "ExceptionRecoverySessionRequested")
            .ToArray();
        Assert.Equal(2, sessionRequests.Length);
        Assert.NotEqual(sessionRequests[0].MessageId, sessionRequests[1].MessageId);
        foreach (var request in sessionRequests)
        {
            using JsonDocument document = JsonDocument.Parse(request.WireLine);
            Assert.Equal(
                request.MessageId,
                document.RootElement.GetProperty("payload").GetProperty("requestId").GetString());
        }
    }

    /// <summary>
    /// 车辆日志里存着一个请求 id，服务端早就带着另一份内容收过它，而车辆从没收到过拒绝——服务端判
    /// 冲突时是直接掐连接的。所以「收到拒绝再退役」救不了这台车，下一次按下必须根本不去读日志里那个 id。
    /// </summary>
    /// <remarks>移植自 MVP 线 <c>ab346ed</c> 的同名测试，布置同上。</remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ARequestIdTheServerAlreadyHoldsIsNotSentAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string burnedRequestId = "55d12a30-3d36-4f54-9d05-4edb22a3129e";
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.PreloadRecoveryRequestLine(
                burnedRequestId,
                "{\"messageType\":\"ExceptionRecoverySessionRequested\",\"note\":\"an earlier press\"}"),
            persistedRecoverySessionRequestId: burnedRequestId);

        Assert.True(harness.Business.CanRequestLoadCompensation);
        bool accepted = await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token);

        Assert.Empty(harness.Server.RecoveryRequestConflicts);
        Assert.True(accepted);
        await harness.WaitForInboundAsync("LoadCompensationRequested", token);
        Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.MessageId == burnedRequestId);
    }

    /// <summary>
    /// 被拒过的在途装货取消再按一次，得到的应该还是一次拒绝，而不是一次掐连接。取消请求的 messageId
    /// 原来就是由需求与 attempt 算出来的 <c>cancellationId</c>，第二次按下带着同一个 messageId、却是新的
    /// <c>sentAt</c>／<c>verifiedAt</c>——真服务端的 <c>ProtocolInbox</c> 判内容冲突、掐连接
    /// （onboard-hmi#39，与恢复请求那一次 program#49 同形）。服务端拒绝不落工作流行，所以每次发送拿
    /// 新 messageId，逻辑身份 <c>cancellationId</c> 留在 payload 里。
    /// </summary>
    /// <remarks>移植自 MVP 线 <c>297dd81</c>。v2 没有扫码前取消那一段，所以只在在途取消上做。</remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ARefusedLoadCancellationIsRefusedAgainInsteadOfDroppingTheConnection()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationDecision = "REJECTED";
            });
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the load cancellation entry to be offered",
            token);

        Assert.False(await harness.Business.RequestLoadCancellationAsync(
            "装载结果未知，现场申请取消。", token));
        Assert.False(await harness.Business.RequestLoadCancellationAsync(
            "被拒之后又按了一次。", token));

        Assert.Empty(harness.Server.RecoveryRequestConflicts);
        var requests = harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "LoadCancellationStartRequested")
            .ToArray();
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(requests[0].MessageId, requests[1].MessageId);
        Assert.Equal(requests[0].Connection, requests[1].Connection);
        Assert.Equal([AttemptId, AttemptId], harness.Server.ReceivedLoadCancellationAttemptIds);
    }

    /// <summary>
    /// 修正请求没有应答，服务端受理之后另发修正命令。命令还没到时操作员再按一次——按钮一直亮着——原来会带着
    /// 由 <c>correctionId</c> 算出的同一个 messageId、却是新的 <c>sentAt</c> 与这一次的理由：真服务端先在
    /// <c>ProtocolInbox</c> 判冲突，换了 messageId 又在工作流那一层比 payload。每次发送拿新 messageId，
    /// 理由沿用首发那一次（操作员本来就从日志里的恢复向量读）。
    /// </summary>
    /// <remarks>移植自 MVP 线 <c>297dd81</c> 的同名测试。</remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task ALoadCorrectionPressedAgainBeforeItsCommandArrivesRepeatsTheFirstRequest()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            loadAlreadySettled: true);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCorrection,
            "the load correction entry to be offered",
            token);

        Assert.True(await harness.Business.RequestLoadCorrectionAsync("第一次修正请求。", token));
        Assert.True(await harness.Business.RequestLoadCorrectionAsync("命令还没到，又按了一次。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Server.JudgedRecoveryRequests == 2,
            "the control server to have judged both correction requests",
            token);

        Assert.Empty(harness.Server.RecoveryRequestConflicts);
        var requests = harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "LoadCorrectionRequested")
            .ToArray();
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(requests[0].MessageId, requests[1].MessageId);
        using JsonDocument first = JsonDocument.Parse(requests[0].WireLine);
        using JsonDocument second = JsonDocument.Parse(requests[1].WireLine);
        Assert.Equal(
            first.RootElement.GetProperty("payload").GetRawText(),
            second.RootElement.GetProperty("payload").GetRawText());
    }

    /// <summary>
    /// An attempt id no local context here ever carries, used as the server naming a scope this
    /// vehicle is not in.
    /// </summary>
    private const string ForeignAttemptId = "88888888-8888-4888-8888-888888888888";

    /// <summary>
    /// A recovery response naming an attempt the vehicle does not hold is refused whole, and
    /// nothing is opened.
    /// </summary>
    /// <remarks>
    /// The server's name is authoritative, so a disagreement is not something to reconcile by
    /// quietly preferring the local record -- which would be the vehicle acting on one scope while
    /// the server settles another. It is also not a reason to switch to the server's: the vehicle
    /// has no context under that id, so following it would mean fabricating one.
    /// </remarks>
    [Fact]
    public async Task ARecoveryResponseNamingAnotherAttemptIsRefusedWhole()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = ForeignAttemptId,
            cargoInTargetSlots: true);

        Assert.False(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_RESPONSE_SCOPE_MISMATCH", token);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.ResultsOfType("LoadCompensationRequested"));
    }

    /// <summary>
    /// A <c>null</c> attempt id on the recovery messages leaves the local identity unchallenged.
    /// </summary>
    /// <remarks>
    /// <c>null</c> is the server saying this session has no slot operation attached, not the server
    /// saying the vehicle's is wrong. Treating the two the same would refuse every recovery opened
    /// before loading began.
    /// </remarks>
    [Fact]
    public async Task ARecoveryResponseNamingNoAttemptDoesNotChallengeTheLocalOne()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = null);

        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));

        await harness.WaitForInboundAsync("LoadCompensationRequested", token);
        Assert.Equal(AttemptId, AttemptIdOfRequest(harness));
    }

    /// <summary>
    /// No local context at all, and a server naming an attempt: still hard-blocked, and no door is
    /// opened.
    /// </summary>
    /// <remarks>
    /// The identity is read out of the vehicle's own record or it is not had. Synthesising one from
    /// the server's name would let a message decide which slots this vehicle opens, which is the
    /// hole <c>8005-agv-onboard-hmi#36</c> closed.
    /// </remarks>
    [Fact]
    public async Task ANamedAttemptWithNoLocalContextStaysHardBlocked()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = AttemptId,
            cargoInTargetSlots: true,
            nothingOnFile: true);

        Assert.False(harness.Business.CanRequestLoadCompensation);
        Assert.False(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_OPERATION_CONTEXT_MISSING", token);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Empty(harness.ResultsOfType("LoadCompensationRequested"));
    }

    /// <summary>
    /// An armed unload over a settled load stays fail-closed even when the server names the settled
    /// load's attempt.
    /// </summary>
    /// <remarks>
    /// The server's attempt id is an extra condition on the subject
    /// <c>FindRecoveryLoadOperation</c> picks, never a way to pick a different one. Here that
    /// subject is nothing -- the unload is armed and unsettled -- and a name pointing at the settled
    /// load must not reopen the entry the batch 5-15 rule keeps shut. Doing so would also rewrite the
    /// journal's unsettled attempt from the unload to the load, erasing the record of the door
    /// standing open.
    /// </remarks>
    [Fact]
    public async Task AServerNamingTheSettledLoadDoesNotReopenCompensationOverAnArmedUnload()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = AttemptId,
            armedUnloadOverSettledLoad: true);

        Assert.False(harness.Business.CanRequestLoadCompensation);
        Assert.False(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_OPERATION_CONTEXT_MISSING", token);
        Assert.Empty(harness.ResultsOfType("LoadCompensationRequested"));
        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(UnloadAttemptId, after.UnsettledSlotOperationAttemptId);
        Assert.Equal(OperationType.Unload, after.OperationContext!.OperationType);
    }

    /// <summary>
    /// Within one recovery session the server may not change the attempt it named: a session that
    /// opened naming an attempt and then accepts the action naming <c>null</c> is refused whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The local check cannot catch this -- <c>null</c> challenges nothing, and the named value is
    /// the vehicle's own -- so the rule is a separate one: the first value a session gives, <c>null</c>
    /// included, is fixed for that session. The source is the fill rule in commit <c>6ed3564</c>
    /// (<c>8005-agv-program#95</c>): the value follows from the session's <c>demandId</c> and whether
    /// that demand had a slot operation, neither of which can change while a recovery session is
    /// open, so the three messages "should give the same value; a disagreement means two sources, and
    /// is a defect".
    /// </para>
    /// <para>
    /// This is not the same statement as "a <c>null</c> does not challenge the local identity".
    /// That one is the vehicle against the server; this one is the server against itself.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARecoverySessionThatNamesAnAttemptAndThenNullIsRefusedWhole()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoveryAttemptIdByMessageType = new Dictionary<string, string?>
            {
                ["ExceptionRecoverySessionOpened"] = AttemptId,
                ["RecoveryActionAccepted"] = null
            },
            cargoInTargetSlots: true);

        Assert.False(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_RESPONSE_SCOPE_MISMATCH", token);
        Assert.Empty(harness.ResultsOfType("LoadCompensationRequested"));
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// The mirror: a session that opened naming <c>null</c> and then accepts the action naming an
    /// attempt is refused whole too.
    /// </summary>
    /// <remarks>
    /// Named apart from the other direction because the two fail differently if the rule is written
    /// as "a later non-null must match an earlier non-null": that version passes this case, since the
    /// first value it would compare against is absent.
    /// </remarks>
    [Fact]
    public async Task ARecoverySessionThatNamesNullAndThenAnAttemptIsRefusedWhole()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoveryAttemptIdByMessageType = new Dictionary<string, string?>
            {
                ["ExceptionRecoverySessionOpened"] = null,
                ["RecoveryActionAccepted"] = AttemptId
            },
            cargoInTargetSlots: true);

        Assert.False(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_RESPONSE_SCOPE_MISMATCH", token);
        Assert.Empty(harness.ResultsOfType("LoadCompensationRequested"));
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// A held OPEN recovery session snapshot naming another attempt closes the entry that snapshot
    /// would otherwise offer, and a press through it is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Once an OPEN snapshot is held, a press builds its scope from the snapshot instead of asking the
    /// server again, and the entries are offered off the snapshot too. The snapshot's own attempt id is
    /// what gates that. The double's OPEN snapshot allows <c>FORCED_MECHANICAL_RECOVERY</c> and not
    /// compensation, so that is the entry examined: a compensation entry would be shut by the
    /// allowed-actions list whatever the attempt said.
    /// </para>
    /// <para>
    /// The control half runs the identical scenario with the snapshot naming nothing, and the entry is
    /// open -- so the attempt id is the only thing that differs between the two, and the only thing
    /// the closed entry can be put down to. On the request path the refusal is also reached by the
    /// scope rebuilt from the snapshot, which carries the same attempt id; the entry is where the
    /// snapshot's check is the sole guard.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARecoverySessionSnapshotNamingAnotherAttemptIsRefusedOnTheSnapshotPath()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        foreach ((string? named, bool offered) in new[] { ((string?)null, true), (ForeignAttemptId, false) })
        {
            await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
                token,
                server =>
                {
                    server.RecoverySlotOperationAttemptId = named;
                    server.RecoverySessionSnapshotStatesAfterOpened = ["OPEN"];
                },
                cargoInTargetSlots: true);

            // A resume press opens the session. It is used rather than a press on the entry under
            // examination because it leaves no recovery vector behind, and a vector would open that
            // entry on its own. The OPEN snapshot comes after the opened response.
            await harness.Business.RequestResumeAfterRepairAsync(
                "现场维修完成，申请恢复原仓位操作。", token);
            await RecoveryVectorHarness.WaitUntilAsync(
                () => harness.Server.SentRecoverySessionSnapshots.Count == 1
                    && harness.Business.CanRequestForcedMechanicalRecovery == offered,
                $"the OPEN snapshot naming {named ?? "null"} to leave the entry {(offered ? "open" : "shut")}",
                token);

            if (offered)
            {
                continue;
            }

            int blockedBefore = harness.RecoveryBlockedCount;
            Assert.False(await harness.Business.RequestForcedMechanicalRecoveryAsync(
                "现场确认仓门无法电动解锁，申请强制机械恢复。", token));
            await harness.WaitForRecoveryBlockedAsync("RECOVERY_RESPONSE_SCOPE_MISMATCH", token);
            Assert.True(harness.RecoveryBlockedCount > blockedBefore);

            // One session request: the second press went through the held snapshot.
            Assert.Single(harness.ResultsOfType("ExceptionRecoverySessionRequested"));
            Assert.Empty(harness.ResultsOfType("ForcedMechanicalRecoveryResult"));
            Assert.Equal(0, harness.Io.UnlockCount);
        }
    }

    /// <summary>
    /// The same refusal on the <c>RESUME_AFTER_REPAIR</c> path, which reaches the server through a
    /// different method and would otherwise have no check at all.
    /// </summary>
    [Fact]
    public async Task ResumeAfterRepairRefusesARecoveryResponseNamingAnotherAttempt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = ForeignAttemptId,
            cargoInTargetSlots: true);

        Assert.False(await harness.Business.RequestResumeAfterRepairAsync(
            "现场确认仓门已修复，申请续作原操作。", token));

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_RESPONSE_SCOPE_MISMATCH", token);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// The <c>slotOperationAttemptId</c> on the single compensation request the vehicle sent.
    /// </summary>
    private static string? AttemptIdOfRequest(RecoveryVectorHarness harness)
    {
        using JsonDocument document = JsonDocument.Parse(
            Assert.Single(harness.ResultsOfType("LoadCompensationRequested")));
        return document.RootElement.GetProperty("payload")
            .GetProperty("slotOperationAttemptId").GetString();
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
        private readonly List<WireToGateOperatorEvent> _recoveryBlockedEvents;
        private readonly bool _ownsServer;
        private readonly MutableSafetySignalProvider _safety;

        private RecoveryVectorHarness(
            FakeControlServer server,
            bool ownsServer,
            FakeIoModuleClient io,
            WireToGateSessionService session,
            WireToGateBusinessService business,
            SqliteWireToGateJournal journal,
            List<WireToGateOperatorEvent> recoveryBlockedEvents,
            RecordingLogger logger,
            MutableSafetySignalProvider safety)
        {
            Server = server;
            Logger = logger;
            _safety = safety;
            _ownsServer = ownsServer;
            Io = io;
            _session = session;
            Business = business;
            _journal = journal;
            _recoveryBlockedEvents = recoveryBlockedEvents;
        }

        public FakeControlServer Server { get; }

        public FakeIoModuleClient Io { get; }

        public WireToGateBusinessService Business { get; }

        /// <summary>What the onboard logged, for a test that has to wait on a step nothing else shows.</summary>
        public RecordingLogger Logger { get; }

        /// <summary>
        /// The double every harness stands up on its own. A test that has to keep the double across
        /// a vehicle restart builds it here too, so the two cannot drift apart.
        /// </summary>
        public static FakeControlServer NewServer() =>
            new(IPAddress.Loopback)
            {
                RequireSafeSafetyForReadiness = true,
                SendReadinessAfterRecoveryAck = true,
                RespondToRecoveryRequests = true,
                SendRecoveryVectorCommandAfterRecoveryAction = true,
                RecoveryVectorSlotOperationAttemptId = AttemptId
            };

        /// <param name="cargoInTargetSlots">
        /// Puts cargo in slots 1 and 2. Without it the clear reaches a safe finish without pulsing
        /// anything, because the executor short-circuits an already-empty slot -- which would make
        /// <c>UnlockCount == 0</c> true of a fully executed command as well as a refused one, and
        /// so worth nothing as an assertion. The refusal tests set it; the tests that want a
        /// COMPLETED outcome leave it alone.
        /// </param>
        /// <param name="existingServer">
        /// A double the caller owns and disposes. One vehicle restart is two harnesses over the
        /// same journal, and the server they meet has to be the same one across it -- so the second
        /// harness takes the double rather than standing up a fresh one with no memory.
        /// </param>
        /// <param name="journalPath">
        /// The journal file to open. Passing the first harness's path is what makes the second one
        /// a restart of the same vehicle rather than a different vehicle.
        /// </param>
        /// <param name="baselineRevision">
        /// The capability and safety revisions the session starts from. A restarted vehicle comes
        /// back with a higher baseline: the journal already holds the revision the first run
        /// advanced to.
        /// </param>
        /// <param name="restart">
        /// This harness is the same vehicle coming back up over a journal an earlier harness left
        /// behind, so the seeded state is not written again -- seeding it would erase exactly what
        /// the restart is meant to carry over.
        /// </param>
        /// <param name="lockerWaitTimesOut">
        /// Every wait on a locker's feedback times out: a pulse goes out and the executor then
        /// cannot confirm the lock released, the ADR-cross-0058 decision 2 failure after door IO.
        /// </param>
        /// <param name="nothingOnFile">
        /// Seeds neither an armed operation nor a settled load: a vehicle with no record at all of
        /// the attempt a server might name. The case the batch 5-15 rule and the attempt check both
        /// have to leave hard-blocked.
        /// </param>
        public static async Task<RecoveryVectorHarness> StartAsync(
            CancellationToken cancellationToken,
            Action<FakeControlServer>? configure = null,
            long seededForcedRecoveryGeneration = 0,
            bool cargoInTargetSlots = false,
            string? persistedRecoverySessionRequestId = null,
            bool loadAlreadySettled = false,
            bool armedUnloadOverSettledLoad = false,
            FakeControlServer? existingServer = null,
            string? journalPath = null,
            long baselineRevision = 1,
            bool restart = false,
            bool nothingOnFile = false,
            IReadOnlyList<int>? seededForcedIsolation = null,
            bool lockerWaitTimesOut = false,
            Func<IWireToGateJournal, IWireToGateJournal>? wrapJournal = null)
        {
            bool ownsServer = existingServer is null;
            FakeControlServer server = existingServer ?? NewServer();
            configure?.Invoke(server);

            try
            {
                FakeIoModuleClient io = new() { LockerWaitTimesOut = lockerWaitTimesOut };
                if (cargoInTargetSlots)
                {
                    io.SetCargoPresent(0, true);
                    io.SetCargoPresent(1, true);
                }

                if (armedUnloadOverSettledLoad)
                {
                    // The unload has not emptied slot 3 yet, so the interrupted settlement that runs
                    // on this seeded journal reports UNKNOWN and leaves the unload unsettled -- which
                    // is the state this case is about. Empty it, and the settlement would report
                    // COMPLETED and clear the very entry the test is examining.
                    io.SetCargoPresent(2, true);
                }

                RecordingLogger logger = new();
                MutableSafetySignalProvider safety = new();
                string databasePath = journalPath ?? Path.Combine(
                    Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
                Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

                SqliteWireToGateJournal journal = new(databasePath);
                WireToGateSessionService session = new(
                    new WireToGateSessionOptions(
                        "127.0.0.1",
                        server.Port,
                        "AGV-8005-01",
                        Guid.NewGuid().ToString("D"),
                        new string('a', 40),
                        CredentialVariable,
                        G2SessionTimeouts.Connect,
                        TimeSpan.FromSeconds(2),
                        baselineRevision,
                        baselineRevision,
                        "eight-slot-v1",
                        "eight-slot-modbus-v1",
                        SupportsBatchUnlock: false),
                    io,
                    wrapJournal?.Invoke(journal) ?? journal,
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
                    TimeSpan.FromMilliseconds(500),
                    new WireToGateRecoveryOptions(
                        ResumeAfterRepairEnabled: true,
                        ProofVariable,
                        "MAINTENANCE_ADMINISTRATOR",
                        "CONFIGURED_PROOF"));

                WireToGateRecoveryOperationContext loadContext = new(
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
                    new string('0', 64));

                await journal.InitializeAsync(cancellationToken);
                WireToGateRecoveryOperationContext load = new(
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
                    new string('0', 64));
                // The unload the vehicle is in the middle of at the gate, after that load completed:
                // slot 3 is open, and the load's identity is still in
                // LastCompletedLoadOperationContext because MarkResultRecordedAsync put it there.
                WireToGateRecoveryOperationContext unload = new(
                    UnloadCommandMessageId,
                    null,
                    1,
                    DateTimeOffset.UtcNow,
                    DemandId,
                    OperationSessionId,
                    UnloadAttemptId,
                    OperationType.Unload,
                    [3],
                    1,
                    false,
                    new string('0', 64));
                // A settled load is what MarkResultRecordedAsync leaves behind: no armed operation
                // and no unsettled attempt, with the identity kept in
                // LastCompletedLoadOperationContext. That is the state the vehicle is in when it has
                // reported COMPLETED and the server has judged the same attempt RecoveryRequired.
                // A restart reopens the journal the first run left behind; seeding it again would
                // erase exactly what the restart is meant to carry over.
                if (restart)
                {
                    return await FinishStartAsync(
                        server,
                        ownsServer,
                        io,
                        journal,
                        session,
                        business,
                        safety,
                        logger,
                        loadAlreadySettled,
                        armedUnloadOverSettledLoad,
                        nothingOnFile,
                        cancellationToken);
                }

                await journal.UpdateRecoveryStateAsync(
                    _ => new WireToGateRecoveryState(
                        armedUnloadOverSettledLoad
                            ? UnloadAttemptId
                            : loadAlreadySettled || nothingOnFile ? null : AttemptId,
                        armedUnloadOverSettledLoad
                            ? WireToGateRecoveryCheckpoint.ActiveUnlockSet
                            : loadAlreadySettled
                                ? WireToGateRecoveryCheckpoint.ResultRecorded
                                : WireToGateRecoveryCheckpoint.Prepared,
                        armedUnloadOverSettledLoad ? [3] : [],
                        seededForcedRecoveryGeneration,
                        [])
                    {
                        RecoverySessionRequestId = persistedRecoverySessionRequestId,
                        OperationContext = armedUnloadOverSettledLoad
                            ? unload
                            : loadAlreadySettled || nothingOnFile ? null : load,
                        LastCompletedLoadOperationContext =
                            loadAlreadySettled || armedUnloadOverSettledLoad ? load : null,
                        // An earlier forced recovery, acknowledged and never cleared by a hardware
                        // recovery record: its session and action are not this test's.
                        ForcedIsolation = seededForcedIsolation is null
                            ? null
                            : new WireToGateForcedIsolation(
                                "99999999-9999-4999-8999-999999999999",
                                "abababab-abab-4bab-8bab-abababababab",
                                seededForcedIsolation)
                    },
                    cancellationToken);

                return await FinishStartAsync(
                    server,
                    ownsServer,
                    io,
                    journal,
                    session,
                    business,
                    safety,
                    logger,
                    loadAlreadySettled,
                    armedUnloadOverSettledLoad,
                    nothingOnFile,
                    cancellationToken);
            }
            catch
            {
                if (ownsServer)
                {
                    await server.DisposeAsync();
                }

                throw;
            }
        }

        private static async Task<RecoveryVectorHarness> FinishStartAsync(
            FakeControlServer server,
            bool ownsServer,
            FakeIoModuleClient io,
            SqliteWireToGateJournal journal,
            WireToGateSessionService session,
            WireToGateBusinessService business,
            MutableSafetySignalProvider safety,
            RecordingLogger logger,
            bool loadAlreadySettled,
            bool armedUnloadOverSettledLoad,
            bool nothingOnFile,
            CancellationToken cancellationToken)
        {
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

            // Subscribed before the pump starts, so no refusal can be published into the gap.
            List<WireToGateOperatorEvent> blocked = [];
            business.OperatorEventPublished += (_, args) =>
            {
                if (args.Value.Kind == "RECOVERY_BLOCKED")
                {
                    lock (blocked)
                    {
                        blocked.Add(args.Value);
                    }
                }
            };
            business.Start();

            // The CanRequest* gates read a cached copy of the recovery state that the pump
            // refreshes on its first pass; the seeded journal alone does not answer them. The
            // request path reads the journal directly, so this wait is what makes the gates
            // meaningful to assert rather than what makes the request work.
            if (nothingOnFile)
            {
                // No operation snapshot and no entry can turn true here, so neither is a signal the
                // pump ran. The one SafetyStateChanged it sends when it starts is.
                await WaitUntilAsync(
                    () => server.Received.Any(item => item.MessageType == "SafetyStateChanged"),
                    "the business pump to start over an empty recovery journal",
                    cancellationToken);
            }
            else if (loadAlreadySettled)
            {
                // Nothing is armed, so no operation snapshot is surfaced. The correction entry
                // already reads LastCompletedLoadOperationContext on this branch, so it turning
                // true is the pump having refreshed the cached recovery state -- a different code
                // path from the compensation entry the settled-load tests assert on.
                await WaitUntilAsync(
                    () => business.CanRequestLoadCorrection,
                    "the business pump to surface the seeded settled load",
                    cancellationToken);
            }
            else
            {
                await WaitUntilAsync(
                    () => business.CurrentOperationSnapshot?.Stage
                        == WireToGateHmiOperationStage.RecoveryRequired,
                    "the business pump to surface the seeded recovery state",
                    cancellationToken);
                Assert.Equal(
                    armedUnloadOverSettledLoad ? UnloadAttemptId : AttemptId,
                    business.CurrentOperationSnapshot!.SlotOperationAttemptId);
            }

            return new RecoveryVectorHarness(
                server, ownsServer, io, session, business, journal, blocked, logger, safety);
        }

        /// <summary>The vehicle's motion is unknown again, the way it was across the handshake.</summary>
        public void VehicleMotionUnknown() => _safety.SetUnknown();

        public void VehicleStopped() => _safety.SetStopped();

        /// <inheritdoc cref="MutableSafetySignalProvider.StopsForOnlyTheFirstCommandCheck"/>
        public void VehicleStartsMovingAfterTheFirstCommandCheck() => _safety.StopsForOnlyTheFirstCommandCheck();

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(
            CancellationToken cancellationToken) =>
            _journal.ReadRecoveryStateAsync(cancellationToken);

        /// <summary>The outgoing message the journal holds under <paramref name="deduplicationKey"/>.</summary>
        public Task<WireToGateDurableMessage?> ReadOutgoingAsync(
            string deduplicationKey,
            CancellationToken cancellationToken) =>
            _journal.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);

        /// <summary>
        /// Whether the vehicle has recorded the server's DurableAck for one of its own messages: the
        /// point after which a restart must not send it again.
        /// </summary>
        public async Task<bool> IsOutgoingAcknowledgedAsync(string messageId, CancellationToken cancellationToken) =>
            (await _journal.ReadOutgoingByMessageIdAsync(messageId, cancellationToken))?.Acknowledged == true;

        /// <summary>
        /// Rewrites the recovery state on disk under the running vehicle: what a journal looks like
        /// after a restart lost or moved part of it.
        /// </summary>
        public async Task RewriteRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState> change,
            CancellationToken cancellationToken)
        {
            WireToGateRecoveryState state = await _journal.ReadRecoveryStateAsync(cancellationToken);
            await _journal.UpdateRecoveryStateAsync(_ => change(state), cancellationToken);
        }

        /// <summary>
        /// Takes the seeded load through a whole forced mechanical recovery -- request, authorization,
        /// the operator's confirmation, the server's acknowledgement -- so its slots end up physically
        /// unknown.
        /// </summary>
        public async Task IsolateByForcedRecoveryAsync(CancellationToken cancellationToken)
        {
            Assert.True(await Business.RequestForcedMechanicalRecoveryAsync(
                "现场确认仓门无法电动解锁，申请强制机械恢复。", cancellationToken));
            await ConfirmForcedMechanicalRecoveryAsync(cancellationToken);
            await WaitUntilAsync(
                () => Business.PhysicallyUnknownSlots.Count > 0,
                "the acknowledged forced recovery to leave its slots physically unknown",
                cancellationToken);
        }

        /// <summary>
        /// Waits for the authorized forced recovery to be waiting on the operator, then confirms
        /// the isolation and the manual extraction the way the HMI button does.
        /// </summary>
        public async Task ConfirmForcedMechanicalRecoveryAsync(CancellationToken cancellationToken)
        {
            await WaitUntilAsync(
                () => Business.CanConfirmForcedMechanicalRecovery,
                "the authorized forced recovery to wait for the operator's confirmation",
                cancellationToken);
            Assert.True(await Business.ConfirmForcedMechanicalRecoveryAsync(cancellationToken));
        }

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

        /// <summary>
        /// Waits for <paramref name="predicate"/>, and fails naming what never happened.
        /// </summary>
        /// <remarks>
        /// Letting the linked token cancel the delay would surface as "A task was canceled", which
        /// says nothing about which condition was being waited on -- and these waits are how the
        /// refusal tests establish that a guard ran at all, so their timeout is a real result and
        /// deserves to read like one.
        /// </remarks>
        public static async Task WaitUntilAsync(
            Func<bool> predicate,
            string expectation,
            CancellationToken cancellationToken)
        {
            StallAwareDeadline deadline = new(TimeSpan.FromSeconds(5));
            while (!predicate())
            {
                if (deadline.HasExpired)
                {
                    Assert.Fail($"Timed out after {deadline.Describe()} waiting for: {expectation}");
                }

                await deadline.PollAsync(cancellationToken);
            }
        }

        public async Task WaitForInboundAsync(
            string messageType,
            CancellationToken cancellationToken)
        {
            await WaitUntilAsync(
                () => Server.Received.Any(item => item.MessageType == messageType),
                $"the control server to receive {messageType}",
                cancellationToken);
        }

        /// <summary>
        /// Waits for a guard to publish its refusal.
        /// </summary>
        /// <remarks>
        /// A refusal produces nothing on the wire, so waiting for the server to have written the
        /// command proves only that the server wrote it -- the onboard might not have read it yet,
        /// and every "nothing happened" assertion would then hold for the wrong reason. The
        /// RECOVERY_BLOCKED event is published by the guard itself, so waiting on it is waiting for
        /// the refusal to have actually been decided.
        /// </remarks>
        public int RecoveryBlockedCount
        {
            get
            {
                lock (_recoveryBlockedEvents)
                {
                    return _recoveryBlockedEvents.Count;
                }
            }
        }

        public async Task WaitForRecoveryBlockedAsync(CancellationToken cancellationToken)
        {
            await WaitUntilAsync(
                () =>
                {
                    lock (_recoveryBlockedEvents)
                    {
                        return _recoveryBlockedEvents.Count > 0;
                    }
                },
                "a guard to publish a RECOVERY_BLOCKED operator event",
                cancellationToken);
        }

        /// <summary>
        /// Waits for a RECOVERY_BLOCKED event naming <paramref name="reasonCode"/>, and returns it.
        /// </summary>
        /// <remarks>
        /// The request paths turn a guard's exception into <c>false</c> plus this event, so the
        /// event text is where the reason is observable from outside. Waiting on the code rather
        /// than on any refusal is what keeps a test from passing on a different guard's refusal.
        /// </remarks>
        public async Task<WireToGateOperatorEvent> WaitForRecoveryBlockedAsync(
            string reasonCode,
            CancellationToken cancellationToken)
        {
            await WaitUntilAsync(
                () =>
                {
                    lock (_recoveryBlockedEvents)
                    {
                        return _recoveryBlockedEvents.Any(
                            item => item.Message.Contains(reasonCode, StringComparison.Ordinal));
                    }
                },
                $"a guard to refuse with {reasonCode}",
                cancellationToken);

            lock (_recoveryBlockedEvents)
            {
                return _recoveryBlockedEvents.First(
                    item => item.Message.Contains(reasonCode, StringComparison.Ordinal));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Business.DisposeAsync();
            await _session.DisposeAsync();
            if (_ownsServer)
            {
                await Server.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Unknown until <see cref="SetStopped"/>, then stopped until <see cref="SetUnknown"/>, and always
    /// freshly observed.
    /// </summary>
    private sealed class MutableSafetySignalProvider : IVehicleSafetySignalProvider
    {
        private int _stopped;
        private int _commandChecksLeft = -1;

        public void SetStopped()
        {
            Interlocked.Exchange(ref _commandChecksLeft, -1);
            Interlocked.Exchange(ref _stopped, 1);
        }

        public void SetUnknown()
        {
            Interlocked.Exchange(ref _commandChecksLeft, -1);
            Interlocked.Exchange(ref _stopped, 0);
        }

        /// <summary>
        /// Stopped for the first motion check a recovery command's handling makes, unknown from its
        /// second on: the vehicle starts moving between the two (onboard-hmi#129 C-2). Reads made
        /// anywhere else -- the safety pump, the request paths -- see it stopped throughout.
        /// </summary>
        public void StopsForOnlyTheFirstCommandCheck()
        {
            Interlocked.Exchange(ref _stopped, 1);
            Interlocked.Exchange(ref _commandChecksLeft, 1);
        }

        public VehicleSafetySignal Read() => new(
            Volatile.Read(ref _stopped) == 1 && !IsACommandCheckPastTheFirst()
                ? VehicleMotionState.Stopped
                : VehicleMotionState.Unknown,
            DateTimeOffset.UtcNow,
            "RECOVERY_VECTOR_G2_TEST");

        private bool IsACommandCheckPastTheFirst() =>
            Volatile.Read(ref _commandChecksLeft) >= 0
            && Environment.StackTrace.Contains("HandleRecoveryVectorCommandAsync", StringComparison.Ordinal)
            && Interlocked.Decrement(ref _commandChecksLeft) < 0;
    }

}
