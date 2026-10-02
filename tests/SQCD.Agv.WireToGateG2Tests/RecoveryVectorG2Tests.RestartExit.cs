using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// After a restart the vehicle tells the operator a compensation's authorization may be lost, and the press it
    /// asks for works: it reaches the server and the compensation is carried out once (onboard-hmi#236 review M1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The restart forgets the exception recovery session snapshot, and the server does not send it again -- it was
    /// acknowledged long ago. The press path required one for any vector on file and refused with
    /// <c>RECOVERY_SESSION_STATE_PENDING</c>, so the entry was lit, the screen said "press again", and the press sent
    /// nothing: stuck for good, the very state this ticket exists to remove, one restart further on.
    /// </para>
    /// <para>
    /// A compensation already prepared and with no command bound needs no snapshot to ask for its authorization: the
    /// session, demand and attempt are on the vector, and the server checks them.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AfterARestartTheShownCompensationPressReachesTheServerAndRunsOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Path.Combine(
            Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.SendLoadCompensationCommandOnRequest = true;
        server.LoadCompensationRequestsToLose = 1;

        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            loadAlreadySettled: true))
        {
            Assert.True(await beforeRestart.Business.RequestLoadCompensationAsync(
                "现场确认装货无法继续，申请补偿清空目标仓位。", token));
            await RecoveryVectorHarness.WaitUntilAsync(
                () => !beforeRestart.Session.Current.Connected,
                "the vehicle to see the connection the request went down with drop",
                token);
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        serverAfterRestart.SendLoadCompensationCommandOnRequest = true;
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            restart: true);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.OperatorEvents.Any(item => item.Kind == "RECOVERY_AUTHORIZATION_UNKNOWN"),
            "the operator to be told the compensation's authorization may be lost",
            token);
        Assert.True(afterRestart.Business.CanRequestLoadCompensation);

        Assert.True(await afterRestart.Business.RequestLoadCompensationAsync(
            "重启后确认现场，再按一次补偿。", token));

        JsonElement result = await afterRestart.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(ActionIdFor(CompensateLoadAction), result.GetProperty("recoveryActionId").GetString());
        Assert.Single(afterRestart.ResultsOfType("LoadCompensationResult"));
        Assert.Single(afterRestart.ResultsOfType("LoadCompensationRequested"));
        Assert.Empty(afterRestart.ResultsOfType("RecoveryActionSubmitted"));
        Assert.Equal(1, serverAfterRestart.LoadCompensationCommandsWritten);
        Assert.Equal(0, afterRestart.RecoveryBlockedCount);
        Assert.Empty(serverAfterRestart.RecoveryRequestConflicts);
    }

    /// <summary>
    /// The same for a load correction: after a restart the shown press reaches the server again, as the same
    /// correction (onboard-hmi#236 review M1).
    /// </summary>
    /// <remarks>
    /// The correction is asked for with the first press's operator and reason, not the one pressing now: the server
    /// compares the whole request with the one it may have accepted (<c>UpsertSimpleWorkflowAsync</c>), so a different
    /// operator would be a content conflict, and the connection would be dropped.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task AfterARestartTheShownCorrectionPressReachesTheServerAsTheSameCorrection()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Path.Combine(
            Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.LoadCorrectionRequestsToLose = 1;

        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            loadAlreadySettled: true))
        {
            await RecoveryVectorHarness.WaitUntilAsync(
                () => beforeRestart.Business.CanRequestLoadCorrection,
                "the load correction entry to be offered",
                token);
            Assert.True(await beforeRestart.Business.RequestLoadCorrectionAsync(
                "现场确认需要修正已完成的装货结果。", token));
            await RecoveryVectorHarness.WaitUntilAsync(
                () => !beforeRestart.Session.Current.Connected,
                "the vehicle to see the connection the correction request went down with drop",
                token);
        }

        string firstPayload;
        using (JsonDocument first = JsonDocument.Parse(server.ReceivedEnvelopes
            .Single(envelope => envelope.MessageType == "LoadCorrectionRequested").WireLine))
        {
            firstPayload = first.RootElement.GetProperty("payload").GetRawText();
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            restart: true);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.OperatorEvents.Any(item => item.Kind == "RECOVERY_AUTHORIZATION_UNKNOWN"),
            "the operator to be told the correction's authorization may be lost",
            token);
        Assert.Empty(afterRestart.ResultsOfType("LoadCorrectionRequested"));
        Assert.True(afterRestart.Business.CanRequestLoadCorrection);

        Assert.True(await afterRestart.Business.RequestLoadCorrectionAsync("重启后确认现场，再按一次修正。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => serverAfterRestart.JudgedRecoveryRequests == 1,
            "the correction request to reach the server and be judged there",
            token);

        using JsonDocument again = JsonDocument.Parse(Assert.Single(afterRestart.ResultsOfType("LoadCorrectionRequested")));
        Assert.Equal(firstPayload, again.RootElement.GetProperty("payload").GetRawText());
        Assert.Empty(serverAfterRestart.RecoveryRequestConflicts);
    }
}
