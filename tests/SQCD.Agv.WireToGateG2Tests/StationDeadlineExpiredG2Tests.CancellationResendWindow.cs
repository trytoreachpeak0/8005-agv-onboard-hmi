using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed partial class StationDeadlineExpiredG2Tests
{
    /// <summary>
    /// An unanswered load cancellation is asked for again by itself only within five minutes of the last time a person
    /// pressed for it, and an automatic resend does not move that time on (onboard-hmi#239 review M1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// After a restart the operator presses 取消装货, and no answer comes back for the rest of the test. Inside the
    /// window the vehicle asks again by itself -- on every readiness the session takes, not only on a reconnect, so
    /// the test counts "at least once" rather than a number. Once the clock is past the window counted from the press,
    /// no further request goes out, whatever readiness comes: the vehicle shows the operator the cancellation is
    /// unanswered. An authorization the vehicle earns by itself opens the slots to empty them, with nobody asked.
    /// </para>
    /// <para>
    /// Until the review the resend ran through the same request path as the press and recorded a press itself, so a
    /// link that kept dropping carried the press forward indefinitely (the reviewer's probe: 306 s after the last
    /// press, resent, authorized, one unlock). The slot holds cargo, so an authorization would have to pulse it.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnAutomaticCancellationResendDoesNotCarryThePressPastTheWindow()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        FakeControlServer before;
        await using (Harness beforeRestart = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
                server.LoadCancellationAuthorizationsToDrop = 1;
                server.ReplayJourneySnapshotsWithStableIdentity = true;
            },
            journalPath: journalPath))
        {
            before = beforeRestart.Server;
            await beforeRestart.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
            await Harness.WaitUntilAsync(
                () => beforeRestart.Business.CanRequestLoadCancellation,
                "the in-flight load cancellation entry to be offered",
                token);
            Assert.False(await beforeRestart.Business.RequestLoadCancellationAsync("现场不装了。", token));
            Assert.NotNull(beforeRestart.ReadRecoveryState(token).PendingLoadCancellation);
        }

        // Two steps of a little more than half the window: the first reconnect is inside it, the second past it.
        TimeSpan step = WireToGateBusinessService.AuthorizationResendWindow / 2 + TimeSpan.FromSeconds(1);
        RealTimeLaggingClock clock = new(step + step);
        FakeIoModuleClient restartedIo = new() { LockerWaitTimesOut = true };
        restartedIo.CloseDoor(0, cargo: true);
        await using Harness afterRestart = await Harness.StartAsync(
            restartedIo,
            token,
            server =>
            {
                server.ReplayJourneySnapshotsWithStableIdentity = true;
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
                // No answer reaches the vehicle in this test: whatever it asks, it is never authorized, so a resend
                // shows only as a request and nothing here can open a door.
                server.LoadCancellationAuthorizationsToDrop = 100;
                server.AdoptDurableRecoveryMemoryFrom(before);
            },
            journalPath: journalPath,
            baselineRevision: 2,
            businessClock: clock);
        await WaitForNotAskedAgainAsync(afterRestart, 1, token);
        await Harness.WaitUntilAsync(
            () => afterRestart.Business.CanRequestLoadCancellation,
            "the in-flight load cancellation entry to be offered after the restart",
            token);

        // A person presses; the answer is lost on the way back.
        Assert.False(await afterRestart.Business.RequestLoadCancellationAsync("重启后确认现场，再按一次取消。", token));
        Assert.Equal(1, CancellationRequests(afterRestart));

        // Inside the window: asked again by itself, at least once.
        clock.Advance(step);
        await afterRestart.Client.DisconnectAsync();
        await afterRestart.Client.ConnectAndRecoverAsync(token);
        await Harness.WaitUntilAsync(
            () => CancellationRequests(afterRestart) >= 2,
            "the cancellation to be asked for again inside the window",
            token);

        // Past the window counted from the press -- and from no resend, which must not have moved it on. A resend
        // already on its way when the clock moves has been written before it, so the count is taken after a pause.
        clock.Advance(step);
        await Task.Delay(TimeSpan.FromSeconds(1), token);
        int requestsAtTheWindow = CancellationRequests(afterRestart);
        int decisionsAtTheWindow = NotAskedAgainDecisions(afterRestart);
        await afterRestart.Client.DisconnectAsync();
        await afterRestart.Client.ConnectAndRecoverAsync(token);
        await WaitForNotAskedAgainAsync(afterRestart, decisionsAtTheWindow + 1, token);
        // Long enough for a resend the readiness after the reconnect might start: requests time out in 2 s.
        await Task.Delay(TimeSpan.FromSeconds(3), token);

        Assert.Equal(requestsAtTheWindow, CancellationRequests(afterRestart));
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        Assert.Contains(afterRestart.Logger.Entries, entry =>
            entry.Message.StartsWith("未收到答复的装货取消不自动重新申请", StringComparison.Ordinal)
            && entry.Message.Contains("已超过 5 分钟", StringComparison.Ordinal));
    }

    private static int CancellationRequests(Harness harness) =>
        harness.Server.ReceivedEnvelopes.Count(envelope => envelope.MessageType == "LoadCancellationStartRequested");

    private static int NotAskedAgainDecisions(Harness harness) =>
        harness.Logger.Entries.Count(entry =>
            entry.Message.StartsWith("未收到答复的装货取消不自动重新申请", StringComparison.Ordinal));

    private static Task WaitForNotAskedAgainAsync(Harness harness, int times, CancellationToken token) =>
        Harness.WaitUntilAsync(
            () => NotAskedAgainDecisions(harness) >= times,
            $"the vehicle to decide, for the {times}. time, not to ask for the unanswered cancellation by itself",
            token);
}
