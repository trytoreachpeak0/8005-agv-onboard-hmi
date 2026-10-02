using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// A compensation request that goes down with the connection is asked for again once the session is
    /// back, and the compensation is carried out (onboard-hmi#236).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>LoadCompensationRequested</c> is a REQUEST with <c>durableBeforeSend=false</c>: the vehicle writes it
    /// to the socket and nothing answers it, no outbox keeps it and no reconnect replays it. The real server
    /// authorizes a compensation only on that request -- <c>RecoveryActionSubmitted</c> leaves the workflow in
    /// AwaitingAuthorization, and nothing on the server moves it on by itself. So a request lost with the link
    /// left both ends waiting for each other for good: the session ACTION_SELECTED, the vector prepared, no
    /// command, and the operator told the compensation had been authorized (hmi#222 rig run 36909685924).
    /// </para>
    /// <para>
    /// The double here is the real server's shape (<see cref="FakeControlServer.SendLoadCompensationCommandOnRequest"/>):
    /// the command answers the request, never the accepted action. The first request is recorded and the
    /// connection closed before anything handles it, so the server holds no authorization; the second, after
    /// the reconnect, earns the command.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationRequestLostWithTheConnectionIsAskedForAgainOnceTheSessionIsBack()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.SendLoadCompensationCommandOnRequest = true;
                server.LoadCompensationRequestsToLose = 1;
            },
            loadAlreadySettled: true);

        Assert.True(harness.Business.CanRequestLoadCompensation);
        long lostGeneration = harness.Session.Current.SessionGeneration
            ?? throw new InvalidOperationException("The harness started without a session.");
        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ResultsOfType("LoadCompensationRequested").Count >= 1,
            "the first compensation request to reach the server",
            token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => !harness.Session.Current.Connected,
            "the vehicle to see the connection the request went down with drop",
            token);

        // The harness runs no reconnect loop, so the reconnect the session service makes in the field is made
        // here. Once it returns the new session is up and settled: the handshake is over and the server has
        // said where the vehicle stands.
        WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);
        Assert.True(reconnected.SessionGeneration > lostGeneration);
        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, reconnected.Readiness);

        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(ActionIdFor(CompensateLoadAction), result.GetProperty("recoveryActionId").GetString());

        // Asked again as the same compensation: a new envelope, the business content the server keys on unchanged.
        IReadOnlyList<string> requests = harness.ResultsOfType("LoadCompensationRequested");
        Assert.Equal(2, requests.Count);
        using JsonDocument first = JsonDocument.Parse(requests[0]);
        using JsonDocument second = JsonDocument.Parse(requests[1]);
        Assert.NotEqual(
            first.RootElement.GetProperty("messageId").GetString(),
            second.RootElement.GetProperty("messageId").GetString());
        Assert.Equal(
            first.RootElement.GetProperty("payload").GetRawText(),
            second.RootElement.GetProperty("payload").GetRawText());
        Assert.Empty(harness.Server.RecoveryRequestConflicts);

        // What the operator was told: never that the compensation had been authorized -- the vehicle cannot know
        // that -- and, after the reconnect, that it was asked for again.
        IReadOnlyList<WireToGateOperatorEvent> events = harness.OperatorEvents;
        Assert.DoesNotContain(events, item => item.Message.Contains("已通过服务端授权", StringComparison.Ordinal));
        Assert.Contains(events, item => item.Kind == "RECOVERY_ACTION_SUBMITTED"
            && item.Message.Contains("已申请补偿授权", StringComparison.Ordinal));
        Assert.Contains(events, item => item.Kind == "RECOVERY_VECTOR_REQUESTED"
            && item.Message.Contains("已在重连后按首次内容重新申请", StringComparison.Ordinal));
    }

    /// <summary>
    /// The link goes down right after the server accepts the compensation, before the authorization request can
    /// reach it: the press does not end silently, and the request goes out once the session is back
    /// (onboard-hmi#236).
    /// </summary>
    /// <remarks>
    /// One of the three reasons the vehicle shows no "result unknown" timeout, pinned. Whether the request fails on
    /// a link the vehicle already knows is down, or is written into one that is going, depends on how fast the
    /// close reaches it; either way the server never sees it, and either way it is asked for again.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationAuthorizationCutOffRightAfterTheActionWasAcceptedIsAskedForOnceTheSessionIsBack()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.SendLoadCompensationCommandOnRequest = true;
                server.CloseConnectionsAfterRecoveryActionAccepted = 1;
            },
            loadAlreadySettled: true);
        long lostGeneration = harness.Session.Current.SessionGeneration
            ?? throw new InvalidOperationException("The harness started without a session.");

        bool pressed = await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token);
        Assert.Single(harness.ResultsOfType("RecoveryActionSubmitted"));
        if (!pressed)
        {
            // Failed on a link already down: the operator saw it fail.
            Assert.True(harness.RecoveryBlockedCount > 0);
        }

        await RecoveryVectorHarness.WaitUntilAsync(
            () => !harness.Session.Current.Connected,
            "the vehicle to see the connection drop after the action was accepted",
            token);
        WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);
        Assert.True(reconnected.SessionGeneration > lostGeneration);

        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(ActionIdFor(CompensateLoadAction), result.GetProperty("recoveryActionId").GetString());
        Assert.Single(harness.ResultsOfType("RecoveryActionSubmitted"));
        Assert.Empty(harness.Server.RecoveryRequestConflicts);
    }

    /// <summary>
    /// A load correction request lost with the connection is asked for again, with the first press's content,
    /// once the session is back (onboard-hmi#236).
    /// </summary>
    /// <remarks>
    /// <c>LoadCorrectionRequested</c> is the compensation request's twin: a REQUEST with
    /// <c>durableBeforeSend=false</c> that nothing answers, and the only thing the server authorizes a correction
    /// on. The double does not go on to issue a correction command; that the server authorizes on this request,
    /// and re-sends the persisted command for one it already authorized, is the server's half and is pinned there.
    /// What is pinned here is the vehicle's: the request reaches the server again, as the same correction.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task ALoadCorrectionRequestLostWithTheConnectionIsAskedForAgainOnceTheSessionIsBack()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.LoadCorrectionRequestsToLose = 1,
            loadAlreadySettled: true);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCorrection,
            "the load correction entry to be offered",
            token);
        long lostGeneration = harness.Session.Current.SessionGeneration
            ?? throw new InvalidOperationException("The harness started without a session.");

        Assert.True(await harness.Business.RequestLoadCorrectionAsync("现场确认需要修正已完成的装货结果。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => !harness.Session.Current.Connected,
            "the vehicle to see the connection the correction request went down with drop",
            token);
        WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);
        Assert.True(reconnected.SessionGeneration > lostGeneration);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Server.JudgedRecoveryRequests == 1,
            "the correction request to reach the server again and be judged there",
            token);
        var requests = harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "LoadCorrectionRequested")
            .ToArray();
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(requests[0].MessageId, requests[1].MessageId);
        Assert.True(requests[1].Connection > requests[0].Connection);
        using JsonDocument first = JsonDocument.Parse(requests[0].WireLine);
        using JsonDocument second = JsonDocument.Parse(requests[1].WireLine);
        Assert.Equal(
            first.RootElement.GetProperty("payload").GetRawText(),
            second.RootElement.GetProperty("payload").GetRawText());
        Assert.Empty(harness.Server.RecoveryRequestConflicts);
        Assert.Contains(harness.OperatorEvents, item => item.Kind == "RECOVERY_VECTOR_REQUESTED"
            && item.Message.Contains("装货修正", StringComparison.Ordinal)
            && item.Message.Contains("已在重连后按首次内容重新申请", StringComparison.Ordinal));
    }

    /// <summary>
    /// After a restart a compensation whose authorization was lost is shown to the operator, not asked for again:
    /// the command it would earn opens doors with nobody asked, and the press may be long past (onboard-hmi#236).
    /// </summary>
    /// <remarks>
    /// The rule the coordinator set on this ticket: within the process that pressed, the vehicle asks again on a
    /// reconnect; once that process is gone, a person who has looked at the slots presses again. So the restarted
    /// vehicle says the authorization may be lost, sends no request, and opens nothing.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AfterARestartALostCompensationAuthorizationIsShownToTheOperatorAndNotAskedForAgain()
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

        // The review that would have asked again is the one that shows this, so once it is shown nothing more is
        // coming in this session.
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.OperatorEvents.Any(item => item.Kind == "RECOVERY_AUTHORIZATION_UNKNOWN"),
            "the operator to be told the compensation's authorization may be lost",
            token);
        WireToGateOperatorEvent shown = afterRestart.OperatorEvents.First(
            item => item.Kind == "RECOVERY_AUTHORIZATION_UNKNOWN");
        // Which vehicle and which request: the operator has to know what to press again, and where.
        Assert.Contains("AGV-8005-01", shown.Message, StringComparison.Ordinal);
        Assert.Contains("补偿清空", shown.Message, StringComparison.Ordinal);
        Assert.Contains(WireToGateRecoveryVectorTypes.LoadCompensation, shown.Message, StringComparison.Ordinal);
        Assert.Contains("请确认现场后再按一次", shown.Message, StringComparison.Ordinal);

        Assert.Empty(afterRestart.ResultsOfType("LoadCompensationRequested"));
        Assert.Empty(afterRestart.ResultsOfType("LoadCompensationResult"));
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        WireToGateRecoveryState state = await afterRestart.ReadRecoveryStateAsync(token);
        Assert.Equal(WireToGateRecoveryVectorTypes.LoadCompensation, state.RecoveryVector?.VectorType);
        Assert.Null(state.RecoveryVector?.CommandContentSha256);
    }
}
