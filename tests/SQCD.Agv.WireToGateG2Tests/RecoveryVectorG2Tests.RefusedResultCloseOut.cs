using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A recovery vector result the control server refuses for good, given up by the outbox (onboard-hmi#254 part 1): what
/// the vehicle does with the vector it leaves behind (part 2, folded into onboard-hmi#150).
/// </summary>
/// <remarks>
/// <para>
/// <c>BUSINESS_ID_CONTENT_CONFLICT</c> on a recovery result means the server's workflow already holds another first
/// result (control-server <c>OnboardRecoveryCoordinator.cs:204-207</c>), so the two ends disagree about this recovery and
/// nothing on the wire will ever settle the vector here. Rulings of 2026-10-05: a verified maintainer ends it after a
/// manual check, an entry the maintenance switch does not gate; until then a new slot operation command is refused, so
/// the executor's fresh journal does not end it silently; a refused forced mechanical recovery goes on to its isolation.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    private static readonly int[] NewLoadSlots = [5];

    public enum RefusedVector
    {
        FaultCargoHandoff,
        LoadCompensation,
        LoadCorrection
    }

    public static TheoryData<RefusedVector> RefusedVectors =>
    [
        RefusedVector.FaultCargoHandoff,
        RefusedVector.LoadCompensation,
        RefusedVector.LoadCorrection
    ];

    /// <summary>
    /// The vector stays -- nothing settles it on its own -- and the operator is sent to the manual check that ends it.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedVectors))]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ARefusedRecoveryResultSendsTheOperatorToTheManualCheck(RefusedVector refused)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string resultType = ResultTypeOf(refused);
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.RecoveryVectorSlotOperationAttemptId = AttemptId;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [resultType] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            },
            loadAlreadySettled: refused == RefusedVector.LoadCorrection);

        await RunRefusedVectorAsync(harness, refused, token);
        WireToGateRecoveryVectorContext vector = await WaitForGivenUpResultAsync(harness, token);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.OperatorEvents.Any(item =>
                item.Message.Contains("人工核对后结束此恢复", StringComparison.Ordinal)),
            "the operator to be sent to the manual check that ends the refused recovery",
            token);
        Assert.Equal(vector, (await harness.ReadRecoveryStateAsync(token)).RecoveryVector);
    }

    /// <summary>
    /// A correction over a settled load holds no unsettled attempt, so the server answers the next handshake READY and
    /// may command a new load. Executed, it would start from a fresh journal and drop the refused vector unseen; it is
    /// refused instead, and the vector stays for the manual check.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ANewSlotCommandWhileARefusedRecoveryAwaitsItsCheckIsRefused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.RecoveryVectorSlotOperationAttemptId = AttemptId;
                server.RefusedRecoveryResultsWereReconciledByAnother = true;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LoadCorrectionResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            },
            loadAlreadySettled: true);
        await RunRefusedVectorAsync(harness, RefusedVector.LoadCorrection, token);
        WireToGateRecoveryVectorContext vector = await WaitForGivenUpResultAsync(harness, token);

        await harness.Session.Client.DisconnectAsync();
        WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);
        Assert.True(
            reconnected.Readiness == WireToGateSessionReadiness.Ready,
            $"precondition: the server answers READY, got {reconnected.Readiness} [{string.Join(",", reconnected.ReasonCodes)}] report={harness.Server.ReceivedEnvelopes.Last(item => item.MessageType == "RecoveryStateReport").WireLine}");

        const string newAttemptId = "9a9a9a9a-9a9a-4a9a-8a9a-9a9a9a9a9a9a";
        await harness.Server.SendCommandAsync(
            "SlotOperationCommand",
            Guid.NewGuid().ToString("D"),
            new
            {
                demandId = "9b9b9b9b-9b9b-4b9b-8b9b-9b9b9b9b9b9b",
                operationSessionId = OperationSessionId,
                slotOperationAttemptId = newAttemptId,
                operationType = "LOAD",
                slots = NewLoadSlots,
                expectedBasketCount = 1,
                expectedFinalPhysicalState = "OCCUPIED",
                commandContentSha256 = new string('0', 64)
            },
            correlationId: Guid.NewGuid().ToString("D"));

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ResultsOfType("SlotOperationCommandRejected").Any(line =>
                line.Contains(newAttemptId, StringComparison.Ordinal)),
            "the new slot command to be refused while the refused recovery awaits its check",
            token);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Equal(vector, (await harness.ReadRecoveryStateAsync(token)).RecoveryVector);
    }

    /// <summary>
    /// A refused forced mechanical recovery result: the server holds a result for this recovery and waits for the
    /// hardware record, which a person submits anyway, so the isolation is recorded as on an acknowledgement and the
    /// hardware record entry appears.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ARefusedForcedRecoveryResultIsolatesTheSlotsAndOffersTheHardwareRecord()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoveryVectorSlotOperationAttemptId = AttemptId;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ForcedMechanicalRecoveryResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            },
            cargoInTargetSlots: true);

        Assert.True(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "现场确认仓门无法电动解锁，申请强制机械恢复。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanConfirmForcedMechanicalRecovery,
            "the authorized forced recovery to wait for the operator's confirmation",
            token);
        await harness.Business.ConfirmForcedMechanicalRecoveryAsync(token);
        await WaitForGivenUpResultAsync(harness, token);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanSubmitHardwareRecoveryRecord,
            "the refused forced recovery to offer the hardware record",
            token);
        WireToGateRecoveryState isolated = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(isolated.RecoveryVector);
        Assert.Equal([1, 2], isolated.ForcedIsolation?.PhysicallyUnknownSlots);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    private static string ResultTypeOf(RefusedVector refused) => refused switch
    {
        RefusedVector.FaultCargoHandoff => "FaultCargoRecoveryResult",
        RefusedVector.LoadCompensation => "LoadCompensationResult",
        _ => "LoadCorrectionResult"
    };

    /// <summary>Waits for the vector's result to be given up, and returns the vector it leaves on file.</summary>
    private static async Task<WireToGateRecoveryVectorContext> WaitForGivenUpResultAsync(
        RecoveryVectorHarness harness,
        CancellationToken token)
    {
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.OperatorEvents.Any(item => item.Kind == "DURABLE_MESSAGE_ABANDONED"),
            "the refused recovery result to be given up",
            token);
        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        WireToGateRecoveryVectorContext vector = state.RecoveryVector
            ?? throw new InvalidOperationException("The refused result left no vector on file.");
        WireToGateDurableMessage row = Assert.IsType<WireToGateDurableMessage>(
            await harness.ReadOutgoingAsync($"recovery-vector-result:{vector.VectorType}:{vector.PrimaryId}", token));
        Assert.Equal("BUSINESS_ID_CONTENT_CONFLICT", row.AbandonedReasonCode);
        return vector;
    }

    private static async Task RunRefusedVectorAsync(RecoveryVectorHarness harness, RefusedVector vector, CancellationToken token)
    {
        switch (vector)
        {
            case RefusedVector.FaultCargoHandoff:
                Assert.True(await harness.Business.RequestFaultCargoHandoffAsync("现场确认故障仓货物需要交接处理。", token));
                break;
            case RefusedVector.LoadCompensation:
                Assert.True(await harness.Business.RequestLoadCompensationAsync("现场确认装货无法继续，申请补偿清空目标仓位。", token));
                break;
            default:
                await RecoveryVectorHarness.WaitUntilAsync(
                    () => harness.Business.CanRequestLoadCorrection,
                    "the load correction entry to be offered",
                    token);
                Assert.True(await harness.Business.RequestLoadCorrectionAsync("现场确认需要修正已完成的装货结果。", token));
                // The double accepts a correction request without commanding it; the command is the server's next step.
                WireToGateRecoveryVectorContext correction = (await harness.ReadRecoveryStateAsync(token)).RecoveryVector
                    ?? throw new InvalidOperationException("The correction request prepared no vector.");
                await SendCorrectionCommandAsync(harness.Server, correction, Guid.NewGuid().ToString("D"));
                break;
        }
    }
}
