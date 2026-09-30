using System.Collections.Concurrent;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// <c>CV-RECOVERY-SESSION-CLOSED-RESULT-NOT-RECONCILED</c> on this end (protocol 3.0.0,
/// 8005-agv-onboard-hmi#216): a recovery session the server closed because the recovery action's result
/// did not reconcile says why (DISPLAY_SESSION_CLOSED_REASON), is not read as recovered
/// (NEVER_TREAT_UNRECONCILED_CLOSE_AS_RECOVERED), and does not stop the next session from being opened
/// (ALLOW_REOPENING_AFTER_SESSION_CLOSED).
/// </summary>
/// <remarks>
/// The vector's path: a compensation refused before any unlock answers <c>FAILED</c>, the server closes the
/// session with <c>RECOVERY_ACTION_RESULT_NOT_RECONCILED</c>, and the vehicle acknowledges the snapshot.
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    private const string ReasonedClosedSnapshotMessageId = "abcdabcd-0000-4000-8000-000000002160";

    /// <summary>
    /// The closed reason is said in the operator's words, and the recovery entry comes back after it: the
    /// display must not keep the CLOSED snapshot where the entries' gates read "a session is open".
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-RECOVERY-SESSION-CLOSED-RESULT-NOT-RECONCILED")]
    public async Task AnUnreconciledCloseSaysWhyAndTheNextSessionCanStillBeOpened()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartForClosedReasonAsync(token);
        ConcurrentQueue<WireToGateOperatorEvent> events = RecordEvents(harness);
        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await RefuseCompensationAsync(harness, prepared, token);

        await SendReasonedClosedSnapshotAsync(harness, prepared, "RECOVERY_ACTION_RESULT_NOT_RECONCILED");
        await WaitForClosedEventAsync(events, token);

        Assert.Equal(
            "服务端恢复会话已关闭：恢复动作结果未闭环，需求与旅程仍阻断，请管理员重新发起恢复。",
            Assert.Single(ClosedEvents(events)).Message);
        // Not read as recovered: the attempt is still unsettled and the vehicle still needs recovery.
        Assert.NotEqual(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
        // The entry itself is offered again -- the property the button binds to -- not only a request made
        // through the service behind the button's back.
        harness.VehicleStopped();
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCompensation,
            "the compensation entry to be offered again after the reasoned close",
            token);
        await AssertANewSessionCanBeOpenedAsync(harness, token);
    }

    /// <summary>
    /// The three ways the sentence reads: a known reason from the table, no reason (as before 3.0.0), and a
    /// reason the table does not know, shown as it came rather than guessed at.
    /// </summary>
    [Theory]
    [InlineData("RECOVERY_ACTION_RESULT_NOT_RECONCILED", "服务端恢复会话已关闭：恢复动作结果未闭环，需求与旅程仍阻断，请管理员重新发起恢复。")]
    [InlineData(null, "服务端恢复会话已关闭。")]
    [InlineData("ACTION_NOT_ALLOWED_IN_STATE", "服务端恢复会话已关闭（ACTION_NOT_ALLOWED_IN_STATE）。")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-RECOVERY-SESSION-CLOSED-RESULT-NOT-RECONCILED")]
    public async Task TheClosedReasonReadsFromTheTableOrAsItCame(string? closedReason, string expected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartForClosedReasonAsync(token);
        ConcurrentQueue<WireToGateOperatorEvent> events = RecordEvents(harness);
        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await RefuseCompensationAsync(harness, prepared, token);

        await SendReasonedClosedSnapshotAsync(harness, prepared, closedReason);
        await WaitForClosedEventAsync(events, token);

        Assert.Equal(expected, Assert.Single(ClosedEvents(events)).Message);
    }

    /// <summary>
    /// The same CLOSED arriving again on the next session -- what the server does with one it has no
    /// acknowledgement for -- is not said twice, although the event deduplicator is cleared on every new
    /// generation; and the entry stays available across it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-RECOVERY-SESSION-CLOSED-RESULT-NOT-RECONCILED")]
    public async Task TheSameClosedArrivingOnTheNextSessionIsNotSaidTwice()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await StartForClosedReasonAsync(token);
        ConcurrentQueue<WireToGateOperatorEvent> events = RecordEvents(harness);
        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await RefuseCompensationAsync(harness, prepared, token);
        await SendReasonedClosedSnapshotAsync(harness, prepared, "RECOVERY_ACTION_RESULT_NOT_RECONCILED");
        await WaitForClosedEventAsync(events, token);

        long? firstGeneration = harness.Session.Current.SessionGeneration;
        WireToGateSessionSnapshot reconnected = await harness.Session.Client.ConnectAndRecoverAsync(token);
        Assert.True(reconnected.SessionGeneration > firstGeneration, $"{reconnected.SessionGeneration} after {firstGeneration}");
        int acknowledgedBefore = AcknowledgedRecoverySnapshots(harness)
            .Count(id => id == ReasonedClosedSnapshotMessageId);
        await SendReasonedClosedSnapshotAsync(harness, prepared, "RECOVERY_ACTION_RESULT_NOT_RECONCILED");
        await RecoveryVectorHarness.WaitUntilAsync(
            () => AcknowledgedRecoverySnapshots(harness).Count(id => id == ReasonedClosedSnapshotMessageId)
                > acknowledgedBefore,
            "the replayed CLOSED to be applied on the new session",
            token);

        Assert.Single(ClosedEvents(events));
        await AssertANewSessionCanBeOpenedAsync(harness, token);
    }

    private static Task<RecoveryVectorHarness> StartForClosedReasonAsync(CancellationToken token) =>
        RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
            },
            cargoInTargetSlots: true);

    private static ConcurrentQueue<WireToGateOperatorEvent> RecordEvents(RecoveryVectorHarness harness)
    {
        ConcurrentQueue<WireToGateOperatorEvent> events = new();
        harness.Business.OperatorEventPublished += (_, args) => events.Enqueue(args.Value);
        return events;
    }

    private static IReadOnlyList<WireToGateOperatorEvent> ClosedEvents(IEnumerable<WireToGateOperatorEvent> events) =>
        [.. events.Where(item => item.Kind == "RECOVERY_SESSION_UPDATED"
            && item.Message.StartsWith("服务端恢复会话已关闭", StringComparison.Ordinal))];

    private static Task WaitForClosedEventAsync(
        ConcurrentQueue<WireToGateOperatorEvent> events,
        CancellationToken token) =>
        RecoveryVectorHarness.WaitUntilAsync(
            () => ClosedEvents(events).Count > 0,
            "the operator event saying the recovery session closed",
            token);

    private static Task SendReasonedClosedSnapshotAsync(
        RecoveryVectorHarness harness,
        WireToGateRecoveryState opened,
        string? closedReason) =>
        harness.Server.SendCommandAsync(
            "ExceptionRecoverySessionSnapshot",
            ReasonedClosedSnapshotMessageId,
            new
            {
                exceptionRecoverySessionId = opened.ExceptionRecoverySessionId,
                recoverySessionRevision = 3,
                state = "CLOSED",
                administratorId = "maintenance-001",
                administratorRole = "MAINTENANCE_ADMINISTRATOR",
                eventId = opened.RecoverySessionRequestId,
                demandId = DemandId,
                slotOperationAttemptId = AttemptId,
                slots = CompensationSlots,
                selectedAction = "COMPENSATE_LOAD_ALL_EMPTY",
                allowedActions = Array.Empty<string>(),
                closedReason,
                blockingFacts = Array.Empty<object>()
            });
}
