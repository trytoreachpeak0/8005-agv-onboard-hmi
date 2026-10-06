using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The load cancellation over a load in flight whose result the server refused for good, ended after a maintainer's
/// manual check (onboard-hmi#254 part 2): the aborted load's attempt stays, marked as the cancellation's by its result's
/// row in the outbox, so nothing is reported for the load (ADR-cross-0046), the load is not cancelled again, and the
/// recovery entries still find it while the server holds the operation in <c>RecoveryRequired</c>.
/// </summary>
public sealed partial class StationDeadlineExpiredG2Tests
{
    private const string DeadlineProofVariable = "W2G_G2_DEADLINE_PROOF";

    /// <summary>
    /// Ended, the vector goes and the attempt, its context and the active unlock set stay. Across the next handshake and
    /// its restore -- where an interrupted settlement would run -- no <c>OperationResult</c> is sent for the aborted load,
    /// and the restore owes the recovery entry for it. The cancellation entry is not offered and a press is refused
    /// before anything goes out; with the server holding the operation in <c>RecoveryRequired</c>, a compensation opens
    /// a recovery session for it.
    /// </summary>
    [Fact]
    public async Task ACancellationEndedAfterTheManualCheckReportsNothingForTheAbortedLoad()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Environment.SetEnvironmentVariable(DeadlineProofVariable, "deadline-proof");
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true, KeepSnapshotFresh = true },
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
                server.RespondToRecoveryRequests = true;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LoadCancellationResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            },
            recoveryResumeEnabled: true);
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        await Harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight load cancellation entry to be offered",
            token);

        Task<bool> cancelling = harness.Business.RequestLoadCancellationAsync("现场不装了。", token);
        await Harness.WaitUntilAsync(
            () => harness.ReadRecoveryState(token).RecoveryVector is not null,
            "the authorized cancellation to be prepared over the aborted load",
            token);
        string attempt = harness.ReadRecoveryState(token).RecoveryVector!.SlotOperationAttemptId!;
        harness.Server.RecoverySlotOperationAttemptId = attempt;
        // The operator empties the open door and shuts it: the cancellation reaches ALL_EMPTY, which the server refuses.
        harness.Io.CloseDoor(0, cargo: false);
        _ = await cancelling;
        await harness.WaitForInboundAsync("LoadCancellationResult", token);
        await Harness.WaitUntilAsync(
            () => harness.Business.CanCloseConflictedRecoveryAfterReview,
            "the refused cancellation to wait for a maintainer's manual check",
            token);
        WireToGateRecoveryState before = harness.ReadRecoveryState(token);
        Assert.Equal(attempt, before.UnsettledSlotOperationAttemptId);

        // While it waits for the check the cancellation entry is shut, and a press that gets past the screen runs nothing
        // again: with cargo back in the slot, running the vector again would pulse its door open.
        harness.Io.SetCargoPresent(0, true);
        Assert.False(harness.Business.CanRequestLoadCancellation);
        int unlocksWhileWaiting = harness.Io.UnlockCount;
        Assert.False(await harness.Business.RequestLoadCancellationAsync("现场不装了。", token));
        Assert.Equal(unlocksWhileWaiting, harness.Io.UnlockCount);
        Assert.Single(harness.Server.Received, item => item.MessageType == "LoadCancellationResult");
        Assert.Contains(
            OnboardCommandRejectionText.DescribeRecoveryBlocked("RECOVERY_AWAITING_MANUAL_CHECK"),
            harness.DescribeEvents(),
            StringComparison.Ordinal);

        Assert.True(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));

        WireToGateRecoveryState after = harness.ReadRecoveryState(token);
        Assert.Null(after.RecoveryVector);
        Assert.Equal(attempt, after.UnsettledSlotOperationAttemptId);
        Assert.Equal(attempt, after.OperationContext?.SlotOperationAttemptId);
        Assert.Equal(before.ActiveUnlockSlots, after.ActiveUnlockSlots);
        // Not offered from the moment it is ended: nothing else changes the session state before the next restore.
        Assert.False(harness.Business.CanRequestLoadCancellation);

        // The next session: the vehicle still reports the attempt, so the double -- which never accepted a conclusion for
        // it -- holds the session in RecoveryRequired, as a server holding the operation in RecoveryRequired would. The
        // restore decides the settlement before it owes the entry, so the entry's line is the second fact to wait for.
        int recoveryLinesBefore = RecoveryEntryLinesOwed(harness);
        await harness.Client.DisconnectAsync();
        WireToGateSessionSnapshot reconnected = await harness.Client.ConnectAndRecoverAsync(token);
        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, reconnected.Readiness);
        await Harness.WaitUntilAsync(
            () => RecoveryEntryLinesOwed(harness) > recoveryLinesBefore,
            "the restore to owe the recovery entry for the aborted load",
            token);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "OperationResult");

        // Cancelled already: not offered, and a press that gets past the screen is refused before anything goes out.
        Assert.False(harness.Business.CanRequestLoadCancellation);
        int unlocks = harness.Io.UnlockCount;
        Assert.False(await harness.Business.RequestLoadCancellationAsync("现场不装了。", token));
        Assert.Single(harness.Server.Received, item => item.MessageType == "LoadCancellationStartRequested");
        Assert.Equal(unlocks, harness.Io.UnlockCount);
        Assert.Contains(
            OnboardCommandRejectionText.DescribeRecoveryBlocked("LOAD_CANCELLATION_ALREADY_CONCLUDED"),
            harness.DescribeEvents(),
            StringComparison.Ordinal);

        // The way out while the server holds the operation: a compensation opens a recovery session for this attempt.
        await Harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCompensation,
            "the compensation entry to be offered for the aborted load",
            token);
        Assert.True(await harness.Business.RequestLoadCompensationAsync("现场确认装货无法继续，申请补偿清空目标仓位。", token));
        Assert.Contains(harness.Server.Received, item => item.MessageType == "ExceptionRecoverySessionRequested");
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "OperationResult");
        Assert.Single(harness.Server.Received, item => item.MessageType == "LoadCancellationResult");
    }

    /// <summary>
    /// After a restart the cancellation entry is still shut over the aborted load: the restore reads the cancellation's
    /// result back from the outbox and marks the attempt, before anybody presses.
    /// </summary>
    [Fact]
    public async Task ACancellationEndedAfterTheManualCheckStaysShutAfterARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        string attempt;
        await using (Harness beforeRestart = await StartForARefusedCancellationAsync(journalPath, token))
        {
            attempt = await RefuseAndEndTheCancellationAsync(beforeRestart, token);
        }

        await using Harness afterRestart = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true, KeepSnapshotFresh = true },
            token,
            server => server.SendSlotOperationCommandAfterRecovery = false,
            recoveryResumeEnabled: true,
            journalPath: journalPath,
            baselineRevision: 2);
        await Harness.WaitUntilAsync(
            () => RecoveryEntryLinesOwed(afterRestart) > 0,
            "the restore to owe the recovery entry for the aborted load after the restart",
            token);

        Assert.Equal(attempt, afterRestart.ReadRecoveryState(token).UnsettledSlotOperationAttemptId);
        Assert.False(afterRestart.Business.CanRequestLoadCancellation);
        Assert.DoesNotContain(afterRestart.Server.Received, item => item.MessageType == "OperationResult");
    }

    /// <summary>
    /// The mark is the attempt's, not the vehicle's: once the server has settled the aborted load and commands the next
    /// one, that load's cancellation entry is offered as ever.
    /// </summary>
    [Fact]
    public async Task ACancellationEndedAfterTheManualCheckDoesNotShutTheNextLoadsCancellation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartForARefusedCancellationAsync(
            Harness.NewJournalPath(),
            token,
            reconciledByAnother: true);
        string attempt = await RefuseAndEndTheCancellationAsync(harness, token);
        Assert.False(harness.Business.CanRequestLoadCancellation);

        // The server took the aborted load off with the cancellation's other result; the session stayed READY throughout.
        Assert.Equal(WireToGateSessionReadiness.Ready, harness.Client.Current.Readiness);

        const string nextAttempt = "4b4b4b4b-4b4b-4b4b-8b4b-4b4b4b4b4b4b";
        await harness.Server.SendCommandAsync(
            "SlotOperationCommand",
            Guid.NewGuid().ToString("D"),
            new
            {
                demandId = "4c4c4c4c-4c4c-4c4c-8c4c-4c4c4c4c4c4c",
                operationSessionId = "33333333-3333-3333-3333-333333333333",
                slotOperationAttemptId = nextAttempt,
                operationType = "LOAD",
                slots = NextLoadSlots,
                expectedBasketCount = 1,
                expectedFinalPhysicalState = "OCCUPIED",
                commandContentSha256 = new string('0', 64)
            },
            correlationId: Guid.NewGuid().ToString("D"));
        await Harness.WaitUntilAsync(
            () => harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId == nextAttempt,
            "the next load to start",
            token);

        Assert.NotEqual(nextAttempt, attempt);
        await Harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the next load's cancellation entry to be offered",
            token);
    }

    private static readonly int[] NextLoadSlots = [2];

    private static Task<Harness> StartForARefusedCancellationAsync(
        string journalPath,
        CancellationToken token,
        bool reconciledByAnother = false)
    {
        Environment.SetEnvironmentVariable(DeadlineProofVariable, "deadline-proof");
        return Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true, KeepSnapshotFresh = true },
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
                server.RefusedRecoveryResultsWereReconciledByAnother = reconciledByAnother;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LoadCancellationResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            },
            recoveryResumeEnabled: true,
            journalPath: journalPath);
    }

    /// <summary>
    /// Cancels the load in flight, has the cancellation's result refused for good, and ends it after the manual check.
    /// Returns the aborted load's attempt.
    /// </summary>
    private static async Task<string> RefuseAndEndTheCancellationAsync(Harness harness, CancellationToken token)
    {
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        await Harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight load cancellation entry to be offered",
            token);
        Task<bool> cancelling = harness.Business.RequestLoadCancellationAsync("现场不装了。", token);
        await Harness.WaitUntilAsync(
            () => harness.ReadRecoveryState(token).RecoveryVector is not null,
            "the authorized cancellation to be prepared over the aborted load",
            token);
        string attempt = harness.ReadRecoveryState(token).RecoveryVector!.SlotOperationAttemptId!;
        harness.Io.CloseDoor(0, cargo: false);
        _ = await cancelling;
        await Harness.WaitUntilAsync(
            () => harness.Business.CanCloseConflictedRecoveryAfterReview,
            "the refused cancellation to wait for a maintainer's manual check",
            token);
        Assert.True(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));
        Assert.Equal(attempt, harness.ReadRecoveryState(token).UnsettledSlotOperationAttemptId);
        return attempt;
    }

    /// <summary>The restore's line owing the recovery entry for an unfinished operation, counted.</summary>
    private static int RecoveryEntryLinesOwed(Harness harness) =>
        harness.DescribeEvents()
            .Split(Environment.NewLine)
            .Count(line => line.StartsWith("OPERATION_RECOVERY_REQUIRED: ", StringComparison.Ordinal)
                && line.Contains("需要管理员恢复", StringComparison.Ordinal));
}
