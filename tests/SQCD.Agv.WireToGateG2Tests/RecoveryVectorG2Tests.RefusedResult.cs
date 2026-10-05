using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A recovery result the control server refuses for good (onboard-hmi#254): what the vehicle leaves in place, and the
/// governed way out of it: four of the five results here, the load cancellation in
/// <c>LoadCancellationBeforeSublotG2Tests.RefusedResult.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>BUSINESS_ID_CONTENT_CONFLICT</c> on a recovery result means the server's workflow already holds another first
/// result (control-server <c>OnboardRecoveryCoordinator.ProcessResultAsync</c>), so the server's conclusion and this
/// vehicle's disagree. The coordinator's ruling: the forced mechanical recovery goes on to its hardware record, which a
/// person submits anyway; the other four keep their vector until a verified maintainer closes it after checking on site.
/// Neither end is left in a state only a database edit frees.
/// </para>
/// <para>
/// "Back to dispatchable" is read where the vehicle decides it: the next handshake's <c>RecoveryStateReport</c> names
/// nothing unsettled and nothing pending, and the double -- modelling the server's other result as having reconciled the
/// operation (<see cref="FakeControlServer.RefusedRecoveryResultsWereReconciledByAnother"/>) -- answers READY.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
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
    /// The vector stays and its result is given up: nothing settles it on its own, the operator is told, and the manual
    /// close entry appears behind the recovery entries' operator and proof checks. Closed, the vehicle holds nothing for
    /// the server to reconcile and the session comes back READY; the refused result is never sent again.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedVectors))]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ARefusedRecoveryResultKeepsItsVectorUntilAMaintainerClosesItAfterReview(RefusedVector refused)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (string resultType, string vectorType) = refused switch
        {
            RefusedVector.FaultCargoHandoff => ("FaultCargoRecoveryResult", WireToGateRecoveryVectorTypes.FaultCargoHandoff),
            RefusedVector.LoadCompensation => ("LoadCompensationResult", WireToGateRecoveryVectorTypes.LoadCompensation),
            _ => ("LoadCorrectionResult", WireToGateRecoveryVectorTypes.LoadCorrection)
        };
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.RecoveryVectorSlotOperationAttemptId = AttemptId;
                server.SendReadinessAfterRecoveryAck = true;
                server.RefusedRecoveryResultsWereReconciledByAnother = true;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [resultType] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            },
            loadAlreadySettled: refused == RefusedVector.LoadCorrection);

        await RunVectorAsync(harness, refused, token);
        await harness.WaitForResultAsync(resultType, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.ConflictedRecoveryView is not null,
            "the refused result's recovery to wait for a maintainer's review",
            token);

        WireToGateRecoveryState before = await harness.ReadRecoveryStateAsync(token);
        WireToGateRecoveryVectorContext vector = Assert.IsType<WireToGateRecoveryVectorContext>(before.RecoveryVector);
        Assert.Equal(vectorType, vector.VectorType);
        WireToGateDurableMessage row = Assert.IsType<WireToGateDurableMessage>(
            await harness.ReadOutgoingAsync($"recovery-vector-result:{vector.VectorType}:{vector.PrimaryId}", token));
        Assert.Equal("BUSINESS_ID_CONTENT_CONFLICT", row.AbandonedReasonCode);
        Assert.False(row.Acknowledged);
        Assert.Contains(harness.OperatorEvents, item => item.Kind == "DURABLE_MESSAGE_ABANDONED"
            && item.Message.Contains(row.MessageId, StringComparison.Ordinal));
        Assert.Contains(harness.Logger.Entries, entry => entry.Message.Contains("BUSINESS_ID_CONTENT_CONFLICT", StringComparison.Ordinal)
            && entry.Message.Contains(row.MessageId, StringComparison.Ordinal)
            && entry.Message.Contains(row.ContentSha256, StringComparison.Ordinal));
        Assert.Equal("BUSINESS_ID_CONTENT_CONFLICT", harness.Business.ConflictedRecoveryView!.ReasonCode);
        Assert.True(harness.Business.CanCloseConflictedRecoveryAfterReview);

        Assert.True(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(after.RecoveryVector);
        Assert.Null(after.UnsettledSlotOperationAttemptId);
        Assert.Null(harness.Business.ConflictedRecoveryView);
        Assert.False(harness.Business.CanCloseConflictedRecoveryAfterReview);
        Assert.Contains(harness.OperatorEvents, item => item.Kind == "CONFLICTED_RECOVERY_CLOSED");
        Assert.Contains(harness.Logger.Entries, entry => entry.Message.StartsWith(
                "维护人员现场核对后结束服务端拒收结果的恢复：", StringComparison.Ordinal)
            && entry.Message.Contains("operator=maintenance-001", StringComparison.Ordinal));

        await harness.Session.Client.DisconnectAsync();
        WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);
        Assert.Equal(WireToGateSessionReadiness.Ready, reconnected.Readiness);
        using JsonDocument report = JsonDocument.Parse(harness.Server.ReceivedEnvelopes
            .Last(item => item.MessageType == "RecoveryStateReport").WireLine);
        JsonElement payload = report.RootElement.GetProperty("payload");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("unsettledSlotOperationAttemptId").ValueKind);
        Assert.Equal(0, payload.GetProperty("pendingResults").GetArrayLength());
        Assert.Single(harness.ResultsOfType(resultType));
    }

    /// <summary>
    /// The close is the recovery entries' own kind of press: without the administrator proof it is not offered and a
    /// press does nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task TheManualCloseNeedsTheRecoveryProof()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["FaultCargoRecoveryResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
            });
        await RunVectorAsync(harness, RefusedVector.FaultCargoHandoff, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanCloseConflictedRecoveryAfterReview,
            "the manual close to be offered",
            token);

        string? proof = Environment.GetEnvironmentVariable(ProofVariable);
        Environment.SetEnvironmentVariable(ProofVariable, null);
        try
        {
            Assert.False(harness.Business.CanCloseConflictedRecoveryAfterReview);
            Assert.False(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProofVariable, proof);
        }

        Assert.NotNull((await harness.ReadRecoveryStateAsync(token)).RecoveryVector);
        Assert.NotNull(harness.Business.ConflictedRecoveryView);
    }

    /// <summary>
    /// A restart between the refusal and the review loses nothing: the given-up row is on file, and the entry is offered
    /// again from it once the session is back.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task TheManualCloseIsOfferedAgainAfterARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Path.Combine(Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FaultCargoRecoveryResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
        };
        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath))
        {
            await RunVectorAsync(beforeRestart, RefusedVector.FaultCargoHandoff, token);
            await RecoveryVectorHarness.WaitUntilAsync(
                () => beforeRestart.Business.ConflictedRecoveryView is not null,
                "the refused result's recovery to wait for review",
                token);
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            baselineRevision: 2,
            restart: true);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Business.CanCloseConflictedRecoveryAfterReview,
            "the manual close to be offered again after the restart",
            token);

        Assert.Empty(afterRestart.ResultsOfType("FaultCargoRecoveryResult"));
        Assert.True(await afterRestart.Business.CloseConflictedRecoveryAfterReviewAsync(token));
        Assert.Null((await afterRestart.ReadRecoveryStateAsync(token)).RecoveryVector);
    }

    /// <summary>
    /// A refused forced mechanical recovery result: the server holds a result for this recovery and waits for the
    /// hardware record, so the isolation is recorded as on an acknowledgement and the hardware record entry appears. The
    /// record, submitted, clears the isolation; the next handshake reports nothing for the server to reconcile.
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
                server.SendReadinessAfterRecoveryAck = true;
                server.RefusedRecoveryResultsWereReconciledByAnother = true;
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
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanSubmitHardwareRecoveryRecord,
            "the refused forced recovery to offer the hardware record",
            token);

        WireToGateRecoveryState isolated = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(isolated.RecoveryVector);
        Assert.Equal([1, 2], isolated.ForcedIsolation?.PhysicallyUnknownSlots);
        Assert.Equal([1, 2], harness.Business.PhysicallyUnknownSlots);
        Assert.Null(harness.Business.ConflictedRecoveryView);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Contains(harness.OperatorEvents, item => item.Kind == "DURABLE_MESSAGE_ABANDONED");

        Assert.True(await harness.Business.SubmitHardwareRecoveryRecordAsync("更换 1、2 号仓锁体，复测锁反馈正常。", token));
        Assert.Null((await harness.ReadRecoveryStateAsync(token)).ForcedIsolation);

        await harness.Session.Client.DisconnectAsync();
        WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);
        Assert.Equal(WireToGateSessionReadiness.Ready, reconnected.Readiness);
        Assert.Single(harness.ResultsOfType("ForcedMechanicalRecoveryResult"));
    }

    private static async Task RunVectorAsync(RecoveryVectorHarness harness, RefusedVector vector, CancellationToken token)
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
