using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The load cancellation over a load in flight whose result the server refused for good, ended after a maintainer's
/// manual check (onboard-hmi#254 part 2): the aborted load's attempt is the cancellation's, so ending it reports nothing
/// for the load (ADR-cross-0046).
/// </summary>
public sealed partial class StationDeadlineExpiredG2Tests
{
    private const string DeadlineProofVariable = "W2G_G2_DEADLINE_PROOF";

    /// <summary>
    /// The attempt and its context go with the vector; the active unlock set stays. Across the next handshake and its
    /// restore, no <c>OperationResult</c> is sent for the aborted load -- kept unsettled, it would have been settled as an
    /// interrupted load and reported.
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
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LoadCancellationResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            });
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

        Assert.True(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));

        WireToGateRecoveryState after = harness.ReadRecoveryState(token);
        Assert.Null(after.RecoveryVector);
        Assert.Null(after.UnsettledSlotOperationAttemptId);
        Assert.Null(after.OperationContext);
        Assert.Equal(before.ActiveUnlockSlots, after.ActiveUnlockSlots);

        // The next session reports nothing unsettled, and its restore -- where an interrupted settlement would run --
        // sends no OperationResult for the load.
        await harness.Client.DisconnectAsync();
        _ = await harness.Client.ConnectAndRecoverAsync(token);
        await Harness.WaitUntilAsync(
            () => harness.Server.Received.Count(item => item.MessageType == "RecoveryStateReport") >= 2,
            "the next handshake's recovery state report",
            token);
        await Harness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Message.StartsWith("判恢复入口：generation=2", StringComparison.Ordinal)),
            "the restore that follows the next session coming up",
            token);
        await Task.Delay(TimeSpan.FromSeconds(1), token);
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "OperationResult");
        Assert.Single(harness.Server.Received, item => item.MessageType == "LoadCancellationResult");
    }
}
