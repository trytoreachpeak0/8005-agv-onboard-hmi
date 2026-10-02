using System.IO;
using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed partial class RecoveryVectorG2Tests
{
    private const string ReviewLogPrefix = "重连后检查授权请求：";

    /// <summary>
    /// The window in which the vehicle still asks again by itself is five minutes (onboard-hmi#236 review S1).
    /// </summary>
    /// <remarks>
    /// Pinned as a number, not as "some window": the command a resend earns opens doors with nobody asked, and how
    /// long after a press that is still acceptable was decided by people, not derived. Changing it is a decision to
    /// make on purpose, and this is where it shows.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public void TheVehicleAsksAgainByItselfOnlyWithinFiveMinutesOfThePress()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), WireToGateBusinessService.AuthorizationResendWindow);
    }

    /// <summary>
    /// A link that comes back more than five minutes after the press is not asked again: the operator is shown the
    /// authorization may be lost, the same way as after a restart (onboard-hmi#236 review S1).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task PastTheWindowALostCompensationAuthorizationIsShownAndNotAskedForAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        MultiDemandViewModelTests.ManualClock clock = new(DateTimeOffset.UtcNow);
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.SendLoadCompensationCommandOnRequest = true;
                server.LoadCompensationRequestsToLose = 1;
            },
            loadAlreadySettled: true,
            businessClock: clock);

        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => !harness.Session.Current.Connected,
            "the vehicle to see the connection the request went down with drop",
            token);
        clock.Advance(WireToGateBusinessService.AuthorizationResendWindow + TimeSpan.FromSeconds(1));
        await harness.Session.Client.ConnectAndRecoverAsync(token);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.OperatorEvents.Any(item => item.Kind == "RECOVERY_AUTHORIZATION_UNKNOWN"),
            "the operator to be told the authorization may be lost",
            token);
        WireToGateOperatorEvent shown = harness.OperatorEvents.First(item => item.Kind == "RECOVERY_AUTHORIZATION_UNKNOWN");
        Assert.Contains("超过 5 分钟", shown.Message, StringComparison.Ordinal);
        Assert.Contains("请先确认仓门附近安全", shown.Message, StringComparison.Ordinal);
        Assert.Single(harness.ResultsOfType("LoadCompensationRequested"));
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// A resend does not move the window on: two drops whose reconnects together come more than five minutes after
    /// the press are asked again once, inside the window, and shown -- not asked -- the second time (onboard-hmi#236
    /// review S-A).
    /// </summary>
    /// <remarks>
    /// The review's probe had every resend refresh the press time, so a link that kept dropping carried the press
    /// forward for as long as it dropped: 8 minutes after the press, three requests, the compensation ran and nothing
    /// was shown. The window runs from the last time a person pressed.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AResendDoesNotCarryThePressPastTheWindow()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        MultiDemandViewModelTests.ManualClock clock = new(DateTimeOffset.UtcNow);
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.SendLoadCompensationCommandOnRequest = true;
                server.LoadCompensationRequestsToLose = 2;
            },
            loadAlreadySettled: true,
            businessClock: clock);
        TimeSpan step = WireToGateBusinessService.AuthorizationResendWindow / 2 + TimeSpan.FromSeconds(1);

        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => !harness.Session.Current.Connected,
            "the press's request to go down with the link",
            token);

        // First reconnect, inside the window: asked again -- and lost again.
        clock.Advance(step);
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ResultsOfType("LoadCompensationRequested").Count == 2 && !harness.Session.Current.Connected,
            "the resend to go out and down with the link again",
            token);
        Assert.DoesNotContain(harness.OperatorEvents, item => item.Kind == "RECOVERY_AUTHORIZATION_UNKNOWN");

        // Second reconnect: past the window counted from the press, though within it counted from the resend.
        clock.Advance(step);
        await harness.Session.Client.ConnectAndRecoverAsync(token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.OperatorEvents.Any(item => item.Kind == "RECOVERY_AUTHORIZATION_UNKNOWN"),
            "the operator to be shown the authorization may be lost",
            token);
        Assert.Contains(
            "超过 5 分钟",
            harness.OperatorEvents.First(item => item.Kind == "RECOVERY_AUTHORIZATION_UNKNOWN").Message,
            StringComparison.Ordinal);
        Assert.Equal(2, harness.ResultsOfType("LoadCompensationRequested").Count);
        Assert.Empty(harness.ResultsOfType("LoadCompensationResult"));
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// A compensation whose command is already bound is not asked for again after a reconnect, and nothing is shown:
    /// the command is on its way through the server's replay, not lost (onboard-hmi#236 review N2).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationWithItsCommandBoundIsNotAskedForAgainAfterAReconnect()
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

        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => !harness.Session.Current.Connected,
            "the vehicle to see the connection the request went down with drop",
            token);
        // The command bound while the link was down -- as if it had arrived and been journaled just before the drop.
        await harness.RewriteRecoveryStateAsync(
            state => state with
            {
                RecoveryVector = state.RecoveryVector! with { CommandContentSha256 = new string('c', 64) }
            },
            token);
        WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Message.StartsWith(ReviewLogPrefix, StringComparison.Ordinal)
                && entry.Message.Contains($"generation={reconnected.SessionGeneration}", StringComparison.Ordinal)),
            "the reconnect review to conclude",
            token);
        Assert.Contains(harness.Logger.Entries, entry =>
            entry.Message.StartsWith(ReviewLogPrefix, StringComparison.Ordinal)
            && entry.Message.Contains("commandBound=True", StringComparison.Ordinal)
            && entry.Message.Contains("无待授权的补偿或修正", StringComparison.Ordinal));
        Assert.Single(harness.ResultsOfType("LoadCompensationRequested"));
        Assert.DoesNotContain(harness.OperatorEvents, item => item.Kind == "RECOVERY_AUTHORIZATION_UNKNOWN");
    }

    /// <summary>
    /// When the reconnect review itself fails -- the journal cannot be read -- the operator is shown the
    /// authorization may be lost, not just a log line (onboard-hmi#236 review N4).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AReconnectReviewThatFailsIsShownToTheOperator()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FailingReviewJournal? failing = null;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.SendLoadCompensationCommandOnRequest = true;
                server.LoadCompensationRequestsToLose = 1;
            },
            loadAlreadySettled: true,
            wrapJournal: inner => failing = new FailingReviewJournal(inner));

        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => !harness.Session.Current.Connected,
            "the vehicle to see the connection the request went down with drop",
            token);
        failing!.FailTheNextReview();
        await harness.Session.Client.ConnectAndRecoverAsync(token);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.OperatorEvents.Any(item => item.Kind == "RECOVERY_AUTHORIZATION_UNKNOWN"),
            "the operator to be told the review could not conclude",
            token);
        WireToGateOperatorEvent shown = harness.OperatorEvents.First(item => item.Kind == "RECOVERY_AUTHORIZATION_UNKNOWN");
        Assert.Contains("重连后检查授权状态时出错", shown.Message, StringComparison.Ordinal);
        Assert.Contains("injected", shown.Message, StringComparison.Ordinal);
        Assert.Equal(1, failing.FailedReviews);
    }

    /// <summary>
    /// After a restart the press asks for the compensation with the operator pressing now, not the one journaled
    /// with the first press: the server does not compare the operator, and keeps who asked again (onboard-hmi#236
    /// review N7).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AfterARestartThePressAsksWithTheOperatorPressingNow()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Path.Combine(
            Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.SendLoadCompensationCommandOnRequest = true;
        server.LoadCompensationRequestsToLose = 1;
        string firstOperator = Environment.GetEnvironmentVariable(OperatorVariable)!;

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

        // Another person at the vehicle. Process-wide, so put back whatever happens: the other tests of this class
        // read the same variable.
        Environment.SetEnvironmentVariable(OperatorVariable, "maintenance-002");
        try
        {
            Assert.True(await afterRestart.Business.RequestLoadCompensationAsync("换人确认现场后再按一次。", token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(OperatorVariable, firstOperator);
        }

        await afterRestart.WaitForResultAsync("LoadCompensationResult", token);
        using JsonDocument request = JsonDocument.Parse(
            Assert.Single(afterRestart.ResultsOfType("LoadCompensationRequested")));
        Assert.Equal(
            "maintenance-002",
            request.RootElement.GetProperty("payload").GetProperty("operator").GetProperty("operatorId").GetString());
    }

    /// <summary>
    /// Fails the reconnect review's journal read, once armed, with an <see cref="IOException"/>, and lets every other
    /// call through.
    /// </summary>
    private sealed class FailingReviewJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private int _armed;
        private int _failed;

        public int FailedReviews => Volatile.Read(ref _failed);

        public void FailTheNextReview() => Volatile.Write(ref _armed, 1);

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1
                && Environment.StackTrace.Contains("ReviewUnauthorizedRecoveryVectorAsync", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Interlocked.Increment(ref _failed);
                throw new IOException("injected: the journal could not be read");
            }

            return inner.ReadRecoveryStateAsync(cancellationToken);
        }

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, cancellationToken);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

        public Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default) =>
            inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);

        public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
            WireToGateDurableMessage expected,
            WireToGateDurableMessage replacement,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceOutgoingForReplayAsync(expected, replacement, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByMessageIdAsync(messageId, cancellationToken);

        public Task MarkOutgoingAcknowledgedAsync(
            string messageId,
            string acceptedContentSha256,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAcknowledgedAsync(messageId, acceptedContentSha256, cancellationToken);

        public Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadUnacknowledgedOutgoingAsync(cancellationToken);

        public Task<IReadOnlyList<WireToGateAppliedJourneySnapshot>> ReadAppliedJourneySnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadAppliedJourneySnapshotsAsync(cancellationToken);

        public Task<WireToGateAppliedJourneySnapshot> SaveAppliedJourneySnapshotAsync(
            WireToGateAppliedJourneySnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            inner.SaveAppliedJourneySnapshotAsync(snapshot, cancellationToken);

        public Task<string> ComputeContentSha256Async(CancellationToken cancellationToken = default) =>
            inner.ComputeContentSha256Async(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
