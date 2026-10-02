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
    }
}
